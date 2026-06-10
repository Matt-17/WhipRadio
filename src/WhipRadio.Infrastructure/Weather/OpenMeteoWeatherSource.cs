using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Weather;

public class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Default: Dresden.</summary>
    public double Latitude { get; set; } = 51.05;

    public double Longitude { get; set; } = 13.74;
}

/// <summary>Key-free weather facts from Open-Meteo for the ScriptWriter.</summary>
public class OpenMeteoWeatherSource(HttpClient http, IOptions<WeatherOptions> options) : IAnnouncementDataSource
{
    public string Kind => "weather";

    public async Task<string> GetSummaryAsync(string language, CancellationToken ct)
    {
        var lat = options.Value.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = options.Value.Longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"/v1/forecast?latitude={lat}&longitude={lon}" +
                  "&current=temperature_2m,weather_code,wind_speed_10m" +
                  "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                  "&timezone=auto";

        var forecast = await http.GetFromJsonAsync<ForecastResponse>(url, ct)
                       ?? throw new InvalidOperationException("Empty response from Open-Meteo.");

        return BuildSummary(forecast, language);
    }

    internal static string BuildSummary(ForecastResponse forecast, string language)
    {
        var current = forecast.Current ?? throw new InvalidOperationException("Missing current weather data.");
        var daily = forecast.Daily;
        var isGerman = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var condition = WmoWeatherCodes.Describe(current.WeatherCode, isGerman);
        var culture = CultureInfo.InvariantCulture;

        var currentPart = isGerman
            ? $"Aktuell {current.Temperature.ToString("0.#", culture)}°C, {condition}, Wind {current.WindSpeed.ToString("0.#", culture)} km/h."
            : $"Currently {current.Temperature.ToString("0.#", culture)}°C, {condition}, wind {current.WindSpeed.ToString("0.#", culture)} km/h.";

        if (daily is null || daily.TemperatureMax.Count == 0)
        {
            return currentPart;
        }

        var max = daily.TemperatureMax[0].ToString("0.#", culture);
        var min = daily.TemperatureMin.Count > 0 ? daily.TemperatureMin[0].ToString("0.#", culture) : "?";
        var rain = daily.PrecipitationProbabilityMax.Count > 0 ? daily.PrecipitationProbabilityMax[0] : 0;

        var dailyPart = isGerman
            ? $" Heute Höchstwert {max}°C, Tiefstwert {min}°C, Regenwahrscheinlichkeit {rain}%."
            : $" Today max {max}°C, min {min}°C, rain chance {rain}%.";

        return currentPart + dailyPart;
    }

    public sealed record ForecastResponse(
        [property: JsonPropertyName("current")] CurrentWeather? Current,
        [property: JsonPropertyName("daily")] DailyWeather? Daily);

    public sealed record CurrentWeather(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("weather_code")] int WeatherCode,
        [property: JsonPropertyName("wind_speed_10m")] double WindSpeed);

    public sealed record DailyWeather(
        [property: JsonPropertyName("temperature_2m_max")] IReadOnlyList<double> TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] IReadOnlyList<double> TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] IReadOnlyList<int> PrecipitationProbabilityMax);
}
