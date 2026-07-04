using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Web.Services;

namespace WhipRadio.Web.Tests;

[TestClass]
public class LiveClientBaseTests
{
    [TestMethod]
    public async Task EnsureStarted_RunsInitialSnapshotOnce_AndSurvivesUnreachableHub()
    {
        // Point the hub at a port nothing listens on: the connect attempt must be
        // swallowed (snapshot-only fallback) and the client still count as started.
        var client = new CountingLiveClient(
            Config(("Orchestrator:Endpoint", "http://localhost:59999")),
            Env(Environments.Production));
        await using (client)
        {
            await client.EnsureStartedAsync();
            await client.EnsureStartedAsync();
            await client.EnsureStartedAsync();

            Assert.Equal(1, client.RefreshCount);
            Assert.Equal(1, client.RegisterCount);
        }
    }

    [TestMethod]
    public async Task EnsureStarted_ConcurrentCallers_StartOnlyOnce()
    {
        var client = new CountingLiveClient(
            Config(("Orchestrator:Endpoint", "http://localhost:59999")),
            Env(Environments.Production));
        await using (client)
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(client.EnsureStartedAsync)));

            Assert.Equal(1, client.RefreshCount);
            Assert.Equal(1, client.RegisterCount);
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IHostEnvironment Env(string name) => new FakeHostEnvironment(name);

    private sealed class CountingLiveClient(IConfiguration configuration, IHostEnvironment environment)
        : LiveClientBase(configuration, environment, NullLogger<CountingLiveClient>.Instance)
    {
        private int _refreshCount;
        private int _registerCount;

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public int RegisterCount => Volatile.Read(ref _registerCount);

        protected override void RegisterHandlers(HubConnection connection)
            => Interlocked.Increment(ref _registerCount);

        protected override Task RefreshCoreAsync()
        {
            Interlocked.Increment(ref _refreshCount);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "WhipRadio.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
