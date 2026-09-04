using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The sync-gate half of issue #54, against real Postgres: the row a sync reconciles can be renamed
// or merged away by TwitchIdentityReconcileWorker while that sync is still running, and the name
// the caller started from is then no longer the name of the row it is about to write.
//
// The blocked caller is produced by taking the row gate in the test itself rather than by racing two
// syncs — the effect under test is "a second caller cannot enter the row's critical section", and
// who is holding it is irrelevant to that.
[Collection("Postgres")]
public class SevenTvSyncServiceRenameHandoverTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890abcd";

    [Fact]
    public async Task SyncChannel_RowGateHeld_WaitsAndThenWritesTheCacheUnderTheRenamedLogin()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_old");
        var gate = new ChannelSyncGate();
        var cache = new EmoteMatchCache();
        var service = CreateService(db, cache, gate, channel.TwitchChannelId!, LiveEmote("7tv-a", "Alpha"));

        // Stands in for the sync that is already reconciling this row under the channel's other
        // login. The name gate for "handover_old" is free, so only the row gate can hold this back.
        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var syncTask = service.SyncChannelAsync("handover_old");
        await Task.Delay(300);
        Assert.False(syncTask.IsCompleted);

        // The handover commits while the sync sits in the queue — the case that made the old
        // name-only gate write the match cache under a login Twitch no longer routes anywhere.
        await using (var renamer = fixture.CreateDbContext())
        {
            var row = await renamer.Channels.SingleAsync(c => c.Id == channel.Id);
            row.ChannelName = "handover_new";
            await renamer.SaveChangesAsync();
        }

        rowLease.Dispose();
        var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.True(cache.GetChannelEmotes("handover_new").ContainsKey("Alpha"));
        Assert.Empty(cache.GetChannelEmotes("handover_old"));
    }

    [Fact]
    public async Task SyncChannel_RowDeletedWhileWaitingOnTheRowGate_ReturnsNullInsteadOfThrowing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_merged");
        var gate = new ChannelSyncGate();
        var cache = new EmoteMatchCache();
        var service = CreateService(db, cache, gate, channel.TwitchChannelId!, LiveEmote("7tv-b", "Beta"));

        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var syncTask = service.SyncChannelAsync("handover_merged");
        await Task.Delay(300);

        // What a merge does to the losing row. Writing on from here would mean a
        // DbUpdateConcurrencyException on the channel update, or a foreign-key violation on the
        // emote insert.
        await using (var merger = fixture.CreateDbContext())
        {
            await merger.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        rowLease.Dispose();

        Assert.Null(await syncTask.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(cache.GetChannelEmotes("handover_merged"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_RowDeletedWhileWaitingOnTheRowGate_ReportsChannelUnknown()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_delta");
        var gate = new ChannelSyncGate();
        var service = CreateService(db, new EmoteMatchCache(), gate, channel.TwitchChannelId!);

        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var deltaTask = service.ApplyEmoteSetUpdateAsync(
            "handover_delta", SetId, new SevenTvEmoteSetDelta([LiveEmote("7tv-c", "Gamma")], [], []));
        await Task.Delay(300);
        Assert.False(deltaTask.IsCompleted);

        await using (var merger = fixture.CreateDbContext())
        {
            await merger.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        rowLease.Dispose();

        Assert.Equal(SevenTvDeltaOutcome.ChannelUnknown, await deltaTask.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private static SevenTvEmote LiveEmote(string sevenTvId, string name) =>
        new(sevenTvId, name, $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp");

    private static SevenTvSyncService CreateService(
        AppDbContext db, EmoteMatchCache cache, ChannelSyncGate gate, string twitchUserId, params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(twitchUserId, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId, liveEmotes))));
        return new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), gate, NullLogger<SevenTvSyncService>.Instance);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }
}
