namespace WhipRadio.Orchestrator.Services;

public static class SongDurationJitter
{
    public const int MaxJitterSeconds = 10;

    public static int Apply(int targetSeconds, int minSeconds, int maxSeconds)
    {
        var offsets = CandidateOffsets(targetSeconds, minSeconds, maxSeconds);
        return offsets.Count == 0
            ? Math.Clamp(targetSeconds, minSeconds, maxSeconds)
            : targetSeconds + offsets[Random.Shared.Next(offsets.Count)];
    }

    public static int Apply(int targetSeconds, int minSeconds, int maxSeconds, int offsetSeconds)
        => Math.Clamp(targetSeconds + Math.Clamp(offsetSeconds, -MaxJitterSeconds, MaxJitterSeconds), minSeconds, maxSeconds);

    public static IReadOnlyList<int> CandidateOffsets(int targetSeconds, int minSeconds, int maxSeconds)
    {
        if (minSeconds > maxSeconds)
        {
            (minSeconds, maxSeconds) = (maxSeconds, minSeconds);
        }

        var offsets = Enumerable.Range(-MaxJitterSeconds, MaxJitterSeconds * 2 + 1)
            .Where(offset => offset != 0)
            .Where(offset => targetSeconds + offset >= minSeconds && targetSeconds + offset <= maxSeconds)
            .ToList();

        var nonRoundedOffsets = offsets
            .Where(offset => (targetSeconds + offset) % 10 != 0)
            .ToList();

        return nonRoundedOffsets.Count > 0 ? nonRoundedOffsets : offsets;
    }
}
