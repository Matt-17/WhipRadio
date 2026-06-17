using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Music;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class MusicGeneratorRouterTests
{
    [TestMethod]
    public async Task SelectsMusicGen()
    {
        var musicgen = new FakeProvider(MusicBackends.MusicGen);
        var ace = new FakeProvider(MusicBackends.AceStep);
        var router = Router([musicgen, ace], MusicBackends.MusicGen);

        var result = await router.GenerateAsync(new MusicRequest("p", "g", false, null, 30), CancellationToken.None);

        Assert.Equal(MusicBackends.MusicGen, result.BackendUsed);
        Assert.Equal(1, musicgen.GenerateCalls);
        Assert.Equal(0, ace.GenerateCalls);
    }

    [TestMethod]
    public async Task SelectsAceStep()
    {
        var musicgen = new FakeProvider(MusicBackends.MusicGen);
        var ace = new FakeProvider(MusicBackends.AceStep);
        var router = Router([musicgen, ace], MusicBackends.AceStep);

        var result = await router.GenerateAsync(new MusicRequest("p", "g", true, "l", 30), CancellationToken.None);

        Assert.Equal(MusicBackends.AceStep, result.BackendUsed);
        Assert.Equal(0, musicgen.GenerateCalls);
        Assert.Equal(1, ace.GenerateCalls);
    }

    [TestMethod]
    public async Task UnknownProviderProducesClearError()
    {
        var router = Router([new FakeProvider(MusicBackends.MusicGen)], "not-real");

        var ex = await Assert.ThrowsAsync<MusicProviderValidationException>(
            () => router.GenerateAsync(new MusicRequest("p", "g", false, null, 30), CancellationToken.None));

        Assert.Contains("Unknown music provider", ex.Message);
        Assert.Contains("musicgen", ex.Message);
        Assert.Contains("ace-step-1.5", ex.Message);
    }

    [TestMethod]
    public async Task RequestProviderOverridesStationDefault()
    {
        var musicgen = new FakeProvider(MusicBackends.MusicGen);
        var ace = new FakeProvider(MusicBackends.AceStep);
        var router = Router([musicgen, ace], MusicBackends.MusicGen);

        await router.GenerateAsync(
            new MusicRequest("p", "g", true, "l", 30) { Provider = "ace-step" },
            CancellationToken.None);

        Assert.Equal(0, musicgen.GenerateCalls);
        Assert.Equal(1, ace.GenerateCalls);
    }

    [TestMethod]
    public async Task StationDefaultIsUsedWhenRequestProviderIsAbsent()
    {
        var ace = new FakeProvider(MusicBackends.AceStep);
        var router = Router([new FakeProvider(MusicBackends.MusicGen), ace], MusicBackends.AceStep);

        await router.GenerateAsync(new MusicRequest("p", "g", true, "l", 30), CancellationToken.None);

        Assert.Equal(1, ace.GenerateCalls);
    }

    [TestMethod]
    public async Task UnavailableAceStepFallsBackOnlyWhenAllowed()
    {
        var musicgen = new FakeProvider(MusicBackends.MusicGen);
        var ace = new FakeProvider(MusicBackends.AceStep) { Available = false };
        var router = Router([musicgen, ace], MusicBackends.AceStep);

        var result = await router.GenerateAsync(
            new MusicRequest("p", "g", true, "l", 30) { AllowProviderFallback = true },
            CancellationToken.None);

        Assert.Equal(MusicBackends.MusicGen, result.BackendUsed);

        await Assert.ThrowsAsync<MusicBackendUnavailableException>(() => router.GenerateAsync(
            new MusicRequest("p", "g", true, "l", 30) { AllowProviderFallback = false },
            CancellationToken.None));
    }

    [TestMethod]
    public async Task NoFallbackHappensAfterAceStepTaskHasBeenCreated()
    {
        var musicgen = new FakeProvider(MusicBackends.MusicGen);
        var ace = new FakeProvider(MusicBackends.AceStep)
        {
            GenerateException = new MusicGenerationFailedException(MusicBackends.AceStep, "task failed"),
        };
        var router = Router([musicgen, ace], MusicBackends.AceStep);

        await Assert.ThrowsAsync<MusicGenerationFailedException>(() => router.GenerateAsync(
            new MusicRequest("p", "g", true, "l", 30) { AllowProviderFallback = true },
            CancellationToken.None));

        Assert.Equal(0, musicgen.GenerateCalls);
        Assert.Equal(1, ace.GenerateCalls);
    }

    private static MusicGenerator Router(IEnumerable<IMusicGenerationProvider> providers, string defaultProvider)
        => new(
            providers,
            _ => Task.FromResult<string?>(defaultProvider),
            NullLogger<MusicGenerator>.Instance);

    private sealed class FakeProvider(string id) : IMusicGenerationProvider
    {
        public string Id { get; } = id;

        public bool Available { get; init; } = true;

        public Exception? GenerateException { get; init; }

        public int GenerateCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            => Task.FromResult(Available);

        public Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken cancellationToken)
        {
            GenerateCalls++;
            if (GenerateException is not null)
            {
                throw GenerateException;
            }

            return Task.FromResult(new MusicResult([1, 2, 3], Id));
        }
    }
}
