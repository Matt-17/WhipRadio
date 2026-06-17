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

    private static CharacterToolCatalog CreateCatalog()
        => new(
        [
            new AnnounceTool(),
            new PlayTool(),
            new MessageTool(),
            new StartTalkBreakTool(),
            new RememberTool(),
            new RequestBitTool(),
            new NoOpTool(),
        ]);
}
