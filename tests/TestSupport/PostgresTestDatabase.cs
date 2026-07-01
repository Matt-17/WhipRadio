using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.TestSupport;

/// <summary>
/// Shared PostgreSQL backing store for the test suite. Starts one disposable Postgres
/// container per test assembly (the Testcontainers resource reaper tears it down at the
/// end of the run), builds the schema once on a template database, and hands each fixture
/// an isolated database cloned from that template — so tests stay independent and can run
/// in parallel without an in-memory SQLite file.
/// </summary>
internal static class PostgresTestDatabase
{
    private const string TemplateDb = "whipradio_template";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static string _adminConnectionString = string.Empty;

    /// <summary>Clones the schema template into a fresh, uniquely named database.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        await EnsureInitializedAsync();

        var dbName = "t_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\" TEMPLATE \"{TemplateDb}\";";
        await cmd.ExecuteNonQueryAsync();

        return ConnectionStringFor(dbName);
    }

    /// <summary>Best-effort drop of a per-test database; the container is discarded anyway.</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        try
        {
            var dbName = new NpgsqlConnectionStringBuilder(connectionString).Database;
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Ignored: dropping is an optimization, not required for correctness.
        }
    }

    private static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database }.ConnectionString;

    private static async Task EnsureInitializedAsync()
    {
        if (_container is not null)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_container is not null)
            {
                return;
            }

            var container = new PostgreSqlBuilder("postgres:17-alpine")
                .Build();
            await container.StartAsync();
            _adminConnectionString = container.GetConnectionString();

            await using (var admin = new NpgsqlConnection(_adminConnectionString))
            {
                await admin.OpenAsync();
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{TemplateDb}\";";
                await cmd.ExecuteNonQueryAsync();
            }

            // Apply the real migration to the template, which doubles as a migration smoke test.
            var options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseNpgsql(ConnectionStringFor(TemplateDb))
                .Options;
            await using (var db = new RadioDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            // Drop any pooled connection to the template so CREATE DATABASE ... TEMPLATE sees it idle.
            NpgsqlConnection.ClearAllPools();

            _container = container;
        }
        finally
        {
            Gate.Release();
        }
    }
}
