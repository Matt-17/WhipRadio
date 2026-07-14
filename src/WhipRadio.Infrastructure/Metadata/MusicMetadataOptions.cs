namespace WhipRadio.Infrastructure.Metadata;

/// <summary>
/// Endpoints and etiquette for the keyless open-metadata sources (Phase 6a).
/// No API keys, no accounts — MusicBrainz asks for a descriptive User-Agent
/// and at most one request per second, which the rate gate enforces.
/// </summary>
public class MusicMetadataOptions
{
    public const string SectionName = "MusicMetadata";

    public string MusicBrainzEndpoint { get; set; } = "https://musicbrainz.org";

    public string WikidataEndpoint { get; set; } = "https://www.wikidata.org";

    /// <summary>{lang} is replaced with the wiki language code.</summary>
    public string WikipediaEndpointTemplate { get; set; } = "https://{lang}.wikipedia.org";

    public string UserAgent { get; set; } = "WhipRadio/1.0 (https://github.com/whipradio; radio@localhost)";

    /// <summary>Minimum spacing between MusicBrainz request starts.</summary>
    public int MusicBrainzMinRequestIntervalMs { get; set; } = 1100;

    /// <summary>How long cached knowledge entries stay fresh.</summary>
    public int KnowledgeRefreshDays { get; set; } = 90;
}
