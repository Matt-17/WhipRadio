using WhipRadio.Core.Metadata;

namespace WhipRadio.Core.Tests;

[TestClass]
public class FilenameHeuristicsTests
{
    [TestMethod]
    public void Parse_ArtistDashTitle()
    {
        var clues = FilenameHeuristics.Parse(@"D:\music\Massive Attack - Teardrop.mp3");

        Assert.Equal("Massive Attack", clues.Artist);
        Assert.Equal("Teardrop", clues.Title);
        Assert.Null(clues.TrackNumber);
    }

    [TestMethod]
    public void Parse_TrackNumberDashTitle()
    {
        var clues = FilenameHeuristics.Parse(@"D:\music\01 - Teardrop.mp3");

        Assert.Null(clues.Artist);
        Assert.Equal("Teardrop", clues.Title);
        Assert.Equal(1, clues.TrackNumber);
    }

    [TestMethod]
    public void Parse_ArtistAlbumFolderShape()
    {
        var clues = FilenameHeuristics.Parse(@"D:\music\Massive Attack\Mezzanine\03 Teardrop.wav");

        Assert.Equal("Massive Attack", clues.Artist);
        Assert.Equal("Mezzanine", clues.Album);
        Assert.Equal("Teardrop", clues.Title);
        Assert.Equal(3, clues.TrackNumber);
    }

    [TestMethod]
    public void Parse_ArtistInNameWinsOverFolders()
    {
        var clues = FilenameHeuristics.Parse(@"D:\music\Mezzanine\Massive Attack - Teardrop.wav");

        Assert.Equal("Massive Attack", clues.Artist);
        Assert.Equal("Teardrop", clues.Title);
        Assert.Null(clues.Album);
    }

    [TestMethod]
    public void Parse_PlainStemFallsBackToTitleOnly()
    {
        var clues = FilenameHeuristics.Parse(@"D:\music\teardrop_final_v2.mp3");

        Assert.Null(clues.Artist);
        Assert.Equal("teardrop_final_v2", clues.Title);
    }

    [TestMethod]
    public void NormalizeForMatching_FoldsCaseWhitespaceAndPunctuation()
    {
        Assert.Equal(
            "don't stop - the remix",
            FilenameHeuristics.NormalizeForMatching("  Don’t   Stop — The Remix "));
    }

    [TestMethod]
    public void NormalizeForMatching_KeepsDistinctArtistNamesDistinct()
    {
        Assert.NotEqual(
            FilenameHeuristics.NormalizeForMatching("Bjork"),
            FilenameHeuristics.NormalizeForMatching("Björk"));
    }
}
