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
    public async Task CreateAsync_WithEmoteIds_PersistsDedupedBallotRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate1");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "EmoteB");
        var service = new VoteSessionService(db);

        // Duplicate and whitespace-padded ids collapse to one clean row each.
        var (result, session) = await service.CreateAsync(
            channel.ChannelName, "Kuratiert", AllowedRoles.Everyone, Actor,
            emoteIds: [emoteA.Id, $" {emoteA.Id} ", emoteB.Id]);

        Assert.Equal(CreateVoteSessionResult.Success, result);
        var ballot = await db.VoteSessionEmotes.Where(se => se.VoteSessionId == session!.Id).ToListAsync();
        Assert.Equal(2, ballot.Count);
        Assert.Contains(ballot, se => se.EmoteId == emoteA.Id);
        Assert.Contains(ballot, se => se.EmoteId == emoteB.Id);
    }

    [Fact]
    public async Task CreateAsync_WithEmptyEmoteIds_ReturnsEmoteIdsEmpty_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate2");
        var service = new VoteSessionService(db);

        // An explicit empty ballot (here: whitespace-only ids) is an error, not "all emotes".
        var (result, session) = await service.CreateAsync(
            channel.ChannelName, "Leer", AllowedRoles.Everyone, Actor, emoteIds: ["   ", ""]);

        Assert.Equal(CreateVoteSessionResult.EmoteIdsEmpty, result);
        Assert.Null(session);
        Assert.Empty(await LoadAuditEntriesAsync(db, "ballotcreate2"));
    }

    [Fact]
    public async Task CreateAsync_WithForeignUnknownOrArchivedEmoteId_ReturnsEmoteIdsInvalid()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcreate3");
        var otherChannel = await SeedChannelAsync(db, "ballotcreate3-other");
        var own = await SeedEmoteAsync(db, channel.Id, "Own");
        var foreign = await SeedEmoteAsync(db, otherChannel.Id, "Foreign");
        var archived = await SeedEmoteAsync(db, channel.Id, "Archived");
        archived.IsArchived = true;
        await db.SaveChangesAsync();
        var service = new VoteSessionService(db);

        var (foreignResult, _) = await service.CreateAsync(
            channel.ChannelName, "Fremd", AllowedRoles.Everyone, Actor, emoteIds: [own.Id, foreign.Id]);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, foreignResult);

        var (unknownResult, _) = await service.CreateAsync(
            channel.ChannelName, "Unbekannt", AllowedRoles.Everyone, Actor, emoteIds: [own.Id, Guid.NewGuid().ToString()]);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, unknownResult);

        var (archivedResult, _) = await service.CreateAsync(
            channel.ChannelName, "Archiviert", AllowedRoles.Everyone, Actor, emoteIds: [own.Id, archived.Id]);
        Assert.Equal(CreateVoteSessionResult.EmoteIdsInvalid, archivedResult);

        Assert.Empty(await LoadAuditEntriesAsync(db, "ballotcreate3"));
    }

    [Fact]
    public async Task CastVoteAsync_SubsetSession_AllowsBallotMembers_RejectsOutsiders()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast1");
        var onBallot = await SeedEmoteAsync(db, channel.Id, "OnBallot");
        var offBallot = await SeedEmoteAsync(db, channel.Id, "OffBallot");
        var voter = await SeedUserAsync(db, "ballotcast1-voter");
        var service = new VoteSessionService(db);
        var (_, session) = await service.CreateAsync(
            channel.ChannelName, "Kuratiert", AllowedRoles.Everyone, Actor, emoteIds: [onBallot.Id]);

        var (onResult, vote) = await service.CastVoteAsync(channel.ChannelName, session!.Id, onBallot.Id, voter.Id, VoteType.Keep);
        Assert.Equal(VoteCastResult.Success, onResult);
        Assert.NotNull(vote);

        // Off-ballot emote is a perfectly valid channel emote — but this session doesn't cover it.
        var (offResult, _) = await service.CastVoteAsync(channel.ChannelName, session.Id, offBallot.Id, voter.Id, VoteType.Delete);
        Assert.Equal(VoteCastResult.EmoteNotEligible, offResult);
    }

    [Fact]
    public async Task CastVoteAsync_OnArchivedEmote_ReturnsEmoteNotEligible_EvenOnItsOwnBallot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast2");
        var emote = await SeedEmoteAsync(db, channel.Id, "SoonGone");
        var voter = await SeedUserAsync(db, "ballotcast2-voter");
        var service = new VoteSessionService(db);
        var (_, session) = await service.CreateAsync(
            channel.ChannelName, "Kuratiert", AllowedRoles.Everyone, Actor, emoteIds: [emote.Id]);

        // Archived mid-session: stays visible in the results (badged), but voting on it is closed.
        emote.IsArchived = true;
        await db.SaveChangesAsync();

        var (result, _) = await service.CastVoteAsync(channel.ChannelName, session!.Id, emote.Id, voter.Id, VoteType.Delete);
        Assert.Equal(VoteCastResult.EmoteNotEligible, result);
    }

    [Fact]
    public async Task CastVoteAsync_DynamicSession_StillAllowsAnyActiveChannelEmote()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballotcast3");
        var emote = await SeedEmoteAsync(db, channel.Id, "AnyEmote");
        var voter = await SeedUserAsync(db, "ballotcast3-voter");
        var service = new VoteSessionService(db);
        var (_, session) = await service.CreateAsync(channel.ChannelName, "Alle", AllowedRoles.Everyone, Actor);

        var (result, vote) = await service.CastVoteAsync(channel.ChannelName, session!.Id, emote.Id, voter.Id, VoteType.Keep);

        Assert.Equal(VoteCastResult.Success, result);
        Assert.NotNull(vote);
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
