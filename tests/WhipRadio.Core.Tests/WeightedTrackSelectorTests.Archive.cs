using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Core.Tests;

/// <summary>Selection behavior for imported real music (Phase 6a): no Artist
/// entity, artist cap keyed by the imported display artist.</summary>
[TestClass]
public class WeightedTrackSelectorArchiveTests
{
    private static Track ImportedTrack(string title, string? artist, string genre = "lofi")
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Genre = genre,
            Source = TrackSource.External,
            ImportedArtist = artist,
            HasVocals = true,
        };

    private static ShowContext Context(string genre = "lofi")
        => new(genre, "", new Moderator { Name = "Test Host" });

    [TestMethod]
    public void Pick_ImportedTrackWithoutArtistEntity_IsSelectable()
    {
        var imported = ImportedTrack("Teardrop", "Massive Attack");

        var picked = WeightedTrackSelector.Pick([imported], Context(), [], new Random(42));

        Assert.Equal(imported.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_SameImportedArtist_DoesNotPlayBackToBack()
    {
        var justPlayed = ImportedTrack("Teardrop", "Massive Attack");
        var sameArtist = ImportedTrack("Angel", "Massive Attack");
        var otherArtist = ImportedTrack("Roads", "Portishead");
        var refs = new List<PlayedTrackRef>
        {
            new(justPlayed.Id, null, "", DateTime.UtcNow, "Massive Attack"),
        };

        for (var i = 0; i < 20; i++)
        {
            var picked = WeightedTrackSelector.Pick(
                [sameArtist, otherArtist], Context(), [justPlayed.Id], [justPlayed.Id], refs,
                FormatSelectionRules.Default, SelectionSettings.Default, new Random(i));
            Assert.Equal(otherArtist.Id, picked!.Id);
        }
    }

    [TestMethod]
    public void Pick_ImportedArtistCap_SoftRelaxesWhenOnlyThatArtistRemains()
    {
        var justPlayed = ImportedTrack("Teardrop", "Massive Attack");
        var sameArtist = ImportedTrack("Angel", "Massive Attack");
        var refs = new List<PlayedTrackRef>
        {
            new(justPlayed.Id, null, "", DateTime.UtcNow, "Massive Attack"),
        };

        var picked = WeightedTrackSelector.Pick(
            [sameArtist], Context(), [justPlayed.Id], [justPlayed.Id], refs,
            FormatSelectionRules.Default, SelectionSettings.Default, new Random(42));

        Assert.Equal(sameArtist.Id, picked!.Id);
    }

    [TestMethod]
    public void Pick_ImportedGenre_FallsThroughWhenNothingMatchesTheFormat()
    {
        var imported = ImportedTrack("Roads", "Portishead", genre: "Trip-Hop");

        var picked = WeightedTrackSelector.Pick([imported], Context("lofi"), [], new Random(42));

        Assert.Equal(imported.Id, picked!.Id);
    }
}
