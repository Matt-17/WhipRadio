namespace WhipRadio.Infrastructure.Llm;

public static class OllamaContextSizer
{
    private const int MinimumContextSize = 4096;
    private const int ResponseReserveTokens = 2048;
    private const int EstimatedCharsPerToken = 4;

    private static readonly int[] ContextBuckets =
    [
        4096,
        8192,
        16384,
        32768,
        65536,
        131072,
    ];

    public static int ChooseContextSize(int configuredMaxContext, int promptCharacters)
    {
        configuredMaxContext = Math.Max(1, configuredMaxContext);
        promptCharacters = Math.Max(0, promptCharacters);

        var estimatedPromptTokens = (int)Math.Ceiling(promptCharacters / (double)EstimatedCharsPerToken);
        var requiredContext = Math.Max(1, estimatedPromptTokens) + ResponseReserveTokens;

        foreach (var bucket in ContextBuckets)
        {
            if (bucket >= requiredContext && bucket <= configuredMaxContext)
            {
                return bucket;
            }
        }

        if (requiredContext <= MinimumContextSize && configuredMaxContext >= MinimumContextSize)
        {
            return MinimumContextSize;
        }

        return configuredMaxContext;
    }
}
