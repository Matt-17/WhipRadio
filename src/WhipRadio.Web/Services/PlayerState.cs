using WhipRadio.Core.Api;

namespace WhipRadio.Web.Services;

/// <summary>
/// Per-circuit footer-player mode: live radio (default) or a library track
/// preview. Pages request a track via <see cref="PlayTrack"/>; the footer
/// player listens to <see cref="Changed"/> and switches its UI/audio.
/// </summary>
public sealed record VoicePreview(string Title, string Url, double DurationSeconds);

public sealed record JinglePreview(JingleDto Jingle, string Url);

public sealed record AnnouncementPreview(Guid Id, string Title, string Url, double DurationSeconds);

public class PlayerState
{
    /// <summary>Track preview mode (library). Null unless previewing a track.</summary>
    public TrackDto? CurrentTrack { get; private set; }

    /// <summary>Designed-voice preview mode (hosts page). Null unless previewing a voice.</summary>
    public VoicePreview? CurrentVoice { get; private set; }

    /// <summary>Station jingle preview mode (branding page). Null unless previewing a jingle.</summary>
    public JinglePreview? CurrentJingle { get; private set; }

    /// <summary>Produced talk/news preview mode. Null unless previewing an announcement.</summary>
    public AnnouncementPreview? CurrentAnnouncement { get; private set; }

    public bool IsLive => CurrentTrack is null
        && CurrentVoice is null
        && CurrentJingle is null
        && CurrentAnnouncement is null;

    public event Action? Changed;

    public void PlayTrack(TrackDto track)
    {
        CurrentTrack = track;
        CurrentVoice = null;
        CurrentJingle = null;
        CurrentAnnouncement = null;
        Changed?.Invoke();
    }

    public void PlayVoice(string title, string url, double durationSeconds)
    {
        CurrentVoice = new VoicePreview(title, url, durationSeconds);
        CurrentTrack = null;
        CurrentJingle = null;
        CurrentAnnouncement = null;
        Changed?.Invoke();
    }

    public void PlayJingle(JingleDto jingle, string url)
    {
        CurrentJingle = new JinglePreview(jingle, url);
        CurrentTrack = null;
        CurrentVoice = null;
        CurrentAnnouncement = null;
        Changed?.Invoke();
    }

    public void PlayAnnouncement(Guid id, string title, string url, double durationSeconds)
    {
        CurrentAnnouncement = new AnnouncementPreview(id, title, url, durationSeconds);
        CurrentTrack = null;
        CurrentVoice = null;
        CurrentJingle = null;
        Changed?.Invoke();
    }

    public void BackToLive()
    {
        CurrentTrack = null;
        CurrentVoice = null;
        CurrentJingle = null;
        CurrentAnnouncement = null;
        Changed?.Invoke();
    }
}
