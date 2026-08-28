using WhipRadio.Web.Components.Pages;

namespace WhipRadio.Web.ComponentTests;

/// <summary>Render pin for the Library page before/after its component split.</summary>
[TestClass]
public class LibraryPageTests : BunitContext
{
    [TestMethod]
    public void RendersArtistRailAndTrackShell_WhenOrchestratorIsUnreachable()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var page = Render<Library>();

        Assert.Contains("Create Artist", page.Markup);
        Assert.Contains("Artists", page.Markup);
    }

    [TestMethod]
    public void CreateArtistButton_OpensTheCreateModal()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var page = Render<Library>();
        page.FindAll("button").First(b => b.TextContent.Contains("Create Artist")).Click();

        Assert.Contains("modal", page.Markup);
    }
}
