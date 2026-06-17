using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Weather;

namespace WhipRadio.Infrastructure.Weather;

public class WeatherOptions
{
    public const string SectionName = "Weather";

    /// <summary>Default: Dresden.</summary>
    public double Latitude { get; set; } = 51.05;

    public double Longitude { get; set; } = 13.74;
}

/// <summary>Key-free weather facts from Open-Meteo for the ScriptWriter.</summary>
public class OpenMeteoWeatherSource(HttpClient http, IOptions<WeatherOptions> options)
    : IAnnouncementDataSource, IWeatherReportSource
{
    public string Kind => "weather";

    public async Task<string> GetSummaryAsync(string language, CancellationToken ct)
        => (await GetReportAsync(language, ct)).ToFacts();

    public async Task<WeatherReport> GetReportAsync(string language, CancellationToken ct)
    {
        var lat = options.Value.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = options.Value.Longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"/v1/forecast?latitude={lat}&longitude={lon}" +
                  "&current=temperature_2m,weather_code,wind_speed_10m" +
                  "&hourly=temperature_2m,weather_code" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                  "&timezone=auto";

        var forecast = await http.GetFromJsonAsync<ForecastResponse>(url, ct)
                       ?? throw new InvalidOperationException("Empty response from Open-Meteo.");

        return BuildReport(forecast, language);
    }

    internal static string BuildSummary(ForecastResponse forecast, string language)
        => BuildReport(forecast, language).ToFacts();

    internal static WeatherReport BuildReport(ForecastResponse forecast, string language)
    {
        var current = forecast.Current ?? throw new InvalidOperationException("Missing current weather data.");
        var isGerman = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var observedAt = ParseDateTime(current.Time);
        var currentHour = SelectCurrentHour(forecast.Hourly, observedAt);
        var weatherCode = currentHour?.WeatherCode ?? current.WeatherCode;
        var currentTemperature = currentHour?.Temperature ?? current.Temperature;
        var days = BuildDays(forecast.Daily, isGerman);
        var today = days.FirstOrDefault()
            ?? new WeatherDay(
                DateOnly.FromDateTime(observedAt ?? DateTime.UtcNow),
                WmoWeatherCodes.Describe(weatherCode, isGerman),
                null,
                null,
                null);

        return new WeatherReport(
            language,
            observedAt ?? DateTime.UtcNow,
            new WeatherNow(
                observedAt,
                currentTemperature,
                WmoWeatherCodes.Describe(weatherCode, isGerman),
                current.WindSpeed),
            today,
            SelectTonightLow(forecast.Hourly, observedAt) ?? days.Skip(1).FirstOrDefault()?.MinTemperatureC ?? today.MinTemperatureC,
            days.Skip(1).FirstOrDefault(),
            days.Skip(1).Take(3).ToList());
    }

    private static IReadOnlyList<WeatherDay> BuildDays(DailyWeather? daily, bool isGerman)
    {
        if (daily is null)
        {
            return [];
        }

        var count = new[]
        {
            daily.Time.Count,
            daily.WeatherCode.Count,
            daily.TemperatureMax.Count,
            daily.TemperatureMin.Count,
            daily.PrecipitationProbabilityMax.Count,
        }.Max();
        var days = new List<WeatherDay>(count);
        for (var i = 0; i < count; i++)
        {
            var date = i < daily.Time.Count
                && DateOnly.TryParse(daily.Time[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(i));
            var code = i < daily.WeatherCode.Count ? daily.WeatherCode[i] : 424242;
            days.Add(new WeatherDay(
                date,
                WmoWeatherCodes.Describe(code, isGerman),
                i < daily.TemperatureMax.Count ? daily.TemperatureMax[i] : null,
                i < daily.TemperatureMin.Count ? daily.TemperatureMin[i] : null,
                i < daily.PrecipitationProbabilityMax.Count ? daily.PrecipitationProbabilityMax[i] : null));
        }

        return days;
    }

    private static HourWeather? SelectCurrentHour(HourlyWeather? hourly, DateTime? observedAt)
    {
        if (hourly is null || hourly.Time.Count == 0 || hourly.Temperature.Count == 0)
        {
            return null;
        }

        var anchor = observedAt ?? DateTime.UtcNow;
        var target = new DateTime(anchor.Year, anchor.Month, anchor.Day, anchor.Hour, 0, 0, anchor.Kind);
        HourWeather? closest = null;
        var closestDistance = TimeSpan.MaxValue;
        for (var i = 0; i < hourly.Time.Count && i < hourly.Temperature.Count; i++)
        {
            var time = ParseDateTime(hourly.Time[i]);
            if (time is null)
            {
                continue;
            }

            var distance = (time.Value - target).Duration();
            if (distance < closestDistance)
            {
                closest = new HourWeather(
                    time.Value,
                    hourly.Temperature[i],
                    i < hourly.WeatherCode.Count ? hourly.WeatherCode[i] : null);
                closestDistance = distance;
            }
        }

        return closest;
    }

    private static double? SelectTonightLow(HourlyWeather? hourly, DateTime? observedAt)
    {
        if (hourly is null || observedAt is null || hourly.Time.Count == 0 || hourly.Temperature.Count == 0)
        {
            return null;
        }

        var start = observedAt.Value.Hour >= 18
            ? observedAt.Value
            : observedAt.Value.Date.AddHours(18);
        var end = observedAt.Value.Date.AddDays(1).AddHours(6);
        var temperatures = hourly.Time
            .Select((value, index) => new { Time = ParseDateTime(value), Index = index })
            .Where(item => item.Time is not null
                && item.Time.Value >= start
                && item.Time.Value <= end
                && item.Index < hourly.Temperature.Count)
            .Select(item => hourly.Temperature[item.Index])
            .ToList();

        return temperatures.Count == 0 ? null : temperatures.Min();
    }

    private static DateTime? ParseDateTime(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;

    public sealed record ForecastResponse(
        [property: JsonPropertyName("current")] CurrentWeather? Current,
        [property: JsonPropertyName("hourly")] HourlyWeather? Hourly,
        [property: JsonPropertyName("daily")] DailyWeather? Daily);

    public sealed record CurrentWeather(
        [property: JsonPropertyName("time")] string? Time,
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("weather_code")] int WeatherCode,
        [property: JsonPropertyName("wind_speed_10m")] double WindSpeed);

    public sealed record HourlyWeather(
        [property: JsonPropertyName("time")] IReadOnlyList<string> Time,
        [property: JsonPropertyName("temperature_2m")] IReadOnlyList<double> Temperature,
        [property: JsonPropertyName("weather_code")] IReadOnlyList<int> WeatherCode);

    public sealed record DailyWeather(
        [property: JsonPropertyName("time")] IReadOnlyList<string> Time,
        [property: JsonPropertyName("weather_code")] IReadOnlyList<int> WeatherCode,
        [property: JsonPropertyName("temperature_2m_max")] IReadOnlyList<double> TemperatureMax,
        [property: JsonPropertyName("temperature_2m_min")] IReadOnlyList<double> TemperatureMin,
        [property: JsonPropertyName("precipitation_probability_max")] IReadOnlyList<int> PrecipitationProbabilityMax);

    private sealed record HourWeather(DateTime Time, double Temperature, int? WeatherCode);
}
