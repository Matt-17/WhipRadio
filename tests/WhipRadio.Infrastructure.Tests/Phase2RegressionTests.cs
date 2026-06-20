using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class Phase2RegressionTests
{
    private sealed class CapturingLlm(string reply = "ok") : ITextGenerationService
    {
        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

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
    public async Task ScriptWriter_SystemPrompt_EnforcesLanguage()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), CancellationToken.None);

        Assert.Contains("STRICTLY in this language: en", llm.SystemPrompt);
        Assert.Contains("Never switch", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task VoiceDirector_PromptCarriesLanguageAndGender()
    {
        var llm = new CapturingLlm();
        var director = new VoiceDirector(llm);
        var host = new Moderator
        {
            Name = "Jordan",
            Language = "en",
            Gender = ModeratorGenders.Male,
            PersonaPrompt = "Measured late-night host.",
            Style = "slow-thoughtful",
        };

        await director.DirectAsync("Script.", host, CancellationToken.None);

        Assert.Contains("language: en", llm.SystemPrompt);
        Assert.Contains("male", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_ListenerGreeting_ReadsSenderAndMessage()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.ListenerGreeting, "WhipRadio", "en",
                Facts: "- Mia (music request): \"Hello to the night owls, play something chill!\""),
            CancellationToken.None);

        Assert.Contains("Mia", llm.UserPrompt);
        Assert.Contains("night owls", llm.UserPrompt);
        Assert.Contains("Do NOT promise a specific song", llm.UserPrompt);
        Assert.Contains("Relay each message's actual content", llm.UserPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_ListenerGreeting_CarriesMultipleMessages()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.ListenerGreeting, "WhipRadio", "en",
                Facts: "- Matt: \"Greetings to John and Mom!\"\n- Anna: \"Is it cold in the studio?\""),
            CancellationToken.None);

        Assert.Contains("Matt", llm.UserPrompt);
        Assert.Contains("Anna", llm.UserPrompt);
        Assert.Contains("weave them into one flowing segment", llm.UserPrompt);
    }
}
