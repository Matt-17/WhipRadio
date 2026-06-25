using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Prompting;

namespace WhipRadio.Infrastructure.Llm;

public sealed record ModerationResult(bool Approved, string? Reason = null, string? ExtractedGenre = null);

/// <summary>Schema-constrained shape of the moderation reply.</summary>
internal sealed record ModerationDto(
    [property: JsonRequired] bool Approved,
    string? Reason = null,
    string? Genre = null);

public class MessageModerator(ITextGenerationService llm, IPromptContextBuilder promptContextBuilder)
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
            var promptContext = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.MessageModeration,
                    Moderator: context.Moderator,
                    Format: context.Format,
                    Facts: message.MessageText,
                    Purpose: message.Kind == ListenerMessageKind.Request
                        ? "Moderate listener music request"
                        : "Moderate listener greeting"),
                ct);

            var raw = await llm.CompleteAsync(
                new TextGenerationRequest(
                    promptContext.RenderSituation(),
                    prompt,
                    "Moderating listener message",
                    StructuredJson.SchemaFor<ModerationDto>(),
                    "moderation"),
                ct);

            var parsed = StructuredJson.Parse<ModerationDto>(raw);
            if (!parsed.IsValid)
            {
                // Bad JSON: approve by default so the station doesn't silently drop messages.
                return new ModerationResult(Approved: true);
            }

            var dto = parsed.Value!;
            var reason = dto.Approved ? null : dto.Reason;
            var genre = dto.Approved ? dto.Genre?.Trim().ToLowerInvariant() : null;
            return new ModerationResult(dto.Approved, reason, string.IsNullOrWhiteSpace(genre) ? null : genre);
        }
        catch
        {
            // If moderation fails (LLM unavailable, bad JSON), approve by default
            // so the station doesn't silently drop messages.
            return new ModerationResult(Approved: true);
        }
    }
}
