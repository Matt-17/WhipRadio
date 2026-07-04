using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ParticipantMemoryRetrieverTests
{
    [TestMethod]
    public async Task Retrieve_RanksByCosineAndDropsUnrelatedMemories()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        await SeedAsync(fixture, "guest:11111111-1111-1111-1111-111111111111",
            ("Bees love rooftop gardens.", [1f, 0f, 0f]),
            ("Honey harvest happens in autumn.", [0.9f, 0.1f, 0f]),
            ("I once fixed a tractor.", [0f, 0f, 1f]));
        var retriever = new ParticipantMemoryRetriever(
            fixture,
            new StubEmbedding([1f, 0f, 0f]),
            NullLogger<ParticipantMemoryRetriever>.Instance);

        var memories = await retriever.RetrieveAsync(
            "guest:11111111-1111-1111-1111-111111111111", "tell me about bees", k: 2);

        Assert.Equal(2, memories.Count);
        Assert.Equal("Bees love rooftop gardens.", memories[0]);
        Assert.Equal("Honey harvest happens in autumn.", memories[1]);
    }

    [TestMethod]
    public async Task Retrieve_IsScopedToTheParticipant()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        await SeedAsync(fixture, "host:1", ("A host memory.", [1f, 0f, 0f]));
        await SeedAsync(fixture, "host:2", ("Another host's memory.", [1f, 0f, 0f]));
        var retriever = new ParticipantMemoryRetriever(
            fixture,
            new StubEmbedding([1f, 0f, 0f]),
            NullLogger<ParticipantMemoryRetriever>.Instance);

        var memories = await retriever.RetrieveAsync("host:1", "anything", k: 5);

        Assert.Equal(new[] { "A host memory." }, memories.ToArray());
    }

    [TestMethod]
    public async Task Retrieve_ReturnsEmptyWhenTheEmbeddingBackendFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        await SeedAsync(fixture, "host:1", ("A host memory.", [1f, 0f, 0f]));
        var retriever = new ParticipantMemoryRetriever(
            fixture,
            new ThrowingEmbedding(),
            NullLogger<ParticipantMemoryRetriever>.Instance);

        var memories = await retriever.RetrieveAsync("host:1", "anything", k: 3);

        Assert.Empty(memories);
    }

    [TestMethod]
    public async Task Writer_StoresPrunesAndRetrieverFindsFacts()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var embedding = new StubEmbedding([1f, 0f, 0f]);
        var writer = new ParticipantMemoryWriter(
            fixture,
            embedding,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WhipRadio.Infrastructure.Llm.LlmOptions()),
            NullLogger<ParticipantMemoryWriter>.Instance);

        await writer.StoreFactsAsync("guest:22222222-2222-2222-2222-222222222222",
            ["I am Ivy Sparks, urban beekeeper.", "My interests: rooftop hives."],
            "guest:test",
            CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        var rows = await db.ParticipantMemories.AsNoTracking()
            .Where(memory => memory.ParticipantKey == "guest:22222222-2222-2222-2222-222222222222")
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(new[] { 1f, 0f, 0f }, row.Embedding));
        Assert.All(rows, row => Assert.Equal("nomic-embed-text", row.EmbeddingModel));
    }

    private static async Task SeedAsync(
        DbFixture fixture, string key, params (string Content, float[] Embedding)[] memories)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        foreach ((string content, float[] embedding) in memories)
        {
            db.ParticipantMemories.Add(new ParticipantMemory
            {
                ParticipantKey = key,
                Kind = ParticipantMemoryKind.TalkSummary,
                Content = content,
                Embedding = embedding,
                EmbeddingModel = "stub",
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class StubEmbedding(float[] vector) : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            => Task.FromResult(vector);
    }

    private sealed class ThrowingEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            => throw new HttpRequestException("embedding backend down");
    }
}
