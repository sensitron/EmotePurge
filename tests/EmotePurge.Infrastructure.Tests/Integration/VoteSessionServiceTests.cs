using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class VoteSessionServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeleteAsync_RemovesSession_AndCascadesVotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "deletetest1-voter");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionService(db);
        var deleted = await service.DeleteAsync(channel.ChannelName, session.Id);

        Assert.True(deleted);
        Assert.Null(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == session.Id));
        Assert.Empty(await db.Votes.Where(v => v.VoteSessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownSession_ReturnsFalse()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest2");

        var service = new VoteSessionService(db);
        var deleted = await service.DeleteAsync(channel.ChannelName, sessionId: 999_999);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownChannel_ReturnsFalse()
    {
        await using var db = fixture.CreateDbContext();

        var service = new VoteSessionService(db);
        var deleted = await service.DeleteAsync("does-not-exist", sessionId: 1);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAffectOtherSessions_InSameChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "deletetest3");
        var toDelete = await SeedActiveSessionAsync(db, channel.Id);
        var toKeep = await SeedActiveSessionAsync(db, channel.Id);

        var service = new VoteSessionService(db);
        var deleted = await service.DeleteAsync(channel.ChannelName, toDelete.Id);

        Assert.True(deleted);
        Assert.NotNull(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == toKeep.Id));
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelId, string name)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<VoteSession> SeedActiveSessionAsync(AppDbContext db, string channelId)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Test Session",
            AllowedVoterRoles = AllowedRoles.Everyone,
            IsActive = true
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<User> SeedUserAsync(AppDbContext db, string twitchUserId)
    {
        var user = new User { Id = twitchUserId, TwitchUsername = twitchUserId, DisplayName = twitchUserId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
