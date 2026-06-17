namespace WhipRadio.Core.Entities;

public enum TalkBitStatus
{
    Active,
    Retired,
}

public class TalkBit
{
    public Guid Id { get; set; }

    public int ModeratorId { get; set; }

    public string Premise { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public TalkBitStatus Status { get; set; } = TalkBitStatus.Active;

    public int CooldownDays { get; set; } = 5;

    public int PlayCount { get; set; }

    public int ExactReplayCount { get; set; }

    public int FreshRetellCount { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RetiredAtUtc { get; set; }

    public string? RetirementReason { get; set; }

    public List<TalkBitRendition> Renditions { get; set; } = [];
}

public class TalkBitRendition
{
    public Guid Id { get; set; }

    public Guid TalkBitId { get; set; }

    public TalkBit? TalkBit { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    public double DurationSeconds { get; set; }

    public bool CreatedFromRetelling { get; set; }

    public int PlayCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastPlayedAtUtc { get; set; }
}
