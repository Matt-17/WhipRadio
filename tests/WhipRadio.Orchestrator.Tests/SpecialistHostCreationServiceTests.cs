using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class SpecialistHostCreationServiceTests
{
    [TestMethod]
    public async Task CreateAsync_UsesExplicitNameFromHintBeforePlanName()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var llm = new CapturingLlm("""
{
  "name": "Celeste Rivera",
  "gender": "male",
  "style": "friendly and warm",
  "personaPrompt": "A concise specialist host with a clear public-service tone.",
  "preferredGenres": "news,current events",
  "talkativeness": 0.25,
  "traits": {
    "energy": "Calm",
    "formality": "Formal",
    "humorLevel": "VeryLow",
    "talkativeness": "Low",
    "warmth": "Warm"
  },
  "voiceDescription": "Clear, polished, and trustworthy.",
  "sampleText": "unused"
}
""");
        var voiceDesigner = new CapturingVoiceDesigner();
        var service = new SpecialistHostCreationService(
            fixture,
            llm,
            voiceDesigner,
            new NoOpProductionUpdatePublisher(),
            NullLogger<SpecialistHostCreationService>.Instance);

        Moderator moderator = await service.CreateAsync(
            SpecialistHostRole.News,
            "Female named Clara Sky, friendly",
            CancellationToken.None);

        Assert.Equal("Clara Sky", moderator.Name);
        Assert.Equal(ModeratorGenders.Female, moderator.Gender);
        Assert.True(moderator.IsNewsSpecialist);
        Assert.Equal("voice-handle", moderator.VoiceId);

        await using RadioDbContext db = fixture.CreateDbContext();
        Moderator persisted = await db.Moderators.SingleAsync();
        StationSettings settings = await db.StationSettings.SingleAsync();

        Assert.Equal("Clara Sky", persisted.Name);
        Assert.Equal(moderator.Id, settings.NewsPresenterModeratorId);
        Assert.Equal("Clear, polished, and trustworthy.", voiceDesigner.LastDescription);
        Assert.Contains("Female named Clara Sky", llm.LastUserPrompt);
        Assert.Contains("Clara Sky", llm.LastUserPrompt);
    }

    private sealed class CapturingLlm(string reply) : ITextGenerationService
    {
        public string? LastUserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            LastUserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

    private sealed class CapturingVoiceDesigner : IVoiceDesignClient
    {
        public string? LastDescription { get; private set; }

        public Task<DesignedVoice> DesignVoiceAsync(
            string description,
            string gender,
            string language,
            string? sampleText,
            CancellationToken ct)
        {
            LastDescription = description;
            return Task.FromResult(new DesignedVoice("voice-handle", 1.25));
        }

        public Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct)
            => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NoOpProductionUpdatePublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishConversationsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishArchiveChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
