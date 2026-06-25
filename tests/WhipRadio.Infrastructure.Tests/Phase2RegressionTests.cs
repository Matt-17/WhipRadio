using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class Phase2RegressionTests
{
    private sealed class CapturingLlm(string? reply = null) : ITextGenerationService
    {
        private readonly string _reply = reply ?? """{"script":"ok","delivery":"ok"}""";

        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(_reply);
        }
    }

    private static Moderator AnyHost() => new()
    {
        Name = "Host",
        Language = "en",
        Gender = ModeratorGenders.Female,
        PersonaPrompt = "A host.",
        Style = "steady",
    };

    [TestMethod]
    public void TitleWordGuard_AlwaysBansTheClicheWords()
    {
        var forbidden = TitleWordGuard.MostFrequentWords(["Random Title", "Another Tune"], take: 5);

        Assert.Contains("ghost", forbidden);
        Assert.Contains("neon", forbidden);
        Assert.Contains("echo", forbidden);
        Assert.Contains("static", forbidden);
        Assert.Contains("fade", forbidden);
    }

    [TestMethod]
    public void TitleWordGuard_AddsDynamicallyOverusedWords()
    {
        string[] titles = ["Crimson Harvest", "Harvest Moonlight", "Harvest of Glass", "Glass Gardens"];

        var forbidden = TitleWordGuard.MostFrequentWords(titles, take: 5);

        Assert.Contains("harvest", forbidden);
        Assert.Contains("glass", forbidden);
    }

    [TestMethod]
    public async Task AnnouncementWriter_SystemPrompt_EnforcesLanguage()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), AnyHost(), CancellationToken.None);

        Assert.Contains("STRICTLY in en", llm.SystemPrompt);
        Assert.Contains("Never switch languages", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task AnnouncementWriter_PromptCarriesLanguageAndGender()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);
        var host = new Moderator
        {
            Name = "Jordan",
            Language = "en",
            Gender = ModeratorGenders.Male,
            PersonaPrompt = "Measured late-night host.",
            Style = "slow-thoughtful",
        };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), host, CancellationToken.None);

        Assert.Contains("STRICTLY in en", llm.SystemPrompt);
        Assert.Contains("male", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task AnnouncementWriter_ListenerGreeting_ReadsSenderAndMessage()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.ListenerGreeting, "WhipRadio", "en",
                Facts: "- Mia (music request): \"Hello to the night owls, play something chill!\""),
            AnyHost(),
            CancellationToken.None);

        Assert.Contains("Mia", llm.UserPrompt);
        Assert.Contains("night owls", llm.UserPrompt);
        Assert.Contains("Do NOT promise a specific song", llm.UserPrompt);
        Assert.Contains("Relay each message's actual content", llm.UserPrompt);
    }

    [TestMethod]
    public async Task AnnouncementWriter_ListenerGreeting_CarriesMultipleMessages()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.ListenerGreeting, "WhipRadio", "en",
                Facts: "- Matt: \"Greetings to John and Mom!\"\n- Anna: \"Is it cold in the studio?\""),
            AnyHost(),
            CancellationToken.None);

        Assert.Contains("Matt", llm.UserPrompt);
        Assert.Contains("Anna", llm.UserPrompt);
        Assert.Contains("weave them into one flowing segment", llm.UserPrompt);
    }
}
