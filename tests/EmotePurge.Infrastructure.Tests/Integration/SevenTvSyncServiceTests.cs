using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Covers the EventAPI delta path (ApplyEmoteSetUpdateAsync) against real Postgres — the unique
// (ChannelId, SevenTvEmoteId) index and the change-tracker-based no-op detection are exactly the
// parts an in-memory provider would fake away. The REST full-sync path is additionally covered
// where the two share behaviour (SevenTvUserId pass-through).
[Collection("Postgres")]
public class SevenTvSyncServiceTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890abcd";

    private static SevenTvEmoteSetDelta Delta(
        IReadOnlyList<SevenTvEmote>? pushed = null,
        IReadOnlyList<SevenTvEmote>? updated = null,
        IReadOnlyList<string>? pulledIds = null) =>
        new(pushed ?? [], updated ?? [], pulledIds ?? []);

    private static SevenTvSyncService CreateService(Persistence.AppDbContext db, EmoteMatchCache cache) =>
        new(db, Substitute.For<ISevenTvApiClient>(), cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

    // The REST answer a seeded channel would get back unchanged — same set, same emotes, same
    // image urls, so a sync over it is a true no-op.
    private static SevenTvSyncService CreateRestService(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        string emoteSetId,
        params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    // Same as CreateRestService, but with an explicit set capacity. Separate method because a
    // params array has to stay last, so the capacity could not be an optional parameter in front
    // of it without breaking every positional call above.
    private static SevenTvSyncService CreateRestServiceWithCapacity(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        string emoteSetId,
        int? capacity,
        params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes, capacity))));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    private static string SeededImageUrl(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    private static SevenTvEmote LiveEmote(string sevenTvId, string name) => new(sevenTvId, name, SeededImageUrl(sevenTvId));

    private async Task<Channel> SeedChannelAsync(Persistence.AppDbContext db, string name, params (string SevenTvId, string Name, bool Archived)[] emotes)
    {
        // Channels.TwitchChannelId carries a unique index — derive it from the (unique) test
        // channel name instead of sharing one literal across tests.
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, emoteName, archived) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = emoteName,
                ImageUrl = SeededImageUrl(sevenTvId),
                IsArchived = archived
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }

    [Fact]
    public async Task SyncChannel_WithDuplicateActiveNames_CoalescesOntoOneIdAndRecordsTheCollision()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dupsync1");
        var cache = new EmoteMatchCache();
        var tracker = new DuplicateEmoteNameTracker();
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId,
                [LiveEmote("7tv-dup-a", "Dup"), LiveEmote("7tv-dup-b", "Dup"), LiveEmote("7tv-solo", "Solo")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, tracker, new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        await service.SyncChannelAsync(channel.ChannelName);

        var cached = cache.GetChannelEmotes(channel.ChannelName);
        Assert.Equal(2, cached.Count);
        Assert.True(cached.ContainsKey("Dup"));
        Assert.True(cached.ContainsKey("Solo"));
        // The collision was recorded during the sync: reporting the same set again is no change.
        Assert.False(tracker.Update(channel.ChannelName, ["Dup"]));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pushed_AddsEmoteAndRefreshesCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_pushed", ("e1", "existing", false));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e2", "catJAM", "https://cdn/e2.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2");
        Assert.Equal("catJAM", row.Name);
        Assert.False(row.IsArchived);
        Assert.True(cache.GetChannelEmotes(channel.ChannelName).ContainsKey("catJAM"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pulled_ArchivesEmoteAndDropsItFromCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_pulled", ("e1", "keepme", false), ("e2", "removeme", false));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["e2"]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2");
        Assert.True(row.IsArchived);
        var cached = cache.GetChannelEmotes(channel.ChannelName);
        Assert.True(cached.ContainsKey("keepme"));
        Assert.False(cached.ContainsKey("removeme"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PushedArchivedEmote_Unarchives()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_unarchive", ("e1", "phoenix", true));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e1", "phoenix", "https://cdn/e1.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.False(row.IsArchived);
        Assert.True(cache.GetChannelEmotes(channel.ChannelName).ContainsKey("phoenix"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UpdatedRename_ReplacesNameInDbAndCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_rename", ("e1", "oldname", false));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(updated: [new SevenTvEmote("e1", "newname", "https://cdn/e1.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.Equal("newname", row.Name);
        var cached = cache.GetChannelEmotes(channel.ChannelName);
        Assert.True(cached.ContainsKey("newname"));
        Assert.False(cached.ContainsKey("oldname"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_DeltaWipingWholeSet_IsSkippedAsImplausible()
    {
        // Same asymmetry as the empty-set guard in SyncChannelAsync (S3-12): one malformed delta
        // must not stop chat matching for the whole channel. The non-effect is the assertion.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_wipe",
            ("e1", "one", false), ("e2", "two", false), ("e3", "three", false));
        var service = CreateService(db, cache);
        cache.ReplaceChannel(channel.ChannelName, new Dictionary<string, string> { ["one"] = "x" });

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pulledIds: ["e1", "e2", "e3"]));

        Assert.Equal(SevenTvDeltaOutcome.ImplausibleSkipped, outcome);
        Assert.Equal(0, await db.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.IsArchived));
        Assert.True(cache.GetChannelEmotes(channel.ChannelName).ContainsKey("one"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_LegitimatelyEmptyChannel_IsNotBlockedByTheGuard()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_empty");
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["unknown"]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, outcome);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_ForNoLongerActiveSet_AppliesNothing()
    {
        // A still-live subscription on a set the channel switched away from must not write.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_setswitch", ("e1", "stale", false));
        channel.ActiveEmoteSetId = "0000e0f0aa1234567890ffff";
        await db.SaveChangesAsync();
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e9", "ghost", "https://cdn/e9.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.SetNotActive, outcome);
        Assert.False(await db.Emotes.AnyAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e9"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UnknownChannel_ReportsChannelUnknown()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, new EmoteMatchCache());

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            "wstest_missing", SetId, Delta(pulledIds: ["e1"]));

        Assert.Equal(SevenTvDeltaOutcome.ChannelUnknown, outcome);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_SharedSet_AppliesToEachChannelWithoutIndexConflict()
    {
        // Two tracked channels sharing one active set is a real configuration (observed live);
        // the same delta lands as one row per channel on the (ChannelId, SevenTvEmoteId) index.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channelA = await SeedChannelAsync(db, "wstest_shared_a");
        var channelB = await SeedChannelAsync(db, "wstest_shared_b");
        var service = CreateService(db, cache);
        var delta = Delta(pushed: [new SevenTvEmote("e7", "sharedjam", "https://cdn/e7.webp")]);

        Assert.Equal(SevenTvDeltaOutcome.Applied, await service.ApplyEmoteSetUpdateAsync(channelA.ChannelName, SetId, delta));
        Assert.Equal(SevenTvDeltaOutcome.Applied, await service.ApplyEmoteSetUpdateAsync(channelB.ChannelName, SetId, delta));

        Assert.Equal(2, await db.Emotes.CountAsync(e => e.SevenTvEmoteId == "e7"));
        Assert.True(cache.GetChannelEmotes(channelA.ChannelName).ContainsKey("sharedjam"));
        Assert.True(cache.GetChannelEmotes(channelB.ChannelName).ContainsKey("sharedjam"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_EmptyDispatchImageUrl_DoesNotOverwriteKnownUrl()
    {
        // Dispatch payloads have not been proven to always carry the image-host block.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_imageurl", ("e1", "pic", false));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(updated: [new SevenTvEmote("e1", "pic_renamed", "")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.Equal("pic_renamed", row.Name);
        Assert.Equal("https://cdn.7tv.app/emote/e1/2x.webp", row.ImageUrl);
    }

    [Fact]
    public async Task SyncChannel_PassesSevenTvUserIdThrough()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_syncresult", TwitchChannelId = "77", ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync("77", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState(
                "7tv-user-77",
                new SevenTvEmoteSet(SetId, [new SevenTvEmote("e1", "hi", "https://cdn/e1.webp")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync("wstest_syncresult");

        Assert.NotNull(result);
        Assert.Equal(SetId, result.EmoteSetId);
        Assert.Equal("7tv-user-77", result.SevenTvUserId);
    }

    // HasChanges is what keeps the unattended sync paths (periodic resync every 60s, EventAPI
    // follow-ups, boot recovery) from publishing channel.synced on every tick — a false positive
    // would make every open page refetch on a timer.
    [Fact]
    public async Task SyncChannel_NoOpResync_ReportsNoChanges()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_noop", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_NewEmote_ReportsChange()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_new", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"), LiveEmote("e2", "fresh"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        Assert.True(await db.Emotes.AnyAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2"));
    }

    [Fact]
    public async Task SyncChannel_EmoteMissingFromLiveSet_ArchivesAndReportsChange()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_gone", ("e1", "stays", false), ("e2", "goes", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stays"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        Assert.True(await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_SwitchedActiveSetWithIdenticalEmotes_ReportsChange()
    {
        // The emote rows stay byte-identical, but which set the channel runs is itself state the
        // UI shows (and the mass-delete panel needs) — so it counts as a change.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_setswitch", ("e1", "stable", false));
        const string OtherSetId = "1111e0f0aa1234567890abcd";
        var service = CreateRestService(db, cache, channel, OtherSetId, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.Equal(OtherSetId, result.EmoteSetId);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_ReportsNoChanges()
    {
        // The guard skips the write entirely; reporting a change would publish an event for a sync
        // that deliberately did nothing.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_emptyguard", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.False(await db.Emotes.Where(e => e.ChannelId == channel.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_PersistsTheSetCapacity()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity", ("e1", "stable", false));
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 1500, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(1500, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_UnreportedCapacity_StaysNull()
    {
        // "7TV told us nothing" must stay distinguishable from a number — the UI shows no budget
        // bar at all rather than inventing a denominator.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_null", ("e1", "stable", false));
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, null, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_CapacityChangeAlone_ReportsNoChanges()
    {
        // A resized set is not a changed inventory. If this reported a change, the resize would
        // make every open page of the channel refetch for nothing.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_nochange", ("e1", "stable", false));
        channel.ActiveEmoteSetCapacity = 1000;
        await db.SaveChangesAsync();
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 1500, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.Equal(1500, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_NoOpResync_StillRecordsTheSuccessfulSync()
    {
        // The whole reason LastSyncedAtUtc exists as its own column: a sync that changed nothing is
        // still a sync that reached 7TV. Emote.LastSyncedAt does not move here, so the admin view
        // would otherwise keep showing the last inventory change as if it were the last sync.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_lastsync_noop", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        var before = DateTime.UtcNow;
        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        var lastSynced = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.LastSyncedAtUtc).SingleAsync();
        Assert.NotNull(lastSynced);
        Assert.True(lastSynced >= before);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_DoesNotRecordASuccessfulSync()
    {
        // The empty-set guard skips the reconciliation entirely, so nothing was verified against
        // 7TV's real state. Stamping it as a successful sync would report a channel as healthily
        // syncing precisely while its syncs are being thrown away.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_lastsync_emptyguard", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.LastSyncedAtUtc).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_SetsFirstSeenAt_FromTheSetEntryTimestamp_OnInsert()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_insert", ("e1", "stable", false));
        var addedAt = new DateTime(2026, 7, 20, 8, 30, 0, DateTimeKind.Utc);
        var service = CreateRestService(
            db, cache, channel, SetId,
            LiveEmote("e1", "stable"),
            new SevenTvEmote("e2", "fresh", SeededImageUrl("e2"), addedAt));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(addedAt, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_BackfillsFirstSeenAt_ForARowThatPredatesTheColumn()
    {
        // Taking the set entry's own timestamp rather than "when we first saw it" is what makes the
        // date correct for emotes that have been in the set for months.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_backfill", ("e1", "stable", false));
        var addedAt = new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc);
        var service = CreateRestService(db, cache, channel, SetId, new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), addedAt));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(addedAt, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_BackfillingFirstSeenAt_ReportsNoChanges()
    {
        // The expensive mistake of this feature: counted as an inventory change, the first resync
        // after the deploy would publish channel.synced for every channel at once and make every
        // open page in the app refetch — for a date nobody's list is sorted by.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_nochange", ("e1", "stable", false));
        var service = CreateRestService(
            db, cache, channel, SetId,
            new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc)));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_MissingTimestamp_LeavesAKnownFirstSeenAtAlone()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_keep", ("e1", "stable", false));
        var known = new DateTime(2026, 1, 9, 6, 0, 0, DateTimeKind.Utc);
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FirstSeenAt, known));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(known, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_CorrectsAKnownWrongFirstSeenAt_WithoutCountingItAsAChange()
    {
        // The column was historically filled from the v3 payload's timestamp — the emote's upload
        // date, not the set-entry date. A differing live value is therefore a correction, and like
        // the original backfill it must not count as an inventory change (channel.synced storm).
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_correct", ("e1", "stable", false));
        var wrong = new DateTime(2023, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FirstSeenAt, wrong));
        var corrected = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var service = CreateRestService(
            db, cache, channel, SetId, new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), corrected));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.Equal(corrected, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PushedInsert_StampsFirstSeenAtWithNow()
    {
        // Dispatch payloads carry no trustworthy added-at (the v3-shaped timestamp is the upload
        // date), but a push IS the join moment — so the insert must be stamped with "now" instead
        // of arriving dateless and waiting a resync tick for v4.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_push", ("e1", "stable", false));
        var service = CreateService(db, cache);
        var before = DateTime.UtcNow.AddSeconds(-1);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [LiveEmote("e2", "fresh")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, outcome);
        var firstSeen = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.FirstSeenAt).SingleAsync();
        Assert.NotNull(firstSeen);
        Assert.InRange(firstSeen.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_NoOpDispatch_StaysNoChange_EvenWhenItCouldBackfill()
    {
        // The dispatch path decides NoChange vs Applied by asking the ChangeTracker, so it must not
        // backfill at all — the periodic resync fills the same gap within a tick without turning a
        // no-op dispatch into a live event for every open page.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_dispatch", ("e1", "stable", false));
        var service = CreateService(db, cache);

        var outcome = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName,
            SetId,
            Delta(updated: [new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc))]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, outcome);
        Assert.Null(await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_ArchivingViaRestReconcile_StampsArchivedAt()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_rest", ("e1", "stays", false), ("e2", "goes", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stays"));

        var before = DateTime.UtcNow;
        await service.SyncChannelAsync(channel.ChannelName);

        var archivedAt = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pulled_StampsArchivedAt()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_pull", ("e1", "keepme", false), ("e2", "removeme", false));
        var service = CreateService(db, cache);

        var before = DateTime.UtcNow;
        await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["e2"]));

        var archivedAt = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UnarchivingPush_ClearsArchivedAt()
    {
        // The restore path (A6) relies on exactly this: a re-added emote heals to "not archived,
        // no archive date" through the ordinary sync, no dedicated endpoint involved.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_clear", ("e1", "phoenix", true));
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ArchivedAt, DateTime.UtcNow.AddDays(-1)));
        var service = CreateService(db, cache);

        await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e1", "phoenix", SeededImageUrl("e1"))]));

        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_DoesNotWriteCapacity()
    {
        // The empty-set guard returns before the set id is assigned, and the capacity has to share
        // that fate: a partial 7TV outage must not leave a wrong slot limit behind.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_guard", ("e1", "stable", false));
        channel.ActiveEmoteSetCapacity = 1000;
        await db.SaveChangesAsync();
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 7);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(1000, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    // ---- Warum ein Sync nichts geliefert hat (Issue #32) ----

    // Builds a sync service whose 7TV client fails with a given status, so the four outcomes of the
    // analysis can be driven one by one. Separate from CreateRestService, which only knows success.
    private static SevenTvSyncService CreateFailingService(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        SevenTvLookupStatus status)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Failed(status));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    [Theory]
    [InlineData(SevenTvLookupStatus.NoActiveEmoteSet, "no_active_emote_set")]
    [InlineData(SevenTvLookupStatus.NoSevenTvAccount, "no_seventv_account")]
    [InlineData(SevenTvLookupStatus.Unavailable, "seventv_unavailable")]
    public async Task SyncChannel_NoActiveEmoteSet_PersistsTheReason(SevenTvLookupStatus status, string expectedReason)
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, $"wstest_reason_{expectedReason}");
        var service = CreateFailingService(db, cache, channel, status);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Equal(expectedReason, row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncAttemptAtUtc);
        // LastSyncedAtUtc keeps meaning "last *successful* sync" — a failed attempt must not
        // advance it, or the admin drilldown would report a healthy sync for a broken channel.
        Assert.Null(row.LastSyncedAtUtc);
    }

    [Fact]
    public async Task SyncChannel_FailedAttempt_LeavesTheKnownSetAndItsEmotesAlone()
    {
        // The asymmetry that governs this whole area: a 7TV outage must not take the mass-delete
        // panel away or archive a channel's entire set. A failure records *why*, and nothing else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_keeps", ("e1", "stable", false));
        var service = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.ActiveEmoteSetId, c.ActiveEmoteSetCapacity })
            .SingleAsync();
        Assert.Equal(SetId, row.ActiveEmoteSetId);
        Assert.False(await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_Success_ClearsAPreviousReason()
    {
        // The half that gets forgotten. A channel that activated an emote set on 7TV must stop being
        // told it has none — otherwise the empty state outlives the problem it describes.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_cleared");
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "fresh"));

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Null(row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncedAtUtc);
        // Attempt and success are stamped from the same instant on a successful run, so the pair
        // reads as "current" rather than leaving a stale attempt behind an up-to-date success.
        Assert.Equal(row.LastSyncedAtUtc, row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_TouchesNeitherReasonNorAttempt()
    {
        // The empty-set guard (S3-12) deliberately makes no statement about the channel: it neither
        // succeeded nor failed, it declined to act. Writing an attempt timestamp there would claim a
        // reconciliation that never happened, and clearing a reason would hide a real one.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_guard", ("e1", "stable", false));
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.Unavailable;
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc })
            .SingleAsync();
        Assert.Equal("seventv_unavailable", row.LastSyncFailureReason);
        Assert.Null(row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_UnresolvableTwitchId_RecordsTheMissingAccount()
    {
        // The pre-step: a channel whose TwitchChannelId was never backfilled resolves it through
        // 7TV's own user search. Its "no match" answer is a missing 7TV account, not a network
        // problem, and used to vanish into the same null as everything else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_reason_noid", TwitchChannelId = null, ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.ResolveTwitchUserIdAsync("wstest_reason_noid", Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync("wstest_reason_noid");

        Assert.Null(result);
        Assert.Equal("no_seventv_account", await db.Channels
            .Where(c => c.ChannelName == "wstest_reason_noid")
            .Select(c => c.LastSyncFailureReason).SingleAsync());
    }
}
