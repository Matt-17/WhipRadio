using WhipRadio.Core.Memory;

namespace WhipRadio.Core.Tests;

[TestClass]
public class VectorMathTests
{
    [TestMethod]
    public void CosineSimilarity_KnownValues()
    {
        Assert.Equal(1.0, VectorMath.CosineSimilarity([1f, 0f], [1f, 0f]), 6);
        Assert.Equal(0.0, VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(-1.0, VectorMath.CosineSimilarity([1f, 0f], [-1f, 0f]), 6);
    }

    [TestMethod]
    public void CosineSimilarity_MismatchedOrEmptyVectorsScoreZero()
    {
        Assert.Equal(0.0, VectorMath.CosineSimilarity([1f, 0f], [1f, 0f, 0f]), 6);
        Assert.Equal(0.0, VectorMath.CosineSimilarity([], []), 6);
        Assert.Equal(0.0, VectorMath.CosineSimilarity([0f, 0f], [1f, 0f]), 6);
    }

    [TestMethod]
    public void TopK_RanksBestFirstAndAppliesTheFloor()
    {
        float[] query = [1f, 0f];
        List<float[]> candidates =
        [
            [0f, 1f],      // orthogonal — below floor
            [1f, 0.1f],    // very close
            [0.7f, 0.7f],  // ~0.7 similarity
            [1f, 0f],      // identical
        ];

        var top = VectorMath.TopK(query, candidates, k: 2, minSimilarity: 0.35);

        Assert.Equal(new[] { 3, 1 }, top.ToArray());
    }

    [TestMethod]
    public void TopK_ReturnsFewerWhenNothingClears()
    {
        var top = VectorMath.TopK([1f, 0f], [[0f, 1f], [0f, -1f]], k: 3, minSimilarity: 0.35);
        Assert.Empty(top);
    }
}
