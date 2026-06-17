using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ModeratorMemoryModelTests
{
    [TestMethod]
    public void Migrations_ExposePhase3bSchemaUpdates()
    {
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var db = new RadioDbContext(options);
        var migrations = db.Database.GetMigrations().ToList();

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "20260617140000_Phase3bMemoryLayers",
                "20260617150000_Phase3bPersonalityTraits",
                "20260617160000_Phase3bTalkProfiles",
                "20260617170000_Phase3bTalkBreaksAndBits",
                "20260617180000_Phase3bWeather",
                "20260617190000_Phase3bBrandingAndJingles",
            },
            migrations);
    }

    [TestMethod]
    public async Task ModeratorMemory_PersistsLayer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new RadioDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Moderators.Add(new Moderator
            {
                Name = "Ava",
                Language = "en",
                Gender = ModeratorGenders.Female,
                PersonaPrompt = "warm",
                Style = "calm",
            });
            db.ModeratorMemories.Add(new ModeratorMemory
            {
                ModeratorId = 1,
                Layer = ModeratorMemoryLayer.LongTermMemory,
                Date = new DateOnly(2026, 6, 17),
                Content = "A running joke about the night shift.",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new RadioDbContext(options))
        {
            var memory = await db.ModeratorMemories.AsNoTracking().SingleAsync();

            Assert.Equal(ModeratorMemoryLayer.LongTermMemory, memory.Layer);
        }
    }
}
