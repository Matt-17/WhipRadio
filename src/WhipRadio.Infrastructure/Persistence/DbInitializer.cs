using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Configuration;
using WhipRadio.Core.Entities;
using WhipRadio.Core.News;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Slugs;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Applies migrations and seeds moderators and station settings.
/// The weekly program plan is produced at runtime by the program director.</summary>
public static class DbInitializer
{
    private const string LegacyAllowedTalkPartKinds =
        "SongIntro,SongOutro,Banter,PersonalNote,Joke,ListenerGreeting,RequestDedication,StationId,Weather,HostChange";

    private const string PreJingleAllowedTalkPartKinds =
        "SongIntro,SongOutro,Banter,PersonalNote,Joke,TalkBit,ListenerGreeting,RequestDedication,StationId,Weather,HostChange";

    private const string PreNewsAllowedTalkPartKinds =
        "SongIntro,SongOutro,Banter,PersonalNote,Joke,TalkBit,Jingle,ListenerGreeting,RequestDedication,StationId,Weather,HostChange";

    private const string AccidentalPhase3bSlogan = "Every song made for this moment.";
    private const string PreviousLlamaSlogan = "Llamas whipped that radio's mix.";
    private const int PreviousDefaultMinTrackDurationSeconds = 150;
    private const int PreviousDefaultMaxTrackDurationSeconds = 480;
    private const string LegacyNewsPresenterName = "Maya Current";
    private const string SeedNewsPresenterName = "Maya Vale";
    private const string LocalhostAceStepUrl = ServiceEndpointDefaults.RecordingStudio;
    private const string LoopbackAceStepUrl = ServiceEndpointDefaults.RecordingStudioLoopback;

