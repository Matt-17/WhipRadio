using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Everything the writer needs for one conversation script.</summary>
public sealed record ConversationScriptRequest(
    ConversationKind Kind,
    ConversationStructure Structure,
    string Topic,
    string Brief,
    int TargetDurationMinutes,
    IReadOnlyList<ConversationSpeakerBrief> Speakers,
    IReadOnlyList<ConversationChapter> Chapters,
    string StationName,
    string StationSlogan,
    string Language,
    IReadOnlyList<string> RecentEpisodeTitles,
    string? KnowledgeFacts = null)
{
    /// <summary>
    /// The brief plus gathered real-world background facts (Phase 6a). Facts
    /// ride the brief so both the single-call writer and the multi-agent
    /// director see them without separate template plumbing; the copyright
    /// rule (paraphrase, never quote) travels with them.
    /// </summary>
    public string BriefWithKnowledge => string.IsNullOrWhiteSpace(KnowledgeFacts)
        ? Brief
        : $"{Brief}\n\nBackground facts about the real artists/tracks involved "
          + $"(paraphrase in your own words; never quote source text, never recite lyrics):\n{KnowledgeFacts}";
}

/// <summary>A resolved speaker: identity plus the persona brief handed to the writer.</summary>
public sealed record ConversationSpeakerBrief(
    string SpeakerKey,
    string DisplayName,
    string ConversationRole,
    string PersonaBrief);

/// <summary>The validated script: title, speaker-keyed turns, and a readable transcript.</summary>
public sealed record ConversationScript(
    string Title,
    IReadOnlyList<ConversationTurn> Turns,
    string Transcript);

