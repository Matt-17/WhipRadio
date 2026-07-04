using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Audio;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ConversationProductionServiceTests
{
    private const string ScriptReply = """
{
  "title": "Night Static",
  "turns": [
    { "speaker": "Nova Quinn", "text": "Welcome to the show. [pause:300ms]" },
    { "speaker": "Makoa Hale", "text": "Thanks for having me." },
    { "speaker": "Nova Quinn", "text": "Let's dive in." }
  ]
}
""";

    [TestMethod]
    public async Task Produce_TalkWithHostAndArtistMember_ReachesProducedWithTranscriptAndWav()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, memberId) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var segmentId = await SeedSegmentAsync(fixture, hostId, memberId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out _);
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Produced, segment.Status);
            Assert.Equal("Night Static", segment.Title);
            Assert.Contains("Nova Quinn: Welcome to the show.", segment.Transcript!);
            Assert.Contains("Makoa Hale: Thanks for having me.", segment.Transcript!);
            Assert.NotNull(segment.TurnsJson);
            Assert.NotNull(segment.OutputFilePath);
            Assert.True(segment.DurationSeconds > 0);

            var wavPath = Path.Combine(dataRoot, segment.OutputFilePath!);
            Assert.True(File.Exists(wavPath), $"composite WAV missing at {wavPath}");
            // 3 × 0.5 s turns + 2 gaps (0.3 s normalized to ~0.2 s + default 0.4 s) > 1.5 s total.
            Assert.True(WavFile.GetDurationSeconds(await File.ReadAllBytesAsync(wavPath)) > 1.5);

            var announcement = await db.Announcements.AsNoTracking()
                .SingleAsync(a => a.Id == segment.AnnouncementId);
            Assert.Equal(AnnouncementKind.Conversation, announcement.Kind);
            Assert.Equal(AnnouncementPlayoutIntent.ScheduledOnly, announcement.PlayoutIntent);
            Assert.Equal(hostId, announcement.ModeratorId);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Produce_MemberWithoutVoice_WaitsAndEnqueuesPriorityVoiceDesign()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, memberId) = await SeedSpeakersAsync(fixture, memberVoiceId: null);
        var segmentId = await SeedSegmentAsync(fixture, hostId, memberId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out var voiceQueue);
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Planned, segment.Status);
            Assert.Contains("Waiting for", segment.ProductionState!);
            Assert.True(voiceQueue.QueuedMemberIds().Contains(memberId), "member voice design must be queued");
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task EnsureEpisodes_CreatesOnePlannedSegmentPerUpcomingShowSlot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, memberId) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");

        Guid showId;
        await using (var db = fixture.CreateDbContext())
        {
            var now = DateTimeOffset.Now; // local — PodcastShowScheduler works on local wall-clock time
            var slotStart = now.AddMinutes(45);
            var show = new PodcastShow
            {
                Id = Guid.NewGuid(),
                Name = "Night Static Weekly",
                Brief = "Music industry talk.",
                EpisodeMinutes = 15,
                DayOfWeek = (int)slotStart.DayOfWeek,
                StartMinute = slotStart.Hour * 60 + slotStart.Minute,
                SlotDurationMinutes = 30,
                ParticipantsJson = ParticipantsJson(hostId, memberId),
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.PodcastShows.Add(show);
            await db.SaveChangesAsync();
            showId = show.Id;
        }

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out _);
            await service.RunCycleForTestsAsync(CancellationToken.None);
            // A second cycle must not create a duplicate for the same occurrence.
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var episodes = await db.ConversationSegments.AsNoTracking()
                .Where(segment => segment.PodcastShowId == showId)
                .ToListAsync();
            Assert.Equal(1, episodes.Count);
            Assert.NotNull(episodes[0].TargetUtc);
            Assert.Equal(ConversationKind.Podcast, episodes[0].Kind);
            Assert.Equal(15, episodes[0].TargetDurationMinutes);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    // --- fixture plumbing -----------------------------------------------------

    private static ConversationProductionService CreateService(
        DbFixture fixture, string dataRoot, out ArtistMemberVoiceQueue voiceQueue)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITextGenerationService>(_ => new StaticLlm(ScriptReply));
        services.AddScoped<ConversationScriptWriter>();
        services.AddScoped<ITtsEngine>(_ => new FakeTts());
        var provider = services.BuildServiceProvider();

        voiceQueue = new ArtistMemberVoiceQueue();
        return new ConversationProductionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture,
            voiceQueue,
            TimeProvider.System,
            new NoOpPublisher(),
            new NoOpMetrics(),
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            NullLogger<ConversationProductionService>.Instance);
    }

    private static async Task<(int HostId, Guid MemberId)> SeedSpeakersAsync(
        DbFixture fixture, string? memberVoiceId)
    {
        await using var db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
        }

        var host = new Moderator
        {
            Name = "Nova Quinn",
            Slug = "nova-quinn",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "Warm late-night host.",
            Style = "calm",
            IsActive = true,
            TtsEngine = TtsEngines.Qwen,
            VoiceId = "qv-host",
        };
        db.Moderators.Add(host);

        var artist = new Artist
        {
            Id = Guid.NewGuid(),
            Name = "Pacific Furnace",
            Genre = "metal",
            Biography = "A heavy band.",
            DeepBackgroundBiography = "Formed after night shifts near Hilo Bay.",
        };
        db.Artists.Add(artist);
        var member = new ArtistMember
        {
            Id = Guid.NewGuid(),
            ArtistId = artist.Id,
            Name = "Makoa Hale",
            Role = "lead vocals",
            Biography = "Writes most lyrics.",
            VoiceCreationPrompt = "Baritone, rough edge.",
            TtsEngine = TtsEngines.Qwen,
            VoiceId = memberVoiceId,
        };
        db.ArtistMembers.Add(member);
        await db.SaveChangesAsync();
        return (host.Id, member.Id);
    }

    private static async Task<Guid> SeedSegmentAsync(DbFixture fixture, int hostId, Guid memberId)
    {
        await using var db = fixture.CreateDbContext();
        var segment = new ConversationSegment
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Talk,
            Structure = ConversationStructure.Freeform,
            Topic = "The new record",
            Brief = "Dig into the writing process.",
            TargetDurationMinutes = 10,
            ParticipantsJson = ParticipantsJson(hostId, memberId),
            Status = ConversationStatus.Planned,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.ConversationSegments.Add(segment);
        await db.SaveChangesAsync();
        return segment.Id;
    }

    private static string ParticipantsJson(int hostId, Guid memberId)
        => System.Text.Json.JsonSerializer.Serialize(new List<ConversationParticipant>
        {
            new()
            {
                SpeakerKey = ConversationParticipant.HostKey(hostId),
                DisplayName = "Nova Quinn",
                ConversationRole = "Host",
            },
            new()
            {
                SpeakerKey = ConversationParticipant.MemberKey(memberId),
                DisplayName = "Makoa Hale",
                ConversationRole = "Guest",
            },
        });

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "whipradio-conversation-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticLlm(string reply) : ITextGenerationService
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(reply);
    }

    private sealed class FakeTts : ITtsEngine
    {
        public Task<TtsResult> SynthesizeAsync(string markedUpText, TtsVoiceOptions options, CancellationToken ct)
        {
            var frames = 4000; // 0.5 s at 8 kHz mono
            var pcm = new byte[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), 2000);
            }

            return Task.FromResult(new TtsResult(WavFile.WrapPcm16(pcm, 8000, 1), 0.5));
        }

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TtsVoice>>([]);
    }

    private sealed class NoOpPublisher : IProductionUpdatePublisher
    {
        public Task PublishNewsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishWeatherChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishConversationsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpMetrics : IStationMetrics
    {
        public void EncoderRestarted() { }
        public void GenerationStarted(string kind) { }
        public void GenerationSucceeded(string kind, TimeSpan elapsed) { }
        public void GenerationFailed(string kind) { }
        public void MixerTransition(string strategy, int clips) { }
    }
}
