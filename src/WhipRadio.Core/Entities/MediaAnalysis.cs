namespace WhipRadio.Core.Entities;

/// <summary>
/// Per-item audio analysis (BPM, beat grid, intro/outro, loudness, energy) —
/// one row per analysed track or announcement. Source-agnostic: computed from
/// the WAV alone, so generated and imported media flow through the same path.
/// </summary>
public class MediaAnalysis
{
    public Guid Id { get; set; }

    public PlayoutItemType ItemType { get; set; }

    /// <summary>FK by convention (Track or Announcement id — no hard FK across two tables).</summary>
    public Guid ItemId { get; set; }

    /// <summary>Null when undetectable.</summary>
    public double? Bpm { get; set; }

    /// <summary>0–1 (tempogram peak strength heuristic).</summary>
    public double BpmConfidence { get; set; }

    /// <summary>JSON double[] of beat timestamps in seconds; null for announcements.</summary>
    public string? BeatGridJson { get; set; }

    /// <summary>Energy onset point; null if low confidence.</summary>
    public double? IntroEndSeconds { get; set; }

    public double IntroConfidence { get; set; }

    /// <summary>Sustained energy drop point.</summary>
    public double? OutroStartSeconds { get; set; }

    public double OutroConfidence { get; set; }

    public double LeadingSilenceSeconds { get; set; }

    public double TrailingSilenceSeconds { get; set; }

    /// <summary>EBU R128 integrated loudness.</summary>
    public double IntegratedLufs { get; set; }

    public double TruePeakDb { get; set; }

    /// <summary>JSON double[] RMS at 2 Hz, normalised 0–1.</summary>
    public string EnergyProfileJson { get; set; } = "[]";

    /// <summary>Authoritative duration (replaces ffprobe-only value).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>0 = analysis failed (item stays playable, planner degrades);
    /// bumped when the algorithm changes so backfill re-runs.</summary>
    public int AnalyzerVersion { get; set; }

    public DateTime AnalyzedAt { get; set; }
}

/// <summary>One row per executed transition — observability for the mixer.</summary>
public class TransitionLogEntry
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public PlayoutItemType OutgoingType { get; set; }

    public Guid OutgoingId { get; set; }

    public PlayoutItemType IncomingType { get; set; }

    public Guid IncomingId { get; set; }

    public string Strategy { get; set; } = string.Empty;

    public double OverlapSeconds { get; set; }

    public int GapMs { get; set; }

    public string ParametersJson { get; set; } = "{}";

    /// <summary>Samples hard-clamped during the transition window (should be ~0).</summary>
    public int ClipCount { get; set; }
}