    public static async Task EnsureSeededAsync(RadioDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await MarkAbandonedStudioHistoryAsync(db, ct);
        await PatchPackageAnnouncementPlayoutIntentAsync(db, ct);

        if (!await db.Moderators.AnyAsync(ct))
        {
            db.Moderators.AddRange(SeedModerators());
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await PatchSeededModeratorsAsync(db, ct);
        }

        if (!await db.StationSettings.AnyAsync(ct))
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                StationName = "WhipRadio",
                StationSlogan = "Llamas whipped the radio's mix.",
                StationVision = "A living AI radio station with original music, distinct hosts, and a coherent on-air identity.",
                StationMission = "Create a continuous local radio experience where music, talk, weather, and listener moments feel intentional.",
                DefaultLanguage = "en",
                TargetQueueLength = 3,
                AnnouncementEveryNTracks = 1,
            });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await PatchSettingsAsync(db, ct);
        }

        await EnsureNewsSeedsAsync(db, ct);

        // Default studios matching start-studios.ps1 — the station produces out
        // of the box; users manage the list on the Studios page.
        if (!await db.Studios.AnyAsync(ct))
        {
            db.Studios.AddRange(
                new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Writer Room #1",
                    Kind = StudioKind.WriterRoom,
                    Url = ServiceEndpointDefaults.WriterRoom,
                    Provider = TextProviders.Ollama,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                },
                new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Studio #1",
                    Kind = StudioKind.Recording,
                    Url = LoopbackAceStepUrl,
                    Provider = MusicBackends.AceStep,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                },
                new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Booth #1",
                    Kind = StudioKind.VoiceBooth,
                    Url = ServiceEndpointDefaults.VoiceBooth,
                    Provider = "local-tts",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await PatchLocalStudioUrlsAsync(db, ct);

            if (!await db.Studios.AnyAsync(s => s.Kind == StudioKind.WriterRoom, ct))
            {
                db.Studios.Add(new Studio
                {
                    Id = Guid.NewGuid(),
                    Name = "Writer Room #1",
                    Kind = StudioKind.WriterRoom,
                    Url = ServiceEndpointDefaults.WriterRoom,
                    Provider = TextProviders.Ollama,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static async Task PatchLocalStudioUrlsAsync(RadioDbContext db, CancellationToken ct)
    {
        await db.Studios
            .Where(s => s.Kind == StudioKind.Recording
                && s.Provider == MusicBackends.AceStep
                && s.Url == LocalhostAceStepUrl)
            .ExecuteUpdateAsync(s => s.SetProperty(studio => studio.Url, LoopbackAceStepUrl), ct);

        // Writer room moved from Ollama's native 11434 to host port 8001 so all
        // studio ports share the 8x01 layout; migrate previously seeded rows.
        await db.Studios
            .Where(s => s.Kind == StudioKind.WriterRoom
                && s.Url == ServiceEndpointDefaults.LegacyWriterRoom)
            .ExecuteUpdateAsync(s => s.SetProperty(studio => studio.Url, ServiceEndpointDefaults.WriterRoom), ct);
    }

    private static async Task MarkAbandonedStudioHistoryAsync(RadioDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        const string abandonedMessage =
            "Marked failed on startup because the Orchestrator stopped before this studio job completed.";

        await db.StudioHistory
            .Where(h => h.Status == StudioHistoryStatus.Running)
            .ExecuteUpdateAsync(h => h
                .SetProperty(x => x.Status, StudioHistoryStatus.Failed)
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.Error, abandonedMessage), ct);
    }

    private static async Task PatchPackageAnnouncementPlayoutIntentAsync(RadioDbContext db, CancellationToken ct)
    {
        var packageAnnouncementIds = await db.NewsPackages.AsNoTracking()
            .Where(package => package.AnnouncementId != null)
            .Select(package => package.AnnouncementId!.Value)
            .ToListAsync(ct);
        if (packageAnnouncementIds.Count == 0)
        {
            return;
        }

        await db.Announcements
            .Where(announcement => packageAnnouncementIds.Contains(announcement.Id)
                && announcement.PlayoutIntent != AnnouncementPlayoutIntent.ScheduledOnly)
            .ExecuteUpdateAsync(s => s.SetProperty(
                announcement => announcement.PlayoutIntent,
                AnnouncementPlayoutIntent.ScheduledOnly), ct);
    }

    /// <summary>
    /// Columns added by later migrations land as 0/false/"" on existing rows —
    /// which would leave the station off air with a zero-sized library. Restore
    /// the intended defaults when those impossible values are detected.
    /// </summary>
    private static async Task PatchSettingsAsync(RadioDbContext db, CancellationToken ct)
    {
        var settings = await db.StationSettings
            .SingleAsync(s => s.Id == StationSettings.SingletonId, ct);
        var defaults = new StationSettings();
        var patched = false;
        if (string.IsNullOrWhiteSpace(settings.DefaultMusicProvider))
        {
            settings.DefaultMusicProvider = MusicBackends.MusicGen;
            patched = true;
        }

        if (string.IsNullOrWhiteSpace(settings.StationSlogan))
        {
            settings.StationSlogan = defaults.StationSlogan;
            settings.StationVision = defaults.StationVision;
            settings.StationMission = defaults.StationMission;
            patched = true;
        }

        if (string.Equals(settings.StationSlogan, AccidentalPhase3bSlogan, StringComparison.Ordinal)
            || string.Equals(settings.StationSlogan, PreviousLlamaSlogan, StringComparison.Ordinal))
        {
            settings.StationSlogan = defaults.StationSlogan;
            patched = true;
        }

        if (settings.MinTrackDurationSeconds == PreviousDefaultMinTrackDurationSeconds
            && settings.MaxTrackDurationSeconds == PreviousDefaultMaxTrackDurationSeconds)
        {
            settings.MinTrackDurationSeconds = defaults.MinTrackDurationSeconds;
            settings.MaxTrackDurationSeconds = defaults.MaxTrackDurationSeconds;
            patched = true;
        }

        if (settings.NewsPackageCadenceMinutes <= 0 || settings.NewsPackageMaxDurationSeconds <= 0)
        {
            settings.NewsEnabled = defaults.NewsEnabled;
            settings.NewsExtractionEnabled = defaults.NewsExtractionEnabled;
            settings.NewsPackageCadenceMinutes = defaults.NewsPackageCadenceMinutes;
            settings.NewsPackageMaxDurationSeconds = defaults.NewsPackageMaxDurationSeconds;
            settings.TopOfHourFadeOutSeconds = defaults.TopOfHourFadeOutSeconds;
            settings.TopOfHourIntroGraceSeconds = defaults.TopOfHourIntroGraceSeconds;
            patched = true;
        }

        if (string.IsNullOrWhiteSpace(settings.NewsCategoryOrder))
        {
            settings.NewsCategoryOrder = NewsCategoryOrdering.ToStorage(NewsCategoryOrdering.DefaultOrder);
            patched = true;
        }

        if (settings.TopOfHourFadeOutSeconds <= 0)
        {
            settings.TopOfHourFadeOutSeconds = defaults.TopOfHourFadeOutSeconds;
            patched = true;
        }

        if (settings.TopOfHourIntroGraceSeconds <= 0)
        {
            settings.TopOfHourIntroGraceSeconds = defaults.TopOfHourIntroGraceSeconds;
            patched = true;
        }

        if (string.IsNullOrWhiteSpace(settings.NewsLongFormatAirTimes))
        {
            settings.NewsLongFormatAirTimes = defaults.NewsLongFormatAirTimes;
            patched = true;
        }

        if (settings.NewsLongFormatDurationMinutes <= 0)
        {
            settings.NewsLongFormatDurationMinutes = defaults.NewsLongFormatDurationMinutes;
            patched = true;
        }

        if (settings.ChatMaxAgentHops <= 0)
        {
            settings.ChatMaxAgentHops = defaults.ChatMaxAgentHops;
            patched = true;
        }

        if (settings.ChatHistoryPromptMessages <= 0)
        {
            settings.ChatHistoryPromptMessages = defaults.ChatHistoryPromptMessages;
            patched = true;
        }

        if (settings.ChatRetainedMessagesPerChannel <= 0)
        {
            settings.ChatRetainedMessagesPerChannel = defaults.ChatRetainedMessagesPerChannel;
            patched = true;
        }

        if (string.IsNullOrWhiteSpace(settings.WeatherLocationName)
            || (settings.WeatherLatitude == 0 && settings.WeatherLongitude == 0))
        {
            settings.WeatherLocationName = defaults.WeatherLocationName;
            settings.WeatherLatitude = defaults.WeatherLatitude;
            settings.WeatherLongitude = defaults.WeatherLongitude;
            patched = true;
        }

        if (settings.MaxLibrarySize > 0)
        {
            await PatchSelectionDiversitySettingsAsync(db, settings, ct);
            if (patched)
            {
                await db.SaveChangesAsync(ct);
            }

            return; // already initialized
        }

        settings.MusicProductionEnabled = defaults.MusicProductionEnabled;
        settings.PlayoutEnabled = defaults.PlayoutEnabled;
        settings.MaxLibrarySize = defaults.MaxLibrarySize;
        settings.MinTrackDurationSeconds = defaults.MinTrackDurationSeconds;
        settings.MaxTrackDurationSeconds = defaults.MaxTrackDurationSeconds;
        settings.FrequencyMhz = defaults.FrequencyMhz;
        settings.FirstDayOfWeek = defaults.FirstDayOfWeek;
        settings.TextProvider = defaults.TextProvider;
        settings.OpenAiModel = defaults.OpenAiModel;
        if (string.IsNullOrWhiteSpace(settings.StationSlogan)
            || string.Equals(settings.StationSlogan, AccidentalPhase3bSlogan, StringComparison.Ordinal)
            || string.Equals(settings.StationSlogan, PreviousLlamaSlogan, StringComparison.Ordinal))
        {
            settings.StationSlogan = defaults.StationSlogan;
        }

        settings.StationVision = defaults.StationVision;
        settings.StationMission = defaults.StationMission;
        settings.GreetingsEnabled = defaults.GreetingsEnabled;
        settings.MaxPendingGreetings = defaults.MaxPendingGreetings;
        settings.NewsEnabled = defaults.NewsEnabled;
        settings.NewsExtractionEnabled = defaults.NewsExtractionEnabled;
        settings.NewsPackageCadenceMinutes = defaults.NewsPackageCadenceMinutes;
        settings.NewsPackageMaxDurationSeconds = defaults.NewsPackageMaxDurationSeconds;
        settings.NewsCategoryOrder = defaults.NewsCategoryOrder;
        settings.TopOfHourFadeOutSeconds = defaults.TopOfHourFadeOutSeconds;
        settings.TopOfHourIntroGraceSeconds = defaults.TopOfHourIntroGraceSeconds;
        settings.WeatherLocationName = defaults.WeatherLocationName;
        settings.WeatherLatitude = defaults.WeatherLatitude;
        settings.WeatherLongitude = defaults.WeatherLongitude;
        settings.DefaultMusicProvider = defaults.DefaultMusicProvider;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One-time backfill: the FormatSelectionRules migration adds the selection-diversity
    /// columns with 0/false defaults (so the migration is non-destructive). A row still in
    /// that pristine post-migration state — every selection knob simultaneously zero/false —
    /// has never been patched, so restore the intended defaults. Once patched the values are
    /// non-zero, so this never runs again and never clobbers a user who later tunes the knobs
    /// or deliberately disables the feature.
    /// </summary>
    private static async Task PatchSelectionDiversitySettingsAsync(
        RadioDbContext db, StationSettings settings, CancellationToken ct)
    {
        var pristine = !settings.SelectionDiversityEnabled
            && settings.RecentExclusionCount == 0
            && settings.DefaultMaxArtistPlaysPerHour == 0
            && settings.DefaultArtistLookbackTracks == 0
            && settings.FatigueFactor == 0.0;
        if (!pristine)
        {
            return;
        }

        var defaults = new StationSettings();
        settings.SelectionDiversityEnabled = defaults.SelectionDiversityEnabled;
        settings.RecentExclusionCount = defaults.RecentExclusionCount;
        settings.DefaultMaxArtistPlaysPerHour = defaults.DefaultMaxArtistPlaysPerHour;
        settings.DefaultArtistLookbackTracks = defaults.DefaultArtistLookbackTracks;
        settings.FatigueFactor = defaults.FatigueFactor;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Databases created before gender/engine existed have every host on the
    /// default female voice. Re-sync the well-known seeded hosts once; legacy
    /// hosts still on a preset voice are migrated to Qwen by HostVoicePreparationService.
    /// </summary>
    private static async Task PatchSeededModeratorsAsync(RadioDbContext db, CancellationToken ct)
    {
        var seeds = SeedModerators().ToDictionary(m => m.Name);
        var patched = false;

        foreach (var moderator in await db.Moderators.ToListAsync(ct))
        {
            // Pre-Phase-2 rows have empty Gender/TtsEngine (migration column default).
            var isStale = string.IsNullOrEmpty(moderator.TtsEngine) || string.IsNullOrEmpty(moderator.Gender);

            if (seeds.TryGetValue(moderator.Name, out var seed) && isStale)
            {
                moderator.Gender = seed.Gender;
                moderator.TtsEngine = seed.TtsEngine;
                moderator.VoiceId = seed.VoiceId;
                patched = true;
            }
            else if (isStale)
            {
                // Unknown legacy host: keep the voice, default the new fields.
                moderator.Gender = string.IsNullOrEmpty(moderator.Gender) ? ModeratorGenders.Female : moderator.Gender;
                moderator.TtsEngine = string.IsNullOrEmpty(moderator.TtsEngine) ? TtsEngines.Qwen : moderator.TtsEngine;
                patched = true;
            }

            if (seeds.TryGetValue(moderator.Name, out seed) && HasNeutralBaselineTraits(moderator))
            {
                moderator.BaselineEnergy = seed.BaselineEnergy;
                moderator.BaselineFormality = seed.BaselineFormality;
                moderator.BaselineHumorLevel = seed.BaselineHumorLevel;
                moderator.BaselineTalkativeness = seed.BaselineTalkativeness;
                moderator.BaselineWarmth = seed.BaselineWarmth;
                patched = true;
            }

            if (seeds.TryGetValue(moderator.Name, out seed)
                && seed.IsNewsSpecialist
                && !moderator.IsNewsSpecialist)
            {
                moderator.IsNewsSpecialist = true;
                patched = true;
            }

            if (seeds.TryGetValue(moderator.Name, out seed)
                && seed.IsWeatherSpecialist
                && !moderator.IsWeatherSpecialist)
            {
                moderator.IsWeatherSpecialist = true;
                patched = true;
            }

            if (ShouldPatchAllowedTalkKinds(moderator))
            {
                moderator.AllowedTalkPartKinds = new Moderator().AllowedTalkPartKinds;
                patched = true;
            }
        }

        if (patched)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool HasNeutralBaselineTraits(Moderator moderator)
        => moderator.BaselineEnergy == Energy.Medium
            && moderator.BaselineFormality == Formality.Balanced
            && moderator.BaselineHumorLevel == HumorLevel.Medium
            && moderator.BaselineTalkativeness == Talkativeness.Medium
            && moderator.BaselineWarmth == Warmth.Medium;

    private static bool ShouldPatchAllowedTalkKinds(Moderator moderator)
        => string.IsNullOrWhiteSpace(moderator.AllowedTalkPartKinds)
            || string.Equals(
                moderator.AllowedTalkPartKinds,
                LegacyAllowedTalkPartKinds,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                moderator.AllowedTalkPartKinds,
                PreJingleAllowedTalkPartKinds,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                moderator.AllowedTalkPartKinds,
                PreNewsAllowedTalkPartKinds,
                StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureNewsSeedsAsync(RadioDbContext db, CancellationToken ct)
    {
        var newsPresenter = await db.Moderators
            .FirstOrDefaultAsync(m => m.Name == SeedNewsPresenterName, ct);
        if (newsPresenter is null)
        {
            newsPresenter = await db.Moderators
                .FirstOrDefaultAsync(m => m.Name == LegacyNewsPresenterName, ct);
            if (newsPresenter is not null)
            {
                newsPresenter.Name = SeedNewsPresenterName;
                newsPresenter.Slug = SlugGenerator.FromName(SeedNewsPresenterName);
                newsPresenter.PersonaPrompt = SeedNewsPresenter().PersonaPrompt;
            }
        }

        if (newsPresenter is null)
        {
            newsPresenter = SeedNewsPresenter();
            db.Moderators.Add(newsPresenter);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            newsPresenter.IsNewsSpecialist = true;
        }

        var settings = await db.StationSettings
            .FirstOrDefaultAsync(s => s.Id == StationSettings.SingletonId, ct);
        if (settings is null)
        {
            return;
        }

        if (!settings.NewsSeedFeedsCreated)
        {
            var seeds = SeedNewsFeeds();
            var existingUrlList = await db.NewsFeeds
                .Select(feed => feed.Url)
                .ToListAsync(ct);
            var existingUrls = existingUrlList.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;

            foreach (var seed in seeds)
            {
                if (existingUrls.Contains(seed.Url))
                {
                    continue;
                }

                seed.CreatedAtUtc = now;
                db.NewsFeeds.Add(seed);
            }

            settings.NewsSeedFeedsCreated = true;
        }

        if (settings.NewsPresenterModeratorId is null && newsPresenter.IsActive)
        {
            settings.NewsPresenterModeratorId = newsPresenter.Id;
        }

        await db.SaveChangesAsync(ct);
    }

    private static Moderator[] SeedModerators()
    {
        var moderators = new[]
        {
            new Moderator
            {
                Name = "Lena Spark",
                Language = "en",
                Gender = ModeratorGenders.Female,
                TtsEngine = TtsEngines.Qwen,
                VoiceId = "",
                VoiceDescription =
                    "A female radio host voice. Style: fast-energetic. Young and bubbly, talks fast with "
                    + "infectious enthusiasm and boundless energy.",
                SpeechRate = 1.15,
                Style = "fast-energetic",
                Talkativeness = 0.8,
                BaselineEnergy = Energy.High,
                BaselineFormality = Formality.Casual,
                BaselineHumorLevel = HumorLevel.High,
                BaselineTalkativeness = Talkativeness.High,
                BaselineWarmth = Warmth.High,
                PersonaPrompt =
                    "You are Lena Spark, a young, bubbly radio host. You talk fast, with infectious " +
                    "enthusiasm and boundless energy. You live for driving beats and celebrate every " +
                    "new discovery like it's the hit of the year.",
                PrefersVocals = true,
                PreferredGenres = "indie rock,electronic",
                IsActive = true,
            },
            new Moderator
            {
                Name = "Herbert Nightwave",
                Language = "en",
                Gender = ModeratorGenders.Male,
                TtsEngine = TtsEngines.Qwen,
                VoiceId = "",
                VoiceDescription =
                    "A male radio host voice. Style: slow-thoughtful. Measured and older, warm timbre, speaks "
                    + "slowly with well-placed pauses and a touch of nostalgia.",
                SpeechRate = 0.85,
                Style = "slow-thoughtful",
                Talkativeness = 0.5,
                BaselineEnergy = Energy.Low,
                BaselineFormality = Formality.Formal,
                BaselineHumorLevel = HumorLevel.Medium,
                BaselineTalkativeness = Talkativeness.Medium,
                BaselineWarmth = Warmth.High,
                PersonaPrompt =
                    "You are Herbert Nightwave, a measured, older radio host with a warm voice. " +
                    "You speak slowly, love a well-placed pause, and sometimes drift into nostalgic " +
                    "thoughts about the golden age of radio.",
                PrefersVocals = false,
                PreferredGenres = "lofi,jazz",
                IsWeatherSpecialist = true,
                IsActive = true,
            },
            new Moderator
            {
                Name = "Charlie Wave",
                Language = "en",
                Gender = ModeratorGenders.Male,
                TtsEngine = TtsEngines.Qwen,
                VoiceId = "",
                VoiceDescription =
                    "A male radio host voice. Style: laid-back. Smooth and casual with a dry sense of humor, "
                    + "like he is broadcasting from a beach bar at sunset.",
                SpeechRate = 1.0,
                Style = "laid-back",
                Talkativeness = 0.35,
                BaselineEnergy = Energy.Low,
                BaselineFormality = Formality.Casual,
                BaselineHumorLevel = HumorLevel.High,
                BaselineTalkativeness = Talkativeness.Low,
                BaselineWarmth = Warmth.Medium,
                PersonaPrompt =
                    "You are Charlie Wave, a laid-back international host with a dry sense of humor. " +
                    "You keep things smooth and casual, drop the occasional pun, and sound like you " +
                    "are broadcasting from a beach bar at sunset.",
                PrefersVocals = null,
                PreferredGenres = "electronic,indie rock",
                IsActive = true,
            },
        };

        foreach (var moderator in moderators)
        {
            moderator.Slug = SlugGenerator.FromName(moderator.Name);
        }

        return moderators;
    }

    private static Moderator SeedNewsPresenter() => new()
    {
        Name = SeedNewsPresenterName,
        Slug = SlugGenerator.FromName(SeedNewsPresenterName),
        Language = "en",
        Gender = ModeratorGenders.Female,
        TtsEngine = TtsEngines.Qwen,
        VoiceId = "",
        VoiceDescription =
            "A female radio host voice. Style: clear-editorial. Clear, professional news-anchor delivery, "
            + "neutral and authoritative.",
        SpeechRate = 1.02,
        Style = "clear-editorial",
        Talkativeness = 0.45,
        BaselineEnergy = Energy.Medium,
        BaselineFormality = Formality.Formal,
        BaselineHumorLevel = HumorLevel.Low,
        BaselineTalkativeness = Talkativeness.Medium,
        BaselineWarmth = Warmth.Medium,
        PersonaPrompt =
            "You are Maya Vale, WhipRadio's news presenter. You sound calm, precise, " +
            "international, and editorially careful. You separate confirmed facts from context " +
            "and never sensationalize.",
            PreferredGenres = "news,technology",
            IsNewsSpecialist = true,
            IsActive = true,
        };

    private static NewsFeed[] SeedNewsFeeds() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            Label = "Ars Technica",
            Url = "https://feeds.arstechnica.com/arstechnica/index",
            Language = "en",
            Region = "us",
            Category = "technology",
            IsSeeded = true,
        },
        new()
        {
            Id = Guid.NewGuid(),
            Label = "The Verge",
            Url = "https://www.theverge.com/rss/index.xml",
            Language = "en",
            Region = "us",
            Category = "technology",
            IsSeeded = true,
        },
        new()
        {
            Id = Guid.NewGuid(),
            Label = "TechCrunch",
            Url = "https://techcrunch.com/feed/",
            Language = "en",
            Region = "us",
            Category = "technology",
            IsSeeded = true,
        },
        new()
        {
            Id = Guid.NewGuid(),
            Label = "NPR Technology",
            Url = "https://feeds.npr.org/1019/rss.xml",
            Language = "en",
            Region = "us",
            Category = "technology",
            IsSeeded = true,
        },
        new()
        {
            Id = Guid.NewGuid(),
            Label = "NPR News",
            Url = "https://feeds.npr.org/1001/rss.xml",
            Language = "en",
            Region = "us",
            Category = "general",
            IsSeeded = true,
        },
        new()
        {
            Id = Guid.NewGuid(),
            Label = "BBC World",
            Url = "https://feeds.bbci.co.uk/news/world/rss.xml",
            Language = "en",
            Region = "global",
            Category = "general",
            IsSeeded = true,
        },
    ];
}
