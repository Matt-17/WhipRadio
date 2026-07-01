using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Design-time factory so `dotnet ef migrations` works without a running host.
/// Uses a local Postgres connection; override with RADIO_DESIGN_CONNECTION.
/// Migrations are generated from the model, so the server need not be reachable.</summary>
public class RadioDbContextFactory : IDesignTimeDbContextFactory<RadioDbContext>
{
    private const string DefaultDesignConnection =
        "Host=localhost;Port=5432;Database=radio;Username=postgres;Password=postgres";

    public RadioDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("RADIO_DESIGN_CONNECTION") ?? DefaultDesignConnection;
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new RadioDbContext(options);
    }
}
