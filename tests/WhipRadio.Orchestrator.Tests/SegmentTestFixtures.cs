using Microsoft.EntityFrameworkCore;
using WhipRadio.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Weather;
using WhipRadio.Infrastructure.Analysis;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

/// <summary>
/// Shared fakes and helpers for <see cref="ITopOfHourSegmentContributor"/> production tests.
/// Builds a real <see cref="AnnouncementFactory"/> with faked LLM/TTS/analysis deps so
/// contributors exercise their actual production flow (script → voice → TTS → DB) without
/// external services. WAV files are written to a temp directory.
/// </summary>
internal static class SegmentTestFixtures
{
    public static Task<DbFixture> CreateDbAsync() => DbFixture.CreateAsync();

    public static AnnouncementFactory CreateFactory(DbFixture db, string dataRoot)
    {
        var radioOptions = Options.Create(new RadioOptions { DataRoot = dataRoot });
        var analysisRecorder = new MediaAnalysisRecorder(
            new ThrowingAnalysisClient(),
            db,
            NullLogger<MediaAnalysisRecorder>.Instance);
        return new AnnouncementFactory(
            new CannedAnnouncementWriter(),
            new StaticPromptContextBuilder(),
            new FakeTtsEngine(),
            analysisRecorder,
            db,
            radioOptions,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero)),
            NullLogger<AnnouncementFactory>.Instance);
    }

    public static NewsFeedPollingService CreateFeedPollingService(DbFixture db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INewsFeedReader, EmptyNewsFeedReader>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new NewsFeedPollingService(
            scopeFactory,
            db,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero)),
            new NoOpProductionUpdatePublisher(),
            NullLogger<NewsFeedPollingService>.Instance);
    }

    public static SpecialistHostCreationService CreateSpecialistHosts(DbFixture db)
        => new(
            db,
            new ThrowingTextGenerationService(),
            new ThrowingVoiceDesignClient(),
            new NoOpProductionUpdatePublisher(),
            NullLogger<SpecialistHostCreationService>.Instance);

    public static IServiceProvider CreateScopeServices(
        DbFixture db,
        AnnouncementFactory factory,
        INewsArticleExtractor? extractor = null,
        IWeatherReportSource? weatherSource = null,
        SpecialistHostCreationService? specialistHosts = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        if (extractor is not null)
        {
            services.AddSingleton(extractor);
        }
        if (weatherSource is not null)
        {
            services.AddSingleton(weatherSource);
        }
        services.AddSingleton(specialistHosts ?? CreateSpecialistHosts(db));
        return services.BuildServiceProvider();
    }

    public static StationSettings DefaultSettings() => new()
    {
        Id = StationSettings.SingletonId,
        StationName = "WhipRadio",
        NewsEnabled = true,
        NewsPackageCadenceMinutes = 60,
        NewsPackageMaxDurationSeconds = 300,
        NewsExtractionEnabled = false,
        WeatherEnabled = true,
        WeatherCadenceMinutes = 30,
        EnableBreathMarkers = false,
    };

    public static async Task SeedStationSettingsAsync(DbFixture db, StationSettings? settings = null)
    {
        await using var ctx = db.CreateDbContext();
        ctx.StationSettings.Add(settings ?? DefaultSettings());
        await ctx.SaveChangesAsync();
    }

    public static async Task SeedModeratorAsync(
        DbFixture db,
        int id,
        string name,
        bool isNewsSpecialist = false,
        bool isWeatherSpecialist = false)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Moderators.Add(new Moderator
        {
            Id = id,
            Name = name,
            Slug = $"moderator-{id}",
            Language = "en",
            Gender = ModeratorGenders.Female,
            TtsEngine = TtsEngines.Kokoro,
            VoiceId = "af_bella",
            IsActive = true,
            IsNewsSpecialist = isNewsSpecialist,
            IsWeatherSpecialist = isWeatherSpecialist,
        });
        await ctx.SaveChangesAsync();
    }

    public static async Task SeedNewsItemAsync(
        DbFixture db,
        string title = "Markets move",
        NewsItemStatus status = NewsItemStatus.New)
    {
        await using var ctx = db.CreateDbContext();
        var feed = new NewsFeed
        {
            Id = Guid.NewGuid(),
            Label = "Reuters",
            Url = "https://example.com/feed",
            Language = "en",
            Category = "general",
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        ctx.NewsFeeds.Add(feed);
        ctx.NewsItems.Add(new NewsItem
        {
            Id = Guid.NewGuid(),
            FeedId = feed.Id,
            Feed = feed,
            Title = title,
            Url = "https://example.com/story",
            Summary = "Stocks rose.",
            FirstSeenAtUtc = DateTime.UtcNow,
            ContentHash = Guid.NewGuid().ToString("N"),
            Status = status,
        });
        await ctx.SaveChangesAsync();
    }

    public static SegmentProductionContext CreateContext(
        StationSettings settings,
        Moderator showModerator,
        IServiceProvider scopeServices,
        DateTimeOffset? targetLocal = null,
        SegmentPosition position = SegmentPosition.First,
        Moderator? previousHost = null)
    {
        var target = targetLocal ?? new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero);
        return new SegmentProductionContext(
            settings,
            target,
            target.UtcDateTime,
            target.UtcDateTime.AddMinutes(15),
            showModerator,
            position,
            previousHost,
            scopeServices,
            (_, _) => Task.CompletedTask,
            PreviousSegmentHosts: previousHost is null ? null : [previousHost]);
    }

    // --- Fakes ---

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CannedAnnouncementWriter : IAnnouncementWriter
    {
        public Task<SpokenAnnouncement> WriteAsync(AnnouncementRequest request, Moderator moderator, CancellationToken ct)
        {
            var text = $"Canned script for {request.Kind}.";
            return Task.FromResult(new SpokenAnnouncement(text, text, null, null));
        }
    }

    private sealed class StaticPromptContextBuilder : IPromptContextBuilder
    {
        public Task<PromptContext> BuildAsync(PromptContextInput input, CancellationToken ct)
            => Task.FromResult(new PromptContext
            {
                Scope = input.Scope,
                Purpose = input.Purpose ?? string.Empty,
                StationName = "WhipRadio",
                FrequencyMhz = 99.7,
                LocalNow = new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero),
                Language = input.Moderator?.Language ?? "en",
                HostName = input.Moderator?.Name,
            });
    }

    private sealed class FakeTtsEngine : ITtsEngine
    {
        public Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
            => Task.FromResult(new TtsResult([0x52, 0x49, 0x46, 0x46], 5.0));

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TtsVoice>>([]);
    }

    internal sealed class ThrowingAnalysisClient : IAudioAnalysisClient
    {
        public Task<MediaAnalysisDto> AnalyzeAsync(string relativePath, AnalysisMode mode, CancellationToken ct)
            => throw new InvalidOperationException("Analysis client is not available in tests.");

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class ThrowingTextGenerationService : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => throw new InvalidOperationException("LLM should not be called — seed specialists instead.");
    }

    private sealed class ThrowingVoiceDesignClient : IVoiceDesignClient
    {
        public Task<DesignedVoice> DesignVoiceAsync(
            string description, string gender, string language, string? sampleText, CancellationToken ct)
            => throw new InvalidOperationException("Voice design should not be called — seed specialists instead.");

        public Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct)
            => throw new InvalidOperationException("Voice preview should not be called in tests.");
    }

    internal sealed class NoOpProductionUpdatePublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Fake weather report source that returns a canned report — no HTTP calls.
