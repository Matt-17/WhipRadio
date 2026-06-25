using System.Text.Json.Nodes;

namespace WhipRadio.Core.Abstractions;

/// <summary>
/// A single text-generation request. <see cref="ResponseSchema"/> opts the call into
/// schema-constrained structured output: when set, the provider asks the model to emit
/// JSON matching the schema (Ollama <c>format</c> / OpenAI <c>response_format</c>).
/// </summary>
public sealed record TextGenerationRequest(
    string SystemPrompt,
    string UserPrompt,
    string? JobLabel = null,
    JsonNode? ResponseSchema = null,
    string? SchemaName = null);

/// <summary>Wraps the LLM chat endpoint (Ollama /api/chat).</summary>
public interface ITextGenerationService
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, string? jobLabel, CancellationToken ct)
        => CompleteAsync(systemPrompt, userPrompt, ct);

    /// <summary>
    /// Schema-aware entry point. The default ignores the schema and falls back to plain
    /// completion, so test doubles only need the string overload; real providers
    /// (Ollama, OpenAI, the router) override this to honor <see cref="TextGenerationRequest.ResponseSchema"/>.
    /// </summary>
    Task<string> CompleteAsync(TextGenerationRequest request, CancellationToken ct)
        => CompleteAsync(request.SystemPrompt, request.UserPrompt, request.JobLabel, ct);
}
