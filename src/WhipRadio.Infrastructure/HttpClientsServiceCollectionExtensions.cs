using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Music;
using WhipRadio.Infrastructure.Tts;
using WhipRadio.Infrastructure.Weather;

namespace WhipRadio.Infrastructure;

public static class HttpClientsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ollama, TTS, music and Open-Meteo typed clients. Base addresses
    /// use Aspire service discovery names (http://ollama etc.); explicit endpoints and
    /// Aspire connection strings take precedence.
    /// </summary>
    public static IServiceCollection AddRadioHttpClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.Configure<WeatherOptions>(configuration.GetSection(WeatherOptions.SectionName));

        services.AddHttpClient<ITextGenerationService, OllamaTextGenerationService>(client =>
        {
            client.BaseAddress = ResolveEndpoint(configuration, "Llm:Endpoint", "ollama", "http://ollama");
            client.Timeout = TimeSpan.FromMinutes(10); // small models on CPU can be slow
        });

        services.AddHttpClient<ITtsEngine, HttpTtsEngine>(client =>
        {
            client.BaseAddress = ResolveEndpoint(configuration, "Tts:Endpoint", "tts", "http://tts");
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddHttpClient<IMusicGenerator, HttpMusicGenerator>(client =>
        {
            client.BaseAddress = ResolveEndpoint(configuration, "Music:Endpoint", "music", "http://music");
            client.Timeout = TimeSpan.FromMinutes(30); // music generation is long-running by design
        });

        services.AddHttpClient<IAnnouncementDataSource, OpenMeteoWeatherSource>(client =>
        {
            client.BaseAddress = new Uri(configuration["Weather:Endpoint"] ?? "https://api.open-meteo.com");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IScriptWriter, ScriptWriter>();
        services.AddScoped<IVoiceDirector, VoiceDirector>();

        return services;
    }

    private static Uri ResolveEndpoint(IConfiguration configuration, string configKey, string connectionName, string fallback)
    {
        var explicitEndpoint = configuration[configKey];
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return new Uri(explicitEndpoint);
        }

        // Aspire integrations inject "Endpoint=http://..." style connection strings.
        var connectionString = configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var endpoint = connectionString.Split(';')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                ?["Endpoint=".Length..] ?? connectionString;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return new Uri(fallback);
    }
}
