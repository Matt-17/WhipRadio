using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite DbContext. Connection string from ConnectionStrings:radio;
    /// default /data/db/radio.db (container), falling back to ./data/db/radio.db when
    /// /data is not available (local dev on Windows).
    /// </summary>
    public static IServiceCollection AddRadioPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("radio") ?? BuildDefaultConnectionString();
        EnsureDatabaseDirectory(connectionString);

        services.AddDbContextFactory<RadioDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ITrackRepository, EfTrackRepository>();
        services.AddScoped<ITrackSelector>(sp => new WeightedTrackSelector(sp.GetRequiredService<ITrackRepository>()));
        return services;
    }

    private static string BuildDefaultConnectionString()
    {
        var root = Directory.Exists("/data") ? "/data" : Path.Combine(Directory.GetCurrentDirectory(), "data");
        return $"Data Source={Path.Combine(root, "db", "radio.db")}";
    }

    private static void EnsureDatabaseDirectory(string connectionString)
    {
        const string prefix = "Data Source=";
        var idx = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return;
        }

        var path = connectionString[(idx + prefix.Length)..].Split(';')[0].Trim();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
