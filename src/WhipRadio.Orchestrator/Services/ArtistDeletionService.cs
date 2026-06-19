using Microsoft.EntityFrameworkCore;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public enum ArtistDeletionStatus
{
    Deleted,
    NotFound,
    HasTracks,
    InProduction,
}

public sealed record ArtistDeletionResult(ArtistDeletionStatus Status, int TrackCount = 0);

/// <summary>Deletes artist profiles only while they have no songs and no pending recording.</summary>
public class ArtistDeletionService(
    IDbContextFactory<RadioDbContext> dbFactory,
    MusicProductionControl productionControl)
{
    public async Task<ArtistDeletionResult> DeleteAsync(Guid artistId, CancellationToken ct)
    {
        if (productionControl.Current?.ArtistId == artistId
            || productionControl.QueuedArtistIds().Contains(artistId))
        {
            return new ArtistDeletionResult(ArtistDeletionStatus.InProduction);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var artist = await db.Artists
            .Include(a => a.Members)
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);
        if (artist is null)
        {
            return new ArtistDeletionResult(ArtistDeletionStatus.NotFound);
        }

        var trackCount = await db.Tracks.CountAsync(t => t.ArtistId == artistId, ct);
        if (trackCount > 0)
        {
            return new ArtistDeletionResult(ArtistDeletionStatus.HasTracks, trackCount);
        }

        db.Artists.Remove(artist);
        await db.SaveChangesAsync(ct);
        return new ArtistDeletionResult(ArtistDeletionStatus.Deleted);
    }
}
