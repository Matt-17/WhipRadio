using Microsoft.EntityFrameworkCore;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.DbMigrator;

/// <summary>
/// Postgres target context that forces EF to send explicit primary-key values on insert
/// instead of letting the identity columns generate new ones — so the original keys from
/// the SQLite database are preserved verbatim. Sequences are realigned afterwards.
/// </summary>
internal sealed class MigrationTargetDbContext(DbContextOptions<RadioDbContext> options)
    : RadioDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Owned types are configured through their owner; skip them.
            if (entityType.IsOwned())
            {
                continue;
            }

            var key = entityType.FindPrimaryKey();
            if (key is null)
            {
                continue;
            }

            foreach (var property in key.Properties)
            {
                modelBuilder.Entity(entityType.ClrType).Property(property.Name).ValueGeneratedNever();
            }
        }
    }
}
