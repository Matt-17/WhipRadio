using WhipRadio.Core.Playout;

namespace WhipRadio.Core.Tests;

[TestClass]
public class PodcastShowSchedulerTests
{
    // Wednesday 2026-07-08 10:00 +02:00
    private static readonly DateTimeOffset WednesdayMorning =
        new(2026, 7, 8, 10, 0, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public void NextOccurrence_LaterSameDay()
    {
        var next = PodcastShowScheduler.NextOccurrence(WednesdayMorning, dayOfWeek: 3, startMinute: 21 * 60);
        Assert.Equal(new DateTimeOffset(2026, 7, 8, 21, 0, 0, TimeSpan.FromHours(2)), next);
    }

    [TestMethod]
    public void NextOccurrence_ExactSlotStart_ReturnsNow()
    {
        var slotStart = new DateTimeOffset(2026, 7, 8, 21, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(slotStart, PodcastShowScheduler.NextOccurrence(slotStart, 3, 21 * 60));
    }

    [TestMethod]
    public void NextOccurrence_EarlierToday_WrapsToNextWeek()
    {
        var next = PodcastShowScheduler.NextOccurrence(WednesdayMorning, dayOfWeek: 3, startMinute: 8 * 60);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.FromHours(2)), next);
    }

    [TestMethod]
    public void NextOccurrence_OtherWeekday()
    {
        // Sunday (0) from a Wednesday → 4 days ahead.
        var next = PodcastShowScheduler.NextOccurrence(WednesdayMorning, dayOfWeek: 0, startMinute: 9 * 60);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.FromHours(2)), next);
    }

    [TestMethod]
    public void IsOccurrenceAt_MatchesExactSlotMinuteOnly()
    {
        var slotStart = new DateTimeOffset(2026, 7, 8, 21, 0, 0, TimeSpan.FromHours(2));
        Assert.True(PodcastShowScheduler.IsOccurrenceAt(slotStart, 3, 21 * 60));
        Assert.False(PodcastShowScheduler.IsOccurrenceAt(slotStart.AddMinutes(1), 3, 21 * 60));
        Assert.False(PodcastShowScheduler.IsOccurrenceAt(slotStart.AddDays(1), 3, 21 * 60));
    }

    [TestMethod]
    public void Normalizers_ClampEpisodeAndSlotMinutes()
    {
        Assert.Equal(20, PodcastShowScheduler.NormalizeEpisodeMinutes(0));
        Assert.Equal(10, PodcastShowScheduler.NormalizeEpisodeMinutes(5));
        Assert.Equal(30, PodcastShowScheduler.NormalizeEpisodeMinutes(90));
        Assert.Equal(30, PodcastShowScheduler.NormalizeSlotMinutes(10, episodeMinutes: 20));
        Assert.Equal(60, PodcastShowScheduler.NormalizeSlotMinutes(60, episodeMinutes: 20));
        Assert.Equal(240, PodcastShowScheduler.NormalizeSlotMinutes(999, episodeMinutes: 20));
    }
}
