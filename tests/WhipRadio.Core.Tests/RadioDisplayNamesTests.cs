using WhipRadio.Core.Api;

namespace WhipRadio.Core.Tests;

[TestClass]
public class RadioDisplayNamesTests
{
    [TestMethod]
    public void AnnouncementTitle_ReturnsNewsOnlyForNewsKind()
    {
        Assert.Equal("News", RadioDisplayNames.AnnouncementTitle("News"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("SongIntro")]
    [DataRow("SongOutro")]
    [DataRow("Weather")]
    public void AnnouncementTitle_ReturnsAnnouncementForOtherKinds(string? kind)
    {
        Assert.Equal("Announcement", RadioDisplayNames.AnnouncementTitle(kind));
    }
}
