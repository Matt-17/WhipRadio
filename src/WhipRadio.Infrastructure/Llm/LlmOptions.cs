namespace WhipRadio.Infrastructure.Llm;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "gemma4:e4b";

    public int ContextSize { get; set; } = 16384;

    /// <summary>
    /// Ollama model residency after a request. The GPU scheduler now unloads the model
    /// explicitly when a different engine (voice/music) is admitted, so the model is kept
    /// resident between consecutive writer jobs to avoid reloads. ("0" would unload after
    /// every request and reintroduce the thrash.)
    /// </summary>
    public string? KeepAlive { get; set; } = "30m";

    public double Temperature { get; set; } = 0.8;

    /// <summary>Ollama embedding model for participant memory retrieval (Phase 5).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
}
