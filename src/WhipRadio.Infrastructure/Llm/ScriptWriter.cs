using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
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

        if (request.PromptContext is not null)
        {
            systemPrompt = $"{systemPrompt}\n\n{request.PromptContext.RenderSituation()}";
        }

        var userPrompt = BuildUserPrompt(request);
        var script = await llm.CompleteAsync(systemPrompt, userPrompt, ct);
        return LlmOutputSanitizer.Sanitize(script);
    }

    private static string BuildUserPrompt(AnnouncementRequest request) => request.Kind switch
    {
        AnnouncementKind.SongIntro => WithTalkDepthInstruction(
            PromptTemplates.Render("ScriptWriter.SongIntro", TrackValues(request)),
            request),
        AnnouncementKind.SongOutro => WithTalkDepthInstruction(
            PromptTemplates.Render("ScriptWriter.SongOutro", TrackValues(request)),
            request),
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
        AnnouncementKind.TalkBit => PromptTemplates.Render("ScriptWriter.TalkBit", new Dictionary<string, string>
        {
            ["Premise"] = string.IsNullOrWhiteSpace(request.Facts)
                ? "a short evergreen host story"
                : request.Facts,
        }),
        AnnouncementKind.EmergencyMessage => PromptTemplates.Render("ScriptWriter.EmergencyMessage", new Dictionary<string, string>
        {
            ["Message"] = string.IsNullOrWhiteSpace(request.Facts)
                ? "an important station update"
                : request.Facts,
        }),
        AnnouncementKind.HostChange => PromptTemplates.Render("ScriptWriter.HostChange", new Dictionary<string, string>
        {
            ["Facts"] = request.Facts ?? "a new show begins",
        }),
        AnnouncementKind.ListenerGreeting => PromptTemplates.Render("ScriptWriter.ListenerGreeting", new Dictionary<string, string>
        {
            ["Messages"] = string.IsNullOrWhiteSpace(request.Facts)
                ? "- a listener: \"Greetings to everyone listening!\""
                : request.Facts,
        }),
        AnnouncementKind.RequestDedication => PromptTemplates.Render("ScriptWriter.RequestDedication", ParseDedicationFacts(request)),
        AnnouncementKind.StationId => PromptTemplates.Render("ScriptWriter.StationId", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Facts"] = request.Facts ?? "regular programming",
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown announcement kind"),
    };

    private static string WithTalkDepthInstruction(string prompt, AnnouncementRequest request)
        => request.PromptContext?.FormatTalkDepth is TalkDepth depth
            ? $"{prompt}\n{TalkPlanner.ScriptInstruction(depth, request.Kind)}"
            : prompt;

    /// <summary>Dedication facts arrive as "SenderName|MessageText|Genre"; the track rides on the request.</summary>
    private static Dictionary<string, string> ParseDedicationFacts(AnnouncementRequest request)
    {
        var parts = (request.Facts ?? string.Empty).Split('|', 3);
        return new Dictionary<string, string>
        {
            ["SenderName"] = parts.ElementAtOrDefault(0) is { Length: > 0 } name ? name : "a listener",
            ["MessageText"] = parts.ElementAtOrDefault(1) ?? "a song wish",
            ["Genre"] = parts.ElementAtOrDefault(2) is { Length: > 0 } genre ? genre : "something special",
            ["Title"] = request.Track?.Title ?? "a brand-new tune",
            ["Artist"] = request.Track?.Artist?.Name ?? "one of our studio artists",
        };
    }

    private static Dictionary<string, string> TrackValues(AnnouncementRequest request) => new()
    {
        ["Title"] = request.Track?.Title ?? "an untitled tune",
        ["Artist"] = request.Track?.Artist?.Name ?? "one of our studio artists",
        ["Genre"] = string.IsNullOrEmpty(request.Track?.Subgenre) ? request.Track?.Genre ?? "unknown" : request.Track.Subgenre,
        ["Style"] = request.Track?.Style ?? "easygoing",
        ["Language"] = request.Track?.Language ?? request.Language,
        ["Story"] = string.IsNullOrWhiteSpace(request.Track?.SongStory)
            ? "No stored song story."
            : request.Track!.SongStory!,
    };
}
