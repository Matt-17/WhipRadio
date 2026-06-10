using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Tests;

public class WeightedTrackSelectorTests
{
    private static readonly Random Seeded = new(42);

    private static Track NewTrack(string genre, bool hasVocals = false, bool retired = false)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = $"{genre} song",
            Genre = genre,
            HasVocals = hasVocals,
            IsRetired = retired,
        };

    private static ScheduleSlot Slot(string genre) => new() { HourOfDay = 10, Genre = genre };

    private static Moderator Host(bool? prefersVocals = null) => new() { Name = "Test Host", PrefersVocals = prefersVocals };

    [Fact]
    public void Pick_EmptyLibrary_ReturnsNull()
    {
        var result = WeightedTrackSelector.Pick([], Slot("lofi"), Host(), [], Seeded);
        Assert.Null(result);
    }

    [Fact]
    public void Pick_PrefersMatchingGenre()
    {
        var lofi = NewTrack("lofi");
        var rock = NewTrack("indie rock");
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick([lofi, rock], Slot("lofi"), Host(), [], new Random(i));
            Assert.Equal(lofi.Id, picked!.Id);
        }
    }

    [Fact]
    public void Pick_NoGenreMatch_FallsBackToAnyGenre()
    {
        var rock = NewTrack("indie rock");
        var picked = WeightedTrackSelector.Pick([rock], Slot("lofi"), Host(), [], Seeded);
        Assert.Equal(rock.Id, picked!.Id);
    }

    [Fact]
    public void Pick_RespectsVocalPreference()
    {
        var vocal = NewTrack("lofi", hasVocals: true);
        var instrumental = NewTrack("lofi", hasVocals: false);
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [vocal, instrumental], Slot("lofi"), Host(prefersVocals: true), [], new Random(i));
            Assert.Equal(vocal.Id, picked!.Id);
        }
    }

    [Fact]
    public void Pick_VocalPreferenceIsNoOpWhenNoVocalTracksExist()
    {
        var instrumental = NewTrack("lofi", hasVocals: false);
        var picked = WeightedTrackSelector.Pick(
            [instrumental], Slot("lofi"), Host(prefersVocals: true), [], Seeded);
        Assert.Equal(instrumental.Id, picked!.Id);
    }

    [Fact]
    public void Pick_ExcludesRecentlyPlayedTracks()
    {
        var tracks = Enumerable.Range(0, 4).Select(_ => NewTrack("lofi")).ToList();
        Guid[] recent = [tracks[0].Id, tracks[1].Id, tracks[2].Id];
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(tracks, Slot("lofi"), Host(), recent, new Random(i));
            Assert.Equal(tracks[3].Id, picked!.Id);
        }
    }

    [Fact]
    public void Pick_AllTracksRecentlyPlayed_ReturnsNull()
    {
        var track = NewTrack("lofi");
        var picked = WeightedTrackSelector.Pick([track], Slot("lofi"), Host(), [track.Id], Seeded);
        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesRetiredTracks()
    {
        var retired = NewTrack("lofi", retired: true);
        var active = NewTrack("lofi");
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick([retired, active], Slot("lofi"), Host(), [], new Random(i));
            Assert.Equal(active.Id, picked!.Id);
        }
    }

    [Fact]
    public async Task PickNextAsync_UsesRepositoryCandidatesAndRecentIds()
    {
        var fresh = NewTrack("lofi");
        var recent = NewTrack("lofi");
        var repository = new FakeTrackRepository([fresh, recent], [recent.Id]);
        var selector = new WeightedTrackSelector(repository, Seeded);

        var picked = await selector.PickNextAsync(Slot("lofi"), Host(), CancellationToken.None);

        Assert.Equal(fresh.Id, picked!.Id);
        Assert.Equal(WeightedTrackSelector.RecentExclusionCount, repository.RequestedRecentCount);
    }

    private sealed class FakeTrackRepository(IReadOnlyList<Track> candidates, IReadOnlyList<Guid> recent) : ITrackRepository
    {
        public int RequestedRecentCount { get; private set; }

        public Task<IReadOnlyList<Track>> GetCandidatesAsync(CancellationToken ct) => Task.FromResult(candidates);

        public Task<IReadOnlyList<Guid>> GetRecentlyPlayedTrackIdsAsync(int count, CancellationToken ct)
        {
            RequestedRecentCount = count;
            return Task.FromResult(recent);
        }
    }
}
