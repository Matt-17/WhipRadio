using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class DbInitializerTests
{
    [TestMethod]
    public async Task EnsureSeededAsync_UpdatesLegacyDefaultTrackDurationRange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new RadioDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                MinTrackDurationSeconds = 180,
                MaxTrackDurationSeconds = 300,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new RadioDbContext(options))
        {
            await DbInitializer.EnsureSeededAsync(db);
        }

        await using (var db = new RadioDbContext(options))
        {
            var settings = await db.StationSettings.SingleAsync();
            Assert.Equal(150, settings.MinTrackDurationSeconds);
            Assert.Equal(480, settings.MaxTrackDurationSeconds);
        }
    }

    [TestMethod]
    public async Task EnsureSeededAsync_MarksAbandonedRunningStudioHistoryFailed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite(connection)
            .Options;
        var runningId = Guid.NewGuid();

        await using (var db = new RadioDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.StudioHistory.Add(new StudioHistoryEntry
            {
                Id = runningId,
                StudioName = "Studio #1",
                StudioKind = StudioKind.Recording,
                Provider = "ace-step-1.5",
                Operation = "Recording for test",
                Status = StudioHistoryStatus.Running,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                Prompt = "Prompt",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new RadioDbContext(options))
        {
            await DbInitializer.EnsureSeededAsync(db);
        }

        await using (var db = new RadioDbContext(options))
        {
            var entry = await db.StudioHistory.SingleAsync(h => h.Id == runningId);
            Assert.Equal(StudioHistoryStatus.Failed, entry.Status);
            Assert.NotNull(entry.CompletedAtUtc);
            Assert.Contains("Orchestrator stopped", entry.Error);
        }
    }
}
