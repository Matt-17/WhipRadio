namespace WhipRadio.Core.Prompting;

public sealed record ChatReply(
    string Prose,
    IReadOnlyList<CharacterToolCall> Actions,
    IReadOnlyList<string> Errors);

public interface IChatReplyParser
{
    ChatReply Parse(string raw, IReadOnlyList<CharacterToolDefinition> allowedTools);
}
