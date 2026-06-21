namespace WhipRadio.Infrastructure.Music;

public sealed class AceStepOptions
{
    public const string SectionName = "AceStep";

    public string Model { get; set; } = "acestep-v15-turbo";

    public bool Thinking { get; set; } = true;

    public int InferenceSteps { get; set; } = 12;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(45);

    public string? ApiKey { get; set; }

    public bool EnableArtistLora { get; set; }

    public int ArtistLoraMinReferenceTracks { get; set; } = 1;

    public double ArtistLoraScale { get; set; } = 0.75;

    public int ArtistLoraRank { get; set; } = 32;

    public int ArtistLoraAlpha { get; set; } = 64;

    public double ArtistLoraDropout { get; set; } = 0.1;

    public double ArtistLoraLearningRate { get; set; } = 0.0001;

    public int ArtistLoraTrainEpochs { get; set; } = 10;

    public int ArtistLoraBatchSize { get; set; } = 1;

    public int ArtistLoraGradientAccumulation { get; set; } = 4;

    public int ArtistLoraSaveEveryEpochs { get; set; } = 5;

    public double ArtistLoraTrainingShift { get; set; } = 3.0;

    public int ArtistLoraTrainingSeed { get; set; } = 42;

    public bool ArtistLoraUseFp8 { get; set; }

    public bool ArtistLoraGradientCheckpointing { get; set; } = true;

    public TimeSpan ArtistLoraPreprocessTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ArtistLoraTrainingTimeout { get; set; } = TimeSpan.FromMinutes(90);
}
