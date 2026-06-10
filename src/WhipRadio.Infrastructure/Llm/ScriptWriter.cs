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
        AnnouncementKind.StationId => PromptTemplates.Render("ScriptWriter.StationId", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Facts"] = request.Facts ?? "regular programming",
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown announcement kind"),
    };

    private static Dictionary<string, string> TrackValues(AnnouncementRequest request) => new()
    {
        ["Title"] = request.Track?.Title ?? "an untitled tune",
        ["Genre"] = request.Track?.Genre ?? "unknown",
        ["Style"] = request.Track?.Style ?? "easygoing",
    };
}
