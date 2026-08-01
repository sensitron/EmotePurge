using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against real Postgres like the rest of the query services: the occupied-slot count is a COUNT
// with a filter over a real index, and the "no capacity reported" case has to survive a round trip
// through a nullable column rather than only through an object in memory.
[Collection("Postgres")]
public class EmoteSetStatusServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetAsync_CountsOnlyActiveEmotesAsOccupiedSlots()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest1", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "One");
        await SeedEmoteAsync(db, channel.Id, "Two");
        await SeedEmoteAsync(db, channel.Id, "AlreadyDeleted", isArchived: true);

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(2, status.OccupiedSlots);
        Assert.Equal(1000, status.Capacity);
    }

    [Fact]
    public async Task GetAsync_UnreportedCapacity_StaysNull()
    {
        // The consumer distinguishes "7TV reported no limit" from a number and renders no bar at
        // all — a 0 or an invented 1000 here would both be a lie about how full the set is.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest2", capacity: null);
        await SeedEmoteAsync(db, channel.Id, "One");

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.Capacity);
        Assert.Equal(1, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_TrackedSince_PrefersTheRejoinOverTheCreation()
    {
        // A channel that was left and rejoined has a gap in its usage history. CreatedAt would
        // claim we counted through it.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest3", capacity: 1000);
        channel.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        channel.TrackingResumedAt = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), status.TrackedSince);
    }

    [Fact]
    public async Task GetAsync_NeverRejoined_FallsBackToTheCreation()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest4", capacity: 1000);
        channel.CreatedAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), status.TrackedSince);
    }

    [Fact]
    public async Task GetAsync_BeforeTheFirstSync_ReportsNoOccupiedSlots()
    {
        // The page polls this endpoint in a loop while waiting for the first sync; with no set id
        // there is nothing to count and the query is skipped entirely.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest5", capacity: null, activeEmoteSetId: "");

        var status = await new EmoteSetStatusService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(string.Empty, status.ActiveEmoteSetId);
        Assert.Equal(0, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_NormalizesTheChannelName()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest6", capacity: 600);
        await SeedEmoteAsync(db, channel.Id, "One");

        var status = await new EmoteSetStatusService(db).GetAsync("  SlotsTest6 ");

        Assert.NotNull(status);
        Assert.Equal(1, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_UntrackedChannel_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        var status = await new EmoteSetStatusService(db).GetAsync("slotstest_missing");

        Assert.Null(status);
    }

    private static async Task<Channel> SeedChannelAsync(
        AppDbContext db, string channelName, int? capacity, string activeEmoteSetId = "64c9e0f0aa1234567890abcd")
    {
        var channel = new Channel
        {
            ChannelName = channelName,
            IsBotActive = true,
            ActiveEmoteSetId = activeEmoteSetId,
            ActiveEmoteSetCapacity = capacity
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task SeedEmoteAsync(AppDbContext db, string channelId, string name, bool isArchived = false)
    {
        db.Emotes.Add(new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived
        });
        await db.SaveChangesAsync();
    }
}
