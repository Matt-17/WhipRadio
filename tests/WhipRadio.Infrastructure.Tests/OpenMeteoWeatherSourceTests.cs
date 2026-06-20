using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Weather;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Weather;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OpenMeteoWeatherSourceTests
{
    private const string SampleJson = """
        {
          "current": { "time": "2026-06-17T20:15", "temperature_2m": 29.9, "weather_code": 3, "wind_speed_10m": 12.0 },
          "hourly": {
            "time": ["2026-06-17T19:00", "2026-06-17T20:00", "2026-06-17T21:00", "2026-06-18T02:00"],
            "temperature_2m": [16.0, 14.3, 13.7, 8.1],
            "weather_code": [3, 61, 61, 2]
          },
          "daily": {
            "time": ["2026-06-17", "2026-06-18", "2026-06-19"],
            "weather_code": [61, 3, 0],
            "temperature_2m_max": [31.0, 18.0, 21.0],
            "temperature_2m_min": [9.4, 8.1, 10.0],
            "precipitation_probability_max": [60, 20, 5]
          }
        }
        """;

    private static OpenMeteoWeatherSource CreateSource(FakeHttpMessageHandler handler)
        => new(handler.CreateClient("https://api.open-meteo.com"), Options.Create(new WeatherOptions()));

    [TestMethod]
    public void AddRadioHttpClients_ResolvesWeatherReportSourceWithSettingsCacheAvailable()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<RadioDbContext>, ThrowingDbContextFactory>();
        services.AddRadioHttpClients(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();

        Assert.True(source is OpenMeteoWeatherSource);
    }

    [TestMethod]
    public async Task GetSummaryAsync_RequestsConfiguredCoordinatesAndFields()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        await source.GetSummaryAsync("en", CancellationToken.None);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("latitude=40.7128", query);
        Assert.Contains("longitude=-74.006", query);
        Assert.Contains("current=temperature_2m,weather_code,wind_speed_10m", query);
        Assert.Contains("hourly=temperature_2m,weather_code", query);
        Assert.Contains("daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max", query);
        Assert.Contains("timezone=auto", query);
    }

    [TestMethod]
    public async Task GetSummaryAsync_BuildsEnglishSummaryWithWmoMapping()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var summary = await source.GetSummaryAsync("en", CancellationToken.None);

        Assert.Contains("Location: New York, US.", summary);
        Assert.Contains("Weather airing time: now.", summary);
        Assert.Contains("Current conditions: 14 C, light rain, wind 12 km/h.", summary);
        Assert.Contains("Today temperature range: current temperature 14 C; daily high 31 C appears to be earlier/already reached", summary);
        Assert.Contains("Next hours after airing time: 21:00 14 C, light rain; 02:00 8 C, partly cloudy.", summary);
        Assert.Contains("Tonight low: around 8 C.", summary);
        Assert.Contains("Tomorrow: overcast, 18 C/8 C, rain chance 20%.", summary);
    }

    [TestMethod]
    public async Task GetSummaryAsync_UsesEnglishSummaryForUnsupportedLanguageCode()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var summary = await source.GetSummaryAsync("es", CancellationToken.None);

        Assert.Contains("Current conditions: 14 C", summary);
        Assert.Contains("light rain", summary);
        Assert.Contains("rain chance 60%", summary);
    }

    [TestMethod]
    public async Task GetReportAsync_UsesCurrentHourTemperatureInsteadOfDailyMaximum()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var report = await source.GetReportAsync("en", CancellationToken.None);

        Assert.Equal(14.3, report.Current.TemperatureC, precision: 10);
        Assert.Equal(31.0, report.Today.MaxTemperatureC!.Value, precision: 10);
    }

    [TestMethod]
    public void BuildReport_IncludesLocationAndMarksDailyHighAlreadyReachedWhenEveningHoursAreCooler()
    {
        var forecast = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoWeatherSource.ForecastResponse>(SampleJson)!;

        var report = OpenMeteoWeatherSource.BuildReport(forecast, "en", "Dresden, DE");

        Assert.Equal("Dresden, DE", report.LocationName);
        Assert.Equal(WeatherDailyMaxStatus.AlreadyReached, report.TodayTemperature.DailyMaxStatus);
        Assert.Equal(14.3, report.TodayTemperature.CurrentTemperatureC!.Value, precision: 10);
        Assert.Equal(13.7, report.TodayTemperature.RemainingMaxTemperatureC!.Value, precision: 10);
        Assert.Contains("Location: Dresden, DE.", report.ToFacts());
        Assert.Contains("daily high 31 C appears to be earlier/already reached", report.ToFacts());
    }

    [TestMethod]
    public void BuildReport_DoesNotSayTheHighIsStillAheadWhenFutureHoursMatchTheCurrentTemperature()
    {
        var forecast = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoWeatherSource.ForecastResponse>(
            """
            {
              "current": { "time": "2026-06-17T18:00", "temperature_2m": 29.8, "weather_code": 3, "wind_speed_10m": 8.0 },
              "hourly": {
                "time": ["2026-06-17T18:00", "2026-06-17T19:00", "2026-06-17T20:00"],
                "temperature_2m": [29.8, 29.8, 29.4],
                "weather_code": [3, 3, 2]
              },
              "daily": {
                "time": ["2026-06-17"],
                "weather_code": [3],
                "temperature_2m_max": [29.8],
                "temperature_2m_min": [19.2],
                "precipitation_probability_max": [10]
              }
            }
            """)!;

        var report = OpenMeteoWeatherSource.BuildReport(forecast, "en", "Dresden, DE");

        Assert.Equal(WeatherDailyMaxStatus.AlreadyReached, report.TodayTemperature.DailyMaxStatus);
        Assert.Contains("current temperature 30 C already matches today's high 30 C", report.ToFacts());
        Assert.DoesNotContain("is still ahead", report.ToFacts());
    }

    [TestMethod]
    public void ToFacts_FiltersPastTargetHourAgainstAiringTimeAndRoundsTemperatures()
    {
        var forecast = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoWeatherSource.ForecastResponse>(
            """
            {
              "current": { "time": "2026-06-17T20:55", "temperature_2m": 28.9, "weather_code": 3, "wind_speed_10m": 7.4 },
              "hourly": {
                "time": ["2026-06-17T20:00", "2026-06-17T21:00", "2026-06-17T22:00"],
                "temperature_2m": [28.9, 27.9, 26.4],
                "weather_code": [3, 3, 2]
              },
              "daily": {
                "time": ["2026-06-17"],
                "weather_code": [3],
                "temperature_2m_max": [29.2],
                "temperature_2m_min": [18.7],
                "precipitation_probability_max": [10]
              }
            }
            """)!;

        var report = OpenMeteoWeatherSource.BuildReport(forecast, "en", "Dresden, DE");
        var facts = report.ToFacts(new DateTime(2026, 6, 17, 21, 0, 0));

        Assert.Contains("Weather airing time: 2026-06-17 21:00 local.", facts);
        Assert.Contains("Current conditions: 29 C", facts);
        Assert.Contains("Next hours after airing time: 22:00 26 C", facts);
        Assert.DoesNotContain("21:00 27.9 C", facts);
        Assert.DoesNotContain("21:00 28 C", facts);
    }

    [TestMethod]
    [DataRow(0, "clear sky")]
    [DataRow(3, "overcast")]
    [DataRow(95, "thunderstorm")]
    [DataRow(424242, "mixed weather")]
    public void WmoWeatherCodes_MapsKnownAndUnknownCodes(int code, string expected)
    {
        Assert.Equal(expected, WmoWeatherCodes.Describe(code));
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext()
            => throw new InvalidOperationException("The weather source should not query settings during construction.");
    }
}
