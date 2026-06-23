using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Abstractions;

public interface ITrackSelector
{
    Task<Track?> PickNextAsync(ShowContext context, CancellationToken ct);
}

/// <summary>Persistence-facing candidate source so the selector stays unit-testable.</summary>
public interface ITrackRepository
{
    /// <summary>All non-retired tracks.</summary>
    Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct);

    /// <summary>Ids of the most recently played tracks, newest first.</summary>
    Task<IReadOnlyList<Guid>> GetRecentlyPlayedTrackIdsAsync(int count, CancellationToken ct);

    /// <summary>Ids of tracks played at or after <paramref name="sinceUtc"/> (the current+previous show window).</summary>
    Task<IReadOnlyList<Guid>> GetTrackIdsPlayedSinceAsync(DateTime sinceUtc, int maxCount, CancellationToken ct);

    /// <summary>Recent plays with artist/subgenre metadata for the artist-repeat cap and subgenre rotation.</summary>
    Task<IReadOnlyList<PlayedTrackRef>> GetRecentPlayedRefsAsync(int count, CancellationToken ct);
}
