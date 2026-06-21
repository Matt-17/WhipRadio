using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ArtistSocialFeedServiceTests
{
    [TestMethod]
    public async Task TrackReleasedPost_PersistsAfterTrackSaveAndIncludesContext()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var (artistId, trackId) = await SeedArtistWithTracksAsync(fixture);
        var llm = new CapturingLlm("Post(\"The new signal keeps the old one in its shadow.\")");
        var publisher = new CapturingPublisher();
        var service = new ArtistSocialFeedService(
            fixture,
            new MusicCopywriter(llm),
            publisher,
            NullLogger<ArtistSocialFeedService>.Instance);

        await service.TryCreateTrackReleasedPostAsync(artistId, trackId, CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        var post = await db.ArtistPosts.SingleAsync(p => p.TrackId == trackId);
        Assert.Equal(ArtistPostKind.TrackReleased, post.Kind);
        Assert.Equal("The new signal keeps the old one in its shadow.", post.Body);
        Assert.NotNull(publisher.LastPost);
        var pushed = publisher.LastPost!;
        Assert.Equal(post.Id, pushed.Id);
        Assert.Equal(trackId, pushed.TrackId);
        Assert.Equal("New Signal", pushed.TrackTitle);

        Assert.Contains("New Signal", llm.UserPrompt);
        Assert.Contains("Old Signal", llm.UserPrompt);
        Assert.Contains("First wire post", llm.UserPrompt);
        Assert.Contains("A reply to the first dock signal", llm.UserPrompt);
        Assert.Contains("song-publishing post", llm.UserPrompt);
        Assert.Contains("stored generation prompt", llm.UserPrompt);
    }

    [TestMethod]
    public async Task GetPostsAsync_ReturnsNewestFirstAndPages()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var artistId = await SeedArtistAsync(fixture);
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            db.ArtistPosts.AddRange(
                NewPost(artistId, "old", DateTime.UtcNow.AddMinutes(-3)),
                NewPost(artistId, "middle", DateTime.UtcNow.AddMinutes(-2)),
                NewPost(artistId, "new", DateTime.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var service = new ArtistSocialFeedService(
            fixture,
            new MusicCopywriter(new CapturingLlm("Skip(\"no-op\")")),
            new CapturingPublisher(),
            NullLogger<ArtistSocialFeedService>.Instance);

        var page = await service.GetPostsAsync(1, 2, CancellationToken.None);

        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal("new", page.Items[0].Body);
        Assert.Equal("middle", page.Items[1].Body);

        var second = await service.GetPostsAsync(2, 2, CancellationToken.None);
        Assert.Equal("old", second.Items.Single().Body);
    }

    private static ArtistPost NewPost(Guid artistId, string body, DateTime createdAtUtc)
        => new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Kind = ArtistPostKind.ArtistCreated,
            Body = body,
            CreatedAtUtc = createdAtUtc,
        };

    private static async Task<(Guid ArtistId, Guid TrackId)> SeedArtistWithTracksAsync(DbFixture fixture)
    {
        var artistId = await SeedArtistAsync(fixture);
        var oldTrackId = Guid.NewGuid();
        var newTrackId = Guid.NewGuid();
        await using RadioDbContext db = fixture.CreateDbContext();
        db.Tracks.AddRange(
            new Track
            {
                Id = oldTrackId,
                ArtistId = artistId,
                Title = "Old Signal",
                Genre = "electronic",
                Subgenre = "dock synth",
                Style = "Older dock synth.",
                Language = "en",
                HasVocals = true,
                SongStory = "The first song from the dock.",
                TargetDurationSeconds = 170,
                DurationSeconds = 169,
                FilePath = "library/tracks/old.wav",
                GenerationPrompt = "old prompt",
                Backend = "ace-step",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            },
            new Track
            {
                Id = newTrackId,
                ArtistId = artistId,
                Title = "New Signal",
                Genre = "electronic",
                Subgenre = "dock synth",
                Style = "Glass synths with steel percussion.",
                Language = "en",
                HasVocals = false,
                SongStory = "A reply to the first dock signal.",
                TargetDurationSeconds = 190,
                DurationSeconds = 188,
                FilePath = "library/tracks/new.wav",
                GenerationPrompt = "stored generation prompt",
                Backend = "musicgen",
                CreatedAt = DateTime.UtcNow,
            });
        db.ArtistPosts.Add(new ArtistPost
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Kind = ArtistPostKind.ArtistCreated,
            Body = "First wire post",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
        });
        await db.SaveChangesAsync();
        return (artistId, newTrackId);
    }

    private static async Task<Guid> SeedArtistAsync(DbFixture fixture)
    {
        var artistId = Guid.NewGuid();
        await using RadioDbContext db = fixture.CreateDbContext();
        db.Artists.Add(new Artist
        {
            Id = artistId,
            Name = "Wire Signal",
            Type = "Band",
            Genre = "electronic",
            Subgenre = "dock synth",
            Origin = "Rotterdam",
            Language = "en",
            StyleDescriptor = "Tape-worn synths and crane field recordings.",
            Biography = "A harbor synth band.",
            DeepBackgroundBiography = "They record after midnight by the docks.",
            PromotionText = "Port lights on tape.",
            CreatedAt = DateTime.UtcNow,
            Members =
            {
                new ArtistMember
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    Name = "Mara Voss",
                    Role = "lead vocals",
                    Biography = "Writes compact dockside lyrics.",
                    VoiceCreationPrompt = "Female alto, close microphone.",
                },
            },
        });
        await db.SaveChangesAsync();
        return artistId;
    }

    private sealed class CapturingLlm(string reply) : ITextGenerationService
    {
        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            UserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

    private sealed class CapturingPublisher : IArtistPostUpdatePublisher
    {
        public ArtistPostDto? LastPost { get; private set; }

        public Task PublishPostAddedAsync(ArtistPostDto post, CancellationToken ct = default)
        {
            LastPost = post;
            return Task.CompletedTask;
        }
    }

    private sealed class DbFixture(SqliteConnection connection, DbContextOptions<RadioDbContext> options)
        : IDbContextFactory<RadioDbContext>, IAsyncDisposable
    {
        public static async Task<DbFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            DbContextOptions<RadioDbContext> options = new DbContextOptionsBuilder<RadioDbContext>()
                .UseSqlite(connection)
                .Options;
            await using (RadioDbContext db = new(options))
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
