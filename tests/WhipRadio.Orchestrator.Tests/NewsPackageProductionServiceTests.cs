using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Playout;
using WhipRadio.Orchestrator.Configuration;
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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPreparationPlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPreparationPlan(settings, localNow, BothContributors);

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

        var plan = TopOfHourPackagePlanner.ResolveNextPreparationPlan(settings, localNow, BothContributors);

        Assert.NotNull(plan);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.FromHours(2)),
            plan!.TargetLocal);
        Assert.Equal(2, plan.Segments.Count);
    }

    private static ITopOfHourSegmentContributor LongFormatContributor
        => new NewsLongFormatSegmentContributor(
            null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<NewsLongFormatSegmentContributor>.Instance);

    private static StationSettings LongFormatSettings(string airTimes = "08:00,20:00") => new()
    {
        NewsPackageCadenceMinutes = 60,
        WeatherEnabled = true,
        WeatherCadenceMinutes = 60,
        NewsLongFormatEnabled = true,
        NewsLongFormatAirTimes = airTimes,
        NewsLongFormatDurationMinutes = 30,
    };

    [TestMethod]
    public void BuildPackagePlan_LongFormatAirTime_ReplacesShortNewsAndSetsKind()
    {
        var settings = LongFormatSettings();
        var target = new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.FromHours(2));

        var plan = TopOfHourPackagePlanner.BuildPackagePlan(
            settings, target, [NewsContributor, WeatherContributor, LongFormatContributor]);

        Assert.Equal(NewsPackageKind.LongFormat, plan.Kind);
        Assert.Equal(
            new[] { NewsLongFormatSegmentContributor.SegmentKey, "weather" },
            plan.Segments.Select(s => s.Key).ToArray());
    }

    [TestMethod]
    public void BuildPackagePlan_OrdinaryBoundary_StaysTopOfHour()
    {
        var settings = LongFormatSettings();
        var target = new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.FromHours(2)); // not an air time

        var plan = TopOfHourPackagePlanner.BuildPackagePlan(
            settings, target, [NewsContributor, WeatherContributor, LongFormatContributor]);

        Assert.Equal(NewsPackageKind.TopOfHour, plan.Kind);
        Assert.Equal(new[] { "news", "weather" }, plan.Segments.Select(s => s.Key).ToArray());
    }

    [TestMethod]
    public void ResolvePreparationPlans_SurfacesLongBlockAlongsideNearerShortTarget()
    {
        // 07:10: the 08:00 short boundary is 50 min out (outside the 30-min short window)
        // but 08:00 is a long air time inside its 60-min runway → only the long plan.
        var settings = LongFormatSettings(airTimes: "08:00");
        var localNow = new DateTimeOffset(2026, 7, 4, 7, 10, 0, TimeSpan.FromHours(2));

        var plans = TopOfHourPackagePlanner.ResolvePreparationPlans(
            settings, localNow, [NewsContributor, WeatherContributor, LongFormatContributor]);

        Assert.Equal(1, plans.Count);
        Assert.Equal(NewsPackageKind.LongFormat, plans[0].Kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.FromHours(2)), plans[0].TargetLocal);
    }

    [TestMethod]
    public void ResolvePreparationPlans_ListsShortTargetBeforeLaterLongBlock()
    {
        // 07:40 with 30-min weather: short weather-only 08:00? No — 08:00 is the long air
        // time. Use 20:00 long with a 19:30 weather boundary: at 19:20 both windows are open.
        var settings = LongFormatSettings(airTimes: "20:00");
        settings.WeatherCadenceMinutes = 30;
        var localNow = new DateTimeOffset(2026, 7, 4, 19, 20, 0, TimeSpan.FromHours(2));

        var plans = TopOfHourPackagePlanner.ResolvePreparationPlans(
            settings, localNow, [NewsContributor, WeatherContributor, LongFormatContributor]);

        Assert.Equal(2, plans.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 19, 30, 0, TimeSpan.FromHours(2)), plans[0].TargetLocal);
        Assert.Equal(NewsPackageKind.TopOfHour, plans[0].Kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 20, 0, 0, TimeSpan.FromHours(2)), plans[1].TargetLocal);
        Assert.Equal(NewsPackageKind.LongFormat, plans[1].Kind);
    }

    [TestMethod]
    public void ResolveNextPackagePlan_HonorsOffCadenceAirTimes()
    {
        // 08:15 is never a cadence boundary — NextOwnTarget must carry it anyway.
        var settings = LongFormatSettings(airTimes: "08:15");
        settings.WeatherEnabled = false;
        var localNow = new DateTimeOffset(2026, 7, 4, 7, 50, 0, TimeSpan.FromHours(2));

        var plan = TopOfHourPackagePlanner.ResolveNextPackagePlan(
            settings, localNow, [NewsContributor, LongFormatContributor]);

        // The hourly bulletin still owns 08:00; the long block follows at 08:15.
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 8, 0, 0, TimeSpan.FromHours(2)), plan.TargetLocal);

        var afterEight = TopOfHourPackagePlanner.ResolveNextPackagePlan(
            settings, localNow.AddMinutes(11), [NewsContributor, LongFormatContributor]);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 8, 15, 0, TimeSpan.FromHours(2)), afterEight.TargetLocal);
        Assert.Equal(NewsPackageKind.LongFormat, afterEight.Kind);
    }

    [TestMethod]
    public void SegmentState_LegacySingleBodyJson_StillLoads()
    {
        var legacyJson = """
            [{"Key":"news","Done":true,
              "IntroAnnouncementId":"11111111-1111-1111-1111-111111111111",
              "BodyAnnouncementId":"22222222-2222-2222-2222-222222222222",
              "SegmentHostModeratorId":7,"SourceSummary":"s","SelectedItemIds":[]}]
            """;

        var segments = System.Text.Json.JsonSerializer.Deserialize<List<NewsPackageSegmentState>>(legacyJson)!;

        Assert.Equal(1, segments.Count);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), segments[0].BodyAnnouncementId!.Value);
        Assert.Equal(0, segments[0].BodyAnnouncementIds.Count); // new list defaults empty → legacy path
    }

    [TestMethod]
    public async Task RunCycle_SkipsNewsBoundaryOwnedByPodcastSlot()
    {
        await using var fixture = await WhipRadio.TestSupport.DbFixture.CreateAsync();
        // Wednesday 2026-07-08 07:40 UTC — the next hourly news boundary is 08:00.
        var time = new FixedUtcTimeProvider(new DateTime(2026, 7, 8, 7, 40, 0, DateTimeKind.Utc));

        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                NewsEnabled = true,
                WeatherEnabled = false,
            });
            db.PodcastShows.Add(new PodcastShow
            {
                Id = Guid.NewGuid(),
                Name = "Night Static Weekly",
                Brief = "talk",
                DayOfWeek = 3, // Wednesday
                StartMinute = 8 * 60,
                SlotDurationMinutes = 30,
                IsEnabled = true,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture, time);
        await service.RunCycleForTestsAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            // The 08:00 boundary belongs to the podcast — no news package may claim it.
            Assert.Equal(0, await db.NewsPackages.CountAsync());
        }
    }

    [TestMethod]
    public async Task RunCycle_ProducesNewsBoundaryWhenPodcastSlotIsElsewhere()
    {
        await using var fixture = await WhipRadio.TestSupport.DbFixture.CreateAsync();
        var time = new FixedUtcTimeProvider(new DateTime(2026, 7, 8, 7, 40, 0, DateTimeKind.Utc));

        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                NewsEnabled = true,
                WeatherEnabled = false,
            });
            db.PodcastShows.Add(new PodcastShow
            {
                Id = Guid.NewGuid(),
                Name = "Night Static Weekly",
                Brief = "talk",
                DayOfWeek = 3,
                StartMinute = 21 * 60, // Wednesday 21:00 — not the 08:00 boundary
                SlotDurationMinutes = 30,
                IsEnabled = true,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture, time);
        await service.RunCycleForTestsAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            // The stub contributor cannot actually produce, but the 08:00 boundary
            // must at least have been claimed (a package row exists for it).
            Assert.Equal(1, await db.NewsPackages.CountAsync());
        }
    }

    private static NewsPackageProductionService CreateService(
        WhipRadio.TestSupport.DbFixture fixture, TimeProvider time)
        => new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            fixture,
            new ScheduleService(fixture, time),
            [NewsContributor],
            new TimedPlayoutInterruptService(NullLogger<TimedPlayoutInterruptService>.Instance),
            time,
            new SegmentTestFixtures.NoOpProductionUpdatePublisher(),
            NullStationMetrics.Instance,
            Options.Create(new RadioOptions
            {
                DataRoot = Path.Combine(Path.GetTempPath(), "whipradio-news-suppression-tests"),
            }),
            NullLogger<NewsPackageProductionService>.Instance);

    private sealed class FixedUtcTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
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
