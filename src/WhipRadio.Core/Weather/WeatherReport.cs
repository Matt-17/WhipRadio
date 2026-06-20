using System.Globalization;

namespace WhipRadio.Core.Weather;

public sealed record WeatherReport(
    string Language,
    string LocationName,
    DateTime LocalTime,
    WeatherNow Current,
    WeatherDay Today,
    WeatherDayTemperatureContext TodayTemperature,
    double? TonightLowC,
    WeatherDay? Tomorrow,
    IReadOnlyList<WeatherDay> Outlook,
    IReadOnlyList<WeatherHour> NextHours)
{
    public string ToFacts(DateTime? airingLocalTime = null)
    {
        var culture = CultureInfo.InvariantCulture;
        var nearTermHours = airingLocalTime is null
            ? NextHours
            : NextHours.Where(hour => hour.Time > airingLocalTime.Value).ToList();

        var lines = new List<string>
        {
            $"Location: {LocationName}.",
            $"Weather observation time: {LocalTime:yyyy-MM-dd HH:mm} local.",
            airingLocalTime is null ? "Weather airing time: now." : $"Weather airing time: {airingLocalTime.Value:yyyy-MM-dd HH:mm} local.",
            $"Current conditions: {FormatTemperature(Current.TemperatureC, culture)} C, {Current.Condition}, wind {Current.WindSpeedKmh.ToString("0", culture)} km/h.",
            FormatTodayTemperature(culture),
        };

        if (nearTermHours.Count > 0)
        {
            lines.Add("Next hours after airing time: " + string.Join("; ", nearTermHours.Select(hour =>
                $"{hour.Time:HH:mm} {FormatTemperature(hour.TemperatureC, culture)} C, {hour.Condition}")) + ".");
        }

        lines.Add($"Today condition: {Today.Condition}; rain chance {Today.PrecipitationProbabilityPercent.GetValueOrDefault()}%.");

        if (TonightLowC is double low)
        {
            lines.Add($"Tonight low: around {FormatTemperature(low, culture)} C.");
        }

        if (Tomorrow is not null)
        {
            lines.Add($"Tomorrow: {Tomorrow.Condition}, {FormatRange(Tomorrow, culture)}, rain chance {Tomorrow.PrecipitationProbabilityPercent.GetValueOrDefault()}%.");
        }

        if (Outlook.Count > 0)
        {
            lines.Add("Three-day outlook: " + string.Join("; ", Outlook.Select(day => $"{day.Date:ddd}: {day.Condition}, {FormatRange(day, culture)}")) + ".");
        }

        return string.Join("\n", lines);
    }

    private static string FormatRange(WeatherDay day, CultureInfo culture)
    {
        var max = day.MaxTemperatureC is double maxTemperature ? FormatTemperature(maxTemperature, culture) : "?";
        var min = day.MinTemperatureC is double minTemperature ? FormatTemperature(minTemperature, culture) : "?";
        return $"{max} C/{min} C";
    }

    private string FormatTodayTemperature(CultureInfo culture)
    {
        var current = TodayTemperature.CurrentTemperatureC is double currentTemperatureValue
            ? FormatTemperature(currentTemperatureValue, culture)
            : "?";
        var max = TodayTemperature.DailyMaxTemperatureC is double maxTemperature
            ? FormatTemperature(maxTemperature, culture)
            : "?";
        var min = TodayTemperature.DailyMinTemperatureC is double minTemperature
            ? FormatTemperature(minTemperature, culture)
            : "?";
        var remaining = TodayTemperature.RemainingMaxTemperatureC is double remainingTemperature
            ? FormatTemperature(remainingTemperature, culture)
            : null;

        return TodayTemperature.DailyMaxStatus switch
        {
            WeatherDailyMaxStatus.AlreadyReached =>
                TodayTemperature.CurrentTemperatureC is double currentTemperature
                    && TodayTemperature.DailyMaxTemperatureC is double dailyMax
                    && Math.Abs(currentTemperature - dailyMax) <= 0.2
                        ? $"Today temperature range: current temperature {current} C already matches today's high {max} C; daily low {min} C; remaining hours stay near {remaining ?? current} C."
                        : $"Today temperature range: current temperature {current} C; daily high {max} C appears to be earlier/already reached; daily low {min} C; remaining hours stay near {remaining ?? current} C.",
            WeatherDailyMaxStatus.StillAhead =>
                $"Today temperature range: daily high {max} C is still ahead, expected near {TodayTemperature.RemainingMaxAt:HH:mm}; daily low {min} C.",
            _ =>
                $"Today temperature range: daily high {max} C (do not say it is still ahead unless next-hours data supports that); daily low {min} C.",
        };
    }

    private static string FormatTemperature(double temperatureC, CultureInfo culture)
        => Math.Round(temperatureC, MidpointRounding.AwayFromZero).ToString("0", culture);
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

public sealed record WeatherHour(
    DateTime Time,
    double TemperatureC,
    string Condition);

public sealed record WeatherDayTemperatureContext(
    double? CurrentTemperatureC,
    double? DailyMaxTemperatureC,
    double? DailyMinTemperatureC,
    double? RemainingMaxTemperatureC,
    DateTime? RemainingMaxAt,
    WeatherDailyMaxStatus DailyMaxStatus);

public enum WeatherDailyMaxStatus
{
    Unknown,
    AlreadyReached,
    StillAhead,
}
