using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Prompting;
using WhipRadio.Orchestrator.Services;
using WhipRadio.TestSupport;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ChatToolExpansionTests
{
    [TestMethod]
    public async Task QueueTrack_DirectorEnqueuesTrack()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("QueueTrack", new Dictionary<string, string> { ["track"] = "Neon Rider" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Equal(1, h.Queue.Items.Count);
        Assert.Contains("Neon Rider", h.Queue.Items[0].Title);
    }

    [TestMethod]
    public async Task QueueTrack_DirectorNextJumpsToFront()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        h.Queue.Enqueue(new WhipRadio.Core.Abstractions.PlayoutItem(
            PlayoutItemType.Track, Guid.NewGuid(), "x.wav", "Existing", 100));
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("QueueTrack", new Dictionary<string, string> { ["track"] = seeded.TrackId.ToString(), ["priority"] = "next" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Contains("Neon Rider", h.Queue.Items[0].Title);
    }

    [TestMethod]
    public async Task QueueTrack_RetiredTrackFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            Track track = await db.Tracks.FirstAsync(t => t.Id == seeded.TrackId);
            track.IsRetired = true;
            await db.SaveChangesAsync();
        }

        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("QueueTrack", new Dictionary<string, string> { ["track"] = seeded.TrackId.ToString() }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Equal(0, h.Queue.Items.Count);
    }

    [TestMethod]
    public async Task QueueTrack_AmbiguousTitleFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            db.Tracks.Add(NewTrack(seeded.ArtistId, "Neon Rider"));
            await db.SaveChangesAsync();
        }

        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("QueueTrack", new Dictionary<string, string> { ["track"] = "Neon Rider" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
    }

    [TestMethod]
    public async Task PlanTalkBreak_HostPersistsPendingBreakWithParsedKinds()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("PlanTalkBreak", new Dictionary<string, string>
            {
                ["parts"] = "Banter: welcome the night; Weather: quick forecast",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        TalkBreak talkBreak = await db.TalkBreaks.Include(item => item.Parts).SingleAsync();
        Assert.Equal(TalkBreakStatus.Pending, talkBreak.Status);
        Assert.Equal(seeded.HostId, talkBreak.ModeratorId);
        Assert.Equal(2, talkBreak.Parts.Count);
        Assert.Contains(TalkPartKind.Banter, talkBreak.Parts.Select(p => p.Kind).ToList());
        Assert.Contains(TalkPartKind.Weather, talkBreak.Parts.Select(p => p.Kind).ToList());
    }

    [TestMethod]
    public async Task CreateTalkBit_HostInsertsActiveBit()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("CreateTalkBit", new Dictionary<string, string> { ["premise"] = "the cat that runs the mixing desk" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        TalkBit bit = await db.TalkBits.SingleAsync();
        Assert.Equal(TalkBitStatus.Active, bit.Status);
        Assert.Equal(seeded.HostId, bit.ModeratorId);
    }

    [TestMethod]
    public async Task Remember_HostStoresDayMemory()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("Remember", new Dictionary<string, string> { ["note"] = "The boss wants more jazz on Sundays." }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        ModeratorMemory memory = await db.ModeratorMemories.SingleAsync(m => m.ModeratorId == seeded.HostId);
        Assert.Equal(ModeratorMemoryLayer.DayMemory, memory.Layer);
        Assert.Contains("jazz", memory.Content);
    }

    [TestMethod]
    public async Task RetireTrack_DirectorFlipsFlag()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("RetireTrack", new Dictionary<string, string> { ["track"] = seeded.TrackId.ToString(), ["reason"] = "off-brand" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True((await db.Tracks.FirstAsync(t => t.Id == seeded.TrackId)).IsRetired);
    }

    [TestMethod]
    public async Task RetireTrack_UnknownTrackFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("RetireTrack", new Dictionary<string, string> { ["track"] = "Nonexistent", ["reason"] = "x" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
    }

    [TestMethod]
    public async Task SetNewsPresenter_ValidatesSpecialistAndWritesSettings()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord ok = await h.Executor.ExecuteAsync(
            new CharacterToolCall("SetNewsPresenter", new Dictionary<string, string> { ["host"] = seeded.NewsHostId.ToString() }),
            context,
            CancellationToken.None);
        Assert.Equal(ChatActionState.Succeeded, ok.State);
        Assert.Equal(1, h.ProductionUpdates.NewsChanged);

        await using RadioDbContext db = fixture.CreateDbContext();
        StationSettings settings = await db.StationSettings.SingleAsync();
        Assert.Equal(seeded.NewsHostId, settings.NewsPresenterModeratorId);

        ChatActionRecord rejected = await h.Executor.ExecuteAsync(
            new CharacterToolCall("SetNewsPresenter", new Dictionary<string, string> { ["host"] = seeded.HostId.ToString() }),
            context,
            CancellationToken.None);
        Assert.Equal(ChatActionState.Failed, rejected.State);
    }

    [TestMethod]
    public async Task SetJingleActive_TogglesJingle()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("SetJingleActive", new Dictionary<string, string> { ["jingle"] = "Night ID", ["isActive"] = "false" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.False((await db.Jingles.FirstAsync(j => j.Id == seeded.JingleId)).IsActive);
    }

    [TestMethod]
    public async Task PostArtistFeed_ArtistPersistsPost()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await ArtistContextAsync(fixture, seeded.MemberId, seeded.GroupChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("PostArtistFeed", new Dictionary<string, string> { ["body"] = "New single dropping Friday!" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Equal(1, h.Posts.Posts.Count);
        await using RadioDbContext db = fixture.CreateDbContext();
        ArtistPost post = await db.ArtistPosts.SingleAsync();
        Assert.Equal(ArtistPostKind.StatusUpdate, post.Kind);
        Assert.Equal(seeded.ArtistId, post.ArtistId);
    }

    [TestMethod]
    public async Task RequestSongFromArtist_PostsToSharedGroupChannel()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.GroupChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("RequestSongFromArtist", new Dictionary<string, string>
            {
                ["artist"] = "Makoa Hale",
                ["brief"] = "A slow ballad about the harbor at dawn.",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext db = fixture.CreateDbContext();
        Assert.True(await db.ChatMessages.AnyAsync(m =>
            m.ChannelId == seeded.GroupChannelId && m.Text.Contains("harbor at dawn")));
    }

    [TestMethod]
    public async Task RequestSongFromArtist_NoSharedGroupFails()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);

        // The member shares no group with anyone: strip the seeded membership.
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            await db.ChatChannelMembers
                .Where(member => member.ArtistMemberId == seeded.MemberId)
                .ExecuteDeleteAsync();
        }

        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("RequestSongFromArtist", new Dictionary<string, string>
            {
                ["artist"] = "Makoa Hale",
                ["brief"] = "Anything.",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Contains("group channel", record.ResultSummary);
    }

    [TestMethod]
    public async Task GetArtistProfile_HidesDeepBackgroundFromHosts()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("GetArtistProfile", new Dictionary<string, string> { ["artist"] = "Pacific Furnace" }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.DoesNotContain("Formed near Hilo Bay", record.ResultSummary);
    }

    [TestMethod]
    public async Task SearchArtist_ReturnsMatchWithoutCreating()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, (await StationChannelIdAsync(fixture)));

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("SearchArtist", new Dictionary<string, string>
            {
                ["style"] = "metal",
                ["createIfMissing"] = "false",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Contains("Pacific Furnace", record.ResultSummary);
    }

    [TestMethod]
    public async Task DeleteArtist_WithoutApproval_QueuesAndDoesNotDelete()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        // A fresh artist with no tracks so deletion would otherwise be allowed.
        Guid emptyArtistId;
        await using (RadioDbContext db = fixture.CreateDbContext())
        {
            Artist artist = new()
            {
                Id = Guid.NewGuid(),
                Name = "Ghost Signal",
                Slug = $"ghost-{Guid.NewGuid():N}",
                Genre = "ambient",
                Subgenre = "drone",
                CreatedAt = DateTime.UtcNow,
            };
            db.Artists.Add(artist);
            await db.SaveChangesAsync();
            emptyArtistId = artist.Id;
        }

        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("DeleteArtist", new Dictionary<string, string>
            {
                ["artist"] = "Ghost Signal",
                ["reason"] = "created by mistake",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        Assert.Contains("approval", record.ResultSummary!, StringComparison.OrdinalIgnoreCase);

        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.True(await verify.Artists.AnyAsync(a => a.Id == emptyArtistId), "artist must survive until approved");
        PendingApproval approval = await verify.PendingApprovals.SingleAsync();
        Assert.Equal("DeleteArtist", approval.Tool);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
    }

    [TestMethod]
    public async Task RetireArtist_DirectorRetiresWithoutApproval()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("RetireArtist", new Dictionary<string, string>
            {
                ["artist"] = "Pacific Furnace",
                ["reason"] = "off brand",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.True(await verify.Artists.Where(a => a.Id == seeded.ArtistId).Select(a => a.IsRetired).SingleAsync());
    }

    [TestMethod]
    public async Task SetProductionSwitch_NewsOff_UpdatesSettings()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        ChatActionContext context = await DirectorContextAsync(fixture, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("SetProductionSwitch", new Dictionary<string, string>
            {
                ["switch"] = "news",
                ["enabled"] = "false",
                ["reason"] = "debugging",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Succeeded, record.State);
        await using RadioDbContext verify = fixture.CreateDbContext();
        Assert.False(await verify.StationSettings.Select(s => s.NewsEnabled).FirstAsync());
    }

    [TestMethod]
    public async Task NewVerb_RejectedForWrongRole()
    {
        await using DbFixture fixture = await DbFixture.CreateAsync();
        Seeded seeded = await SeedAsync(fixture);
        Harness h = CreateHarness(fixture);
        // A host cannot fire people.
        ChatActionContext context = await HostContextAsync(fixture, seeded.HostId, seeded.StationChannelId);

        ChatActionRecord record = await h.Executor.ExecuteAsync(
            new CharacterToolCall("FireHost", new Dictionary<string, string>
            {
                ["host"] = "Nova Quinn",
                ["reason"] = "nope",
            }),
            context,
            CancellationToken.None);

        Assert.Equal(ChatActionState.Failed, record.State);
        Assert.Equal(0, await CountApprovalsAsync(fixture));
    }

    private static async Task<int> CountApprovalsAsync(DbFixture fixture)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        return await db.PendingApprovals.CountAsync();
    }

    private sealed record Harness(
        ChatActionExecutor Executor,
        TestPlayoutQueue Queue,
        TestNotificationBus Notifications,
        RecordingArtistPostPublisher Posts,
        TestProductionUpdatePublisher ProductionUpdates,
        ChatTurnQueue TurnQueue);

    private static Harness CreateHarness(DbFixture fixture)
    {
        var queue = new TestPlayoutQueue();
        var notifications = new TestNotificationBus();
        var posts = new RecordingArtistPostPublisher();
        var productionUpdates = new TestProductionUpdatePublisher();
        var turnQueue = new ChatTurnQueue(NullLogger<ChatTurnQueue>.Instance);
        var chat = new ChatService(fixture, new TestHubContext(), TimeProvider.System, NullLogger<ChatService>.Instance);
        var moderatorMemory = new ModeratorMemoryService(
            fixture, null!, null!, null!, NullLogger<ModeratorMemoryService>.Instance);
        var director = new DirectorPlanningService(
            fixture, null!, null!, null!, NullLogger<DirectorPlanningService>.Instance);
        var socialFeed = new ArtistSocialFeedService(fixture, null!, posts, NullLogger<ArtistSocialFeedService>.Instance);

        var executor = new ChatActionExecutor(
            fixture,
            FullCatalog(),
            chat,
            new ChatParticipantResolver(fixture),
            turnQueue,
            new TrackQueryService(fixture),
            new MusicProductionControl(),
            priorityDispatcher: null!,
            schedule: null!,
            director,
            notifications,
            scopeFactory: null!,
            queue,
            moderatorMemory,
            participantMemory: null!,
            productionUpdates,
            socialFeed,
            newsProduction: null!,
            new TestHubContext(),
            TimeProvider.System,
            NullLogger<ChatActionExecutor>.Instance);
        return new Harness(executor, queue, notifications, posts, productionUpdates, turnQueue);
    }

    private static CharacterToolCatalog FullCatalog()
    {
        var tools = typeof(MessageTool).Assembly.GetTypes()
            .Where(type => typeof(ICharacterTool).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (ICharacterTool)Activator.CreateInstance(type)!)
            .ToArray();
        return new CharacterToolCatalog(tools);
    }

    private static async Task<ChatActionContext> DirectorContextAsync(DbFixture fixture, Guid channelId)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        ChatChannel channel = await db.ChatChannels.AsNoTracking().FirstAsync(item => item.Id == channelId);
        return new ChatActionContext(channel, null, ChatParticipantResolver.Director, Guid.NewGuid(), 0);
    }

    private static async Task<ChatActionContext> HostContextAsync(DbFixture fixture, int hostId, Guid channelId)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        Moderator host = await db.Moderators.AsNoTracking().FirstAsync(m => m.Id == hostId);
        ChatChannel channel = await db.ChatChannels.AsNoTracking().FirstAsync(item => item.Id == channelId);
        return new ChatActionContext(channel, null, ChatParticipantResolver.FromModerator(host), Guid.NewGuid(), 0);
    }

    private static async Task<ChatActionContext> ArtistContextAsync(DbFixture fixture, Guid memberId, Guid channelId)
    {
        var resolver = new ChatParticipantResolver(fixture);
        ChatParticipant sender = await resolver.ResolveAsync(ChatParticipantRef.ForArtistMember(memberId), CancellationToken.None)
            ?? throw new InvalidOperationException("Member not resolved.");
        await using RadioDbContext db = fixture.CreateDbContext();
        ChatChannel channel = await db.ChatChannels.AsNoTracking().FirstAsync(item => item.Id == channelId);
        return new ChatActionContext(channel, null, sender, Guid.NewGuid(), 0);
    }

    private static async Task<Guid> StationChannelIdAsync(DbFixture fixture)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        return await db.ChatChannels.Where(c => c.Kind == ChatChannelKind.Station).Select(c => c.Id).FirstAsync();
    }

    private sealed record Seeded(
        int HostId,
        int NewsHostId,
        Guid ArtistId,
        Guid MemberId,
        Guid TrackId,
        Guid JingleId,
        Guid GroupChannelId,
        Guid StationChannelId);

    private static async Task<Seeded> SeedAsync(DbFixture fixture)
    {
        await using RadioDbContext db = fixture.CreateDbContext();
        db.StationSettings.Add(new StationSettings { Id = StationSettings.SingletonId });

        Moderator host = new()
        {
            Name = "Nova Quinn",
            Slug = $"nova-{Guid.NewGuid():N}",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "Warm late-night host.",
            Style = "calm",
            IsActive = true,
        };
        Moderator news = new()
        {
            Name = "Cass Vega",
            Slug = $"cass-{Guid.NewGuid():N}",
            Language = "en",
            Gender = ModeratorGenders.Female,
            PersonaPrompt = "Crisp news reader.",
            Style = "formal",
            IsActive = true,
            IsNewsSpecialist = true,
        };
        db.Moderators.AddRange(host, news);

        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Pacific Furnace",
            Slug = $"pf-{Guid.NewGuid():N}",
            Genre = "metal",
            Subgenre = "doom",
            StyleDescriptor = "slow heavy riffs",
            Biography = "A doom trio.",
            DeepBackgroundBiography = "Formed near Hilo Bay.",
        };
        db.Artists.Add(artist);
        ArtistMember member = new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artist.Id,
            SortOrder = 0,
            Name = "Makoa Hale",
            Role = "lead vocals",
            Biography = "Writes lyrics.",
            VoiceCreationPrompt = "Baritone.",
            VoiceId = "qv-makoa",
        };
        db.ArtistMembers.Add(member);

        Track track = NewTrack(artist.Id, "Neon Rider");
        db.Tracks.Add(track);

        Jingle jingle = new()
        {
            Id = Guid.NewGuid(),
            Label = "Night ID",
            Prompt = "station id",
            Style = "synth",
            Language = "en",
            DurationSeconds = 6,
            FilePath = $"branding/{Guid.NewGuid():N}.wav",
            Backend = "ace-step",
            Status = JingleStatus.Ready,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Jingles.Add(jingle);

        ChatChannel station = new()
        {
            Id = Guid.NewGuid(),
            Kind = ChatChannelKind.Station,
            Name = "Station",
        };
        ChatChannel group = new()
        {
            Id = Guid.NewGuid(),
            Kind = ChatChannelKind.Group,
            Name = "Studio Group",
        };
        db.ChatChannels.AddRange(station, group);
        db.ChatChannelMembers.Add(new ChatChannelMember
        {
            ChannelId = group.Id,
            Kind = ChatParticipantKind.ArtistMember,
            ArtistMemberId = member.Id,
        });

        await db.SaveChangesAsync();
        return new Seeded(host.Id, news.Id, artist.Id, member.Id, track.Id, jingle.Id, group.Id, station.Id);
    }

    private static Track NewTrack(Guid artistId, string title)
        => new()
        {
            Id = Guid.NewGuid(),
            ArtistId = artistId,
            Title = title,
            Genre = "metal",
            Subgenre = "doom",
            Style = "heavy",
            Language = "en",
            FilePath = $"library/tracks/{Guid.NewGuid():N}.wav",
            GenerationPrompt = "prompt",
            Backend = "ace-step",
            DurationSeconds = 180,
            SongStory = "Night shift.",
            CreatedAt = DateTime.UtcNow,
        };
}
