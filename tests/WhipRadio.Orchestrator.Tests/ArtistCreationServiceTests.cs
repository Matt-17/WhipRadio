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
public class ArtistCreationServiceTests
{
    [TestMethod]
    public async Task RedefineArtistAsync_ReplacesProfileMembersAndPreservesTracks()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var artistId = await AddSparseArtistAsync(fixture);
        var llm = new CapturingLlm("""
{
  "name": "Harbor Signal Reborn",
  "type": "Band",
  "genre": "electronic",
  "subgenre": "coastal synthwave",
  "origin": "Rotterdam harbor district",
  "formationYear": 2019,
  "style": "Mid-tempo drum machines, glassy arpeggiators, warm bass pulses, and close female alto vocals with a salt-air production texture.",
  "language": "en",
  "shortBiography": "Broken Signal are a harbor-born synth band with a sharper identity and a usable public story.",
  "deepBackgroundBiography": "The group formed after late shifts around container terminals and small club residencies. Their internal tension comes from balancing glossy hooks with field recordings from cranes, rain, and radios. They write in English because their local scene grew around international port workers. Their stage setup places the singer between two stacked samplers and a battered reel recorder.",
  "promotionText": "Harbor lights, synth pressure, and a clear voice rebuilt for late-night radio.",
  "members": [
    {
      "name": "Mara Voss",
      "role": "lead vocals and sampler",
      "biography": "Mara writes lyrics from overheard dockside conversations and keeps the band's emotional center direct. She pushes the arrangements toward memorable choruses.",
      "voiceCreationPrompt": "Female alto singer, early 30s, light Dutch accent, intimate close microphone, controlled late-night energy."
    }
  ]
}
""");
        var service = new ArtistCreationService(
            fixture,
            new MusicCopywriter(llm),
            new ArtistSocialFeedService(
                fixture,
                new MusicCopywriter(llm),
                new NoOpArtistPostUpdatePublisher(),
                NullLogger<ArtistSocialFeedService>.Instance),
            new ArtistCreationQueue(),
            NullLogger<ArtistCreationService>.Instance);

        var updated = await service.RedefineArtistAsync(artistId, hint: null, CancellationToken.None);

        Assert.Equal(artistId, updated.Id);
        Assert.Equal("Broken Signal", updated.Name);
        Assert.Equal("coastal synthwave", updated.Subgenre);
        Assert.Equal("en", updated.Language);
        Assert.Contains("harbor-born synth band", updated.Biography);
        Assert.Equal(1, updated.Members.Count);
        Assert.Equal("Mara Voss", updated.Members.Single().Name);

        await using RadioDbContext db = fixture.CreateDbContext();
        var artist = await db.Artists.Include(a => a.Members).SingleAsync(a => a.Id == artistId);
        Assert.Equal(1, artist.Members.Count);
        Assert.Equal("Mara Voss", artist.Members.Single().Name);
        Assert.False(await db.ArtistMembers.AnyAsync(m => m.Name == "Placeholder Member"));
        Assert.True(await db.Tracks.AnyAsync(t => t.ArtistId == artistId && t.Title == "Old Song"));

