using System.Net;
using System.Text.Json;
using WhipRadio.Core.Abstractions;
using WhipRadio.Infrastructure.Tts;

namespace WhipRadio.Infrastructure.Tests;

public class HttpTtsEngineTests
{
    private static readonly TtsVoiceOptions DefaultVoice = new("af_heart", "en", 1.0);

    [Fact]
    public async Task SynthesizeAsync_SendsContractBody_AndReadsDurationHeader()
    {
        var wav = WavTestData.Pcm(dataBytes: 88200); // exactly 1 s at 44.1 kHz mono 16-bit
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
            response.Headers.Add(HttpTtsEngine.DurationHeader, "2.5");
            return response;
        });
        var engine = new HttpTtsEngine(handler.CreateClient());

        var result = await engine.SynthesizeAsync("Hello [pause:300ms] world", DefaultVoice, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("Hello [pause:300ms] world", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("af_heart", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("en", body.RootElement.GetProperty("language").GetString());
        Assert.Equal(1.0, body.RootElement.GetProperty("rate").GetDouble());

        Assert.Equal(wav, result.WavData);
        Assert.Equal(2.5, result.DurationSeconds); // header wins over WAV inspection
    }

    [Fact]
    public async Task SynthesizeAsync_WithoutHeader_ComputesDurationFromWavHeader()
    {
        var wav = WavTestData.Pcm(dataBytes: 44100); // 0.5 s at 44.1 kHz mono 16-bit
        var handler = FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new ByteArrayContent(wav));
        var engine = new HttpTtsEngine(handler.CreateClient());

        var result = await engine.SynthesizeAsync("Hi", DefaultVoice, CancellationToken.None);

        Assert.Equal(0.5, result.DurationSeconds, precision: 6);
    }

    [Fact]
    public async Task GetVoicesAsync_ParsesVoiceList()
    {
        var json = """[{"id":"af_heart","language":"en","gender":"f"},{"id":"bm_george","language":"en","gender":"m"}]""";
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        var engine = new HttpTtsEngine(handler.CreateClient());

        var voices = await engine.GetVoicesAsync(CancellationToken.None);

        Assert.Equal(2, voices.Count);
        Assert.Equal(new TtsVoice("af_heart", "en", "f"), voices[0]);
    }
}
