namespace WhipRadio.Core.Entities;

public enum ProgramDirectorLogSource
{
    Autonomous = 0,
    Chat = 1,
}

public class ProgramDirectorLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ProgramDirectorLogSource Source { get; set; }

    public string PromptSummary { get; set; } = string.Empty;

    public string? ActionsJson { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Error { get; set; }
}
