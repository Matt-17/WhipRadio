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
        var resourceName = $"WhipRadio.Infrastructure.Prompts.{templateName}.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Prompt template not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
