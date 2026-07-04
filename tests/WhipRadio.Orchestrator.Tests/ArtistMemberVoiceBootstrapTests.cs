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
public class ArtistMemberVoiceBootstrapTests
{
    [TestMethod]
    public async Task VoicePreparation_StoresDesignedVoiceAndClearsOldError()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: "old error");
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            new ArtistMemberVoiceQueue(),
            new FakeVoiceDesignClient("qwen-voice-1", WavBytes()),
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        var processed = await service.ProcessMemberAsync(memberId, CancellationToken.None);

        Assert.True(processed);
        await using RadioDbContext db = fixture.CreateDbContext();
        var member = await db.ArtistMembers.SingleAsync(m => m.Id == memberId);
        Assert.Equal("qwen", member.TtsEngine);
        Assert.Equal("qwen-voice-1", member.VoiceId);
        Assert.NotNull(member.VoiceDesignedAtUtc);
        Assert.Null(member.VoiceDesignLastError);
        Assert.Contains(Path.Combine("acestep", "voice-references"), member.VoiceReferencePath);
        Assert.True(File.Exists(Path.Combine(dataRoot.Path, member.VoiceReferencePath!)));
    }

    [TestMethod]
    public async Task VoicePreparation_RecordsFailureAndLeavesMemberRetryable()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            new ArtistMemberVoiceQueue(),
            new FakeVoiceDesignClient("qwen-voice-1", WavBytes())
            {
                Exception = new InvalidOperationException("voice booth unavailable"),
            },
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        var processed = await service.ProcessMemberAsync(memberId, CancellationToken.None);

        Assert.False(processed);
        await using RadioDbContext db = fixture.CreateDbContext();
        var member = await db.ArtistMembers.SingleAsync(m => m.Id == memberId);
        Assert.Null(member.VoiceId);
        Assert.Null(member.VoiceReferencePath);
        Assert.Contains("voice booth unavailable", member.VoiceDesignLastError);
    }

    [TestMethod]
    public async Task VoicePreparation_RequeuesWhenVoiceBoothIsTemporarilyUnavailable()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        var queue = new ArtistMemberVoiceQueue();
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            queue,
            new FakeVoiceDesignClient("qwen-voice-1", WavBytes())
            {
                Exception = new VoiceDesignUnavailableException("No active local voice booth is ready."),
            },
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        var processed = await service.ProcessMemberAsync(memberId, CancellationToken.None);

        Assert.False(processed);
        Assert.Equal(memberId, queue.TryDequeue());
        await using RadioDbContext db = fixture.CreateDbContext();
        var member = await db.ArtistMembers.SingleAsync(m => m.Id == memberId);
        Assert.Null(member.VoiceId);
        Assert.Null(member.VoiceReferencePath);
        Assert.Null(member.VoiceDesignLastError);
    }

    [TestMethod]
    public async Task StartupScan_QueuesOnlyMembersWithoutDesignedVoice()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var voicelessId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        var voicedId = await AddArtistAsync(
            fixture,
            voiceId: "qwen-voice-9",
            referencePath: Path.Combine("acestep", "voice-references", "x", "y.wav"),
            lastError: null);
        var queue = new ArtistMemberVoiceQueue();
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            queue,
            new FakeVoiceDesignClient("qwen-voice-1", WavBytes()),
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        await service.EnqueuePendingMembersAsync(CancellationToken.None);

        var queued = new List<Guid>();
        while (queue.TryDequeue() is { } id)
        {
            queued.Add(id);
        }

        Assert.Contains(voicelessId, queued);
        Assert.DoesNotContain(voicedId, queued);
    }

    [TestMethod]
    public async Task StartupScan_SkipsMembersOfRetiredArtists()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            await db.Artists.ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRetired, true));
        }

        var queue = new ArtistMemberVoiceQueue();
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            queue,
            new FakeVoiceDesignClient("qwen-voice-1", WavBytes()),
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        await service.EnqueuePendingMembersAsync(CancellationToken.None);

        Assert.Null(queue.TryDequeue());
    }

    [TestMethod]
    public async Task VoicePreparation_PrefersStoredGenderOverInference()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            // Bio/voice prompt say "female alto"; the stored column must win.
            await db.ArtistMembers
                .Where(m => m.Id == memberId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Gender, "male"));
        }

        var design = new FakeVoiceDesignClient("qwen-voice-1", WavBytes());
        var service = new ArtistMemberVoicePreparationService(
            fixture,
            new ArtistMemberVoiceQueue(),
            design,
            BuildScopeFactory(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }),
            NullLogger<ArtistMemberVoicePreparationService>.Instance);

        var processed = await service.ProcessMemberAsync(memberId, CancellationToken.None);

        Assert.True(processed);
        Assert.Equal("male", design.LastGender);
    }

    [TestMethod]
    public async Task Resolver_UsesLeadVocalistSpokenReferenceWhenNoSungHistoryExists()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var referencePath = Path.Combine("acestep", "voice-references", "artist", "lead.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dataRoot.Path, referencePath))!);
        await File.WriteAllBytesAsync(Path.Combine(dataRoot.Path, referencePath), WavBytes());
        var memberId = await AddArtistAsync(fixture, "qwen-voice-1", referencePath, lastError: null);
        var artist = await LoadArtistAsync(fixture, memberId);
        var resolver = new ArtistVoiceReferenceResolver(
            new ArtistMemberVoiceQueue(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }));

        var resolution = await resolver.ResolveAsync(artist, CancellationToken.None);

        Assert.NotNull(resolution.Reference);
        Assert.Contains("spoken reference", resolution.Reference!.ReferenceAudioLabel);
        Assert.Equal(Path.Combine(dataRoot.Path, referencePath), resolution.Reference.ReferenceAudioPath);
    }

    [TestMethod]
    public async Task Resolver_QueuesLeadVocalistWhenSpokenReferenceIsMissing()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var memberId = await AddArtistAsync(fixture, voiceId: null, referencePath: null, lastError: null);
        var artist = await LoadArtistAsync(fixture, memberId);
        var queue = new ArtistMemberVoiceQueue();
        var resolver = new ArtistVoiceReferenceResolver(
            queue,
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }));

        var resolution = await resolver.ResolveAsync(artist, CancellationToken.None);

        Assert.Null(resolution.Reference);
        Assert.Equal(memberId, resolution.MissingVoice?.MemberId);
        Assert.Equal(memberId, queue.TryDequeue());
    }

    [TestMethod]
    public async Task Resolver_UsesSpokenReferenceBeforeExistingSungAceStepTracks()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        using TempDataRoot dataRoot = new();
        var spokenPath = Path.Combine("acestep", "voice-references", "artist", "lead.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dataRoot.Path, spokenPath))!);
        await File.WriteAllBytesAsync(Path.Combine(dataRoot.Path, spokenPath), WavBytes());
        var memberId = await AddArtistAsync(fixture, "qwen-voice-1", spokenPath, lastError: null);
        var artist = await LoadArtistAsync(fixture, memberId);
        var weakTrack = Path.Combine("library", "tracks", "weak.wav");
        var bestTrack = Path.Combine("library", "tracks", "best.wav");
        Directory.CreateDirectory(Path.Combine(dataRoot.Path, "library", "tracks"));
        await File.WriteAllBytesAsync(Path.Combine(dataRoot.Path, weakTrack), WavBytes());
        await File.WriteAllBytesAsync(Path.Combine(dataRoot.Path, bestTrack), WavBytes());
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            db.Tracks.AddRange(
                Track(artist.Id, "Weak Song", weakTrack, upVotes: 1, downVotes: 0, createdAt: DateTime.UtcNow.AddMinutes(-1)),
                Track(artist.Id, "Best Song", bestTrack, upVotes: 5, downVotes: 1, createdAt: DateTime.UtcNow.AddMinutes(-10)));
            await db.SaveChangesAsync();
        }

        var resolver = new ArtistVoiceReferenceResolver(
            new ArtistMemberVoiceQueue(),
            Options.Create(new RadioOptions { DataRoot = dataRoot.Path }));

        var resolution = await resolver.ResolveAsync(artist, CancellationToken.None);

        Assert.NotNull(resolution.Reference);
        Assert.Equal(Path.Combine(dataRoot.Path, spokenPath), resolution.Reference!.ReferenceAudioPath);
        Assert.Contains("spoken reference", resolution.Reference.ReferenceAudioLabel);
    }

    // The voice prep service resolves a scoped MusicCopywriter to write the spoken
    // self-introduction; a stub writer room keeps the sample-text path deterministic.
    private static IServiceScopeFactory BuildScopeFactory(string reply = "Hi, I'm a test voice, and I love analog synths.")
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITextGenerationService>(new StubLlm(reply));
        services.AddScoped<MusicCopywriter>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<Guid> AddArtistAsync(
        DbFixture fixture,
        string? voiceId,
        string? referencePath,
        string? lastError)
    {
        var artistId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await using RadioDbContext db = fixture.CreateDbContext();
        db.Artists.Add(new Artist
        {
            Id = artistId,
            Name = "Broken Signal",
            Slug = $"broken-signal-{artistId:N}",
            Genre = "electronic",
            Subgenre = "dock synth",
            StyleDescriptor = "Tape synths and direct vocals.",
            Type = "Band",
            Language = "en",
            CreatedAt = DateTime.UtcNow,
            Members =
            {
                new ArtistMember
                {
                    Id = memberId,
                    SortOrder = 0,
                    Name = "Mara Voss",
                    Role = "lead vocals",
                    Biography = "Mara writes compact dockside lyrics.",
                    VoiceCreationPrompt = "Female alto, close microphone, light Dutch accent.",
                    VoiceId = voiceId,
                    VoiceReferencePath = referencePath,
                    VoiceDesignLastError = lastError,
                },
                new ArtistMember
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 1,
                    Name = "Nils Key",
                    Role = "synths",
                    Biography = "Nils arranges the hooks.",
                    VoiceCreationPrompt = "Calm spoken male voice.",
                },
            },
        });
        await db.SaveChangesAsync();
        return memberId;
    }

    private static async Task<Artist> LoadArtistAsync(DbFixture fixture, Guid memberId)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        var artistId = await db.ArtistMembers
            .Where(m => m.Id == memberId)
            .Select(m => m.ArtistId)
            .SingleAsync();
        return await db.Artists
            .AsNoTracking()
            .Include(a => a.Members)
            .SingleAsync(a => a.Id == artistId);
    }

    private static Track Track(Guid artistId, string title, string filePath, int upVotes, int downVotes, DateTime createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Title = title,
            Genre = "electronic",
            Subgenre = "dock synth",
            Style = "vocal dock synth",
            Language = "en",
            HasVocals = true,
            FilePath = filePath,
            GenerationPrompt = "prompt",
            Backend = MusicBackends.AceStep,
            DurationSeconds = 90,
            UpVotes = upVotes,
            DownVotes = downVotes,
            CreatedAt = createdAt,
        };

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"whipradio-test-{Guid.NewGuid():N}");
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
