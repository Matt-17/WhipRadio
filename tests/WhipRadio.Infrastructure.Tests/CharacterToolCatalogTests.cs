using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Prompting;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class CharacterToolCatalogTests
{
    [TestMethod]
    public void GetTools_HostDecision_IncludesActionTools()
    {
        var catalog = CreateCatalog();

        var tools = catalog.GetTools(PromptScope.CharacterDecision, CharacterRole.Host);
        var names = tools.Select(tool => tool.Name).ToList();

        Assert.Contains("Announce", names);
        Assert.Contains("Play", names);
        Assert.Contains("StartTalkBreak", names);
        Assert.Contains("Remember", names);
        Assert.Contains("RequestBit", names);
        Assert.Contains("NoOp", names);
    }

    [TestMethod]
    public void GetTools_UtilityScope_HidesCharacterActions()
    {
        var catalog = CreateCatalog();

        var tools = catalog.GetTools(PromptScope.Utility, CharacterRole.System);

        Assert.Empty(tools);
    }

    [TestMethod]
    public void GetTool_ReturnsAvailableToolByName()
    {
        var catalog = CreateCatalog();

        var tool = catalog.GetTool("announce", PromptScope.CharacterDecision, CharacterRole.Host);

        Assert.NotNull(tool);
        Assert.Equal("Announce", tool!.Definition.Name);
    }

    [TestMethod]
    public void GetTools_ChatHost_IncludesHostChatToolsOnly()
    {
        var catalog = CreateCatalog();

        var names = catalog.GetTools(PromptScope.Chat, CharacterRole.Host)
            .Select(tool => tool.Name)
            .ToList();

        Assert.Contains("Message", names);
        Assert.Contains("Announcement", names);
        Assert.Contains("SearchMusic", names);
        Assert.DoesNotContain("PlanFormat", names);
        Assert.DoesNotContain("HireHost", names);
        Assert.DoesNotContain("AssignHost", names);
        Assert.DoesNotContain("StatusReport", names);
    }

    [TestMethod]
    public void GetTools_ChatDirector_IncludesDirectorToolsOnly()
    {
        var catalog = CreateCatalog();

        var names = catalog.GetTools(PromptScope.Chat, CharacterRole.ProgramDirector)
            .Select(tool => tool.Name)
            .ToList();

        Assert.Contains("Message", names);
        Assert.Contains("SearchMusic", names);
        Assert.Contains("PlanFormat", names);
        Assert.Contains("HireHost", names);
        Assert.Contains("AssignHost", names);
        Assert.Contains("StatusReport", names);
        Assert.DoesNotContain("Announcement", names);
    }

    private static CharacterToolCatalog CreateCatalog()
        => new(
        [
            new AnnounceTool(),
            new PlayTool(),
            new MessageTool(),
            new AnnouncementTool(),
            new SearchMusicTool(),
            new PlanFormatTool(),
            new HireHostTool(),
            new AssignHostTool(),
            new StatusReportTool(),
            new StartTalkBreakTool(),
            new RememberTool(),
            new RequestBitTool(),
            new NoOpTool(),
        ]);
}
