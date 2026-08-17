namespace WhipRadio.Core.Entities;

/// <summary>Risk category shown to the Boss when a destructive/authority-sensitive verb waits for approval.</summary>
public enum ApprovalRisk
{
    Schedule = 0,
    Personnel = 1,
    Library = 2,
    External = 3,
    Settings = 4,
    Cost = 5,
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Expired = 3,
}

/// <summary>
/// A chat verb that requires explicit Boss confirmation before it runs. The agent
/// (or a direct test invocation) creates one of these instead of executing the
/// side effect; the Boss approves or denies it from the chat approvals strip or the
/// Verbs page. On approval the stored verb is revalidated and executed through the
/// same <c>ChatActionExecutor</c> path, so no side effect ever bypasses validation.
/// </summary>
public class PendingApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Verb name to execute on approval (must exist in the tool catalog for the requester role).</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Serialized argument dictionary for the pending verb.</summary>
    public string ArgumentsJson { get; set; } = string.Empty;

    /// <summary>Operator-readable summary of what will happen.</summary>
    public string Summary { get; set; } = string.Empty;

    public ApprovalRisk Risk { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    // Requester identity, stored so the sender context can be rebuilt on approval.
    public ChatParticipantKind RequesterKind { get; set; }

    public int? RequesterModeratorId { get; set; }

    public Guid? RequesterEntityId { get; set; }

    public string RequesterName { get; set; } = string.Empty;

    /// <summary>Origin chat channel; the outcome is posted back here.</summary>
    public Guid ChannelId { get; set; }

    public Guid CorrelationId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ResolvedUtc { get; set; }

    /// <summary>Result summary after approval execution, or the denial reason.</summary>
    public string? ResultSummary { get; set; }
}
