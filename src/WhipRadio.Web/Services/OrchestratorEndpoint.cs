namespace WhipRadio.Web.Services;

/// <summary>Single source of truth for the orchestrator base URL: Aspire service
/// discovery first, explicit setting second, environment default last.</summary>
public static class OrchestratorEndpoint
{
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
        => (configuration["services:orchestrator:http:0"]
            ?? configuration["Orchestrator:Endpoint"]
            ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator"))
            .TrimEnd('/');
}