/// <summary>
/// Writes a whole multi-speaker conversation in ONE schema-constrained LLM call
/// (Phase 3c.2). Modeled on SpecialistHostCreationService.PlanAsync: prompt
/// template → CompleteAsync with a typed schema → parse/validate → one retry
/// with the rejection reason appended. A later multi-agent engine replaces this
/// class but emits the same <see cref="ConversationTurn"/> records.
/// </summary>
public sealed class ConversationScriptWriter(
    ITextGenerationService llm,
    ILogger<ConversationScriptWriter> logger)
{
    private const int WordsPerMinute = 150;
    private const int MaxAttempts = 2;

    public async Task<ConversationScript> WriteAsync(ConversationScriptRequest request, CancellationToken ct)
    {
        if (request.Speakers.Count < 2)
        {
            throw new InvalidOperationException("A conversation needs at least two speakers.");
        }

        var prompt = BuildPrompt(request);
        var systemPrompt = PromptTemplates.Render("ConversationWriter.System", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["Language"] = request.Language,
        });
        var jobLabel = $"Writing {request.Kind.ToString().ToLowerInvariant()} script: {Truncate(request.Topic, 60)}";

        Exception? lastError = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var userPrompt = attempt == 0
                ? prompt
                : $"{prompt}\n\nPrevious reply rejected: {lastError?.Message}. Return only valid JSON matching the schema.";
            var raw = await llm.CompleteAsync(
                new TextGenerationRequest(
                    systemPrompt,
                    userPrompt,
                    jobLabel,
                    StructuredJson.SchemaFor<ConversationScriptJson>(),
                    "conversationScript"),
                ct);
            try
            {
                return ParseScript(raw, request.Speakers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                logger.LogWarning(
                    "Conversation script attempt {Attempt}/{Max} rejected: {Reason}",
                    attempt + 1, MaxAttempts, ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"The conversation writer could not produce a valid script: {lastError?.Message}");
    }

    internal static ConversationScript ParseScript(
        string raw, IReadOnlyList<ConversationSpeakerBrief> speakers)
    {
        var result = StructuredJson.Parse<ConversationScriptJson>(raw);
        var script = result.IsValid
            ? result.Value!
            : throw new InvalidOperationException(result.Error ?? "JSON was empty.");

        if (string.IsNullOrWhiteSpace(script.Title))
        {
            throw new InvalidOperationException("The script is missing a title.");
        }

        if (script.Turns.Count < 2)
        {
            throw new InvalidOperationException("The script must contain at least two turns.");
        }

        var speakersByName = speakers.ToDictionary(
            speaker => speaker.DisplayName.Trim(),
            speaker => speaker,
            StringComparer.OrdinalIgnoreCase);

        var turns = new List<ConversationTurn>(script.Turns.Count);
        foreach (var turn in script.Turns)
        {
            if (!speakersByName.TryGetValue((turn.Speaker ?? string.Empty).Trim(), out var speaker))
            {
                throw new InvalidOperationException(
                    $"Turn speaker \"{turn.Speaker}\" is not in the roster "
                    + $"({string.Join(", ", speakers.Select(s => s.DisplayName))}).");
            }

            var markers = SpeechMarkerNormalizer.Normalize(turn.Text ?? string.Empty, allowBreath: true);
            var text = SpeechMarkerNormalizer.StripMarkers(turn.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException($"A turn by {speaker.DisplayName} has no spoken text.");
            }

            turns.Add(new ConversationTurn
            {
                SpeakerKey = speaker.SpeakerKey,
                Text = text,
                Markers = markers,
            });
        }

        if (turns.Select(turn => turn.SpeakerKey).Distinct().Count() < 2)
        {
            throw new InvalidOperationException("The script must use at least two distinct speakers.");
        }

        var keyToName = speakers.ToDictionary(speaker => speaker.SpeakerKey, speaker => speaker.DisplayName);
        var transcript = string.Join(
            "\n\n",
            turns.Select(turn => $"{keyToName[turn.SpeakerKey]}: {turn.Text}"));

        return new ConversationScript(script.Title.Trim(), turns, transcript);
    }

    private static string BuildPrompt(ConversationScriptRequest request)
    {
        var kindLabel = request.Kind == ConversationKind.Podcast ? "podcast episode" : "studio talk";
        var kindGuidance = request.Kind == ConversationKind.Podcast
            ? "This is a host-led podcast episode: the lead speaker frames the topics, hands the floor "
              + "to the guests, digs deeper with follow-up questions, and bridges between sections verbally."
            : "This is a tight two-way studio talk: quick exchanges, direct questions, concrete answers, "
              + "one clear thread from start to finish.";

        var roster = new StringBuilder();
        foreach (var speaker in request.Speakers)
        {
            roster.AppendLine($"- {speaker.DisplayName} ({speaker.ConversationRole}): {speaker.PersonaBrief}");
        }

        var structureBlock = request.Structure == ConversationStructure.Chaptered && request.Chapters.Count > 0
            ? "Chapters (cover them in order; the lead speaker bridges between them verbally — "
              + "no chapter labels in the spoken text):\n"
              + string.Join(
                  Environment.NewLine,
                  request.Chapters.Select((chapter, index) =>
                      $"{index + 1}. {chapter.Title} — {chapter.Intent} (~{Math.Max(1, chapter.TargetMinutes)} min)"))
            : "Structure: freeform — one continuous conversation with a natural arc from opening to sign-off.";

        var recentBlock = request.RecentEpisodeTitles.Count == 0
            ? "This is a fresh conversation; invent an angle that fits the brief."
            : "Invent a FRESH episode angle within the brief. Do not repeat these recent episodes:\n"
              + string.Join(Environment.NewLine, request.RecentEpisodeTitles.Select(title => $"- {title}"));

        return PromptTemplates.Render("ConversationWriter.Script", new Dictionary<string, string>
        {
            ["StationName"] = request.StationName,
            ["StationSlogan"] = string.IsNullOrWhiteSpace(request.StationSlogan)
                ? request.StationName
                : request.StationSlogan,
            ["KindLabel"] = kindLabel,
            ["KindGuidance"] = kindGuidance,
            ["Topic"] = string.IsNullOrWhiteSpace(request.Topic) ? "The lead speaker's choice within the brief." : request.Topic,
            ["Brief"] = string.IsNullOrWhiteSpace(request.BriefWithKnowledge) ? "No additional brief." : request.BriefWithKnowledge,
            ["DurationMinutes"] = request.TargetDurationMinutes.ToString(),
            ["WordBudget"] = (Math.Max(1, request.TargetDurationMinutes) * WordsPerMinute).ToString(),
            ["SpeakerRoster"] = roster.ToString().TrimEnd(),
            ["StructureBlock"] = structureBlock,
            ["RecentEpisodesBlock"] = recentBlock,
            ["Language"] = request.Language,
        });
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    internal sealed record ConversationScriptJson(
        [property: JsonRequired] string Title,
        [property: JsonRequired] IReadOnlyList<ConversationScriptTurnJson> Turns);

    internal sealed record ConversationScriptTurnJson(
        [property: JsonRequired] string Speaker,
        [property: JsonRequired] string Text);
}
