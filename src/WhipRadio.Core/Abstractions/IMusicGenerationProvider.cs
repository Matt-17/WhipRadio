namespace WhipRadio.Core.Abstractions;

public interface IMusicGenerationProvider
{
    string Id { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken cancellationToken);
}
