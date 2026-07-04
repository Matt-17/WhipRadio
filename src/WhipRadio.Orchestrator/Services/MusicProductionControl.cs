namespace WhipRadio.Orchestrator.Services;

/// <summary>What the studio is currently recording (shown live in the library UI).</summary>
public sealed record GenerationStatus(Guid ArtistId, string ArtistName, string? TrackTitle, DateTime StartedAtUtc);

/// <summary>A manual "create new song" request; the optional hint steers the song plan.</summary>
public sealed record ManualSongRequest(Guid ArtistId, string? Hint = null);

/// <summary>
/// Library-driven production control: "create new song" requests queued from the
/// UI or chat, plus the live status of whatever generation (manual or automatic)
/// is currently running.
/// </summary>
public class MusicProductionControl
{
    private readonly object _queueLock = new();
    private readonly Queue<ManualSongRequest> _manualRequests = new();
    private readonly object _generationLock = new();
    private CancellationTokenSource? _currentCancel;
    private GenerationStatus? _current;

    public void RequestTrackFor(Guid artistId)
        => RequestTrackFor(new ManualSongRequest(artistId));

    public void RequestTrackFor(ManualSongRequest request)
    {
        lock (_queueLock)
        {
            _manualRequests.Enqueue(request);
        }
    }

    public ManualSongRequest? TryPeekManualRequest()
    {
        lock (_queueLock)
        {
            return _manualRequests.TryPeek(out var request) ? request : null;
        }
    }

    public ManualSongRequest? TryDequeueManualRequest()
    {
        lock (_queueLock)
        {
            return _manualRequests.TryDequeue(out var request) ? request : null;
        }
    }

    public void RequeueTrackForFront(ManualSongRequest request)
    {
        lock (_queueLock)
        {
            var existing = _manualRequests.ToArray();
            _manualRequests.Clear();
            _manualRequests.Enqueue(request);
            foreach (var queuedRequest in existing)
            {
                _manualRequests.Enqueue(queuedRequest);
            }
        }
    }

    public IReadOnlyList<Guid> QueuedArtistIds()
    {
        lock (_queueLock)
        {
            return _manualRequests.Select(request => request.ArtistId).ToList();
        }
    }

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
