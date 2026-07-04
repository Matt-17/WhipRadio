using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WhipRadio.Web.Services;

namespace WhipRadio.Web.Tests;

[TestClass]
public class OrchestratorEndpointTests
{
    [TestMethod]
    public void Resolve_PrefersAspireServiceDiscovery_OverExplicitSetting()
    {
        var configuration = Config(
            ("services:orchestrator:http:0", "http://aspire-endpoint:5151/"),
            ("Orchestrator:Endpoint", "http://explicit:5151"));

        Assert.Equal(
            "http://aspire-endpoint:5151",
            OrchestratorEndpoint.Resolve(configuration, Env(Environments.Production)));
    }

    [TestMethod]
    public void Resolve_FallsBackToExplicitSetting_AndTrimsTrailingSlash()
    {
        var configuration = Config(("Orchestrator:Endpoint", "http://explicit:5151/"));

        Assert.Equal(
            "http://explicit:5151",
            OrchestratorEndpoint.Resolve(configuration, Env(Environments.Production)));
    }

    [TestMethod]
    public void Resolve_UsesEnvironmentDefault_WhenNothingConfigured()
    {
        Assert.Equal(
            "http://localhost:5151",
            OrchestratorEndpoint.Resolve(Config(), Env(Environments.Development)));
        Assert.Equal(
            "http://orchestrator",
            OrchestratorEndpoint.Resolve(Config(), Env(Environments.Production)));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IHostEnvironment Env(string name) => new FakeHostEnvironment(name);

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "WhipRadio.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
