using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class PromptTemplatesTests
{
    [TestMethod]
    public void Render_FindsTemplatesInNestedPromptFolders()
    {
        var prompt = PromptTemplates.Render("SongPlanner", new Dictionary<string, string>
        {
            ["ArtistName"] = "Tidal Static Parade",
            ["Genre"] = "indie rock",
            ["Subgenre"] = "surf rock",
            ["ArtistStyle"] = "reverb guitars and tape-warbled production",
            ["ArtistBiography"] = "A coastal project obsessed with travel ephemera.",
            ["SongHistory"] = "(no released songs yet)",
            ["AvoidTitles"] = "(none yet)",
            ["ForbiddenWords"] = "ghost, neon",
            ["MinDurationSeconds"] = "150",
            ["MaxDurationSeconds"] = "480",
            ["DefaultLanguage"] = "en",
            ["VocalCapability"] = "Vocals are available.",
        });

        Assert.Contains("Tidal Static Parade", prompt);
        Assert.Contains("Optional only when Vocals", prompt);
        Assert.DoesNotContain("{ArtistName}", prompt);
    }

    [TestMethod]
    public void Render_NewsPrompt_UsesFactualAnchorRules()
    {
        var prompt = PromptTemplates.Render("ScriptWriter.News", new Dictionary<string, string>
        {
            ["NewsFacts"] = "Bulletin time: 2026-06-20 18:00 local.\n\nSource: Test Wire\nTitle: Markets move",
        });

        Assert.Contains("Do not say the station slogan", prompt);
        Assert.Contains("Do not frame this like a DJ break", prompt);
        Assert.Contains("Do not introduce yourself", prompt);
        Assert.Contains("Anchor the lead to the bulletin time", prompt);
        Assert.Contains("Vary the opening slightly", prompt);
        Assert.Contains("Avoid geography-tour transitions", prompt);
        Assert.DoesNotContain("{NewsFacts}", prompt);
    }

    [TestMethod]
    public void Render_WeatherPrompt_UsesSpecialistForecastRules()
    {
        var prompt = PromptTemplates.Render("ScriptWriter.Weather", new Dictionary<string, string>
        {
            ["WeatherFacts"] = "Location: Dresden, Germany.\nCurrently 14 C, light rain.",
        });

        Assert.Contains("concise specialist forecast", prompt);
        Assert.Contains("weather desk", prompt);
        Assert.Contains("Treat the forecast location in the facts as the station's home city", prompt);
        Assert.Contains("here in Dresden", prompt);
        Assert.Contains("Do not say \"for Dresden, Germany\"", prompt);
        Assert.Contains("Do not introduce yourself", prompt);
        Assert.Contains("weather airing time", prompt);
        Assert.Contains("next-hours-after-airing-time", prompt);
        Assert.DoesNotContain("{WeatherFacts}", prompt);
    }
}
