using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class NewsPackageProductionServiceTests
{
    private static readonly ITopOfHourSegmentContributor NewsContributor = new StubContributor(
        "news", 10, AnnouncementKind.News, "NewsPackage", "News update",
        settings => settings.NewsEnabled,
        settings => TopOfHourScheduler.NormalizeCadence(settings.NewsPackageCadenceMinutes));

    private static readonly ITopOfHourSegmentContributor WeatherContributor = new StubContributor(
        "weather", 20, AnnouncementKind.Weather, "WeatherReport", "Weather",
        settings => settings.WeatherEnabled,
        settings => WeatherScheduler.NormalizeCadence(settings.WeatherCadenceMinutes));

    private static IReadOnlyList<ITopOfHourSegmentContributor> BothContributors =>
        [NewsContributor, WeatherContributor];

    [TestMethod]
    public void BuildSelfIntroText_UsesCurrentLocalTimeAndNewsHostName()
    {
        var airtime = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);
        var newsHost = new Moderator { Id = 2, Name = "Maya" };

        var intro = NewsSegmentContributor.BuildSelfIntroText(newsHost, airtime);

        Assert.Equal("It's 18:00. I'm Maya with your news.", intro);
    }

    [TestMethod]
    public void BuildSelfIntroText_UsesScheduledAirtimeAcrossMidnight()
    {
        var airtime = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.FromHours(2));
        var newsHost = new Moderator { Id = 2, Name = "Maya" };

        var intro = NewsSegmentContributor.BuildSelfIntroText(newsHost, airtime);

        Assert.Equal("It's 00:00. I'm Maya with your news.", intro);
    }

    [TestMethod]
    public void BuildNewsFacts_PrependsBulletinTime()
    {
        var airtime = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);
        var item = new NewsItem
        {
            Title = "Markets move",
            Url = "https://example.com/story",
            PublishedAtUtc = new DateTime(2026, 6, 20, 16, 0, 0, DateTimeKind.Utc),
            Summary = "Stocks rose.",
            FirstSeenAtUtc = new DateTime(2026, 6, 20, 16, 5, 0, DateTimeKind.Utc),
            ContentHash = "abc123",
        };

        var facts = NewsSegmentContributor.BuildNewsFacts([item], airtime);

        Assert.Contains("Bulletin time: 2026-06-20 18:00 local.", facts);
        Assert.Contains("Title: Markets move", facts);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_PicksWeatherBoundaryWhenItIsSooner()
    {
        // News=60, Weather=30, at 02:15 → next weather boundary is 02:30 (sooner than 03:00 news).
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("weather", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_PicksFullBlockWhenBothTargetsCoincide()
    {
        // News=60, Weather=30, at 02:45 → both target 03:00 → full block.
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 45, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal("news", plan.Segments[0].Key);
        Assert.Equal("weather", plan.Segments[1].Key);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_PicksNewsBoundaryWhenItIsSooner()
    {
        // News=30, Weather=60, at 02:15 → next news boundary is 02:30 (sooner than 03:00 weather).
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 30,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 60,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("news", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_UsesWeatherWhenNewsIsDisabled()
    {
        var settings = new StationSettings
        {
            NewsEnabled = false,
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 21, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("weather", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_UsesNewsOnlyWhenWeatherIsDisabled()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = false,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("news", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_BothSameCadenceAlwaysFullBlock()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 60,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(2, plan.Segments.Count);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_BothHalfHourCadenceFullBlockAtHalfHour()
    {
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 30,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 15, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(2, plan.Segments.Count);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_HandlesNonDivisorCadences()
    {
        // News=45, Weather=30, at 02:20 → next weather is 02:30, next news is 03:00 → 02:30 weather-only.
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 45,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 20, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPackagePlan(settings, localNow, BothContributors);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("weather", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPreparationPlan_ReturnsWeatherPlanWithinWindow()
    {
        // News=60, Weather=30, at 02:21 → 02:30 is 9 min away (within 30-min window) → weather-only plan.
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 21, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPreparationPlan(settings, localNow, BothContributors);

        Assert.NotNull(plan);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 2, 30, 0, TimeSpan.FromHours(2)),
            plan!.TargetLocal);
        Assert.Equal(1, plan.Segments.Count);
        Assert.Equal("weather", plan.Segments[0].Key);
    }

    [TestMethod]
    public void ResolveNextPreparationPlan_WaitsOutsidePrepareWindow()
    {
        // News=60, Weather=60, at 02:20 → 03:00 is 40 min away (outside 30-min window) → null.
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 60,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 20, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPreparationPlan(settings, localNow, BothContributors);

        Assert.Null(plan);
    }

    [TestMethod]
    public void ResolveNextPreparationPlan_ReturnsFullBlockWithinWindow()
    {
        // News=60, Weather=30, at 02:52 → 03:00 is 8 min away → full block plan.
        var settings = new StationSettings
        {
            NewsPackageCadenceMinutes = 60,
            WeatherEnabled = true,
            WeatherCadenceMinutes = 30,
        };
        var localNow = new DateTimeOffset(2026, 6, 21, 2, 52, 0, TimeSpan.FromHours(2));

        var plan = NewsPackageProductionService.ResolveNextPreparationPlan(settings, localNow, BothContributors);

        Assert.NotNull(plan);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan!.TargetLocal);
        Assert.Equal(2, plan.Segments.Count);
    }

    /// <summary>
    /// Stub contributor for planning tests. Only the planning methods are exercised;
    /// ProduceAsync throws so accidental calls are caught.
    /// </summary>
    private sealed class StubContributor(
        string key,
        int order,
        AnnouncementKind kind,
        string purpose,
        string title,
        Func<StationSettings, bool> isEnabled,
        Func<StationSettings, int> cadenceMinutes) : ITopOfHourSegmentContributor
    {
        public string Key => key;
        public int Order => order;
        public SegmentLabel Label => new(kind, purpose, title);
        public bool IsEnabled(StationSettings settings) => isEnabled(settings);
        public int CadenceMinutes(StationSettings settings) => cadenceMinutes(settings);

        public bool IsIncludedAt(StationSettings settings, DateTimeOffset targetLocal)
        {
            var cadence = cadenceMinutes(settings);
            var minuteOfDay = targetLocal.Hour * 60 + targetLocal.Minute;
            return minuteOfDay % cadence == 0;
        }

        public Task<SegmentDraftPlan> PlanDraftsAsync(SegmentProductionContext context, CancellationToken ct)
            => throw new NotImplementedException("Planning tests do not exercise production.");
    }
}
