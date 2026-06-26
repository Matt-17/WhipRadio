using System.Text.Json.Serialization;

namespace WhipRadio.Core.Speech;

/// <summary>
/// Schema-constrained envelope for a single combined script/delivery run. One LLM call
/// returns the clean transcript (<c>script</c>), the same words shaped for speech with the
/// host's speech markers (<c>delivery</c>), and a small per-delivery voice hint.
/// Replaces the old two-run ScriptWriter + VoiceDirector envelopes.
/// </summary>
public sealed record SpokenDeliveryDto(
    [property: JsonRequired] string Script,
    [property: JsonRequired] string Delivery,
    VoiceDirectionDto? Voice = null);

/// <summary>Per-delivery TTS direction. <c>stylePrompt</c> stays code-derived from the host
/// persona; the model only shapes the moment-specific delivery hint and rate.</summary>
public sealed record VoiceDirectionDto(
    string? DeliveryPrompt = null,
    double? Rate = null);
