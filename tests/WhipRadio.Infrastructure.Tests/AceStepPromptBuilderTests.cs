using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Music;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class AceStepPromptBuilderTests
{
    private readonly AceStepPromptBuilder builder = new();

    [TestMethod]
    public void InstrumentalRequestsContainNoVocalInstruction()
    {
        var prompt = builder.Build(new MusicRequest("atmospheric indie rock", "rock", false, null, 120)
        {
            LyricsMode = LyricsMode.Instrumental,
            VocalGender = VocalGender.Female,
            VocalStyle = "warm lower register",
            Language = "English",
        });

        Assert.DoesNotContain("Lead vocals", prompt);
        Assert.Contains("Language: English.", prompt);
        Assert.Contains("complete song structure", prompt);
        Assert.Contains("avoid an abrupt ending", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void MaleVocalGuidanceAppearsWhenRequested()
    {
        var prompt = builder.Build(new MusicRequest("soul pop", "pop", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            VocalGender = VocalGender.Male,
            VocalStyle = "clear tenor",
            Language = "English",
        });

        Assert.Contains("male lead vocals", prompt);
        Assert.Contains("clear tenor", prompt);
        Assert.Contains("Language: English.", prompt);
        Assert.Contains("Vocal continuity:", prompt);
        Assert.Contains("preserving timbre", prompt);
    }

    [TestMethod]
    public void FemaleVocalGuidanceAppearsWhenRequested()
    {
        var prompt = builder.Build(new MusicRequest("dream pop", "pop", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            VocalGender = VocalGender.Female,
        });

        Assert.Contains("female lead vocals", prompt);
    }

    [TestMethod]
    public void ArtistBackstoryIsIncludedAndLengthLimited()
    {
        var longBackstory = string.Join(" ", Enumerable.Repeat("fictional nocturnal band with restrained verses", 20));

        var prompt = builder.Build(new MusicRequest("indie rock", "rock", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Amber Meridian",
            ArtistBackstory = longBackstory,
        });

        Assert.Contains("Amber Meridian", prompt);
        Assert.Contains("fictional artist", prompt);
        Assert.True(prompt.Length < longBackstory.Length + 700);
    }

    [TestMethod]
    public void SongPlanContextIsIncluded()
    {
        var prompt = builder.Build(new MusicRequest("krautrock with motorik drums", "rock", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            ArtistName = "Die Kurvenlichter",
            ArtistBackstory = "A German band formed after night shifts in Essen.",
            ArtistStyleDescription = "Motorik drums and bright analog synths.",
            SongTitle = "Morgens am Gleis",
            SongStory = "The band wrote it after a delayed train turned into a sunrise rehearsal.",
            ArtistSongHistory = "- Alte Funken (vocal, de, target 180s, likes 4, dislikes 1).",
            Language = "de",
        });

        Assert.Contains("Song title: Morgens am Gleis.", prompt);
        Assert.Contains("Song origin story:", prompt);
        Assert.Contains("delayed train", prompt);
        Assert.Contains("Artist catalog context:", prompt);
        Assert.Contains("Alte Funken", prompt);
        Assert.Contains("Language: de.", prompt);
    }

    [TestMethod]
    public void SuppliedTempoKeyAndMeterAreIncluded()
    {
        var prompt = builder.Build(new MusicRequest("jazz ballad", "jazz", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            Bpm = 118,
            KeyScale = "A minor",
            TimeSignature = "4/4",
        });

        Assert.Contains("Tempo: approximately 118 BPM.", prompt);
        Assert.Contains("Key: A minor.", prompt);
        Assert.Contains("Time signature: 4/4.", prompt);
    }

    [TestMethod]
    public void VocalReferenceLabelAppearsAsIdentityAnchor()
    {
        var prompt = builder.Build(new MusicRequest("synth pop", "pop", true, "words", 180)
        {
            LyricsMode = LyricsMode.Provided,
            ReferenceAudioLabel = "First Signal",
        });

        Assert.Contains("uploaded reference audio", prompt);
        Assert.Contains("First Signal", prompt);
    }
}
