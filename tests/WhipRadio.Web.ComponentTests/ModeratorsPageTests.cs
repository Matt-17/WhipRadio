using WhipRadio.Web.Components.Pages;

namespace WhipRadio.Web.ComponentTests;

/// <summary>
/// Render pin for the Hosts page before/after its component split: the roster
/// panel and hire flow must survive an unreachable orchestrator, and the hire
/// modal must open with its form.
/// </summary>
[TestClass]
public class ModeratorsPageTests : BunitContext
{
    [TestMethod]
    public void RendersRosterShell_WhenOrchestratorIsUnreachable()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var page = Render<Moderators>();

        Assert.Contains("The Hosts", page.Markup);
        Assert.Contains("Roster", page.Markup);
        Assert.Contains("Hire Host", page.Markup);
    }

    [TestMethod]
    public void HireHostButton_OpensTheHireModal()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var page = Render<Moderators>();
        page.FindAll("button").First(b => b.TextContent.Contains("Hire Host")).Click();

        Assert.Contains("modal", page.Markup);
    }
}
