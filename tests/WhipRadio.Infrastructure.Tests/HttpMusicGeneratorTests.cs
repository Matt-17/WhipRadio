using System.Net;
using System.Text.Json;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Music;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class HttpMusicGeneratorTests
{
    [TestMethod]
    public async Task GenerateAsync_InstrumentalRequest_TargetsMusicGen()
    {
        var wav = WavTestData.Pcm(dataBytes: 1024);
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new ByteArrayContent(wav));
        var generator = new HttpMusicGenerator(handler.CreateClient());

        var result = await generator.GenerateAsync(
            new MusicRequest("energetic indie rock, driving drums", "indie rock", WantVocals: false, Lyrics: null, DurationSeconds: 90),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("energetic indie rock, driving drums", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("musicgen", body.RootElement.GetProperty("backend").GetString());
        Assert.Equal(90, body.RootElement.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lyrics").ValueKind);

        Assert.Equal(wav, result.WavData);
        Assert.Equal("musicgen", result.BackendUsed);
    }

    [TestMethod]
    public async Task GenerateAsync_VocalRequest_StillTargetsMusicGenCompatibilityClient()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new ByteArrayContent([1]));
        var generator = new HttpMusicGenerator(handler.CreateClient());

        await generator.GenerateAsync(
            new MusicRequest("dream pop ballad", "pop", WantVocals: true, Lyrics: "la la la", DurationSeconds: 60),
            CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("musicgen", body.RootElement.GetProperty("backend").GetString());
        Assert.Equal("la la la", body.RootElement.GetProperty("lyrics").GetString());
    }

    [TestMethod]
    public async Task GenerateAsync_503_ThrowsBackendUnavailable()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.ServiceUnavailable, new StringContent(""));
        var generator = new HttpMusicGenerator(handler.CreateClient());

        var ex = await Assert.ThrowsAsync<MusicBackendUnavailableException>(
            () => generator.GenerateAsync(
                new MusicRequest("p", "g", WantVocals: true, Lyrics: "l", DurationSeconds: 30), CancellationToken.None));

        Assert.Equal("musicgen", ex.Backend);
    }

    [TestMethod]
    [DataRow("musicgen", true)]
    public async Task IsBackendAvailableAsync_ParsesHealthResponse(string backend, bool expected)
    {
        var json = """{"status":"ok","backends":{"musicgen":true,"ace-step":false}}""";
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        var generator = new HttpMusicGenerator(handler.CreateClient());

        Assert.Equal(expected, await generator.IsBackendAvailableAsync(backend, CancellationToken.None));
    }

    [TestMethod]
    public async Task IsBackendAvailableAsync_SidecarDown_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var generator = new HttpMusicGenerator(handler.CreateClient());

        Assert.False(await generator.IsBackendAvailableAsync("musicgen", CancellationToken.None));
    }
}
