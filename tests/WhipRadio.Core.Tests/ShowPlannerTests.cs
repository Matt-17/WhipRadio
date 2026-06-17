using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class ShowPlannerTests
{
    private static ShowPlannerInput Input(
        int queueCount = 0,
        int maxQueueDepth = 2,
        bool trackAvailable = true,
        int tracksSinceAnnouncement = 0,
        int announcementEveryNTracks = 1)
        => new(queueCount, maxQueueDepth, trackAvailable, tracksSinceAnnouncement, announcementEveryNTracks);

    [TestMethod]
    public void FullQueue_Waits()
    {
        Assert.Equal(ShowAction.Wait, ShowPlanner.Decide(Input(queueCount: 2)));
    }

    [TestMethod]
    public void OverfullQueue_Waits()
    {
        Assert.Equal(ShowAction.Wait, ShowPlanner.Decide(Input(queueCount: 5)));
    }

    [TestMethod]
    public void NoTrackAvailable_ProducesFillerTalk()
    {
        Assert.Equal(ShowAction.EnqueueFillerTalk, ShowPlanner.Decide(Input(trackAvailable: false)));
    }

    [TestMethod]
    public void FullQueueWithoutTracks_StillWaits()
    {
        // Queue depth wins over cold start: don't pile up filler talk.
        Assert.Equal(ShowAction.Wait, ShowPlanner.Decide(Input(queueCount: 2, trackAvailable: false)));
    }

    [TestMethod]
    public void DefaultSettings_AnnounceEveryTrack()
    {
        Assert.Equal(
            ShowAction.EnqueueTrackWithIntro,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 0, announcementEveryNTracks: 1)));
    }

    [TestMethod]
    public void EveryThirdTrack_FirstTwoPlayWithoutIntro()
    {
        Assert.Equal(
            ShowAction.EnqueueTrackOnly,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 0, announcementEveryNTracks: 3)));
        Assert.Equal(
            ShowAction.EnqueueTrackOnly,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 1, announcementEveryNTracks: 3)));
        Assert.Equal(
            ShowAction.EnqueueTrackWithIntro,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 2, announcementEveryNTracks: 3)));
    }

    [TestMethod]
    public void AnnouncementsDisabled_AlwaysTrackOnly()
    {
        Assert.Equal(
            ShowAction.EnqueueTrackOnly,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 99, announcementEveryNTracks: 0)));
    }

    [TestMethod]
    public void PriorityTalk_ForcesIntroEvenBeforeCadence()
    {
        Assert.Equal(
            ShowAction.EnqueueTrackWithIntro,
            ShowPlanner.Decide(Input(tracksSinceAnnouncement: 0, announcementEveryNTracks: 3) with
            {
                PriorityTalkPending = true,
            }));
    }

    [TestMethod]
    public void PriorityTalk_OverridesDisabledAnnouncements()
    {
        // A queued listener greeting airs even when regular announcements are off.
        Assert.Equal(
            ShowAction.EnqueueTrackWithIntro,
            ShowPlanner.Decide(Input(announcementEveryNTracks: 0) with { PriorityTalkPending = true }));
    }

    [TestMethod]
    public void PriorityTalk_DoesNotOverrideFullQueue()
    {
        Assert.Equal(
            ShowAction.Wait,
            ShowPlanner.Decide(Input(queueCount: 2) with { PriorityTalkPending = true }));
    }
}
