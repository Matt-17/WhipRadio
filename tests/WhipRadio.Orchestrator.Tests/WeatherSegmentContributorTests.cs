using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Core.Prompting;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class WeatherSegmentContributorTests
{
    private const string TempRoot = "/tmp/opencode/weather-contributor-tests";

    [TestInitialize]
    public void Setup()
    {
        Directory.CreateDirectory(TempRoot);
    }

    [TestMethod]
    public async Task ProduceAsync_ProducesIntroAndBodyWhenWeatherSucceeds()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var weatherSource = new FakeWeatherReportSource();
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, weatherSource: weatherSource);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(
            settings, ShowHost(), scopeServices, previousHost: NewsHost());
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        Assert.NotNull(result.Intro);
        Assert.NotNull(result.Body);
        Assert.Null(result.GapLine);
        Assert.Null(result.DegradationReason);
        Assert.Equal(3, result.SegmentHost.Id);
        Assert.Equal("Weather forecast", result.SourceSummary);
    }

    [TestMethod]
    public async Task ProduceAsync_HandoffAlwaysAirsEvenWhenForecastFails()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        // Factory with throwing script writer → body (forecast) fails after retries.
        var throwingFactory = CreateFactoryWithThrowingScriptWriter(db);
        var weatherSource = new FakeWeatherReportSource();
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, throwingFactory, weatherSource: weatherSource);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // Intro (handoff) should still be produced — it falls back to direct text.
        Assert.NotNull(result.Intro);
        // Body should be null (all retries failed).
        Assert.Null(result.Body);
        // Gap line should be produced.
        Assert.NotNull(result.GapLine);
        Assert.NotNull(result.DegradationReason);
        Assert.Contains("failed after retries", result.DegradationReason!);
    }

    [TestMethod]
    public async Task ProduceAsync_IntroFallsBackToDirectTextWhenLlmFails()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        var throwingFactory = CreateFactoryWithThrowingScriptWriter(db);
        var weatherSource = new FakeWeatherReportSource();
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, throwingFactory, weatherSource: weatherSource);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // LLM path throws → falls back to ProduceDirectAsync with "Alex has the weather."
        Assert.NotNull(result.Intro);
        Assert.Contains("Alex has the weather", result.Intro.ScriptText);
    }

    [TestMethod]
    public async Task ProduceAsync_UsesShowModeratorWhenNoPreviousHost()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var weatherSource = new FakeWeatherReportSource();
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, weatherSource: weatherSource);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        // First position, no previous host — weather is the first segment (weather-only :30 package).
        var context = SegmentTestFixtures.CreateContext(
            settings, ShowHost(), scopeServices, position: SegmentPosition.First, previousHost: null);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        Assert.NotNull(result.Intro);
        Assert.NotNull(result.Body);
        Assert.Equal(3, result.SegmentHost.Id);
    }

    [TestMethod]
    public async Task IsIncludedAt_TrueOnCadenceBoundary()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        var settings = SegmentTestFixtures.DefaultSettings();
        // Weather cadence is 30 min, so :00 and :30 are boundaries.
        var target = new DateTimeOffset(2026, 6, 21, 3, 30, 0, TimeSpan.Zero);
        Assert.True(contributor.IsIncludedAt(settings, target));
    }

    [TestMethod]
    public async Task IsIncludedAt_FalseOffCadenceBoundary()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        var settings = SegmentTestFixtures.DefaultSettings();
        // :15 is not a 30-min boundary.
        var target = new DateTimeOffset(2026, 6, 21, 3, 15, 0, TimeSpan.Zero);
        Assert.False(contributor.IsIncludedAt(settings, target));
    }

    [TestMethod]
    public async Task Label_IsWeatherKind()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        Assert.Equal(AnnouncementKind.Weather, contributor.Label.Kind);
        Assert.Equal("WeatherReport", contributor.Label.Purpose);
        Assert.Equal("Weather", contributor.Label.Title);
    }

    [TestMethod]
    public async Task Order_IsAfterNews()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var contributor = new WeatherSegmentContributor(
            db, NullStationMetrics.Instance, NullLogger<WeatherSegmentContributor>.Instance);

        Assert.Equal(20, contributor.Order);
    }

    private static Moderator ShowHost() => new() { Id = 1, Name = "Ava", Language = "en" };
    private static Moderator NewsHost() => new() { Id = 2, Name = "Maya", Language = "en" };

    private static AnnouncementFactory CreateFactoryWithThrowingScriptWriter(
        SegmentTestFixtures.SqliteDbFixture db)
    {
        var radioOptions = Microsoft.Extensions.Options.Options.Create(
            new WhipRadio.Orchestrator.Configuration.RadioOptions { DataRoot = TempRoot });
        var analysisRecorder = new MediaAnalysisRecorder(
            new SegmentTestFixtures.ThrowingAnalysisClient(),
            db,
            NullLogger<MediaAnalysisRecorder>.Instance);

        return new AnnouncementFactory(
            new ThrowingAnnouncementWriter(),
            new StaticPromptContextBuilderForTests(),
            new FakeTtsEngineForTests(),
            analysisRecorder,
            db,
            radioOptions,
            new FixedTimeProviderForTests(),
            NullLogger<AnnouncementFactory>.Instance);
    }

    private sealed class ThrowingAnnouncementWriter : IAnnouncementWriter
    {
        public Task<SpokenAnnouncement> WriteAsync(AnnouncementRequest request, Moderator moderator, CancellationToken ct)
            => throw new InvalidOperationException("Simulated LLM failure.");
    }

    private sealed class StaticPromptContextBuilderForTests : IPromptContextBuilder
    {
        public Task<WhipRadio.Core.Prompting.PromptContext> BuildAsync(
            WhipRadio.Core.Prompting.PromptContextInput input, CancellationToken ct)
            => Task.FromResult(new WhipRadio.Core.Prompting.PromptContext
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

    private sealed class FakeTtsEngineForTests : ITtsEngine
    {
        public Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
            => Task.FromResult(new TtsResult([0x52, 0x49, 0x46, 0x46], 5.0));

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TtsVoice>>([]);
    }

    private sealed class FixedTimeProviderForTests : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 6, 21, 3, 0, 0, TimeSpan.Zero);
    }
}
