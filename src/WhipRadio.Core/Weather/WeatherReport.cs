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
        var isGerman = Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.InvariantCulture;

        var current = isGerman
            ? $"Aktuell {Current.TemperatureC.ToString("0.#", culture)} C, {Current.Condition}, Wind {Current.WindSpeedKmh.ToString("0.#", culture)} km/h."
            : $"Currently {Current.TemperatureC.ToString("0.#", culture)} C, {Current.Condition}, wind {Current.WindSpeedKmh.ToString("0.#", culture)} km/h.";

        var today = isGerman
            ? $" Heute {FormatRange(Today, culture)} mit {Today.PrecipitationProbabilityPercent.GetValueOrDefault()}% Regenwahrscheinlichkeit."
            : $" Today {FormatRange(Today, culture)} with {Today.PrecipitationProbabilityPercent.GetValueOrDefault()}% rain chance.";

        var tonight = TonightLowC is double low
            ? isGerman
                ? $" Heute Nacht etwa {low.ToString("0.#", culture)} C."
                : $" Tonight around {low.ToString("0.#", culture)} C."
            : string.Empty;

        var tomorrow = Tomorrow is not null
            ? isGerman
                ? $" Morgen {Tomorrow.Condition}, {FormatRange(Tomorrow, culture)}, Regenwahrscheinlichkeit {Tomorrow.PrecipitationProbabilityPercent.GetValueOrDefault()}%."
                : $" Tomorrow {Tomorrow.Condition}, {FormatRange(Tomorrow, culture)}, rain chance {Tomorrow.PrecipitationProbabilityPercent.GetValueOrDefault()}%."
            : string.Empty;

        var outlook = Outlook.Count > 0
            ? isGerman
                ? " Drei-Tage-Ausblick: " + string.Join("; ", Outlook.Select(day => $"{day.Date:ddd}: {day.Condition}, {FormatRange(day, culture)}")) + "."
                : " Three-day outlook: " + string.Join("; ", Outlook.Select(day => $"{day.Date:ddd}: {day.Condition}, {FormatRange(day, culture)}")) + "."
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
