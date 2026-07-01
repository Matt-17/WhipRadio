using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.TestSupport;

/// <summary>
/// Per-test database handle backed by an isolated Postgres database (see
/// <see cref="PostgresTestDatabase"/>). Drop-in replacement for the old in-memory SQLite
/// fixtures: same <see cref="IDbContextFactory{RadioDbContext}"/> surface, created fresh
/// per test and disposed at the end of the test.
/// </summary>
internal sealed class DbFixture : IDbContextFactory<RadioDbContext>, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly DbContextOptions<RadioDbContext> _options;

    private DbFixture(string connectionString)
    {
        _connectionString = connectionString;
        _options = new DbContextOptionsBuilder<RadioDbContext>().UseNpgsql(connectionString).Options;
    }

    public static async Task<DbFixture> CreateAsync()
    {
        var connectionString = await PostgresTestDatabase.CreateDatabaseAsync();
        return new DbFixture(connectionString);
    }

    /// <summary>Creates the database and seeds the singleton station settings with the given
    /// playout state — mirrors the old SQLite fixture overload used by PlayoutService tests.</summary>
    public static async Task<DbFixture> CreateAsync(bool playoutEnabled)
    {
        var fixture = await CreateAsync();
        await using var db = fixture.CreateDbContext();
        db.StationSettings.Add(new StationSettings
        {
            Id = StationSettings.SingletonId,
            PlayoutEnabled = playoutEnabled,
            MixerEnabled = false,
        });
        await db.SaveChangesAsync();
        return fixture;
    }

    public RadioDbContext CreateDbContext() => new(_options);

    public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());

    public async ValueTask DisposeAsync() => await PostgresTestDatabase.DropDatabaseAsync(_connectionString);
}
