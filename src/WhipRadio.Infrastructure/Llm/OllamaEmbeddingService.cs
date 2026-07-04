using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// Embeddings via Ollama's /api/embed on the existing Writer Room endpoint
/// (Phase 5). The model is tiny (nomic-embed-text) — no GPU scheduling, no
/// resilience handler, callers stay failure-soft.
/// </summary>
public sealed class OllamaEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> options) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(TextGenerationRouter.OllamaClientName);
        using var response = await client.PostAsJsonAsync(
            "/api/embed",
            new EmbedRequest(options.Value.EmbeddingModel, text),
            ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
            ?? throw new InvalidOperationException("Ollama returned an empty embed response.");
        var embedding = payload.Embeddings is { Count: > 0 } ? payload.Embeddings[0] : null;
        return embedding is { Length: > 0 }
            ? embedding
            : throw new InvalidOperationException("Ollama returned no embedding vector.");
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]>? Embeddings);
}
