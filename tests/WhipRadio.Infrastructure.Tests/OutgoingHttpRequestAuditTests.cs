using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using WhipRadio.Infrastructure.Privacy;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OutgoingHttpRequestAuditTests
{
    [TestMethod]
    public async Task HttpClientFilter_RecordsExternalRequestWithSanitizedTarget()
    {
        var audit = new OutgoingHttpRequestAudit();
        using var provider = BuildProvider(
            audit,
            "external",
            "https://api.example.com",
            new StaticResponseHandler(HttpStatusCode.Accepted));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("external");

        using var response = await client.GetAsync("/v1/generate?api_key=secret&prompt=hello#fragment");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var snapshot = audit.Snapshot();
        Assert.Equal(1, snapshot.Count);
        var entry = snapshot[0];
        Assert.Equal("GET", entry.Method);
        Assert.Equal("https://api.example.com/v1/generate", entry.Target);
        Assert.Equal("api.example.com", entry.Host);
        Assert.Equal("external", entry.Source);
        Assert.Equal(202, entry.StatusCode);
        Assert.True(entry.Succeeded);
        Assert.Equal("external", entry.Classification);
    }

    [TestMethod]
    public async Task HttpClientFilter_DoesNotRecordLocalRequest()
    {
        var audit = new OutgoingHttpRequestAudit();
        using var provider = BuildProvider(
            audit,
            "ollama",
            "http://localhost:11434",
            new StaticResponseHandler(HttpStatusCode.OK));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ollama");

        using var response = await client.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, audit.Snapshot().Count);
    }

    [TestMethod]
    public void Classifier_TreatsPrivateAndServiceHostsAsInternal()
    {
        Assert.False(PrivacyRequestClassifier.IsExternal(new Uri("http://127.0.0.1:8000/radio.mp3")));
        Assert.False(PrivacyRequestClassifier.IsExternal(new Uri("http://192.168.1.22/api")));
        Assert.False(PrivacyRequestClassifier.IsExternal(new Uri("http://orchestrator/api/nowplaying")));
        Assert.False(PrivacyRequestClassifier.IsExternal(new Uri("http://musicgen:8000/generate")));
        Assert.True(PrivacyRequestClassifier.IsExternal(new Uri("https://api.open-meteo.com/v1/forecast")));
    }

    [TestMethod]
    public void Audit_EnforcesCapacity()
    {
        var audit = new OutgoingHttpRequestAudit(capacity: 2);

        audit.Record(Entry("https://one.example.com"));
        audit.Record(Entry("https://two.example.com"));
        audit.Record(Entry("https://three.example.com"));

        var snapshot = audit.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("three.example.com", snapshot[0].Host);
        Assert.Equal("two.example.com", snapshot[1].Host);
    }

    private static ServiceProvider BuildProvider(
        OutgoingHttpRequestAudit audit,
        string clientName,
        string baseAddress,
        HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(audit);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, PrivacyAuditHttpMessageHandlerFilter>());
        services.AddHttpClient(clientName, client => client.BaseAddress = new Uri(baseAddress))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static OutgoingHttpRequestEntry Entry(string target)
    {
        var uri = new Uri(target);
        return new OutgoingHttpRequestEntry(
            DateTime.UtcNow,
            "GET",
            PrivacyRequestClassifier.SanitizeTarget(uri),
            uri.Host,
            "test",
            200,
            true,
            1,
            "external",
            null);
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
