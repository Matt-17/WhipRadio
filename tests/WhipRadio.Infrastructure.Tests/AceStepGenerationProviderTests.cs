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

        using var body = JsonDocument.Parse(ReleaseTaskBody(handler));
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

        using var body = JsonDocument.Parse(ReleaseTaskBody(handler));
        Assert.False(body.RootElement.GetProperty("sample_mode").GetBoolean());
        Assert.Equal("line one", body.RootElement.GetProperty("lyrics").GetString());
    }

    [TestMethod]
    public async Task ReferenceAudioIsUploadedAsMultipart()
    {
        var referencePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(referencePath, WavTestData.Pcm(96));
        try
        {
            var handler = SuccessHandler(WavTestData.Pcm(128));
            var provider = CreateProvider(handler);

            await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
            {
                LyricsMode = LyricsMode.Provided,
                ReferenceAudioPath = referencePath,
                ReferenceAudioLabel = "First Signal",
            }, CancellationToken.None);

            var releaseIndex = handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/release_task");
            Assert.True(releaseIndex >= 0);
            Assert.Equal("multipart/form-data", handler.Requests[releaseIndex].Content?.Headers.ContentType?.MediaType);
            var releaseBody = handler.RequestBodies[releaseIndex]!;
            Assert.Contains("name=ref_audio", releaseBody);
            Assert.Contains("name=lyrics", releaseBody);
            Assert.Contains("line one", releaseBody);
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [TestMethod]
    public async Task ExistingArtistLoraIsLoadedBeforeGeneration()
    {
        var paths = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => JsonResponse("""{"data":{"status":"ok","models_initialized":true},"code":200,"error":null}"""),
                "/v1/lora/load" => JsonResponse("""{"data":{"message":"loaded"},"code":200,"error":null}"""),
                "/v1/lora/scale" => JsonResponse("""{"data":{"message":"scaled"},"code":200,"error":null}"""),
                "/v1/lora/toggle" => JsonResponse("""{"data":{"message":"enabled"},"code":200,"error":null}"""),
                "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
                "/query_result" => JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x")),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WavTestData.Pcm(128)) },
            };
        });
        var provider = CreateProvider(handler, enableLora: true);

        await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Signal Hands",
            AceStepLoraDatasetPath = "/app/data/acestep/lora-datasets/a",
            AceStepLoraTensorPath = "/models/whipradio/lora/a/tensors",
            AceStepLoraTrainingOutputPath = "/models/whipradio/lora/a/training",
            AceStepLoraAdapterPath = "/models/whipradio/lora/a/adapter",
            AceStepLoraReferences =
            [
                new MusicVoiceReferenceTrack(
                    "First Signal",
                    "0001.wav",
                    "dream pop with breathy vocals",
                    "line one",
                    "en",
                    180,
                    181,
                    0,
                    0),
            ],
        }, CancellationToken.None);

        Assert.True(paths.IndexOf("/v1/lora/load") < paths.IndexOf("/release_task"));
        Assert.Contains("/v1/lora/scale", paths);
        Assert.Contains("/v1/lora/toggle", paths);
    }

    [TestMethod]
    public async Task ArtistLoraSkipsModelDependentEndpointsWhenModelsAreCold()
    {
        var paths = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => JsonResponse("""{"data":{"status":"ok","models_initialized":false},"code":200,"error":null}"""),
                "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
                "/query_result" => JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x")),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WavTestData.Pcm(128)) },
            };
        });
        var provider = CreateProvider(handler, enableLora: true);

        await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Signal Hands",
            AceStepLoraDatasetPath = "/app/data/acestep/lora-datasets/a",
            AceStepLoraTensorPath = "/models/whipradio/lora/a/tensors",
            AceStepLoraTrainingOutputPath = "/models/whipradio/lora/a/training",
            AceStepLoraAdapterPath = "/models/whipradio/lora/a/adapter",
            AceStepLoraReferences =
            [
                new MusicVoiceReferenceTrack(
                    "First Signal",
                    "0001.wav",
                    "dream pop with breathy vocals",
                    "line one",
                    "en",
                    180,
                    181,
                    0,
                    0),
            ],
        }, CancellationToken.None);

        Assert.Contains("/health", paths);
        Assert.DoesNotContain("/v1/init", paths);
        Assert.DoesNotContain("/v1/lora/load", paths);
        Assert.True(paths.IndexOf("/health") < paths.IndexOf("/release_task"));
    }

    [TestMethod]
    public async Task ArtistLoraDisabledUnloadsExistingAdapterBeforeGeneration()
    {
        var paths = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri!.AbsolutePath switch
            {
                "/v1/lora/unload" => JsonResponse("""{"data":{"message":"unloaded"},"code":200,"error":null}"""),
                "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
                "/query_result" => JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x")),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WavTestData.Pcm(128)) },
            };
        });
        var provider = CreateProvider(handler, enableLora: false);

        await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Signal Hands",
            AceStepLoraAdapterPath = "/models/whipradio/lora/a/adapter",
        }, CancellationToken.None);

        Assert.True(paths.IndexOf("/v1/lora/unload") < paths.IndexOf("/release_task"));
        Assert.DoesNotContain("/v1/lora/load", paths);
        Assert.DoesNotContain("/v1/lora/scale", paths);
    }

    [TestMethod]
    public async Task ArtistLoraDatasetSampleUpdateIncludesRequiredSampleIndex()
    {
        var loadCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => JsonResponse("""{"data":{"status":"ok","models_initialized":true},"code":200,"error":null}"""),
                "/v1/lora/load" => ++loadCalls == 1
                    ? JsonResponse("""{"data":null,"code":400,"error":"not found"}""")
                    : JsonResponse("""{"data":{"message":"loaded"},"code":200,"error":null}"""),
                "/v1/dataset/scan" => JsonResponse("""
                    {
                      "data": {
                        "message": "scan-ok",
                        "num_samples": 1,
                        "samples": [{ "index": 0, "filename": "0001.wav", "audio_path": "/app/data/0001.wav" }]
                      },
                      "code": 200,
                      "error": null
                    }
                    """),
                "/v1/dataset/sample/0" => JsonResponse("""{"data":{"message":"updated"},"code":200,"error":null}"""),
                "/v1/dataset/preprocess_async" => JsonResponse("""{"data":{"task_id":"prep-1","message":"started","total":1},"code":200,"error":null}"""),
                "/v1/dataset/preprocess_status/prep-1" => JsonResponse("""{"data":{"task_id":"prep-1","status":"completed","progress":"done","current":1,"total":1,"error":null},"code":200,"error":null}"""),
                "/v1/training/start" => JsonResponse("""{"data":{"message":"started"},"code":200,"error":null}"""),
                "/v1/training/status" => JsonResponse("""{"data":{"is_training":false,"current_loss":null,"status":"done","current_epoch":1,"error":null},"code":200,"error":null}"""),
                "/v1/training/export" => JsonResponse("""{"data":{"message":"exported"},"code":200,"error":null}"""),
                "/v1/lora/scale" => JsonResponse("""{"data":{"message":"scaled"},"code":200,"error":null}"""),
                "/v1/lora/toggle" => JsonResponse("""{"data":{"message":"enabled"},"code":200,"error":null}"""),
                "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
                "/query_result" => JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x")),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WavTestData.Pcm(128)) },
            };
        });
        var provider = CreateProvider(handler, enableLora: true);

        await provider.GenerateAsync(new MusicRequest("dream pop", "pop", true, "line one", 60)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Signal Hands",
            AceStepLoraDatasetPath = "/app/data/acestep/lora-datasets/a",
            AceStepLoraTensorPath = "/models/whipradio/lora/a/tensors",
            AceStepLoraTrainingOutputPath = "/models/whipradio/lora/a/training",
            AceStepLoraAdapterPath = "/models/whipradio/lora/a/adapter",
            AceStepLoraReferences =
            [
                new MusicVoiceReferenceTrack(
                    "First Signal",
                    "0001.wav",
                    "dream pop with breathy vocals",
                    "line one",
                    "en",
                    180,
                    181,
                    0,
                    0),
            ],
        }, CancellationToken.None);

        var updateIndex = handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/v1/dataset/sample/0");
        Assert.True(updateIndex >= 0);
        using var body = JsonDocument.Parse(handler.RequestBodies[updateIndex]!);
        Assert.Equal(0, body.RootElement.GetProperty("sample_idx").GetInt32());
        Assert.Equal("caption", body.RootElement.GetProperty("prompt_override").GetString());
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

        using var body = JsonDocument.Parse(ReleaseTaskBody(handler));
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
    public async Task RunningStatusReportsProgressText()
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
                    ? JsonResponse("""{"data":[{"task_id":"task-1","status":0,"result":null,"progress_text":" diffusion   step 3/12 "}],"code":200,"error":null}""")
                    : JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        });
        var provider = CreateProvider(handler);
        var progress = new List<MusicGenerationProgress>();

        await provider.GenerateAsync(Request() with
        {
            ProgressReporter = (update, _) =>
            {
                progress.Add(update);
                return ValueTask.CompletedTask;
            },
        }, CancellationToken.None);

        Assert.Contains(progress, p => p.TaskId == "task-1" && p.Message == "queued");
        Assert.Contains(progress, p => p.TaskId == "task-1" && p.Message == "diffusion step 3/12");
        Assert.Contains(progress, p => p.TaskId == "task-1" && p.Message == "render complete");
        Assert.Contains(progress, p => p.TaskId == "task-1" && p.Message == "downloading audio");
    }

    [TestMethod]
    public async Task RunningStatusStripsProviderLogTimestampFromProgressText()
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
                    ? JsonResponse("""{"data":[{"task_id":"task-1","status":0,"result":null,"progress_text":"19:21:05 | WARNING | [tiled_decode] Reduced overlap from 64 to 32"}],"code":200,"error":null}""")
                    : JsonResponse(QuerySuccess("task-1", "/v1/audio?path=x"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        });
        var provider = CreateProvider(handler);
        var progress = new List<MusicGenerationProgress>();

        await provider.GenerateAsync(Request() with
        {
            ProgressReporter = (update, _) =>
            {
                progress.Add(update);
                return ValueTask.CompletedTask;
            },
        }, CancellationToken.None);

        Assert.Contains(progress, p => p.TaskId == "task-1"
            && p.Message == "WARNING | [tiled_decode] Reduced overlap from 64 to 32");
        Assert.DoesNotContain(progress, p => p.Message.StartsWith("19:21", StringComparison.Ordinal));
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
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":2,"result":null,"progress_text":"RuntimeError: Insufficient KV cache to schedule sequence."}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Contains("Task task-1 failed", ex.Message);
        Assert.Contains("Insufficient KV cache", ex.Message);
    }

    [TestMethod]
    public async Task InternalGenerationTimeoutThrowsTimeoutException()
    {
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/release_task" => JsonResponse("""{"data":{"task_id":"task-1","status":"queued"},"code":200,"error":null}"""),
            "/query_result" => JsonResponse("""{"data":[{"task_id":"task-1","status":2,"result":null,"progress_text":"TimeoutError: Music generation timed out after 600 seconds."}],"code":200,"error":null}"""),
            _ => throw new InvalidOperationException("unexpected"),
        });
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Contains("timed out inside the sidecar", ex.Message);
    }

    [TestMethod]
    public async Task JingleRequestUsesDirectShortPrompt()
    {
        var handler = SuccessHandler(WavTestData.Pcm(128));
        var provider = CreateProvider(handler);

        await provider.GenerateAsync(new MusicRequest(
            "Vocal 9s radio jingle for Night Lab FM. Mood: Made after dark. Style: tight analog drums. Sung station ID and slogan hook.",
            "jingle",
            WantVocals: true,
            Lyrics: "Night Lab FM\nMade after dark.",
            DurationSeconds: 9)
        {
            LyricsMode = LyricsMode.Provided,
            Provider = MusicBackends.AceStep,
            SubGenre = "radio identity",
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(ReleaseTaskBody(handler));
        var prompt = body.RootElement.GetProperty("prompt").GetString();
        Assert.False(body.RootElement.GetProperty("thinking").GetBoolean());
        Assert.False(body.RootElement.GetProperty("sample_mode").GetBoolean());
        Assert.Equal("Night Lab FM\nMade after dark.", body.RootElement.GetProperty("lyrics").GetString());
        Assert.Contains("radio jingle", prompt);
        Assert.Contains("slogan hook", prompt);
        Assert.DoesNotContain("full-length", prompt);
        Assert.DoesNotContain("complete song structure", prompt);
        Assert.DoesNotContain("no vocals", prompt);
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

        var ex = await Assert.ThrowsAsync<MusicGenerationFailedException>(() => provider.GenerateAsync(Request(), CancellationToken.None));

        Assert.Contains("/release_task returned 500", ex.Message);
        Assert.Contains("boom", ex.Message);
        Assert.Equal(1, releaseCalls);
    }

    private static MusicRequest Request() => new("atmospheric indie rock", "rock", false, null, 30)
    {
        LyricsMode = LyricsMode.Instrumental,
        Provider = MusicBackends.AceStep,
    };

    private static AceStepGenerationProvider CreateProvider(
        FakeHttpMessageHandler handler,
        AceStepOptions? options = null,
        bool enableLora = false)
    {
        var configured = options ?? new AceStepOptions { PollInterval = TimeSpan.FromMilliseconds(1) };
        configured.EnableArtistLora = enableLora;
        return new AceStepGenerationProvider(
            handler.CreateClient(),
            new AceStepPromptBuilder(),
            Options.Create(configured),
            NullLogger<AceStepGenerationProvider>.Instance);
    }

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

    private static string ReleaseTaskBody(FakeHttpMessageHandler handler)
    {
        var index = handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/release_task");
        Assert.True(index >= 0);
        return handler.RequestBodies[index]!;
    }

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
