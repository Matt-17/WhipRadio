using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Operator decisions over stored match candidates (Phase 6a §8.4): accept one
/// (siblings rejected, fields applied as user-provided claims, status
/// Verified), reject one, pin a track local-only (enrichment skips it), or
/// bulk-promote every Matched track to Verified.
/// </summary>
public sealed class MetadataReviewService(
    IDbContextFactory<RadioDbContext> dbFactory,
    TimeProvider timeProvider)
{
    public async Task<bool> AcceptCandidateAsync(Guid trackId, Guid candidateId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId, ct);
        var candidates = await db.MetadataCandidates.Where(c => c.TrackId == trackId).ToListAsync(ct);
        var accepted = candidates.FirstOrDefault(c => c.Id == candidateId);
        if (track is null || accepted is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var candidate in candidates)
        {
            candidate.Status = candidate.Id == candidateId ? CandidateStatus.Accepted : CandidateStatus.Rejected;
        }

        track.Title = accepted.DisplayTitle;
        track.ImportedArtist = string.IsNullOrWhiteSpace(accepted.DisplayArtist) ? track.ImportedArtist : accepted.DisplayArtist;
        track.ImportedAlbum = accepted.DisplayAlbum ?? track.ImportedAlbum;
        track.ImportedYear = accepted.DisplayYear ?? track.ImportedYear;
        track.MetadataStatus = MetadataStatus.Verified;
        track.MetadataConfidence = 1.0;

        AddUserClaim(db, trackId, "Title", accepted.DisplayTitle, accepted.SourceEntityId, now);
        AddUserClaim(db, trackId, "Artist", accepted.DisplayArtist, accepted.ArtistEntityId, now);
        if (accepted.DisplayAlbum is not null)
        {
            AddUserClaim(db, trackId, "Album", accepted.DisplayAlbum, accepted.SourceEntityId, now);
        }

        if (!await db.ExternalIds.AnyAsync(e => e.OwnerType == MetadataOwnerType.Track
            && e.OwnerId == trackId && e.Source == "MusicBrainz" && e.Value == accepted.SourceEntityId, ct))
        {
            db.ExternalIds.Add(new ExternalId
            {
                Id = Guid.NewGuid(),
                OwnerType = MetadataOwnerType.Track,
                OwnerId = trackId,
                Source = "MusicBrainz",
                EntityType = "Recording",
                Value = accepted.SourceEntityId,
                Confidence = 1.0,
                CreatedAt = now,
            });
            if (!string.IsNullOrWhiteSpace(accepted.ArtistEntityId))
            {
                db.ExternalIds.Add(new ExternalId
                {
                    Id = Guid.NewGuid(),
                    OwnerType = MetadataOwnerType.Track,
                    OwnerId = trackId,
                    Source = "MusicBrainz",
                    EntityType = "Artist",
                    Value = accepted.ArtistEntityId!,
                    Confidence = 1.0,
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RejectCandidateAsync(Guid trackId, Guid candidateId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = await db.MetadataCandidates
            .Where(c => c.TrackId == trackId && c.Id == candidateId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CandidateStatus.Rejected), ct);
        if (updated == 0)
        {
            return false;
        }

        // All candidates rejected → the track needs new evidence, not re-review.
        var pendingLeft = await db.MetadataCandidates
            .AnyAsync(c => c.TrackId == trackId && c.Status == CandidateStatus.Pending, ct);
        if (!pendingLeft)
        {
            await db.Tracks
                .Where(t => t.Id == trackId && t.MetadataStatus == MetadataStatus.Ambiguous)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.MetadataStatus, MetadataStatus.NeedsReview), ct);
        }

        return true;
    }

    /// <summary>Pins the track to its file identity; Rejected is excluded from enrichment.</summary>
    public async Task<bool> KeepLocalAsync(Guid trackId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId && t.Source != TrackSource.Generated, ct);
        if (track is null)
        {
            return false;
        }

        track.MetadataStatus = MetadataStatus.Rejected;
        track.MetadataConfidence = null;
        await db.MetadataCandidates
            .Where(c => c.TrackId == trackId && c.Status == CandidateStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CandidateStatus.Rejected), ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Bulk: promotes every Matched/AutoMatched track to Verified.</summary>
    public async Task<int> AcceptAllMatchedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks
            .Where(t => t.Source != TrackSource.Generated
                && (t.MetadataStatus == MetadataStatus.Matched || t.MetadataStatus == MetadataStatus.AutoMatched))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MetadataStatus, MetadataStatus.Verified), ct);
    }

    private void AddUserClaim(RadioDbContext db, Guid trackId, string field, string value, string? sourceEntityId, DateTime now)
        => db.MetadataClaims.Add(new MetadataClaim
        {
            Id = Guid.NewGuid(),
            OwnerType = MetadataOwnerType.Track,
            OwnerId = trackId,
            FieldName = field,
            Value = value,
            Source = "User",
            SourceEntityId = sourceEntityId,
            LicenseClass = MetadataLicenseClass.UserProvided,
            Confidence = 1.0,
            IsApplied = true,
            CreatedAt = now,
        });
}
