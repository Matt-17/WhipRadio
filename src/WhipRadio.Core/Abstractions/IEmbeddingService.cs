namespace WhipRadio.Core.Abstractions;

/// <summary>Text-to-vector embedding for participant memory retrieval (Phase 5).</summary>
public interface IEmbeddingService
{
    /// <summary>Embeds one text. Throws when the embedding backend is unavailable —
    /// callers are failure-soft (memory retrieval is never worth blocking production).</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
