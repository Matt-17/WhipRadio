using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit footer-player mode: live radio (default) or a library track
/// preview. Pages request a track via <see cref="PlayTrack"/>; the footer
/// player listens to <see cref="Changed"/> and switches its UI/audio.
/// </summary>
public class PlayerState
{
    /// <summary>Null = live radio mode.</summary>
    public TrackDto? CurrentTrack { get; private set; }

    public event Action? Changed;

    public void PlayTrack(TrackDto track)
    {
        CurrentTrack = track;
        Changed?.Invoke();
    }

    public void BackToLive()
    {
        CurrentTrack = null;
        Changed?.Invoke();
    }
}
