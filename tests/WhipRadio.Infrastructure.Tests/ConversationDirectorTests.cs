using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Conversations;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ConversationDirectorTests
{
    private const string PlanReply = """
{"title":"Bees After Dark","chapters":[{"title":"Rooftops","intent":"How the hives got up there."},{"title":"The Honey","intent":"What comes out of it."}]}
""";

    private static ConversationScriptRequest Request(int minutes = 10) => new(
        ConversationKind.Podcast,
        ConversationStructure.Freeform,
        "City bees",
        "Rooftop hives and honey.",
        minutes,
        [
            new ConversationSpeakerBrief("host:1", "Nova Quinn", "Host", "Warm late-night host."),
            new ConversationSpeakerBrief("guest:33333333-3333-3333-3333-333333333333", "Ivy Sparks", "Guest", "Urban beekeeper."),
        ],
        [],
        "WhipRadio",
        "No maps after midnight.",
        "en",
        []);

    [TestMethod]
    public async Task WriteAsync_AlternatesSpeakersAndEndsOnLeadWrapUp()
    {
        var llm = new SequencedLlm(
            PlanReply,
            """{"text":"Welcome to Bees After Dark. Ivy, how did you end up on a rooftop?","addressedTo":"Ivy Sparks","wrapUp":false}""",
            """{"text":"It started with two hives on a parking garage.","wrapUp":false}""",
            """{"text":"That's the show — thanks Ivy, and good night.","wrapUp":true}""");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        var script = await director.WriteAsync(Request(), memorySlices: null, CancellationToken.None);

        Assert.Equal("Bees After Dark", script.Title);
        Assert.Equal(3, script.Turns.Count);
        Assert.Equal("host:1", script.Turns[0].SpeakerKey);
        Assert.Equal("guest:33333333-3333-3333-3333-333333333333", script.Turns[1].SpeakerKey);
        Assert.Equal("host:1", script.Turns[2].SpeakerKey);
        Assert.Contains("Nova Quinn: Welcome to Bees After Dark.", script.Transcript);
        Assert.Contains("Ivy Sparks: It started with two hives", script.Transcript);
        // Plan + 3 turns = 4 LLM calls.
        Assert.Equal(4, llm.Requests.Count);
    }

    [TestMethod]
    public async Task WriteAsync_TurnPromptCarriesPersonaTranscriptAndMemory()
    {
        var llm = new SequencedLlm(
            PlanReply,
            """{"text":"Welcome. Ivy Sparks, take it away.","wrapUp":false}""",
            """{"text":"Happy to. The bees are thriving.","wrapUp":false}""",
            """{"text":"And that's our time — good night.","wrapUp":true}""");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);
        var memory = new Dictionary<string, IReadOnlyList<string>>
        {
            ["guest:33333333-3333-3333-3333-333333333333"] = ["Last time she promised a honey tasting."],
        };

        await director.WriteAsync(Request(), memory, CancellationToken.None);

        // Turn 2 belongs to Ivy: her persona, her memory, and the running transcript.
        var ivyPrompt = llm.Requests[2].UserPrompt;
        Assert.Contains("You are Ivy Sparks", ivyPrompt);
        Assert.Contains("Urban beekeeper.", ivyPrompt);
        Assert.Contains("honey tasting", ivyPrompt);
        Assert.Contains("Nova Quinn: Welcome.", ivyPrompt);
        // Nova's opening prompt must NOT carry Ivy's memory.
        Assert.DoesNotContain("honey tasting", llm.Requests[1].UserPrompt);
    }

    [TestMethod]
    public async Task WriteAsync_StopsAtTurnCapWithClosingTurnByLead()
    {
        // Tiny duration → cap = participants*2*... floor: 2 participants → floor 4 turns.
        var replies = new List<string> { PlanReply };
        for (var i = 0; i < 10; i++)
        {
            replies.Add("""{"text":"Another point about bees without naming anyone.","wrapUp":false}""");
        }

        var llm = new SequencedLlm([.. replies]);
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        var script = await director.WriteAsync(Request(minutes: 1), memorySlices: null, CancellationToken.None);

        Assert.Equal(4, script.Turns.Count);
        Assert.Equal("host:1", script.Turns[^1].SpeakerKey);
    }

    [TestMethod]
    public async Task WriteAsync_InterjectionOverlapsThePreviousLongTurn()
    {
        var llm = new SequencedLlm(
            PlanReply,
            """{"text":"Welcome to Bees After Dark. Ivy, tell me how a parking garage in the middle of the city became home to two of the busiest hives around.","wrapUp":false}""",
            """{"text":"Oh, I have to jump in there!","wrapUp":false,"interject":true}""",
            """{"text":"And that's the show — good night.","wrapUp":true}""");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        var script = await director.WriteAsync(Request(), memorySlices: null, CancellationToken.None);

        // The interjecting turn pulls itself into the previous speaker's tail.
        var overlap = script.Turns[0].PauseAfterMs;
        Assert.NotNull(overlap);
        Assert.True(overlap < 0);
        Assert.True(overlap >= -700 && overlap <= -350, $"overlap {overlap} outside [-700,-350]");
        // No other turn carries a pause hint.
        Assert.Null(script.Turns[1].PauseAfterMs);
        Assert.Null(script.Turns[2].PauseAfterMs);
    }

    [TestMethod]
    public async Task WriteAsync_InterjectionIsIgnoredAfterAShortTurn()
    {
        var llm = new SequencedLlm(
            PlanReply,
            """{"text":"Welcome to the show, Ivy.","wrapUp":false}""",
            """{"text":"Thanks for having me!","wrapUp":false,"interject":true}""",
            """{"text":"And that's the show — good night.","wrapUp":true}""");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        var script = await director.WriteAsync(Request(), memorySlices: null, CancellationToken.None);

        // The previous turn is under the word floor: no overlap applied.
        Assert.Null(script.Turns[0].PauseAfterMs);
    }

    [TestMethod]
    public void ShouldInterject_RequiresPreviousTurnNotClosingAndEnoughWords()
    {
        var longTurn = new ConversationTurn
        {
            Text = "one two three four five six seven eight nine ten eleven twelve",
        };
        var shortTurn = new ConversationTurn { Text = "too short to talk over" };

        Assert.True(ConversationDirector.ShouldInterject(true, closing: false, longTurn));
        Assert.False(ConversationDirector.ShouldInterject(false, closing: false, longTurn));
        Assert.False(ConversationDirector.ShouldInterject(true, closing: true, longTurn));
        Assert.False(ConversationDirector.ShouldInterject(true, closing: false, previousTurn: null));
        Assert.False(ConversationDirector.ShouldInterject(true, closing: false, shortTurn));
    }

    [TestMethod]
    public async Task WriteAsync_RetriesOnceThenThrowsAfterConsecutiveFailures()
    {
        var llm = new SequencedLlm(
            PlanReply,
            "not json at all",
            "still not json");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            director.WriteAsync(Request(), memorySlices: null, CancellationToken.None));
    }

    [TestMethod]
    public async Task WriteAsync_RecoversFromASingleBadTurn()
    {
        var llm = new SequencedLlm(
            PlanReply,
            "garbage",
            """{"text":"Welcome to the show, Ivy Sparks.","wrapUp":false}""",
            """{"text":"Glad to be here.","wrapUp":false}""",
            """{"text":"And that's the show — good night.","wrapUp":true}""");
        var director = new ConversationDirector(
            llm, new AddressedToRoundRobinPolicy(), NullLogger<ConversationDirector>.Instance);

        var script = await director.WriteAsync(Request(), memorySlices: null, CancellationToken.None);

        Assert.Equal(3, script.Turns.Count);
    }

    private sealed class SequencedLlm(params string[] replies) : ITextGenerationService
    {
        private readonly Queue<string> _replies = new(replies);

        public List<TextGenerationRequest> Requests { get; } = [];

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(_replies.Dequeue());

        public Task<string> CompleteAsync(TextGenerationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_replies.Dequeue());
        }
    }
}
