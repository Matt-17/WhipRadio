using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// Designs one-off guest personas from a short hint plus the mandatory station
/// context, following the artist-profile pattern (schema-constrained JSON).
/// </summary>
public class GuestProfileWriter(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are WhipRadio's booking producer. You invent believable fictional radio guests. Return only valid JSON.";

    public async Task<GuestProfilePlan> DesignGuestAsync(
        string? hint,
        StationSettings settings,
        IReadOnlyCollection<string> existingNames,
        CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("GuestProfilePlanner", new Dictionary<string, string>
        {
            ["Hint"] = string.IsNullOrWhiteSpace(hint) ? "No manual hint. Invent a guest that fits the station." : hint.Trim(),
            ["StationName"] = FirstNonEmpty(settings.StationName, "WhipRadio"),
            ["StationSlogan"] = FirstNonEmpty(settings.StationSlogan, "No slogan configured."),
            ["StationVision"] = FirstNonEmpty(settings.StationVision, "No station vision configured."),
            ["StationMission"] = FirstNonEmpty(settings.StationMission, "No station mission configured."),
            ["Language"] = StationLanguages.Normalize(settings.DefaultLanguage),
            ["AvoidNames"] = existingNames.Count == 0
                ? "- none"
                : string.Join(Environment.NewLine, existingNames.OrderBy(name => name).Select(name => $"- {name}")),
        });

        var reply = await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Creating guest profile", StructuredJson.SchemaFor<GuestProfileJson>(), "guestProfile"),
            ct);
        return ParseProfile(reply, prompt, hint);
    }

    public async Task<string?> WriteSelfIntroAsync(Guest guest, string language, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("GuestSelfIntro", new Dictionary<string, string>
        {
            ["GuestName"] = guest.Name,
            ["Expertise"] = FirstNonEmpty(guest.Expertise, "a guest"),
            ["Biography"] = FirstNonEmpty(guest.Biography, "(no biography on record)"),
            ["Language"] = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim(),
        });

        var reply = LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Writing guest self-introduction", StructuredJson.SchemaFor<TextDto>(), "text"),
            ct)));
        var intro = string.Join(" ", reply
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(intro) ? null : intro.Trim();
    }

    private static GuestProfilePlan ParseProfile(string reply, string generationPrompt, string? hint)
    {
        var parsed = StructuredJson.Parse<GuestProfileJson>(reply);
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException($"Guest profile response was not valid JSON: {parsed.Error}");
        }

        var profile = parsed.Value!;
        return new GuestProfilePlan(
            Require(profile.Name, "name").Trim('"'),
            Require(profile.Expertise, "expertise"),
            NormalizeGender(profile.Gender),
            profile.Age is { } age ? Math.Clamp(age, 16, 99) : null,
            profile.Interests?.Trim() ?? string.Empty,
            profile.Personality?.Trim() ?? string.Empty,
            Require(profile.Biography, "biography"),
            Require(profile.DeepBackground, "deepBackground"),
            Require(profile.VoiceCreationPrompt, "voiceCreationPrompt"),
            string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
            generationPrompt);
    }

    private static string NormalizeGender(string? gender)
    {
        var value = (gender ?? string.Empty).Trim().ToLowerInvariant();
        return value is "male" or "female" ? value : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Require(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Guest profile JSON missing required field '{fieldName}'.")
            : value.Trim();
}

internal sealed record GuestProfileJson(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Expertise,
    [property: JsonRequired] string Gender,
    [property: JsonRequired] string Interests,
    [property: JsonRequired] string Personality,
    [property: JsonRequired] string Biography,
    [property: JsonRequired] string DeepBackground,
    [property: JsonRequired] string VoiceCreationPrompt,
    int? Age = null);

public sealed record GuestProfilePlan(
    string Name,
    string Expertise,
    string Gender,
    int? Age,
    string Interests,
    string Personality,
    string Biography,
    string DeepBackground,
    string VoiceCreationPrompt,
    string? Hint,
    string GenerationPrompt);
