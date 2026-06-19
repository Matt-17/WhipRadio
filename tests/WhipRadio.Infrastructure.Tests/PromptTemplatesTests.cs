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
}
