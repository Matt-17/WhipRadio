namespace WhipRadio.Infrastructure.Weather;

/// <summary>Maps WMO weather interpretation codes to human words (en/de).</summary>
public static class WmoWeatherCodes
{
    private static readonly Dictionary<int, (string En, string De)> Map = new()
    {
        [0] = ("clear sky", "klarer Himmel"),
        [1] = ("mainly clear", "überwiegend klar"),
        [2] = ("partly cloudy", "teilweise bewölkt"),
        [3] = ("overcast", "bedeckt"),
        [45] = ("fog", "Nebel"),
        [48] = ("depositing rime fog", "Raureifnebel"),
        [51] = ("light drizzle", "leichter Nieselregen"),
        [53] = ("drizzle", "Nieselregen"),
        [55] = ("dense drizzle", "starker Nieselregen"),
        [56] = ("freezing drizzle", "gefrierender Nieselregen"),
        [57] = ("dense freezing drizzle", "starker gefrierender Nieselregen"),
        [61] = ("light rain", "leichter Regen"),
        [63] = ("rain", "Regen"),
        [65] = ("heavy rain", "starker Regen"),
        [66] = ("freezing rain", "gefrierender Regen"),
        [67] = ("heavy freezing rain", "starker gefrierender Regen"),
        [71] = ("light snow", "leichter Schneefall"),
        [73] = ("snow", "Schneefall"),
        [75] = ("heavy snow", "starker Schneefall"),
        [77] = ("snow grains", "Schneegriesel"),
        [80] = ("light rain showers", "leichte Regenschauer"),
        [81] = ("rain showers", "Regenschauer"),
        [82] = ("violent rain showers", "heftige Regenschauer"),
        [85] = ("light snow showers", "leichte Schneeschauer"),
        [86] = ("heavy snow showers", "starke Schneeschauer"),
        [95] = ("thunderstorm", "Gewitter"),
        [96] = ("thunderstorm with hail", "Gewitter mit Hagel"),
        [99] = ("thunderstorm with heavy hail", "Gewitter mit starkem Hagel"),
    };

    public static string Describe(int code, bool german = false)
        => Map.TryGetValue(code, out var entry)
            ? (german ? entry.De : entry.En)
            : (german ? "wechselhaftes Wetter" : "mixed weather");
}
