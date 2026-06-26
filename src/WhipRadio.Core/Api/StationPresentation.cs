namespace WhipRadio.Core.Api;

/// <summary>
/// The single derived state every "is the station on air?" surface should show.
/// Folds the three orthogonal inputs — the encoder lifecycle
/// (<see cref="StationStatusDto.Status"/>), the operator's On Air intent
/// (<c>PlayoutEnabled</c>), and whether anything is actually on air
/// (<c>NowPlaying</c>) — into one value so the header lamp and the admin badge
/// can never disagree.
/// </summary>
public enum StationPresentation
{
    /// <summary>Circuit breaker tripped — station parked until On Air is re-enabled.</summary>
    Offline,

    /// <summary>Encoder crashed and is backing off before the next restart.</summary>
    Reconnecting,

    /// <summary>Encoder healthy but the operator has playout switched off.</summary>
    OffAir,

    /// <summary>On air with a now-playing item.</summary>
    Live,

    /// <summary>On air, encoder healthy, but nothing playing yet.</summary>
    Standby,
}

public static class StationPresentationState
{
    /// <summary>
    /// Derives the combined station state. The encoder lifecycle wins first (both
    /// surfaces agree on Offline/Reconnecting), then the operator's On Air intent
    /// gates on/off air, then a now-playing item distinguishes the live glow from
    /// an idle-but-on-air standby.
    /// </summary>
    /// <param name="status"><see cref="StationStatusDto.Status"/> ("Online"/"Reconnecting"/"Offline"); null/unknown is treated as online.</param>
    /// <param name="playoutEnabled">The operator's On Air switch.</param>
    /// <param name="hasNowPlaying">Whether a now-playing item is on air.</param>
    public static StationPresentation Derive(string? status, bool playoutEnabled, bool hasNowPlaying)
    {
        if (Is(status, nameof(StationPresentation.Offline)))
        {
            return StationPresentation.Offline;
        }

        if (Is(status, nameof(StationPresentation.Reconnecting)))
        {
            return StationPresentation.Reconnecting;
        }

        if (!playoutEnabled)
        {
            return StationPresentation.OffAir;
        }

        return hasNowPlaying ? StationPresentation.Live : StationPresentation.Standby;
    }

    private static bool Is(string? status, string name)
        => string.Equals(status, name, StringComparison.OrdinalIgnoreCase);
}
