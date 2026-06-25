using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// Single-run announcement pipeline: one LLM call writes the clean script, the
/// speech-marked delivery, and the per-delivery voice direction together. Replaces the
/// former two-stage ScriptWriter + VoiceDirector.
/// </summary>
public partial class AnnouncementWriter(ITextGenerationService llm) : IAnnouncementWriter
{
    public async Task<SpokenAnnouncement> WriteAsync(AnnouncementRequest request, Moderator moderator, CancellationToken ct)
    {
        var systemPrompt = PromptTemplates.Render("AnnouncementWriter.System", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Language"] = request.Language,
            ["LengthHint"] = string.IsNullOrEmpty(request.LengthHint) ? "2-5 sentences." : request.LengthHint,
            ["HostName"] = moderator.Name,
            ["PersonaPrompt"] = moderator.PersonaPrompt,
            ["Style"] = moderator.Style,
            ["Gender"] = moderator.Gender == ModeratorGenders.Male ? "male" : "female",
        });

        if (request.PromptContext is { } context)
        {
            if (context.BaselineTraits is not null && context.CurrentTraits is not null)
            {
                systemPrompt =
                    $"{systemPrompt}\n\nKeep the baseline persona stable ({context.BaselineTraits}), " +
                    $"but shade this delivery with the current mood traits ({context.CurrentTraits}).";
            }

            systemPrompt = $"{systemPrompt}\n\n{context.RenderSituation()}";
        }

        var userPrompt = BuildUserPrompt(request);
        var jobLabel = ScriptJobLabel(request.Kind);
        var raw = await llm.CompleteAsync(
            new TextGenerationRequest(systemPrompt, userPrompt, jobLabel, StructuredJson.SchemaFor<SpokenDeliveryDto>(), "spokenDelivery"),
            ct);
        return await ParseOrRetryAsync(raw, systemPrompt, userPrompt, jobLabel, ct);
    }

    private async Task<SpokenAnnouncement> ParseOrRetryAsync(
        string raw,
        string systemPrompt,
        string userPrompt,
        string jobLabel,
        CancellationToken ct)
    {
        if (TryBuild(raw, out var result))
        {
            return result;
        }

        var retryPrompt =
            $"{userPrompt}\n\nPrevious reply was not valid. Return ONLY one JSON object with " +
            """non-empty "script" and "delivery" string fields and an optional "voice" object.""";
        var retry = await llm.CompleteAsync(
            new TextGenerationRequest(systemPrompt, retryPrompt, $"{jobLabel} retry", StructuredJson.SchemaFor<SpokenDeliveryDto>(), "spokenDelivery"),
            ct);
        if (TryBuild(retry, out result))
        {
            return result;
        }

        throw new InvalidOperationException("The announcement writer returned invalid script/delivery JSON twice.");
    }

    /// <summary>Parses the combined DTO and cleans both text fields: the transcript is
    /// stripped of any stray markers, the delivery keeps its markers.</summary>
    private static bool TryBuild(string raw, out SpokenAnnouncement result)
    {
        result = null!;
        var parsed = StructuredJson.Parse<SpokenDeliveryDto>(raw);
        if (!parsed.IsValid || parsed.Value is null)
        {
            return false;
        }

        var dto = parsed.Value;

        // Delivery keeps its speech markers; only meta-chatter/markdown is removed.
        if (!LlmOutputSanitizer.TrySanitizeSpokenText(dto.Delivery, out var delivery, out _)
            || string.IsNullOrWhiteSpace(delivery))
        {
            return false;
        }

        // Script is the transcript: sanitize, then strip any markers the model leaked in.
        var script = LlmOutputSanitizer.TrySanitizeSpokenText(dto.Script, out var cleanScript, out _)
            && !string.IsNullOrWhiteSpace(cleanScript)
                ? SpeechMarkerNormalizer.StripMarkers(cleanScript)
                : SpeechMarkerNormalizer.StripMarkers(delivery);

        if (string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        result = new SpokenAnnouncement(
            EnsureTerminalPunctuation(script), delivery, NormalizePrompt(dto.Voice?.DeliveryPrompt), dto.Voice?.Rate);
        return true;
    }

    private static string? NormalizePrompt(string? prompt)
        => string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();

    /// <summary>Guarantees the transcript ends on a sentence terminator (allowing a trailing
    /// closing quote/bracket), so even a one-line script reads cleanly.</summary>
    private static string EnsureTerminalPunctuation(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return TerminalPunctuationRegex().IsMatch(trimmed) ? trimmed : trimmed + ".";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[.!?…][""'”’)\]]?$")]
    private static partial System.Text.RegularExpressions.Regex TerminalPunctuationRegex();

    private static string ScriptJobLabel(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.SongIntro => "Writing song intro",
        AnnouncementKind.SongOutro => "Writing song outro",
        AnnouncementKind.Weather => "Writing weather report",
        AnnouncementKind.News => "Writing news bulletin",
        AnnouncementKind.Joke => "Writing joke",
        AnnouncementKind.Banter => "Writing banter",
        AnnouncementKind.PersonalNote => "Writing personal note",
        AnnouncementKind.TalkBit => "Writing talk bit",
        AnnouncementKind.EmergencyMessage => "Writing emergency message",
        AnnouncementKind.HostChange => "Writing host handover",
        AnnouncementKind.ListenerGreeting => "Writing listener greeting",
        AnnouncementKind.RequestDedication => "Writing song dedication",
        AnnouncementKind.StationId => "Writing station ID",
        _ => "Writing announcement",
    };

    private static string StationIdTemplate(AnnouncementRequest request)
        => request.PromptContext?.Purpose switch
        {
            "NewsHandover" => "ScriptWriter.NewsHandover",
            "WeatherHandoff" => "ScriptWriter.WeatherHandoff",
            _ => "ScriptWriter.StationId",
        };

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
        AnnouncementKind.News => PromptTemplates.Render("ScriptWriter.News", new Dictionary<string, string>
        {
            ["NewsFacts"] = request.Facts ?? string.Empty,
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
        AnnouncementKind.StationId => PromptTemplates.Render(StationIdTemplate(request), new Dictionary<string, string>
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
