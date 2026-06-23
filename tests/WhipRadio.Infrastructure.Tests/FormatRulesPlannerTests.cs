using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class FormatRulesPlannerTests
{
    [TestMethod]
    public void ParseRules_StandardRotationJson_PreservesFields()
    {
        var json = """
        {
          "mode": "StandardRotation",
          "featuredArtistId": null,
          "maxArtistPlaysPerHour": 2,
          "artistLookbackTracks": 8,
          "subgenreRotation": true,
          "preferHostGenres": true,
          "theme": null
        }
        """;

        var rules = FormatRulesPlanner.ParseRules(json, []);

        Assert.Equal(SelectionMode.StandardRotation, rules.Mode);
        Assert.Null(rules.FeaturedArtistId);
        Assert.Equal(2, rules.MaxArtistPlaysPerHour);
        Assert.Equal(8, rules.ArtistLookbackTracks);
        Assert.True(rules.SubgenreRotation);
        Assert.True(rules.PreferHostGenres);
        Assert.Null(rules.Theme);
    }

    [TestMethod]
    public void ParseRules_SingleArtistFeature_ResolvesFeaturedArtistFromCatalog()
    {
        var artistId = Guid.NewGuid();
        var catalog = new[] { new ArtistCatalogEntry(artistId, "Glass Harbor", "synth pop", "night drive") };
        var json = $$"""{"mode":"SingleArtistFeature","featuredArtistId":"{{artistId}}"}""";

        var rules = FormatRulesPlanner.ParseRules(json, catalog);

        Assert.Equal(SelectionMode.SingleArtistFeature, rules.Mode);
        Assert.Equal(artistId, rules.FeaturedArtistId);
    }

    [TestMethod]
    public void ParseRules_ArtistFeatureWithoutMatchingArtist_FallsBackToStandardRotation()
    {
        var catalog = new[] { new ArtistCatalogEntry(Guid.NewGuid(), "Glass Harbor", "synth pop", "night drive") };
        var json = """{"mode":"SingleArtistFeature","featuredArtistId":"00000000-0000-0000-0000-000000000000"}""";

        var rules = FormatRulesPlanner.ParseRules(json, catalog);

        Assert.Equal(SelectionMode.StandardRotation, rules.Mode);
        Assert.Null(rules.FeaturedArtistId);
    }

    [TestMethod]
    public void ParseRules_ThemeBlock_CapturesThemeKeyword()
    {
        var json = """{"mode":"ThemeBlock","theme":"midnight drive"}""";

        var rules = FormatRulesPlanner.ParseRules(json, []);

        Assert.Equal(SelectionMode.ThemeBlock, rules.Mode);
        Assert.Equal("midnight drive", rules.Theme);
    }

    [TestMethod]
    public void ParseRules_MalformedJson_ReturnsDefault()
    {
        var rules = FormatRulesPlanner.ParseRules("not json at all", []);
        Assert.Equal(SelectionMode.StandardRotation, rules.Mode);
    }

    [TestMethod]
    public void ParseRules_EmptyJson_ReturnsDefault()
    {
        var rules = FormatRulesPlanner.ParseRules("{}", []);
        Assert.Equal(SelectionMode.StandardRotation, rules.Mode);
        Assert.Equal(8, rules.ArtistLookbackTracks);
    }

    [TestMethod]
    public void ParseRules_FeaturedArtistByName_MatchesCatalogEntry()
    {
        var artistId = Guid.NewGuid();
        var catalog = new[] { new ArtistCatalogEntry(artistId, "Glass Harbor", "synth pop", "night drive") };
        var json = """{"mode":"SpotlightArtist","featuredArtistId":"Glass Harbor"}""";

        var rules = FormatRulesPlanner.ParseRules(json, catalog);

        Assert.Equal(SelectionMode.SpotlightArtist, rules.Mode);
        Assert.Equal(artistId, rules.FeaturedArtistId);
    }
}
