using System.Text.Json.Serialization;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Schema-constrained envelope for spoken copy. The script-writer and voice-director
/// models return <c>{"script":"…natural spoken radio copy…"}</c>; the single required
/// field keeps the words meant for the TTS cleanly separated from any model chatter.
/// </summary>
public sealed record ScriptDto([property: JsonRequired] string Script);

/// <summary>
/// Generic single-field text envelope (<c>{"text":"…"}</c>) for non-spoken free-text
/// outputs (bios, titles, lyrics, translations, memory summaries). Keeps every LLM
/// reply as schema-constrained JSON without inventing a bespoke DTO per call site.
/// </summary>
public sealed record TextDto([property: JsonRequired] string Text);
