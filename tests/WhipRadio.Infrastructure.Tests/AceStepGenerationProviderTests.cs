using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Music;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class AceStepGenerationProviderTests
{
    [TestMethod]
    public async Task AutomaticLyricsRequestIsMappedCorrectly()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(new MusicRequest("make a pop song", "pop", true, null, 120)
        {
            LyricsMode = LyricsMode.Auto,
            Language = "en",
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.True(body.RootElement.GetProperty("sample_mode").GetBoolean());
        Assert.Equal("en", body.RootElement.GetProperty("vocal_language").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lyrics").ValueKind);
        Assert.Equal("wav", body.RootElement.GetProperty("audio_format").GetString());
    }

    [TestMethod]
    public async Task ProvidedLyricsAreSentCorrectly()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
        {
            LyricsMode = LyricsMode.Provided,
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.False(body.RootElement.GetProperty("sample_mode").GetBoolean());
        Assert.Equal("line one", body.RootElement.GetProperty("lyrics").GetString());
    }

    [TestMethod]
    public async Task MissingProvidedLyricsFailBeforeHttpRequest()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicProviderValidationException>(() => provider.GenerateAsync(
            new MusicRequest("dream pop", "pop", true, "", 60) { LyricsMode = LyricsMode.Provided },
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [TestMethod]
    public async Task DurationIsClampedToSupportedRange()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(new MusicRequest("ambient", "ambient", false, null, 900)
        {
            LyricsMode = LyricsMode.Instrumental,
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal(600, body.RootElement.GetProperty("audio_duration").GetInt32());
    }

    [TestMethod]
    public async Task TaskCreationReturnsAndStoresTaskId()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128), taskId: "task-123");
        var provider = CreateProvider(handler);

        var result = await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal("task-123", result.TaskId);
    }

    [TestMethod]
    public async Task RunningStatusContinuesPolling()
    {
        var wav = WavTestData.Pcm(128);
        var queryCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/release_task")
            {
                return JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}""");
            }

            if (req.RequestUri.AbsolutePath == "/query_result")
            {
                queryCalls++;
                return queryCalls == 1
                    ? JsonResponse("""{"data":[{"task_id":"task-1","status":0,"result":null}],"code":200,"error":null}""")
                    : JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        });
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(2, queryCalls);
    }

    [TestMethod]
    public async Task SuccessStatusDownloadsResult()
    {
        var wav = WavTestData.Pcm(128);
        var handler = SuccessHandler(wav);
        var provider = CreateProvider(handler);

        var result = await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.Equal(wav, result.WavData);
        Assert.Equal(MusicBackends.AceStep, result.BackendUsed);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/v1/audio");
    }

    [TestMethod]
    public async Task FailureStatusThrowsUsefulException()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":2,"result":null}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Contains("Task task-1 failed", ex.Message);
    }

    [TestMethod]
    public async Task CancellationStopsPolling()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":0,"result":null}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler, new AceStepOptions { PollInterval = TimeSpan.FromMilliseconds(5), GenerationTimeout = TimeSpan.FromMinutes(1) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Request(), cts.Token));
    }

    [TestMethod]
    public async Task TimeoutStopsPolling()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":0,"result":null}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler, new AceStepOptions { PollInterval = TimeSpan.FromMilliseconds(5), GenerationTimeout = TimeSpan.FromMilliseconds(20) });

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateAsync(Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task MalformedResultIsRejected()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":1,"result":"not json"}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task EmptyResultIsRejected()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x")),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
        });
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidWavDataIsRejected()
    {
        var handler = SuccessHandler([1, 2, 3, 4, 5]);
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task ApiKeyIsSentWhenConfigured()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler, new AceStepOptions { ApiKey = "secret" });

        await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
        Assert.All(handler.Requests, request => Assert.Equal("secret", request.Headers.Authorization?.Parameter));
    }

    [TestMethod]
    public async Task NoAuthorizationHeaderIsSentWithoutKey()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(Request(), CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [TestMethod]
    public async Task TaskCreationIsNotAutomaticallyRetried()
    {
        var releaseCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/release_task")
            {
                releaseCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom"),
                };
            }

            throw new InvalidOperationException("unexpected retry path");
        });
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Equal(1, releaseCalls);
    }

    private static MusicRequest Request() => new("atmospheric indie rock", "rock", false, null, 30)
    {
        LyricsMode = LyricsMode.Instrumental,
        Provider = MusicBackends.AceStep,
    };

    private static AceStepGenerationProvider CreateProvider(FakeHttpMessageHandler handler, AceStepOptions? options = null)
        => new(
            handler.CreateClient(),
            new AceStepPromptBuilder(),
            Options.Create(options ?? new AceStepOptions { PollInterval = TimeSpan.FromMilliseconds(1) }),
            NullLogger<AceStepGenerationProvider>.Instance);

    private static FakeHttpMessageHandler SuccessHandler(byte[] wav, string taskId = "task-1")
        => new(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse($$"""{"data":{"task_id":"{{taskId}}","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse(QuerySuccess(taskId, "/v1/audio?path=x")),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) },
        });

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string QuerySuccess(string taskId, string file)
    {
        var result = JsonSerializer.Serialize(new[]
        {
            new
            {
                file,
                seed_value = "123",
                dit_model = "acestep-v15-turbo",
            },
        });
        return JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { task_id = taskId, status = 1, result },
            },
            code = 200,
            error = (string?)null,
        });
    }
}
