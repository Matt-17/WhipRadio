using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Music;

public sealed class AceStepGenerationProvider(
    HttpClient http,
    AceStepPromptBuilder promptBuilder,
    IOptions<AceStepOptions> options,
    ILogger<AceStepGenerationProvider> logger) : IMusicGenerationProvider
{
    public const int MinDurationSeconds = 10;
    public const int MaxDurationSeconds = 600;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id => MusicBackends.AceStep;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await SendAsync<ApiResponse<HealthData>>(HttpMethod.Get, "/health", null, cancellationToken);
            return string.Equals(health.Data?.Status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken cancellationToken)
    {
        if (request.LyricsMode == LyricsMode.Provided && string.IsNullOrWhiteSpace(request.Lyrics))
        {
            throw new MusicProviderValidationException("ACE-Step lyrics mode 'Provided' requires non-empty lyrics.");
        }

        var configured = options.Value;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(configured.GenerationTimeout);
        var ct = timeoutCts.Token;

        var prompt = promptBuilder.Build(request);
        var durationSeconds = ClampDuration(request.DurationSeconds);
        var body = BuildRequest(request, prompt, durationSeconds, configured);
        var stopwatch = Stopwatch.StartNew();

        string taskId;
        try
        {
            var created = await SendAsync<ApiResponse<CreateTaskData>>(HttpMethod.Post, "/release_task", body, ct);
            taskId = created.Data?.TaskId
                ?? throw new MusicGenerationFailedException(Id, "Task creation response did not include a task_id.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ACE-Step generation timed out after {configured.GenerationTimeout} before task creation completed.");
        }

        logger.LogInformation(
            "ACE-Step task {TaskId} accepted for artist {Artist}; model {Model}; requested duration {Duration}s",
            taskId, request.ArtistName ?? "(unknown)", configured.Model, durationSeconds);

        QueryResultItem result;
        try
        {
            result = await PollUntilCompleteAsync(taskId, configured, ct);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ACE-Step task {taskId} timed out after {configured.GenerationTimeout}.");
        }

        var audio = await DownloadAudioAsync(result.File, ct);
        ValidateWav(audio);

        stopwatch.Stop();
        logger.LogInformation(
            "ACE-Step task {TaskId} completed with provider {Provider}; model {Model}; seed {Seed}; artist {Artist}; requested duration {Duration}s; elapsed {Elapsed}; result size {Bytes} bytes",
            taskId, Id, result.DitModel ?? configured.Model, result.SeedValue ?? "(unknown)",
            request.ArtistName ?? "(unknown)", durationSeconds, stopwatch.Elapsed, audio.Length);

        return new MusicResult(audio, Id, result.DitModel ?? configured.Model, result.SeedValue, taskId);
    }

    internal static int ClampDuration(int durationSeconds)
        => Math.Clamp(durationSeconds, MinDurationSeconds, MaxDurationSeconds);

    private static ReleaseTaskRequest BuildRequest(
        MusicRequest request,
        string prompt,
        int durationSeconds,
        AceStepOptions options)
    {
        var hasSeed = request.Seed.HasValue;
        var seed = request.Seed ?? -1;
        var thinking = options.Thinking && !IsJingleRequest(request);
        return request.LyricsMode switch
        {
            LyricsMode.Auto => new ReleaseTaskRequest(
                Prompt: null,
                SampleQuery: prompt,
                SampleMode: true,
                Lyrics: null,
                Thinking: thinking,
                VocalLanguage: request.Language ?? "en",
                AudioFormat: "wav",
                AudioDuration: durationSeconds,
                Bpm: request.Bpm,
                KeyScale: request.KeyScale,
                TimeSignature: request.TimeSignature,
                InferenceSteps: options.InferenceSteps,
                UseRandomSeed: !hasSeed,
                Seed: hasSeed ? seed : -1,
                Model: options.Model,
                BatchSize: 1),
            _ => new ReleaseTaskRequest(
                Prompt: prompt,
                SampleQuery: null,
                SampleMode: false,
                Lyrics: request.LyricsMode == LyricsMode.Instrumental ? string.Empty : request.Lyrics,
                Thinking: thinking,
                VocalLanguage: request.Language ?? "en",
                AudioFormat: "wav",
                AudioDuration: durationSeconds,
                Bpm: request.Bpm,
                KeyScale: request.KeyScale,
                TimeSignature: request.TimeSignature,
                InferenceSteps: options.InferenceSteps,
                UseRandomSeed: !hasSeed,
                Seed: hasSeed ? seed : -1,
                Model: options.Model,
                BatchSize: 1),
        };
    }

    private static bool IsJingleRequest(MusicRequest request)
        => string.Equals(request.Genre, "jingle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.SubGenre, "radio identity", StringComparison.OrdinalIgnoreCase);

    private async Task<QueryResultItem> PollUntilCompleteAsync(
        string taskId,
        AceStepOptions configured,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (true)
        {
            await Task.Delay(configured.PollInterval, cancellationToken);

            ApiResponse<List<QueryTaskData>> response;
            try
            {
                response = await SendAsync<ApiResponse<List<QueryTaskData>>>(
                    HttpMethod.Post,
                    "/query_result",
                    new QueryRequest([taskId]),
                    cancellationToken);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                consecutiveFailures++;
                if (consecutiveFailures > 3)
                {
                    throw new MusicGenerationFailedException(Id, $"Polling failed repeatedly for task {taskId}: {ex.Message}");
                }

                logger.LogWarning(ex, "Temporary ACE-Step polling failure for task {TaskId}", taskId);
                continue;
            }

            var task = response.Data?.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));
            if (task is null)
            {
                throw new MusicGenerationFailedException(Id, $"Polling response did not include task {taskId}.");
            }

            if (task.Status == 0)
            {
                continue;
            }

            if (task.Status == 2)
            {
                var detail = DescribeFailedTask(task);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    logger.LogWarning("ACE-Step task {TaskId} failed: {Detail}", taskId, detail);
                    throw new MusicGenerationFailedException(Id, $"Task {taskId} failed: {Truncate(detail, 600)}");
                }

                throw new MusicGenerationFailedException(Id, $"Task {taskId} failed.");
            }

            if (task.Status != 1)
            {
                throw new MusicGenerationFailedException(Id, $"Task {taskId} returned unknown status {task.Status}.");
            }

            var parsed = ParseTaskResult(task.Result);
            if (string.IsNullOrWhiteSpace(parsed.File))
            {
                throw new MusicGenerationFailedException(Id, $"Task {taskId} succeeded without an audio file URL.");
            }

            return parsed;
        }
    }

    private static QueryResultItem ParseTaskResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new MusicGenerationFailedException(MusicBackends.AceStep, "Task result was empty.");
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<QueryResultItem>>(result, JsonOptions);
            return items?.FirstOrDefault()
                ?? throw new MusicGenerationFailedException(MusicBackends.AceStep, "Task result did not contain any items.");
        }
        catch (JsonException ex)
        {
            throw new MusicGenerationFailedException(MusicBackends.AceStep, $"Task result JSON was malformed: {ex.Message}");
        }
    }

    private static string? DescribeFailedTask(QueryTaskData task)
    {
        var progress = NormalizeFailureText(task.ProgressText);
        if (!string.IsNullOrWhiteSpace(progress))
        {
            return progress;
        }

        if (string.IsNullOrWhiteSpace(task.Result))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(task.Result);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("stage", out var stage)
                    && stage.ValueKind == JsonValueKind.String)
                {
                    return $"stage={stage.GetString()}";
                }
            }
        }
        catch (JsonException)
        {
            return NormalizeFailureText(task.Result);
        }

        return null;
    }

    private static string? NormalizeFailureText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";

    private async Task<byte[]> DownloadAudioAsync(string file, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Get, file, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
        {
            throw new MusicGenerationFailedException(Id, "Downloaded audio was empty.");
        }

        return audio;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new MusicBackendUnavailableException(Id);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new MusicGenerationFailedException(Id, $"Response from {path} was empty.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var apiKey = options.Value.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void ValidateWav(byte[] audio)
    {
        if (audio.Length < 12)
        {
            throw new MusicGenerationFailedException(MusicBackends.AceStep, "Downloaded audio was too small to be a WAV file.");
        }

        if (audio[0] != 'R' || audio[1] != 'I' || audio[2] != 'F' || audio[3] != 'F'
            || audio[8] != 'W' || audio[9] != 'A' || audio[10] != 'V' || audio[11] != 'E')
        {
            throw new MusicGenerationFailedException(MusicBackends.AceStep, "Downloaded audio was not valid WAV data.");
        }
    }

    private sealed record ReleaseTaskRequest(
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("sample_query")] string? SampleQuery,
        [property: JsonPropertyName("sample_mode")] bool SampleMode,
        [property: JsonPropertyName("lyrics")] string? Lyrics,
        [property: JsonPropertyName("thinking")] bool Thinking,
        [property: JsonPropertyName("vocal_language")] string VocalLanguage,
        [property: JsonPropertyName("audio_format")] string AudioFormat,
        [property: JsonPropertyName("audio_duration")] int AudioDuration,
        [property: JsonPropertyName("bpm")] int? Bpm,
        [property: JsonPropertyName("key_scale")] string? KeyScale,
        [property: JsonPropertyName("time_signature")] string? TimeSignature,
        [property: JsonPropertyName("inference_steps")] int InferenceSteps,
        [property: JsonPropertyName("use_random_seed")] bool UseRandomSeed,
        [property: JsonPropertyName("seed")] int Seed,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("batch_size")] int BatchSize);

    private sealed record QueryRequest(
        [property: JsonPropertyName("task_id_list")] IReadOnlyList<string> TaskIdList);

    private sealed record ApiResponse<T>(
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record HealthData(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("service")] string? Service,
        [property: JsonPropertyName("version")] string? Version);

    private sealed record CreateTaskData(
        [property: JsonPropertyName("task_id")] string? TaskId,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record QueryTaskData(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("progress_text")] string? ProgressText);

    private sealed record QueryResultItem(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("seed_value")] string? SeedValue,
        [property: JsonPropertyName("dit_model")] string? DitModel);
}
