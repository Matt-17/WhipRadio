namespace WhipRadio.Web.Components.Chat;

/// <summary>Time helpers shared by the chat rail and the message list.</summary>
internal static class ChatFormat
{
    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
