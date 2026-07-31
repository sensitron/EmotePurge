using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class ChannelServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4711", "sensitron");

    [Fact]
    public async Task JoinAsync_CreatesChannel_AndPublishesJoinCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var channel = await service.JoinAsync("ChannelServiceTest1", Actor);

        Assert.Equal("channelservicetest1", channel.ChannelName);
        Assert.True(channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "JOIN:channelservicetest1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_CalledTwice_WithoutLeaving_DoesNotCreateADuplicateRow()
    {
        // JoinAsync's null-check/else branch (reuse existing row, just flip IsBotActive) only
        // matters because the unique index on ChannelName would otherwise reject a second insert.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var first = await service.JoinAsync("channelservicetest2", Actor);
        var second = await service.JoinAsync("channelservicetest2", Actor);

        Assert.Equal(first.Id, second.Id);
        var rows = await db.Channels.AsNoTracking().Where(c => c.ChannelName == "channelservicetest2").ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task LeaveAsync_DeactivatesChannel_ButKeepsTheRow()
    {
        // The row must survive: it hangs on four cascade edges (emotes, usage stats, vote sessions,
        // votes), and none of that history is reconstructible. Deleting on leave used to make a
        // moderator's single click destroy the channel's entire recorded history.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelservicetest3", Actor);

        var deactivated = await service.LeaveAsync("ChannelServiceTest3", Actor);

        Assert.True(deactivated);
        var channel = await service.GetByNameAsync("channelservicetest3");
        Assert.NotNull(channel);
        Assert.False(channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "LEAVE:channelservicetest3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_AfterLeave_ReactivatesTheSameRow()
    {
        // The whole point of the soft deactivate: rejoining must bring the channel and its history
        // back rather than start a second, empty row.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        var joined = await service.JoinAsync("channelservicetest5", Actor);
        await service.LeaveAsync("channelservicetest5", Actor);

        var rejoined = await service.JoinAsync("channelservicetest5", Actor);

        Assert.Equal(joined.Id, rejoined.Id);
        Assert.True(rejoined.IsBotActive);
    }

    [Fact]
    public async Task PurgeAsync_RemovesChannel_AndPublishesLeaveCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelservicetest6", Actor);

        var purged = await service.PurgeAsync("ChannelServiceTest6", Actor);

        Assert.True(purged);
        Assert.Null(await service.GetByNameAsync("channelservicetest6"));
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "LEAVE:channelservicetest6", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeAsync_ForUnknownChannel_ReturnsFalse_AndDoesNotPublish()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var purged = await service.PurgeAsync("neverjoinedchannel", Actor);

        Assert.False(purged);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveAsync_ForUnknownChannel_ReturnsFalse_AndDoesNotPublish()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var deactivated = await service.LeaveAsync("neverjoinedchannel", Actor);

        Assert.False(deactivated);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByNameAsync_Normalizes_ChannelNameLookup()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("ChannelServiceTest4", Actor);

        var found = await service.GetByNameAsync("  channelservicetest4  ");

        Assert.NotNull(found);
    }

    [Fact]
    public async Task JoinAsync_WritesAuditEntry_WithNormalizedChannelName_AndActor()
    {
        await using var db = fixture.CreateDbContext();
        var service = new ChannelService(db, Substitute.For<IRedisPublisher>());

        await service.JoinAsync("ChannelServiceAudit1", Actor);

        var entry = Assert.Single(await LoadAuditEntriesAsync(db, "channelserviceaudit1"));
        Assert.Equal(AuditActions.ChannelJoin, entry.Action);
        // The name is stored normalized, not as typed — the audit log filters on the same form every
        // other channel lookup does.
        Assert.Equal("channelserviceaudit1", entry.ChannelName);
        Assert.Equal(Actor.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(Actor.Login, entry.ActorLogin);
        Assert.Null(entry.DetailsJson);
    }

    [Fact]
    public async Task LeaveAsync_WritesAuditEntry_AfterTheJoinEntry()
    {
        await using var db = fixture.CreateDbContext();
        var service = new ChannelService(db, Substitute.For<IRedisPublisher>());
        await service.JoinAsync("channelserviceaudit2", Actor);

        await service.LeaveAsync("ChannelServiceAudit2", Actor);

        var entries = await LoadAuditEntriesAsync(db, "channelserviceaudit2");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelLeave], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task PurgeAsync_AuditEntry_SurvivesTheChannelDeletion()
    {
        // The reason ChannelName is a snapshot string and not a foreign key: the entry is written in
        // the same transaction that deletes the channel, and an FK would have cascaded away the only
        // record that the purge ever happened.
        await using var db = fixture.CreateDbContext();
        var service = new ChannelService(db, Substitute.For<IRedisPublisher>());
        await service.JoinAsync("channelserviceaudit3", Actor);

        await service.PurgeAsync("channelserviceaudit3", Actor);

        Assert.Null(await service.GetByNameAsync("channelserviceaudit3"));
        var entries = await LoadAuditEntriesAsync(db, "channelserviceaudit3");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelPurge], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task LeaveAndPurge_ForUnknownChannel_WriteNoAuditEntry()
    {
        // No-ops are not events: both calls return false without touching anything, so the log must
        // stay empty rather than record an action that did not happen.
        await using var db = fixture.CreateDbContext();
        var service = new ChannelService(db, Substitute.For<IRedisPublisher>());

        await service.LeaveAsync("channelserviceaudit4", Actor);
        await service.PurgeAsync("channelserviceaudit4", Actor);

        Assert.Empty(await LoadAuditEntriesAsync(db, "channelserviceaudit4"));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForActiveChannel_PublishesResyncCommand_AndWritesAuditEntry()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelserviceresync1", Actor);

        var result = await service.TriggerResyncAsync("channelserviceresync1", Actor);

        Assert.Equal(ChannelResyncResult.Triggered, result);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "RESYNC:channelserviceresync1", Arg.Any<CancellationToken>());

        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync1");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelResync], entries.Select(e => e.Action));
        var resync = entries[^1];
        Assert.Equal("channelserviceresync1", resync.ChannelName);
        Assert.Equal(Actor.TwitchUserId, resync.ActorTwitchUserId);
        Assert.Equal(Actor.Login, resync.ActorLogin);
    }

    [Fact]
    public async Task TriggerResyncAsync_NormalizesTheChannelName_ForLookupPublishAndAudit()
    {
        // The command payload the worker parses and the audit row must both carry the stored form,
        // not whatever casing/padding the caller typed.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelserviceresync2", Actor);

        var result = await service.TriggerResyncAsync("  ChannelServiceResync2  ", Actor);

        Assert.Equal(ChannelResyncResult.Triggered, result);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "RESYNC:channelserviceresync2", Arg.Any<CancellationToken>());
        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync2");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelResync], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForUnknownChannel_ReturnsNotFound_AndDoesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var result = await service.TriggerResyncAsync("channelserviceresync3", Actor);

        Assert.Equal(ChannelResyncResult.NotFound, result);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(await LoadAuditEntriesAsync(db, "channelserviceresync3"));
    }

    [Fact]
    public async Task TriggerResyncAsync_ForInactiveChannel_ReturnsNotActive_AndDoesNothing()
    {
        // A left channel keeps its row and its history, but resyncing it would subscribe its emote
        // set on the EventAPI while nothing consumes the events — a no-op, so also unaudited.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelserviceresync4", Actor);
        await service.LeaveAsync("channelserviceresync4", Actor);
        redisPublisher.ClearReceivedCalls();

        var result = await service.TriggerResyncAsync("channelserviceresync4", Actor);

        Assert.Equal(ChannelResyncResult.NotActive, result);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var entries = await LoadAuditEntriesAsync(db, "channelserviceresync4");
        Assert.Equal([AuditActions.ChannelJoin, AuditActions.ChannelLeave], entries.Select(e => e.Action));
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(AppDbContext db, string channelName)
    {
        return await db.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }
}
