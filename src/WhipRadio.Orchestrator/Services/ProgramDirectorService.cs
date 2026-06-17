using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Core.Speech;
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
    ILogger<ProgramDirectorService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(10);

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})\s*\|\s*(?<format>[^|]+)\|\s*(?<genre>[^|]+)\|\s*HOST=(?<host>.+)$")]
    private static partial Regex PlanLineRegex();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the rest of the system a head start (LLM may still be loading).
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);

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

    private async Task<List<PlannedBlock>?> TryLlmDayPlanAsync(int day, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<ITextGenerationService>();

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

            var reply = await llm.CompleteAsync(
                "You are an experienced radio program director. Follow the output format EXACTLY.",
                prompt, ct);
            return ParsePlan(LlmOutputSanitizer.Sanitize(reply));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM day planning failed; using the fallback plan");
            return null;
        }
    }

    internal static List<PlannedBlock>? ParsePlan(string reply)
    {
        var reason = "planned by the program director";
        var blocks = new List<PlannedBlock>();

        foreach (var rawLine in reply.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.StartsWith("REASON:", StringComparison.OrdinalIgnoreCase))
            {
                reason = rawLine["REASON:".Length..].Trim();
                continue;
            }

            var match = PlanLineRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var start = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
            var endHour = int.Parse(match.Groups[3].Value);
            var end = (endHour == 0 ? 24 : endHour) * 60 + int.Parse(match.Groups[4].Value);
            if (end <= start)
            {
                end = Math.Min(start + 120, 24 * 60);
            }

            var duration = Math.Clamp(end - start, 30, 240);
            var genreParts = match.Groups["genre"].Value.Trim().Split('/', 2);

            blocks.Add(new PlannedBlock(
                start, duration,
                match.Groups["format"].Value.Trim(),
                genreParts[0].Trim().ToLowerInvariant(),
                genreParts.Length > 1 ? genreParts[1].Trim().ToLowerInvariant() : string.Empty,
                match.Groups["host"].Value.Trim(),
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
                    Talkativeness = GuessFormatTalkativeness(block),
                    CreatedAt = DateTime.UtcNow,
                };
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
            var prompt = PromptTemplates.Render("HostPersona", new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Gender"] = gender == ModeratorGenders.Male ? "male" : "female",
                ["Language"] = language,
                ["Style"] = style,
            });
            persona = LlmOutputSanitizer.Sanitize(await llm.CompleteAsync(
                "You write radio host personas. Output only the persona.", prompt, ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Persona generation failed for {Name}; using a simple persona", name);
        }

        var styleWords = style.ToLowerInvariant();
        var moderator = new Moderator
        {
            Name = name,
            Gender = gender,
            Language = language,
            Style = style,
            PersonaPrompt = persona,
            TtsEngine = language.StartsWith("de") ? TtsEngines.Piper : TtsEngines.Kokoro,
            SpeechRate = 1.0,
            Talkativeness = styleWords.Contains("energetic") || styleWords.Contains("chatty") || styleWords.Contains("fast")
                ? 0.75
                : styleWords.Contains("calm") || styleWords.Contains("slow") || styleWords.Contains("laid")
                    ? 0.35
                    : 0.5,
            IsActive = true,
            IsAutoGenerated = true,
        };

        var voices = scope.ServiceProvider.GetRequiredService<VoiceCatalogService>();
        moderator.VoiceId = await voices.PickVoiceAsync(moderator, ct);

        db.Moderators.Add(moderator);
        await db.SaveChangesAsync(ct);
        all.Add(moderator);
        logger.LogInformation("Director created host {Name} ({Gender}, {Language}, voice {Voice})",
            name, gender, language, moderator.VoiceId);
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

    private static string NormalizeGenre(string genre)
        => GenreCatalog.Genres.FirstOrDefault(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase))
           ?? GenreCatalog.Subgenres.FirstOrDefault(kv => kv.Value.Contains(genre, StringComparer.OrdinalIgnoreCase)).Key
           ?? "electronic";
}
