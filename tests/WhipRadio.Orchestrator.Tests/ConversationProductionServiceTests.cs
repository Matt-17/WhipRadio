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
    public async Task Produce_DirectorFailure_FallsBackToSingleCallWriterWithDegradationReason()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, memberId) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var segmentId = await SeedSegmentAsync(fixture, hostId, memberId);

        var dataRoot = TestRoot();
        try
        {
            // The static reply only matches the single-call script schema, so the
            // multi-agent director's plan call fails and production degrades.
            var service = CreateService(fixture, dataRoot, out _);
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Produced, segment.Status);
            Assert.NotNull(segment.DegradationReason);
            Assert.Contains("single-call writer", segment.DegradationReason);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Produce_MultiAgentDirector_ScriptsWithoutDegradation()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, memberId) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var segmentId = await SeedSegmentAsync(fixture, hostId, memberId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateSequencedService(
                fixture,
                dataRoot,
                """{"title":"Night Static","chapters":[{"title":"Opening","intent":"Set the scene."}]}""",
                """{"text":"Welcome to the show, Makoa Hale.","wrapUp":false}""",
                """{"text":"Thanks for having me.","wrapUp":false}""",
                """{"text":"That's the show — good night.","wrapUp":true}""");
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Produced, segment.Status);
            Assert.Null(segment.DegradationReason);
            Assert.Equal("Night Static", segment.Title);
            Assert.Contains("Makoa Hale: Thanks for having me.", segment.Transcript!);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Produce_GuestWithoutVoice_WaitsAndEnqueuesPriorityGuestVoiceDesign()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, _) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var guestId = await SeedGuestAsync(fixture, voiceId: null);
        var segmentId = await SeedGuestSegmentAsync(fixture, hostId, guestId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out _, out var guestQueue);
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Planned, segment.Status);
            Assert.Contains("Waiting for Ivy Sparks", segment.ProductionState!);
            Assert.True(guestQueue.QueuedGuestIds().Contains(guestId), "guest voice design must be queued");
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Produce_TalkWithVoicedGuest_ReachesProduced()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, _) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var guestId = await SeedGuestAsync(fixture, voiceId: "qv-ivy");
        var segmentId = await SeedGuestSegmentAsync(fixture, hostId, guestId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out _, out _, """
{
  "title": "Beekeeping After Dark",
  "turns": [
    { "speaker": "Nova Quinn", "text": "Ivy, welcome." },
    { "speaker": "Ivy Sparks", "text": "Happy to be here." },
    { "speaker": "Nova Quinn", "text": "Tell us about the rooftops." }
  ]
}
""");
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Produced, segment.Status);
            Assert.Contains("Ivy Sparks: Happy to be here.", segment.Transcript!);
            Assert.NotNull(segment.OutputFilePath);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Produce_GuestWithTelephoneFx_FiltersOnlyTheGuestTurns()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var (hostId, _) = await SeedSpeakersAsync(fixture, memberVoiceId: "qv-guest");
        var guestId = await SeedGuestAsync(fixture, voiceId: "qv-ivy", voiceFx: "telephone");
        var segmentId = await SeedGuestSegmentAsync(fixture, hostId, guestId);

        var dataRoot = TestRoot();
        try
        {
            var service = CreateService(fixture, dataRoot, out _, out _, """
{
  "title": "Beekeeping After Dark",
  "turns": [
    { "speaker": "Nova Quinn", "text": "Ivy, welcome." },
    { "speaker": "Ivy Sparks", "text": "Happy to be here." },
    { "speaker": "Nova Quinn", "text": "Tell us about the rooftops." }
  ]
}
""");
            await service.RunCycleForTestsAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var segment = await db.ConversationSegments.AsNoTracking().SingleAsync(s => s.Id == segmentId);
            Assert.Equal(ConversationStatus.Produced, segment.Status);

            // FakeTts emits constant DC (2000); the telephone high-pass removes
            // DC, so the guest's turn decays to silence while host turns keep it.
            var wav = await File.ReadAllBytesAsync(Path.Combine(dataRoot, segment.OutputFilePath!));
            var audio = WavFile.ParsePcm16Audio(wav);
            short SampleAtFrame(long frame)
                => BinaryPrimitives.ReadInt16LittleEndian(
                    audio.Data.Span[(int)(frame * audio.BytesPerFrame)..]);

            // Turn layout at 8 kHz mono: host 0..4000, gap 3200, guest 7200..11200.
            Assert.Equal((short)2000, SampleAtFrame(2000));
            var guestTail = Math.Abs((int)SampleAtFrame(7200 + 3800));
            Assert.True(guestTail < 200, $"guest turn still carries DC ({guestTail}) — fx not applied");
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
        => CreateService(fixture, dataRoot, out voiceQueue, out _);

    private static ConversationProductionService CreateService(
        DbFixture fixture,
        string dataRoot,
        out ArtistMemberVoiceQueue voiceQueue,
        out GuestVoiceQueue guestVoiceQueue,
        string? scriptReply = null)
        => CreateServiceCore(fixture, dataRoot, _ => new StaticLlm(scriptReply ?? ScriptReply), out voiceQueue, out guestVoiceQueue);

    private static ConversationProductionService CreateSequencedService(
        DbFixture fixture,
        string dataRoot,
        params string[] replies)
    {
        var llm = new SequencedLlm(replies);
        return CreateServiceCore(fixture, dataRoot, _ => llm, out _, out _);
    }

    private static ConversationProductionService CreateServiceCore(
        DbFixture fixture,
        string dataRoot,
        Func<IServiceProvider, ITextGenerationService> llmFactory,
        out ArtistMemberVoiceQueue voiceQueue,
        out GuestVoiceQueue guestVoiceQueue)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(llmFactory);
        services.AddScoped<ConversationScriptWriter>();
        services.AddScoped<ConversationDirector>();
        services.AddSingleton<WhipRadio.Core.Conversations.ITurnTakingPolicy,
            WhipRadio.Core.Conversations.AddressedToRoundRobinPolicy>();
        services.AddScoped<ITtsEngine>(_ => new FakeTts());
        var provider = services.BuildServiceProvider();

        voiceQueue = new ArtistMemberVoiceQueue();
        guestVoiceQueue = new GuestVoiceQueue();
        var embedding = new StubEmbedding();
        return new ConversationProductionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture,
            voiceQueue,
            guestVoiceQueue,
            new ParticipantMemoryWriter(
                fixture,
                embedding,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new WhipRadio.Infrastructure.Llm.LlmOptions()),
                NullLogger<ParticipantMemoryWriter>.Instance),
            new ParticipantMemoryRetriever(fixture, embedding, NullLogger<ParticipantMemoryRetriever>.Instance),
            new KnowledgeContextResolver(
                fixture,
                new WhipRadio.Infrastructure.Persistence.StationSettingsCache(fixture, TimeProvider.System),
                NullLogger<KnowledgeContextResolver>.Instance),
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

    private static async Task<Guid> SeedGuestAsync(DbFixture fixture, string? voiceId, string? voiceFx = null)
    {
        await using var db = fixture.CreateDbContext();
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            Name = "Ivy Sparks",
            Slug = $"ivy-sparks-{Guid.NewGuid():N}",
            Expertise = "urban beekeeper",
            Gender = "female",
            Biography = "Keeps hives on downtown rooftops.",
            DeepBackground = "Started with two hives on a parking garage.",
            VoiceCreationPrompt = "Bright, quick, enthusiastic.",
            TtsEngine = TtsEngines.Qwen,
            VoiceId = voiceId,
            VoiceFx = voiceFx,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest.Id;
    }

    private static async Task<Guid> SeedGuestSegmentAsync(DbFixture fixture, int hostId, Guid guestId)
    {
        await using var db = fixture.CreateDbContext();
        var segment = new ConversationSegment
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Talk,
            Structure = ConversationStructure.Freeform,
            Topic = "City bees",
            Brief = "Rooftop hives and honey.",
            TargetDurationMinutes = 10,
            ParticipantsJson = System.Text.Json.JsonSerializer.Serialize(new List<ConversationParticipant>
            {
                new()
                {
                    SpeakerKey = ConversationParticipant.HostKey(hostId),
                    DisplayName = "Nova Quinn",
                    ConversationRole = "Host",
                },
                new()
                {
                    SpeakerKey = ConversationParticipant.GuestKey(guestId),
                    DisplayName = "Ivy Sparks",
                    ConversationRole = "Guest",
                },
            }),
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

    private sealed class StubEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
            => Task.FromResult(new float[] { 1f, 0f, 0f });
    }

    private sealed class SequencedLlm(string[] replies) : ITextGenerationService
    {
        private readonly Queue<string> _replies = new(replies);

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(_replies.Dequeue());
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
        public Task PublishArchiveChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
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
