using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real postgres:16-alpine container, not EF Core InMemory. The conditional-sum
// GroupBy in GetUsageContextAsync only translates cleanly because the query is pre-scoped to a
// plain emote-ID list (see the comment in UsageStatQueryService.cs) — InMemory would happily
// evaluate the naive, untranslatable version client-side and never catch a regression back to it.
[Collection("Postgres")]
public class UsageStatQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetUsageContextAsync_SumsUseCountAcrossDays_WithinRange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "PogChamp");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 5 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 2), UseCount = 7 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 10), UseCount = 100 }); // outside range
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 3));

        var result = Assert.Single(totals);
        Assert.Equal(12, result.TotalUseCount);
    }

    [Fact]
    public async Task GetUsageContextAsync_ZeroFills_ActiveEmotesWithoutUsageStatRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest2");
        await SeedEmoteAsync(db, channel.Id, "NeverUsed");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = Assert.Single(totals);
        Assert.Equal("NeverUsed", result.EmoteName);
        Assert.Equal(0, result.TotalUseCount);
        Assert.Null(result.LastUsedDate);
        Assert.Equal(0, result.PreviousWindowUseCount);
    }

    [Fact]
    public async Task GetUsageContextAsync_Excludes_ArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest3");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 7, 1), UseCount = 42 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetUsageContextAsync_ReturnsEmpty_ForChannelWithNoEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest4");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetUsageContextAsync_LastUsedDate_IsNotBoundedByTheRange()
    {
        // The whole point of the field: "0 uses in the last 7 days" must still be able to say the
        // emote was heavily used a month ago. Clipping the maximum to the range would turn this
        // into a restatement of the total and report almost every emote as never used.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_lastused");
        var emote = await SeedEmoteAsync(db, channel.Id, "Faded");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 5, 4), UseCount = 900 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        var result = Assert.Single(totals);
        Assert.Equal(0, result.TotalUseCount);
        Assert.Equal(new DateOnly(2026, 5, 4), result.LastUsedDate);
    }

    [Fact]
    public async Task GetUsageContextAsync_PreviousWindow_CoversTheEquallyLongPrecedingRange()
    {
        // Range 07-08..07-14 is 7 days inclusive, so the preceding window is 07-01..07-07:
        // previousFrom inclusive, from exclusive. Both boundary days are asserted, because an
        // off-by-one here silently shifts every trend label by a day.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_window");
        var emote = await SeedEmoteAsync(db, channel.Id, "Trending");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 6, 30), UseCount = 1000 }, // before the previous window
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },     // first previous-window day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 7), UseCount = 4 },     // last previous-window day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 8), UseCount = 20 },    // first range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 14), UseCount = 5 });   // last range day
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 14));

        var result = Assert.Single(totals);
        Assert.Equal(25, result.TotalUseCount);
        Assert.Equal(7, result.PreviousWindowUseCount);
        Assert.Equal(new DateOnly(2026, 7, 14), result.LastUsedDate);
    }

    [Fact]
    public async Task GetUsageContextAsync_CarriesFirstSeenAt()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_firstseen");
        var firstSeen = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        await SeedEmoteAsync(db, channel.Id, "Fresh", firstSeenAt: firstSeen);
        await SeedEmoteAsync(db, channel.Id, "Unknown");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(firstSeen, totals.Single(t => t.EmoteName == "Fresh").FirstSeenAt);
        // Null stays null — a row that predates the column must read as "unknown", never as "new".
        Assert.Null(totals.Single(t => t.EmoteName == "Unknown").FirstSeenAt);
    }

    [Fact]
    public async Task GetUsageContextAsync_TranslatesServerSide_ForAFullSizedEmoteSet()
    {
        // 1200 emotes is above HandOfBlood's ~900. A regression to client evaluation would either
        // throw here or drag the whole set through memory; both must fail this test rather than
        // only show up in production.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_large");
        for (var i = 0; i < 1200; i++)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                Name = $"Bulk{i}",
                SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
                ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
            });
        }

        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(1200, totals.Count);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_ReturnsOnlyTheRequestedIds()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballottest1");
        var onBallot = await SeedEmoteAsync(db, channel.Id, "OnBallot");
        var offBallot = await SeedEmoteAsync(db, channel.Id, "OffBallot");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = onBallot.Id, Date = new DateOnly(2026, 7, 2), UseCount = 9 },
            new UsageStat { EmoteId = offBallot.Id, Date = new DateOnly(2026, 7, 2), UseCount = 99 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([onBallot.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(9, Assert.Single(totals).Value);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_OmitsEmotesWithoutUsage()
    {
        // No zero-fill here, unlike the context query: the caller already holds the emote rows and
        // reads a missing key as zero, so filling them in would only make the payload bigger.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballottest2");
        var unused = await SeedEmoteAsync(db, channel.Id, "Unused");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([unused.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_ForNoIds_ReturnsEmptyWithoutQuerying()
    {
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Empty(totals);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(
        AppDbContext db, string channelId, string name, bool isArchived = false, DateTime? firstSeenAt = null)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived,
            FirstSeenAt = firstSeenAt
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }
}
