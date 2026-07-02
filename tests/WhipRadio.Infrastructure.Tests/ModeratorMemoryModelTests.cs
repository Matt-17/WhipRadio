using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.TestSupport;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class ModeratorMemoryModelTests
{
    [TestMethod]
    public void Migrations_ExposeConsolidatedPostgresBaseline()
    {
        // The phase-tagged SQLite migrations were squashed into a single Postgres
        // baseline at the cutover; this verifies the consolidated migration is wired.
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseNpgsql("Host=localhost;Database=whipradio;Username=postgres")
            .Options;

        using var db = new RadioDbContext(options);
        var migrations = db.Database.GetMigrations().ToList();

        Assert.True(migrations.Count >= 1);
        Assert.True(migrations[0].EndsWith("InitialPostgres", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("Phase4Chat", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ModeratorMemory_PersistsLayer()
    {
        await using var fixture = await DbFixture.CreateAsync();

        await using (var db = fixture.CreateDbContext())
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

        await using (var db = fixture.CreateDbContext())
        {
            var memory = await db.ModeratorMemories.AsNoTracking().SingleAsync();

            Assert.Equal(ModeratorMemoryLayer.LongTermMemory, memory.Layer);
        }
    }
}
