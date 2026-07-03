namespace WhipRadio.Core.Configuration;

/// <summary>
/// Single source of truth for the local default endpoints of external services.
/// Configuration (Llm:Endpoint, Analysis:Endpoint, Stream:PublicUrl, studio rows
/// in the database) always wins; these are only the fallbacks for a bare local
/// setup and the pre-filled values in the studio forms.
/// </summary>
public static class ServiceEndpointDefaults
{
    /// <summary>Ollama / writer room.</summary>
    public const string WriterRoom = "http://localhost:11434";

    /// <summary>Music recording studio (ACE-Step / MusicGen sidecar).</summary>
    public const string RecordingStudio = "http://localhost:8101";

    /// <summary>Loopback alias of <see cref="RecordingStudio"/> kept for legacy seeded rows.</summary>
    public const string RecordingStudioLoopback = "http://127.0.0.1:8101";

    /// <summary>Voice booth (TTS sidecar).</summary>
    public const string VoiceBooth = "http://localhost:8201";

    /// <summary>Audio analysis sidecar.</summary>
    public const string Analysis = "http://localhost:8301";

    /// <summary>Public Icecast mount as heard by listeners.</summary>
    public const string PublicStream = "http://localhost:8000/radio.mp3";
}
