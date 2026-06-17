using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Tests;

[TestClass]
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

    private static ShowContext Context(string genre, string subgenre = "", bool? prefersVocals = null)
        => new(genre, subgenre, new Moderator { Name = "Test Host", PrefersVocals = prefersVocals });

    [TestMethod]
    public void Pick_EmptyLibrary_ReturnsNull()
    {
        var result = WeightedTrackSelector.Pick([], Context("lofi"), [], Seeded);
        Assert.Null(result);
    }

    [TestMethod]
    public void Pick_PrefersMatchingGenre()
    {
        var lofi = NewTrack("lofi");
        var rock = NewTrack("indie rock");
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick([lofi, rock], Context("lofi"), [], new Random(i));
            Assert.Equal(lofi.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_PrefersMatchingSubgenreWithinGenre()
    {
        var techno = NewTrack("electronic");
        techno.Subgenre = "techno";
        var trance = NewTrack("electronic");
        trance.Subgenre = "trance";
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [techno, trance], Context("electronic", "trance"), [], new Random(i));
            Assert.Equal(trance.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_NoGenreMatch_FallsBackToAnyGenre()
    {
        var rock = NewTrack("indie rock");
        var picked = WeightedTrackSelector.Pick([rock], Context("lofi"), [], Seeded);
        Assert.Equal(rock.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_RespectsVocalPreference()
    {
        var vocal = NewTrack("lofi", hasVocals: true);
        var instrumental = NewTrack("lofi", hasVocals: false);
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [vocal, instrumental], Context("lofi", prefersVocals: true), [], new Random(i));
            Assert.Equal(vocal.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_VocalPreferenceIsNoOpWhenNoVocalTracksExist()
    {
        var instrumental = NewTrack("lofi", hasVocals: false);
        var picked = WeightedTrackSelector.Pick(
            [instrumental], Context("lofi", prefersVocals: true), [], Seeded);
        Assert.Equal(instrumental.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_ExcludesRecentlyPlayedTracks()
    {
        var tracks = Enumerable.Range(0, 4).Select(_ => NewTrack("lofi")).ToList();
        Guid[] recent = [tracks[0].Id, tracks[1].Id, tracks[2].Id];
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(tracks, Context("lofi"), recent, new Random(i));
            Assert.Equal(tracks[3].Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_AllTracksRecentlyPlayed_ReturnsNull()
    {
        var track = NewTrack("lofi");
        var picked = WeightedTrackSelector.Pick([track], Context("lofi"), [track.Id], Seeded);
        Assert.Null(picked);
    }

    [TestMethod]
    public void Pick_ExcludesRetiredTracks()
    {
        var retired = NewTrack("lofi", retired: true);
        var active = NewTrack("lofi");
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick([retired, active], Context("lofi"), [], new Random(i));
            Assert.Equal(active.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void ComputeArtistFactors_ScaleWithNetVotes()
    {
        var lovedArtist = Guid.NewGuid();
        var hatedArtist = Guid.NewGuid();
        var loved = NewTrack("lofi");
        loved.ArtistId = lovedArtist;
        loved.UpVotes = 10;
        var hated = NewTrack("lofi");
        hated.ArtistId = hatedArtist;
        hated.DownVotes = 30;

        var factors = WeightedTrackSelector.ComputeArtistFactors([loved, hated]);

        Assert.Equal(1.5, factors[lovedArtist], precision: 10);
        Assert.Equal(0.25, factors[hatedArtist], precision: 10); // clamped floor
    }

    [TestMethod]
    public async Task PickNextAsync_UsesRepositoryCandidatesAndRecentIds()
    {
        var fresh = NewTrack("lofi");
        var recent = NewTrack("lofi");
        var repository = new FakeTrackRepository([fresh, recent], [recent.Id]);
        var selector = new WeightedTrackSelector(repository, Seeded);

        var picked = await selector.PickNextAsync(Context("lofi"), CancellationToken.None);

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
