using System.Collections.Concurrent;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// Tracks the "waiting for GPU / loading model / running" operations shown on
/// the studios page while jobs queue for a turn, and publishes a studios-changed
/// signal on every transition. Purely presentational — no booking authority.
/// </summary>
public sealed class StudioPendingOperationsTracker(IStudioUpdatePublisher updatePublisher)
{
    private readonly ConcurrentDictionary<Guid, StudioPendingOperation> _pendingOperations = new();

    public IReadOnlyList<StudioPendingOperation> PendingOperations =>
        _pendingOperations.Values.OrderBy(operation => operation.StartedAtUtc).ToList();

    public async Task<Guid> AddAsync(
        StudioKind kind,
        string label,
        string status,
        string? detail,
        string? resourceGroup,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _pendingOperations[id] = new StudioPendingOperation(
            id,
            kind,
            label.Trim(),
            DateTime.UtcNow,
            status,
            detail,
            Progress: null,
            resourceGroup);
        await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        return id;
    }

    public async Task UpdateAsync(
        Guid id,
        string status,
        string? detail,
        string? progress,
        CancellationToken ct)
    {
        if (!_pendingOperations.TryGetValue(id, out var operation))
        {
            return;
        }

        _pendingOperations[id] = operation with
        {
            Status = status,
            Detail = detail,
            Progress = progress,
        };
        await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        if (_pendingOperations.TryRemove(id, out _))
        {
            await updatePublisher.PublishStudiosChangedAsync(CancellationToken.None);
        }
    }

    public static string DefaultLabel(StudioKind kind)
        => kind switch
        {
            StudioKind.WriterRoom => "Writing text",
            StudioKind.VoiceBooth => "Voicing audio",
            _ => "Recording music",
        };

    public static string ActiveStatus(StudioKind kind)
        => kind == StudioKind.WriterRoom
            ? StudioPendingOperationStatus.Work
            : StudioPendingOperationStatus.Recording;

    public static string PreparingDetail(StudioKind kind)
        => $"Preparing {KindDisplayName(kind)} endpoint";

    public static string ModelSwitchDetail(StudioKind kind, LocalGpuScheduler.GpuLease lease)
    {
        var target = KindDisplayName(kind);
        return string.IsNullOrWhiteSpace(lease.PreviousAffinity)
            ? $"Loading {target} model"
            : $"Switching from {AffinityDisplayName(lease.PreviousAffinity)} to {target}";
    }

    private static string AffinityDisplayName(string affinity)
        => Enum.TryParse<StudioKind>(affinity, ignoreCase: true, out var kind)
            ? KindDisplayName(kind)
            : affinity;

    private static string KindDisplayName(StudioKind kind)
        => kind switch
        {
            StudioKind.WriterRoom => "Writer Room",
            StudioKind.VoiceBooth => "Voice Booth",
            _ => "Recording",
        };
}
