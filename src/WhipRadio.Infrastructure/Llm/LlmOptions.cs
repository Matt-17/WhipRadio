namespace WhipRadio.Infrastructure.Llm;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Model { get; set; } = "gemma3:4b";

    public double Temperature { get; set; } = 0.8;
}
