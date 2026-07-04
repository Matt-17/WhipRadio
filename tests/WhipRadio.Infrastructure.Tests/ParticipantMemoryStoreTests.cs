using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.TestSupport;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ParticipantMemoryStoreTests
{
    [TestMethod]
    public async Task Embedding_FloatArrayRoundTripsThroughPostgresRealArray()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        float[] embedding = [0.25f, -1f, 3.5f, 0f, 123456.78f];
        Guid id;

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            var memory = new ParticipantMemory
            {
                ParticipantKey = "host:1",
                Kind = ParticipantMemoryKind.TalkSummary,
                Content = "Talked about rooftop bees.",
                Embedding = embedding,
                EmbeddingModel = "nomic-embed-text",
                SourceRef = "conversation:test",
            };
            db.ParticipantMemories.Add(memory);
            await db.SaveChangesAsync();
            id = memory.Id;
        }

        await using (RadioDbContext verify = fixture.CreateDbContext())
        {
            ParticipantMemory stored = await verify.ParticipantMemories.AsNoTracking().SingleAsync(m => m.Id == id);
            Assert.Equal(embedding, stored.Embedding);
            Assert.Equal(ParticipantMemoryKind.TalkSummary, stored.Kind);
            Assert.Equal("host:1", stored.ParticipantKey);
        }
    }
}
