namespace WhipRadio.Orchestrator.Configuration;

public class RadioOptions
{
    public const string SectionName = "Radio";

    /// <summary>Root of the shared data volume; tracks/announcements/db live below it.</summary>
    public string DataRoot { get; set; } = OperatingSystem.IsWindows() || !Directory.Exists("/data")
        ? Path.Combine(Directory.GetCurrentDirectory(), "data")
        : "/data";

    public string TracksDirectory => Path.Combine(DataRoot, "library", "tracks");

    public string AnnouncementsDirectory => Path.Combine(DataRoot, "library", "announcements");
}

public class IcecastOptions
{
    public const string SectionName = "Icecast";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 8000;

    public string SourceUser { get; set; } = "source";

    public string SourcePassword { get; set; } = "hackme-dev";

    public string AdminUser { get; set; } = "admin";

    public string AdminPassword { get; set; } = "hackme-admin";
}

public class StreamOptions
{
    public const string SectionName = "Stream";

    public string Mount { get; set; } = "/radio.mp3";

    public string Bitrate { get; set; } = "192k";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Seconds between feeding an item into the encoder and the listener actually
    /// hearing it (ffmpeg pipes + Icecast burst + browser buffer). Now-playing,
    /// queue and play-log updates are delayed by this much so the display matches
    /// the ears, not the encoder. Tune if titles still flip early/late.
    /// </summary>
    public double DisplayLatencySeconds { get; set; } = 8;
}

public class MusicOptions
{
    public const string SectionName = "Music";

    /// <summary>Length of generated tracks. CPU generation of 90 s takes minutes — by design.</summary>
    public int TrackDurationSeconds { get; set; } = 90;

    /// <summary>Seconds between library checks of the music producer.</summary>
    public int ProducerBackoffSeconds { get; set; } = 30;
}
