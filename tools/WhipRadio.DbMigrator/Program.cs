using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using WhipRadio.DbMigrator;
using WhipRadio.Infrastructure.Persistence;

// One-shot copy of the legacy SQLite radio.db into the new Postgres "radio" database.
//
//   dotnet run --project tools/WhipRadio.DbMigrator -- \
//       --sqlite data/db/radio.db \
//       --pg "Host=localhost;Port=5432;Database=radio;Username=postgres;Password=..."
//
// The target schema must already exist (run the app once, or `dotnet ef database update`).
// Original primary keys are preserved (MigrationTargetDbContext) and identity sequences are
// realigned afterwards. FK constraints are disabled for the session so copy order is free.

string sqlitePath = GetArg("--sqlite") ?? Path.Combine("data", "db", "radio.db");
string? pg = GetArg("--pg") ?? Environment.GetEnvironmentVariable("ConnectionStrings__radio");

if (pg is null)
{
    Console.Error.WriteLine("Target connection required: pass --pg \"<connstring>\" or set ConnectionStrings__radio.");
    return 1;
}

if (!File.Exists(sqlitePath))
{
    Console.Error.WriteLine($"Source SQLite database not found: {Path.GetFullPath(sqlitePath)}");
    return 1;
}

Console.WriteLine($"Source : {Path.GetFullPath(sqlitePath)}");
Console.WriteLine($"Target : {pg}");
Console.WriteLine();

var sourceOptions = new DbContextOptionsBuilder<RadioDbContext>()
    .UseSqlite($"Data Source={sqlitePath}")
    .Options;
var targetOptions = new DbContextOptionsBuilder<RadioDbContext>()
    .UseNpgsql(pg)
    .Options;

await using var source = new RadioDbContext(sourceOptions);
await using var target = new MigrationTargetDbContext(targetOptions);
target.ChangeTracker.AutoDetectChangesEnabled = false;

// Keep one connection open for the whole session so session_replication_role sticks.
await target.Database.OpenConnectionAsync();
await target.Database.ExecuteSqlRawAsync("SET session_replication_role = 'replica';");

var entityTypes = source.Model.GetEntityTypes()
    .Where(t => !t.IsOwned())
    .DistinctBy(t => t.ClrType)
    .ToList();

var copyMethod = typeof(Program).GetMethod(nameof(CopyEntity), BindingFlags.Static | BindingFlags.NonPublic)!;
var countMethod = typeof(Program).GetMethod(nameof(CountEntity), BindingFlags.Static | BindingFlags.NonPublic)!;

var mismatches = 0;
Console.WriteLine($"{"Entity",-26} {"copied",8} {"target",8}");
Console.WriteLine(new string('-', 46));

foreach (var entityType in entityTypes.OrderBy(t => t.ClrType.Name))
{
    var copied = (int)copyMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [source, target])!;
    var targetCount = (int)countMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [target])!;
    var flag = copied == targetCount ? "" : "  <-- MISMATCH";
    if (copied != targetCount)
    {
        mismatches++;
    }

    Console.WriteLine($"{entityType.ClrType.Name,-26} {copied,8} {targetCount,8}{flag}");
}

// Re-enable FK enforcement and realign identity sequences to MAX(id).
await target.Database.ExecuteSqlRawAsync("SET session_replication_role = 'origin';");
Console.WriteLine();
Console.WriteLine("Realigning identity sequences...");
foreach (var entityType in entityTypes)
{
    var key = entityType.FindPrimaryKey();
    if (key is null || key.Properties.Count != 1)
    {
        continue;
    }

    var prop = key.Properties[0];
    if (prop.ClrType != typeof(int) && prop.ClrType != typeof(long) && prop.ClrType != typeof(short))
    {
        continue;
    }

    var table = entityType.GetTableName();
    var column = prop.GetColumnName();
    if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column))
    {
        continue;
    }

    // DO block: only acts if the column actually has a backing sequence (identity/serial).
    var sql = $"""
        DO $$
        DECLARE seq text;
        BEGIN
          seq := pg_get_serial_sequence('"{table}"', '{column}');
          IF seq IS NOT NULL THEN
            PERFORM setval(seq, GREATEST((SELECT COALESCE(MAX("{column}"), 0) FROM "{table}"), 1));
          END IF;
        END $$;
        """;
    await target.Database.ExecuteSqlRawAsync(sql);
}

await target.Database.CloseConnectionAsync();

Console.WriteLine();
Console.WriteLine(mismatches == 0
    ? "Done. All row counts match."
    : $"Done with {mismatches} count mismatch(es) — review above.");
return mismatches == 0 ? 0 : 2;

static int CopyEntity<T>(RadioDbContext source, RadioDbContext target) where T : class
{
    var rows = source.Set<T>().AsNoTracking().ToList();
    if (rows.Count > 0)
    {
        target.Set<T>().AddRange(rows);
        target.SaveChanges();
        target.ChangeTracker.Clear();
    }

    return rows.Count;
}

static int CountEntity<T>(RadioDbContext target) where T : class
    => target.Set<T>().AsNoTracking().Count();

static string? GetArg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}
