using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed class TalkBitRuntimeService(
    IDbContextFactory<RadioDbContext> dbFactory,
    AnnouncementFactory announcementFactory,
    TimeProvider timeProvider,
    ILogger<TalkBitRuntimeService> logger)
{
    public async Task<Announcement?> TryProduceAsync(
        Moderator moderator,
        string stationName,
        CancellationToken ct,
        string? lengthHint = null)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var bit = await PickBitAsync(moderator, utcNow, ct);
        if (bit is null)
        {
            return null;
        }

        return TalkBitPolicy.ShouldForceRetelling(bit, moderator.ExactReplayTolerance)
            || bit.Renditions.All(rendition => string.IsNullOrWhiteSpace(rendition.FilePath))
            ? await RetellAsync(bit.Id, moderator, stationName, utcNow, ct, lengthHint)
            : await ReplayAsync(bit.Id, moderator, utcNow, ct);
    }

    private async Task<TalkBit?> PickBitAsync(Moderator moderator, DateTime utcNow, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bits = await db.TalkBits
            .Include(bit => bit.Renditions)
            .Where(bit => bit.ModeratorId == moderator.Id)
            .ToListAsync(ct);

        var changed = false;
        foreach (var bit in bits.Where(bit => TalkBitPolicy.ShouldRetire(bit, utcNow: utcNow)))
        {
            if (bit.Status == TalkBitStatus.Retired)
            {
                continue;
            }

            bit.Status = TalkBitStatus.Retired;
            bit.RetiredAtUtc = utcNow;
            bit.RetirementReason = "Retired by runtime policy.";
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return TalkBitPolicy.PickWeighted(bits, utcNow, Random.Shared);
    }

    private async Task<Announcement?> ReplayAsync(Guid bitId, Moderator moderator, DateTime utcNow, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bit = await db.TalkBits
            .Include(item => item.Renditions)
            .FirstOrDefaultAsync(item => item.Id == bitId && item.Status == TalkBitStatus.Active, ct);
        if (bit is null)
        {
            return null;
        }

        var rendition = bit.Renditions
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
            .OrderBy(item => item.PlayCount)
            .ThenBy(item => item.LastPlayedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();
        if (rendition is null)
        {
            return null;
        }

        var announcement = new Announcement
        {
            Id = Guid.NewGuid(),
            ModeratorId = moderator.Id,
            Kind = AnnouncementKind.TalkBit,
            ScriptText = rendition.Text,
            VoicedText = rendition.Text,
            FilePath = rendition.FilePath!,
            DurationSeconds = rendition.DurationSeconds,
            CreatedAt = utcNow,
            WasPlayed = false,
        };

        db.Announcements.Add(announcement);
        db.TalkBreaks.Add(CreateReplayTalkBreak(announcement, bit, utcNow));
        MarkExactReplay(bit, rendition, utcNow);
        RememberUse(db, moderator.Id, bit.Premise, announcement.ScriptText);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Replayed TalkBit {TalkBitId} for {Moderator}", bit.Id, moderator.Name);
        return announcement;
    }

    private async Task<Announcement> RetellAsync(
        Guid bitId,
        Moderator moderator,
        string stationName,
        DateTime utcNow,
        CancellationToken ct,
        string? lengthHint)
    {
        TalkBit bit;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            bit = await db.TalkBits.AsNoTracking()
                .FirstAsync(item => item.Id == bitId, ct);
        }

        var announcement = await announcementFactory.ProduceAsync(
            AnnouncementKind.TalkBit,
            moderator,
            relatedTrack: null,
            facts: bit.Premise,
            stationName: stationName,
            ct: ct,
            lengthHint: lengthHint);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var trackedBit = await db.TalkBits
                .FirstAsync(item => item.Id == bitId, ct);
            trackedBit.PlayCount++;
            trackedBit.FreshRetellCount++;
            trackedBit.ExactReplayCount = 0;
            trackedBit.LastUsedAtUtc = utcNow;

            db.TalkBitRenditions.Add(new TalkBitRendition
            {
                Id = Guid.NewGuid(),
                TalkBitId = trackedBit.Id,
                Text = announcement.ScriptText,
                FilePath = announcement.FilePath,
                DurationSeconds = announcement.DurationSeconds,
                CreatedFromRetelling = true,
                PlayCount = 1,
                CreatedAtUtc = utcNow,
                LastPlayedAtUtc = utcNow,
            });

            var talkBreak = await db.TalkBreaks
                .Include(item => item.Parts)
                .FirstOrDefaultAsync(item => item.AnnouncementId == announcement.Id, ct);
            if (talkBreak is not null)
            {
                talkBreak.Purpose = "TalkBit";
                foreach (var part in talkBreak.Parts)
                {
                    part.Kind = TalkPartKind.TalkBit;
                    part.Purpose = "TalkBit";
                    part.TalkBitId = trackedBit.Id;
                }
            }

            if (TalkBitPolicy.ShouldRetire(trackedBit, utcNow: utcNow))
            {
                trackedBit.Status = TalkBitStatus.Retired;
                trackedBit.RetiredAtUtc = utcNow;
                trackedBit.RetirementReason = "Retired by runtime policy.";
            }

            RememberUse(db, moderator.Id, trackedBit.Premise, announcement.ScriptText);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Retold TalkBit {TalkBitId} for {Moderator}", bitId, moderator.Name);
        return announcement;
    }

    private static TalkBreak CreateReplayTalkBreak(Announcement announcement, TalkBit bit, DateTime utcNow)
        => new()
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcement.Id,
            ModeratorId = announcement.ModeratorId,
            Priority = TalkBreakPriority.Normal,
            Status = TalkBreakStatus.Rendered,
            Purpose = "TalkBit",
            Title = "Announcement",
            CreatedAtUtc = utcNow,
            RenderedAtUtc = utcNow,
            DurationSeconds = announcement.DurationSeconds,
            Parts =
            [
                new TalkPart
                {
                    SortOrder = 0,
                    Kind = TalkPartKind.TalkBit,
                    Status = TalkPartStatus.Rendered,
                    Priority = TalkBreakPriority.Normal,
                    Purpose = "TalkBit",
                    AnnouncementId = announcement.Id,
                    TalkBitId = bit.Id,
                    CreatedAtUtc = utcNow,
                },
            ],
        };

    private static void MarkExactReplay(TalkBit bit, TalkBitRendition rendition, DateTime utcNow)
    {
        bit.PlayCount++;
        bit.ExactReplayCount++;
        bit.LastUsedAtUtc = utcNow;
        rendition.PlayCount++;
        rendition.LastPlayedAtUtc = utcNow;

        if (TalkBitPolicy.ShouldRetire(bit, utcNow: utcNow))
        {
            bit.Status = TalkBitStatus.Retired;
            bit.RetiredAtUtc = utcNow;
            bit.RetirementReason = "Retired by runtime policy.";
        }
    }

    private void RememberUse(RadioDbContext db, int moderatorId, string premise, string script)
    {
        db.ModeratorMemories.Add(new ModeratorMemory
        {
            ModeratorId = moderatorId,
            Layer = ModeratorMemoryLayer.DayMemory,
            Date = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime),
            Content = Trim($"TalkBit used: {premise}. Said: {script}", ModeratorMemoryService.DayMemoryMaxChars),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
    }

    private static string Trim(string value, int maxChars)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }
}
