namespace WhipRadio.Infrastructure.Llm;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "gemma4:e4b";

    public int ContextSize { get; set; } = 16384;

    public double Temperature { get; set; } = 0.8;
}
