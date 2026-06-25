using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Speech;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>LLM helper for the music producer: invents artists and plans artist-owned songs.</summary>
public class MusicCopywriter(ITextGenerationService llm)
{
    private const string SystemPrompt =
        "You are a creative assistant for a radio station's music department. " +
        "Answer exactly as instructed, with no extra commentary.";

    public async Task<(string Name, string Style, string? Biography)> InventArtistAsync(
        string genre, string subgenre, IReadOnlyCollection<string> existingNames, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistMaker", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Subgenre"] = string.IsNullOrEmpty(subgenre) ? genre : subgenre,
            ["AvoidNames"] = existingNames.Count == 0 ? "(none yet)" : string.Join(", ", existingNames.Take(20)),
        });

        var parsed = StructuredJson.Parse<InventArtistDto>(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Inventing artist", StructuredJson.SchemaFor<InventArtistDto>(), "inventArtist"),
            ct));

        var name = parsed.IsValid ? parsed.Value!.Name : null;
        var style = parsed.IsValid ? parsed.Value!.Style : null;
        var bio = parsed.IsValid ? parsed.Value!.Bio : null;

        name = string.IsNullOrWhiteSpace(name) ? $"The {subgenre} Collective" : name.Trim();
        style = string.IsNullOrWhiteSpace(style) ? $"{subgenre}, catchy and radio-friendly" : style.Trim();
        return (name, style, string.IsNullOrWhiteSpace(bio) ? null : bio.Trim());
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

        var bio = LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Writing artist biography", StructuredJson.SchemaFor<TextDto>(), "text"),
            ct)));
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

        var reply = await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Creating artist profile", StructuredJson.SchemaFor<ArtistProfileJson>(), "artistProfile"),
            ct);
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
            ["ArtistType"] = FirstNonEmpty(artist.Type, "Artist")!,
            ["ArtistOrigin"] = FirstNonEmpty(artist.Origin, "unknown")!,
            ["ArtistFormationYear"] = artist.FormationYear?.ToString() ?? "unknown",
            ["ArtistLanguage"] = NormalizeSongLanguageCode(FirstNonEmpty(artist.Language, defaultLanguage, "en")),
            ["ArtistCreationHint"] = FirstNonEmpty(artist.CreationHint, "(not recorded)")!,
            ["ArtistPromotionText"] = FirstNonEmpty(artist.PromotionText, "(none)")!,
            ["LeadVocalist"] = FormatLeadVocalist(artist.Members),
            ["DefaultLanguage"] = string.IsNullOrWhiteSpace(defaultLanguage) ? "en" : defaultLanguage,
            ["MinDurationSeconds"] = minDurationSeconds.ToString(),
            ["MaxDurationSeconds"] = maxDurationSeconds.ToString(),
            ["VocalCapability"] = supportsVocals ? "Vocals are available." : "Vocals are not available; set \"vocals\" to false.",
            ["AvoidTitles"] = existingTitles.Count == 0 ? "(none yet)" : string.Join("; ", existingTitles.TakeLast(30)),
            ["ForbiddenWords"] = string.Join(", ", TitleWordGuard.MostFrequentWords(existingTitles, take: 8)),
            ["SongHistory"] = FormatHistory(history),
        });

        var reply = await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Planning artist song", StructuredJson.SchemaFor<SongPlanDto>(), "songPlan"),
            ct);
        var parsed = StructuredJson.Parse<SongPlanDto>(reply);
        var dto = parsed.IsValid ? parsed.Value! : new SongPlanDto(string.Empty);
        var plan = ParseSongPlan(dto, artist, history, defaultLanguage, minDurationSeconds, maxDurationSeconds, supportsVocals);
        if (existingTitles.Any(t => string.Equals(t, plan.Title, StringComparison.OrdinalIgnoreCase)))
        {
            plan = plan with { Title = $"{plan.Title} No. {Random.Shared.Next(2, 99)}" };
        }

        return plan;
    }

    public async Task<ArtistPostPlan> PlanArtistPostAsync(
        Artist artist,
        IReadOnlyCollection<ArtistRecentPostItem> recentPosts,
        ArtistPostKind kind,
        Track? track,
        IReadOnlyCollection<ArtistSongHistoryItem> songHistory,
        CancellationToken ct)
    {
        var templateName = kind switch
        {
            ArtistPostKind.ArtistCreated => "ArtistIntroductionPostPlanner",
            ArtistPostKind.TrackReleased => "ArtistSongPublishPostPlanner",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artist post kind."),
        };

        var prompt = PromptTemplates.Render(templateName, new Dictionary<string, string>
        {
            ["PostKind"] = kind.ToString(),
            ["ArtistName"] = artist.Name,
            ["ArtistType"] = FirstNonEmpty(artist.Type, "Artist")!,
            ["Genre"] = artist.Genre,
            ["Subgenre"] = string.IsNullOrEmpty(artist.Subgenre) ? artist.Genre : artist.Subgenre,
            ["ArtistStyle"] = artist.StyleDescriptor,
            ["ArtistOrigin"] = FirstNonEmpty(artist.Origin, "unknown")!,
            ["ArtistFormationYear"] = artist.FormationYear?.ToString() ?? "unknown",
            ["ArtistLanguage"] = NormalizeSongLanguageCode(artist.Language),
            ["ArtistCreationHint"] = FirstNonEmpty(artist.CreationHint, "(not recorded)")!,
            ["ArtistBiography"] = FirstNonEmpty(artist.Biography, "(no public biography yet)")!,
            ["ArtistDeepBiography"] = FirstNonEmpty(artist.DeepBackgroundBiography, "(no deep biography yet)")!,
            ["ArtistPromotionText"] = FirstNonEmpty(artist.PromotionText, "(none)")!,
            ["ArtistMembers"] = FormatArtistMembers(artist.Members),
            ["RecentPosts"] = FormatRecentPosts(recentPosts),
            ["NewTrack"] = FormatNewTrack(track),
            ["SongHistory"] = FormatHistory(songHistory),
        });

        var reply = await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Planning artist social post", StructuredJson.SchemaFor<ArtistPostDto>(), "artistPost"),
            ct);
        return ParseArtistPostPlan(reply, prompt);
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

        var title = LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Writing song title", StructuredJson.SchemaFor<TextDto>(), "text"),
            ct)));
        var firstLine = title.Split('\n')[0].Trim();

        if (existingTitles.Any(t => string.Equals(t, firstLine, StringComparison.OrdinalIgnoreCase)))
        {
            firstLine = $"{firstLine} No. {Random.Shared.Next(2, 99)}";
        }

        return string.IsNullOrWhiteSpace(firstLine) ? $"Untitled {artist.Subgenre} tune" : firstLine;
    }

    /// <summary>
    /// A short, natural first-person self-introduction spoken as the member's hidden
    /// voice reference sample — a personal description with one highlight, never a
    /// station promo. Returns null when the model gives nothing usable.
    /// </summary>
    public async Task<string?> WriteMemberSelfIntroAsync(ArtistMember member, string language, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("ArtistMemberSelfIntro", new Dictionary<string, string>
        {
            ["MemberName"] = member.Name,
            ["Role"] = FirstNonEmpty(member.Role, "performer")!,
            ["ArtistName"] = FirstNonEmpty(member.Artist?.Name, "their band")!,
            ["Biography"] = FirstNonEmpty(member.Biography, "(no biography on record)")!,
            ["Language"] = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim(),
        });

        var reply = LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Writing member self-introduction", StructuredJson.SchemaFor<TextDto>(), "text"),
            ct)));
        var intro = string.Join(" ", reply
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(intro) ? null : intro.Trim();
    }

    public async Task<string> WriteLyricsAsync(string genre, string language, CancellationToken ct)
    {
        var prompt = PromptTemplates.Render("LyricsWriter", new Dictionary<string, string>
        {
            ["Genre"] = genre,
            ["Language"] = language,
        });
        return LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
            new TextGenerationRequest(SystemPrompt, prompt, "Writing lyrics", StructuredJson.SchemaFor<TextDto>(), "text"),
            ct)));
    }

    private static ArtistProfilePlan ParseArtistProfile(
        string reply,
        string generationPrompt,
        string? hint,
        string? suggestedGenre,
        string? suggestedSubgenre)
    {
        var parsed = StructuredJson.Parse<ArtistProfileJson>(reply);
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException($"Artist profile response was not valid JSON: {parsed.Error}");
        }

        var profile = parsed.Value!;
        var name = RequireField(profile.Name, "name");
        var type = FirstNonEmpty(profile.Type, "Artist")!;
        var genre = FirstNonEmpty(profile.Genre, suggestedGenre, "pop")!;
        var parsedSubgenre = FirstNonEmpty(profile.Subgenre, suggestedSubgenre, genre)!;
        var origin = FirstNonEmpty(profile.Origin, "unknown origin")!;
        var style = FirstNonEmpty(profile.Style, parsedSubgenre, genre)!;
        var shortBiography = RequireField(profile.ShortBiography, "shortBiography");
        var deepBiography = RequireField(profile.DeepBackgroundBiography, "deepBackgroundBiography");
        var promotionText = RequireField(profile.PromotionText, "promotionText");
        var language = NormalizeSongLanguageCode(FirstNonEmpty(profile.Language, "en"));
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
            language,
            string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
            members,
            generationPrompt);
    }

    private static ArtistSongPlan ParseSongPlan(
        SongPlanDto dto,
        Artist artist,
        IReadOnlyCollection<ArtistSongHistoryItem> history,
        string defaultLanguage,
        int minDurationSeconds,
        int maxDurationSeconds,
        bool supportsVocals)
    {
        var lyrics = string.IsNullOrWhiteSpace(dto.Lyrics) ? null : dto.Lyrics;
        var title = FirstNonEmpty(dto.Title, $"Untitled {artist.Subgenre} tune")!;
        var style = FirstNonEmpty(dto.Style, artist.StyleDescriptor, artist.Subgenre, artist.Genre)!;
        var (language, languageWasCorrected) = ResolveSongLanguage(dto.Language, defaultLanguage, artist, history);
        var story = FirstNonEmpty(
            dto.Story,
            $"{artist.Name} shaped this track as the next chapter in their {artist.Subgenre} catalog.")!;

        var duration = Math.Clamp(
            dto.DurationSeconds ?? (minDurationSeconds + maxDurationSeconds) / 2,
            minDurationSeconds,
            maxDurationSeconds);

        var wantsVocals = supportsVocals && dto.Vocals;
        if (wantsVocals && string.IsNullOrWhiteSpace(lyrics))
        {
            wantsVocals = false;
        }
        if (wantsVocals && languageWasCorrected)
        {
            wantsVocals = false;
        }

        return new ArtistSongPlan(
            title.Trim().Trim('"'),
            style.Trim(),
            language,
            wantsVocals,
            wantsVocals ? lyrics : null,
            duration,
            story.Trim());
    }

    private static ArtistPostPlan ParseArtistPostPlan(string reply, string prompt)
    {
        var parsed = StructuredJson.Parse<ArtistPostDto>(reply);
        if (!parsed.IsValid)
        {
            throw new InvalidOperationException($"Artist post response was not valid JSON: {parsed.Error}");
        }

        var dto = parsed.Value!;
        return new ArtistPostPlan(dto.ShouldPost, (dto.Text ?? string.Empty).Trim(), prompt);
    }

    private static (string Language, bool WasCorrected) ResolveSongLanguage(
        string? requestedLanguage,
        string defaultLanguage,
        Artist artist,
        IReadOnlyCollection<ArtistSongHistoryItem> history)
    {
        var fallback = NormalizeSongLanguageCode(FirstNonEmpty(artist.Language, defaultLanguage, "en"));
        if (string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return (fallback, false);
        }

        var requested = NormalizeSongLanguageCode(requestedLanguage);
        if (requested == fallback)
        {
            return (fallback, IsNonDefaultLanguageRequest(requestedLanguage, fallback));
        }

        return HasExplicitLanguageEvidence(requested, artist, history)
            ? (requested, false)
            : (fallback, true);
    }

    private static string NormalizeSongLanguageCode(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return "en";
        }

        if (value.StartsWith("en", StringComparison.Ordinal)
            || value.Contains("english", StringComparison.Ordinal))
        {
            return "en";
        }

        return value.Length >= 2 && IsAsciiLetter(value[0]) && IsAsciiLetter(value[1])
            ? value[..2]
            : "en";
    }

    private static bool IsNonDefaultLanguageRequest(string requestedLanguage, string fallback)
    {
        var requested = requestedLanguage.Trim();
        if (requested.Length == 0)
        {
            return false;
        }

        return NormalizeSongLanguageCode(requested) != fallback
            || !IsDefaultLanguageName(requested, fallback);
    }

    private static bool IsDefaultLanguageName(string requestedLanguage, string fallback)
    {
        var value = requestedLanguage.Trim().ToLowerInvariant();
        return fallback == "en"
            ? value.StartsWith("en", StringComparison.Ordinal) || value.Contains("english", StringComparison.Ordinal)
            : value.StartsWith(fallback, StringComparison.Ordinal);
    }

    private static bool HasExplicitLanguageEvidence(
        string language,
        Artist artist,
        IReadOnlyCollection<ArtistSongHistoryItem> history)
    {
        if (history.Any(item => NormalizeSongLanguageCode(item.Language) == language))
        {
            return true;
        }

        return false;
    }

    private static bool IsAsciiLetter(char value)
        => value is >= 'a' and <= 'z';

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

    private static string FormatRecentPosts(IReadOnlyCollection<ArtistRecentPostItem> posts)
    {
        if (posts.Count == 0)
        {
            return "(no prior posts)";
        }

        return string.Join(Environment.NewLine, posts.Take(8).Select(post =>
        {
            var track = string.IsNullOrWhiteSpace(post.TrackTitle) ? "" : $" Track: {post.TrackTitle}.";
            return $"- {post.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC [{post.Kind}].{track} {Trim(post.Body, 220)}";
        }));
    }

    private static string FormatNewTrack(Track? track)
    {
        if (track is null)
        {
            return "(no new track; this is an artist-created post)";
        }

        var vocals = track.HasVocals
            ? $"vocal track with {(string.IsNullOrWhiteSpace(track.Lyrics) ? "no stored lyrics" : "stored lyrics")}"
            : "instrumental track";
        var target = track.TargetDurationSeconds?.ToString() ?? "unknown";
        return $"""
            Title: {track.Title}
            Style: {track.Style}
            Language: {track.Language}
            Vocals: {vocals}
            Song story: {FirstNonEmpty(track.SongStory, "(none)")!}
            Target duration: {target}s
            Actual duration: {Math.Round(track.DurationSeconds)}s
            Backend: {track.Backend}
            Generation context: {Trim(track.GenerationPrompt, 1200)}
            """;
    }

    /// <summary>
    /// The song planner only needs to know who is singing and how they sound —
    /// not every member's biography — so the model isn't distracted from the
    /// band's style and the lead singer's voice.
    /// </summary>
    private static string FormatLeadVocalist(IEnumerable<ArtistMember> members)
    {
        var lead = ArtistMemberRoster.SelectLeadVocalist(members);
        if (lead is null)
        {
            return "(no lead singer recorded)";
        }

        var voice = string.IsNullOrWhiteSpace(lead.VoiceCreationPrompt)
            ? "no voice description recorded"
            : Trim(lead.VoiceCreationPrompt, 280);
        return $"{lead.Name} ({lead.Role}): {voice}";
    }

    private static string FormatArtistMembers(IEnumerable<ArtistMember> members)
    {
        var lines = members
            .OrderBy(member => member.SortOrder)
            .Select(member =>
            {
                var voice = string.IsNullOrWhiteSpace(member.VoiceCreationPrompt)
                    ? "no voice prompt recorded"
                    : Trim(member.VoiceCreationPrompt, 220);
                var bio = string.IsNullOrWhiteSpace(member.Biography)
                    ? "no member biography recorded"
                    : Trim(member.Biography, 220);
                return $"- {member.Name}: {member.Role}. Bio: {bio}. Voice prompt: {voice}.";
            })
            .ToList();

        return lines.Count == 0 ? "(no member roster recorded)" : string.Join(Environment.NewLine, lines);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string RequireField(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Artist profile JSON missing required field '{fieldName}'.")
            : value.Trim();

    private static string Trim(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars].TrimEnd() + "...";
}

internal sealed record InventArtistDto(
    [property: JsonRequired] string Name,
    string? Style = null,
    string? Bio = null);

internal sealed record SongPlanDto(
    [property: JsonRequired] string Title,
    string? Style = null,
    string? Language = null,
    bool Vocals = false,
    string? Lyrics = null,
    int? DurationSeconds = null,
    string? Story = null);

internal sealed record ArtistPostDto(
    [property: JsonRequired] bool ShouldPost,
    string? Text = null);

internal sealed record ArtistProfileJson(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string ShortBiography,
    [property: JsonRequired] string DeepBackgroundBiography,
    [property: JsonRequired] string PromotionText,
    [property: JsonRequired] IReadOnlyList<ArtistMemberJson> Members,
    string? Type = null,
    string? Genre = null,
    string? Subgenre = null,
    string? Origin = null,
    int? FormationYear = null,
    string? Style = null,
    string? Language = null);

internal sealed record ArtistMemberJson(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Biography,
    [property: JsonRequired] string VoiceCreationPrompt);

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

public sealed record ArtistRecentPostItem(
    string Kind,
    string Body,
    DateTime CreatedAtUtc,
    string? TrackTitle);

public sealed record ArtistPostPlan(
    bool ShouldPost,
    string Text,
    string GenerationPrompt);

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
