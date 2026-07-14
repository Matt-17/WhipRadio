using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Audio;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ImportedAudioStagerTests
{
    [TestMethod]
    public async Task Stage_InRootWav_PassesThroughWithoutTempFile()
    {
        var dataRoot = TestRoot();
        try
        {
            var relative = Path.Combine("library", "tracks", "song.wav");
            var absolute = Path.Combine(dataRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllBytesAsync(absolute, ToneWav());

            using var staged = await CreateStager(dataRoot).StageAsync(Guid.NewGuid(), relative, CancellationToken.None);

            Assert.Equal(relative, staged.SidecarRelativePath);
            Assert.Null(staged.TempAbsolutePath);
        }
        finally
        {
            DeleteRoot(dataRoot);
        }
    }

    [TestMethod]
    public async Task Stage_ExternalWav_TranscodesIntoTheAnalysisCacheAndCleansUp()
    {
        var dataRoot = TestRoot();
        var externalFolder = TestRoot();
        try
        {
            Directory.CreateDirectory(externalFolder);
            var source = Path.Combine(externalFolder, "external.wav");
            await File.WriteAllBytesAsync(source, ToneWav());
            var itemId = Guid.NewGuid();

            string tempPath;
            using (var staged = await CreateStager(dataRoot).StageAsync(itemId, source, CancellationToken.None))
            {
                Assert.Equal($"cache/analysis/{itemId}.wav", staged.SidecarRelativePath);
                tempPath = staged.TempAbsolutePath!;
                Assert.True(File.Exists(tempPath), "staged WAV must exist inside the data root");
                var stagedAudio = WavFile.ParsePcm16Audio(await File.ReadAllBytesAsync(tempPath));
                Assert.True(stagedAudio.DurationSeconds > 0.4, "staged audio must carry the source content");
            }

            Assert.False(File.Exists(tempPath), "temp WAV must be deleted after analysis");
            Assert.True(File.Exists(source), "the external source file is never touched");
        }
        finally
        {
            DeleteRoot(dataRoot);
            DeleteRoot(externalFolder);
        }
    }

    private static ImportedAudioStager CreateStager(string dataRoot)
        => new(
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            Options.Create(new StreamOptions()),
            NullLogger<ImportedAudioStager>.Instance);

    private static byte[] ToneWav()
    {
        const int rate = 8000;
        var frames = rate / 2; // 0.5 s
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }

        return WavFile.WrapPcm16(pcm, rate, 1);
    }

    private static string TestRoot()
        => Path.Combine(Path.GetTempPath(), "whipradio-stager-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
