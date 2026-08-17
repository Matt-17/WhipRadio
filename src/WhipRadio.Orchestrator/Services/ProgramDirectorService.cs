using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Helpers;
using System.Globalization;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Json;
using WhipRadio.Core.Personality;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
using WhipRadio.Core.Slugs;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// The AI program director. Works in the background: fills the weekly program
/// one day per cycle (LLM reasoning with a deterministic fallback so the grid
/// always completes), invents formats and hosts when needed, disables formats
/// that get heavily downvoted, and slowly reassigns spots whose format was
/// turned off — never instantly, an off-switch might be an accident.
/// </summary>
public partial class ProgramDirectorService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    TimeProvider timeProvider,
    DirectorControl control,
    IHubContext<RadioHub> hub,
    ILogger<ProgramDirectorService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the rest of the system a head start (LLM may still be loading).
        await stoppingToken.DelayNoThrow(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyFormatVoteRulesAsync(stoppingToken);
                await ReassignDisabledFormatSlotsAsync(stoppingToken);
                await PlanNextUnplannedDayAsync(stoppingToken);
                control.MarkRun();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Program director cycle failed ({Reason})", ex.GetBaseException().Message);
            }

            try
            {
                await control.WaitForNextCycleAsync(CycleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Heavily downvoted formats get disabled — the director's "rare change".</summary>
    private async Task ApplyFormatVoteRulesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var victims = await db.Formats
            .Where(f => f.IsEnabled && f.DownVotes >= 5 && f.DownVotes > 2 * f.UpVotes)
            .ToListAsync(ct);

        foreach (var format in victims)
        {
            format.IsEnabled = false;
            format.Reason += " [director: disabled after heavy listener downvotes]";
            logger.LogInformation("Director disabled format \"{Name}\" (votes {Up}/{Down})",
                format.Name, format.UpVotes, format.DownVotes);
        }

        if (victims.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await NotifyScheduleChangedAsync(ct);
        }
    }

    /// <summary>Slots pointing at disabled formats get a replacement — eventually (25% per cycle).</summary>
    private async Task ReassignDisabledFormatSlotsAsync(CancellationToken ct)
    {
        if (Random.Shared.NextDouble() > 0.25)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var orphaned = await db.ProgramSlots
            .Include(s => s.Format)
            .Where(s => s.Format != null && !s.Format.IsEnabled)
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.StartMinute)
            .Take(4)
            .ToListAsync(ct);
        if (orphaned.Count == 0)
        {
            return;
        }

        var replacements = await db.Formats
            .Where(f => f.IsEnabled && f.Moderator != null && f.Moderator.IsActive)
            .ToListAsync(ct);

        foreach (var slot in orphaned)
        {
            var sameGenre = replacements.Where(f => f.Genre == slot.Format!.Genre).ToList();
            var replacement = (sameGenre.Count > 0 ? sameGenre : replacements)
                .OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
            slot.FormatId = replacement?.Id; // null ⇒ back to "in planning"
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Director reassigned {Count} slot(s) away from disabled formats", orphaned.Count);
        await NotifyScheduleChangedAsync(ct);
    }

    private async Task PlanNextUnplannedDayAsync(CancellationToken ct)
    {
        int day;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // A day counts as planned when its slots cover ≥ 20 hours.
            var coverage = await db.ProgramSlots
                .GroupBy(s => s.DayOfWeek)
                .Select(g => new { Day = g.Key, Minutes = g.Sum(s => s.DurationMinutes) })
                .ToDictionaryAsync(x => x.Day, x => x.Minutes, ct);

            var today = (int)timeProvider.GetLocalNow().DayOfWeek;
            day = Enumerable.Range(0, 7)
                .Select(offset => (today + offset) % 7)
                .FirstOrDefault(d => coverage.GetValueOrDefault(d) < 20 * 60, -1);
            if (day < 0)
            {
                return; // the whole week is planned
            }
        }

        logger.LogInformation("Director is planning {Day}", (DayOfWeek)day);

        var blocks = await TryLlmDayPlanAsync(day, ct) ?? FallbackDayPlan(day);
        await MaterializeDayAsync(day, blocks, ct);
    }

    internal sealed record PlannedBlock(
        int StartMinute, int DurationMinutes, string FormatName, string Genre, string Subgenre,
        string HostSpec, string Reason);

    /// <summary>Schema-constrained shape of the program director's day plan reply.</summary>
    internal sealed record DayPlanDto(
        [property: JsonRequired] IReadOnlyList<DayPlanBlockDto> Blocks,
        string? Reason = null);

    internal sealed record DayPlanBlockDto(
        [property: JsonRequired] string Start,
        [property: JsonRequired] string End,
        [property: JsonRequired] string Format,
        [property: JsonRequired] string Genre,
        [property: JsonRequired] string Host,
        string? Subgenre = null);

    private async Task<List<PlannedBlock>?> TryLlmDayPlanAsync(int day, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();
            var promptContextBuilder = scope.ServiceProvider.GetRequiredService<IPromptContextBuilder>();

            string stationName;
            List<string> formatNames;
            List<string> hostNames;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                stationName = (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).StationName;
                formatNames = await db.Formats.Where(f => f.IsEnabled)
                    .Select(f => $"{f.Name} ({f.Genre}/{f.Subgenre})").ToListAsync(ct);
                hostNames = await db.Moderators.Where(m => m.IsActive)
                    .Select(m => $"{m.Name} ({m.Gender}, {m.Language}, {m.Style})").ToListAsync(ct);
            }

            var dayOfWeek = (DayOfWeek)day;
            var isWeekend = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var prompt = PromptTemplates.Render("DirectorDayPlan", new Dictionary<string, string>
            {
                ["StationName"] = stationName,
                ["DayName"] = dayOfWeek.ToString(),
                ["DayType"] = isWeekend ? "weekend" : "weekday",
                ["Genres"] = string.Join("; ", GenreCatalog.Subgenres.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}")),
                ["Formats"] = formatNames.Count == 0 ? "(none yet)" : string.Join("; ", formatNames),
                ["Hosts"] = hostNames.Count == 0 ? "(none)" : string.Join("; ", hostNames),
            });
            var promptContext = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.ProgramDirector,
                    Facts: $"Planning day: {dayOfWeek}; day type: {(isWeekend ? "weekend" : "weekday")}",
                    Purpose: "Director day plan"),
                ct);

            var reply = await llm.CompleteAsync(
                new TextGenerationRequest(
                    "You are an experienced radio program director. Return only the requested JSON.\n\n"
                    + promptContext.RenderSituation(),
                    prompt,
                    "Planning station day",
                    StructuredJson.SchemaFor<DayPlanDto>(),
                    "dayPlan"),
                ct);
            return ParsePlan(reply);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM day planning failed; using the fallback plan");
            return null;
        }
    }

    internal static List<PlannedBlock>? ParsePlan(string reply)
    {
        var parsed = StructuredJson.Parse<DayPlanDto>(reply);
        if (!parsed.IsValid || parsed.Value!.Blocks.Count == 0)
        {
            return null;
        }

        var reason = string.IsNullOrWhiteSpace(parsed.Value.Reason)
            ? "planned by the program director"
            : parsed.Value.Reason!.Trim();
        var blocks = new List<PlannedBlock>();

        foreach (var block in parsed.Value.Blocks)
        {
            if (!TryParseClock(block.Start, out var start) || !TryParseClock(block.End, out var end))
            {
                continue;
            }

            // "00:00" as an end means midnight/end-of-day, not the start of the day.
            if (end == 0)
            {
                end = 24 * 60;
            }

            if (end <= start)
            {
                end = Math.Min(start + 120, 24 * 60);
            }

            var duration = Math.Clamp(end - start, 30, 240);
            var genre = (block.Genre ?? string.Empty).Trim().ToLowerInvariant();
            var subgenre = (block.Subgenre ?? string.Empty).Trim().ToLowerInvariant();

            blocks.Add(new PlannedBlock(
                start, duration,
                (block.Format ?? string.Empty).Trim(),
                genre,
                subgenre,
                (block.Host ?? string.Empty).Trim(),
                reason));
        }

        // Drop overlapping/duplicate blocks: keep the earlier one, push starts forward.
        blocks = blocks.OrderBy(b => b.StartMinute).ToList();
        var sanitized = new List<PlannedBlock>();
        var nextFree = 0;
        foreach (var block in blocks)
        {
            var start = Math.Max(block.StartMinute, nextFree);
            if (start >= 24 * 60)
            {
                break;
            }

            var duration = Math.Clamp(Math.Min(block.DurationMinutes, 24 * 60 - start), 30, 240);
            sanitized.Add(block with { StartMinute = start, DurationMinutes = duration, Reason = reason });
            nextFree = start + duration;
        }

        // Require meaningful coverage, otherwise let the fallback take over.
        var coverage = sanitized.Sum(b => b.DurationMinutes);
        return coverage >= 18 * 60 ? sanitized : null;
    }

    /// <summary>Parses an "HH:MM" clock string into minutes-since-midnight.</summary>
    private static bool TryParseClock(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins))
        {
            return false;
        }

        minutes = Math.Clamp(hours, 0, 24) * 60 + Math.Clamp(mins, 0, 59);
        return true;
    }

    private static List<PlannedBlock> FallbackDayPlan(int day)
    {
        var dayOfWeek = (DayOfWeek)day;
        var isFriday = dayOfWeek == DayOfWeek.Friday;
        var isWeekend = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        const string reason = "deterministic baseline plan (director fallback)";

        var blocks = new List<PlannedBlock>
        {
            new(0, 240, "Nachtwelle", "lofi", "ambient lofi", "HOSTPOOL", reason),
            new(240, 120, "Early Static", "lofi", "chillhop", "HOSTPOOL", reason),
            new(360, 240, "Morning Drive", "indie rock", "garage rock", "HOSTPOOL", reason),
            new(600, 240, "Midday Mix", "pop", "synth pop", "HOSTPOOL", reason),
            new(840, 240, isWeekend ? "Weekend Lounge" : "Afternoon Loop", "electronic", "deep house", "HOSTPOOL", reason),
            new(1080, 120, "Sundown Sessions", "jazz", "nu jazz", "HOSTPOOL", reason),
        };

        blocks.Add(isFriday
            ? new PlannedBlock(1200, 240, "Friday Party Night", "electronic", "techno", "HOSTPOOL", reason)
            : new PlannedBlock(1200, 240, "Evening Waves", "electronic", "synthwave", "HOSTPOOL", reason));

        return blocks;
    }

    private async Task MaterializeDayAsync(int day, List<PlannedBlock> blocks, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existingSlots = await db.ProgramSlots.Where(s => s.DayOfWeek == day).ToListAsync(ct);
        db.ProgramSlots.RemoveRange(existingSlots);

        var formats = await db.Formats.ToListAsync(ct);
        var moderators = await db.Moderators.ToListAsync(ct);
        var activeHosts = moderators.Where(m => m.IsActive).ToList();
        _roundRobin = 0;

        foreach (var block in blocks)
        {
            var moderator = await ResolveHostAsync(scope, db, moderators, activeHosts, block.HostSpec, ct);

            var format = formats.FirstOrDefault(f =>
                string.Equals(f.Name, block.FormatName, StringComparison.OrdinalIgnoreCase));
            if (format is null)
            {
                var talkDensity = GuessFormatTalkativeness(block);
                format = new Format
                {
                    Id = Guid.NewGuid(),
                    Name = block.FormatName,
                    Description = $"{block.Subgenre} block hosted by {moderator.Name}",
                    Genre = NormalizeGenre(block.Genre),
                    Subgenre = block.Subgenre,
                    ModeratorId = moderator.Id,
                    Reason = block.Reason,
                    IsEnabled = true,
                    Talkativeness = talkDensity,
                    TalkDensity = talkDensity,
                    TalkDepth = GuessFormatTalkDepth(block),
                    CreatedAt = DateTime.UtcNow,
                };
                await PlanFormatSelectionRulesAsync(scope, db, format, ct);
                db.Formats.Add(format);
                formats.Add(format);
                logger.LogInformation("Director created format \"{Name}\" ({Genre}/{Subgenre})",
                    format.Name, format.Genre, format.Subgenre);
            }
            else if (format.ModeratorId is null)
            {
                format.ModeratorId = moderator.Id;
            }

            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = day,
                StartMinute = block.StartMinute,
                DurationMinutes = block.DurationMinutes,
                FormatId = format.Id,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Director published the plan for {Day} ({Count} blocks)", (DayOfWeek)day, blocks.Count);
        await NotifyScheduleChangedAsync(ct);
    }

    private async Task NotifyScheduleChangedAsync(CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ScheduleChanged", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast schedule update to SignalR clients");
        }
    }

    private int _roundRobin;

    private async Task<Moderator> ResolveHostAsync(
        IServiceScope scope, RadioDbContext db, List<Moderator> all, List<Moderator> active,
        string hostSpec, CancellationToken ct)
    {
        if (hostSpec == "HOSTPOOL" || string.IsNullOrWhiteSpace(hostSpec))
        {
            return active[_roundRobin++ % Math.Max(1, active.Count)];
        }

        if (hostSpec.StartsWith("new:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = hostSpec[4..].Split('|', StringSplitOptions.TrimEntries);
            var name = parts.ElementAtOrDefault(0) ?? "Alex Airwave";
            var existing = all.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            // Hosts always speak the station language — the plan's language hint is ignored.
            var stationLanguage = StationLanguages.Normalize(
                (await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct)).DefaultLanguage);

            return await CreateHostAsync(scope, db, all, name,
                gender: parts.ElementAtOrDefault(1) == "m" ? ModeratorGenders.Male : ModeratorGenders.Female,
                language: stationLanguage,
                style: parts.ElementAtOrDefault(3) ?? "friendly", ct);
        }

        var byName = all.FirstOrDefault(m => string.Equals(m.Name, hostSpec, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => hostSpec.Contains(m.Name, StringComparison.OrdinalIgnoreCase));
        return byName ?? active[_roundRobin++ % Math.Max(1, active.Count)];
    }

    private async Task<Moderator> CreateHostAsync(
        IServiceScope scope, RadioDbContext db, List<Moderator> all,
        string name, string gender, string language, string style, CancellationToken ct)
    {
        var persona = $"You are {name}, a {style} radio host.";
        try
        {
            var llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();
            var promptContextBuilder = scope.ServiceProvider.GetRequiredService<IPromptContextBuilder>();
            var prompt = PromptTemplates.Render("HostPersona", new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Gender"] = gender == ModeratorGenders.Male ? "male" : "female",
                ["Language"] = language,
                ["Style"] = style,
            });
            var promptContext = await promptContextBuilder.BuildAsync(
                new PromptContextInput(
                    PromptScope.ProgramDirector,
                    Facts: $"New host name: {name}; gender: {gender}; language: {language}; style: {style}",
                    Purpose: "Create host persona"),
                ct);
            persona = LlmOutputSanitizer.Sanitize(StructuredJson.ParseTextOrRaw(await llm.CompleteAsync(
                new TextGenerationRequest(
                    "You write radio host personas. " +
                    """Respond with ONLY one JSON object: {"text":"<the persona>"}.""" + "\n\n" +
                    promptContext.RenderSituation(),
                    prompt,
                    "Creating host persona",
                    StructuredJson.SchemaFor<TextDto>(),
                    "text"),
                ct)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Persona generation failed for {Name}; using a simple persona", name);
        }

        var styleWords = style.ToLowerInvariant();
        var talkativeness = styleWords.Contains("energetic") || styleWords.Contains("chatty") || styleWords.Contains("fast")
            ? 0.75
            : styleWords.Contains("calm") || styleWords.Contains("slow") || styleWords.Contains("laid")
                ? 0.35
                : 0.5;
        var baselineTraits = MoodEngine.InferBaseline(style, talkativeness);
        var moderator = new Moderator
        {
            Name = name,
            Slug = SlugGenerator.UniqueFromName(name, all.Select(host => host.Slug)),
            Gender = gender,
            Language = language,
            Style = style,
            PersonaPrompt = persona,
            TtsEngine = TtsEngines.Qwen,
            SpeechRate = 1.0,
            Talkativeness = talkativeness,
            BaselineEnergy = baselineTraits.Energy,
            BaselineFormality = baselineTraits.Formality,
            BaselineHumorLevel = baselineTraits.HumorLevel,
            BaselineTalkativeness = baselineTraits.Talkativeness,
            BaselineWarmth = baselineTraits.Warmth,
            IsActive = true,
            IsAutoGenerated = true,
        };

        // A designed Qwen voice is minted in the background so directing the grid
        // never blocks on the voice booth; the host speaks once its qv- handle lands.
        moderator.VoiceDescription = $"A {(gender == ModeratorGenders.Male ? "male" : "female")} radio host voice. "
            + $"Style: {style}. {persona}";

        db.Moderators.Add(moderator);
        await db.SaveChangesAsync(ct);
        all.Add(moderator);

        scope.ServiceProvider.GetRequiredService<HostVoiceQueue>().Enqueue(moderator.Id);
        logger.LogInformation("Director created host {Name} ({Gender}, {Language}); queued for Qwen voice design",
            name, gender, language);
        return moderator;
    }

    /// <summary>Party blocks let the music run; morning shows chat a lot.</summary>
    private static double GuessFormatTalkativeness(PlannedBlock block)
    {
        var name = block.FormatName.ToLowerInvariant();
        if (name.Contains("party") || name.Contains("night") || name.Contains("club"))
        {
            return 0.2;
        }

        if (name.Contains("morning") || name.Contains("drive") || name.Contains("talk"))
        {
            return 0.75;
        }

        return block.StartMinute < 6 * 60 ? 0.3 : 0.5; // overnight blocks stay quiet
    }

    private static TalkDepth GuessFormatTalkDepth(PlannedBlock block)
    {
        var name = block.FormatName.ToLowerInvariant();
        if (name.Contains("party") || name.Contains("club"))
        {
            return TalkDepth.NameOnly;
        }

        if (name.Contains("morning") || name.Contains("drive") || name.Contains("talk"))
        {
            return TalkDepth.Detailed;
        }

        if (name.Contains("night") || name.Contains("lounge") || name.Contains("session"))
        {
            return TalkDepth.Light;
        }

        return TalkDepth.Light;
    }

    private static string NormalizeGenre(string genre)
        => GenreCatalog.Genres.FirstOrDefault(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase))
           ?? GenreCatalog.Subgenres.FirstOrDefault(kv => kv.Value.Contains(genre, StringComparer.OrdinalIgnoreCase)).Key
           ?? "electronic";

    /// <summary>
    /// Asks the LLM to read the format description and produce structured
    /// <see cref="FormatSelectionRules"/> (so an "artist feature" locks to one
    /// artist, a "theme night" leans on a keyword, etc.). Falls back to the
    /// default StandardRotation rules on any failure — never blocks format creation.
    /// </summary>
    private static async Task PlanFormatSelectionRulesAsync(
        IServiceScope scope, RadioDbContext db, Format format, CancellationToken ct)
    {
        try
        {
            var catalog = await db.Artists.AsNoTracking()
                .Where(a => !a.IsRetired)
                .OrderBy(a => a.Name)
                .Take(40)
                .Select(a => new ArtistCatalogEntry(a.Id, a.Name, a.Genre, a.Subgenre))
                .ToListAsync(ct);

            var llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();
            var planner = new FormatRulesPlanner(llm);
            var rules = await planner.PlanAsync(format, catalog, ct);
            format.SelectionRules = rules;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // SelectionRules already defaults to StandardRotation via field initializer.
        }
    }
}
