using WhipRadio.Web.Components;
using WhipRadio.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<RadioApiClient>(client =>
{
    client.BaseAddress = new Uri(GetOrchestratorEndpoint(builder.Configuration, builder.Environment));
    client.Timeout = TimeSpan.FromSeconds(10);
})
    .RemoveAllResilienceHandlers();

// Voice design runs for minutes (transient 1.7B model; first call downloads
// weights). The default 10 s timeout + Polly retries would cancel and then
// QUEUE THREE designs — this client has neither.
builder.Services.AddHttpClient("orchestrator-long", client =>
    {
        client.BaseAddress = new Uri(GetOrchestratorEndpoint(builder.Configuration, builder.Environment));
        client.Timeout = TimeSpan.FromMinutes(12);
    })
    .RemoveAllResilienceHandlers();

// Media proxy clients: audio must be served same-origin (the page is https;
// browsers block plain-http media as mixed content). Infinite timeout — the
// live stream is endless by design.
builder.Services.AddHttpClient("orchestrator-media", client =>
{
    client.BaseAddress = new Uri(GetOrchestratorEndpoint(builder.Configuration, builder.Environment));
    client.Timeout = Timeout.InfiniteTimeSpan;
})
    .RemoveAllResilienceHandlers();
builder.Services.AddHttpClient("live-stream", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .RemoveAllResilienceHandlers();

builder.Services.AddScoped<RadioLiveClient>();
builder.Services.AddScoped<ConsoleLiveClient>();
builder.Services.AddScoped<PlayerState>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// --- same-origin media proxy --------------------------------------------------
// All audio the browser touches is served from THIS origin: the page may be
// https while Icecast/orchestrator speak plain http (mixed content would be
// blocked), and internal service names aren't browser-resolvable anyway.

app.MapGet("/media/track/{id:guid}", (Guid id, IHttpClientFactory factory, HttpContext context) =>
    ProxyMediaAsync(context, factory.CreateClient("orchestrator-media"), $"/api/library/{id}/audio"));

app.MapGet("/media/announcement/{id:guid}", (Guid id, IHttpClientFactory factory, HttpContext context) =>
    ProxyMediaAsync(context, factory.CreateClient("orchestrator-media"), $"/api/announcements/{id}/audio"));

app.MapGet("/media/jingle/{id:guid}", (Guid id, IHttpClientFactory factory, HttpContext context) =>
    ProxyMediaAsync(context, factory.CreateClient("orchestrator-media"), $"/api/jingles/{id}/audio"));

app.MapGet("/media/live", (IHttpClientFactory factory, IConfiguration config, HttpContext context) =>
    ProxyMediaAsync(
        context,
        factory.CreateClient("live-stream"),
        config["Stream:PublicUrl"] ?? "http://localhost:8000/radio.mp3"));

app.MapGet("/media/voice-preview/{handle}", (string handle, IHttpClientFactory factory, HttpContext context) =>
    ProxyMediaAsync(context, factory.CreateClient("orchestrator-media"),
        $"/api/voices/{Uri.EscapeDataString(handle)}/preview"));

app.Run();

static string GetOrchestratorEndpoint(IConfiguration configuration, IHostEnvironment environment)
    => configuration["services:orchestrator:http:0"]
        ?? configuration["Orchestrator:Endpoint"]
        ?? (environment.IsDevelopment() ? "http://localhost:5151" : "http://orchestrator");

static async Task ProxyMediaAsync(HttpContext context, HttpClient client, string upstreamUrl)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
    if (context.Request.Headers.TryGetValue("Range", out var range))
    {
        request.Headers.TryAddWithoutValidation("Range", (string)range!);
    }

    using var response = await client.SendAsync(
        request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

    context.Response.StatusCode = (int)response.StatusCode;
    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
    // Live audio must NEVER be cached: a replayed stale chunk after a server
    // restart is exactly the "weird sounds" failure mode.
    context.Response.Headers.CacheControl = "no-store, no-cache";
    context.Response.Headers.Pragma = "no-cache";
    if (response.Content.Headers.ContentLength is { } length)
    {
        context.Response.ContentLength = length;
    }

    if (response.Content.Headers.ContentRange is { } contentRange)
    {
        context.Response.Headers.ContentRange = contentRange.ToString();
    }

    if (response.Headers.AcceptRanges.Count > 0)
    {
        context.Response.Headers.AcceptRanges = string.Join(",", response.Headers.AcceptRanges);
    }

    // Live streams are endless — no buffering between Icecast and the listener.
    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

    try
    {
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // listener tuned out — normal for streams
    }
}
