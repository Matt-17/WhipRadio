using System.Text.Json.Serialization;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Generic single-field text envelope (<c>{"text":"…"}</c>) for non-spoken free-text
/// outputs (bios, titles, lyrics, translations, memory summaries). Keeps every LLM
/// reply as schema-constrained JSON without inventing a bespoke DTO per call site.
/// </summary>
public sealed record TextDto([property: JsonRequired] string Text);
