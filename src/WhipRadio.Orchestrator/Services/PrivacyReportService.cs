using WhipRadio.Core.Api;
using WhipRadio.Core.Configuration;
using WhipRadio.Infrastructure.Privacy;

namespace WhipRadio.Orchestrator.Services;

public class PrivacyReportService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    OutgoingHttpRequestAudit requestAudit)
{
    private static readonly string[] Notes =
    [
        "Request history is memory-only and resets when the Orchestrator restarts.",
        "Request bodies, headers, API keys, tokens, credentials, query strings, and URL fragments are not shown.",
        "Private, loopback, sidecar, and internal service traffic is excluded from the external request log.",
    ];

    public PrivacyReportDto BuildReport()
        => new(
            DateTime.UtcNow,
            requestAudit.Capacity,
            BuildServices(),
            requestAudit.Snapshot(),
            Notes);

    private List<PrivacyServiceDto> BuildServices()
    {
        var services = new List<PrivacyServiceDto>
        {
            EndpointService(
                "Open-Meteo",
                configuration["Weather:Endpoint"] ?? "https://api.open-meteo.com",
                "external endpoint",
                "Weather forecast data source."),
            Service(
                "RSS and article feeds",
                "operator-configured feed URLs",
                "external",
                "when feeds are enabled",
                "News polling and article extraction use the feed URLs configured on the News page."),
            Service(
                "Local data root",
                configuration["Radio:DataRoot"]
                    ?? (Directory.Exists("/data") ? "/data" : Path.Combine(environment.ContentRootPath, "data")),
                "local",
                "local",
                "Tracks, generated announcements, and images stay under the configured data root; "
                + "station metadata is stored in a local PostgreSQL database."),
            EndpointService(
                "Ollama Writer Room",
                configuration["Llm:Endpoint"] ?? ServiceEndpointDefaults.WriterRoom,
                "local",
                "Local text generation endpoint unless configured otherwise."),
            EndpointService(
                "Icecast stream",
                configuration["Stream:PublicUrl"] ?? ServiceEndpointDefaults.PublicStream,
                "stream endpoint",
                "Listener stream URL. Source credentials are never shown here."),
        };

        var otelEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services.Add(string.IsNullOrWhiteSpace(otelEndpoint)
            ? Service(
                "OpenTelemetry exporter",
                "not configured",
                "external",
                "not configured",
                "Only active when OTEL_EXPORTER_OTLP_ENDPOINT is set.")
            : EndpointService(
                "OpenTelemetry exporter",
                otelEndpoint,
                "export endpoint",
                "Only active when OTEL_EXPORTER_OTLP_ENDPOINT is set."));

        AddOptionalService(
            services,
            "OpenAI",
            configuration["OpenAi:Endpoint"] ?? "https://api.openai.com",
            "OpenAi:ApiKey",
            "OpenAi:Endpoint",
            "External text generation provider when selected/configured.");

        AddOptionalService(
            services,
            "ElevenLabs",
            configuration["ElevenLabs:Endpoint"] ?? "https://api.elevenlabs.io",
            "ElevenLabs:ApiKey",
            "ElevenLabs:Endpoint",
            "Optional external voice/music provider when enabled/configured.");

        return services
            .OrderByDescending(service => service.Classification == "external")
            .ThenBy(service => service.Name)
            .ToList();
    }

    private static PrivacyServiceDto Service(
        string name,
        string target,
        string classification,
        string status,
        string detail)
        => new(name, SanitizeTarget(target), classification, status, detail);

    private static PrivacyServiceDto EndpointService(
        string name,
        string target,
        string status,
        string detail)
    {
        var classification = IsExternalTarget(target) ? "external" : "local";
        var endpointStatus = classification == "external" ? status : "local";
        return Service(name, target, classification, endpointStatus, detail);
    }

    private void AddOptionalService(
        List<PrivacyServiceDto> services,
        string name,
        string target,
        string apiKeyConfigName,
        string endpointConfigName,
        string detail)
    {
        var configured = !string.IsNullOrWhiteSpace(configuration[apiKeyConfigName])
            || !string.IsNullOrWhiteSpace(configuration[endpointConfigName]);
        services.Add(Service(
            name,
            target,
            IsExternalTarget(target) ? "external" : "local",
            configured ? "configured" : "not configured",
            detail));
    }

    private static bool IsExternalTarget(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && PrivacyRequestClassifier.IsExternal(uri);

    private static string SanitizeTarget(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return target;
        }

        return PrivacyRequestClassifier.SanitizeTarget(uri);
    }
}
