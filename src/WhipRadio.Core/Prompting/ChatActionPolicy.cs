namespace WhipRadio.Core.Prompting;

public static class ChatActionPolicy
{
    public static bool IsInTurnLookup(CharacterToolCall call)
        => call.Name.Equals("SearchMusic", StringComparison.OrdinalIgnoreCase)
            || call.Name.Equals("StatusReport", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminalAdminReport(CharacterToolCall call)
        => call.Name.Equals("Message", StringComparison.OrdinalIgnoreCase)
            && call.Arguments.TryGetValue("characterId", out string? target)
            && IsAdminTarget(target);

    public static bool WouldEnqueueAgentTurn(CharacterToolCall call)
        => call.Name.Equals("Message", StringComparison.OrdinalIgnoreCase)
            && (!call.Arguments.TryGetValue("characterId", out string? target)
                || (!IsAdminTarget(target) && !IsUserTarget(target)));

    private static bool IsAdminTarget(string? value)
        => value is not null
            && (value.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                || value.Equals("User", StringComparison.OrdinalIgnoreCase));

    private static bool IsUserTarget(string? value)
        => value is not null && value.Equals("User", StringComparison.OrdinalIgnoreCase);
}
