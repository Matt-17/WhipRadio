using System.Globalization;

namespace WhipRadio.Core.Weather;

public sealed record WeatherReport(
    string Language,
    DateTime LocalTime,
    WeatherNow Current,
    WeatherDay Today,
    double? TonightLowC,
    WeatherDay? Tomorrow,
    IReadOnlyList<WeatherDay> Outlook)
{
    public string ToFacts()
    {
        var culture = CultureInfo.InvariantCulture;

        var current =
            $"Currently {Current.TemperatureC.ToString("0.#", culture)} C, {Current.Condition}, wind {Current.WindSpeedKmh.ToString("0.#", culture)} km/h.";

        var today =
            $" Today {FormatRange(Today, culture)} with {Today.PrecipitationProbabilityPercent.GetValueOrDefault()}% rain chance.";

        var tonight = TonightLowC is double low
            ? $" Tonight around {low.ToString("0.#", culture)} C."
            : string.Empty;

        var tomorrow = Tomorrow is not null
            ? $" Tomorrow {Tomorrow.Condition}, {FormatRange(Tomorrow, culture)}, rain chance {Tomorrow.PrecipitationProbabilityPercent.GetValueOrDefault()}%."
            : string.Empty;

        var outlook = Outlook.Count > 0
            ? " Three-day outlook: " + string.Join("; ", Outlook.Select(day => $"{day.Date:ddd}: {day.Condition}, {FormatRange(day, culture)}")) + "."
            : string.Empty;

        return string.Concat(current, today, tonight, tomorrow, outlook);
    }

    private static string FormatRange(WeatherDay day, CultureInfo culture)
    {
        var max = day.MaxTemperatureC?.ToString("0.#", culture) ?? "?";
        var min = day.MinTemperatureC?.ToString("0.#", culture) ?? "?";
        return $"{max} C/{min} C";
    }
}

public sealed record WeatherNow(
    DateTime? ObservedAt,
    double TemperatureC,
    string Condition,
    double WindSpeedKmh);

public sealed record WeatherDay(
    DateOnly Date,
    string Condition,
    double? MaxTemperatureC,
    double? MinTemperatureC,
    int? PrecipitationProbabilityPercent);
