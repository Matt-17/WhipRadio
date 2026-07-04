namespace WhipRadio.Core.Memory;

/// <summary>
/// In-process vector scoring for participant memory (Phase 5). Candidate sets
/// are tiny (a few hundred rows per participant), so a plain cosine scan beats
/// hauling in a vector database — see Phase-0-Tech-Decisions.
/// </summary>
public static class VectorMath
{
    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    /// <summary>Indices of the top-k candidates by cosine similarity, best first,
    /// dropping everything below <paramref name="minSimilarity"/>.</summary>
    public static IReadOnlyList<int> TopK(
        float[] query,
        IReadOnlyList<float[]> candidates,
        int k,
        double minSimilarity = 0)
    {
        var scored = new List<(int Index, double Score)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = CosineSimilarity(query, candidates[i]);
            if (score >= minSimilarity)
            {
                scored.Add((i, score));
            }
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Max(0, k))
            .Select(entry => entry.Index)
            .ToList();
    }
}
