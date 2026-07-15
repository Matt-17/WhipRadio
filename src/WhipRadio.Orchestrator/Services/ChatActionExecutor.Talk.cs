using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecutePlanTalkBreakAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Moderator moderator = await ResolveTalkOwnerAsync(call, context, ct);
        string partsArgument = Require(call, "parts");

        List<(TalkPartKind Kind, string Purpose)> parts = ParseTalkParts(partsArgument);
        if (parts.Count == 0)
        {
            return Failed(call, "No talk parts were given. Provide 'kind: purpose' entries separated by semicolons.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime expiresAt = now.AddDays(7);
        string title = Optional(call, "title") ?? $"Planned talk break for {moderator.Name}";

        TalkBreak talkBreak = new()
        {
            Id = Guid.NewGuid(),
            ModeratorId = moderator.Id,
            Priority = TalkBreakPriority.Scheduled,
            Status = TalkBreakStatus.Pending,
            Purpose = title,
            Title = title,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            Parts = parts.Select((part, index) => new TalkPart
            {
                SortOrder = index,
                Kind = part.Kind,
                Status = TalkPartStatus.Pending,
                Priority = TalkBreakPriority.Scheduled,
                Purpose = part.Purpose,
                DesiredDurationSeconds = 40,
                CreatedAtUtc = now,
                ExpiresAtUtc = expiresAt,
            }).ToList(),
        };

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.TalkBreaks.Add(talkBreak);
        await db.SaveChangesAsync(ct);
        return Succeeded(
            call,
            $"Planned a {parts.Count}-part talk break for {moderator.Name}: "
            + string.Join(", ", parts.Select(part => part.Kind)));
    }

    private async Task<ChatActionRecord> ExecuteCreateTalkBitAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        Moderator moderator = await ResolveTalkOwnerAsync(call, context, ct);
        string premise = Require(call, "premise");
        string tags = Optional(call, "kind") ?? "station_bit";

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.TalkBits.Add(new TalkBit
        {
            Id = Guid.NewGuid(),
            ModeratorId = moderator.Id,
            Premise = premise,
            Tags = tags,
            Status = TalkBitStatus.Active,
            CooldownDays = 5,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return Succeeded(call, $"Reusable bit for {moderator.Name} saved: \"{premise}\".");
    }

    private async Task<ChatActionRecord> ExecuteRememberAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string note = Require(call, "note");

        if (context.SenderModerator is { } host)
        {
            DateOnly today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
            await moderatorMemory.RememberAsync(host.Id, ModeratorMemoryLayer.DayMemory, today, note, ct);
            return Succeeded(call, "Noted for continuity.");
        }

        if (context.SenderRole == CharacterRole.Artist && context.Sender.Ref.EntityId is { } memberId)
        {
            // Embedding write is failure-soft and must not block the reply.
            participantMemory.StoreFactsAsync(
                ConversationParticipant.MemberKey(memberId),
                [note],
                "chat:remember",
                CancellationToken.None).Forget();
            return Succeeded(call, "Noted for continuity.");
        }

        return Failed(call, "There is no memory store for this participant.");
    }

    /// <summary>Hosts own their notes/bits/breaks; the director may name another host.</summary>
    private async Task<Moderator> ResolveTalkOwnerAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        if (context.SenderModerator is { } host)
        {
            return host;
        }

        string? hostArg = Optional(call, "host");
        if (context.SenderRole == CharacterRole.ProgramDirector && !string.IsNullOrWhiteSpace(hostArg))
        {
            return await director.ResolveHostAsync(hostArg, ct);
        }

        throw new InvalidOperationException(
            "Name the target host with the 'host' argument.");
    }

    private static List<(TalkPartKind Kind, string Purpose)> ParseTalkParts(string argument)
    {
        List<(TalkPartKind, string)> parts = [];
        foreach (string entry in argument.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = entry.IndexOf(':');
            string kindText = colon >= 0 ? entry[..colon].Trim() : entry.Trim();
            string purpose = colon >= 0 ? entry[(colon + 1)..].Trim() : entry.Trim();
            TalkPartKind kind = MapKind(kindText);
            parts.Add((kind, string.IsNullOrWhiteSpace(purpose) ? kindText : purpose));
        }

        return parts;
    }

    private static TalkPartKind MapKind(string text)
    {
        if (Enum.TryParse(text, ignoreCase: true, out TalkPartKind parsed))
        {
            return parsed;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "intro" or "songintro" or "song intro" => TalkPartKind.NextSongIntro,
            "outro" or "songoutro" or "song outro" => TalkPartKind.PreviousSongComment,
            "news" => TalkPartKind.Banter,
            "ad" or "advert" or "promo" => TalkPartKind.StationId,
            "bit" => TalkPartKind.TalkBit,
            "weather" => TalkPartKind.Weather,
            "joke" => TalkPartKind.Joke,
            _ => TalkPartKind.Banter,
        };
    }
}
