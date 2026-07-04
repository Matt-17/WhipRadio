using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OllamaEmbeddingServiceTests
{
    [TestMethod]
    public async Task EmbedAsync_PostsModelAndInputAndReturnsFirstVector()
    {
        var handler = new StubHandler("""{"embeddings":[[0.1,0.2,0.3]]}""");
        var service = new OllamaEmbeddingService(
            new StubFactory(handler),
            Options.Create(new LlmOptions { EmbeddingModel = "nomic-embed-text" }));

        float[] vector = await service.EmbedAsync("rooftop bees", CancellationToken.None);

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, vector);
        Assert.Equal("/api/embed", handler.LastPath);
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("nomic-embed-text", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("rooftop bees", body.RootElement.GetProperty("input").GetString());
    }

    [TestMethod]
    public async Task EmbedAsync_ThrowsOnEmptyEmbedding()
    {
        var handler = new StubHandler("""{"embeddings":[]}""");
        var service = new OllamaEmbeddingService(
            new StubFactory(handler),
            Options.Create(new LlmOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EmbedAsync("anything", CancellationToken.None));
    }

    private sealed class StubHandler(string reply) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:8001") };
    }
}
