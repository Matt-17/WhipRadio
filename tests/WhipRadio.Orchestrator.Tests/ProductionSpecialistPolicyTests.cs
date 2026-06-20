using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ProductionSpecialistPolicyTests
{
    [TestMethod]
    public void ResolveNewsModerator_ReturnsNullWhenNoActiveNewsSpecialistExists()
    {
        var musicHost = Moderator(1, "Music");
        var settings = new StationSettings
        {
            NewsEnabled = true,
        };

        var news = ProductionSpecialistPolicy.ResolveNewsModerator(settings, [musicHost]);

        Assert.Null(news);
    }

    [TestMethod]
    public void ResolveNewsModerator_UsesConfiguredActiveNewsSpecialist()
    {
        var firstNews = Moderator(1, "Aster", news: true);
        var configuredNews = Moderator(2, "Beacon", news: true);
        var settings = new StationSettings
        {
            NewsEnabled = true,
            NewsPresenterModeratorId = configuredNews.Id,
        };

        var news = ProductionSpecialistPolicy.ResolveNewsModerator(settings, [firstNews, configuredNews]);

        Assert.NotNull(news);
        Assert.Equal(configuredNews.Id, news!.Id);
    }

    [TestMethod]
    public void BuildWarning_ReportsAutomaticNewsCreation()
    {
        var settings = new StationSettings
        {
            NewsEnabled = true,
        };

        var warning = ProductionSpecialistPolicy.BuildWarning(settings, []);

        Assert.Contains("program director will create one", warning);
    }

    [TestMethod]
    public void ResolveWeatherModerator_ReturnsNullWhenOnlyNewsPresenterIsWeatherSpecialist()
    {
        var news = Moderator(1, "News", news: true, weather: true);
        var settings = new StationSettings
        {
            WeatherEnabled = true,
            WeatherSpecialistModeratorId = news.Id,
        };

        var weather = ProductionSpecialistPolicy.ResolveWeatherModerator(settings, [news], news);

        Assert.Null(weather);
    }

    [TestMethod]
    public void ResolveWeatherModerator_UsesDistinctConfiguredSpecialist()
    {
        var news = Moderator(1, "News", news: true);
        var weatherHost = Moderator(2, "Weather", weather: true);
        var settings = new StationSettings
        {
            WeatherEnabled = true,
            WeatherSpecialistModeratorId = weatherHost.Id,
        };

        var weather = ProductionSpecialistPolicy.ResolveWeatherModerator(settings, [news, weatherHost], news);

        Assert.NotNull(weather);
        Assert.Equal(weatherHost.Id, weather!.Id);
    }

    [TestMethod]
    public void BuildWarning_ReportsMissingDistinctWeatherSpecialist()
    {
        var news = Moderator(1, "News", news: true, weather: true);
        var settings = new StationSettings
        {
            NewsEnabled = true,
            WeatherEnabled = true,
            NewsPresenterModeratorId = news.Id,
            WeatherSpecialistModeratorId = news.Id,
        };

        var warning = ProductionSpecialistPolicy.BuildWarning(settings, [news]);

        Assert.Contains("program director will create one", warning);
    }

    private static Moderator Moderator(int id, string name, bool news = false, bool weather = false)
        => new()
        {
            Id = id,
            Name = name,
            IsActive = true,
            IsNewsSpecialist = news,
            IsWeatherSpecialist = weather,
        };
}
