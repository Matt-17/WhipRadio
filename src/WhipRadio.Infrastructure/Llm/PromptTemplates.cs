using System.Collections.Concurrent;
using System.Reflection;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>Loads prompt templates from embedded resources and fills {Placeholder} tokens.</summary>
public static class PromptTemplates
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Render(string templateName, IReadOnlyDictionary<string, string> values)
    {
        var template = Cache.GetOrAdd(templateName, Load);
        foreach (var (key, value) in values)
        {
            template = template.Replace($"{{{key}}}", value);
        }

        return template.Trim();
    }

    private static string Load(string templateName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = $".{templateName}.txt";
        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Prompts.", StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.Ordinal))
            .ToList();
        var resourceName = matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Prompt template not found: {templateName}"),
            _ => throw new InvalidOperationException(
                $"Prompt template name is ambiguous: {templateName} ({string.Join(", ", matches)})"),
        };

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Prompt template not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
