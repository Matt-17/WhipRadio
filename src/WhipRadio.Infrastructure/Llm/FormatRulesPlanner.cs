using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
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

            var reply = await llm.CompleteAsync(
                new TextGenerationRequest(
                    SystemPrompt,
                    prompt,
                    "Planning format rules",
                    StructuredJson.SchemaFor<FormatRulesJson>(),
                    "formatRules"),
                ct);
            return ParseRules(reply, artistCatalog);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FormatSelectionRules.Default;
        }
    }

    public static FormatSelectionRules ParseRules(string reply, IReadOnlyCollection<ArtistCatalogEntry> artistCatalog)
    {
        var result = StructuredJson.Parse<FormatRulesJson>(reply);
        if (!result.IsValid)
        {
            return FormatSelectionRules.Default;
        }

        var parsed = result.Value!;
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

}

internal sealed record FormatRulesJson(
    [property: JsonRequired] string? Mode,
    string? FeaturedArtistId = null,
    int? MaxArtistPlaysPerHour = null,
    int? ArtistLookbackTracks = null,
    bool? SubgenreRotation = null,
    bool? PreferHostGenres = null,
    string? Theme = null);

/// <summary>One row of the artist catalog passed to the rules planner.</summary>
public sealed record ArtistCatalogEntry(Guid Id, string Name, string Genre, string Subgenre);
