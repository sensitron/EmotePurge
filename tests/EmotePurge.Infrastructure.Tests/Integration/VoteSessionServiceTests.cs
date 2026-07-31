using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class VoteSessionServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4711", "sensitron");

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
        var deleted = await service.DeleteAsync(channel.ChannelName, session.Id, Actor);

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
        var deleted = await service.DeleteAsync(channel.ChannelName, sessionId: 999_999, Actor);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownChannel_ReturnsFalse()
    {
        await using var db = fixture.CreateDbContext();

        var service = new VoteSessionService(db);
        var deleted = await service.DeleteAsync("does-not-exist", sessionId: 1, Actor);

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
        var deleted = await service.DeleteAsync(channel.ChannelName, toDelete.Id, Actor);

        Assert.True(deleted);
        Assert.NotNull(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == toKeep.Id));
    }

    [Fact]
    public async Task CreateAsync_WritesAuditEntry_PointingAtTheNewSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit1");
        var service = new VoteSessionService(db);

        var (result, session) = await service.CreateAsync(
            channel.ChannelName, "Sommer-Purge", AllowedRoles.Everyone, Actor);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit1"));
        Assert.Equal(AuditActions.VoteSessionCreate, entry.Action);
        Assert.Equal(Actor.Login, entry.ActorLogin);
        // TargetId is only knowable after the insert — the service takes an explicit transaction for
        // exactly this, so the entry can name the session it created and still commit atomically.
        Assert.Equal("voteSession", entry.TargetType);
        Assert.Equal(session!.Id.ToString(), entry.TargetId);
        Assert.Equal("Sommer-Purge", ReadDetail(entry.DetailsJson, "title"));
    }

    [Fact]
    public async Task CreateAsync_RejectedByValidation_WritesNoAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit2");
        var service = new VoteSessionService(db);

        var (result, _) = await service.CreateAsync(channel.ChannelName, "   ", AllowedRoles.Everyone, Actor);

        Assert.Equal(CreateVoteSessionResult.TitleEmpty, result);
        Assert.Empty(await LoadAuditEntriesAsync(db, "votesessionaudit2"));
    }

    [Fact]
    public async Task EndAsync_WritesAuditEntry_Once_EvenWhenCalledTwice()
    {
        // Ending an already-ended session is an idempotent no-op by contract, and a no-op is not an
        // event — the second call must not add a second entry.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit3");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var service = new VoteSessionService(db);

        await service.EndAsync(channel.ChannelName, session.Id, Actor);
        await service.EndAsync(channel.ChannelName, session.Id, Actor);

        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit3"));
        Assert.Equal(AuditActions.VoteSessionEnd, entry.Action);
        Assert.Equal(session.Id.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task DeleteAsync_WritesAuditEntry_ThatOutlivesTheSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit4");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var service = new VoteSessionService(db);

        await service.DeleteAsync(channel.ChannelName, session.Id, Actor);

        Assert.Null(await db.VoteSessions.SingleOrDefaultAsync(s => s.Id == session.Id));
        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "votesessionaudit4"));
        Assert.Equal(AuditActions.VoteSessionDelete, entry.Action);
        // The title is captured into the entry because after the delete there is nothing left to
        // look it up in.
        Assert.Equal("Test Session", ReadDetail(entry.DetailsJson, "title"));
    }

    [Fact]
    public async Task DeleteAsync_ForUnknownSession_WritesNoAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesessionaudit5");
        var service = new VoteSessionService(db);

        await service.DeleteAsync(channel.ChannelName, sessionId: 999_999, Actor);

        Assert.Empty(await LoadAuditEntriesAsync(db, "votesessionaudit5"));
    }

    /// <summary>
    /// Reads one string member out of an entry's <c>DetailsJson</c>. Parsed rather than string-
    /// compared, because the column is <c>jsonb</c>: Postgres stores a normalized form and hands back
    /// <c>{"title": "x"}</c> for the <c>{"title":"x"}</c> that was written. The value is the contract,
    /// the byte-for-byte formatting is not.
    /// </summary>
    private static string? ReadDetail(string? detailsJson, string property)
    {
        return detailsJson is null ? null : JsonDocument.Parse(detailsJson).RootElement.GetProperty(property).GetString();
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(AppDbContext db, string channelName)
    {
        return await db.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
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
