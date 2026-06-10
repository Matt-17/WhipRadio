using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Entities;

namespace WhipRadio.Infrastructure.Persistence;

public class RadioDbContext(DbContextOptions<RadioDbContext> options) : DbContext(options)
{
    public DbSet<Moderator> Moderators => Set<Moderator>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<PlayLogEntry> PlayLog => Set<PlayLogEntry>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<ScheduleSlot> ScheduleSlots => Set<ScheduleSlot>();

    public DbSet<StationSettings> StationSettings => Set<StationSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Track>(track =>
        {
            track.HasIndex(t => t.Genre);
            track.HasIndex(t => t.IsRetired);
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

        modelBuilder.Entity<ScheduleSlot>(slot =>
        {
            slot.HasOne(s => s.Moderator)
                .WithMany()
                .HasForeignKey(s => s.ModeratorId);
            slot.HasIndex(s => s.HourOfDay).IsUnique();
        });
    }
}
