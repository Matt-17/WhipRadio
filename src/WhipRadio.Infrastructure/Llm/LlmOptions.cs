namespace WhipRadio.Infrastructure.Llm;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "gemma4:e4b";

    public int ContextSize { get; set; } = 16384;

    /// <summary>
    /// Ollama model residency after a request. "0" unloads immediately so the
    /// shared GPU is available for ACE-Step/music generation.
    /// </summary>
    public string? KeepAlive { get; set; } = "0";

    public double Temperature { get; set; } = 0.8;
}
