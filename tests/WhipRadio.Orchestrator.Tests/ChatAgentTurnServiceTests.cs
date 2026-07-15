using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Prompting;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ChatAgentTurnServiceTests
{
    [TestMethod]
    public async Task RunTurnAsync_SearchMusicFeedsResultsBackBeforeFinalReply()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int hostId;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
            var host = new Moderator
            {
                Name = "Charlie Wave",
                Language = "en",
                Gender = ModeratorGenders.Male,
                PersonaPrompt = "laid back",
                Style = "late-night",
                IsActive = true,
            };
            var artist = new Artist
            {
                Id = Guid.NewGuid(),
                Name = "Neon Atlas",
                Slug = "neon-atlas",
                Genre = "electronic",
                Subgenre = "synthwave",
                StyleDescriptor = "late night synthwave",
                CreatedAt = DateTime.UtcNow,
            };
            db.Moderators.Add(host);
            db.Artists.Add(artist);
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                Artist = artist,
                Title = "Neon Rider",
                Genre = "electronic",
                Subgenre = "synthwave",
                Style = "glossy night drive",
                DurationSeconds = 185,
                CreatedAt = DateTime.UtcNow,
                FilePath = "library/tracks/neon-rider.wav",
                GenerationPrompt = "synthwave",
                Backend = "ace-step-1.5",
            });
            await db.SaveChangesAsync();
            hostId = host.Id;
        }

        var hub = new NullHubContext();
        var chat = new ChatService(fixture, hub, TimeProvider.System, NullLogger<ChatService>.Instance);
        Guid channelId = await chat.GetHostDmChannelIdAsync(hostId, CancellationToken.None)
            ?? throw new InvalidOperationException("Host DM was not created.");
        Guid correlationId = Guid.NewGuid();
        var trigger = await chat.PostAsync(
            channelId,
            ChatSenderKind.Admin,
            moderatorId: null,
            "Find a synthwave track and tell me what fits.",
            actionsJson: null,
            correlationId,
            hopCount: 0,
            CancellationToken.None);

        var catalog = new CharacterToolCatalog([new MessageTool(), new SearchMusicTool()]);
        var llm = new SequencedLlm(
            """{"reply":"I'll search the library.","actions":[{"tool":"SearchMusic","arguments":{"query":"synthwave","limit":"1"}}]}""",
            """{"reply":"Neon Atlas - Neon Rider fits the synthwave brief.","actions":[]}""");
        var turnQueue = new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance);
        var actionExecutor = new ChatActionExecutor(
            fixture,
            catalog,
            chat,
            new ChatParticipantResolver(fixture),
            turnQueue,
            new TrackQueryService(fixture),
            new MusicProductionControl(),
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
        var turn = new ChatAgentTurnService(
            fixture,
            new StaticPromptContextBuilder(catalog),
            llm,
            new ChatReplyParser(),
            chat,
            actionExecutor,
            new ChatParticipantResolver(fixture),
            new ChatResponderResolver(fixture, turnQueue, NullLogger<ChatResponderResolver>.Instance),
            new AgentActionLogService(fixture, hub, TimeProvider.System, NullLogger<AgentActionLogService>.Instance),
            hub,
            NullLogger<ChatAgentTurnService>.Instance);

        await turn.RunTurnAsync(
            new ChatTurnRequest(channelId, ChatParticipantRef.ForHost(hostId), trigger.Id, correlationId, 0),
            CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.ChatMessages.AsNoTracking()
            .Where(message => message.ChannelId == channelId)
            .OrderBy(message => message.CreatedAtUtc)
            .ToListAsync();

        // The lookup round stays internal (logs only); chat gets the trigger and
        // the single final reply.
        Assert.Equal(2, messages.Count);
        Assert.Contains("Neon Rider fits", messages[1].Text);
        Assert.Null(messages[1].ActionsJson);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("SearchMusic -> Succeeded", llm.Requests[1].UserPrompt);
        Assert.Contains("Neon Atlas - Neon Rider", llm.Requests[1].UserPrompt);
    }

    [TestMethod]
    public async Task RunTurnAsync_FailedActionFeedsBackAndAgentAdmitsIt()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int hostId;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
            var host = new Moderator
            {
                Name = "Charlie Wave",
                Language = "en",
                Gender = ModeratorGenders.Male,
                PersonaPrompt = "laid back",
                Style = "late-night",
                IsActive = true,
            };
            db.Moderators.Add(host);
            await db.SaveChangesAsync();
            hostId = host.Id;
        }

        var hub = new NullHubContext();
        var chat = new ChatService(fixture, hub, TimeProvider.System, NullLogger<ChatService>.Instance);
        Guid channelId = await chat.GetHostDmChannelIdAsync(hostId, CancellationToken.None)
            ?? throw new InvalidOperationException("Host DM was not created.");
        Guid correlationId = Guid.NewGuid();
        var trigger = await chat.PostAsync(
            channelId,
            ChatSenderKind.Admin,
            moderatorId: null,
            "Tell Jenny to prepare a segment.",
            actionsJson: null,
            correlationId,
            hopCount: 0,
            CancellationToken.None);

        var catalog = new CharacterToolCatalog([new MessageTool()]);
        var llm = new SequencedLlm(
            """{"reply":"Sure, I'll pass that on.","actions":[{"tool":"Message","arguments":{"characterId":"Jenny","message":"Prepare a segment please."}}]}""",
            """{"reply":"Sorry, there is no Jenny at the station right now.","actions":[]}""");
        var turnQueue = new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance);
        var actionExecutor = new ChatActionExecutor(
            fixture,
            catalog,
            chat,
            new ChatParticipantResolver(fixture),
            turnQueue,
            new TrackQueryService(fixture),
            new MusicProductionControl(),
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
        var turn = new ChatAgentTurnService(
            fixture,
            new StaticPromptContextBuilder(catalog),
            llm,
            new ChatReplyParser(),
            chat,
            actionExecutor,
            new ChatParticipantResolver(fixture),
            new ChatResponderResolver(fixture, turnQueue, NullLogger<ChatResponderResolver>.Instance),
            new AgentActionLogService(fixture, hub, TimeProvider.System, NullLogger<AgentActionLogService>.Instance),
            hub,
            NullLogger<ChatAgentTurnService>.Instance);

        await turn.RunTurnAsync(
            new ChatTurnRequest(channelId, ChatParticipantRef.ForHost(hostId), trigger.Id, correlationId, 0),
            CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.ChatMessages.AsNoTracking()
            .Where(message => message.ChannelId == channelId)
            .OrderBy(message => message.CreatedAtUtc)
            .ToListAsync();

        // The failed round never reaches the chat; the agent gets the failure fed
        // back and answers honestly in one final message.
        Assert.Equal(2, messages.Count);
        Assert.Contains("no Jenny", messages[1].Text);
        Assert.Null(messages[1].ActionsJson);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("Message -> Failed", llm.Requests[1].UserPrompt);
    }

    [TestMethod]
    public async Task RunTurnAsync_ArtistMemberResponder_PostsAsArtistMemberSender()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid memberId;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });
            var artist = new Artist
            {
                Id = Guid.NewGuid(),
                Name = "Pacific Furnace",
                Slug = "pacific-furnace",
                Genre = "metal",
                DeepBackgroundBiography = "Formed near Hilo Bay.",
                CreatedAt = DateTime.UtcNow,
            };
            var member = new ArtistMember
            {
                Id = Guid.NewGuid(),
                Artist = artist,
                Name = "Makoa Hale",
                Role = "lead vocals",
                Biography = "Writes most lyrics.",
                Personality = "Intense and protective.",
                Interests = "lava field hikes",
                VoiceCreationPrompt = "Baritone.",
            };
            db.Artists.Add(artist);
            db.ArtistMembers.Add(member);
            await db.SaveChangesAsync();
            memberId = member.Id;
        }

        var hub = new NullHubContext();
        var chat = new ChatService(fixture, hub, TimeProvider.System, NullLogger<ChatService>.Instance);
        ChatChannelDto group = await chat.CreateGroupChannelAsync(
            "Band Talk",
            [(ChatParticipantRef.ForArtistMember(memberId), "Makoa Hale")],
            CancellationToken.None);
        Guid correlationId = Guid.NewGuid();
        var trigger = await chat.PostAsync(
            group.Id,
            ChatSenderKind.Admin,
            moderatorId: null,
            "Makoa Hale, how is the new record coming along?",
            actionsJson: null,
            correlationId,
            hopCount: 0,
            CancellationToken.None);

        var catalog = new CharacterToolCatalog([new MessageTool()]);
        var llm = new SequencedLlm(
            """{"reply":"Slow and heavy, just like the lava.","actions":[]}""");
        var turnQueue = new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance);
        var actionExecutor = new ChatActionExecutor(
            fixture,
            catalog,
            chat,
            new ChatParticipantResolver(fixture),
            turnQueue,
            new TrackQueryService(fixture),
            new MusicProductionControl(),
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
        var turn = new ChatAgentTurnService(
            fixture,
            new StaticPromptContextBuilder(catalog),
            llm,
            new ChatReplyParser(),
            chat,
            actionExecutor,
            new ChatParticipantResolver(fixture),
            new ChatResponderResolver(fixture, turnQueue, NullLogger<ChatResponderResolver>.Instance),
            new AgentActionLogService(fixture, hub, TimeProvider.System, NullLogger<AgentActionLogService>.Instance),
            hub,
            NullLogger<ChatAgentTurnService>.Instance);

        await turn.RunTurnAsync(
            new ChatTurnRequest(group.Id, ChatParticipantRef.ForArtistMember(memberId), trigger.Id, correlationId, 0),
            CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var reply = await verify.ChatMessages.AsNoTracking()
            .Where(message => message.ChannelId == group.Id && message.SenderKind == ChatSenderKind.ArtistMember)
            .SingleAsync();
        Assert.Equal(memberId, reply.SenderArtistMemberId);
        Assert.Null(reply.SenderModeratorId);
        Assert.Contains("lava", reply.Text);
    }

    private sealed class StaticPromptContextBuilder(ICharacterToolCatalog catalog) : IPromptContextBuilder
    {
        public Task<PromptContext> BuildAsync(PromptContextInput input, CancellationToken ct)
            => Task.FromResult(new PromptContext
            {
                Scope = input.Scope,
                Purpose = input.Purpose ?? string.Empty,
                StationName = "WhipRadio",
                FrequencyMhz = 99.7,
                LocalNow = new DateTimeOffset(2026, 7, 1, 22, 0, 0, TimeSpan.Zero),
                Language = "en",
                HostName = input.Moderator?.Name,
                ChatAudience = input.ChatCounterpartName,
                Tools = catalog.GetTools(PromptScope.Chat, CharacterRole.Host),
            });
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

    private sealed class NoOpNotificationBus : INotificationBus
    {
        public Task PublishAsync(StationNotification notification, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullHubContext : IHubContext<RadioHub>
    {
        public IHubClients Clients { get; } = new NullHubClients();

        public IGroupManager Groups { get; } = new NullGroupManager();
    }

    private sealed class NullHubClients : IHubClients
    {
        public IClientProxy All { get; } = NullClientProxy.Instance;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public IClientProxy Client(string connectionId) => NullClientProxy.Instance;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullClientProxy.Instance;

        public IClientProxy Group(string groupName) => NullClientProxy.Instance;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;

        public IClientProxy User(string userId) => NullClientProxy.Instance;

        public IClientProxy Users(IReadOnlyList<string> userIds) => NullClientProxy.Instance;
    }

    private sealed class NullClientProxy : IClientProxy
    {
        public static readonly NullClientProxy Instance = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
