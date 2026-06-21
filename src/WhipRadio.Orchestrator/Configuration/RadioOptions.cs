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

    /// <summary>Secret — set via <c>ICECAST_SOURCE_PASSWORD</c> env var / <c>.env</c>. Never committed.</summary>
    public string SourcePassword { get; set; } = "";

    public string AdminUser { get; set; } = "admin";

    /// <summary>Secret — set via <c>ICECAST_ADMIN_PASSWORD</c> env var / <c>.env</c>. Never committed.</summary>
    public string AdminPassword { get; set; } = "";
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

    /// <summary>
    /// Initial delay before restarting the encoder ffmpeg after a crash. Doubles
    /// on each consecutive rapid crash (5s → 10s → 20s → 40s) up to
    /// <see cref="EncoderMaxBackoffSeconds"/>, so a sustained Icecast outage
    /// can't hot-loop ffmpeg crashes. A session that runs longer than
    /// <see cref="EncoderSuccessResetsAfterSeconds"/> before crashing resets the
    /// streak (treated as a fresh incident, not a hot-loop).
    /// </summary>
    public int EncoderInitialBackoffSeconds { get; set; } = 5;

    /// <summary>Cap for the encoder restart backoff.</summary>
    public int EncoderMaxBackoffSeconds { get; set; } = 60;

    /// <summary>
    /// Crashing again only counts as a "rapid" crash if the previous encoder
    /// session ran shorter than this. A long healthy session clears the crash
    /// streak so a later, unrelated crash starts the backoff from the floor.
    /// </summary>
    public int EncoderSuccessResetsAfterSeconds { get; set; } = 120;

    /// <summary>
    /// Circuit-breaker threshold: if this many encoder crashes happen inside
    /// <see cref="EncoderCrashWindowMinutes"/>, the station is parked (PlayoutEnabled
    /// flipped off, "station offline" surfaced to the UI) and stays parked until
    /// an operator re-enables On Air. Stops silent ffmpeg crash-loops during a
    /// sustained Icecast outage.
    /// </summary>
    public int EncoderCrashThreshold { get; set; } = 5;

    /// <summary>Rolling window for <see cref="EncoderCrashThreshold"/>.</summary>
    public int EncoderCrashWindowMinutes { get; set; } = 5;
}

public class MusicOptions
{
    public const string SectionName = "Music";

    /// <summary>Length of generated tracks. CPU generation of 90 s takes minutes — by design.</summary>
    public int TrackDurationSeconds { get; set; } = 90;

    /// <summary>Seconds between library checks of the music producer.</summary>
    public int ProducerBackoffSeconds { get; set; } = 30;
}
