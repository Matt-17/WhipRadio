using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

public class RadioDbContext(DbContextOptions<RadioDbContext> options) : DbContext(options)
{
    public DbSet<Moderator> Moderators => Set<Moderator>();

    public DbSet<Artist> Artists => Set<Artist>();

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

    public DbSet<MediaAnalysis> MediaAnalyses => Set<MediaAnalysis>();

    public DbSet<TransitionLogEntry> TransitionLog => Set<TransitionLogEntry>();

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
            artist.HasIndex(a => a.Genre);
        });

        modelBuilder.Entity<Announcement>(announcement =>
        {
            announcement.Property(a => a.Kind).HasConversion<string>();
            announcement.HasOne(a => a.Moderator)
                .WithMany()
                .HasForeignKey(a => a.ModeratorId);
            announcement.HasIndex(a => a.WasPlayed);
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
            entry.HasIndex(e => e.PlayedAt);
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
    }
}
