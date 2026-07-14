namespace WhipRadio.Core.Entities.Metadata;

/// <summary>
/// Cached factual knowledge about a real-world entity (Phase 6/6a): structured
/// facts plus a compact paraphrased digest for prompt context. Never stores
/// article prose or lyrics — the digest is generated from facts, and hosts
/// paraphrase it again on air. The DB is the cache: entries refresh after
/// <see cref="ExpiresAt"/>, keeping the station offline-friendly in between.
/// </summary>
public class KnowledgeEntry
{
    public Guid Id { get; set; }

    /// <summary>"artist" (v1) — recordings/releases can join later.</summary>
    public string EntityKind { get; set; } = "artist";

    /// <summary>Human name used for lookups ("Massive Attack").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Wikidata" (facts) — the digest may also draw on a Wikipedia summary.</summary>
    public string Source { get; set; } = "Wikidata";

    /// <summary>QID / source key; unique together with <see cref="Source"/>.</summary>
    public string SourceEntityId { get; set; } = string.Empty;

    /// <summary>Structured facts as JSON (origin, formed, genres, members, ...).</summary>
    public string FactsJson { get; set; } = "{}";

    /// <summary>3–6 short paraphrased fact sentences for prompt context.</summary>
    public string Digest { get; set; } = string.Empty;

    public MetadataLicenseClass LicenseClass { get; set; } = MetadataLicenseClass.CC0;

    public DateTime RetrievedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
