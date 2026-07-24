using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Emote> Emotes => Set<Emote>();
    public DbSet<UsageStat> UsageStats => Set<UsageStat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasIndex(c => c.ChannelName).IsUnique();
            entity.HasIndex(c => c.TwitchChannelId).IsUnique();
        });

        modelBuilder.Entity<Emote>(entity =>
        {
            // The same 7TV emote can be active in more than one channel, so
            // uniqueness only holds per channel, not globally.
            entity.HasIndex(e => new { e.ChannelId, e.SevenTvEmoteId }).IsUnique();

            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Emotes)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageStat>(entity =>
        {
            // One aggregated row per emote per UTC day.
            entity.HasIndex(u => new { u.EmoteId, u.Date }).IsUnique();

            entity.HasOne(u => u.Emote)
                .WithMany(e => e.UsageStats)
                .HasForeignKey(u => u.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
