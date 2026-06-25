using System.Text.Json.Serialization;
using WhipRadio.Core.Json;

namespace WhipRadio.Core.Tests;

[TestClass]
public class StructuredJsonTests
{
    private sealed record SampleDto(
        [property: JsonRequired] string Title,
        int Count = 0,
        string? Note = null);

    [TestMethod]
    public void Parse_ValidJson_ReturnsValue()
    {
        var result = StructuredJson.Parse<SampleDto>("""{"title":"Hello","count":3}""");

        Assert.True(result.IsValid);
        Assert.Equal("Hello", result.Value!.Title);
        Assert.Equal(3, result.Value.Count);
    }

    [TestMethod]
    public void Parse_FencedJson_ReturnsValue()
    {
        var result = StructuredJson.Parse<SampleDto>(
            """
            ```json
            {"title":"Fenced"}
            ```
            """);

        Assert.True(result.IsValid);
        Assert.Equal("Fenced", result.Value!.Title);
    }

    [TestMethod]
    public void Parse_MissingRequiredField_Fails()
    {
        var result = StructuredJson.Parse<SampleDto>("""{"count":3}""");

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [TestMethod]
    public void Parse_MalformedJson_Fails()
    {
        var result = StructuredJson.Parse<SampleDto>("not json at all");

        Assert.False(result.IsValid);
    }

    [TestMethod]
    public void SchemaFor_OmitsNullRootTypeAndMarksRequired()
    {
        var schema = StructuredJson.SchemaFor<SampleDto>();

        Assert.Equal("object", schema!["type"]!.GetValue<string>());

        var required = schema["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("title", required);
        // Defaulted/optional members must not be required.
        Assert.DoesNotContain("count", required);
        Assert.DoesNotContain("note", required);
    }
}
