using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WhipRadio.Core.Speech;

namespace WhipRadio.Core.Json;

/// <summary>
/// Single source of truth for schema-constrained LLM JSON. A typed record describes an
/// output; <see cref="SchemaFor{T}"/> generates the JSON Schema we hand to the model's
/// structured-output channel (Ollama <c>format</c> / OpenAI <c>response_format</c>),
/// and <see cref="Parse{T}"/> deserializes the reply back into that same record. Schema
/// and parser therefore can never drift, and required fields are enforced for free.
/// </summary>
public static class StructuredJson
{
    /// <summary>Shared options: camelCase on the wire, tolerant on read.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Required for schema export on a custom options instance (the default static
        // instance ships a resolver; ours must declare one before it is frozen).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly ConcurrentDictionary<Type, JsonNode> SchemaCache = new();

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        // Decoders such as Ollama's reject schemas whose root permits null; strip the
        // "null" that the exporter adds for every reference type, at every level.
        TransformSchemaNode = static (_, node) => NormalizeSchemaNode(node),
    };

    /// <summary>Generates (and caches) the JSON Schema for an output record.</summary>
    public static JsonNode SchemaFor<T>()
        => SchemaCache.GetOrAdd(typeof(T), static type => Options.GetJsonSchemaAsNode(type, ExporterOptions));

    /// <summary>
    /// Parses an LLM reply into <typeparamref name="T"/>. Strips a stray code fence
    /// first (structured output makes this rare, but cheap insurance). Returns a failure
    /// message instead of throwing when the JSON is invalid or a required field is
    /// missing.
    /// </summary>
    public static StructuredJsonResult<T> Parse<T>(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return StructuredJsonResult<T>.Fail("The model returned an empty response.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(StripCodeFence(raw), Options);
            return value is null
                ? StructuredJsonResult<T>.Fail("The model returned a null JSON value.")
                : StructuredJsonResult<T>.Ok(value);
        }
        catch (JsonException ex)
        {
            return StructuredJsonResult<T>.Fail($"The model did not return valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the text from a <see cref="TextDto"/> envelope (<c>{"text":"…"}</c>),
    /// falling back to the fence-stripped raw reply if the model ignored the envelope.
    /// Use with <c>SchemaFor&lt;TextDto&gt;()</c> on the request for free-text outputs.
    /// </summary>
    public static string ParseTextOrRaw(string raw)
    {
        var result = Parse<TextDto>(raw);
        return result.IsValid ? result.Value!.Text : StripCodeFence(raw);
    }

    /// <summary>
    /// Removes a leading <c>```</c>/<c>```json</c> fence and its closing counterpart.
    /// Shared by every structured-output consumer so the logic lives in one place.
    /// </summary>
    public static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }

    private static JsonNode NormalizeSchemaNode(JsonNode node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("type", out var type) || type is not JsonArray array)
        {
            return node;
        }

        var kept = array
            .Where(item => item is not null && item.GetValue<string>() != "null")
            .Select(item => item!.GetValue<string>())
            .ToArray();

        if (kept.Contains("string", StringComparer.Ordinal)
            && (kept.Contains("number", StringComparer.Ordinal) || kept.Contains("integer", StringComparer.Ordinal)))
        {
            kept = kept.Where(value => value != "string").ToArray();
            obj.Remove("pattern");
        }

        obj["type"] = kept.Length == 1 ? kept[0] : new JsonArray(kept.Select(value => (JsonNode)value!).ToArray());
        return obj;
    }
}

public sealed record StructuredJsonResult<T>(bool IsValid, T? Value, string? Error)
{
    public static StructuredJsonResult<T> Ok(T value) => new(true, value, null);

    public static StructuredJsonResult<T> Fail(string error) => new(false, default, error);
}
