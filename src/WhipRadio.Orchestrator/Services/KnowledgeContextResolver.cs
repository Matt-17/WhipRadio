using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Entities.Metadata;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Resolves gathered knowledge digests for a conversation segment (Phase 6a):
/// artists behind the episode's referenced imported tracks, plus knowledge
/// entries whose display name appears in the topic/brief. Gated by the
/// station's knowledge setting and per-track metadata trust — ambiguous or
/// local-only tracks contribute nothing.
/// </summary>
public sealed class KnowledgeContextResolver(
    IDbContextFactory<RadioDbContext> dbFactory,
    StationSettingsCache settingsCache,
    ILogger<KnowledgeContextResolver> logger)
{
    public async Task<string?> ResolveForSegmentAsync(ConversationSegment segment, CancellationToken ct)
    {
        try
        {
            var settings = await settingsCache.GetAsync(ct);
            if (!settings.PodcastKnowledgeEnabled)
            {
                return null;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var lines = new List<string>();
            var seenQids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var trackId in ParseTrackIds(segment.ReferencedTrackIdsJson))
            {
                var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
                if (track is null || track.Source == TrackSource.Generated)
                {
                    continue;
                }

                var cautious = track.MetadataStatus switch
                {
                    MetadataStatus.Verified or MetadataStatus.AutoMatched => false,
                    MetadataStatus.Matched => true,
                    _ => (bool?)null,
                };
                if (cautious is null)
                {
                    continue; // Ambiguous/LocalOnly: no factual claims (§9)
                }

                var qids = await db.ExternalIds.AsNoTracking()
                    .Where(e => e.OwnerType == MetadataOwnerType.Track
                        && e.OwnerId == track.Id && e.Source == "Wikidata")
                    .Select(e => e.Value)
                    .ToListAsync(ct);
                foreach (var entry in await LoadEntriesAsync(db, qids, ct))
                {
                    if (seenQids.Add(entry.SourceEntityId))
                    {
                        lines.Add(Format(entry, cautious.Value));
                    }
                }
            }

            // Topic/brief name-dropping: an episode "about Massive Attack" gets
            // the artist's digest even without a referenced track.
            var text = $"{segment.Topic} {segment.Brief}";
            if (!string.IsNullOrWhiteSpace(text))
            {
                var named = await db.KnowledgeEntries.AsNoTracking()
                    .Where(e => e.Digest != "")
                    .Select(e => new { e.DisplayName, e.SourceEntityId, e.Digest })
                    .ToListAsync(ct);
                foreach (var entry in named)
                {
                    if (entry.DisplayName.Length >= 3
                        && text.Contains(entry.DisplayName, StringComparison.OrdinalIgnoreCase)
                        && seenQids.Add(entry.SourceEntityId))
                    {
                        lines.Add($"{entry.DisplayName}: {entry.Digest}");
                    }
                }
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Knowledge resolution failed for segment {Segment}", segment.Id);
            return null;
        }
    }

    private static async Task<List<KnowledgeEntry>> LoadEntriesAsync(
        RadioDbContext db, IReadOnlyList<string> qids, CancellationToken ct)
        => qids.Count == 0
            ? []
            : await db.KnowledgeEntries.AsNoTracking()
                .Where(e => qids.Contains(e.SourceEntityId) && e.Digest != "")
                .ToListAsync(ct);

    private static string Format(KnowledgeEntry entry, bool cautious)
        => cautious
            ? $"{entry.DisplayName}: {entry.Digest} (Match unconfirmed — keep factual claims light.)"
            : $"{entry.DisplayName}: {entry.Digest}";

    private static IReadOnlyList<Guid> ParseTrackIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
