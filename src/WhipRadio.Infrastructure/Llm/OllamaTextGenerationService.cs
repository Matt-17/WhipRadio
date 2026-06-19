using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Non-streaming client for Ollama's /api/chat endpoint.</summary>
public class OllamaTextGenerationService(
    HttpClient http,
    IOptions<LlmOptions> options,
    ILogger<OllamaTextGenerationService>? logger = null) : ITextGenerationService
{
    private readonly ILogger<OllamaTextGenerationService> _logger =
        logger ?? NullLogger<OllamaTextGenerationService>.Instance;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var configured = options.Value;
        var promptChars = systemPrompt.Length + userPrompt.Length;
        var contextSize = OllamaContextSizer.ChooseContextSize(configured.ContextSize, promptChars);
        var keepAlive = ParseKeepAlive(configured.KeepAlive);
        var request = new ChatRequest(
            Model: configured.Model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            Stream: false,
            KeepAlive: keepAlive,
            Options: new ChatOptions(configured.Temperature, contextSize));

        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Writer Room Ollama request started: model {Model}, context {ContextSize}/{ConfiguredContextSize}, keep-alive {KeepAlive}, temperature {Temperature:F2}, prompt {PromptChars} chars",
            configured.Model, contextSize, configured.ContextSize, configured.KeepAlive ?? "(default)", configured.Temperature, promptChars);

        try
        {
            var result = await SendAsync(request, ct);
            _logger.LogInformation(
                "Writer Room Ollama request completed: model {Model}, duration {ElapsedMs} ms, output {OutputChars} chars",
                configured.Model, sw.ElapsedMilliseconds, result.Length);
            return result;
        }
        catch (Exception ex) when (IsTransportFailure(ex) && !ct.IsCancellationRequested)
        {
            // One retry bridges dropped keep-alive connections / model reloads.
            _logger.LogWarning(
                ex,
                "Writer Room Ollama transport failed after {ElapsedMs} ms; retrying once",
                sw.ElapsedMilliseconds);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                var result = await SendAsync(request, ct);
                _logger.LogInformation(
                    "Writer Room Ollama retry completed: model {Model}, duration {ElapsedMs} ms, output {OutputChars} chars",
                    configured.Model, sw.ElapsedMilliseconds, result.Length);
                return result;
            }
            catch (Exception retryEx) when (!ct.IsCancellationRequested)
            {
                await LogFailureAsync("retry", retryEx, configured.Model, sw.ElapsedMilliseconds, ct);
                throw;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await LogFailureAsync("request", ex, configured.Model, sw.ElapsedMilliseconds, ct);
            throw;
        }
    }

    /// <summary>Retry only transport-level drops — never HTTP error statuses.</summary>
    private static bool IsTransportFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        IOException => true,
        _ => false,
    };

    private static int? StatusCode(Exception ex)
        => ex is HttpRequestException { StatusCode: { } statusCode } ? (int)statusCode : null;

    private async Task LogFailureAsync(
        string operation,
        Exception ex,
        string model,
        long elapsedMs,
        CancellationToken ct)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
        {
            var installedModels = await ReadInstalledModelsAsync(ct);
            _logger.LogWarning(
                ex,
                "Writer Room Ollama model missing during {Operation}: requested {Model}; installed models: {InstalledModels}; duration {ElapsedMs} ms",
                operation, model, installedModels, elapsedMs);
            return;
        }

        _logger.LogWarning(
            ex,
            "Writer Room Ollama {Operation} failed: model {Model}, status {StatusCode}, duration {ElapsedMs} ms",
            operation, model, StatusCode(ex), elapsedMs);
    }

    private async Task<string> ReadInstalledModelsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var tags = await http.GetFromJsonAsync<TagsResponse>("/api/tags", timeout.Token);
            var names = tags?.Models
                .Select(model => model.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return names is { Length: > 0 } ? string.Join(", ", names) : "(none)";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read Ollama model list from /api/tags.");
            return "(unavailable)";
        }
    }

    private async Task<string> SendAsync(ChatRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var chat = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
                   ?? throw new InvalidOperationException("Empty response from Ollama.");
        return chat.Message?.Content.Trim() ?? string.Empty;
    }

    private static object? ParseKeepAlive(string? keepAlive)
    {
        if (string.IsNullOrWhiteSpace(keepAlive))
        {
            return null;
        }

        var trimmed = keepAlive.Trim();
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric
            : trimmed;
    }

    internal sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? KeepAlive,
        [property: JsonPropertyName("options")] ChatOptions Options);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_ctx")] int ContextSize);

    internal sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    internal sealed record TagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<ModelTag> Models);

    internal sealed record ModelTag(
        [property: JsonPropertyName("name")] string Name);
}
