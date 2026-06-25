using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Personality;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class AnnouncementWriterTests
{
    private sealed class CapturingLlm(params string[] replies) : ITextGenerationService
    {
        private readonly Queue<string> _replies = new(
            replies.Length == 0 ? [Dto("Generated copy.")] : replies);

        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public int CallCount { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            CallCount++;
            return Task.FromResult(_replies.Count == 0 ? Dto("Generated copy.") : _replies.Dequeue());
        }
    }

    /// <summary>Minimal valid combined-run reply (script == delivery, no voice block).</summary>
    private static string Dto(string text)
        => $$"""{"script":{{System.Text.Json.JsonSerializer.Serialize(text)}},"delivery":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""";

    private static Moderator Host() => new()
    {
        Name = "Lena",
        PersonaPrompt = "Quirlige Moderatorin mit viel Energie.",
        Style = "fast-energetic",
        Language = "en",
        Gender = ModeratorGenders.Female,
    };

    [TestMethod]
    public async Task SongIntro_FillsTrackPlaceholders()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);
        var artistId = Guid.NewGuid();
        var artist = new Artist { Id = artistId, Name = "Static Velvet" };
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
            new AnnouncementRequest(AnnouncementKind.SongIntro, "WhipRadio", "en", track), Host(), CancellationToken.None);

        Assert.Contains("WhipRadio", llm.SystemPrompt);
        Assert.Contains("STRICTLY in en", llm.SystemPrompt);
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
    public async Task SongOutro_FillsArtistAndTrackContext()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);
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
            new AnnouncementRequest(AnnouncementKind.SongOutro, "WhipRadio", "en", track), Host(), CancellationToken.None);

        Assert.Contains("Afterimage Arcade", llm.UserPrompt);
        Assert.Contains("Glass Harbor", llm.UserPrompt);
        Assert.Contains("night drive", llm.UserPrompt);
        Assert.Contains("warm arpeggios and late-night drums", llm.UserPrompt);
        Assert.Contains("Song language: de", llm.UserPrompt);
        Assert.Contains("broken tape echo", llm.UserPrompt);
        Assert.DoesNotContain("{Artist}", llm.UserPrompt);
    }

    [TestMethod]
    public async Task Weather_UsesFacts()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Weather, "WhipRadio", "de", Facts: "Currently 14°C, light rain."),
            Host(),
            CancellationToken.None);

        Assert.Contains("Currently 14°C, light rain.", llm.UserPrompt);
        Assert.DoesNotContain("{WeatherFacts}", llm.UserPrompt);
    }

    [TestMethod]
    public async Task InjectsPersonaAndStyleIntoSystemPrompt()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), Host(), CancellationToken.None);

        Assert.Contains("Quirlige Moderatorin", llm.SystemPrompt);
        Assert.Contains("fast-energetic", llm.SystemPrompt);
        Assert.Contains("[pause:NNNms]", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task ReturnsCleanScriptAndMarkedDelivery()
    {
        var llm = new CapturingLlm(
            """{"script":"Up next, a banger.","delivery":"Up next, uh [pause:300ms] a banger!","voice":{"deliveryPrompt":"slightly slow, warm","rate":0.95}}""");
        var writer = new AnnouncementWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), Host(), CancellationToken.None);

        Assert.Equal("Up next, a banger.", result.Script);
        Assert.Equal("Up next, uh [pause:300ms] a banger!", result.Delivery);
        Assert.Equal("slightly slow, warm", result.DeliveryPrompt);
        Assert.Equal(0.95, result.Rate);
        Assert.Equal(1, llm.CallCount);
    }

    [TestMethod]
    public async Task StripsStrayMarkersFromTranscript()
    {
        var llm = new CapturingLlm(
            """{"script":"Markets [pause:200ms] open lower.","delivery":"Markets open lower."}""");
        var writer = new AnnouncementWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.News, "WhipRadio", "en", Facts: "market facts"),
            Host(),
            CancellationToken.None);

        Assert.Equal("Markets open lower.", result.Script);
        Assert.DoesNotContain("[pause", result.Script);
    }

    [TestMethod]
    public async Task AddsTerminalPunctuationToScript()
    {
        var llm = new CapturingLlm(
            """{"script":"Up next, a banger","delivery":"Up next, a banger"}""");
        var writer = new AnnouncementWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), Host(), CancellationToken.None);

        Assert.Equal("Up next, a banger.", result.Script);
    }

    [TestMethod]
    public async Task KeepsExistingTerminalPunctuation()
    {
        var llm = new CapturingLlm(
            """{"script":"Is it cold in the studio?","delivery":"Is it cold in the studio?"}""");
        var writer = new AnnouncementWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), Host(), CancellationToken.None);

        Assert.Equal("Is it cold in the studio?", result.Script);
    }

    [TestMethod]
    public async Task RetriesInvalidJsonOnce()
    {
        var llm = new CapturingLlm(
            "not json at all",
            """{"script":"Clean copy.","delivery":"Clean copy."}""");
        var writer = new AnnouncementWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.News, "WhipRadio", "en", Facts: "market facts"),
            Host(),
            CancellationToken.None);

        Assert.Equal("Clean copy.", result.Script);
        Assert.Equal(2, llm.CallCount);
        Assert.Contains("Previous reply was not valid", llm.UserPrompt);
    }

    [TestMethod]
    public async Task WithPromptContext_AppendsSituationToSystemPrompt()
    {
        var llm = new CapturingLlm();
        var writer = new AnnouncementWriter(llm);
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
                Energy.High, Formality.Casual, HumorLevel.High, Talkativeness.High, Warmth.High),
            CurrentTraits = new HostPersonalityTraits(
                Energy.VeryHigh, Formality.Casual, HumorLevel.High, Talkativeness.High, Warmth.High),
            SpeechRate = 1.0,
            WordsPerSecond = 2.8,
            AvailableSeconds = 30,
            WordBudget = 84,
            RecentTalkTopics = ["metronome joke"],
            RecurringBits = ["drummer/metronome premise"],
            QueuedListenerMessages = ["Maya (greeting): hello from the late shift"],
            AlreadySpokenContext = "Top of the hour. Maya has the news.",
        };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en", PromptContext: context),
            Host(),
            CancellationToken.None);

        Assert.Contains("Current situation:", llm.SystemPrompt);
        Assert.Contains("metronome joke", llm.SystemPrompt);
        Assert.Contains("drummer/metronome premise", llm.SystemPrompt);
        Assert.Contains("roughly 84 words", llm.SystemPrompt);
        Assert.Contains("baseline persona stable", llm.SystemPrompt);
        Assert.Contains("Already aired immediately before this segment", llm.SystemPrompt);
    }

    [TestMethod]
    public async Task SongIntro_ChangesInstructionByFormatTalkDepth()
    {
        var track = new Track { Title = "Neon Llama", Genre = "indie rock", Style = "driving drums" };

        var nameOnlyLlm = new CapturingLlm();
        await new AnnouncementWriter(nameOnlyLlm).WriteAsync(
            new AnnouncementRequest(
                AnnouncementKind.SongIntro, "WhipRadio", "en", track,
                PromptContext: ContextWithTalkDepth(TalkDepth.NameOnly)),
            Host(),
            CancellationToken.None);

        var deepDiveLlm = new CapturingLlm();
        await new AnnouncementWriter(deepDiveLlm).WriteAsync(
            new AnnouncementRequest(
                AnnouncementKind.SongIntro, "WhipRadio", "en", track,
                PromptContext: ContextWithTalkDepth(TalkDepth.DeepDive)),
            Host(),
            CancellationToken.None);

        Assert.Contains("Talk depth is NameOnly", nameOnlyLlm.UserPrompt);
        Assert.Contains("Talk depth is DeepDive", deepDiveLlm.UserPrompt);
        Assert.NotEqual(nameOnlyLlm.UserPrompt, deepDiveLlm.UserPrompt);
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
