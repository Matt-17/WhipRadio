using System.Text.Json;
using System.Text.RegularExpressions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>LLM helper for the music producer: invents artists and plans artist-owned songs.</summary>
public class MusicCopywriter(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are a creative assistant for a radio station's music department. " +
        "Answer exactly as instructed, with no extra commentary.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex FunctionValueRegex = new(
        @"(?im)^\s*(?<name>Title|Style|Language|Vocals|Story)\(\s*(?:""(?<quoted>(?:\\.|[^""\\])*)""|(?<bare>.*?))\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationRegex = new(
        @"(?im)^\s*DurationSeconds\(\s*""?(?<value>\d{1,4})""?\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LyricsBlockRegex = new(
        @"(?is)Lyrics\(\s*(?:""""""(?<block>.*?)""""""|""(?<quoted>(?:\\.|[^""\\])*)""|(?<bare>.*?))\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<(string Name, string Style, string? Biography)> InventArtistAsync(
        string genre, string subgenre, IReadOnlyCollection<string> existingNames, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistMaker", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Subgenre"] = string.IsNullOrEmpty(subgenre) ? genre : subgenre,
            ["AvoidNames"] = existingNames.Count == 0 ? "(none yet)" : string.Join(", ", existingNames.Take(20)),
        });

        var reply = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        string? name = null, style = null, bio = null;
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
            else if (line.StartsWith("BIO:", StringComparison.OrdinalIgnoreCase))
            {
                bio = line["BIO:".Length..].Trim();
            }
        }

        name = string.IsNullOrWhiteSpace(name) ? $"The {subgenre} Collective" : name;
        style = string.IsNullOrWhiteSpace(style) ? $"{subgenre}, catchy and radio-friendly" : style;
        return (name, style, string.IsNullOrWhiteSpace(bio) ? null : bio);
    }

    /// <summary>Backfills a biography for artists created before bios existed.</summary>
    public async Task<string> WriteArtistBiographyAsync(Artist artist, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistBio", new Dictionary<string, string>
        {
            ["ArtistName"] = artist.Name,
            ["Genre"] = artist.Genre,
            ["Subgenre"] = string.IsNullOrEmpty(artist.Subgenre) ? artist.Genre : artist.Subgenre,
            ["Style"] = artist.StyleDescriptor,
        });

        var bio = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        return string.IsNullOrWhiteSpace(bio)
            ? $"{artist.Name} keep their past a mystery; the {artist.Subgenre} speaks for itself."
            : bio.Trim();
    }

    public async Task<ArtistProfilePlan> DesignArtistAsync(
        string? hint,
        string? genre,
        string? subgenre,
        IReadOnlyCollection<string> existingNames,
        CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistProfilePlanner", new Dictionary<string, string>
        {
            ["Hint"] = string.IsNullOrWhiteSpace(hint) ? "surprise original radio artist" : hint.Trim(),
            ["Genre"] = string.IsNullOrWhiteSpace(genre) ? "(artist may choose)" : genre.Trim(),
            ["Subgenre"] = string.IsNullOrWhiteSpace(subgenre) ? "(artist may choose)" : subgenre.Trim(),
            ["AvoidNames"] = existingNames.Count == 0 ? "(none yet)" : string.Join(", ", existingNames.Take(40)),
        });

        var reply = CleanStructuredOutput(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        return ParseArtistProfile(reply, prompt, hint, genre, subgenre);
    }

    public async Task<ArtistSongPlan> PlanSongAsync(
        Artist artist,
        IReadOnlyCollection<ArtistSongHistoryItem> history,
        IReadOnlyCollection<string> existingTitles,
        string defaultLanguage,
        int minDurationSeconds,
        int maxDurationSeconds,
        bool supportsVocals,
        CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("SongPlanner", new Dictionary<string, string>
        {
            ["ArtistName"] = artist.Name,
            ["Genre"] = artist.Genre,
            ["Subgenre"] = string.IsNullOrEmpty(artist.Subgenre) ? artist.Genre : artist.Subgenre,
            ["ArtistStyle"] = artist.StyleDescriptor,
            ["ArtistBiography"] = FirstNonEmpty(
                artist.DeepBackgroundBiography,
                artist.Biography,
                "(no biography yet)")!,
            ["DefaultLanguage"] = string.IsNullOrWhiteSpace(defaultLanguage) ? "en" : defaultLanguage,
            ["MinDurationSeconds"] = minDurationSeconds.ToString(),
            ["MaxDurationSeconds"] = maxDurationSeconds.ToString(),
            ["VocalCapability"] = supportsVocals ? "Vocals are available." : "Vocals are not available; choose Vocals(\"no\").",
            ["AvoidTitles"] = existingTitles.Count == 0 ? "(none yet)" : string.Join("; ", existingTitles.TakeLast(30)),
            ["ForbiddenWords"] = string.Join(", ", TitleWordGuard.MostFrequentWords(existingTitles, take: 8)),
            ["SongHistory"] = FormatHistory(history),
        });

        var reply = CleanStructuredOutput(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        var plan = ParseSongPlan(reply, artist, defaultLanguage, minDurationSeconds, maxDurationSeconds, supportsVocals);
        if (existingTitles.Any(t => string.Equals(t, plan.Title, StringComparison.OrdinalIgnoreCase)))
        {
            plan = plan with { Title = $"{plan.Title} No. {Random.Shared.Next(2, 99)}" };
        }

        return plan;
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
            ["ForbiddenWords"] = string.Join(", ", forbidden),
            ["AvoidTitles"] = existingTitles.Count == 0 ? "(none yet)" : string.Join("; ", existingTitles.TakeLast(15)),
        });

        var title = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(SystemPrompt, prompt, ct));
        var firstLine = title.Split('\n')[0].Trim();

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

    private static ArtistProfilePlan ParseArtistProfile(
        string reply,
        string generationPrompt,
        string? hint,
        string? suggestedGenre,
        string? suggestedSubgenre)
    {
        var json = ExtractJsonObject(reply);
        if (json is null)
        {
            throw new InvalidOperationException("Artist profile response was not a JSON object.");
        }

        ArtistProfileJson profile;
        try
        {
            profile = JsonSerializer.Deserialize<ArtistProfileJson>(json, JsonOptions)
                ?? throw new InvalidOperationException("Artist profile JSON was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Artist profile response was not valid JSON.", ex);
        }

        var name = RequireField(profile.Name, "name");
        var type = FirstNonEmpty(profile.Type, "Artist")!;
        var genre = FirstNonEmpty(profile.Genre, suggestedGenre, "pop")!;
        var parsedSubgenre = FirstNonEmpty(profile.Subgenre, suggestedSubgenre, genre)!;
        var origin = FirstNonEmpty(profile.Origin, "unknown origin")!;
        var style = FirstNonEmpty(profile.Style, parsedSubgenre, genre)!;
        var shortBiography = RequireField(profile.ShortBiography, "shortBiography");
        var deepBiography = RequireField(profile.DeepBackgroundBiography, "deepBackgroundBiography");
        var promotionText = RequireField(profile.PromotionText, "promotionText");
        var language = FirstNonEmpty(profile.Language, "en")!;
        var formationYear = profile.FormationYear is { } year
            ? Math.Clamp(year, 1950, DateTime.UtcNow.Year)
            : (int?)null;
        var members = (profile.Members ?? [])
            .Select(member => new ArtistMemberPlan(
                RequireField(member.Name, "members[].name"),
                RequireField(member.Role, "members[].role"),
                RequireField(member.Biography, "members[].biography"),
                RequireField(member.VoiceCreationPrompt, "members[].voiceCreationPrompt")))
            .ToList();

        if (members.Count == 0)
        {
            throw new InvalidOperationException("Artist profile JSON must include at least one member.");
        }

        return new ArtistProfilePlan(
            name.Trim().Trim('"'),
            type.Trim(),
            genre.Trim(),
            parsedSubgenre.Trim(),
            origin.Trim(),
            formationYear,
            style.Trim(),
            shortBiography.Trim(),
            deepBiography.Trim(),
            promotionText.Trim(),
            language.Trim(),
            string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
            members,
            generationPrompt);
    }

    private static ArtistSongPlan ParseSongPlan(
        string reply,
        Artist artist,
        string defaultLanguage,
        int minDurationSeconds,
        int maxDurationSeconds,
        bool supportsVocals)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FunctionValueRegex.Matches(reply))
        {
            values[match.Groups["name"].Value] = CleanFunctionValue(match);
        }

        var lyricsMatch = LyricsBlockRegex.Match(reply);
        var lyrics = lyricsMatch.Success ? CleanFunctionValue(lyricsMatch) : null;
        var title = FirstNonEmpty(values.GetValueOrDefault("Title"), $"Untitled {artist.Subgenre} tune")!;
        var style = FirstNonEmpty(values.GetValueOrDefault("Style"), artist.StyleDescriptor, artist.Subgenre, artist.Genre)!;
        var language = FirstNonEmpty(values.GetValueOrDefault("Language"), defaultLanguage, "en")!;
        var story = FirstNonEmpty(
            values.GetValueOrDefault("Story"),
            $"{artist.Name} shaped this track as the next chapter in their {artist.Subgenre} catalog.")!;

        var duration = (minDurationSeconds + maxDurationSeconds) / 2;
        var durationMatch = DurationRegex.Match(reply);
        if (durationMatch.Success && int.TryParse(durationMatch.Groups["value"].Value, out var parsedDuration))
        {
            duration = parsedDuration;
        }

        duration = Math.Clamp(duration, minDurationSeconds, maxDurationSeconds);
        var wantsVocals = supportsVocals && IsAffirmative(values.GetValueOrDefault("Vocals"));
        if (wantsVocals && string.IsNullOrWhiteSpace(lyrics))
        {
            wantsVocals = false;
        }

        return new ArtistSongPlan(
            title.Trim().Trim('"'),
            style.Trim(),
            language.Trim(),
            wantsVocals,
            wantsVocals ? lyrics : null,
            duration,
            story.Trim());
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

    private static string FormatHistory(IReadOnlyCollection<ArtistSongHistoryItem> history)
    {
        if (history.Count == 0)
        {
            return "(no released songs yet)";
        }

        return string.Join(Environment.NewLine, history.TakeLast(12).Select(item =>
        {
            var vocal = item.HasVocals ? "vocal" : "instrumental";
            var duration = item.TargetDurationSeconds ?? (int)Math.Round(item.DurationSeconds);
            var story = string.IsNullOrWhiteSpace(item.SongStory) ? "" : $" Story: {Trim(item.SongStory!, 180)}";
            return $"- \"{item.Title}\" ({vocal}, {item.Language}, target {duration}s, likes {item.UpVotes}, dislikes {item.DownVotes}). Style: {Trim(item.Style, 160)}.{story}";
        }));
    }

    private static bool IsAffirmative(string? value)
        => value?.Trim().ToLowerInvariant() is "yes" or "true" or "vocal" or "vocals" or "with vocals";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string RequireField(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Artist profile JSON missing required field '{fieldName}'.")
            : value.Trim();

    private static string UnescapeFunctionValue(string value)
        => value.Replace("\\\"", "\"").Replace("\\n", "\n").Trim();

    private static string CleanFunctionValue(Match match)
    {
        var group = match.Groups["block"];
        if (!group.Success)
        {
            group = match.Groups["quoted"];
        }

        if (!group.Success)
        {
            group = match.Groups["bare"];
        }

        return UnescapeFunctionValue(group.Success ? group.Value : string.Empty)
            .Trim()
            .Trim('"');
    }

    private static string Trim(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars].TrimEnd() + "...";
}

internal sealed record ArtistProfileJson(
    string? Name,
    string? Type,
    string? Genre,
    string? Subgenre,
    string? Origin,
    int? FormationYear,
    string? Style,
    string? Language,
    string? ShortBiography,
    string? DeepBackgroundBiography,
    string? PromotionText,
    IReadOnlyList<ArtistMemberJson>? Members);

internal sealed record ArtistMemberJson(
    string? Name,
    string? Role,
    string? Biography,
    string? VoiceCreationPrompt);

public sealed record ArtistSongPlan(
    string Title,
    string Style,
    string Language,
    bool WantVocals,
    string? Lyrics,
    int TargetDurationSeconds,
    string Story);

public sealed record ArtistSongHistoryItem(
    string Title,
    string Style,
    string Language,
    bool HasVocals,
    string? SongStory,
    int? TargetDurationSeconds,
    double DurationSeconds,
    int UpVotes,
    int DownVotes);

public sealed record ArtistProfilePlan(
    string Name,
    string Type,
    string Genre,
    string Subgenre,
    string Origin,
    int? FormationYear,
    string Style,
    string ShortBiography,
    string DeepBackgroundBiography,
    string PromotionText,
    string Language,
    string? Hint,
    IReadOnlyList<ArtistMemberPlan> Members,
    string GenerationPrompt);

public sealed record ArtistMemberPlan(
    string Name,
    string Role,
    string Biography,
    string VoiceCreationPrompt);

/// <summary>Finds overused words in existing titles so prompts can forbid them.</summary>
public static class TitleWordGuard
{
    /// <summary>LLM cliches that are always banned from titles.</summary>
    public static readonly IReadOnlyList<string> AlwaysBanned =
        ["ghost", "neon", "echo", "static", "fade", "shadow", "pulse", "void", "dark", "night"];

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "into", "from", "your", "this", "that",
        "der", "die", "das", "und", "ein", "eine", "von", "mit",
    };

    public static IReadOnlyList<string> MostFrequentWords(IEnumerable<string> titles, int take)
    {
        var dynamic = titles
            .SelectMany(t => t.Split([' ', '-', ':', ',', '.'], StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 3 && !Stopwords.Contains(w) && !AlwaysBanned.Contains(w))
            .GroupBy(w => w)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(take)
            .Select(g => g.Key);

        return [.. AlwaysBanned, .. dynamic];
    }
}
