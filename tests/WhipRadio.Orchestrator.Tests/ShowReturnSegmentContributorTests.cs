using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ShowReturnSegmentContributorTests
{
    private const string TempRoot = "/tmp/opencode/showreturn-contributor-tests";

    [TestInitialize]
    public void Setup() => Directory.CreateDirectory(TempRoot);

    [TestMethod]
    public void Order_IsAfterNewsAndWeather()
        => Assert.Equal(30, new ShowReturnSegmentContributor().Order);

    [TestMethod]
    public void IsEnabled_WhenNewsOrWeatherEnabled()
    {
        var contributor = new ShowReturnSegmentContributor();
        Assert.True(contributor.IsEnabled(new StationSettings { NewsEnabled = true, WeatherEnabled = false }));
        Assert.True(contributor.IsEnabled(new StationSettings { NewsEnabled = false, WeatherEnabled = true }));
        Assert.False(contributor.IsEnabled(new StationSettings { NewsEnabled = false, WeatherEnabled = false }));
    }

    [TestMethod]
    public void IsIncludedAt_OnlyOnBoundaryWithASpecialist()
    {
        var contributor = new ShowReturnSegmentContributor();
        var settings = new StationSettings { NewsEnabled = true, WeatherEnabled = true, NewsPackageCadenceMinutes = 60 };

        Assert.True(contributor.IsIncludedAt(settings, new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero)));
        Assert.False(contributor.IsIncludedAt(settings, new DateTimeOffset(2026, 6, 21, 3, 30, 0, TimeSpan.Zero)));

        var noSpecialists = new StationSettings { NewsEnabled = false, WeatherEnabled = false, NewsPackageCadenceMinutes = 60 };
        Assert.False(contributor.IsIncludedAt(noSpecialists, new DateTimeOffset(2026, 6, 21, 3, 0, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public async Task ProduceAsync_ShowHostVoicesTheReturn()
    {
        await using var db = await SegmentTestFixtures.CreateDbAsync();
        await SegmentTestFixtures.SeedStationSettingsAsync(db);
        await SegmentTestFixtures.SeedModeratorAsync(db, 1, "Ava");
        await SegmentTestFixtures.SeedModeratorAsync(db, 2, "Maya", isNewsSpecialist: true);
        await SegmentTestFixtures.SeedModeratorAsync(db, 3, "Alex", isWeatherSpecialist: true);

        var factory = SegmentTestFixtures.CreateFactory(db, TempRoot);
        var scopeServices = SegmentTestFixtures.CreateScopeServices(db, factory);

        var settings = SegmentTestFixtures.DefaultSettings();
        var contributor = new ShowReturnSegmentContributor();

        var context = SegmentTestFixtures.CreateContext(
            settings, ShowHost(), scopeServices, position: SegmentPosition.Last, previousHost: WeatherHost());
        var result = await SegmentProductionRunner.RunInlineAsync(contributor, context, CancellationToken.None);

        // The closing line is voiced by the show host, with no body or song intro.
        Assert.NotNull(result.Intro);
        Assert.Equal(1, result.Intro.ModeratorId);
        Assert.Null(result.Body);
        Assert.Null(result.Outro);
        Assert.Equal(1, result.SegmentHost.Id);
    }

    private static Moderator ShowHost() => new() { Id = 1, Name = "Ava", Language = "en" };
    private static Moderator WeatherHost() => new() { Id = 3, Name = "Alex", Language = "en" };
}
