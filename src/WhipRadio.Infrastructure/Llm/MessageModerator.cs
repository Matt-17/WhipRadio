using System.Text.Json;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Llm;

public sealed record ModerationResult(bool Approved, string? Reason = null, string? ExtractedGenre = null);

public class MessageModerator(ITextGenerationService llm)
{
    public async Task<ModerationResult> ModerateAsync(
        ListenerMessage message,
        ShowContext context,
        string stationName,
        CancellationToken ct)
    {
        var genreRequest = message.Kind == ListenerMessageKind.Request && message.RequestGenre is not null
            ? $"Requested genre: {message.RequestGenre}"
            : string.Empty;

        var prompt = PromptTemplates.Render("MessageModerator", new Dictionary<string, string>
        {
            ["StationName"] = stationName,
            ["Genre"] = context.Genre,
            ["Subgenre"] = context.Subgenre,
            ["HostName"] = context.Moderator.Name,
            ["Kind"] = message.Kind == ListenerMessageKind.Request ? "MUSIC REQUEST" : "GREETING",
            ["SenderName"] = message.SenderName,
            ["MessageText"] = message.MessageText,
            ["GenreRequest"] = genreRequest,
        });

        try
        {
            var raw = await llm.CompleteAsync(string.Empty, prompt, ct);
            var trimmed = raw.Trim();

            // Strip markdown code fences if the LLM wraps the JSON
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                var lastFence = trimmed.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                {
                    trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
                }
            }

            using var doc = JsonDocument.Parse(trimmed);
            var approved = doc.RootElement.GetProperty("approved").GetBoolean();

            string? reason = null;
            if (!approved && doc.RootElement.TryGetProperty("reason", out var reasonEl))
            {
                reason = reasonEl.GetString();
            }

            string? genre = null;
            if (approved && doc.RootElement.TryGetProperty("genre", out var genreEl))
            {
                genre = genreEl.GetString()?.Trim().ToLowerInvariant();
            }

            return new ModerationResult(approved, reason, string.IsNullOrWhiteSpace(genre) ? null : genre);
        }
        catch
        {
            // If moderation fails (LLM unavailable, bad JSON), approve by default
            // so the station doesn't silently drop messages.
            return new ModerationResult(Approved: true);
        }
    }
}
