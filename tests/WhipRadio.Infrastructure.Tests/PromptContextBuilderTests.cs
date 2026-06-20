using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class PromptContextBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_IncludesStationBrandingInRenderedSituation()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                StationName = "Night Lab FM",
                StationSlogan = "Made after dark.",
                StationVision = "A station that knows why it exists.",
                StationMission = "Make original radio feel intentional.",
                DefaultLanguage = "en",
            });
            db.Moderators.Add(new Moderator
            {
                Name = "Ava",
                Language = "en",
                Gender = ModeratorGenders.Female,
                PersonaPrompt = "warm",
                Style = "calm",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var time = new FixedTimeProvider(new DateTime(2026, 6, 17, 18, 0, 0, DateTimeKind.Utc));
        var builder = new PromptContextBuilder(
            fixture,
            new ScheduleService(fixture, time),
            time,
            new EmptyToolCatalog(),
            NullLogger<PromptContextBuilder>.Instance);

        var context = await builder.BuildAsync(
            new PromptContextInput(PromptScope.AnnouncementScript),
            CancellationToken.None);

        var rendered = context.RenderSituation();
        Assert.Contains("Night Lab FM", rendered);
        Assert.Contains("Made after dark.", rendered);
        Assert.Contains("A station that knows why it exists.", rendered);
        Assert.Contains("Make original radio feel intentional.", rendered);
    }

    [TestMethod]
    public async Task BuildAsync_UsesLocalNowOverrideForScheduledAirtime()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                StationName = "Night Lab FM",
                DefaultLanguage = "en",
            });
            await db.SaveChangesAsync();
        }

        var time = new FixedTimeProvider(new DateTime(2026, 6, 20, 21, 50, 0, DateTimeKind.Utc));
        var builder = new PromptContextBuilder(
            fixture,
            new ScheduleService(fixture, time),
            time,
            new EmptyToolCatalog(),
            NullLogger<PromptContextBuilder>.Instance);

        var context = await builder.BuildAsync(
            new PromptContextInput(
                PromptScope.AnnouncementScript,
                AnnouncementKind: AnnouncementKind.Weather,
                LocalNowOverride: new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.FromHours(2))),
            CancellationToken.None);

        var rendered = context.RenderSituation();
        Assert.Contains("2026-06-21 00:00", rendered);
        Assert.DoesNotContain("21:50", rendered);
    }

    private sealed class EmptyToolCatalog : ICharacterToolCatalog
    {
        public IReadOnlyList<CharacterToolDefinition> GetTools(PromptScope scope, CharacterRole role) => [];

        public ICharacterTool? GetTool(string name, PromptScope scope, CharacterRole role) => null;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (var db = new RadioDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new DbFixture(connection, options);
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
