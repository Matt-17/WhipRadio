using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Orchestrator.Api;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class PodcastShowScheduleSeederTests
{
    [TestMethod]
    public async Task Sync_EnabledShow_SeedsFormatAndWeeklySlot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var showId = await SeedShowAsync(fixture, dayOfWeek: 2, startMinute: 21 * 60, slotMinutes: 45);

        await CreateSeeder(fixture).SyncAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var show = await db.PodcastShows.SingleAsync(s => s.Id == showId);
        Assert.NotNull(show.FormatId);

        var format = await db.Formats.SingleAsync(f => f.Id == show.FormatId);
        Assert.True(format.IsEnabled);
        Assert.Equal(SelectionMode.PodcastShow, format.SelectionRules.Mode);
        Assert.Equal("Night Static Weekly", format.Name);

        var slots = await db.ProgramSlots.Where(slot => slot.FormatId == format.Id).ToListAsync();
        Assert.Equal(1, slots.Count);
        Assert.Equal(2, slots[0].DayOfWeek);
        Assert.Equal(21 * 60, slots[0].StartMinute);
        Assert.Equal(45, slots[0].DurationMinutes);
    }

    [TestMethod]
    public async Task Sync_SlotChange_MovesTheSeededSlot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var showId = await SeedShowAsync(fixture, dayOfWeek: 2, startMinute: 21 * 60, slotMinutes: 30);
        var seeder = CreateSeeder(fixture);
        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var show = await db.PodcastShows.SingleAsync(s => s.Id == showId);
            show.DayOfWeek = 5;
            show.StartMinute = 19 * 60;
            await db.SaveChangesAsync();
        }

        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var show = await db.PodcastShows.SingleAsync(s => s.Id == showId);
            var slots = await db.ProgramSlots.Where(slot => slot.FormatId == show.FormatId).ToListAsync();
            Assert.Equal(1, slots.Count);
            Assert.Equal(5, slots[0].DayOfWeek);
            Assert.Equal(19 * 60, slots[0].StartMinute);
        }
    }

    [TestMethod]
    public async Task Sync_DisabledShow_RemovesSlotAndDisablesFormat()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var showId = await SeedShowAsync(fixture, dayOfWeek: 2, startMinute: 21 * 60, slotMinutes: 30);
        var seeder = CreateSeeder(fixture);
        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var show = await db.PodcastShows.SingleAsync(s => s.Id == showId);
            show.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var show = await db.PodcastShows.SingleAsync(s => s.Id == showId);
            Assert.Equal(0, await db.ProgramSlots.CountAsync(slot => slot.FormatId == show.FormatId));
            var format = await db.Formats.SingleAsync(f => f.Id == show.FormatId);
            Assert.False(format.IsEnabled);
        }
    }

    [TestMethod]
    public async Task Sync_DisplacesOverlappingForeignSlot()
    {
        await using var fixture = await DbFixture.CreateAsync();
        Guid otherFormatId;
        await using (var db = fixture.CreateDbContext())
        {
            var other = new Format
            {
                Id = Guid.NewGuid(),
                Name = "Evening Drive",
                Description = "test",
                Genre = "electronic",
                Subgenre = "house",
                Reason = "test",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Formats.Add(other);
            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = 2,
                StartMinute = (20 * 60) + 30,
                DurationMinutes = 120, // Tue 20:30–22:30 overlaps the 21:00 podcast slot
                FormatId = other.Id,
            });
            await db.SaveChangesAsync();
            otherFormatId = other.Id;
        }

        await SeedShowAsync(fixture, dayOfWeek: 2, startMinute: 21 * 60, slotMinutes: 30);
        await CreateSeeder(fixture).SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.ProgramSlots.CountAsync(slot => slot.FormatId == otherFormatId));
        }
    }

    private static PodcastShowScheduleSeeder CreateSeeder(DbFixture fixture)
        => new(fixture, new NullHubContext(), NullLogger<PodcastShowScheduleSeeder>.Instance);

    private static async Task<Guid> SeedShowAsync(
        DbFixture fixture, int dayOfWeek, int startMinute, int slotMinutes)
    {
        await using var db = fixture.CreateDbContext();
        var host = new Moderator
        {
            Name = "Nova Quinn",
            Slug = $"nova-{Guid.NewGuid():N}",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "host",
            Style = "calm",
            IsActive = true,
        };
        db.Moderators.Add(host);
        await db.SaveChangesAsync();

        var show = new PodcastShow
        {
            Id = Guid.NewGuid(),
            Name = "Night Static Weekly",
            Brief = "Music industry talk.",
            EpisodeMinutes = 20,
            DayOfWeek = dayOfWeek,
            StartMinute = startMinute,
            SlotDurationMinutes = slotMinutes,
            ParticipantsJson = JsonSerializer.Serialize(new List<ConversationParticipant>
            {
                new()
                {
                    SpeakerKey = ConversationParticipant.HostKey(host.Id),
                    DisplayName = host.Name,
                    ConversationRole = "Host",
                },
            }),
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.PodcastShows.Add(show);
        await db.SaveChangesAsync();
        return show.Id;
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
