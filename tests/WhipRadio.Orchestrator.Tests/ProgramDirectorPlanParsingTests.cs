using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ProgramDirectorPlanParsingTests
{
    [TestMethod]
    public void ParsePlan_ValidJson_ReturnsBlocksWithReason()
    {
        var json = """
        {
          "blocks": [
            {"start":"00:00","end":"04:00","format":"Nightwave","genre":"lofi","subgenre":"ambient","host":"Lena"},
            {"start":"04:00","end":"08:00","format":"Early","genre":"lofi","host":"Lena"},
            {"start":"08:00","end":"12:00","format":"Morning","genre":"pop","subgenre":"synth pop","host":"Max"},
            {"start":"12:00","end":"16:00","format":"Midday","genre":"electronic","subgenre":"house","host":"Lena"},
            {"start":"16:00","end":"20:00","format":"Afternoon","genre":"indie rock","host":"Max"},
            {"start":"20:00","end":"00:00","format":"Evening","genre":"jazz","subgenre":"nu jazz","host":"Lena"}
          ],
          "reason": "balanced day"
        }
        """;

        var blocks = ProgramDirectorService.ParsePlan(json);

        Assert.NotNull(blocks);
        Assert.True(blocks!.Count >= 5);
        Assert.Equal(0, blocks[0].StartMinute);
        Assert.Equal("Nightwave", blocks[0].FormatName);
        Assert.Equal("lofi", blocks[0].Genre);
        Assert.Equal("ambient", blocks[0].Subgenre);
        Assert.Equal("balanced day", blocks[0].Reason);
    }

    [TestMethod]
    public void ParsePlan_NewHostSyntaxIsPreservedAsHostSpec()
    {
        var json = """
        {
          "blocks": [
            {"start":"00:00","end":"04:00","format":"A","genre":"lofi","host":"new:Nova|f|en|calm night host"},
            {"start":"04:00","end":"08:00","format":"B","genre":"lofi","host":"Lena"},
            {"start":"08:00","end":"12:00","format":"C","genre":"pop","host":"Lena"},
            {"start":"12:00","end":"16:00","format":"D","genre":"pop","host":"Lena"},
            {"start":"16:00","end":"20:00","format":"E","genre":"pop","host":"Lena"},
            {"start":"20:00","end":"00:00","format":"F","genre":"pop","host":"Lena"}
          ]
        }
        """;

        var blocks = ProgramDirectorService.ParsePlan(json);

        Assert.NotNull(blocks);
        Assert.Equal("new:Nova|f|en|calm night host", blocks![0].HostSpec);
    }

    [TestMethod]
    public void ParsePlan_InvalidJson_ReturnsNull()
    {
        Assert.Null(ProgramDirectorService.ParsePlan("this is not json"));
    }

    [TestMethod]
    public void ParsePlan_InsufficientCoverage_ReturnsNull()
    {
        var json = """
        {"blocks":[{"start":"00:00","end":"02:00","format":"A","genre":"lofi","host":"Lena"}]}
        """;

        Assert.Null(ProgramDirectorService.ParsePlan(json));
    }
}
