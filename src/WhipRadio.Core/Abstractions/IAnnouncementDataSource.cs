namespace WhipRadio.Core.Abstractions;

/// <summary>Source of factual content for data-driven announcements (weather now; news/traffic later).</summary>
public interface IAnnouncementDataSource
{
    /// <summary>E.g. "weather".</summary>
    string Kind { get; }

    /// <summary>Plain-text facts the ScriptWriter turns into radio copy.</summary>
    Task<string> GetSummaryAsync(string language, CancellationToken ct);
}
