using System.Collections.Concurrent;

namespace WhipRadio.Orchestrator.Services;

/// <summary>What the studio is currently recording (shown live in the library UI).</summary>
public sealed record GenerationStatus(Guid ArtistId, string ArtistName, string? TrackTitle, DateTime StartedAtUtc);

/// <summary>
/// Library-driven production control: "create new song" requests queued from the
/// UI, plus the live status of whatever generation (manual or automatic) is
/// currently running.
/// </summary>
public class MusicProductionControl
{
    private readonly ConcurrentQueue<Guid> _manualRequests = new();
    private readonly object _generationLock = new();
    private CancellationTokenSource? _currentCancel;
    private GenerationStatus? _current;

    public void RequestTrackFor(Guid artistId) => _manualRequests.Enqueue(artistId);

    public Guid? TryDequeueManualRequest()
        => _manualRequests.TryDequeue(out var artistId) ? artistId : null;

    public IReadOnlyList<Guid> QueuedArtistIds() => [.. _manualRequests];

    public GenerationStatus? Current => Volatile.Read(ref _current);

    public CancellationToken BeginGeneration(Guid artistId, string artistName, CancellationToken parentToken)
    {
        lock (_generationLock)
        {
            _currentCancel?.Dispose();
            _currentCancel = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            Volatile.Write(ref _current, new GenerationStatus(artistId, artistName, null, DateTime.UtcNow));
            return _currentCancel.Token;
        }
    }

    public void ReportTitle(string title)
    {
        if (Volatile.Read(ref _current) is { } status)
        {
            Volatile.Write(ref _current, status with { TrackTitle = title });
        }
    }

    public bool CancelGeneration()
    {
        lock (_generationLock)
        {
            if (_current is null || _currentCancel is null || _currentCancel.IsCancellationRequested)
            {
                return false;
            }

            _currentCancel.Cancel();
            return true;
        }
    }

    public void EndGeneration()
    {
        lock (_generationLock)
        {
            Volatile.Write(ref _current, null);
            _currentCancel?.Dispose();
            _currentCancel = null;
        }
    }
}
