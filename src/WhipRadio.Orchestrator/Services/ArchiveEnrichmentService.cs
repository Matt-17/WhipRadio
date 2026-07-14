using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Metadata;
using WhipRadio.Infrastructure.Metadata;
using WhipRadio.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using WhipRadio.Orchestrator.Configuration;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Background metadata enrichment for imported music (Phase 6a): file tags →
/// MusicBrainz identity anchoring (keyless, rate-gated) → confidence-scored
/// claims/candidates → Wikidata facts + Wikipedia summary paraphrased into a
/// cached knowledge digest. Never blocks the stream; every failure is soft —
/// the track stays LocalOnly and retries after a cool-down.
/// </summary>
public sealed class ArchiveEnrichmentService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<RadioDbContext> dbFactory,
    StationSettingsCache settingsCache,
    Core.Metadata.IFileTagReader tagReader,
    IOptions<RadioOptions> radioOptions,
    IOptions<MusicMetadataOptions> metadataOptions,
    IProductionUpdatePublisher publisher,
    TimeProvider timeProvider,
    ILogger<ArchiveEnrichmentService> logger) : BackgroundService
{
    private static readonly TimeSpan CycleDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryCoolDown = TimeSpan.FromHours(24);
    private const int BatchSize = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsCache.GetAsync(stoppingToken);
                if (settings.ArchiveEnrichmentEnabled)
                {
                    await RunCycleAsync(settings.DefaultLanguage, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Enrichment cycle failed ({Reason})", ex.GetBaseException().Message);
            }

            await Task.Delay(CycleDelay, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    /// <summary>Exposed for tests: one enrichment pass over the pending batch.</summary>
    public async Task RunCycleAsync(string language, CancellationToken ct)
    {
        List<Track> batch;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff = timeProvider.GetUtcNow().UtcDateTime - RetryCoolDown;
            batch = await db.Tracks.AsNoTracking()
                .Where(t => t.Source != TrackSource.Generated
                    && !t.FileMissing
                    && t.MetadataStatus == MetadataStatus.LocalOnly
                    && (t.LastEnrichmentAttemptUtc == null || t.LastEnrichmentAttemptUtc < cutoff))
                .OrderBy(t => t.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        if (batch.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var musicBrainz = scope.ServiceProvider.GetRequiredService<IMusicBrainzClient>();
        var wikidata = scope.ServiceProvider.GetRequiredService<WikidataClient>();
        var wikipedia = scope.ServiceProvider.GetRequiredService<IWikipediaClient>();
        var digestWriter = scope.ServiceProvider.GetRequiredService<KnowledgeDigestWriter>();

        var changed = false;
        foreach (var track in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnrichTrackAsync(track, musicBrainz, wikidata, wikipedia, digestWriter, language, ct);
                changed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Enrichment failed for \"{Title}\" ({Reason}) — staying LocalOnly",
                    track.Title, ex.GetBaseException().Message);
                await StampAttemptAsync(track.Id, ct);
            }
        }

        if (changed)
        {
            await publisher.PublishArchiveChangedAsync(ct);
        }
    }

    private async Task EnrichTrackAsync(
        Track track,
        IMusicBrainzClient musicBrainz,
        WikidataClient wikidata,
        IWikipediaClient wikipedia,
        KnowledgeDigestWriter digestWriter,
        string language,
        CancellationToken ct)
    {
        var evidence = BuildEvidence(track);
        var candidates = await musicBrainz.SearchRecordingsAsync(evidence, ct);
        var scored = candidates
            .Select(candidate => (Candidate: candidate, Match: MetadataMatchScorer.Score(evidence, candidate)))
            .OrderByDescending(pair => pair.Match.Score)
            .ToList();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tracked = await db.Tracks.FirstOrDefaultAsync(t => t.Id == track.Id, ct);
        if (tracked is null || tracked.MetadataStatus != MetadataStatus.LocalOnly)
        {
            return; // reviewed/changed while we were fetching
        }

        tracked.LastEnrichmentAttemptUtc = now;
        if (scored.Count == 0)
        {
            tracked.MetadataStatus = MetadataStatus.NeedsReview;
            await db.SaveChangesAsync(ct);
            return;
        }

        var (best, bestMatch) = scored[0];
        var status = MetadataMatchScorer.Classify(bestMatch);
        tracked.MetadataStatus = status;
        tracked.MetadataConfidence = bestMatch.Score;

        // Record the original file identity once, so any applied match stays undoable.
        AddClaimIfMissing(db, tracked.Id, "Title", track.Title, "FileTags", MetadataLicenseClass.FileProvided, now);
        AddClaimIfMissing(db, tracked.Id, "Artist", track.ImportedArtist, "FileTags", MetadataLicenseClass.FileProvided, now);
        AddClaimIfMissing(db, tracked.Id, "Album", track.ImportedAlbum, "FileTags", MetadataLicenseClass.FileProvided, now);

        switch (status)
        {
            case MetadataStatus.AutoMatched or MetadataStatus.Matched:
                ApplyCandidate(db, tracked, best, bestMatch, now,
                    accepted: status == MetadataStatus.AutoMatched);
                break;
            case MetadataStatus.Ambiguous:
                // Every plausible sibling goes to review: the ambiguous band plus
                // near-ties just under it (they are exactly what made it ambiguous).
                foreach (var (candidate, match) in scored
                    .Where(pair => pair.Match.Score >= MetadataMatchScorer.AmbiguousThreshold
                        || pair.Match.Score >= bestMatch.Score - 0.1)
                    .Take(5))
                {
                    db.MetadataCandidates.Add(ToCandidateRow(tracked.Id, candidate, match, now, CandidateStatus.Pending));
                }

                break;
        }

        await db.SaveChangesAsync(ct);

        // Knowledge gathering only for trustworthy artist identities.
        if (status is MetadataStatus.AutoMatched or MetadataStatus.Matched
            && !string.IsNullOrWhiteSpace(best.ArtistId))
        {
            await GatherArtistKnowledgeAsync(
                tracked.Id, best.Artist, best.ArtistId!, musicBrainz, wikidata, wikipedia, digestWriter, language, ct);
        }
    }

    private void ApplyCandidate(
        RadioDbContext db, Track tracked, RecordingCandidate best, MatchScore match, DateTime now, bool accepted)
    {
        tracked.Title = best.Title;
        tracked.ImportedArtist = string.IsNullOrWhiteSpace(best.Artist) ? tracked.ImportedArtist : best.Artist;
        tracked.ImportedAlbum = best.Album ?? tracked.ImportedAlbum;
        tracked.ImportedYear = best.Year ?? tracked.ImportedYear;

        AddClaim(db, tracked.Id, "Title", best.Title, "MusicBrainz", MetadataLicenseClass.CC0, now, best.RecordingId, match.Score, applied: true);
        AddClaim(db, tracked.Id, "Artist", best.Artist, "MusicBrainz", MetadataLicenseClass.CC0, now, best.ArtistId, match.Score, applied: true);
        if (best.Album is not null)
        {
            AddClaim(db, tracked.Id, "Album", best.Album, "MusicBrainz", MetadataLicenseClass.CC0, now, best.RecordingId, match.Score, applied: true);
        }

        db.ExternalIds.Add(NewExternalId(tracked.Id, "MusicBrainz", "Recording", best.RecordingId, match.Score, now));
        if (!string.IsNullOrWhiteSpace(best.ArtistId))
        {
            db.ExternalIds.Add(NewExternalId(tracked.Id, "MusicBrainz", "Artist", best.ArtistId!, match.Score, now));
        }

        db.MetadataCandidates.Add(ToCandidateRow(
            tracked.Id, best, match, now, accepted ? CandidateStatus.Accepted : CandidateStatus.Pending));
    }

    private async Task GatherArtistKnowledgeAsync(
        Guid trackId,
        string artistName,
        string artistMbid,
        IMusicBrainzClient musicBrainz,
        WikidataClient wikidata,
        IWikipediaClient wikipedia,
        KnowledgeDigestWriter digestWriter,
        string language,
        CancellationToken ct)
    {
        var qid = await musicBrainz.GetArtistWikidataQidAsync(artistMbid, ct);
        if (qid is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.ExternalIds.AnyAsync(
            e => e.OwnerType == MetadataOwnerType.Track && e.OwnerId == trackId
                && e.Source == "Wikidata" && e.Value == qid, ct))
        {
            db.ExternalIds.Add(NewExternalId(trackId, "Wikidata", "Qid", qid, 1.0, now));
            await db.SaveChangesAsync(ct);
        }

        var existing = await db.KnowledgeEntries
            .FirstOrDefaultAsync(e => e.Source == "Wikidata" && e.SourceEntityId == qid, ct);
        if (existing is not null && (existing.ExpiresAt is null || existing.ExpiresAt > now))
        {
            return; // fresh — the DB is the cache
        }

        var artistFacts = await wikidata.GetArtistFactsAsync(qid, language, ct);
        if (artistFacts is null)
        {
            return;
        }

        var facts = new Dictionary<string, string>();
        if (artistFacts.FormedYear is { } formed)
        {
            facts["Formed"] = formed.ToString();
        }

        if (artistFacts.DissolvedYear is { } dissolved)
        {
            facts["Dissolved"] = dissolved.ToString();
        }

        if (!string.IsNullOrWhiteSpace(artistFacts.Description))
        {
            facts["Description"] = artistFacts.Description;
        }

        var labelQids = artistFacts.GenreQids.ToList();
        if (artistFacts.OriginLabelQid is { } origin)
        {
            labelQids.Add(origin);
        }
        var labels = labelQids.Count > 0
            ? await wikidata.GetLabelsAsync(labelQids, language, ct)
            : new Dictionary<string, string>();
        var genres = artistFacts.GenreQids
            .Select(g => labels.TryGetValue(g, out var label) ? label : null)
            .Where(label => label is not null)
            .ToList();
        if (genres.Count > 0)
        {
            facts["Genres"] = string.Join(", ", genres);
        }

        if (artistFacts.OriginLabelQid is { } originQid && labels.TryGetValue(originQid, out var originLabel))
        {
            facts["Origin"] = originLabel;
        }

        string? summary = null;
        if (artistFacts.WikipediaTitle is { } wikiTitle)
        {
            summary = await wikipedia.GetSummaryAsync(wikiTitle, artistFacts.WikipediaLanguage ?? language, ct);
        }

        var digest = await digestWriter.WriteAsync(artistFacts.Name ?? artistName, facts, summary, ct);
        if (digest is null && facts.Count == 0)
        {
            return;
        }

        var refreshDays = Math.Max(1, metadataOptions.Value.KnowledgeRefreshDays);
        if (existing is null)
        {
            db.KnowledgeEntries.Add(new KnowledgeEntry
            {
                Id = Guid.NewGuid(),
                EntityKind = "artist",
                DisplayName = artistFacts.Name ?? artistName,
                Source = "Wikidata",
                SourceEntityId = qid,
                FactsJson = JsonSerializer.Serialize(facts),
                Digest = digest ?? string.Empty,
                LicenseClass = MetadataLicenseClass.CC0,
                RetrievedAt = now,
                ExpiresAt = now.AddDays(refreshDays),
            });
        }
        else
        {
            existing.DisplayName = artistFacts.Name ?? artistName;
            existing.FactsJson = JsonSerializer.Serialize(facts);
            existing.Digest = digest ?? existing.Digest;
            existing.RetrievedAt = now;
            existing.ExpiresAt = now.AddDays(refreshDays);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Knowledge gathered for {Artist} ({Qid})", artistFacts.Name ?? artistName, qid);
    }

    private TrackMatchEvidence BuildEvidence(Track track)
    {
        // Tags carry anchors (ISRC, embedded MBIDs) that the Track row doesn't store.
        var absolute = MediaPaths.ResolveAbsolute(radioOptions.Value.DataRoot, track.FilePath);
        var tags = File.Exists(absolute) ? tagReader.Read(absolute) : new Core.Metadata.FileTags();
        return new TrackMatchEvidence(
            Title: track.Title,
            Artist: track.ImportedArtist,
            Album: track.ImportedAlbum,
            TrackNumber: tags.TrackNumber,
            Year: track.ImportedYear,
            DurationSeconds: track.DurationSeconds > 0 ? track.DurationSeconds : tags.DurationSeconds,
            Isrc: tags.Isrc,
            MusicBrainzRecordingId: tags.MusicBrainzRecordingId);
    }

    private async Task StampAttemptAsync(Guid trackId, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Tracks
                .Where(t => t.Id == trackId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.LastEnrichmentAttemptUtc, timeProvider.GetUtcNow().UtcDateTime), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to stamp enrichment attempt for {TrackId}", trackId);
        }
    }

    private static ExternalId NewExternalId(
        Guid trackId, string source, string entityType, string value, double confidence, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        OwnerType = MetadataOwnerType.Track,
        OwnerId = trackId,
        Source = source,
        EntityType = entityType,
        Value = value,
        Confidence = confidence,
        CreatedAt = now,
    };

    private static MetadataCandidate ToCandidateRow(
        Guid trackId, RecordingCandidate candidate, MatchScore match, DateTime now, CandidateStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TrackId = trackId,
        Source = "MusicBrainz",
        SourceEntityId = candidate.RecordingId,
        DisplayTitle = candidate.Title,
        DisplayArtist = candidate.Artist,
        DisplayAlbum = candidate.Album,
        DisplayYear = candidate.Year,
        ArtistEntityId = candidate.ArtistId,
        Score = match.Score,
        ReasonsJson = JsonSerializer.Serialize(match.Reasons),
        Status = status,
        CreatedAt = now,
    };

    private static void AddClaim(
        RadioDbContext db, Guid trackId, string field, string value, string source,
        MetadataLicenseClass licenseClass, DateTime now, string? sourceEntityId = null,
        double confidence = 1.0, bool applied = false)
        => db.MetadataClaims.Add(new MetadataClaim
        {
            Id = Guid.NewGuid(),
            OwnerType = MetadataOwnerType.Track,
            OwnerId = trackId,
            FieldName = field,
            Value = value,
            Source = source,
            SourceEntityId = sourceEntityId,
            LicenseClass = licenseClass,
            Confidence = confidence,
            IsApplied = applied,
            CreatedAt = now,
        });

    private static void AddClaimIfMissing(
        RadioDbContext db, Guid trackId, string field, string? value, string source,
        MetadataLicenseClass licenseClass, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var exists = db.MetadataClaims.Local.Any(c => c.OwnerId == trackId && c.FieldName == field && c.Source == source)
            || db.MetadataClaims.Any(c => c.OwnerType == MetadataOwnerType.Track
                && c.OwnerId == trackId && c.FieldName == field && c.Source == source);
        if (!exists)
        {
            AddClaim(db, trackId, field, value, source, licenseClass, now);
        }
    }
}
