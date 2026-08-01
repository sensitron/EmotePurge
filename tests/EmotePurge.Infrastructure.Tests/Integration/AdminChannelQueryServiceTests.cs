using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real postgres:16-alpine, not EF Core InMemory: ListAsync's two GroupBy aggregates (Count with a
// predicate, Max over a nullable projection) are exactly the shape InMemory happily evaluates
// client-side while Npgsql refuses to translate it — see the Regel-10 comment in the service.
//
// The fixture's database is shared by the whole "Postgres" collection and ListAsync is deliberately
// global (it lists *every* channel), so no assertion here may assume the list contains only this
// class's rows. Every test filters by its own channel-name prefix; the ordering test is phrased over
// the full list, which stays valid no matter what else is seeded.
[Collection("Postgres")]
public class AdminChannelQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListAsync_ReportsEmoteAndVoteSessionAggregates_PerChannel()
    {
        await using var db = fixture.CreateDbContext();

        var withData = await SeedChannelAsync(db, "adminaggregatefull", isBotActive: true, twitchChannelId: "4711");
        await SeedEmoteAsync(db, withData.Id, "Active1", lastSyncedAt: new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));
        await SeedEmoteAsync(db, withData.Id, "Active2", lastSyncedAt: new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        await SeedEmoteAsync(db, withData.Id, "Gone", isArchived: true, lastSyncedAt: new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc));
        await SeedVoteSessionAsync(db, withData.Id, isActive: true);
        await SeedVoteSessionAsync(db, withData.Id, isActive: false);
        await SeedVoteSessionAsync(db, withData.Id, isActive: false);

        // A second channel proves the aggregates are grouped per channel instead of summed globally.
        var neighbour = await SeedChannelAsync(db, "adminaggregateneighbour", isBotActive: false);
        await SeedEmoteAsync(db, neighbour.Id, "Solo");

        var service = new AdminChannelQueryService(db);
        var all = await service.ListAsync();

        var row = Assert.Single(all, c => c.ChannelName == "adminaggregatefull");
        Assert.Equal("4711", row.TwitchChannelId);
        Assert.True(row.IsBotActive);
        Assert.Equal(3, row.EmoteCount);          // total, archived included
        Assert.Equal(1, row.ArchivedEmoteCount);  // subset of the above
        Assert.Equal(3, row.VoteSessionCount);
        Assert.Equal(1, row.ActiveVoteSessionCount);
        Assert.Equal(new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc), row.LastInventoryChangeUtc);

        var neighbourRow = Assert.Single(all, c => c.ChannelName == "adminaggregateneighbour");
        Assert.False(neighbourRow.IsBotActive);
        Assert.Null(neighbourRow.TwitchChannelId);
        Assert.Equal(1, neighbourRow.EmoteCount);
        Assert.Equal(0, neighbourRow.ArchivedEmoteCount);
        Assert.Equal(0, neighbourRow.VoteSessionCount);
    }

    [Fact]
    public async Task ListAsync_ZeroFills_ChannelWithoutEmotesOrVoteSessions()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "adminaggregateempty");

        var service = new AdminChannelQueryService(db);
        var all = await service.ListAsync();

        // A freshly joined channel must still be a row — an inner join would have dropped it, and
        // dropping it is precisely the case where an admin needs to see it (nothing synced yet).
        var row = Assert.Single(all, c => c.ChannelName == "adminaggregateempty");
        Assert.Equal(0, row.EmoteCount);
        Assert.Equal(0, row.ArchivedEmoteCount);
        Assert.Equal(0, row.VoteSessionCount);
        Assert.Equal(0, row.ActiveVoteSessionCount);
        // Null, not DateTime.MinValue: "never synced" and "synced at the epoch" are different states.
        Assert.Null(row.LastInventoryChangeUtc);
        Assert.Null(row.LastSyncedAtUtc);
    }

    [Fact]
    public async Task ListAsync_ReportsLastSync_SeparatelyFromLastInventoryChange()
    {
        await using var db = fixture.CreateDbContext();

        // The whole point of the split: a channel synced a minute ago whose emote set nobody has
        // touched in three months. Reported as one number, this used to read as "the bot stopped
        // syncing three months ago" — the exact wrong conclusion on a support page.
        var channel = await SeedChannelAsync(db, "adminsyncsplit");
        channel.LastSyncedAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        await SeedEmoteAsync(db, channel.Id, "Ancient", lastSyncedAt: new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));

        var service = new AdminChannelQueryService(db);
        var row = Assert.Single(await service.ListAsync(), c => c.ChannelName == "adminsyncsplit");

        Assert.Equal(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), row.LastSyncedAtUtc);
        Assert.Equal(new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc), row.LastInventoryChangeUtc);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheSameRowAsTheList_ForOneChannel()
    {
        await using var db = fixture.CreateDbContext();

        var channel = await SeedChannelAsync(db, "admindrilldown", twitchChannelId: "9001");
        channel.ActiveEmoteSetId = "01HSET";
        channel.ActiveEmoteSetCapacity = 600;
        await db.SaveChangesAsync();
        await SeedEmoteAsync(db, channel.Id, "One");
        await SeedEmoteAsync(db, channel.Id, "Two", isArchived: true);
        await SeedVoteSessionAsync(db, channel.Id, isActive: true);

        var service = new AdminChannelQueryService(db);
        var single = await service.GetAsync("AdminDrilldown");   // Regel 9: normalized on the way in

        Assert.NotNull(single);
        // Compared against the list row rather than re-asserted field by field: the drilldown
        // disagreeing with the row the admin clicked is the failure worth pinning.
        var fromList = Assert.Single(await service.ListAsync(), c => c.ChannelName == "admindrilldown");
        Assert.Equal(fromList, single);

        Assert.Equal("01HSET", single.ActiveEmoteSetId);
        Assert.Equal(600, single.ActiveEmoteSetCapacity);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForAnUnknownChannel()
    {
        await using var db = fixture.CreateDbContext();
        var service = new AdminChannelQueryService(db);

        Assert.Null(await service.GetAsync("adminneverjoinedanything"));
    }

    [Fact]
    public async Task GetAsync_ReportsAnEmptyActiveSetIdAsNull()
    {
        await using var db = fixture.CreateDbContext();
        // The column defaults to "" for a channel that has never synced. An empty string travelling
        // to the UI as an emote-set id would render as a set that exists and is nameless.
        await SeedChannelAsync(db, "adminneversynced");

        var service = new AdminChannelQueryService(db);
        var row = await service.GetAsync("adminneversynced");

        Assert.NotNull(row);
        Assert.Null(row.ActiveEmoteSetId);
    }

    [Fact]
    public async Task ListActiveChannelNamesAsync_ReturnsOnlyActiveChannels()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "adminrosteractive", isBotActive: true);
        await SeedChannelAsync(db, "adminrosterleft", isBotActive: false);

        var service = new AdminChannelQueryService(db);
        var names = await service.ListActiveChannelNamesAsync();

        // A channel the bot has left is not "missing from the roster" — it is correctly absent, and
        // including it here would make the roster card report a permanent deficit.
        Assert.Contains("adminrosteractive", names);
        Assert.DoesNotContain("adminrosterleft", names);
    }

    [Fact]
    public async Task ListAsync_OrdersByChannelName_Ascending()
    {
        await using var db = fixture.CreateDbContext();
        // Seeded out of order on purpose, so a missing OrderBy would surface as insertion order.
        await SeedChannelAsync(db, "adminorderzulu");
        await SeedChannelAsync(db, "adminorderalpha");

        var service = new AdminChannelQueryService(db);
        var all = await service.ListAsync();

        // Scoped to this test's own rows: the shared database also holds other classes' channels,
        // and Postgres orders those under its own collation, which need not match .NET's ordinal
        // comparison for names containing digits or underscores.
        var names = all.Select(c => c.ChannelName).Where(n => n.StartsWith("adminorder")).ToList();
        Assert.Equal(["adminorderalpha", "adminorderzulu"], names);
    }

    private static async Task<Channel> SeedChannelAsync(
        AppDbContext db, string channelName, bool isBotActive = true, string? twitchChannelId = null)
    {
        var channel = new Channel
        {
            ChannelName = channelName,
            IsBotActive = isBotActive,
            TwitchChannelId = twitchChannelId
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(
        AppDbContext db, string channelId, string name, bool isArchived = false, DateTime? lastSyncedAt = null)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived,
            LastSyncedAt = lastSyncedAt ?? DateTime.UtcNow
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<VoteSession> SeedVoteSessionAsync(AppDbContext db, string channelId, bool isActive)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = isActive ? "Laufend" : "Beendet",
            IsActive = isActive,
            EndedAt = isActive ? null : DateTime.UtcNow
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}
