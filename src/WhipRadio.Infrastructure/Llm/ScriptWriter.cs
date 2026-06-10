using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Stage 1 of the announcement pipeline: writes the spoken content.</summary>
public class ScriptWriter(ITextGenerationService llm) : IScriptWriter
{
    public async Task<string> WriteAsync(AnnouncementRequest request, CancellationToken ct)
    {
        var systemPrompt = PromptTemplates.Render("ScriptWriter.System", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Language"] = request.Language,
            ["LengthHint"] = string.IsNullOrEmpty(request.LengthHint) ? "2-5 sentences." : request.LengthHint,
        });

        var userPrompt = BuildUserPrompt(request);
        var script = await llm.CompleteAsync(systemPrompt, userPrompt, ct);
        return LlmOutputSanitizer.Sanitize(script);
    }

    private static string BuildUserPrompt(AnnouncementRequest request) => request.Kind switch
    {
        AnnouncementKind.SongIntro => PromptTemplates.Render("ScriptWriter.SongIntro", TrackValues(request)),
        AnnouncementKind.SongOutro => PromptTemplates.Render("ScriptWriter.SongOutro", TrackValues(request)),
        AnnouncementKind.Weather => PromptTemplates.Render("ScriptWriter.Weather", new Dictionary<string, string>
        {
            ["WeatherFacts"] = request.Facts ?? string.Empty,
        }),
        AnnouncementKind.Joke => PromptTemplates.Render("ScriptWriter.Joke", new Dictionary<string, string>()),
        AnnouncementKind.Banter => PromptTemplates.Render("ScriptWriter.Banter", new Dictionary<string, string>
        {
            ["Facts"] = request.Facts ?? "everyday radio life",
        }),
        AnnouncementKind.PersonalNote => PromptTemplates.Render("ScriptWriter.PersonalNote", new Dictionary<string, string>
        {
            ["Facts"] = string.IsNullOrWhiteSpace(request.Facts) ? "nothing yet" : request.Facts,
        }),
        AnnouncementKind.HostChange => PromptTemplates.Render("ScriptWriter.HostChange", new Dictionary<string, string>
        {
            ["Facts"] = request.Facts ?? "a new show begins",
        }),
        AnnouncementKind.ListenerGreeting => PromptTemplates.Render("ScriptWriter.ListenerGreeting", ParseGreetingFacts(request.Facts)),
        AnnouncementKind.StationId => PromptTemplates.Render("ScriptWriter.StationId", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Facts"] = request.Facts ?? "regular programming",
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown announcement kind"),
    };

    /// <summary>Facts for greetings arrive as "SenderName|MessageText".</summary>
    private static Dictionary<string, string> ParseGreetingFacts(string? facts)
    {
        var parts = (facts ?? string.Empty).Split('|', 2);
        return new Dictionary<string, string>
        {
            ["SenderName"] = parts.ElementAtOrDefault(0) is { Length: > 0 } name ? name : "a listener",
            ["MessageText"] = parts.ElementAtOrDefault(1) ?? "Greetings to everyone listening!",
        };
    }

    private static Dictionary<string, string> TrackValues(AnnouncementRequest request) => new()
    {
        ["Title"] = request.Track?.Title ?? "an untitled tune",
        ["Artist"] = request.Track?.Artist?.Name ?? "one of our studio artists",
        ["Genre"] = string.IsNullOrEmpty(request.Track?.Subgenre) ? request.Track?.Genre ?? "unknown" : request.Track.Subgenre,
        ["Style"] = request.Track?.Style ?? "easygoing",
    };
}
