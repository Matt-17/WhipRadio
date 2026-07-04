using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class GuestProfileWriterTests
{
    private sealed class CapturingLlm(string reply) : ITextGenerationService
    {
        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            UserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

    private static StationSettings Settings() => new()
    {
        Id = StationSettings.SingletonId,
        StationName = "WhipRadio",
        StationSlogan = "No maps after midnight.",
        DefaultLanguage = "en",
    };

    [TestMethod]
    public async Task DesignGuestAsync_ParsesFullProfile()
    {
        var llm = new CapturingLlm("""
{
  "name": "Ivy Sparks",
  "expertise": "urban beekeeper",
  "gender": "Female",
  "age": 47,
  "interests": "rooftop hives, native wildflowers, city zoning fights",
  "personality": "Enthusiastic, precise, a little combative about pesticides.",
  "biography": "Ivy keeps forty hives on downtown rooftops and sells honey at the night market.",
  "deepBackground": "She started with two hives on a parking garage after leaving a lab job. She argues that cities beat farmland for bee health and has the data to back it up. When challenged she gets faster, not louder.",
  "voiceCreationPrompt": "Bright mid-range female voice, quick tempo, city accent, close mic."
}
""");
        var writer = new GuestProfileWriter(llm);

        var plan = await writer.DesignGuestAsync(
            "a beekeeper for the night show",
            Settings(),
            ["Nova Quinn"],
            CancellationToken.None);

        Assert.Equal("Ivy Sparks", plan.Name);
        Assert.Equal("urban beekeeper", plan.Expertise);
        Assert.Equal("female", plan.Gender);
        Assert.Equal(47, plan.Age);
        Assert.Contains("rooftop hives", plan.Interests);
        Assert.Contains("combative", plan.Personality);
        Assert.Contains("forty hives", plan.Biography);
        Assert.Contains("parking garage", plan.DeepBackground);
        Assert.Contains("Bright mid-range", plan.VoiceCreationPrompt);
        Assert.Equal("a beekeeper for the night show", plan.Hint);
        Assert.False(string.IsNullOrWhiteSpace(plan.GenerationPrompt));

        Assert.Contains("a beekeeper for the night show", llm.UserPrompt);
        Assert.Contains("WhipRadio", llm.UserPrompt);
        Assert.Contains("No maps after midnight.", llm.UserPrompt);
        Assert.Contains("Nova Quinn", llm.UserPrompt);
    }

    [TestMethod]
    public async Task DesignGuestAsync_NormalizesUnknownGenderToEmpty()
    {
        var llm = new CapturingLlm("""
{
  "name": "Rex Halloway",
  "expertise": "storm chaser",
  "gender": "prefer not to say",
  "interests": "supercells, radio scanners",
  "personality": "Laconic.",
  "biography": "Chases weather across three states.",
  "deepBackground": "Grew up next to a tornado siren and never got over it.",
  "voiceCreationPrompt": "Low, dry, unhurried voice."
}
""");
        var writer = new GuestProfileWriter(llm);

        var plan = await writer.DesignGuestAsync(null, Settings(), [], CancellationToken.None);

        Assert.Equal("", plan.Gender);
        Assert.Null(plan.Age);
    }

    [TestMethod]
    public async Task DesignGuestAsync_RejectsNonJsonReply()
    {
        var llm = new CapturingLlm("Name(Ivy Sparks) Expertise(bees)");
        var writer = new GuestProfileWriter(llm);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.DesignGuestAsync("bees", Settings(), [], CancellationToken.None));
    }
}
