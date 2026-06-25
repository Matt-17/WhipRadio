namespace WhipRadio.Core.Prompting;

public sealed record CharacterToolArgument(
    string Name,
    string Description,
    bool IsRequired = true,
    string JsonType = "string");

public sealed record CharacterToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<CharacterToolArgument> Arguments);

public sealed record CharacterToolExecutionResult(
    bool Handled,
    string? Message = null)
{
    public static CharacterToolExecutionResult NotHandled(string? message = null)
        => new(false, message);
}

public interface ICharacterTool
{
    CharacterToolDefinition Definition { get; }

    bool IsAvailable(PromptScope scope, CharacterRole role);

    ValueTask<CharacterToolExecutionResult> ExecuteAsync(CharacterToolCall call, CancellationToken ct)
        => ValueTask.FromResult(CharacterToolExecutionResult.NotHandled(
            $"Tool '{Definition.Name}' has no runtime handler yet."));
}

public interface ICharacterToolCatalog
{
    IReadOnlyList<CharacterToolDefinition> GetTools(PromptScope scope, CharacterRole role);

    ICharacterTool? GetTool(string name, PromptScope scope, CharacterRole role);
}

public interface IPromptContextBuilder
{
    Task<PromptContext> BuildAsync(PromptContextInput input, CancellationToken ct);
}
