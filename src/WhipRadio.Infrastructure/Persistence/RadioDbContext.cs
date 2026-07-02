using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Selection;

namespace WhipRadio.Infrastructure.Persistence;

public class RadioDbContext(DbContextOptions<RadioDbContext> options) : DbContext(options)
{
    public DbSet<Moderator> Moderators => Set<Moderator>();

    public DbSet<Artist> Artists => Set<Artist>();

    public DbSet<ArtistMember> ArtistMembers => Set<ArtistMember>();

    public DbSet<ArtistPost> ArtistPosts => Set<ArtistPost>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<TalkBreak> TalkBreaks => Set<TalkBreak>();

    public DbSet<TalkPart> TalkParts => Set<TalkPart>();

    public DbSet<TalkBit> TalkBits => Set<TalkBit>();

    public DbSet<TalkBitRendition> TalkBitRenditions => Set<TalkBitRendition>();

    public DbSet<Jingle> Jingles => Set<Jingle>();

    public DbSet<PlayLogEntry> PlayLog => Set<PlayLogEntry>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<Format> Formats => Set<Format>();

    public DbSet<ProgramSlot> ProgramSlots => Set<ProgramSlot>();

    public DbSet<ModeratorMemory> ModeratorMemories => Set<ModeratorMemory>();

    public DbSet<ListenerMessage> ListenerMessages => Set<ListenerMessage>();

    public DbSet<StationSettings> StationSettings => Set<StationSettings>();

    public DbSet<Studio> Studios => Set<Studio>();

    public DbSet<StudioHistoryEntry> StudioHistory => Set<StudioHistoryEntry>();

    public DbSet<MediaAnalysis> MediaAnalyses => Set<MediaAnalysis>();

    public DbSet<TransitionLogEntry> TransitionLog => Set<TransitionLogEntry>();

    public DbSet<NewsFeed> NewsFeeds => Set<NewsFeed>();

    public DbSet<NewsItem> NewsItems => Set<NewsItem>();

    public DbSet<NewsPackage> NewsPackages => Set<NewsPackage>();

    public DbSet<ChatChannel> ChatChannels => Set<ChatChannel>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<ProgramDirectorLog> ProgramDirectorLogs => Set<ProgramDirectorLog>();

    public DbSet<AgentActionLog> AgentActionLogs => Set<AgentActionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Track>(track =>
        {
            track.HasIndex(t => t.Genre);
            track.HasIndex(t => t.IsRetired);
            track.HasOne(t => t.Artist)
                .WithMany()
                .HasForeignKey(t => t.ArtistId);
        });

