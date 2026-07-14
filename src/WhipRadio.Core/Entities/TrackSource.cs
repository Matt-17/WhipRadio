namespace WhipRadio.Core.Entities;

/// <summary>
/// Where a track's audio came from. Imported music (uploaded or from a
/// read-only external folder) may be copyrighted and is administrated
/// separately from the station's own generated songs: external files are
/// never deleted or modified on disk, uploads live under the data root and
/// can be removed from the Archive page.
/// </summary>
public enum TrackSource
{
    /// <summary>Produced by a music studio (musicgen/ace-step/...).</summary>
    Generated = 0,

    /// <summary>Uploaded through the Archive page; stored under the data root, deletable.</summary>
    Uploaded = 1,

    /// <summary>Found in a configured external music folder; the file is read-only.</summary>
    External = 2,
}
