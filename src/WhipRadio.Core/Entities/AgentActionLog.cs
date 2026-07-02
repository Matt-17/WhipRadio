namespace WhipRadio.Core.Entities;

public enum AgentLogEventKind
{
    Reply = 0,
    Action = 1,
    Error = 2,
}

/// <summary>
/// One event inside an agentic loop: a round's reply, an executed action with
/// its result, or an error. Every agent (Program Director, hosts, future
/// artists) writes here so the Agent Log page can show the full back-and-forth
/// that the consumer-facing chat intentionally hides.
/// </summary>
public class AgentActionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string AgentName { get; set; } = string.Empty;

    public int? ModeratorId { get; set; }

    /// <summary>Which loop produced the event, such as "chat".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Ties all events of one turn/exchange together.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>1-based loop round within the turn; 0 when not round-scoped.</summary>
    public int Round { get; set; }

    public AgentLogEventKind Kind { get; set; }

    /// <summary>Tool name for Action events.</summary>
    public string? Tool { get; set; }

    /// <summary>Reply prose, action arguments + result, or error text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Action outcome such as Succeeded or Failed.</summary>
    public string? Outcome { get; set; }
}
