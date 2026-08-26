using System.Collections.Concurrent;

namespace WhipRadio.Infrastructure.Studios;

/// <summary>
/// In-memory booking state: which studio runs which job right now, and which
/// studio holds each shared GPU group. A single gate guards jobs and GPU leases
/// together so a booking and its lease can never be observed half-applied.
/// </summary>
public sealed class StudioBookingRegistry
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, StudioJob> _jobs = new();
    private readonly Dictionary<string, Guid> _gpuLeases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<Guid, StudioJob> ActiveJobs => _jobs;

    /// <summary>Books the studio unless it is already busy or its GPU group is leased
    /// to another studio; a successful booking takes the GPU lease along.</summary>
    public bool TryBook(Guid studioId, string? gpuResourceGroup, StudioJob job)
    {
        lock (_gate)
        {
            if (_jobs.ContainsKey(studioId))
            {
                return false;
            }

            if (gpuResourceGroup is not null && TryGetLiveGpuLease(gpuResourceGroup, out _, out _))
            {
                return false;
            }

            _jobs[studioId] = job;
            if (gpuResourceGroup is not null)
            {
                _gpuLeases[gpuResourceGroup] = studioId;
            }

            return true;
        }
    }

    /// <summary>Frees the booking and, when this studio holds its GPU group, the lease.</summary>
    public void Release(Guid studioId)
    {
        lock (_gate)
        {
            if (!_jobs.TryRemove(studioId, out var job))
            {
                return;
            }

            if (job.GpuResourceGroup is not null
                && _gpuLeases.TryGetValue(job.GpuResourceGroup, out var leasedStudioId)
                && leasedStudioId == studioId)
            {
                _gpuLeases.Remove(job.GpuResourceGroup);
            }
        }
    }

    public bool IsBookedOrGpuBlocked(Guid studioId, string? gpuResourceGroup)
    {
        lock (_gate)
        {
            if (_jobs.ContainsKey(studioId))
            {
                return true;
            }

            return gpuResourceGroup is not null && TryGetLiveGpuLease(gpuResourceGroup, out _, out _);
        }
    }

    /// <summary>The job currently holding the GPU group, if any.</summary>
    public bool TryGetGpuBlocker(string? gpuResourceGroup, out StudioJob blocker)
    {
        blocker = default!;
        if (gpuResourceGroup is null)
        {
            return false;
        }

        lock (_gate)
        {
            return TryGetLiveGpuLease(gpuResourceGroup, out _, out blocker);
        }
    }

    /// <summary>Stores the trimmed progress on the booked job; false when nothing changed
    /// (unknown studio, blank progress, or same value) so callers can skip publishing.</summary>
    public bool TryUpdateProgress(Guid studioId, string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
        {
            return false;
        }

        var trimmed = progress.Trim();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(studioId, out var job)
                || string.Equals(job.Progress, trimmed, StringComparison.Ordinal))
            {
                return false;
            }

            _jobs[studioId] = job with { Progress = trimmed };
            return true;
        }
    }

    /// <summary>Must run under the gate. Prunes a stale lease whose job is gone.</summary>
    private bool TryGetLiveGpuLease(string group, out Guid studioId, out StudioJob job)
    {
        if (!_gpuLeases.TryGetValue(group, out studioId))
        {
            job = default!;
            return false;
        }

        if (_jobs.TryGetValue(studioId, out job!))
        {
            return true;
        }

        _gpuLeases.Remove(group);
        job = default!;
        return false;
    }
}
