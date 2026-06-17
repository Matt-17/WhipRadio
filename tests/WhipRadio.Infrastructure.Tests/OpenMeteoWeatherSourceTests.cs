using System.Net;
using Microsoft.Extensions.Options;
using WhipRadio.Infrastructure.Weather;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OpenMeteoWeatherSourceTests
{
    private const string SampleJson = """
        {
          "current": { "temperature_2m": 14.3, "weather_code": 61, "wind_speed_10m": 12.0 },
          "daily": {
            "temperature_2m_max": [19.1],
            "temperature_2m_min": [9.4],
            "precipitation_probability_max": [60]
          }
        }
        """;

    private static OpenMeteoWeatherSource CreateSource(FakeHttpMessageHandler handler)
        => new(handler.CreateClient("https://api.open-meteo.com"), Options.Create(new WeatherOptions()));

    [TestMethod]
    public async Task GetSummaryAsync_RequestsConfiguredCoordinatesAndFields()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        await source.GetSummaryAsync("en", CancellationToken.None);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("latitude=51.05", query);
        Assert.Contains("longitude=13.74", query);
        Assert.Contains("current=temperature_2m,weather_code,wind_speed_10m", query);
        Assert.Contains("daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max", query);
        Assert.Contains("timezone=auto", query);
    }

    [TestMethod]
    public async Task GetSummaryAsync_BuildsEnglishSummaryWithWmoMapping()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var summary = await source.GetSummaryAsync("en", CancellationToken.None);

        Assert.Equal("Currently 14.3°C, light rain, wind 12 km/h. Today max 19.1°C, min 9.4°C, rain chance 60%.", summary);
    }

    [TestMethod]
    public async Task GetSummaryAsync_BuildsGermanSummary()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json"));
        var source = CreateSource(handler);

        var summary = await source.GetSummaryAsync("de", CancellationToken.None);

        Assert.Contains("Aktuell 14.3°C", summary);
        Assert.Contains("leichter Regen", summary);
        Assert.Contains("Regenwahrscheinlichkeit 60%", summary);
    }

    [TestMethod]
    [DataRow(0, false, "clear sky")]
    [DataRow(3, true, "bedeckt")]
    [DataRow(95, false, "thunderstorm")]
    [DataRow(424242, false, "mixed weather")]
    public void WmoWeatherCodes_MapsKnownAndUnknownCodes(int code, bool german, string expected)
    {
        Assert.Equal(expected, WmoWeatherCodes.Describe(code, german));
    }
}
