using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>LLM helper for the music producer: invents artists, titles and lyrics.</summary>
public class MusicCopywriter(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are a creative assistant for a radio station's music department. " +
        "Answer exactly as instructed, with no extra commentary.";

    public async Task<(string Name, string Style)> InventArtistAsync(
        string genre, string subgenre, IReadOnlyCollection<string> existingNames, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistMaker", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Subgenre"] = string.IsNullOrEmpty(subgenre) ? genre : subgenre,
            ["AvoidNames"] = existingNames.Count == 0 ? "(none yet)" : string.Join(", ", existingNames.Take(20)),
        });

        var reply = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        string? name = null, style = null;
        foreach (var line in reply.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase))
            {
                name = line["NAME:".Length..].Trim();
            }
            else if (line.StartsWith("STYLE:", StringComparison.OrdinalIgnoreCase))
            {
                style = line["STYLE:".Length..].Trim();
            }
        }

        name = string.IsNullOrWhiteSpace(name) ? $"The {subgenre} Collective" : name;
        style = string.IsNullOrWhiteSpace(style) ? $"{subgenre}, catchy and radio-friendly" : style;
        return (name, style);
    }

    public async Task<string> InventTitleAsync(
        Artist artist, IReadOnlyCollection<string> existingTitles, CancellationToken ct)
    {
        var forbidden = TitleWordGuard.MostFrequentWords(existingTitles, take: 8);
        var prompt = PromptTemplates.Render("MusicTitle", new Dictionary<string, string>
        {
            ["ArtistName"] = artist.Name,
            ["ArtistStyle"] = artist.StyleDescriptor,
            ["Subgenre"] = string.IsNullOrEmpty(artist.Subgenre) ? artist.Genre : artist.Subgenre,
            ["ForbiddenWords"] = forbidden.Count == 0 ? "(none)" : string.Join(", ", forbidden),
            ["AvoidTitles"] = existingTitles.Count == 0 ? "(none yet)" : string.Join("; ", existingTitles.TakeLast(15)),
        });

        var title = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        var firstLine = title.Split('\n')[0].Trim();

        // Last line of defense against the LLM repeating itself verbatim.
        if (existingTitles.Any(t => string.Equals(t, firstLine, StringComparison.OrdinalIgnoreCase)))
        {
            firstLine = $"{firstLine} No. {Random.Shared.Next(2, 99)}";
        }

        return string.IsNullOrWhiteSpace(firstLine) ? $"Untitled {artist.Subgenre} tune" : firstLine;
    }

    public async Task<string> WriteLyricsAsync(string genre, string language, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("LyricsWriter", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Language"] = language,
        });
        return LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
    }
}

/// <summary>Finds overused words in existing titles so prompts can forbid them.</summary>
public static class TitleWordGuard
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "into", "from", "your", "this", "that",
        "der", "die", "das", "und", "ein", "eine", "von", "mit",
    };

    public static IReadOnlyList<string> MostFrequentWords(IEnumerable<string> titles, int take)
    {
        return titles
            .SelectMany(t => t.Split([' ', '-', ':', ',', '.'], StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 3 && !Stopwords.Contains(w))
            .GroupBy(w => w)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(take)
            .Select(g => g.Key)
            .ToList();
    }
}
