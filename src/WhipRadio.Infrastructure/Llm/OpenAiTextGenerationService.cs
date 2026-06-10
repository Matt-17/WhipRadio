using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>OpenAI chat-completions client (alternative text provider for scripts,
/// titles, lyrics and program-director reasoning).</summary>
public class OpenAiTextGenerationService(HttpClient http, string apiKey, string model) : ITextGenerationService
{
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new ChatRequest(
            model,
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();

        var completion = await response.Content.ReadFromJsonAsync<ChatResponse>(ct)
                         ?? throw new InvalidOperationException("Empty response from OpenAI.");
        return completion.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
    }

    internal sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    internal sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
