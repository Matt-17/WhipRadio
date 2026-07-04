using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class GuestCreationServiceTests
{
    private const string ProfileReply = """
{
  "name": "Ivy Sparks",
  "expertise": "urban beekeeper",
  "gender": "female",
  "age": 47,
  "interests": "rooftop hives, native wildflowers",
  "personality": "Enthusiastic and precise.",
  "biography": "Keeps forty hives on downtown rooftops.",
  "deepBackground": "Started with two hives on a parking garage after leaving a lab job.",
  "voiceCreationPrompt": "Bright mid-range female voice, quick tempo."
}
""";

    [TestMethod]
    public async Task CreateGuestAsync_PersistsProfileAndQueuesVoiceDesign()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var voiceQueue = new GuestVoiceQueue();
        var service = CreateService(fixture, voiceQueue, ProfileReply);

        var guest = await service.CreateGuestAsync("a beekeeper", CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        var stored = await db.Guests.AsNoTracking().SingleAsync(g => g.Id == guest.Id);
        Assert.Equal("Ivy Sparks", stored.Name);
        Assert.Equal("ivy-sparks", stored.Slug);
        Assert.Equal("urban beekeeper", stored.Expertise);
        Assert.Equal("female", stored.Gender);
        Assert.Equal(47, stored.Age);
        Assert.Contains("rooftop hives", stored.Interests);
        Assert.Contains("parking garage", stored.DeepBackground);
        Assert.Equal("a beekeeper", stored.CreationHint);
        Assert.NotNull(stored.GenerationPrompt);
        Assert.Null(stored.VoiceId);
        Assert.Equal(new[] { guest.Id }, voiceQueue.QueuedGuestIds().ToArray());
    }

    [TestMethod]
    public async Task CreateGuestAsync_AvoidsNameCollisionWithExistingHost()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            db.Moderators.Add(new Moderator
            {
                Name = "Ivy Sparks",
                Slug = "ivy-sparks-host",
                Language = "en",
                Gender = ModeratorGenders.Female,
                PersonaPrompt = "Host persona.",
                Style = "calm",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture, new GuestVoiceQueue(), ProfileReply);

        var guest = await service.CreateGuestAsync(null, CancellationToken.None);

        Assert.Equal("Ivy Sparks 2", guest.Name);
    }

    [TestMethod]
    public async Task RedefineGuestAsync_KeepsIdentityAndResetsVoice()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Guid guestId;
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                Name = "Old Name",
                Slug = "old-name",
                Expertise = "retired lighthouse keeper",
                Biography = "Old bio.",
                DeepBackground = "Old background.",
                VoiceCreationPrompt = "Old voice.",
                VoiceId = "qv-old",
                VoiceReferencePath = "acestep/voice-references/guests/x.wav",
                VoiceDesignedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Guests.Add(guest);
            await db.SaveChangesAsync();
            guestId = guest.Id;
        }

        var voiceQueue = new GuestVoiceQueue();
        var service = CreateService(fixture, voiceQueue, ProfileReply);

        var updated = await service.RedefineGuestAsync(guestId, "make her a beekeeper", CancellationToken.None);

        Assert.Equal(guestId, updated.Id);
        Assert.Equal("Old Name", updated.Name);
        Assert.Equal("old-name", updated.Slug);
        Assert.Equal("urban beekeeper", updated.Expertise);
        Assert.Null(updated.VoiceId);
        Assert.Null(updated.VoiceReferencePath);
        Assert.Equal(new[] { guestId }, voiceQueue.QueuedGuestIds().ToArray());
    }

    private static GuestCreationService CreateService(DbFixture fixture, GuestVoiceQueue voiceQueue, string reply)
        => new(
            fixture,
            new GuestProfileWriter(new StubLlm(reply)),
            new GuestCreationQueue(),
            voiceQueue,
            CreateMemoryWriter(fixture),
            NullLogger<GuestCreationService>.Instance);

    private static ParticipantMemoryWriter CreateMemoryWriter(DbFixture fixture)
        => new(
            fixture,
            new StubEmbedding(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WhipRadio.Infrastructure.Llm.LlmOptions()),
            NullLogger<ParticipantMemoryWriter>.Instance);

    private sealed class StubEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            => Task.FromResult(new float[] { 1f, 0f, 0f });
    }

    private sealed class StubLlm(string reply) : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(reply);
    }
}
