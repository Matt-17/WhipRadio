namespace WhipRadio.Infrastructure.Weather;

/// <summary>Maps WMO weather interpretation codes to English words.</summary>
public static class WmoWeatherCodes
{
    private static readonly Dictionary<int, string> Map = new()
    {
        [0] = "clear sky",
        [1] = "mainly clear",
        [2] = "partly cloudy",
        [3] = "overcast",
        [45] = "fog",
        [48] = "depositing rime fog",
        [51] = "light drizzle",
        [53] = "drizzle",
        [55] = "dense drizzle",
        [56] = "freezing drizzle",
        [57] = "dense freezing drizzle",
        [61] = "light rain",
        [63] = "rain",
        [65] = "heavy rain",
        [66] = "freezing rain",
        [67] = "heavy freezing rain",
        [71] = "light snow",
        [73] = "snow",
        [75] = "heavy snow",
        [77] = "snow grains",
        [80] = "light rain showers",
        [81] = "rain showers",
        [82] = "violent rain showers",
        [85] = "light snow showers",
        [86] = "heavy snow showers",
        [95] = "thunderstorm",
        [96] = "thunderstorm with hail",
        [99] = "thunderstorm with heavy hail",
    };

    public static string Describe(int code)
        => Map.TryGetValue(code, out var entry) ? entry : "mixed weather";
}
