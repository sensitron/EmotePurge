using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real postgres:16-alpine container, not EF Core InMemory. GetUsageTotalsAsync's
// GroupBy+Sum only translates cleanly because the query is pre-scoped to a plain emote-ID list
// (see the comment in UsageStatQueryService.cs) — InMemory would happily evaluate the naive,
// untranslatable version client-side and never catch a regression back to it.
[Collection("Postgres")]
public class UsageStatQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetUsageTotalsAsync_SumsUseCountAcrossDays_WithinRange()
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
        var totals = await service.GetUsageTotalsAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 3));

        var result = Assert.Single(totals);
        Assert.Equal(12, result.TotalUseCount);
    }

    [Fact]
    public async Task GetUsageTotalsAsync_ZeroFills_ActiveEmotesWithoutUsageStatRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest2");
        await SeedEmoteAsync(db, channel.Id, "NeverUsed");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageTotalsAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = Assert.Single(totals);
        Assert.Equal("NeverUsed", result.EmoteName);
        Assert.Equal(0, result.TotalUseCount);
    }

    [Fact]
    public async Task GetUsageTotalsAsync_Excludes_ArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest3");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 7, 1), UseCount = 42 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageTotalsAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetUsageTotalsAsync_ReturnsEmpty_ForChannelWithNoEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest4");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageTotalsAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelId, string name, bool isArchived = false)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }
}
