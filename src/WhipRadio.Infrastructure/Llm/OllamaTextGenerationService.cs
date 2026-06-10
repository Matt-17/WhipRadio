using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Non-streaming client for Ollama's /api/chat endpoint.</summary>
public class OllamaTextGenerationService(HttpClient http, IOptions<LlmOptions> options) : ITextGenerationService
{
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new ChatRequest(
            Model: options.Value.Model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            Stream: false,
            Options: new ChatOptions(options.Value.Temperature));

        try
        {
            return await SendAsync(request, ct);
        }
        catch (Exception ex) when (IsTransportFailure(ex) && !ct.IsCancellationRequested)
        {
            // One retry bridges dropped keep-alive connections / model reloads.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return await SendAsync(request, ct);
        }
    }

    /// <summary>Retry only transport-level drops — never HTTP error statuses.</summary>
    private static bool IsTransportFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        IOException => true,
        _ => false,
    };

    private async Task<string> SendAsync(ChatRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var chat = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
                   ?? throw new InvalidOperationException("Empty response from Ollama.");
        return chat.Message?.Content.Trim() ?? string.Empty;
    }

    internal sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] ChatOptions Options);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record ChatOptions(
        [property: JsonPropertyName("temperature")] double Temperature);

    internal sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
