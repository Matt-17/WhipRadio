using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Configuration;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class JingleProductionServiceTests
{
    [TestMethod]
    public async Task GenerateAsync_UsesBrandingAndStoresJingleFile()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var dataRoot = Path.Combine(Path.GetTempPath(), "whipradio-jingle-tests", Guid.NewGuid().ToString("N"));
        var generator = new CapturingMusicGenerator(WavTestData.Pcm(44100));
        var service = new JingleProductionService(
            fixture,
            generator,
            Options.Create(new RadioOptions { DataRoot = dataRoot }),
            new FixedTimeProvider(new DateTime(2026, 6, 17, 18, 0, 0, DateTimeKind.Utc)),
            NullLogger<JingleProductionService>.Instance);

        await using (var db = fixture.CreateDbContext())
        {
            db.StationSettings.Add(new StationSettings
            {
                Id = StationSettings.SingletonId,
                StationName = "Night Lab FM",
                StationSlogan = "Made after dark.",
                StationMission = "Keep original AI radio moving.",
                DefaultLanguage = "de",
            });
            await db.SaveChangesAsync();
        }

        var jingle = await service.GenerateAsync(
            new CreateJingleDto("Top hour", "tight analog drums", 9),
            CancellationToken.None);

        Assert.Equal("Top hour", jingle.Label);
        Assert.Equal(MusicBackends.AceStep, jingle.Backend);
        Assert.True(File.Exists(Path.Combine(dataRoot, jingle.FilePath)));
        Assert.Contains("Night Lab FM", generator.LastRequest?.Prompt);
        Assert.Contains("Made after dark.", generator.LastRequest?.Prompt);
        Assert.DoesNotContain("Keep original AI radio moving.", generator.LastRequest?.Prompt);
        Assert.Contains("Sung station ID", generator.LastRequest?.Prompt);
        Assert.DoesNotContain("no vocals", generator.LastRequest?.Prompt);
        Assert.True((generator.LastRequest?.Prompt.Length ?? int.MaxValue) <= 220);
        Assert.True(generator.LastRequest?.WantVocals ?? false);
        Assert.Equal(LyricsMode.Provided, generator.LastRequest?.LyricsMode);
        Assert.Equal("Night Lab FM\nMade after dark.", generator.LastRequest?.Lyrics);
        Assert.Equal(MusicBackends.AceStep, generator.LastRequest?.Provider);
        Assert.False(generator.LastRequest?.AllowProviderFallback ?? true);
        Assert.Equal("de", generator.LastRequest?.Language);
        Assert.InRange(generator.LastRequest?.DurationSeconds ?? 0, 5, 20);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.Jingles.CountAsync());

        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class CapturingMusicGenerator(byte[] wav) : IMusicGenerator
    {
        public MusicRequest? LastRequest { get; private set; }

        public Task<MusicResult> GenerateAsync(MusicRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new MusicResult(wav, MusicBackends.AceStep, "test-model", "123", "task-1"));
        }

        public Task<bool> IsBackendAvailableAsync(string backend, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (var db = new RadioDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new DbFixture(connection, options);
        }

        public RadioDbContext CreateDbContext() => new(options);

        public Task<RadioDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
