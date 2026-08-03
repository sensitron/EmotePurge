using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class LiveCoverageServiceTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 8, 3);

    [Fact]
    public async Task AddLiveMinutesAsync_CreatesARowPerLiveChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "livetest_a1");
        var channelB = await SeedChannelAsync(db, "livetest_a2");

        var service = new LiveCoverageService(db);
        var credited = await service.AddLiveMinutesAsync([channelA.ChannelName, channelB.ChannelName], Day, 5);

        Assert.Equal(2, credited);
        var rows = await db.ChannelLiveDays.Where(d => d.Date == Day).ToListAsync();
        Assert.Equal(5, rows.Single(r => r.ChannelId == channelA.Id).LiveMinutes);
        Assert.Equal(5, rows.Single(r => r.ChannelId == channelB.Id).LiveMinutes);
    }

    [Fact]
    public async Task AddLiveMinutesAsync_AccumulatesAcrossPollTicks()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "livetest_accumulate");

        var service = new LiveCoverageService(db);
        await service.AddLiveMinutesAsync([channel.ChannelName], Day, 5);
        await service.AddLiveMinutesAsync([channel.ChannelName], Day, 5);

        var row = await db.ChannelLiveDays.SingleAsync(d => d.ChannelId == channel.Id && d.Date == Day);
        Assert.Equal(10, row.LiveMinutes);
    }

    [Fact]
    public async Task AddLiveMinutesAsync_NormalizesChannelNames()
    {
        // Helix returns lowercase logins, but the contract must not depend on that — the same
        // normalization rule as every other lookup (rule 9).
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "livetest_normalize");

        var service = new LiveCoverageService(db);
        var credited = await service.AddLiveMinutesAsync(["  LiveTest_Normalize  "], Day, 5);

        Assert.Equal(1, credited);
        Assert.Equal(5, (await db.ChannelLiveDays.SingleAsync(d => d.ChannelId == channel.Id && d.Date == Day)).LiveMinutes);
    }

    [Fact]
    public async Task AddLiveMinutesAsync_SkipsUnknownChannelsSilently()
    {
        // A channel can be purged between the worker's channel listing and its poll answer —
        // that must cost nothing, not throw on a dangling FK.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "livetest_unknown");

        var service = new LiveCoverageService(db);
        var credited = await service.AddLiveMinutesAsync([channel.ChannelName, "livetest_no_such_channel"], Day, 5);

        Assert.Equal(1, credited);
        Assert.Single(await db.ChannelLiveDays.Where(d => d.Date == Day && d.ChannelId == channel.Id).ToListAsync());
    }

    [Fact]
    public async Task AddLiveMinutesAsync_ClampsADayToItsOwnLength()
    {
        // An extra poll after a worker restart must not push a day past 1440 minutes — the value
        // feeds "usage per live hour" (stage 2), where >24h of coverage would skew every ratio.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "livetest_clamp");
        db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = channel.Id, Date = Day, LiveMinutes = 1438 });
        await db.SaveChangesAsync();

        var service = new LiveCoverageService(db);
        await service.AddLiveMinutesAsync([channel.ChannelName], Day, 5);

        Assert.Equal(1440, (await db.ChannelLiveDays.SingleAsync(d => d.ChannelId == channel.Id && d.Date == Day)).LiveMinutes);
    }

    [Fact]
    public async Task AddLiveMinutesAsync_ForNoChannelsOrNoMinutes_WritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "livetest_noop");

        var service = new LiveCoverageService(db);
        Assert.Equal(0, await service.AddLiveMinutesAsync([], Day, 5));
        Assert.Equal(0, await service.AddLiveMinutesAsync([channel.ChannelName], Day, 0));

        Assert.Empty(await db.ChannelLiveDays.Where(d => d.ChannelId == channel.Id).ToListAsync());
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }
}
