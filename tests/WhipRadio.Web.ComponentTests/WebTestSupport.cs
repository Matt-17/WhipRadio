using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.TestSupport;
using WhipRadio.Web.Services;
using WhipRadio.Web.Services.Api;

namespace WhipRadio.Web.ComponentTests;

/// <summary>
/// Shared bUnit wiring: registers every feature API client backed by one
/// <see cref="FakeHttpMessageHandler"/>, plus the page-level services the
/// console components inject. JS interop runs in loose mode.
/// </summary>
internal static class WebTestSupport
{
    /// <summary>A handler where every request fails — pages must render their
    /// empty/fallback states, never crash.</summary>
    public static FakeHttpMessageHandler UnreachableOrchestrator()
        => new(_ => throw new HttpRequestException("orchestrator down"));

    public static void RegisterConsoleServices(this BunitContext ctx, FakeHttpMessageHandler handler)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(CreateClient<StationApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<LibraryApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<ChatApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<ModeratorsApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<StudiosApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<ProductionApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<ArchiveApiClient>(handler));
        ctx.Services.AddSingleton(CreateClient<SettingsApiClient>(handler));
        ctx.Services.AddSingleton(new PlayerState());
    }

    public static T CreateClient<T>(FakeHttpMessageHandler handler) where T : ApiClientBase
        => (T)Activator.CreateInstance(
            typeof(T),
            handler.CreateClient(),
            new SingleClientFactory(() => handler.CreateClient()),
            Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(typeof(T)))!)!;

    private sealed class SingleClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }
}
