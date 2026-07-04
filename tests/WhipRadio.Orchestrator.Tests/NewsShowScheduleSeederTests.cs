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
public class NewsShowScheduleSeederTests
{
    [TestMethod]
    public async Task Sync_Enabled_SeedsFormatAndDailySlots()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await SeedSettingsAsync(fixture, enabled: true, airTimes: "08:00,20:00", durationMinutes: 30);

        await CreateSeeder(fixture).SyncAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var settings = await db.StationSettings.SingleAsync();
        Assert.NotNull(settings.NewsShowFormatId);

        var format = await db.Formats.SingleAsync(f => f.Id == settings.NewsShowFormatId);
        Assert.True(format.IsEnabled);
        Assert.Equal(SelectionMode.NewsShow, format.SelectionRules.Mode);

        var slots = await db.ProgramSlots.Where(s => s.FormatId == format.Id).ToListAsync();
        Assert.Equal(14, slots.Count); // 2 air times × 7 days
        Assert.True(slots.All(s => s.DurationMinutes == 30));
        Assert.Equal(7, slots.Count(s => s.StartMinute == 8 * 60));
        Assert.Equal(7, slots.Count(s => s.StartMinute == 20 * 60));
    }

    [TestMethod]
    public async Task Sync_AirTimeChange_ReplacesStaleSlots()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await SeedSettingsAsync(fixture, enabled: true, airTimes: "08:00", durationMinutes: 30);
        var seeder = CreateSeeder(fixture);
        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.StationSettings.SingleAsync();
            settings.NewsLongFormatAirTimes = "12:00";
            settings.NewsLongFormatDurationMinutes = 45;
            await db.SaveChangesAsync();
        }

        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.StationSettings.SingleAsync();
            var slots = await db.ProgramSlots.Where(s => s.FormatId == settings.NewsShowFormatId).ToListAsync();
            Assert.Equal(7, slots.Count);
            Assert.True(slots.All(s => s.StartMinute == 12 * 60 && s.DurationMinutes == 45));
        }
    }

    [TestMethod]
    public async Task Sync_Disabled_RemovesOnlySeededSlots()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await SeedSettingsAsync(fixture, enabled: true, airTimes: "08:00", durationMinutes: 30);
        var seeder = CreateSeeder(fixture);
        await seeder.SyncAsync(CancellationToken.None);

        Guid otherFormatId;
        await using (var db = fixture.CreateDbContext())
        {
            var other = NewFormat("Evening Drive");
            db.Formats.Add(other);
            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = 2,
                StartMinute = 18 * 60,
                DurationMinutes = 120,
                FormatId = other.Id,
            });
            var settings = await db.StationSettings.SingleAsync();
            settings.NewsLongFormatEnabled = false;
            await db.SaveChangesAsync();
            otherFormatId = other.Id;
        }

        await seeder.SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.StationSettings.SingleAsync();
            Assert.Equal(0, await db.ProgramSlots.CountAsync(s => s.FormatId == settings.NewsShowFormatId));
            Assert.Equal(1, await db.ProgramSlots.CountAsync(s => s.FormatId == otherFormatId));

            var newsFormat = await db.Formats.SingleAsync(f => f.Id == settings.NewsShowFormatId);
            Assert.False(newsFormat.IsEnabled);
        }
    }

    [TestMethod]
    public async Task Sync_DisplacesOverlappingForeignSlots()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await SeedSettingsAsync(fixture, enabled: true, airTimes: "08:00", durationMinutes: 30);

        Guid otherFormatId;
        await using (var db = fixture.CreateDbContext())
        {
            var other = NewFormat("Morning Mix");
            db.Formats.Add(other);
            // Monday 07:30–09:30 overlaps the seeded Monday 08:00 news block.
            db.ProgramSlots.Add(new ProgramSlot
            {
                DayOfWeek = 1,
                StartMinute = (7 * 60) + 30,
                DurationMinutes = 120,
                FormatId = other.Id,
            });
            await db.SaveChangesAsync();
            otherFormatId = other.Id;
        }

        await CreateSeeder(fixture).SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.ProgramSlots.CountAsync(s => s.FormatId == otherFormatId));
            var settings = await db.StationSettings.SingleAsync();
            Assert.Equal(7, await db.ProgramSlots.CountAsync(s => s.FormatId == settings.NewsShowFormatId));
        }
    }

    [TestMethod]
    public async Task Sync_AssignsResolvableNewsPresenterToTheFormat()
    {
        await using var fixture = await DbFixture.CreateAsync();
        int presenterId;
        await using (var db = fixture.CreateDbContext())
        {
            var presenter = new Moderator
            {
                Name = "Maya Vale",
                Slug = "maya-vale",
                Language = "en",
                Gender = ModeratorGenders.Female,
                PersonaPrompt = "news anchor",
                Style = "crisp",
                IsActive = true,
                IsNewsSpecialist = true,
            };
            db.Moderators.Add(presenter);
            await db.SaveChangesAsync();
            presenterId = presenter.Id;
        }

        await SeedSettingsAsync(
            fixture, enabled: true, airTimes: "08:00", durationMinutes: 30, presenterId: presenterId);

        await CreateSeeder(fixture).SyncAsync(CancellationToken.None);

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.StationSettings.SingleAsync();
            var format = await db.Formats.SingleAsync(f => f.Id == settings.NewsShowFormatId);
            Assert.Equal(presenterId, format.ModeratorId);
        }
    }

    private static NewsShowScheduleSeeder CreateSeeder(DbFixture fixture)
        => new(fixture, new NullHubContext(), NullLogger<NewsShowScheduleSeeder>.Instance);

    private static async Task SeedSettingsAsync(
        DbFixture fixture, bool enabled, string airTimes, int durationMinutes, int? presenterId = null)
    {
        await using var db = fixture.CreateDbContext();
        var settings = await db.StationSettings.SingleOrDefaultAsync(s => s.Id == StationSettings.SingletonId);
        if (settings is null)
        {
            settings = new StationSettings { Id = StationSettings.SingletonId };
            db.StationSettings.Add(settings);
        }

        settings.NewsLongFormatEnabled = enabled;
        settings.NewsLongFormatAirTimes = airTimes;
        settings.NewsLongFormatDurationMinutes = durationMinutes;
        settings.NewsPresenterModeratorId = presenterId;
        await db.SaveChangesAsync();
    }

    private static Format NewFormat(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "test format",
        Genre = "electronic",
        Subgenre = "house",
        Reason = "test",
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
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
