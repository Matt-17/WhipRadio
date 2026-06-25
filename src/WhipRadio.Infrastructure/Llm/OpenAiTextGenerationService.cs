using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>OpenAI chat-completions client (alternative text provider for scripts,
/// titles, lyrics and program-director reasoning).</summary>
public class OpenAiTextGenerationService(HttpClient http, string apiKey, string model) : ITextGenerationService
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        => CompleteAsync(new TextGenerationRequest(systemPrompt, userPrompt), ct);

    public async Task<string> CompleteAsync(TextGenerationRequest generation, CancellationToken ct)
    {
        var request = new ChatRequest(
            model,
            [
                new ChatMessage("system", generation.SystemPrompt),
                new ChatMessage("user", generation.UserPrompt),
            ],
            ResponseFormat: BuildResponseFormat(generation));

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

    private static ResponseFormat? BuildResponseFormat(TextGenerationRequest generation)
        => generation.ResponseSchema is null
            ? null
            : new ResponseFormat("json_schema", new JsonSchemaSpec(
                Name: string.IsNullOrWhiteSpace(generation.SchemaName) ? "response" : generation.SchemaName!,
                Strict: true,
                Schema: generation.ResponseSchema));

    internal sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ResponseFormat? ResponseFormat = null);

    internal sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("json_schema")] JsonSchemaSpec JsonSchema);

    internal sealed record JsonSchemaSpec(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("strict")] bool Strict,
        [property: JsonPropertyName("schema")] JsonNode Schema);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    internal sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}
