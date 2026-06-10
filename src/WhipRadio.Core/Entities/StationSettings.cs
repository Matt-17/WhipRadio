namespace WhipRadio.Core.Entities;

/// <summary>Single-row station configuration (Id = 1).</summary>
public class StationSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string StationName { get; set; } = "WhipRadio";

    public string DefaultLanguage { get; set; } = "en";

    /// <summary>How many unplayed tracks the music producer keeps in stock.</summary>
    public int TargetQueueLength { get; set; } = 3;

    public int AnnouncementEveryNTracks { get; set; } = 1;
}
