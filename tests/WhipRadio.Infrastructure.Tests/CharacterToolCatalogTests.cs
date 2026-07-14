using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Prompting;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class CharacterToolCatalogTests
{
    [TestMethod]
    public void GetTools_NonChatScopes_OfferNothingForAnyRole()
    {
        var catalog = CreateCatalog();

        foreach (PromptScope scope in Enum.GetValues<PromptScope>())
        {
            if (scope == PromptScope.Chat)
            {
                continue;
            }

            foreach (CharacterRole role in Enum.GetValues<CharacterRole>())
            {
                Assert.Empty(catalog.GetTools(scope, role));
            }
        }
    }

    [TestMethod]
    public void GetTool_ReturnsAvailableToolByName()
    {
        var catalog = CreateCatalog();

        var tool = catalog.GetTool("searchmusic", PromptScope.Chat, CharacterRole.Host);

        Assert.NotNull(tool);
        Assert.Equal("SearchMusic", tool!.Definition.Name);
    }

    [TestMethod]
    public void GetTool_NonChatScope_ReturnsNull()
    {
        var catalog = CreateCatalog();

        var tool = catalog.GetTool("searchmusic", PromptScope.ProgramDirector, CharacterRole.Host);

        Assert.Null(tool);
    }

    [TestMethod]
    public void GetTools_ChatHost_IncludesHostChatTools()
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
    }

    [TestMethod]
    public void GetTools_ChatDirector_IncludesDirectorTools()
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
            new MessageTool(),
            new AnnouncementTool(),
            new SearchMusicTool(),
            new PlanFormatTool(),
            new HireHostTool(),
            new AssignHostTool(),
            new StatusReportTool(),
            new InviteTool(),
            new RemoveFromChannelTool(),
            new MakeSongTool(),
            new BriefPodcastTool(),
            new LookupKnowledgeTool(),
        ]);
}