        Assert.Contains("Current artist:", llm.UserPrompt);
        Assert.Contains("Output Name exactly as: Broken Signal", llm.UserPrompt);
        Assert.Contains("Placeholder Member", llm.UserPrompt);
    }

    [TestMethod]
    public async Task CreateArtistAsync_PersistsArtistCreatedPostWhenLlmReturnsPost()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var llm = new SequencedLlm(ArtistProfileJson("Wire Signal"), "Post(\"We found a frequency under the loading dock.\")");
        var copywriter = new MusicCopywriter(llm);
        var service = new ArtistCreationService(
            fixture,
            copywriter,
            new ArtistSocialFeedService(
                fixture,
                copywriter,
                new NoOpArtistPostUpdatePublisher(),
                NullLogger<ArtistSocialFeedService>.Instance),
            new ArtistCreationQueue(),
            NullLogger<ArtistCreationService>.Instance);

        var artist = await service.CreateArtistAsync("dock signal band", "electronic", "dock synth", CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        var post = await db.ArtistPosts.Include(p => p.Artist).SingleAsync();
        Assert.Equal(artist.Id, post.ArtistId);
        Assert.Equal(ArtistPostKind.ArtistCreated, post.Kind);
        Assert.Equal("We found a frequency under the loading dock.", post.Body);
        Assert.Equal("Wire Signal", post.Artist.Name);
    }

    [TestMethod]
    public async Task CreateArtistAsync_StillSucceedsWhenPostGenerationThrows()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        var llm = new SequencedLlm(ArtistProfileJson("Silent Relay"), "this is not a post function");
        var copywriter = new MusicCopywriter(llm);
        var service = new ArtistCreationService(
            fixture,
            copywriter,
            new ArtistSocialFeedService(
                fixture,
                copywriter,
                new NoOpArtistPostUpdatePublisher(),
                NullLogger<ArtistSocialFeedService>.Instance),
            new ArtistCreationQueue(),
            NullLogger<ArtistCreationService>.Instance);

        var artist = await service.CreateArtistAsync("private ambient artist", "ambient", "tape ambient", CancellationToken.None);

        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True(await db.Artists.AnyAsync(a => a.Id == artist.Id));
        Assert.False(await db.ArtistPosts.AnyAsync());
    }

    private static async Task<Guid> AddSparseArtistAsync(DbFixture fixture)
    {
        var artistId = Guid.NewGuid();
        await using RadioDbContext db = fixture.CreateDbContext();
        db.Artists.Add(new Artist
        {
            Id = artistId,
            Name = "Broken Signal",
            Genre = "electronic",
            Subgenre = "synthwave",
            StyleDescriptor = "old short style",
            Type = "Artist",
            Origin = "unknown",
            Language = "en",
            CreatedAt = DateTime.UtcNow,
            Biography = "old short bio",
            Members =
            {
                new ArtistMember
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    Name = "Placeholder Member",
                    Role = "unknown",
                    Biography = "not enough detail",
                    VoiceCreationPrompt = "generic voice",
                },
            },
        });
        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Title = "Old Song",
            Genre = "electronic",
            Subgenre = "synthwave",
            Style = "old style",
            FilePath = "library/tracks/old.wav",
            DurationSeconds = 120,
            GenerationPrompt = "old prompt",
            CreatedAt = DateTime.UtcNow,
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

    private sealed class SequencedLlm(params string[] replies) : ITextGenerationService
    {
        private int _index;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var reply = replies[Math.Min(_index, replies.Length - 1)];
            _index++;
            return Task.FromResult(reply);
        }
    }

    private sealed class NoOpArtistPostUpdatePublisher : IArtistPostUpdatePublisher
    {
        public Task PublishPostAddedAsync(ArtistPostDto post, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static string ArtistProfileJson(string name)
        => $$"""
{
  "name": "{{name}}",
  "type": "Band",
  "genre": "electronic",
  "subgenre": "dock synth",
  "origin": "Rotterdam harbor district",
  "formationYear": 2024,
  "style": "Tape-worn synths, crane field recordings, and clipped bass lines.",
  "language": "en",
  "shortBiography": "{{name}} turn harbor infrastructure into late-night electronic songs.",
  "deepBackgroundBiography": "The band formed after long dock shifts and treats ship radios as percussion sources.",
  "promotionText": "Port lights, tape hiss, and steel-frame rhythm.",
  "members": [
    {
      "name": "Mara Voss",
      "role": "lead vocals",
      "biography": "Mara writes compact dockside lyrics.",
      "voiceCreationPrompt": "Female alto, close microphone, light Dutch accent."
    }
  ]
}
""";

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
