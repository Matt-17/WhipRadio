using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Prompting;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class CharacterToolCatalogMatrixTests
{
    // Reflection so the matrix always reflects every registered tool: any new
    // ICharacterTool with a parameterless constructor is picked up automatically.
    private static CharacterToolCatalog FullCatalog()
    {
        var tools = typeof(MessageTool).Assembly.GetTypes()
            .Where(type => typeof(ICharacterTool).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (ICharacterTool)Activator.CreateInstance(type)!)
            .ToArray();
        return new CharacterToolCatalog(tools);
    }

    private static IReadOnlyList<string> ChatTools(CharacterRole role)
        => FullCatalog().GetTools(PromptScope.Chat, role).Select(tool => tool.Name).ToList();

    [TestMethod]
    public void Chat_DirectorGetsControlTools()
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
        Assert.Contains("MakeSong", tools);
        Assert.Contains("BriefPodcast", tools);
        Assert.Contains("SearchArtist", tools);
        Assert.Contains("GetArtistProfile", tools);
        Assert.Contains("QueueTrack", tools);
        Assert.Contains("PlanTalkBreak", tools);
        Assert.Contains("CreateTalkBit", tools);
        Assert.Contains("ProduceNewsPackage", tools);
        Assert.Contains("ProduceWeatherReport", tools);
        Assert.Contains("CreateJingle", tools);
        Assert.Contains("SetJingleActive", tools);
        Assert.Contains("SetNewsPresenter", tools);
        Assert.Contains("SetWeatherPresenter", tools);
        Assert.Contains("RetireTrack", tools);
        Assert.Contains("RequestSongFromArtist", tools);
        // The director commissions announcements through hosts, keeps its own
        // memory nowhere, and never posts to an artist feed.
        Assert.DoesNotContain("Announcement", tools);
        Assert.DoesNotContain("Remember", tools);
        Assert.DoesNotContain("PostArtistFeed", tools);
    }

    [TestMethod]
    public void Chat_HostGetsOnAirAndSelfServiceTools()
    {
        var tools = ChatTools(CharacterRole.Host);
        Assert.Contains("Message", tools);
        Assert.Contains("Announcement", tools);
        Assert.Contains("SearchMusic", tools);
        Assert.Contains("StatusReport", tools);
        Assert.Contains("SearchArtist", tools);
        Assert.Contains("GetArtistProfile", tools);
        Assert.Contains("QueueTrack", tools);
        Assert.Contains("PlanTalkBreak", tools);
        Assert.Contains("CreateTalkBit", tools);
        Assert.Contains("Remember", tools);
        Assert.Contains("RequestSongFromArtist", tools);
        // Personnel, scheduling, and destructive tools stay with the director.
        Assert.DoesNotContain("PlanFormat", tools);
        Assert.DoesNotContain("Invite", tools);
        Assert.DoesNotContain("HireHost", tools);
        Assert.DoesNotContain("RetireTrack", tools);
        Assert.DoesNotContain("ProduceNewsPackage", tools);
        Assert.DoesNotContain("CreateJingle", tools);
        Assert.DoesNotContain("PostArtistFeed", tools);
    }

    [TestMethod]
    public void Chat_NewsSpecialistProducesNewsNotWeather()
    {
        var tools = ChatTools(CharacterRole.NewsSpecialist);
        Assert.Contains("ProduceNewsPackage", tools);
        Assert.Contains("Announcement", tools);
        Assert.DoesNotContain("ProduceWeatherReport", tools);
    }

    [TestMethod]
    public void Chat_WeatherSpecialistProducesWeatherNotNews()
    {
        var tools = ChatTools(CharacterRole.WeatherSpecialist);
        Assert.Contains("ProduceWeatherReport", tools);
        Assert.Contains("Announcement", tools);
        Assert.DoesNotContain("ProduceNewsPackage", tools);
        Assert.DoesNotContain("LookupKnowledge", tools);
    }

    [TestMethod]
    public void Chat_ArtistGetsSelfServiceToolsOnly()
    {
        var tools = ChatTools(CharacterRole.Artist);
        Assert.Contains("MakeSong", tools);
        Assert.Contains("PostArtistFeed", tools);
        Assert.Contains("GetArtistProfile", tools);
        Assert.Contains("Remember", tools);
        Assert.DoesNotContain("Message", tools);
        Assert.DoesNotContain("Announcement", tools);
        Assert.DoesNotContain("SearchMusic", tools);
        Assert.DoesNotContain("QueueTrack", tools);
        Assert.DoesNotContain("RequestSongFromArtist", tools);
        Assert.DoesNotContain("PlanFormat", tools);
        Assert.DoesNotContain("Invite", tools);
    }

    [TestMethod]
    public void Chat_GuestHasNoTools()
    {
        Assert.Empty(ChatTools(CharacterRole.Guest));
    }

    [TestMethod]
    public void Chat_LookupKnowledgeIsOfferedToHostsDirectorAndNewsOnly()
    {
        Assert.Contains("LookupKnowledge", ChatTools(CharacterRole.Host));
        Assert.Contains("LookupKnowledge", ChatTools(CharacterRole.ProgramDirector));
        Assert.Contains("LookupKnowledge", ChatTools(CharacterRole.NewsSpecialist));
        Assert.DoesNotContain("LookupKnowledge", ChatTools(CharacterRole.WeatherSpecialist));
    }

    [TestMethod]
    public void NonChatScopes_OfferNoToolsForAnyRole()
    {
        foreach (PromptScope scope in Enum.GetValues<PromptScope>())
        {
            if (scope == PromptScope.Chat)
            {
                continue;
            }

            foreach (CharacterRole role in Enum.GetValues<CharacterRole>())
            {
                Assert.Empty(FullCatalog().GetTools(scope, role));
            }
        }
    }
}
