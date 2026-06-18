using System.Text.Json;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Persists the live playout timeline so a process restart can keep the same
/// current item and listener-facing next-up queue instead of rebuilding it.
/// </summary>
public sealed class PlayoutStateStore(
    IOptions<RadioOptions> radioOptions,
    TimeProvider timeProvider,
    ILogger<PlayoutStateStore> logger)
{
    private const double MinimumResumeRemainderSeconds = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Lock _lock = new();
    private readonly string _statePath = Path.Combine(radioOptions.Value.DataRoot, "state", "playout-state.json");
    private StateDocument _state = LoadState(
        Path.Combine(radioOptions.Value.DataRoot, "state", "playout-state.json"),
        logger);

    public PlayoutResumePlan BuildResumePlan()
    {
        lock (_lock)
        {
            var queued = _state.QueuedItems
                .Select(NormalizeQueueItem)
                .ToList();

            if (_state.ActiveItem is null || _state.ActiveStartedAtUtc is null)
            {
                return BuildQueuedOnlyPlan(queued);
            }

            var active = NormalizeActiveItem(_state.ActiveItem);
            queued.RemoveAll(item => SameIdentity(item, active));

            var elapsed = Math.Max(
                0,
                (timeProvider.GetUtcNow().UtcDateTime - _state.ActiveStartedAtUtc.Value).TotalSeconds);

            return BuildTimelinePlan([active, .. queued], elapsed);
        }
    }

    public void ResetForRestore()
    {
        lock (_lock)
        {
            _state = new StateDocument();
            SaveLocked();
        }
    }

    public void Enqueued(PlayoutItem item)
    {
        lock (_lock)
        {
            _state.QueuedItems.Add(NormalizeQueueItem(item));
            SaveLocked();
        }
    }

    public void EnqueuedFront(PlayoutItem item)
    {
        lock (_lock)
        {
            _state.QueuedItems.Insert(0, NormalizeQueueItem(item));
            SaveLocked();
        }
    }

    public void BecameVisible(PlayoutItem item)
    {
        lock (_lock)
        {
            _state.QueuedItems.RemoveAll(queued => queued.ItemId == item.ItemId);
            SaveLocked();
        }
    }

    public DateTime MarkStarted(PlayoutItem item)
    {
        var startOffset = ClampOffset(item.StartOffsetSeconds, item.DurationSeconds);
        var activeStartedAtUtc = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(startOffset);

        lock (_lock)
        {
            _state.ActiveItem = NormalizeActiveItem(item);
            _state.ActiveStartedAtUtc = activeStartedAtUtc;
            SaveLocked();
        }

        return activeStartedAtUtc;
    }

    public void Complete(PlayoutItem item)
    {
        lock (_lock)
        {
            if (_state.ActiveItem is not null && SameIdentity(_state.ActiveItem, item))
            {
                _state.ActiveItem = null;
                _state.ActiveStartedAtUtc = null;
                SaveLocked();
            }
        }
    }

    private PlayoutResumePlan BuildQueuedOnlyPlan(List<PlayoutItem> queued)
    {
        if (queued.Count == 0)
        {
            return PlayoutResumePlan.Empty;
        }

        var first = queued[0];
        if (first.StartOffsetSeconds <= 0 || _state.SavedAtUtc == default)
        {
            return new PlayoutResumePlan(null, queued, []);
        }

        queued[0] = NormalizeActiveItem(first);
        var elapsed = first.StartOffsetSeconds
            + Math.Max(0, (timeProvider.GetUtcNow().UtcDateTime - _state.SavedAtUtc).TotalSeconds);
        return BuildTimelinePlan(queued, elapsed);
    }

    private static PlayoutResumePlan BuildTimelinePlan(IReadOnlyList<PlayoutItem> timeline, double elapsedSeconds)
    {
        var skipped = new List<PlayoutItem>();

        for (var i = 0; i < timeline.Count; i++)
        {
            var item = NormalizeActiveItem(timeline[i]);
            var duration = Math.Max(0, item.DurationSeconds);
            var remaining = duration - elapsedSeconds;
            if (remaining > MinimumResumeRemainderSeconds)
            {
                var current = item with { StartOffsetSeconds = ClampOffset(elapsedSeconds, duration) };
                var queue = timeline
                    .Skip(i + 1)
                    .Select(NormalizeQueueItem)
                    .ToList();
                return new PlayoutResumePlan(current, queue, skipped);
            }

            skipped.Add(item);
            elapsedSeconds -= duration;
            if (elapsedSeconds < 0)
            {
                elapsedSeconds = 0;
            }
        }

        return new PlayoutResumePlan(null, [], skipped);
    }

    private static StateDocument LoadState(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return new StateDocument();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StateDocument>(json, JsonOptions) ?? new StateDocument();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read playout state from {Path}; starting with an empty queue", path);
            return new StateDocument();
        }
    }

    private void SaveLocked()
    {
        _state.SavedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tempPath = _statePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist playout state to {Path}", _statePath);
        }
    }

    private static PlayoutItem NormalizeActiveItem(PlayoutItem item)
        => item with { StartOffsetSeconds = 0 };

    private static PlayoutItem NormalizeQueueItem(PlayoutItem item)
        => item with { StartOffsetSeconds = ClampOffset(item.StartOffsetSeconds, item.DurationSeconds) };

    private static double ClampOffset(double offset, double duration)
        => Math.Clamp(double.IsFinite(offset) ? offset : 0, 0, Math.Max(0, duration));

    private static bool SameIdentity(PlayoutItem left, PlayoutItem right)
        => left.ItemType == right.ItemType && left.ItemId == right.ItemId;

    private sealed class StateDocument
    {
        public PlayoutItem? ActiveItem { get; set; }

        public DateTime? ActiveStartedAtUtc { get; set; }

        public DateTime SavedAtUtc { get; set; }

        public List<PlayoutItem> QueuedItems { get; set; } = [];
    }
}

public sealed record PlayoutResumePlan(
    PlayoutItem? CurrentItem,
    IReadOnlyList<PlayoutItem> QueueItems,
    IReadOnlyList<PlayoutItem> SkippedItems)
{
    public static PlayoutResumePlan Empty { get; } = new(null, [], []);

    public IEnumerable<PlayoutItem> ItemsToEnqueue()
    {
        if (CurrentItem is not null)
        {
            yield return CurrentItem;
        }

        foreach (var item in QueueItems)
        {
            yield return item;
        }
    }
}
