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

    private static Track NewTrackWithDuration(string genre, double durationSeconds)
    {
        var track = NewTrack(genre);
        track.DurationSeconds = durationSeconds;
        return track;
    }

    [TestMethod]
    public void Pick_DurationCap_FiltersToFittingTracks()
    {
        var shortTrack = NewTrackWithDuration("lofi", 120);
        var longTrack = NewTrackWithDuration("lofi", 280);
        var selection = SelectionSettings.Default with { MaxTrackDurationSeconds = 180 };

        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [shortTrack, longTrack], Context("lofi"), [], [], [],
                FormatSelectionRules.Default, selection, new Random(i));
            Assert.Equal(shortTrack.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_DurationCap_SoftRelaxesWhenNothingFits()
    {
        var longTrack = NewTrackWithDuration("lofi", 280);
        var selection = SelectionSettings.Default with { MaxTrackDurationSeconds = 180 };

        var picked = WeightedTrackSelector.Pick(
            [longTrack], Context("lofi"), [], [], [],
            FormatSelectionRules.Default, selection, Seeded);

        // Soft filter: never returns null just because of timing — the caller re-checks.
        Assert.Equal(longTrack.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_DurationCap_AppliesBeforeGenreChain()
    {
        // The fitting track has the wrong genre; the cap must still win because the
        // genre chain is a preference while the cap protects the package boundary.
        var fittingWrongGenre = NewTrackWithDuration("indie rock", 100);
        var overlongRightGenre = NewTrackWithDuration("lofi", 280);
        var selection = SelectionSettings.Default with { MaxTrackDurationSeconds = 180 };

        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [fittingWrongGenre, overlongRightGenre], Context("lofi"), [], [], [],
                FormatSelectionRules.Default, selection, new Random(i));
            Assert.Equal(fittingWrongGenre.Id, picked!.Id);
        }
    }

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

    [TestMethod]
    public void Pick_HardExcludesPreviousShowTracks()
    {
        var tracks = Enumerable.Range(0, 6).Select(_ => NewTrack("lofi")).ToList();
        var hardExcluded = new[] { tracks[0].Id, tracks[1].Id, tracks[2].Id, tracks[3].Id };
        var recentExcluded = new[] { tracks[0].Id };
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                tracks, Context("lofi"), hardExcluded, recentExcluded, [],
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.True(picked is not null && !hardExcluded.Contains(picked.Id));
        }
    }

    [TestMethod]
    public void Pick_RelaxesPreviousShowWindowWhenPoolEmpties()
    {
        // Only 3 tracks; all 3 are hard-excluded (played this/previous show), but
        // only track[0] is in the short recent window. Relaxation must drop the
        // previous-show layer and pick from tracks[1] or tracks[2] (not tracks[0]).
        var tracks = Enumerable.Range(0, 3).Select(_ => NewTrack("lofi")).ToList();
        var hardExcluded = tracks.Select(t => t.Id).ToList();
        var recentExcluded = new[] { tracks[0].Id };
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                tracks, Context("lofi"), hardExcluded, recentExcluded, [],
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.NotNull(picked);
            Assert.NotEqual(tracks[0].Id, picked!.Id); // short-recent always enforced
        }
    }

    [TestMethod]
    public void Pick_NeverRelaxesBelowRecentWindow()
    {
        // One track, excluded by both hard and recent -> no relaxation can save it.
        var track = NewTrack("lofi");
        var picked = WeightedTrackSelector.Pick(
            [track], Context("lofi"), [track.Id], [track.Id], [],
            FormatSelectionRules.Default, SelectionSettings.Default, Seeded);
        Assert.Null(picked);
    }

    [TestMethod]
    public void Pick_PreventsBackToBackArtist()
    {
        var artistA = Guid.NewGuid();
        var artistB = Guid.NewGuid();
        var a1 = NewTrack("lofi"); a1.ArtistId = artistA;
        var a2 = NewTrack("lofi"); a2.ArtistId = artistA;
        var b1 = NewTrack("lofi"); b1.ArtistId = artistB;
        var refs = new[] { new PlayedTrackRef(a1.Id, artistA, "lofi hip hop", DateTime.UtcNow) };
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [a1, a2, b1], Context("lofi"), [], [], refs,
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.Equal(b1.Id, picked!.Id); // artist A just played -> must pick B
        }
    }

    [TestMethod]
    public void Pick_ArtistCapLimitsPlaysPerLookback()
    {
        var artistA = Guid.NewGuid();
        var artistB = Guid.NewGuid();
        var a1 = NewTrack("lofi"); a1.ArtistId = artistA;
        var b1 = NewTrack("lofi"); b1.ArtistId = artistB;
        // Artist A already played twice in the lookback; cap=2 -> A rejected.
        var refs = new[]
        {
            new PlayedTrackRef(a1.Id, artistA, "lofi hip hop", DateTime.UtcNow),
            new PlayedTrackRef(a1.Id, artistA, "lofi hip hop", DateTime.UtcNow.AddMinutes(-5)),
        };
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [a1, b1], Context("lofi"), [], [], refs,
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.Equal(b1.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_SubgenreRotationAvoidsSameSubgenreBackToBack()
    {
        var techno = NewTrack("electronic"); techno.Subgenre = "techno";
        var trance = NewTrack("electronic"); trance.Subgenre = "trance";
        var refs = new[] { new PlayedTrackRef(techno.Id, null, "techno", DateTime.UtcNow) };
        // Context electronic/techno would normally lock to techno, but rotation
        // soft-prefers a different subgenre when one just played.
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [techno, trance], Context("electronic", "techno"), [], [], refs,
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.Equal(trance.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_SingleArtistFeatureNarrowsToFeaturedArtist()
    {
        var featured = Guid.NewGuid();
        var other = Guid.NewGuid();
        var f1 = NewTrack("lofi"); f1.ArtistId = featured;
        var f2 = NewTrack("lofi"); f2.ArtistId = featured;
        var o1 = NewTrack("lofi"); o1.ArtistId = other;
        var rules = new FormatSelectionRules { Mode = SelectionMode.SingleArtistFeature, FeaturedArtistId = featured };
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [f1, f2, o1], Context("lofi"), [], [], [], rules, SelectionSettings.Default, new Random(i));
            Assert.Equal(featured, picked!.ArtistId);
        }
    }

    [TestMethod]
    public void Pick_SingleArtistFeatureRelaxesToStandardWhenExhausted()
    {
        var featured = Guid.NewGuid();
        var other = Guid.NewGuid();
        var f1 = NewTrack("lofi"); f1.ArtistId = featured;
        var o1 = NewTrack("lofi"); o1.ArtistId = other;
        var rules = new FormatSelectionRules { Mode = SelectionMode.SingleArtistFeature, FeaturedArtistId = featured };
        // The only featured track is hard-excluded -> feature exhausts -> relax to StandardRotation -> pick o1.
        var picked = WeightedTrackSelector.Pick(
            [f1, o1], Context("lofi"), [f1.Id], [], [], rules, SelectionSettings.Default, Seeded);
        Assert.Equal(o1.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_DiversityDisabledFallsBackToLegacyBehavior()
    {
        var tracks = Enumerable.Range(0, 4).Select(_ => NewTrack("lofi")).ToList();
        Guid[] recent = [tracks[0].Id, tracks[1].Id, tracks[2].Id];
        var disabled = SelectionSettings.Disabled;
        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                tracks, Context("lofi"), recent, recent, [], FormatSelectionRules.Default, disabled, new Random(i));
            Assert.Equal(tracks[3].Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_FreeformSkipsGenreFilter()
    {
        var lofi = NewTrack("lofi");
        var rock = NewTrack("indie rock");
        var rules = new FormatSelectionRules { Mode = SelectionMode.Freeform };
        // Context is lofi but Freeform ignores genre -> both tracks are eligible.
        // Just assert it does not always pick lofi (genre filter would lock to lofi).
        var pickedLofi = 0;
        for (var i = 0; i < 40; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [lofi, rock], Context("lofi"), [], [], [], rules, SelectionSettings.Default, new Random(i));
            if (picked!.Id == lofi.Id) pickedLofi++;
        }
        Assert.True(pickedLofi < 40, "Freeform should not lock to the context genre like StandardRotation does.");
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

        public Task<IReadOnlyList<Guid>> GetTrackIdsPlayedSinceAsync(DateTime sinceUtc, int maxCount, CancellationToken ct)
            => Task.FromResult(recent);

        public Task<IReadOnlyList<PlayedTrackRef>> GetRecentPlayedRefsAsync(int count, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PlayedTrackRef>>([]);
    }
}
