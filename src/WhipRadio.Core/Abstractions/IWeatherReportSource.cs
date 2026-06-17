using WhipRadio.Core.Weather;

namespace WhipRadio.Core.Abstractions;

public interface IWeatherReportSource
{
    Task<WeatherReport> GetReportAsync(string language, CancellationToken ct);
}
