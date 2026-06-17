using WhipRadio.Core.Entities;

namespace WhipRadio.Core.Tests;

[TestClass]
public sealed class StationSettingsTests
{
    [TestMethod]
    public void DefaultSlogan_UsesOriginalWhipRadioLine()
    {
        var settings = new StationSettings();

        Assert.Equal("Llamas whipped the radio's mix.", settings.StationSlogan);
    }
}
