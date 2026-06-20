using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>Renders an ordered set of already-produced talk parts into one announcement WAV.</summary>
public sealed class SegmentRenderer(
    IDbContextFactory<RadioDbContext> dbFactory,
    IOptions<RadioOptions> radioOptions,
    MediaAnalysisRecorder analysisRecorder,
    TimeProvider timeProvider,
    ILogger<SegmentRenderer> logger)
{
    public async Task<Announcement> RenderAsync(
        IReadOnlyList<Announcement> orderedAnnouncements,
        Moderator fallbackModerator,
        CancellationToken ct)
    {
        if (orderedAnnouncements.Count == 0)
        {
            throw new ArgumentException("At least one announcement is required.", nameof(orderedAnnouncements));
        }

        if (orderedAnnouncements.Count == 1)
        {
            return orderedAnnouncements[0];
        }

        var idsInOrder = orderedAnnouncements.Select(announcement => announcement.Id).ToList();
        var audio = new List<byte[]>(orderedAnnouncements.Count);
        foreach (var announcement in orderedAnnouncements)
        {
            var absolutePath = Path.Combine(radioOptions.Value.DataRoot, announcement.FilePath);
            audio.Add(await File.ReadAllBytesAsync(absolutePath, ct));
        }

        var compositeWav = WavFile.ConcatPcm16(audio);
        var id = Guid.NewGuid();
        var relativePath = Path.Combine("library", "announcements", $"{id}.wav");
        var compositePath = Path.Combine(radioOptions.Value.DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(compositePath)!);
        await File.WriteAllBytesAsync(compositePath, compositeWav, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sourceBreaks = await db.TalkBreaks
            .Include(talkBreak => talkBreak.Parts)
            .Where(talkBreak => talkBreak.AnnouncementId != null
                && idsInOrder.Contains(talkBreak.AnnouncementId.Value))
            .ToListAsync(ct);
        var sourceBreakByAnnouncementId = sourceBreaks
            .Where(talkBreak => talkBreak.AnnouncementId is not null)
            .ToDictionary(talkBreak => talkBreak.AnnouncementId!.Value);

        var composite = new Announcement
        {
            Id = id,
            ModeratorId = orderedAnnouncements[0].ModeratorId == 0
                ? fallbackModerator.Id
                : orderedAnnouncements[0].ModeratorId,
            Kind = orderedAnnouncements[0].Kind,
            ScriptText = string.Join("\n\n", orderedAnnouncements.Select(announcement => announcement.ScriptText)),
            VoicedText = string.Join("\n\n", orderedAnnouncements.Select(announcement => announcement.VoicedText)),
            FilePath = relativePath,
            DurationSeconds = WavFile.GetDurationSeconds(compositeWav),
            RelatedTrackId = orderedAnnouncements.Select(announcement => announcement.RelatedTrackId).FirstOrDefault(id => id is not null),
            CreatedAt = now,
            WasPlayed = false,
        };

        var clonedParts = CloneParts(
            orderedAnnouncements,
            sourceBreakByAnnouncementId,
            composite.Id,
            now);
        var compositeBreak = new TalkBreak
        {
            Id = Guid.NewGuid(),
            AnnouncementId = composite.Id,
            ModeratorId = composite.ModeratorId,
            Priority = PickPriority(clonedParts),
            Status = TalkBreakStatus.Rendered,
            Purpose = clonedParts.Select(part => part.Purpose).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? clonedParts[0].Purpose
                : "Composite",
            Title = "Announcement",
            TargetWindowStartUtc = clonedParts
                .Select(part => part.TargetWindowStartUtc)
                .Where(value => value is not null)
                .Min(),
            TargetWindowEndUtc = clonedParts
                .Select(part => part.TargetWindowEndUtc)
                .Where(value => value is not null)
                .Max(),
            CreatedAtUtc = now,
            RenderedAtUtc = now,
            ExpiresAtUtc = clonedParts
                .Select(part => part.ExpiresAtUtc)
                .Where(value => value is not null)
                .Min(),
            DurationSeconds = composite.DurationSeconds,
            Parts = clonedParts,
        };

        db.Announcements.Add(composite);
        db.TalkBreaks.Add(compositeBreak);
        foreach (var sourceBreak in sourceBreaks.Where(talkBreak => talkBreak.Status == TalkBreakStatus.Rendered))
        {
            sourceBreak.Status = TalkBreakStatus.Expired;
            foreach (var part in sourceBreak.Parts.Where(part => part.Status == TalkPartStatus.Rendered))
            {
                part.Status = TalkPartStatus.Expired;
            }
        }

        await db.SaveChangesAsync(ct);
        await analysisRecorder.AnalyzeAndStoreAsync(PlayoutItemType.Announcement, composite.Id, relativePath, ct);

        logger.LogInformation(
            "Rendered TalkBreak segment {AnnouncementId} from {Count} ordered part(s), duration {Duration:F1}s",
            composite.Id,
            orderedAnnouncements.Count,
            composite.DurationSeconds);

        return composite;

        List<TalkPart> CloneParts(
            IReadOnlyList<Announcement> announcements,
            IReadOnlyDictionary<Guid, TalkBreak> talkBreaks,
            Guid compositeAnnouncementId,
            DateTime createdAtUtc)
        {
            var sortOrder = 0;
            var parts = new List<TalkPart>();
            foreach (var announcement in announcements)
            {
                if (talkBreaks.TryGetValue(announcement.Id, out var sourceBreak) && sourceBreak.Parts.Count > 0)
                {
                    foreach (var part in sourceBreak.Parts.OrderBy(part => part.SortOrder))
                    {
                        parts.Add(new TalkPart
                        {
                            SortOrder = sortOrder++,
                            Kind = part.Kind,
                            Status = TalkPartStatus.Rendered,
                            Priority = part.Priority,
                            Purpose = part.Purpose,
                            AnnouncementId = compositeAnnouncementId,
                            RelatedTrackId = part.RelatedTrackId,
                            TalkBitId = part.TalkBitId,
                            JingleId = part.JingleId,
                            DesiredDurationSeconds = part.DesiredDurationSeconds,
                            WordBudget = part.WordBudget,
                            TargetWindowStartUtc = part.TargetWindowStartUtc,
                            TargetWindowEndUtc = part.TargetWindowEndUtc,
                            ExpiresAtUtc = part.ExpiresAtUtc,
                            CreatedAtUtc = createdAtUtc,
                        });
                    }

                    continue;
                }

                parts.Add(new TalkPart
                {
                    SortOrder = sortOrder++,
                    Kind = PartKindFor(announcement.Kind),
                    Status = TalkPartStatus.Rendered,
                    Priority = TalkBreakPriority.Normal,
                    Purpose = announcement.Kind.ToString(),
                    AnnouncementId = compositeAnnouncementId,
                    RelatedTrackId = announcement.RelatedTrackId,
                    CreatedAtUtc = createdAtUtc,
                });
            }

            return parts;
        }
    }

    private static TalkBreakPriority PickPriority(IReadOnlyList<TalkPart> parts)
    {
        if (parts.Any(part => part.Priority == TalkBreakPriority.Emergency))
        {
            return TalkBreakPriority.Emergency;
        }

        if (parts.Any(part => part.Priority == TalkBreakPriority.High))
        {
            return TalkBreakPriority.High;
        }

        if (parts.Any(part => part.Priority == TalkBreakPriority.Scheduled))
        {
            return TalkBreakPriority.Scheduled;
        }

        if (parts.Any(part => part.Priority == TalkBreakPriority.Normal))
        {
            return TalkBreakPriority.Normal;
        }

        return TalkBreakPriority.Low;
    }

    private static TalkPartKind PartKindFor(AnnouncementKind kind)
        => kind switch
        {
            AnnouncementKind.SongIntro => TalkPartKind.NextSongIntro,
            AnnouncementKind.SongOutro => TalkPartKind.PreviousSongComment,
            AnnouncementKind.Weather => TalkPartKind.Weather,
            AnnouncementKind.News => TalkPartKind.News,
            AnnouncementKind.ListenerGreeting => TalkPartKind.ListenerGreeting,
            AnnouncementKind.RequestDedication => TalkPartKind.RequestDedication,
            AnnouncementKind.Banter => TalkPartKind.Banter,
            AnnouncementKind.PersonalNote => TalkPartKind.PersonalNote,
            AnnouncementKind.Joke => TalkPartKind.Joke,
            AnnouncementKind.TalkBit => TalkPartKind.TalkBit,
            AnnouncementKind.Jingle => TalkPartKind.Jingle,
            AnnouncementKind.EmergencyMessage => TalkPartKind.EmergencyMessage,
            AnnouncementKind.StationId => TalkPartKind.StationId,
            AnnouncementKind.HostChange => TalkPartKind.HostChange,
            _ => TalkPartKind.Banter,
        };
}
