using WhipRadio.Core.Api;
using WhipRadio.Web.Components.Library;

namespace WhipRadio.Web.ComponentTests;

/// <summary>Direct tests for the components extracted from the Library page.</summary>
[TestClass]
public class LibraryComponentsTests : BunitContext
{
    private static ArtistDto Artist(string name = "The Nightdrivers", int trackCount = 3) => new(
        Guid.NewGuid(), name, "the-nightdrivers", "Electronic", "Synthwave",
        StyleDescriptor: "retro synths", TrackCount: trackCount, UpVotes: 4, DownVotes: 1, IsRetired: false);

    private static TrackDto TrackRow(string title) => new(
        Guid.NewGuid(), title, "Electronic", "Synthwave", "The Nightdrivers", Guid.NewGuid(),
        HasVocals: true, DurationSeconds: 200, PlayCount: 7, UpVotes: 2, DownVotes: 0,
        IsRetired: false, Backend: "ace-step-1.5", CreatedAt: DateTime.UtcNow);

    [TestMethod]
    public void ArtistRail_ShowsAllTracksEntry_PendingCreations_AndArtists()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());
        var pending = new PendingArtistCreation("dark techno duo") { Status = PendingArtistCreationStatus.Creating };

        var rail = Render<ArtistRail>(parameters => parameters
            .Add(p => p.Artists, [Artist()])
            .Add(p => p.ArtistFilter, _ => true)
            .Add(p => p.PendingCreations, [pending]));

        Assert.Contains("All tracks", rail.Markup);
        Assert.Contains("3 songs", rail.Markup);
        Assert.Contains("The Nightdrivers", rail.Markup);
        Assert.Contains("Creating artist...", rail.Markup);
        Assert.Contains("dark techno duo", rail.Markup);
    }

    [TestMethod]
    public void TrackTable_RendersRows_AndFiltersBySearch()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());
        List<TrackDto> tracks = [TrackRow("Midnight Drive"), TrackRow("Sunrise Fade")];

        var table = Render<TrackTable>(parameters => parameters
            .Add(p => p.Tracks, tracks)
            .Add(p => p.Search, "midnight"));

        Assert.Contains("Midnight Drive", table.Markup);
        Assert.DoesNotContain("Sunrise Fade", table.Markup);
        Assert.Contains("vocal", table.Markup);
    }

    [TestMethod]
    public void TrackTable_ShowsEmptyState_WhenNothingMatches()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var table = Render<TrackTable>(parameters => parameters
            .Add(p => p.Tracks, new List<TrackDto>())
            .Add(p => p.Search, "nope"));

        Assert.Contains("No records match your search.", table.Markup);
    }

    [TestMethod]
    public void ArtistDetailPanel_ShowsPressFileLoading_ThenBiography()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());
        var artist = Artist();

        var loading = Render<ArtistDetailPanel>(parameters => parameters
            .Add(p => p.Artist, artist));
        Assert.Contains("pulling the press file...", loading.Markup);

        var loaded = Render<ArtistDetailPanel>(parameters => parameters
            .Add(p => p.Artist, artist)
            .Add(p => p.Detail, artist with { Biography = "Formed in a basement studio." }));
        Assert.Contains("Formed in a basement studio.", loaded.Markup);
        Assert.Contains("Record new song", loaded.Markup);
        Assert.Contains("Redefine Artist", loaded.Markup);
    }
}
