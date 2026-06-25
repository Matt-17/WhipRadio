using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Json;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OllamaTextGenerationServiceTests
{
    private static OllamaTextGenerationService CreateService(
        FakeHttpMessageHandler handler,
        string model = "gemma4:e4b",
        int contextSize = 16384,
        string? keepAlive = "0")
        => new(handler.CreateClient(), Options.Create(new LlmOptions
        {
            Model = model,
            ContextSize = contextSize,
            KeepAlive = keepAlive,
        }));

    private static FakeHttpMessageHandler OkHandler(string content = "Hello from the studio!")
        => FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { message = new { role = "assistant", content } }));

    [TestMethod]
    public async Task CompleteAsync_SendsExpectedRequestShape()
    {
        var handler = OkHandler();
        var service = CreateService(handler, model: "test-model", contextSize: 32768);

        await service.CompleteAsync("system prompt", "user prompt", CancellationToken.None);

        Assert.Equal("/api/chat", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var options = root.GetProperty("options");
        Assert.Equal(0.8, options.GetProperty("temperature").GetDouble());
        Assert.Equal(4096, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0, root.GetProperty("keep_alive").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user prompt", messages[1].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task CompleteAsync_OmitsFormatWhenNoSchema()
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        await service.CompleteAsync("s", "u", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("format", out _));
    }

    [TestMethod]
    public async Task CompleteAsync_IncludesFormatSchemaWhenSupplied()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var schema = StructuredJson.SchemaFor<SampleDto>();

        await service.CompleteAsync(
            new TextGenerationRequest("s", "u", "label", schema, "sample"), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var format = body.RootElement.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("properties").TryGetProperty("name", out _));
    }

    private sealed record SampleDto([property: System.Text.Json.Serialization.JsonRequired] string Name);

    [TestMethod]
    public async Task CompleteAsync_ParsesAssistantContent()
    {
        var service = CreateService(OkHandler("  Up next: a fresh track!  "));

        var result = await service.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Equal("Up next: a fresh track!", result);
    }

    [TestMethod]
    public async Task CompleteAsync_ThrowsOnHttpError()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.InternalServerError, new StringContent(""));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.CompleteAsync("s", "u", CancellationToken.None));
    }

    [TestMethod]
    public void ChooseContextSize_UsesSmallestBucketWithinConfiguredMax()
    {
        Assert.Equal(4096, OllamaContextSizer.ChooseContextSize(16384, 1_000));
        Assert.Equal(8192, OllamaContextSizer.ChooseContextSize(16384, 20_000));
        Assert.Equal(16384, OllamaContextSizer.ChooseContextSize(16384, 50_000));
        Assert.Equal(32768, OllamaContextSizer.ChooseContextSize(32768, 80_000));
        Assert.Equal(2048, OllamaContextSizer.ChooseContextSize(2048, 1_000));
    }

    [TestMethod]
    public async Task TryUnloadDefaultModelAsync_SendsGenerateUnloadRequest()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            JsonContent.Create(new { done = true }));
        var manager = new OllamaModelMemoryManager(
            new SingleClientFactory(handler.CreateClient()),
            Options.Create(new LlmOptions { Model = "test-model" }),
            new ThrowingDbFactory(),
            NullLogger<OllamaModelMemoryManager>.Instance);

        await manager.TryUnloadDefaultModelAsync(CancellationToken.None);

        Assert.Equal("/api/generate", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("keep_alive").GetInt32());
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<RadioDbContext>
    {
        public RadioDbContext CreateDbContext() => throw new InvalidOperationException("Database was not expected.");

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Database was not expected.");
    }
}
