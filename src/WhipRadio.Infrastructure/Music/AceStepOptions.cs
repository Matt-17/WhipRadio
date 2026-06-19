namespace WhipRadio.Infrastructure.Music;

public sealed class AceStepOptions
{
    public const string SectionName = "AceStep";

    public string Model { get; set; } = "acestep-v15-turbo";

    public bool Thinking { get; set; } = true;

    public int InferenceSteps { get; set; } = 12;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public string? ApiKey { get; set; }
}
