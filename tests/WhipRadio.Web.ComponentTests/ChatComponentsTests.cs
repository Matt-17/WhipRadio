using WhipRadio.Core.Api;
using WhipRadio.Web.Components.Chat;

namespace WhipRadio.Web.ComponentTests;

/// <summary>Direct tests for the components extracted from the Chat page.</summary>
[TestClass]
public class ChatComponentsTests : BunitContext
{
    private static ChatChannelDto Channel(string name, string kind = "HostDm", int unread = 0) => new(
        Guid.NewGuid(), kind, name, ModeratorId: 1, PhotoUrl: null,
        LastMessageAtUtc: DateTime.UtcNow, LastMessagePreview: "last words", UnreadCount: unread, IsArchived: false);

    private static ChatMessageDto Message(string text, string senderKind = "Host", params ChatActionDto[] actions) => new(
        Guid.NewGuid(), Guid.NewGuid(), senderKind, SenderModeratorId: 1, SenderName: "Maya",
        SenderPhotoUrl: null, Text: text, Actions: actions, CreatedAtUtc: DateTime.UtcNow,
        CorrelationId: null, HopCount: 0);

    [TestMethod]
    public void ChannelRail_ShowsChannels_UnreadBadges_AndEmptyState()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var rail = Render<ChannelRail>(parameters => parameters
            .Add(p => p.Channels, [Channel("Maya"), Channel("Writers", kind: "Group", unread: 120)]));

        Assert.Contains("Maya", rail.Markup);
        Assert.Contains("Writers", rail.Markup);
        Assert.Contains("99+", rail.Markup);
        Assert.Contains("last words", rail.Markup);

        var empty = Render<ChannelRail>(parameters => parameters
            .Add(p => p.Channels, new List<ChatChannelDto>()));
        Assert.Contains("No chat channels yet.", empty.Markup);
    }

    [TestMethod]
    public void MessageList_RendersDaySeparator_Text_AndVisibleActionsOnly()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());
        var visible = new ChatActionDto("PlayTrack", new Dictionary<string, string> { ["track"] = "Midnight" }, "Succeeded", null);
        var hidden = new ChatActionDto("FireHost", new Dictionary<string, string>(), "Failed", "nope");

        var list = Render<MessageList>(parameters => parameters
            .Add(p => p.Messages, [Message("hello boss", actions: [visible, hidden])]));

        Assert.Contains("hello boss", list.Markup);
        Assert.Contains("PlayTrack - track: Midnight", list.Markup);
        Assert.DoesNotContain("FireHost", list.Markup);
        Assert.Contains("chat-day", list.Markup);
    }

    [TestMethod]
    public void ChatComposer_ShowsTheDisabledNote_WhenPostingIsNotAllowed()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var composer = Render<ChatComposer>(parameters => parameters
            .Add(p => p.CanPost, false)
            .Add(p => p.DisabledLabel, "agents only - you are reading their exchange"));

        Assert.Contains("agents only - you are reading their exchange", composer.Markup);
        Assert.True(composer.Find("textarea").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ChatComposer_EnablesSend_OnlyWithText()
    {
        this.RegisterConsoleServices(WebTestSupport.UnreachableOrchestrator());

        var emptyText = Render<ChatComposer>(parameters => parameters
            .Add(p => p.CanPost, true)
            .Add(p => p.Text, ""));
        Assert.True(emptyText.Find("button[type=submit]").HasAttribute("disabled"));

        var withText = Render<ChatComposer>(parameters => parameters
            .Add(p => p.CanPost, true)
            .Add(p => p.Text, "hi"));
        Assert.False(withText.Find("button[type=submit]").HasAttribute("disabled"));
    }
}
