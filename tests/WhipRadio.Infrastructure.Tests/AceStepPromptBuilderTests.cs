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
        Assert.DoesNotContain("Language:", prompt);
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
        Assert.True(prompt.Length < longBackstory.Length + 300);
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
}
