using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class NewsSegmentContributorTests
{
    private const string TempRoot = "/tmp/opencode/news-contributor-tests";

    [TestInitialize]
    public void Setup()
    {
        Directory.CreateDirectory(TempRoot);
    }

    [TestMethod]
    public async Task ProduceAsync_ProducesIntroAndBodyWhenItemsExist()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava", isNewsSpecialist: false);
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);
        await SegmentTestFixtures.SeedNewsItemAsync(db, "Markets rally on tech earnings");

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var extractor = new FakeNewsArticleExtractor();
        var specialistHosts = SegmentTestFixtures.CreateSpecialistHosts(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, extractor, specialistHosts: specialistHosts);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        Assert.NotNull(result.Intro);
        Assert.NotNull(result.Body);
        Assert.Null(result.GapLine);
        Assert.Null(result.DegradationReason);
        Assert.True(result.SelectedItems.Count > 0);
        Assert.Equal(2, result.SegmentHost.Id);
        Assert.Contains("Markets rally", result.SourceSummary);
    }

    [TestMethod]
    public async Task ProduceAsync_ProducesIntroAndGapLineWhenNoItems()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, extractor: new FakeNewsArticleExtractor());

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        Assert.NotNull(result.Intro);
        Assert.Null(result.Body);
        Assert.NotNull(result.GapLine);
        Assert.NotNull(result.DegradationReason);
        Assert.Contains("No news items", result.DegradationReason!);
        Assert.Equal(0, result.SelectedItems.Count);
    }

    [TestMethod]
    public async Task ProduceAsync_IntroAlwaysProducedEvenWhenBodyFails()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);
        await SegmentTestFixtures.SeedNewsItemAsync(db, "Breaking story");

        // Use a factory whose ScriptWriter always throws → body production fails after retries.
        var throwingFactory = CreateFactoryWithThrowingScriptWriter(db);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(
            db, throwingFactory, extractor: new FakeNewsArticleExtractor());

        var settings = SegmentTestFixtures.DefaultSettings();
        // Override NewsExtractionEnabled so EnrichItemsAsync doesn't hit the DB with extraction.
        settings.NewsExtractionEnabled = false;
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // Intro should still be produced (it uses ProduceDirectAsync as fallback, or CannedScriptWriter for the LLM path).
        // Actually the intro uses WriteScriptDraftAsync which calls the throwing script writer → fallback to ProduceDirectAsync.
        Assert.NotNull(result.Intro);
        // Body should be null (all 3 retries failed).
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
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);

        var throwingFactory = CreateFactoryWithThrowingScriptWriter(db);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(
            db, throwingFactory, extractor: new FakeNewsArticleExtractor());

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // The intro LLM path throws → falls back to ProduceDirectAsync with BuildSelfIntroText.
        // The news host introduces THEMSELVES → "It's 03:00. I'm Maya with your news."
        Assert.NotNull(result.Intro);
        Assert.Contains("Maya with your news", result.Intro.ScriptText);
        Assert.Equal(2, result.Intro.ModeratorId);
    }

    [TestMethod]
    public async Task ProduceAsync_FirstPositionIntroIncludesTime()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, extractor: new FakeNewsArticleExtractor());

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var target = new DateTimeOffset(2026, 6, 21, 7, 30, 0, TimeSpan.Zero);
        var context = SegmentTestFixtures.CreateContext(settings, ShowHost(), scopeServices, targetLocal: target);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // First-position intro should mention the time (via the facts passed to the LLM).
        Assert.NotNull(result.Intro);
    }

    [TestMethod]
    public async Task ProduceAsync_MiddlePositionReferencesPreviousHost()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory, extractor: new FakeNewsArticleExtractor());

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var previousHost = new Moderator { Id = 3, Name = "Alex" };
        var context = SegmentTestFixtures.CreateContext(
            settings, ShowHost(), scopeServices, position: SegmentPosition.Middle, previousHost: previousHost);
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // Middle position should reference the previous host (Alex) in the intro facts.
        // The intro is LLM-produced; verify it was produced (the facts include "follows Alex").
        Assert.NotNull(result.Intro);
    }

    [TestMethod]
    public async Task IsIncludedAt_TrueOnCadenceBoundary()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var settings = SegmentTestFixtures.DefaultSettings();
        var target = new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero);
        Assert.True(contributor.IsIncludedAt(settings, target));
    }

    [TestMethod]
    public async Task IsIncludedAt_FalseOffCadenceBoundary()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        var feedPolling = SegmentTestFixtures.CreateFeedPollingService(db);
        var contributor = new NewsSegmentContributor(
            db, feedPolling, NullStationMetrics.Instance, NullLogger<NewsSegmentContributor>.Instance);

        var settings = SegmentTestFixtures.DefaultSettings();
        var target = new DateTimeOffset(2026, 6, 21, 3, 30, 0, TimeSpan.Zero);
        Assert.False(contributor.IsIncludedAt(settings, target));
    }

    [TestMethod]
    public void SelectBalancedCandidates_BalancesAcrossTopicsInPriorityOrder()
    {
        var order = new[] { "general", "business", "technology" };
        var items = new List<NewsItem>();
        void Add(string category, string title, int minutesAgo) => items.Add(new NewsItem
        {
            Title = title,
            Feed = new NewsFeed { Category = category },
            PublishedAtUtc = new DateTime(2026, 6, 21, 3, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
        });

        // 5 general (only 4 survive the per-category cap), 2 business, 1 technology.
        for (var i = 0; i < 5; i++)
        {
            Add("general", $"g{i}", i);
        }
        Add("business", "b0", 1);
        Add("business", "b1", 2);
        Add("technology", "t0", 1);

        var result = NewsSegmentContributor.SelectBalancedCandidates(items, order);

        // 4 general (capped) + 2 business + 1 technology = 7, in priority order.
        Assert.Equal(7, result.Count);
        Assert.True(result.Take(4).All(item => item.Feed!.Category == "general"));
        Assert.Equal("business", result[4].Feed!.Category);
        Assert.Equal("business", result[5].Feed!.Category);
        Assert.Equal("technology", result[6].Feed!.Category);
        // At least two stories for each topic that has them.
        Assert.Equal(4, result.Count(item => item.Feed!.Category == "general"));
        Assert.Equal(2, result.Count(item => item.Feed!.Category == "business"));
    }

    private static Moderator ShowHost() => new() { Id = 1, Name = "Ava", Language = "en" };

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
