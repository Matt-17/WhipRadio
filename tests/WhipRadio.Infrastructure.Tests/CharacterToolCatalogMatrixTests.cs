using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Prompting;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class CharacterToolCatalogMatrixTests
{
    private static CharacterToolCatalog FullCatalog() => new(
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
        new InviteTool(),
        new RemoveFromChannelTool(),
        new StartTalkBreakTool(),
        new RememberTool(),
        new RequestBitTool(),
        new NoOpTool(),
    ]);

    private static IReadOnlyList<string> ChatTools(CharacterRole role)
        => FullCatalog().GetTools(PromptScope.Chat, role).Select(tool => tool.Name).ToList();

    [TestMethod]
    public void Chat_DirectorGetsFullControlVerbSet()
    {
        var tools = ChatTools(CharacterRole.ProgramDirector);
        Assert.Contains("Message", tools);
        Assert.Contains("SearchMusic", tools);
        Assert.Contains("PlanFormat", tools);
        Assert.Contains("HireHost", tools);
        Assert.Contains("AssignHost", tools);
        Assert.Contains("StatusReport", tools);
        Assert.Contains("Invite", tools);
        Assert.Contains("RemoveFromChannel", tools);
        Assert.DoesNotContain("Announce", tools);
    }

    [TestMethod]
    public void Chat_HostGetsMessageAnnouncementAndSearch()
    {
        var tools = ChatTools(CharacterRole.Host);
        Assert.Contains("Message", tools);
        Assert.Contains("Announcement", tools);
        Assert.Contains("SearchMusic", tools);
        Assert.DoesNotContain("PlanFormat", tools);
        Assert.DoesNotContain("Invite", tools);
    }

    [TestMethod]
    public void Chat_ArtistAndGuestHaveNoStationVerbs()
    {
        foreach (CharacterRole role in new[] { CharacterRole.Artist, CharacterRole.Guest })
        {
            var tools = ChatTools(role);
            Assert.DoesNotContain("Message", tools);
            Assert.DoesNotContain("Announcement", tools);
            Assert.DoesNotContain("SearchMusic", tools);
            Assert.DoesNotContain("PlanFormat", tools);
            Assert.DoesNotContain("HireHost", tools);
            Assert.DoesNotContain("Invite", tools);
            Assert.DoesNotContain("RemoveFromChannel", tools);
        }
    }

    [TestMethod]
    public void OnAirTools_AreNeverOfferedInChat()
    {
        foreach (CharacterRole role in Enum.GetValues<CharacterRole>())
        {
            var tools = ChatTools(role);
            Assert.DoesNotContain("Announce", tools);
            Assert.DoesNotContain("Play", tools);
            Assert.DoesNotContain("StartTalkBreak", tools);
            Assert.DoesNotContain("Remember", tools);
            Assert.DoesNotContain("NoOp", tools);
            Assert.DoesNotContain("RequestBit", tools);
        }
    }
}
