using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Emote> Emotes => Set<Emote>();
    public DbSet<UsageStat> UsageStats => Set<UsageStat>();
    public DbSet<User> Users => Set<User>();
    public DbSet<VoteSession> VoteSessions => Set<VoteSession>();
    public DbSet<Vote> Votes => Set<Vote>();

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
            // One aggregated row per emote per UTC day. Covering index (UseCount included)
            // so range-sum queries over (EmoteId, Date) can be answered as an index-only scan.
            entity.HasIndex(u => new { u.EmoteId, u.Date })
                .IsUnique()
                .IncludeProperties(u => u.UseCount);

            entity.HasOne(u => u.Emote)
                .WithMany(e => e.UsageStats)
                .HasForeignKey(u => u.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.TwitchUsername).IsUnique();
        });

        modelBuilder.Entity<VoteSession>(entity =>
        {
            entity.HasIndex(s => new { s.ChannelId, s.IsActive });

            entity.HasOne(s => s.Channel)
                .WithMany()
                .HasForeignKey(s => s.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Vote>(entity =>
        {
            // One vote per user per emote per session — a repeat vote updates the existing row.
            entity.HasIndex(v => new { v.VoteSessionId, v.EmoteId, v.UserId }).IsUnique();

            entity.HasOne(v => v.VoteSession)
                .WithMany(s => s.Votes)
                .HasForeignKey(v => v.VoteSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(v => v.Emote)
                .WithMany()
                .HasForeignKey(v => v.EmoteId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: no code path deletes a User today, but a future one shouldn't
            // silently wipe vote history as a side effect.
            entity.HasOne(v => v.User)
                .WithMany()
                .HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
