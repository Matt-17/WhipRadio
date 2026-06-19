namespace WhipRadio.Core.Abstractions;

/// <summary>Wraps the LLM chat endpoint (Ollama /api/chat).</summary>
public interface ITextGenerationService
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, string? jobLabel, CancellationToken ct)
        => CompleteAsync(systemPrompt, userPrompt, ct);
}
