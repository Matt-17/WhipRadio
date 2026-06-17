using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class OllamaTextGenerationServiceTests
{
    private static OllamaTextGenerationService CreateService(
        FakeHttpMessageHandler handler,
        string model = "gemma4:e4b",
        int contextSize = 16384)
        => new(handler.CreateClient(), Options.Create(new LlmOptions { Model = model, ContextSize = contextSize }));

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
        Assert.Equal(32768, options.GetProperty("num_ctx").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user prompt", messages[1].GetProperty("content").GetString());
    }

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
}
