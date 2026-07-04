using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ConversationScriptWriterTests
{
    private static readonly IReadOnlyList<ConversationSpeakerBrief> Speakers =
    [
        new("host:1", "Nova Quinn", "Host", "Warm late-night host."),
        new("member:9f0e8a3c-0000-0000-0000-000000000001", "Makoa Hale", "Guest", "Metal band lead vocalist."),
    ];

    private const string ValidReply = """
{
  "title": "Volcanoes and Verses",
  "turns": [
    { "speaker": "Nova Quinn", "text": "Welcome back. [pause:300ms] Tonight I'm joined by Makoa Hale." },
    { "speaker": "makoa hale", "text": "Good to be here, Nova." },
    { "speaker": "Nova Quinn", "text": "Let's talk about the new record." }
  ]
}
""";

    private sealed class SequencedLlm(params string[] replies) : ITextGenerationService
    {
        private int _index;

        public List<string> UserPrompts { get; } = [];

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            UserPrompts.Add(userPrompt);
            var reply = replies[Math.Min(_index, replies.Length - 1)];
            _index++;
            return Task.FromResult(reply);
        }
    }

    private static ConversationScriptRequest Request(
        ConversationKind kind = ConversationKind.Talk,
        ConversationStructure structure = ConversationStructure.Freeform,
        IReadOnlyList<ConversationChapter>? chapters = null,
        IReadOnlyList<string>? recentTitles = null)
        => new(
            kind,
            structure,
            Topic: "The new record",
            Brief: "Dig into the writing process.",
            TargetDurationMinutes: 10,
            Speakers,
            chapters ?? [],
            StationName: "Whip FM",
            StationSlogan: "All night long",
            Language: "en",
            recentTitles ?? []);

    private static ConversationScriptWriter Writer(ITextGenerationService llm)
        => new(llm, NullLogger<ConversationScriptWriter>.Instance);

    [TestMethod]
    public async Task Write_ValidReply_MapsSpeakersCaseInsensitivelyAndNormalizesMarkers()
    {
        var script = await Writer(new SequencedLlm(ValidReply)).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal("Volcanoes and Verses", script.Title);
        Assert.Equal(3, script.Turns.Count);
        Assert.Equal("host:1", script.Turns[0].SpeakerKey);
        Assert.Equal(Speakers[1].SpeakerKey, script.Turns[1].SpeakerKey); // "makoa hale" lowercase matched

        // Clean text has markers stripped; Markers keeps the normalized (scaled) pause.
        Assert.False(script.Turns[0].Text.Contains('['), "transcript text must be marker-free");
        Assert.Contains("[pause:", script.Turns[0].Markers!);

        Assert.Contains("Nova Quinn: Welcome back.", script.Transcript);
        Assert.Contains("Makoa Hale: Good to be here, Nova.", script.Transcript);
    }

    [TestMethod]
    public async Task Write_UnknownSpeaker_RetriesWithRejectionAppended()
    {
        const string badReply = """
{ "title": "Ghost", "turns": [
  { "speaker": "Somebody Else", "text": "Hi." },
  { "speaker": "Nova Quinn", "text": "Hello." } ] }
""";
        var llm = new SequencedLlm(badReply, ValidReply);

        var script = await Writer(llm).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, llm.UserPrompts.Count);
        Assert.Contains("Previous reply rejected", llm.UserPrompts[1]);
        Assert.Contains("Somebody Else", llm.UserPrompts[1]);
        Assert.Equal("Volcanoes and Verses", script.Title);
    }

    [TestMethod]
    public async Task Write_TwoInvalidReplies_Throws()
    {
        var llm = new SequencedLlm("not json at all");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(llm).WriteAsync(Request(), CancellationToken.None));
        Assert.Equal(2, llm.UserPrompts.Count); // exactly one retry
    }

    [TestMethod]
    public async Task Write_SingleSpeakerScript_IsRejected()
    {
        const string monologue = """
{ "title": "Solo", "turns": [
  { "speaker": "Nova Quinn", "text": "Just me." },
  { "speaker": "Nova Quinn", "text": "Still me." } ] }
""";
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(new SequencedLlm(monologue)).WriteAsync(Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task Write_PromptCarriesRosterChaptersAndRecentTitles()
    {
        var llm = new SequencedLlm(ValidReply);
        var request = Request(
            kind: ConversationKind.Podcast,
            structure: ConversationStructure.Chaptered,
            chapters: [new ConversationChapter { Title = "Origins", Intent = "How the band formed", TargetMinutes = 4 }],
            recentTitles: ["Last Week's Episode"]);

        await Writer(llm).WriteAsync(request, CancellationToken.None);

        var prompt = llm.UserPrompts[0];
        Assert.Contains("Nova Quinn (Host)", prompt);
        Assert.Contains("Makoa Hale (Guest)", prompt);
        Assert.Contains("Origins", prompt);
        Assert.Contains("Last Week's Episode", prompt);
        Assert.Contains("podcast episode", prompt);
        Assert.Contains("1500", prompt); // word budget = 10 min × 150
    }

    [TestMethod]
    public async Task Write_FewerThanTwoSpeakers_ThrowsImmediately()
    {
        var request = Request() with { Speakers = [Speakers[0]] };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(new SequencedLlm(ValidReply)).WriteAsync(request, CancellationToken.None));
    }
}
