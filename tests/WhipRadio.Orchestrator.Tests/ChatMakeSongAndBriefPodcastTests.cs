using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Prompting;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ChatMakeSongAndBriefPodcastTests
{
    [TestMethod]
    public async Task MakeSong_ArtistSenderQueuesOwnBandWithHint()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        MusicProductionControl control = new();
        ChatActionExecutor executor = CreateExecutor(fixture, control);
        ChatParticipant sender = await new ChatParticipantResolver(fixture)
            .ResolveAsync(ChatParticipantRef.ForArtistMember(seeded.MemberId), CancellationToken.None)
            ?? throw new InvalidOperationException("Member not resolved.");
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, sender);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("MakeSong", new Dictionary<string, string> { ["hint"] = "an indie track about ferries" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        ManualSongRequest? queued = control.TryDequeueManualRequest();
        Assert.NotNull(queued);
        Assert.Equal(seeded.ArtistId, queued!.ArtistId);
        Assert.Equal("an indie track about ferries", queued.Hint);
    }

    [TestMethod]
    public async Task MakeSong_DirectorNamesTheArtist()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        MusicProductionControl control = new();
        ChatActionExecutor executor = CreateExecutor(fixture, control);
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("MakeSong", new Dictionary<string, string> { ["artist"] = "Pacific Furnace" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Equal(seeded.ArtistId, control.TryDequeueManualRequest()?.ArtistId);
    }

    [TestMethod]
    public async Task MakeSong_DirectorWithUnknownArtistFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        MusicProductionControl control = new();
        ChatActionExecutor executor = CreateExecutor(fixture, control);
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("MakeSong", new Dictionary<string, string> { ["artist"] = "The Nonexistents" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Null(control.TryDequeueManualRequest());
    }

    [TestMethod]
    public async Task BriefPodcast_CreatesSegmentWithSpeakersAndReferencedTracks()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        ChatActionExecutor executor = CreateExecutor(fixture, new MusicProductionControl());
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("BriefPodcast", new Dictionary<string, string>
            {
                ["participants"] = "Nova Quinn, Ivy Sparks",
                ["topic"] = "City bees",
                ["brief"] = "Rooftop hives and honey.",
                ["tracks"] = "Neon Rider",
                ["durationMinutes"] = "12",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        ConversationSegment segment = await db.ConversationSegments.AsNoTracking().SingleAsync();
        Assert.Equal(ConversationStatus.Planned, segment.Status);
        Assert.Equal("City bees", segment.Topic);
        Assert.Equal(12, segment.TargetDurationMinutes);
        Assert.Null(segment.TargetUtc);
        Assert.Contains("Neon Rider", segment.Brief);
        Assert.Contains(seeded.TrackId.ToString(), segment.ReferencedTrackIdsJson);
        Assert.Contains("host:", segment.ParticipantsJson);
        Assert.Contains("guest:", segment.ParticipantsJson);
    }

    [TestMethod]
    public async Task BriefPodcast_BandNameExpandsToVoicedMembers()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        ChatActionExecutor executor = CreateExecutor(fixture, new MusicProductionControl());
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("BriefPodcast", new Dictionary<string, string>
            {
                ["participants"] = "Nova Quinn, Pacific Furnace",
                ["topic"] = "The new record",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        ConversationSegment segment = await db.ConversationSegments.AsNoTracking().SingleAsync();
        // Only the voiced member joins; the voiceless drummer stays out.
        Assert.Contains("Makoa Hale", segment.ParticipantsJson);
        Assert.DoesNotContain("Tem Kline", segment.ParticipantsJson);
    }

    [TestMethod]
    public async Task BriefPodcast_FailsWhenBandHasNoVoicedMembers()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture, voiceLeadVocalist: false);
        ChatActionExecutor executor = CreateExecutor(fixture, new MusicProductionControl());
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("BriefPodcast", new Dictionary<string, string>
            {
                ["participants"] = "Nova Quinn, Pacific Furnace",
                ["topic"] = "The new record",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Contains("no members with a designed voice", record.ResultSummary);
    }

    [TestMethod]
    public async Task BriefPodcast_FailsWithTooFewSpeakers()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        ChatActionExecutor executor = CreateExecutor(fixture, new MusicProductionControl());
        ChatActionContext context = await BuildContextAsync(fixture, seeded.GroupChannelId, ChatParticipantResolver.Director);

        ChatActionRecord record = await executor.ExecuteAsync(
            new CharacterToolCall("BriefPodcast", new Dictionary<string, string>
            {
                ["participants"] = "Nova Quinn",
                ["topic"] = "Monologue",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Contains("2-5 speakers", record.ResultSummary);
    }

    [TestMethod]
    public async Task ReferencedTracks_FrontQueueInReverseSoEpisodeLeads()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Guid secondTrackId;
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            Track second = NewTrack(seeded.ArtistId, "Harbor Lights");
            db.Tracks.Add(second);
            await db.SaveChangesAsync();
            secondTrackId = second.Id;
        }

        ConversationSegment segment = new()
        {
            Id = Guid.NewGuid(),
            Topic = "City bees",
            ReferencedTrackIdsJson = System.Text.Json.JsonSerializer.Serialize(
                new List<Guid> { seeded.TrackId, secondTrackId }),
        };
        FakePlayoutQueue queue = new();

        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            await ConversationDispatcher.EnqueueReferencedTracksAsync(
                db, queue, segment, NullLogger<ConversationDispatcher>.Instance, CancellationToken.None);
        }

        // The caller pushes the episode on top afterwards.
        queue.EnqueueFront(new PlayoutItem(PlayoutItemType.Announcement, Guid.NewGuid(), "episode.wav", "Episode", 600));

        Assert.Equal(3, queue.Items.Count);
        Assert.Equal("Episode", queue.Items[0].Title);
        Assert.Contains("Neon Rider", queue.Items[1].Title);
        Assert.Contains("Harbor Lights", queue.Items[2].Title);
    }

    private sealed record Seeded(int HostId, Guid ArtistId, Guid MemberId, Guid GuestId, Guid TrackId, Guid GroupChannelId);

    private static async Task<Seeded> SeedAsync(DbFixture fixture, bool voiceLeadVocalist = true)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        if (!await db.StationSettings.AnyAsync())
        {
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
        }

        Moderator host = new()
        {
            Name = "Nova Quinn",
            Slug = $"nova-quinn-{Guid.NewGuid():N}",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "Warm late-night host.",
            Style = "calm",
            IsActive = true,
        };
        db.Moderators.Add(host);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Pacific Furnace",
            Slug = $"pacific-furnace-{Guid.NewGuid():N}",
            Genre = "metal",
            DeepBackgroundBiography = "Formed near Hilo Bay.",
        };
        db.Artists.Add(artist);
        ArtistMember vocalist = new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artist.Id,
            SortOrder = 0,
            Name = "Makoa Hale",
            Role = "lead vocals",
            Biography = "Writes most lyrics.",
            VoiceCreationPrompt = "Baritone.",
            VoiceId = voiceLeadVocalist ? "qv-makoa" : null,
        };
        ArtistMember drummer = new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artist.Id,
            SortOrder = 1,
            Name = "Tem Kline",
            Role = "drums",
            Biography = "Builds the percussion rig.",
            VoiceCreationPrompt = "Clipped.",
        };
        db.ArtistMembers.AddRange(vocalist, drummer);

        Guest guest = new()
        {
            Id = Guid.NewGuid(),
            Name = "Ivy Sparks",
            Slug = $"ivy-sparks-{Guid.NewGuid():N}",
            Expertise = "urban beekeeper",
            Biography = "Keeps hives on rooftops.",
            DeepBackground = "Left a lab job for bees.",
            VoiceCreationPrompt = "Bright and quick.",
            VoiceId = "qv-ivy",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Guests.Add(guest);

        Track track = NewTrack(artist.Id, "Neon Rider");
        db.Tracks.Add(track);

        ChatChannel channel = new()
        {
            Id = Guid.NewGuid(),
            Kind = ChatChannelKind.Group,
            Name = "Studio Group",
        };
        db.ChatChannels.Add(channel);

        await db.SaveChangesAsync();
        return new Seeded(host.Id, artist.Id, vocalist.Id, guest.Id, track.Id, channel.Id);
    }

    private static Track NewTrack(Guid artistId, string title)
        => new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Title = title,
            Genre = "metal",
            Subgenre = "doom",
            Style = "heavy",
            Language = "en",
            FilePath = $"library/tracks/{Guid.NewGuid():N}.wav",
            GenerationPrompt = "prompt",
            Backend = "ace-step",
            DurationSeconds = 180,
            SongStory = "Written after a night shift.",
            CreatedAt = DateTime.UtcNow,
        };

    private static ChatActionExecutor CreateExecutor(DbFixture fixture, MusicProductionControl control)
        => new(
            fixture,
            new CharacterToolCatalog([new MakeSongTool(), new BriefPodcastTool()]),
            new ChatService(fixture, new NullHubContext(), TimeProvider.System, NullLogger<ChatService>.Instance),
            new ChatParticipantResolver(fixture),
            new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance),
            new TrackQueryService(fixture),
            control,
            priorityDispatcher: null!,
            schedule: null!,
            director: null!,
            new NoOpNotificationBus(),
            scopeFactory: null!,
            playoutQueue: null!,
            moderatorMemory: null!,
            participantMemory: null!,
            productionUpdates: null!,
            socialFeed: null!,
            newsProduction: null!,
            hub: null!,
            TimeProvider.System,
            NullLogger<ChatActionExecutor>.Instance);

    private static async Task<ChatActionContext> BuildContextAsync(DbFixture fixture, Guid channelId, ChatParticipant sender)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        ChatChannel channel = await db.ChatChannels.AsNoTracking().FirstAsync(item => item.Id == channelId);
        return new ChatActionContext(channel, AgentMessage: null, sender, Guid.NewGuid(), HopCount: 0);
    }

    private sealed class NoOpNotificationBus : INotificationBus
    {
        public Task PublishAsync(StationNotification notification, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullHubContext : Microsoft.AspNetCore.SignalR.IHubContext<Api.RadioHub>
    {
        public Microsoft.AspNetCore.SignalR.IHubClients Clients { get; } = new NullHubClients();

        public Microsoft.AspNetCore.SignalR.IGroupManager Groups { get; } = new NullGroupManager();
    }

    private sealed class NullHubClients : Microsoft.AspNetCore.SignalR.IHubClients
    {
        public Microsoft.AspNetCore.SignalR.IClientProxy All { get; } = NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => NullClientProxy.Instance;

        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => NullClientProxy.Instance;
    }

    private sealed class NullClientProxy : Microsoft.AspNetCore.SignalR.IClientProxy
    {
        public static readonly NullClientProxy Instance = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullGroupManager : Microsoft.AspNetCore.SignalR.IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakePlayoutQueue : IPlayoutQueue
    {
        public List<PlayoutItem> Items { get; } = [];

        public void Enqueue(PlayoutItem item) => Items.Add(item);

        public void EnqueueFront(PlayoutItem item) => Items.Insert(0, item);

        public PlayoutItem? PeekNext() => Items.FirstOrDefault();

        public Task<PlayoutItem> DequeueAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public int Count => Items.Count;
    }
}