        modelBuilder.Entity<Artist>(artist =>
        {
            artist.HasIndex(a => a.Name);
            artist.HasIndex(a => a.Slug).IsUnique();
            artist.HasIndex(a => a.Genre);
            artist.HasMany(a => a.Members)
                .WithOne(m => m.Artist)
                .HasForeignKey(m => m.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArtistMember>(member =>
        {
            member.Property(m => m.TtsEngine).HasDefaultValue("qwen");
            member.HasIndex(m => new { m.ArtistId, m.SortOrder });
        });

        modelBuilder.Entity<ArtistPost>(post =>
        {
            post.Property(p => p.Kind).HasConversion<string>();
            post.HasIndex(p => p.CreatedAtUtc).IsDescending();
            post.HasIndex(p => p.ArtistId);
            post.HasOne(p => p.Artist)
                .WithMany()
                .HasForeignKey(p => p.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);
            post.HasOne(p => p.Track)
                .WithMany()
                .HasForeignKey(p => p.TrackId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Announcement>(announcement =>
        {
            announcement.Property(a => a.Kind).HasConversion<string>();
            announcement.Property(a => a.PlayoutIntent)
                .HasConversion<string>()
                .HasDefaultValue(AnnouncementPlayoutIntent.Immediate);
            announcement.HasOne(a => a.Moderator)
                .WithMany()
                .HasForeignKey(a => a.ModeratorId);
            announcement.HasIndex(a => a.WasPlayed);
            announcement.HasIndex(a => a.PlayoutIntent);
        });

        modelBuilder.Entity<TalkBreak>(talkBreak =>
        {
            talkBreak.Property(t => t.Priority).HasConversion<string>();
            talkBreak.Property(t => t.Status).HasConversion<string>();
            talkBreak.HasIndex(t => t.AnnouncementId).IsUnique();
            talkBreak.HasIndex(t => new { t.Status, t.ExpiresAtUtc });
            talkBreak.HasMany(t => t.Parts)
                .WithOne(p => p.TalkBreak)
                .HasForeignKey(p => p.TalkBreakId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TalkPart>(part =>
        {
            part.Property(p => p.Kind).HasConversion<string>();
            part.Property(p => p.Status).HasConversion<string>();
            part.Property(p => p.Priority).HasConversion<string>();
            part.HasIndex(p => new { p.TalkBreakId, p.SortOrder }).IsUnique();
            part.HasIndex(p => new { p.Status, p.ExpiresAtUtc });
        });

        modelBuilder.Entity<TalkBit>(bit =>
        {
            bit.Property(b => b.Status).HasConversion<string>();
            bit.HasIndex(b => new { b.ModeratorId, b.Status });
            bit.HasIndex(b => b.LastUsedAtUtc);
            bit.HasMany(b => b.Renditions)
                .WithOne(r => r.TalkBit)
                .HasForeignKey(r => r.TalkBitId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Jingle>(jingle =>
        {
            jingle.Property(j => j.Status).HasConversion<string>();
            jingle.HasIndex(j => j.IsActive);
            jingle.HasIndex(j => j.CreatedAtUtc);
        });

        modelBuilder.Entity<TalkBitRendition>(rendition =>
        {
            rendition.HasIndex(r => r.TalkBitId);
        });

        modelBuilder.Entity<PlayLogEntry>(entry =>
        {
            entry.Property(e => e.ItemType).HasConversion<string>();
            entry.Property(e => e.WasFallback).HasDefaultValue(false);
            entry.HasIndex(e => e.PlayedAt);
            entry.HasIndex(e => new { e.ItemType, e.PlayedAt });
        });

        modelBuilder.Entity<Vote>()
            .HasOne(v => v.Track)
            .WithMany()
            .HasForeignKey(v => v.TrackId);

        modelBuilder.Entity<Format>(format =>
        {
            format.Property(f => f.TalkDepth).HasConversion<string>();
            format.HasOne(f => f.Moderator)
                .WithMany()
                .HasForeignKey(f => f.ModeratorId);
            format.OwnsOne(f => f.SelectionRules, rules =>
            {
                rules.Property(r => r.Mode).HasConversion<string>().HasDefaultValue(SelectionMode.StandardRotation);
                rules.Property(r => r.ArtistLookbackTracks).HasDefaultValue(8);
                rules.Property(r => r.SubgenreRotation).HasDefaultValue(true);
                rules.Property(r => r.PreferHostGenres).HasDefaultValue(true);
            });
        });

        modelBuilder.Entity<ProgramSlot>(slot =>
        {
            slot.HasOne(s => s.Format)
                .WithMany()
                .HasForeignKey(s => s.FormatId);
            slot.HasIndex(s => new { s.DayOfWeek, s.StartMinute }).IsUnique();
        });

        modelBuilder.Entity<Moderator>(moderator =>
        {
            moderator.Property(m => m.BaselineEnergy).HasConversion<string>();
            moderator.Property(m => m.BaselineFormality).HasConversion<string>();
            moderator.Property(m => m.BaselineHumorLevel).HasConversion<string>();
            moderator.Property(m => m.BaselineTalkativeness).HasConversion<string>();
            moderator.Property(m => m.BaselineWarmth).HasConversion<string>();
            moderator.HasIndex(m => m.Slug).IsUnique();
        });

        modelBuilder.Entity<ModeratorMemory>(memory =>
        {
            memory.Property(m => m.Layer).HasConversion<string>();
            memory.HasIndex(m => new { m.ModeratorId, m.Layer, m.Date });
        });

        modelBuilder.Entity<ListenerMessage>(message =>
        {
            message.Property(m => m.Kind).HasConversion<string>();
            message.Property(m => m.Status).HasConversion<string>();
            message.HasIndex(m => m.Status);
        });

        modelBuilder.Entity<Studio>(studio =>
        {
            studio.Property(s => s.Kind).HasConversion<string>();
            studio.HasIndex(s => new { s.Kind, s.IsActive });
        });

        modelBuilder.Entity<StudioHistoryEntry>(history =>
        {
            history.Property(h => h.StudioKind).HasConversion<string>();
            history.HasOne(h => h.Studio)
                .WithMany()
                .HasForeignKey(h => h.StudioId)
                .OnDelete(DeleteBehavior.SetNull);
            history.HasIndex(h => new { h.StudioId, h.StartedAtUtc });
            history.HasIndex(h => new { h.StudioKind, h.StartedAtUtc });
            history.HasIndex(h => new { h.Status, h.StartedAtUtc });
        });

        modelBuilder.Entity<MediaAnalysis>(analysis =>
        {
            analysis.Property(a => a.ItemType).HasConversion<string>();
            analysis.HasIndex(a => new { a.ItemType, a.ItemId }).IsUnique();
        });

        modelBuilder.Entity<TransitionLogEntry>(entry =>
        {
            entry.Property(e => e.OutgoingType).HasConversion<string>();
            entry.Property(e => e.IncomingType).HasConversion<string>();
            entry.HasIndex(e => e.OccurredAt);
        });

        modelBuilder.Entity<NewsFeed>(feed =>
        {
            feed.HasIndex(f => f.Url).IsUnique();
            feed.HasIndex(f => new { f.IsEnabled, f.LastPolledAtUtc });
            feed.HasMany(f => f.Items)
                .WithOne(i => i.Feed)
                .HasForeignKey(i => i.FeedId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NewsItem>(item =>
        {
            item.Property(i => i.Status).HasConversion<string>();
            item.HasIndex(i => new { i.FeedId, i.Url }).IsUnique();
            item.HasIndex(i => i.ContentHash);
            item.HasIndex(i => new { i.Status, i.PublishedAtUtc, i.FirstSeenAtUtc });
        });

        modelBuilder.Entity<NewsPackage>(package =>
        {
            package.Property(p => p.Kind).HasConversion<string>();
            package.Property(p => p.Status).HasConversion<string>();
            package.HasIndex(p => new { p.Kind, p.TargetUtc }).IsUnique();
            package.HasIndex(p => new { p.Status, p.TargetUtc });
            package.HasOne<Announcement>()
                .WithMany()
                .HasForeignKey(p => p.AnnouncementId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChatChannel>(channel =>
        {
            channel.Property(c => c.Kind).HasConversion<string>();
            channel.HasIndex(c => new { c.Kind, c.ModeratorId, c.CounterpartModeratorId });
            channel.HasIndex(c => c.LastMessageAtUtc);
            channel.HasOne(c => c.Moderator)
                .WithMany()
                .HasForeignKey(c => c.ModeratorId)
                .OnDelete(DeleteBehavior.SetNull);
            channel.HasOne(c => c.CounterpartModerator)
                .WithMany()
                .HasForeignKey(c => c.CounterpartModeratorId)
                .OnDelete(DeleteBehavior.SetNull);
            channel.HasMany(c => c.Messages)
                .WithOne(m => m.Channel)
                .HasForeignKey(m => m.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(message =>
        {
            message.Property(m => m.SenderKind).HasConversion<string>();
            message.HasIndex(m => new { m.ChannelId, m.CreatedAtUtc });
            message.HasIndex(m => m.CorrelationId);
            message.HasOne(m => m.SenderModerator)
                .WithMany()
                .HasForeignKey(m => m.SenderModeratorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProgramDirectorLog>(log =>
        {
            log.Property(l => l.Source).HasConversion<string>();
            log.HasIndex(l => new { l.Source, l.CreatedAtUtc });
        });

        modelBuilder.Entity<AgentActionLog>(log =>
        {
            log.Property(l => l.Kind).HasConversion<string>();
            log.HasIndex(l => l.CreatedAtUtc);
            log.HasIndex(l => new { l.AgentName, l.CreatedAtUtc });
            log.HasIndex(l => l.CorrelationId);
        });
    }
}
