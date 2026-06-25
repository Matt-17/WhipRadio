using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ScriptWriterAndVoiceDirectorTests
{
    private sealed class CapturingLlm(params string[] replies) : ITextGenerationService
    {
        private readonly Queue<string> _replies = new(replies.Length == 0 ? ["Generated copy."] : replies);

        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public int CallCount { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            CallCount++;
            return Task.FromResult(_replies.Count == 0 ? "Generated copy." : _replies.Dequeue());
        }
    }

    [TestMethod]
    public async Task ScriptWriter_SongIntro_FillsTrackPlaceholders()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);
        var artistId = Guid.NewGuid();
        var artist = new Artist
        {
            Id = artistId,
            Name = "Static Velvet",
        };
        var track = new Track
        {
            Title = "Neon Llama",
            Artist = artist,
            ArtistId = artistId,
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
        Assert.Contains("Static Velvet", llm.UserPrompt);
        Assert.Contains("indie rock", llm.UserPrompt);
        Assert.Contains("driving drums", llm.UserPrompt);
        Assert.Contains("rooftop rehearsal", llm.UserPrompt);
        Assert.Contains("Song language: en", llm.UserPrompt);
        Assert.DoesNotContain("{Title}", llm.UserPrompt);
        Assert.DoesNotContain("{Artist}", llm.UserPrompt);
    }

    [TestMethod]
    public async Task ScriptWriter_SongOutro_FillsArtistAndTrackContext()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);
        var track = new Track
        {
            Title = "Afterimage Arcade",
            Artist = new Artist { Name = "Glass Harbor" },
            Genre = "synth pop",
            Subgenre = "night drive",
            Style = "warm arpeggios and late-night drums",
            Language = "de",
            SongStory = "Glass Harbor wrote it while testing a broken tape echo.",
        };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.SongOutro, "WhipRadio", "en", track), CancellationToken.None);

        Assert.Contains("Afterimage Arcade", llm.UserPrompt);
        Assert.Contains("Glass Harbor", llm.UserPrompt);
        Assert.Contains("night drive", llm.UserPrompt);
        Assert.Contains("warm arpeggios and late-night drums", llm.UserPrompt);
        Assert.Contains("Song language: de", llm.UserPrompt);
        Assert.Contains("broken tape echo", llm.UserPrompt);
        Assert.DoesNotContain("{Artist}", llm.UserPrompt);
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
    public async Task ScriptWriter_ExtractsAnnounceToolJson()
    {
        var llm = new CapturingLlm("""{"tool":"Announce","arguments":{"text":"Markets open lower."}}""");
        var writer = new ScriptWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.News, "WhipRadio", "en", Facts: "market facts"),
            CancellationToken.None);

        Assert.Equal("Markets open lower.", result);
        Assert.Equal(1, llm.CallCount);
    }

    [TestMethod]
    public async Task ScriptWriter_RetriesInvalidToolJsonOnce()
    {
        var llm = new CapturingLlm(
            """{"tool":"Announce","arguments":{}}""",
            "Clean spoken copy.");
        var writer = new ScriptWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.News, "WhipRadio", "en", Facts: "market facts"),
            CancellationToken.None);

        Assert.Equal("Clean spoken copy.", result);
        Assert.Equal(2, llm.CallCount);
        Assert.Contains("Previous reply rejected", llm.UserPrompt);
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
            AlreadySpokenContext = "Top of the hour. Maya has the news.",
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
        Assert.Contains("Announce: Create spoken text.", llm.SystemPrompt);
        Assert.Contains("text (string, required)", llm.SystemPrompt);
        Assert.Contains("roughly 84 words", llm.SystemPrompt);
        Assert.Contains("Host baseline traits", llm.SystemPrompt);
        Assert.Contains("Current mood traits", llm.SystemPrompt);
        Assert.Contains("Already aired immediately before this segment", llm.SystemPrompt);
        Assert.Contains("Maya has the news", llm.SystemPrompt);
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

    [TestMethod]
    public async Task VoiceDirector_RetriesInvalidToolJsonOnce()
    {
        var llm = new CapturingLlm(
            """{"tool":"Announce","arguments":{}}""",
            "Adapted copy.");
        var director = new VoiceDirector(llm);
        var moderator = new Moderator
        {
            Name = "Lena",
            PersonaPrompt = "Clear host.",
            Style = "steady",
        };

        var result = await director.DirectAsync("Original script.", moderator, CancellationToken.None);

        Assert.Equal("Adapted copy.", result);
        Assert.Equal(2, llm.CallCount);
        Assert.Contains("Previous reply rejected", llm.UserPrompt);
    }

    [TestMethod]
    public async Task VoiceDirector_AcceptsLeadingSpeechMarkerWithoutRetry()
    {
        var llm = new CapturingLlm("[rate:slow] Adapted [pause:300ms] copy.");
        var director = new VoiceDirector(llm);
        var moderator = new Moderator
        {
            Name = "Lena",
            PersonaPrompt = "Clear host.",
            Style = "steady",
        };

        var result = await director.DirectAsync("Original script.", moderator, CancellationToken.None);

        Assert.Equal("[rate:slow] Adapted [pause:300ms] copy.", result);
        Assert.Equal(1, llm.CallCount);
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
