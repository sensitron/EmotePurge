using EmotePurge.Core.Entities;
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
        new(db, Substitute.For<ISevenTvApiClient>(), cache, new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

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
            .Returns(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes)));
        return new SevenTvSyncService(db, apiClient, cache, new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
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
            .Returns(new SevenTvChannelState(
                "7tv-user-77",
                new SevenTvEmoteSet(SetId, [new SevenTvEmote("e1", "hi", "https://cdn/e1.webp")])));
        var service = new SevenTvSyncService(db, apiClient, cache, new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

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
}
