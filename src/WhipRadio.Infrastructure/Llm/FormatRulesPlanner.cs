using System.Text.Json;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// LLM helper for the program director: reads a format's free-text description and
/// produces structured <see cref="FormatSelectionRules"/>. Called once at
/// format-creation time so per-pick selection stays fast and deterministic. Falls
/// back to <see cref="FormatSelectionRules.Default"/> on any LLM/parse failure —
/// never blocks format creation.
/// </summary>
public class FormatRulesPlanner(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are the music-rotation engineer for a radio station. " +
        "Answer with the requested JSON only, no commentary.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<FormatSelectionRules> PlanAsync(
        Format format,
        IReadOnlyCollection<ArtistCatalogEntry> artistCatalog,
        CancellationToken ct)
    {
        try
        {
            var prompt = PromptTemplates.Render("FormatRulesPlanner", new Dictionary<string, string>
            {
                ["StationName"] = format.Name,
                ["StationSlogan"] = "(see station settings)",
                ["FormatName"] = format.Name,
                ["Description"] = string.IsNullOrWhiteSpace(format.Description) ? format.Name : format.Description,
                ["Reason"] = string.IsNullOrWhiteSpace(format.Reason) ? "(none)" : format.Reason,
                ["Genre"] = string.IsNullOrWhiteSpace(format.Genre) ? "(any)" : format.Genre,
                ["Subgenre"] = string.IsNullOrWhiteSpace(format.Subgenre) ? "(any)" : format.Subgenre,
                ["ArtistCatalog"] = FormatCatalog(artistCatalog),
            });

            var reply = CleanStructuredOutput(await llm.CompleteAsync(SystemPrompt, prompt, "Planning format rules", ct));
            return ParseRules(reply, artistCatalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FormatSelectionRules.Default;
        }
    }

    public static FormatSelectionRules ParseRules(string reply, IReadOnlyCollection<ArtistCatalogEntry> artistCatalog)
    {
        var json = ExtractJsonObject(reply);
        if (json is null)
        {
            return FormatSelectionRules.Default;
        }

        FormatRulesJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FormatRulesJson>(json, JsonOptions);
            if (parsed is null)
            {
                return FormatSelectionRules.Default;
            }
        }
        catch (JsonException)
        {
            return FormatSelectionRules.Default;
        }

        var mode = ParseMode(parsed.Mode);
        var featured = ResolveFeaturedArtist(parsed.FeaturedArtistId, artistCatalog);

        // An artist feature without a resolvable featured artist falls back to standard rotation.
        if (mode is SelectionMode.SingleArtistFeature or SelectionMode.SpotlightArtist && featured is null)
        {
            mode = SelectionMode.StandardRotation;
        }

        return new FormatSelectionRules
        {
            Mode = mode,
            FeaturedArtistId = featured,
            MaxArtistPlaysPerHour = parsed.MaxArtistPlaysPerHour is int max && max is > 0 and <= 10 ? max : null,
            ArtistLookbackTracks = parsed.ArtistLookbackTracks is int lookback && lookback is > 0 and <= 30 ? lookback : 8,
            SubgenreRotation = parsed.SubgenreRotation ?? true,
            PreferHostGenres = parsed.PreferHostGenres ?? true,
            Theme = string.IsNullOrWhiteSpace(parsed.Theme) ? null : parsed.Theme!.Trim(),
        };
    }

    private static SelectionMode ParseMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "singleartistfeature" or "single-artist-feature" or "artistfeature" => SelectionMode.SingleArtistFeature,
        "spotlightartist" or "spotlight" => SelectionMode.SpotlightArtist,
        "themeblock" or "theme" or "theme-block" => SelectionMode.ThemeBlock,
        "freeform" or "free-form" => SelectionMode.Freeform,
        _ => SelectionMode.StandardRotation,
    };

    private static Guid? ResolveFeaturedArtist(string? raw, IReadOnlyCollection<ArtistCatalogEntry> catalog)
    {
        if (string.IsNullOrWhiteSpace(raw) || catalog.Count == 0)
        {
            return null;
        }

        if (Guid.TryParse(raw.Trim(), out var parsed))
        {
            return catalog.FirstOrDefault(entry => entry.Id == parsed)?.Id;
        }

        // Tolerate the model returning a name instead of a Guid.
        var name = raw.Trim();
        return catalog.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string FormatCatalog(IReadOnlyCollection<ArtistCatalogEntry> catalog)
    {
        if (catalog.Count == 0)
        {
            return "(no artists in the catalog yet)";
        }

        return string.Join(Environment.NewLine, catalog.Take(40).Select(entry =>
            $"- {entry.Id} | {entry.Name} | {entry.Genre}/{entry.Subgenre}"));
    }

    private static string CleanStructuredOutput(string text)
    {
        var result = text.Trim();
        if (result.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = result.IndexOf('\n');
            if (firstNewline >= 0)
            {
                result = result[(firstNewline + 1)..];
            }

            var fenceEnd = result.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                result = result[..fenceEnd];
            }
        }

        return result.Trim();
    }

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }
}

internal sealed record FormatRulesJson(
    string? Mode,
    string? FeaturedArtistId,
    int? MaxArtistPlaysPerHour,
    int? ArtistLookbackTracks,
    bool? SubgenreRotation,
    bool? PreferHostGenres,
    string? Theme);

/// <summary>One row of the artist catalog passed to the rules planner.</summary>
public sealed record ArtistCatalogEntry(Guid Id, string Name, string Genre, string Subgenre);