/// </summary>
internal sealed class FakeWeatherReportSource : IWeatherReportSource
{
    private readonly WeatherReport _report;

    public FakeWeatherReportSource()
    {
        _report = new WeatherReport(
            "en",
            "Test City",
            new DateTime(2026, 6, 21, 3, 0, 0),
            new WeatherNow(null, 20.0, "Clear", 10.0),
            new WeatherDay(new DateOnly(2026, 6, 21), "Sunny", 25.0, 15.0, 10),
            new WeatherDayTemperatureContext(20.0, 25.0, 15.0, null, null, WeatherDailyMaxStatus.AlreadyReached),
            12.0,
            new WeatherDay(new DateOnly(2026, 6, 22), "Cloudy", 22.0, 14.0, 30),
            [],
            []);
    }

    public Task<WeatherReport> GetReportAsync(string language, CancellationToken ct)
        => Task.FromResult(_report);
}

/// <summary>
/// Fake news article extractor that returns canned text — no HTTP calls.
/// </summary>
internal sealed class FakeNewsArticleExtractor : INewsArticleExtractor
{
    public Task<string?> ExtractAsync(string url, CancellationToken ct)
        => Task.FromResult<string?>("Extracted article body for testing.");
}

/// <summary>
/// Fake news article extractor that always throws — simulates extraction failure.
/// </summary>
internal sealed class ThrowingNewsArticleExtractor : INewsArticleExtractor
{
    public Task<string?> ExtractAsync(string url, CancellationToken ct)
        => throw new HttpRequestException("Simulated extraction failure.");
}

/// <summary>
/// Fake RSS reader that returns no entries — lets polling complete without HTTP calls.
/// </summary>
internal sealed class EmptyNewsFeedReader : INewsFeedReader
{
    public Task<IReadOnlyList<NewsFeedEntry>> ReadAsync(NewsFeed feed, int maxItems, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NewsFeedEntry>>([]);
}
