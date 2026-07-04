using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ChatGroupChannelTests
{
    [TestMethod]
    public async Task CreateGroupChannel_PersistsMembersAndReturnsDto()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, Guid memberId, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);

        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            "Bee Talk",
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForArtistMember(memberId), "Makoa Hale"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);

        Assert.Equal("Group", dto.Kind);
        Assert.Equal("Bee Talk", dto.Name);
        Assert.NotNull(dto.Members);
        Assert.Equal(3, dto.Members!.Count);
        Assert.Contains(dto.Members, member => member.DisplayName == "Ivy Sparks" && member.Kind == "Guest");

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.Equal(3, await db.ChatChannelMembers.CountAsync(member => member.ChannelId == dto.Id));
    }

    [TestMethod]
    public async Task AddMember_IsIdempotentPerParticipant()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, Guid memberId, _) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [(ChatParticipantRef.ForHost(hostId), "Nova Quinn")],
            CancellationToken.None);

        Assert.True(await chat.AddMemberAsync(dto.Id, ChatParticipantRef.ForArtistMember(memberId), "Makoa Hale", CancellationToken.None));
        Assert.False(await chat.AddMemberAsync(dto.Id, ChatParticipantRef.ForArtistMember(memberId), "Makoa Hale", CancellationToken.None));
    }

    [TestMethod]
    public async Task RemoveMemberById_RemovesTheRow()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, Guid memberId, _) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForArtistMember(memberId), "Makoa Hale"),
            ],
            CancellationToken.None);
        ChatChannelMemberDto target = dto.Members!.Single(member => member.DisplayName == "Makoa Hale");

        Assert.True(await chat.RemoveMemberByIdAsync(dto.Id, target.Id, CancellationToken.None));
        Assert.False(await chat.RemoveMemberByIdAsync(dto.Id, target.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task AdminMessage_MentioningGuestMember_EnqueuesGuestTurn()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);
        ChatTurnQueue queue = new(NullLogger<ChatTurnQueue>.Instance);
        ChatResponderResolver resolver = new(fixture, queue, NullLogger<ChatResponderResolver>.Instance);
        ChatMessageDto message = await chat.PostAsync(
            dto.Id, ChatSenderKind.Admin, null, "Ivy Sparks, how are the rooftop hives doing?",
            null, Guid.NewGuid(), 0, CancellationToken.None);

        Assert.True(await resolver.TryEnqueueForAdminMessageAsync(message, CancellationToken.None));

        ChatTurnRequest request = await ReadOneAsync(queue);
        Assert.Equal(ChatParticipantRef.ForGuest(guestId), request.Responder);
    }

    [TestMethod]
    public async Task AdminMessage_WithoutMention_StaysSilentInGroup()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);
        ChatTurnQueue queue = new(NullLogger<ChatTurnQueue>.Instance);
        ChatResponderResolver resolver = new(fixture, queue, NullLogger<ChatResponderResolver>.Instance);
        ChatMessageDto message = await chat.PostAsync(
            dto.Id, ChatSenderKind.Admin, null, "Nice weather today.",
            null, Guid.NewGuid(), 0, CancellationToken.None);

        Assert.False(await resolver.TryEnqueueForAdminMessageAsync(message, CancellationToken.None));
    }

    [TestMethod]
    public async Task AgentMessage_AddressingAnotherMember_ChainsWithinHopCap()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);
        ChatTurnQueue queue = new(NullLogger<ChatTurnQueue>.Instance);
        ChatResponderResolver resolver = new(fixture, queue, NullLogger<ChatResponderResolver>.Instance);

        // The host addresses the guest by name — the guest gets the next turn.
        ChatMessageDto hostMessage = await chat.PostAsync(
            dto.Id, ChatSenderKind.Host, hostId, "Ivy Sparks, what do the bees think?",
            null, Guid.NewGuid(), 0, CancellationToken.None);
        Assert.True(await resolver.TryEnqueueForAgentMessageAsync(hostMessage, CancellationToken.None));
        ChatTurnRequest request = await ReadOneAsync(queue);
        Assert.Equal(ChatParticipantRef.ForGuest(guestId), request.Responder);
        Assert.Equal(1, request.HopCount);

        // At the hop cap the exchange stops.
        ChatMessageDto cappedMessage = await chat.PostAsync(
            dto.Id, ChatSenderKind.Host, hostId, "Ivy Sparks, one more thing.",
            null, Guid.NewGuid(), 99, CancellationToken.None);
        Assert.False(await resolver.TryEnqueueForAgentMessageAsync(cappedMessage, CancellationToken.None));
    }

    [TestMethod]
    public async Task AgentMessage_MentioningOwnName_DoesNotSelfTrigger()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);
        ChatTurnQueue queue = new(NullLogger<ChatTurnQueue>.Instance);
        ChatResponderResolver resolver = new(fixture, queue, NullLogger<ChatResponderResolver>.Instance);
        ChatMessageDto message = await chat.PostAsync(
            dto.Id, ChatSenderKind.Host, hostId, "I'm Nova Quinn and I love this show.",
            null, Guid.NewGuid(), 0, CancellationToken.None);

        Assert.False(await resolver.TryEnqueueForAgentMessageAsync(message, CancellationToken.None));
    }

    [TestMethod]
    public async Task ParticipantResolver_BuildsPersonaForMemberAndGuest()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (_, Guid memberId, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatParticipantResolver resolver = new(fixture);

        ChatParticipant? member = await resolver.ResolveAsync(ChatParticipantRef.ForArtistMember(memberId), CancellationToken.None);
        Assert.NotNull(member);
        Assert.Equal(CharacterRole.Artist, member!.Role);
        Assert.Contains("lead vocals of Pacific Furnace", member.PersonaSummary);
        Assert.Contains("lava field hikes", member.PersonaSummary);

        ChatParticipant? guest = await resolver.ResolveAsync(ChatParticipantRef.ForGuest(guestId), CancellationToken.None);
        Assert.NotNull(guest);
        Assert.Equal(CharacterRole.Guest, guest!.Role);
        Assert.Contains("urban beekeeper", guest.PersonaSummary);

        ChatParticipant? byName = await resolver.ResolveByNameAsync("ivy sparks", CancellationToken.None);
        Assert.NotNull(byName);
        Assert.Equal(ChatParticipantKind.Guest, byName!.Kind);
    }

    [TestMethod]
    public async Task InviteAction_AddsGuestToCurrentGroupChannel()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            "Bee Talk",
            [(ChatParticipantRef.ForHost(hostId), "Nova Quinn")],
            CancellationToken.None);
        ChatActionExecutor executor = CreateExecutor(fixture, chat);
        ChatActionContext context = await BuildDirectorContextAsync(fixture, dto.Id);

        ChatActionRecord record = await executor.ExecuteAsync(
            new WhipRadio.Core.Prompting.CharacterToolCall(
                "Invite", new Dictionary<string, string> { ["participant"] = "Ivy Sparks" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True(await db.ChatChannelMembers.AnyAsync(member =>
            member.ChannelId == dto.Id && member.GuestId == guestId));
    }

    [TestMethod]
    public async Task InviteAction_FailsOutsideGroupChannelsWithoutChannelArgument()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, _) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        Guid dmChannelId = await chat.GetHostDmChannelIdAsync(hostId, CancellationToken.None)
            ?? throw new InvalidOperationException("Host DM missing.");
        ChatActionExecutor executor = CreateExecutor(fixture, chat);
        ChatActionContext context = await BuildDirectorContextAsync(fixture, dmChannelId);

        ChatActionRecord record = await executor.ExecuteAsync(
            new WhipRadio.Core.Prompting.CharacterToolCall(
                "Invite", new Dictionary<string, string> { ["participant"] = "Ivy Sparks" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Contains("not a group channel", record.ResultSummary);
    }

    [TestMethod]
    public async Task RemoveFromChannelAction_RemovesMember()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        (int hostId, _, Guid guestId) = await SeedPeopleAsync(fixture);
        ChatService chat = CreateChatService(fixture);
        ChatChannelDto dto = await chat.CreateGroupChannelAsync(
            null,
            [
                (ChatParticipantRef.ForHost(hostId), "Nova Quinn"),
                (ChatParticipantRef.ForGuest(guestId), "Ivy Sparks"),
            ],
            CancellationToken.None);
        ChatActionExecutor executor = CreateExecutor(fixture, chat);
        ChatActionContext context = await BuildDirectorContextAsync(fixture, dto.Id);

        ChatActionRecord record = await executor.ExecuteAsync(
            new WhipRadio.Core.Prompting.CharacterToolCall(
                "RemoveFromChannel", new Dictionary<string, string> { ["participant"] = "Ivy Sparks" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.False(await db.ChatChannelMembers.AnyAsync(member =>
            member.ChannelId == dto.Id && member.GuestId == guestId));
    }

    private static ChatActionExecutor CreateExecutor(DbFixture fixture, ChatService chat)
        => new(
            fixture,
            new WhipRadio.Infrastructure.Prompting.CharacterToolCatalog(
                [new WhipRadio.Infrastructure.Prompting.InviteTool(), new WhipRadio.Infrastructure.Prompting.RemoveFromChannelTool()]),
            chat,
            new ChatParticipantResolver(fixture),
            new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance),
            new TrackQueryService(fixture),
            new MusicProductionControl(),
            priorityDispatcher: null!,
            schedule: null!,
            director: null!,
            new NoOpNotificationBus(),
            scopeFactory: null!,
            TimeProvider.System,
            NullLogger<ChatActionExecutor>.Instance);

    private static async Task<ChatActionContext> BuildDirectorContextAsync(DbFixture fixture, Guid channelId)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        ChatChannel channel = await db.ChatChannels.AsNoTracking().FirstAsync(item => item.Id == channelId);
        return new ChatActionContext(
            channel,
            AgentMessage: null,
            ChatParticipantResolver.Director,
            Guid.NewGuid(),
            HopCount: 0);
    }

    private sealed class NoOpNotificationBus : WhipRadio.Core.Abstractions.INotificationBus
    {
        public Task PublishAsync(WhipRadio.Core.Abstractions.StationNotification notification, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static async Task<ChatTurnRequest> ReadOneAsync(ChatTurnQueue queue)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await foreach (ChatTurnRequest request in queue.ReadAllAsync(cts.Token))
        {
            return request;
        }

        throw new InvalidOperationException("Queue was empty.");
    }

    private static ChatService CreateChatService(DbFixture fixture)
        => new(fixture, new NullHubContext(), TimeProvider.System, NullLogger<ChatService>.Instance);

    private static async Task<(int HostId, Guid MemberId, Guid GuestId)> SeedPeopleAsync(DbFixture fixture)
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
            DeepBackgroundBiography = "Formed after night shifts near Hilo Bay.",
        };
        db.Artists.Add(artist);
        ArtistMember member = new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artist.Id,
            Name = "Makoa Hale",
            Role = "lead vocals",
            Biography = "Writes most lyrics.",
            Interests = "lava field hikes, freight logistics",
            Personality = "Intense and protective.",
            VoiceCreationPrompt = "Baritone.",
        };
        db.ArtistMembers.Add(member);

        Guest guest = new()
        {
            Id = Guid.NewGuid(),
            Name = "Ivy Sparks",
            Slug = $"ivy-sparks-{Guid.NewGuid():N}",
            Expertise = "urban beekeeper",
            Biography = "Keeps hives on rooftops.",
            DeepBackground = "Left a lab job for bees.",
            VoiceCreationPrompt = "Bright and quick.",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Guests.Add(guest);

        await db.SaveChangesAsync();
        return (host.Id, member.Id, guest.Id);
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
