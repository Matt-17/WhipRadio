using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ScriptWriterAndVoiceDirectorTests
{
    private sealed class CapturingLlm(string reply = "Generated copy.") : ITextGenerationService
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
    public async Task ScriptWriter_SongIntro_FillsTrackPlaceholders()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);
        var track = new Track
        {
            Title = "Neon Llama",
            Genre = "indie rock",
            Style = "driving drums",
            Language = "en",
            SongStory = "The artist wrote it after a rooftop rehearsal during a power cut.",
        };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.SongIntro, "WhipRadio", "en", track), CancellationToken.None);

        Assert.Contains("WhipRadio", llm.SystemPrompt);
        Assert.Contains("language: en", llm.SystemPrompt);
        Assert.Contains("Neon Llama", llm.UserPrompt);
        Assert.Contains("indie rock", llm.UserPrompt);
        Assert.Contains("driving drums", llm.UserPrompt);
        Assert.Contains("rooftop rehearsal", llm.UserPrompt);
        Assert.Contains("Song language: en", llm.UserPrompt);
        Assert.DoesNotContain("{Title}", llm.UserPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_Weather_UsesFacts()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Weather, "WhipRadio", "de", Facts: "Currently 14°C, light rain."),
            CancellationToken.None);

        Assert.Contains("Currently 14°C, light rain.", llm.UserPrompt);
        Assert.DoesNotContain("{WeatherFacts}", llm.UserPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_SanitizesLlmOutput()
    {
        var llm = new CapturingLlm("\"Sure, here is your intro: Up next, a banger!\"");
        var writer = new ScriptWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), CancellationToken.None);

        Assert.Equal("Up next, a banger!", result);
    }

    [TestMethod]
    public async Task ScriptWriter_WithPromptContext_AppendsSituationToSystemPrompt()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);
        var context = new PromptContext
        {
            Scope = PromptScope.AnnouncementScript,
            Purpose = "SongIntro",
            StationName = "WhipRadio",
            FrequencyMhz = 104.4,
            LocalNow = new DateTimeOffset(2026, 6, 17, 18, 30, 0, TimeSpan.Zero),
            Language = "en",
            HostName = "Lena",
            PersonaSummary = "High-energy evening host.",
            BaselineTraits = new HostPersonalityTraits(
                Energy.High,
                Formality.Casual,
                HumorLevel.High,
                Talkativeness.High,
                Warmth.High),
            CurrentTraits = new HostPersonalityTraits(
                Energy.VeryHigh,
                Formality.Casual,
                HumorLevel.High,
                Talkativeness.High,
                Warmth.High),
            SpeechRate = 1.0,
            WordsPerSecond = 2.8,
            AvailableSeconds = 30,
            WordBudget = 84,
            RecentTalkTopics = ["metronome joke"],
            RecurringBits = ["drummer/metronome premise"],
            QueuedListenerMessages = ["Maya (greeting): hello from the late shift"],
            Tools =
            [
                new CharacterToolDefinition(
                    "Announce",
                    "Create spoken text.",
                    [new CharacterToolArgument("text", "Spoken text.")]),
            ],
        };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en", PromptContext: context),
            CancellationToken.None);

        Assert.Contains("Current situation:", llm.SystemPrompt);
        Assert.Contains("metronome joke", llm.SystemPrompt);
        Assert.Contains("drummer/metronome premise", llm.SystemPrompt);
        Assert.Contains("Maya (greeting)", llm.SystemPrompt);
        Assert.Contains("Announce(text)", llm.SystemPrompt);
        Assert.Contains("roughly 84 words", llm.SystemPrompt);
        Assert.Contains("Host baseline traits", llm.SystemPrompt);
        Assert.Contains("Current mood traits", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_SongIntro_ChangesInstructionByFormatTalkDepth()
    {
        var track = new Track { Title = "Neon Llama", Genre = "indie rock", Style = "driving drums" };

        var nameOnlyLlm = new CapturingLlm();
        var nameOnlyWriter = new ScriptWriter(nameOnlyLlm);
        await nameOnlyWriter.WriteAsync(
            new AnnouncementRequest(
                AnnouncementKind.SongIntro,
                "WhipRadio",
                "en",
                track,
                PromptContext: ContextWithTalkDepth(TalkDepth.NameOnly)),
            CancellationToken.None);

        var deepDiveLlm = new CapturingLlm();
        var deepDiveWriter = new ScriptWriter(deepDiveLlm);
        await deepDiveWriter.WriteAsync(
            new AnnouncementRequest(
                AnnouncementKind.SongIntro,
                "WhipRadio",
                "en",
                track,
                PromptContext: ContextWithTalkDepth(TalkDepth.DeepDive)),
            CancellationToken.None);

        Assert.Contains("Talk depth is NameOnly", nameOnlyLlm.UserPrompt);
        Assert.Contains("Talk depth is DeepDive", deepDiveLlm.UserPrompt);
        Assert.NotEqual(nameOnlyLlm.UserPrompt, deepDiveLlm.UserPrompt);
    }

    [TestMethod]
    public async Task VoiceDirector_InjectsPersonaAndPassesScript()
    {
        var llm = new CapturingLlm("Adapted [pause:300ms] text.");
        var director = new VoiceDirector(llm);
        var moderator = new Moderator
        {
            Name = "Lena",
            PersonaPrompt = "Quirlige Moderatorin mit viel Energie.",
            Style = "fast-energetic",
        };

        var result = await director.DirectAsync("Original script.", moderator, CancellationToken.None);

        Assert.Contains("Quirlige Moderatorin", llm.SystemPrompt);
        Assert.Contains("fast-energetic", llm.SystemPrompt);
        Assert.Contains("[pause:NNNms]", llm.SystemPrompt);
        Assert.Equal("Original script.", llm.UserPrompt);
        Assert.Equal("Adapted [pause:300ms] text.", result);
    }

    private static PromptContext ContextWithTalkDepth(TalkDepth talkDepth)
        => new()
        {
            Scope = PromptScope.AnnouncementScript,
            Purpose = "SongIntro",
            StationName = "WhipRadio",
            FrequencyMhz = 104.4,
            LocalNow = new DateTimeOffset(2026, 6, 17, 18, 30, 0, TimeSpan.Zero),
            Language = "en",
            FormatName = "Test Format",
            FormatTalkDepth = talkDepth,
            FormatTalkDensity = 0.5,
            SpeechRate = 1.0,
            WordsPerSecond = 2.8,
        };
}
