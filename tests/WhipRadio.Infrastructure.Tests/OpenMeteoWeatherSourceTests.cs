using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        Assert.Contains("Currently 14.3 C, light rain, wind 12 km/h.", summary);
        Assert.Contains("Tonight around 8.1 C.", summary);
        Assert.Contains("Tomorrow overcast, 18 C/8.1 C, rain chance 20%.", summary);
    }

    [TestMethod]
    public async Task GetSummaryAsync_UsesEnglishSummaryForUnsupportedLanguageCode()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var summary = await source.GetSummaryAsync("es", CancellationToken.None);

        Assert.Contains("Currently 14.3 C", summary);
        Assert.Contains("light rain", summary);
        Assert.Contains("60% rain chance", summary);
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
