using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Abstractions;

public interface ITrackSelector
{
    Task<Track?> PickNextAsync(ScheduleSlot slot, Moderator moderator, CancellationToken ct);
}

/// <summary>Persistence-facing candidate source so the selector stays unit-testable.</summary>
public interface ITrackRepository
{
    /// <summary>All non-retired tracks.</summary>
    Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct);

    /// <summary>Ids of the most recently played tracks, newest first.</summary>
    Task<IReadOnlyList<Guid>> GetRecentlyPlayedTrackIdsAsync(int count, CancellationToken ct);
}
