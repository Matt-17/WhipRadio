namespace WhipRadio.Core.Playout;

public enum ShowAction
{
    /// <summary>Queue is full enough — check again later.</summary>
    Wait,

    /// <summary>No playable track exists (cold start) — talk until music exists.</summary>
    EnqueueFillerTalk,

    /// <summary>Enqueue intro announcement (producing it synchronously if missing), then the track.</summary>
    EnqueueTrackWithIntro,

    /// <summary>Enqueue the track without an announcement.</summary>
    EnqueueTrackOnly,
}

public sealed record ShowPlannerInput(
    int QueueCount,
    int MaxQueueDepth,
    bool TrackAvailable,
    int TracksSinceAnnouncement,
    int AnnouncementEveryNTracks);

/// <summary>Pure decision logic for the ShowRunner loop (unit-testable, Plan.md §M6.6).</summary>
public static class ShowPlanner
{
    public static ShowAction Decide(ShowPlannerInput input)
    {
        if (input.QueueCount >= input.MaxQueueDepth)
        {
            return ShowAction.Wait;
        }

        if (!input.TrackAvailable)
        {
            return ShowAction.EnqueueFillerTalk;
        }

        var announcementsEnabled = input.AnnouncementEveryNTracks > 0;
        var announcementDue = announcementsEnabled
            && input.TracksSinceAnnouncement + 1 >= input.AnnouncementEveryNTracks;

        return announcementDue ? ShowAction.EnqueueTrackWithIntro : ShowAction.EnqueueTrackOnly;
    }
}
