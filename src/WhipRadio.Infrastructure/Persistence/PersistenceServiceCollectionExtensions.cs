using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL DbContext. Connection string comes from
    /// ConnectionStrings:radio, which the Aspire AppHost injects for the
    /// "radio" database resource. Fails fast if it is missing.
    /// </summary>
    public static IServiceCollection AddRadioPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("radio")
            ?? throw new InvalidOperationException(
                "Connection string 'radio' is not configured. The Aspire AppHost supplies it for the "
                + "'radio' Postgres database; for a standalone run set ConnectionStrings__radio "
                + "(e.g. Host=localhost;Port=5432;Database=radio;Username=postgres;Password=...).");

        services.AddDbContextFactory<RadioDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(new StationSettingsCacheInvalidationInterceptor(sp)));
        services.AddScoped<ITrackRepository, EfTrackRepository>();
        services.AddScoped<ITrackSelector>(sp => new WeightedTrackSelector(sp.GetRequiredService<ITrackRepository>()));
        return services;
    }
}
