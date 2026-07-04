using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class GuestVoicePreparationServiceTests
{
    [TestMethod]
    public async Task ProcessGuest_StoresDesignedVoiceAndReferenceClip()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var guestId = await AddGuestAsync(fixture, voiceId: null, gender: "female");
        var design = new FakeVoiceDesignClient("qv-guest-1", WavBytes());
        var service = CreateService(fixture, new GuestVoiceQueue(), design, dataRoot.Path);

        var processed = await service.ProcessGuestAsync(guestId, CancellationToken.None);

        Assert.True(processed);
        Assert.Equal("female", design.LastGender);
        await using RadioDbContext db = fixture.CreateDbContext();
        var guest = await db.Guests.SingleAsync(g => g.Id == guestId);
        Assert.Equal("qwen", guest.TtsEngine);
        Assert.Equal("qv-guest-1", guest.VoiceId);
        Assert.NotNull(guest.VoiceDesignedAtUtc);
        Assert.Null(guest.VoiceDesignLastError);
        Assert.Contains(Path.Combine("acestep", "voice-references", "guests"), guest.VoiceReferencePath);
        Assert.True(File.Exists(Path.Combine(dataRoot.Path, guest.VoiceReferencePath!)));
    }

    [TestMethod]
    public async Task ProcessGuest_RecordsFailureAndStaysRetryable()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var guestId = await AddGuestAsync(fixture, voiceId: null, gender: "male");
        var design = new FakeVoiceDesignClient("qv-guest-1", WavBytes())
        {
            Exception = new InvalidOperationException("voice booth exploded"),
        };
        var service = CreateService(fixture, new GuestVoiceQueue(), design, dataRoot.Path);

        var processed = await service.ProcessGuestAsync(guestId, CancellationToken.None);

        Assert.False(processed);
        await using RadioDbContext db = fixture.CreateDbContext();
        var guest = await db.Guests.SingleAsync(g => g.Id == guestId);
        Assert.Null(guest.VoiceId);
        Assert.Contains("voice booth exploded", guest.VoiceDesignLastError);
    }

    [TestMethod]
    public async Task StartupScan_QueuesOnlyVoicelessNonArchivedGuests()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var voicelessId = await AddGuestAsync(fixture, voiceId: null, gender: "female");
        var voicedId = await AddGuestAsync(
            fixture, voiceId: "qv-done", gender: "female",
            referencePath: Path.Combine("acestep", "voice-references", "guests", "done.wav"));
        var archivedId = await AddGuestAsync(fixture, voiceId: null, gender: "female", archived: true);
        var queue = new GuestVoiceQueue();
        var service = CreateService(fixture, queue, new FakeVoiceDesignClient("qv-x", WavBytes()), dataRoot.Path);

        await service.EnqueuePendingGuestsAsync(CancellationToken.None);

        var queued = queue.QueuedGuestIds();
        Assert.Contains(voicelessId, queued);
        Assert.DoesNotContain(voicedId, queued);
        Assert.DoesNotContain(archivedId, queued);
    }

    private static GuestVoicePreparationService CreateService(
        DbFixture fixture, GuestVoiceQueue queue, FakeVoiceDesignClient design, string dataRoot)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITextGenerationService>(new StubLlm("Hi, I'm a guest voice sample."));
        services.AddScoped<GuestProfileWriter>();
        var provider = services.BuildServiceProvider();

        return new GuestVoicePreparationService(
            fixture,
            queue,
            design,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            NullLogger<GuestVoicePreparationService>.Instance);
    }

    private static async Task<Guid> AddGuestAsync(
        DbFixture fixture,
        string? voiceId,
        string gender,
        string? referencePath = null,
        bool archived = false)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
        }

        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            Name = "Ivy Sparks",
            Slug = $"ivy-sparks-{Guid.NewGuid():N}",
            Expertise = "urban beekeeper",
            Gender = gender,
            Biography = "Keeps hives on rooftops.",
            DeepBackground = "Left a lab job for bees.",
            VoiceCreationPrompt = "Bright, quick female voice.",
            VoiceId = voiceId,
            VoiceReferencePath = referencePath,
            IsArchived = archived,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest.Id;
    }

    private static byte[] WavBytes()
        => [0x52, 0x49, 0x46, 0x46, 0x04, 0, 0, 0, 0x57, 0x41, 0x56, 0x45];

    private sealed class StubLlm(string reply) : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(reply);
    }

    private sealed class FakeVoiceDesignClient(string handle, byte[] preview) : IVoiceDesignClient
    {
        public Exception? Exception { get; init; }

        public string? LastGender { get; private set; }

        public Task<DesignedVoice> DesignVoiceAsync(
            string description,
            string gender,
            string language,
            string? sampleText,
            CancellationToken ct)
        {
            LastGender = gender;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(new DesignedVoice(handle, 3.5));
        }

        public Task<byte[]> GetPreviewAsync(string handle, CancellationToken ct)
            => Task.FromResult(preview);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"whipradio-guest-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
