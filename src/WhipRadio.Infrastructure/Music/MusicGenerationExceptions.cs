namespace WhipRadio.Infrastructure.Music;

/// <summary>Thrown when the requested music backend is not available.</summary>
public class MusicBackendUnavailableException(string backend)
    : Exception($"Music backend '{backend}' is unavailable.")
{
    public string Backend { get; } = backend;
}

public class MusicProviderValidationException(string message) : ArgumentException(message);

public class MusicGenerationFailedException(string backend, string message)
    : Exception($"Music backend '{backend}' failed: {message}")
{
    public string Backend { get; } = backend;
}
