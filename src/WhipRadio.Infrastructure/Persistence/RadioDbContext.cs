using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

public class RadioDbContext(DbContextOptions<RadioDbContext> options) : DbContext(options)
{
    public DbSet<Moderator> Moderators => Set<Moderator>();

    public DbSet<Artist> Artists => Set<Artist>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<PlayLogEntry> PlayLog => Set<PlayLogEntry>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<Format> Formats => Set<Format>();

    public DbSet<ProgramSlot> ProgramSlots => Set<ProgramSlot>();

    public DbSet<ModeratorMemory> ModeratorMemories => Set<ModeratorMemory>();

    public DbSet<ListenerMessage> ListenerMessages => Set<ListenerMessage>();

    public DbSet<StationSettings> StationSettings => Set<StationSettings>();

    public DbSet<Studio> Studios => Set<Studio>();

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

        modelBuilder.Entity<ModeratorMemory>(memory =>
        {
            memory.HasIndex(m => new { m.ModeratorId, m.Date });
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
    }
}
