using WhipRadio.Core.Entities;
using WhipRadio.Core.Metadata;

namespace WhipRadio.Core.Tests;

[TestClass]
public class MetadataMatchScorerTests
{
    private static TrackMatchEvidence Evidence(
        string? title = "Teardrop",
        string? artist = "Massive Attack",
        string? album = "Mezzanine",
        int? trackNumber = 3,
        double? duration = 330,
        string? isrc = null,
        string? recordingId = null)
        => new(title, artist, album, trackNumber, Year: 1998, duration, isrc, recordingId);

    private static RecordingCandidate Candidate(
        string title = "Teardrop",
        string artist = "Massive Attack",
        string? album = "Mezzanine",
        int? trackNumber = 3,
        double? duration = 330,
        string recordingId = "mbid-recording-1",
        IReadOnlyList<string>? isrcs = null)
        => new(recordingId, title, artist, "mbid-artist-1", album, 1998, trackNumber, duration, isrcs);

    [TestMethod]
    public void EmbeddedRecordingId_IsAStrongAnchorAndAutoMatches()
    {
        var match = MetadataMatchScorer.Score(
            Evidence(recordingId: "mbid-recording-1"), Candidate());

        Assert.True(match.HasStrongAnchor);
        Assert.Equal(MetadataStatus.AutoMatched, MetadataMatchScorer.Classify(match));
    }

    [TestMethod]
    public void IsrcWithPlausibleTitle_IsAStrongAnchorAndAutoMatches()
    {
        var match = MetadataMatchScorer.Score(
            Evidence(isrc: "GBAAA9800001"), Candidate(isrcs: ["GBAAA9800001"]));

        Assert.True(match.HasStrongAnchor);
        Assert.Equal(MetadataStatus.AutoMatched, MetadataMatchScorer.Classify(match));
    }

    [TestMethod]
    public void IsrcMatchWithCompletelyDifferentIdentity_IsNotAnAnchor()
    {
        var match = MetadataMatchScorer.Score(
            Evidence(title: "Something Else Entirely", artist: "Somebody Unrelated", isrc: "GBAAA9800001"),
            Candidate(isrcs: ["GBAAA9800001"]));

        Assert.False(match.HasStrongAnchor);
    }

    [TestMethod]
    public void FullFieldAgreement_IsAStrongAnchor()
    {
        var match = MetadataMatchScorer.Score(Evidence(), Candidate());

        Assert.True(match.HasStrongAnchor);
        Assert.Equal(MetadataStatus.AutoMatched, MetadataMatchScorer.Classify(match));
        Assert.Contains(match.Reasons, r => r.Contains("track number"));
    }

    [TestMethod]
    public void FuzzyOnlyMatch_NeverReachesAutoMatch()
    {
        // Perfect fuzzy agreement but no album/track anchor: capped below 0.95.
        var match = MetadataMatchScorer.Score(
            Evidence(album: null, trackNumber: null),
            Candidate(album: null, trackNumber: null));

        Assert.False(match.HasStrongAnchor);
        Assert.True(match.Score < MetadataMatchScorer.AutoMatchThreshold);
        Assert.Equal(MetadataStatus.Matched, MetadataMatchScorer.Classify(match));
    }

    [TestMethod]
    public void PartialSimilarity_LandsInAmbiguous()
    {
        var match = MetadataMatchScorer.Score(
            Evidence(title: "Teardrop (Live at Glastonbury)", album: null, trackNumber: null, duration: 402),
            Candidate(album: null, trackNumber: null));

        Assert.Equal(MetadataStatus.Ambiguous, MetadataMatchScorer.Classify(match));
    }

    [TestMethod]
    public void UnrelatedCandidate_NeedsReview()
    {
        var match = MetadataMatchScorer.Score(
            Evidence(title: "track07_final", artist: null, album: null, trackNumber: null, duration: 95),
            Candidate());

        Assert.Equal(MetadataStatus.NeedsReview, MetadataMatchScorer.Classify(match));
    }

    [TestMethod]
    public void Similarity_IsCaseAndPunctuationInsensitiveButKeepsDistinctNamesApart()
    {
        Assert.Equal(1.0, MetadataMatchScorer.Similarity("Don’t Stop", "don't stop"), 3);
        Assert.True(MetadataMatchScorer.Similarity("Björk", "Bjork") < 1.0);
        Assert.Equal(0.0, MetadataMatchScorer.Similarity(null, "anything"), 3);
    }
}
