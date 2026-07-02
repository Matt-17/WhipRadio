using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Studios;

public sealed record StudioPendingOperation(
    Guid Id,
    StudioKind Kind,
    string Label,
    DateTime StartedAtUtc,
    string Status,
    string? Detail = null,
    string? Progress = null,
    string? ResourceGroup = null,
    Guid? StudioId = null);

public static class StudioPendingOperationStatus
{
    public const string Waiting = "WAITING";
    public const string Loading = "LOADING";
    public const string Preparing = "PREPARING";
    public const string Work = "WORK";
    public const string Recording = "REC";
}
