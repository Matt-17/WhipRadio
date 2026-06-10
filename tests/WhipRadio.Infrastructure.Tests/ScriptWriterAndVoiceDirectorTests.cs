using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

public class ScriptWriterAndVoiceDirectorTests
{
    private sealed class CapturingLlm(string reply = "Generated copy.") : ITextGenerationService
    {
        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task ScriptWriter_SongIntro_FillsTrackPlaceholders()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);
        var track = new Track { Title = "Neon Llama", Genre = "indie rock", Style = "driving drums" };

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.SongIntro, "WhipRadio", "en", track), CancellationToken.None);

        Assert.Contains("WhipRadio", llm.SystemPrompt);
        Assert.Contains("language: en", llm.SystemPrompt);
        Assert.Contains("Neon Llama", llm.UserPrompt);
        Assert.Contains("indie rock", llm.UserPrompt);
        Assert.Contains("driving drums", llm.UserPrompt);
        Assert.DoesNotContain("{Title}", llm.UserPrompt);
    }

    [Fact]
    public async Task ScriptWriter_Weather_UsesFacts()
    {
        var llm = new CapturingLlm();
        var writer = new ScriptWriter(llm);

        await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Weather, "WhipRadio", "de", Facts: "Currently 14°C, light rain."),
            CancellationToken.None);

        Assert.Contains("Currently 14°C, light rain.", llm.UserPrompt);
        Assert.DoesNotContain("{WeatherFacts}", llm.UserPrompt);
    }

    [Fact]
    public async Task ScriptWriter_SanitizesLlmOutput()
    {
        var llm = new CapturingLlm("\"Sure, here is your intro: Up next, a banger!\"");
        var writer = new ScriptWriter(llm);

        var result = await writer.WriteAsync(
            new AnnouncementRequest(AnnouncementKind.Joke, "WhipRadio", "en"), CancellationToken.None);

        Assert.Equal("Up next, a banger!", result);
    }

    [Fact]
    public async Task VoiceDirector_InjectsPersonaAndPassesScript()
    {
        var llm = new CapturingLlm("Adapted [pause:300ms] text.");
        var director = new VoiceDirector(llm);
        var moderator = new Moderator
        {
            Name = "Lena",
            PersonaPrompt = "Quirlige Moderatorin mit viel Energie.",
            Style = "fast-energetic",
        };

        var result = await director.DirectAsync("Original script.", moderator, CancellationToken.None);

        Assert.Contains("Quirlige Moderatorin", llm.SystemPrompt);
        Assert.Contains("fast-energetic", llm.SystemPrompt);
        Assert.Contains("[pause:NNNms]", llm.SystemPrompt);
        Assert.Equal("Original script.", llm.UserPrompt);
        Assert.Equal("Adapted [pause:300ms] text.", result);
    }
}
