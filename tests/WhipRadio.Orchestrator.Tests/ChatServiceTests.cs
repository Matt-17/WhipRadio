using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ChatServiceTests
{
    [TestMethod]
    public async Task EnsureChannels_SeedsStationDirectorAndHostDms_AndArchivesFiredHosts()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int activeHostId;
        int firedHostId;
        await using (var db = fixture.CreateDbContext())
        {
            var active = Host("Beth Nova", isActive: true);
            var fired = Host("Adam Static", isActive: true);
            db.Moderators.AddRange(active, fired);
            await db.SaveChangesAsync();
            activeHostId = active.Id;
            firedHostId = fired.Id;
        }

        var chat = CreateService(fixture);
        var channels = await chat.GetChannelsAsync(CancellationToken.None);

        // Rail order: Station, Director, then hosts A-Z.
        Assert.Equal("Station", channels[0].Kind);
        Assert.Equal("DirectorDm", channels[1].Kind);
        Assert.Equal(new[] { "Adam Static", "Beth Nova" },
            channels.Where(c => c.Kind == "HostDm").Select(c => c.Name).ToArray());

        // Firing a host archives their DM on the next channel sync.
        await using (var db = fixture.CreateDbContext())
        {
            var fired = await db.Moderators.FirstAsync(m => m.Id == firedHostId);
            fired.IsActive = false;
            await db.SaveChangesAsync();
        }

        channels = await chat.GetChannelsAsync(CancellationToken.None);
        var firedChannel = channels.Single(c => c.Kind == "HostDm" && c.Name == "Adam Static");
        Assert.True(firedChannel.IsArchived);
        // Archived channels sort last.
        Assert.Equal(channels[^1].Id, firedChannel.Id);

        var activeChannel = channels.Single(c => c.Kind == "HostDm" && c.Name == "Beth Nova");
        Assert.False(activeChannel.IsArchived);
        Assert.Equal(activeHostId, activeChannel.ModeratorId);
    }

    [TestMethod]
    public async Task GetMessages_PagesNewestFirstWindows_AndHonorsBeforeCursorOfAnyKind()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var chat = CreateService(fixture);
        Guid channelId = await chat.GetStationChannelIdAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await chat.PostAsync(
                channelId, ChatSenderKind.Admin, moderatorId: null, $"message {i}",
                actionsJson: null, correlationId: null, hopCount: 0, CancellationToken.None);
            await Task.Delay(15); // distinct CreatedAtUtc ordering
        }

        var latest = await chat.GetMessagesAsync(channelId, beforeUtc: null, take: 3, CancellationToken.None);
        Assert.True(latest.HasMore);
        Assert.Equal(new[] { "message 2", "message 3", "message 4" },
            latest.Messages.Select(m => m.Text).ToArray());

        // The web client round-trips the cursor through a query string, which
        // binds with Kind=Unspecified/Local — the service must still treat the
        // value as UTC (regression guard for the timestamptz migration).
        var cursor = DateTime.SpecifyKind(latest.Messages[0].CreatedAtUtc, DateTimeKind.Unspecified);
        var older = await chat.GetMessagesAsync(channelId, cursor, take: 10, CancellationToken.None);
        Assert.False(older.HasMore);
        Assert.Equal(new[] { "message 0", "message 1" }, older.Messages.Select(m => m.Text).ToArray());
    }

    [TestMethod]
    public async Task Post_UpdatesChannelPreviewAndUnread_AndMarkReadClearsIt()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int hostId;
        await using (var db = fixture.CreateDbContext())
        {
            var host = Host("Charlie Wave", isActive: true);
            db.Moderators.Add(host);
            await db.SaveChangesAsync();
            hostId = host.Id;
        }

        var chat = CreateService(fixture);
        Guid channelId = await chat.GetHostDmChannelIdAsync(hostId, CancellationToken.None)
            ?? throw new InvalidOperationException("Host DM was not created.");

        await chat.PostAsync(
            channelId, ChatSenderKind.Host, hostId, "hello from the booth",
            actionsJson: null, correlationId: null, hopCount: 0, CancellationToken.None);

        var channel = (await chat.GetChannelsAsync(CancellationToken.None)).Single(c => c.Id == channelId);
        Assert.Equal(1, channel.UnreadCount);
        Assert.Equal("hello from the booth", channel.LastMessagePreview);

        await chat.MarkReadAsync(channelId, CancellationToken.None);
        channel = (await chat.GetChannelsAsync(CancellationToken.None)).Single(c => c.Id == channelId);
        Assert.Equal(0, channel.UnreadCount);
    }

    [TestMethod]
    public async Task Post_RejectsEmptyText_AndAdminPostsToArchivedChannels()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int hostId;
        await using (var db = fixture.CreateDbContext())
        {
            var host = Host("Dora Fade", isActive: true);
            db.Moderators.Add(host);
            await db.SaveChangesAsync();
            hostId = host.Id;
        }

        var chat = CreateService(fixture);
        Guid stationId = await chat.GetStationChannelIdAsync(CancellationToken.None);
        Guid hostDmId = await chat.GetHostDmChannelIdAsync(hostId, CancellationToken.None)
            ?? throw new InvalidOperationException("Host DM was not created.");

        await Assert.ThrowsAsync<ArgumentException>(() => chat.PostAsync(
            stationId, ChatSenderKind.Admin, moderatorId: null, "   ",
            actionsJson: null, correlationId: null, hopCount: 0, CancellationToken.None));

        // Fire the host, sync channels so the DM archives, then admin posts must fail.
        await using (var db = fixture.CreateDbContext())
        {
            var host = await db.Moderators.FirstAsync(m => m.Id == hostId);
            host.IsActive = false;
            await db.SaveChangesAsync();
        }

        await chat.GetChannelsAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.PostAsync(
            hostDmId, ChatSenderKind.Admin, moderatorId: null, "anyone home?",
            actionsJson: null, correlationId: null, hopCount: 0, CancellationToken.None));
    }

    private static ChatService CreateService(DbFixture fixture)
        => new(fixture, new NullHubContext(), TimeProvider.System, NullLogger<ChatService>.Instance);

    private static Moderator Host(string name, bool isActive) => new()
    {
        Name = name,
        Slug = name.ToLowerInvariant().Replace(' ', '-'),
        Language = "en",
        Gender = ModeratorGenders.Female,
        PersonaPrompt = "persona",
        Style = "style",
        IsActive = isActive,
    };

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
