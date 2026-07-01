using System.Runtime.CompilerServices;

namespace WhipRadio.Infrastructure.Persistence;

internal static class NpgsqlConfiguration
{
    /// <summary>
    /// Maps <see cref="System.DateTime"/> to `timestamp without time zone` so values
    /// round-trip the way they did under SQLite (Kind=Unspecified), instead of Npgsql's
    /// default UTC-only `timestamptz` (which throws on non-UTC writes). Deliberate,
    /// reversible choice; revisit to normalize all persisted timestamps to UTC and move
    /// to `timestamptz`.
    ///
    /// Runs at assembly load — before any DbContext model or Npgsql data source is built
    /// — for both the running app and `dotnet ef` design-time tooling, so the generated
    /// migration schema and the runtime mapping stay consistent.
    /// </summary>
    // CA2255: a module initializer is exactly right here — the switch must be set once,
    // as early as the Infrastructure assembly loads, before this assembly builds any
    // Npgsql data source or DbContext model (app and `dotnet ef` alike).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }
}
