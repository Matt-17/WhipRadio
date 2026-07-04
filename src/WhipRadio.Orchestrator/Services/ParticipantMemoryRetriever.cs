using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Memory;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

/// <summary>
/// Read path of the thin retrieval memory (Phase 5): embeds the query, loads
/// the participant's newest memories, and returns the top-k by in-process
/// cosine similarity. Failure-soft: any backend trouble returns an empty list
/// so memory never blocks a chat turn or a production run.
/// </summary>
public sealed class ParticipantMemoryRetriever(
    IDbContextFactory<RadioDbContext> dbFactory,
    IEmbeddingService embeddings,
    ILogger<ParticipantMemoryRetriever> logger)
{
    /// <summary>Below this cosine similarity a memory is considered unrelated.</summary>
    public const double MinSimilarity = 0.35;

    public async Task<IReadOnlyList<string>> RetrieveAsync(
        string participantKey,
        string query,
        int k = 3,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(participantKey) || string.IsNullOrWhiteSpace(query) || k <= 0)
        {
            return [];
        }

        try
        {
            List<(string Content, float[] Embedding)> candidates;
            await using (RadioDbContext db = await dbFactory.CreateDbContextAsync(ct))
            {
                candidates = (await db.ParticipantMemories.AsNoTracking()
                        .Where(memory => memory.ParticipantKey == participantKey)
                        .OrderByDescending(memory => memory.CreatedAtUtc)
                        .Take(ParticipantMemoryWriter.MaxMemoriesPerParticipant)
                        .Select(memory => new { memory.Content, memory.Embedding })
                        .ToListAsync(ct))
                    .Select(row => (row.Content, row.Embedding))
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                return [];
            }

            float[] queryEmbedding = await embeddings.EmbedAsync(query, ct);
            IReadOnlyList<int> top = VectorMath.TopK(
                queryEmbedding,
                candidates.Select(candidate => candidate.Embedding).ToList(),
                k,
                MinSimilarity);
            return top.Select(index => candidates[index].Content).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Memory retrieval failed for {Key}; continuing without memories.", participantKey);
            return [];
        }
    }
}
