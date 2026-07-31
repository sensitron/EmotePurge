using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The Postgres collection shares one database across test classes, so every assertion here filters
/// down to this class's own channel-name prefix instead of reading the whole table — a second test
/// class writing entries must not be able to shift this one's pages.
/// </summary>
[Collection("Postgres")]
public class AuditLogQueryServiceTests(PostgresFixture fixture)
{
    private const string ChannelPrefix = "auditquery";

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-order";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 29, 10, 0, 0, DateTimeKind.Utc)),
            (AuditActions.ChannelLeave, new DateTime(2099, 7, 31, 10, 0, 0, DateTimeKind.Utc)),
            (AuditActions.ChannelPurge, new DateTime(2099, 7, 30, 10, 0, 0, DateTimeKind.Utc)));

        var page = await new AuditLogQueryService(db).ListAsync(1, 50);

        var actions = page.Items.Where(i => i.ChannelName == channel).Select(i => i.Action).ToList();
        Assert.Equal([AuditActions.ChannelLeave, AuditActions.ChannelPurge, AuditActions.ChannelJoin], actions);
    }

    [Fact]
    public async Task ListAsync_BreaksTimestampTiesById_SoNoRowAppearsOnTwoPages()
    {
        // Entries written inside one transaction share a timestamp to the tick. Without the Id
        // tiebreaker, Postgres may order them differently per query and Skip/Take would then return
        // the same row twice while dropping another entirely.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-ties";
        var sameInstant = new DateTime(2099, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, sameInstant),
            (AuditActions.ChannelLeave, sameInstant),
            (AuditActions.ChannelPurge, sameInstant));

        var service = new AuditLogQueryService(db);
        var all = await service.ListAsync(1, 50);
        var expected = all.Items.Where(i => i.ChannelName == channel).Select(i => i.Id).ToList();

        // Newest (highest Id) first, because the timestamps cannot decide it.
        Assert.Equal(expected.OrderByDescending(id => id), expected);
        Assert.Equal(3, expected.Count);
    }

    [Fact]
    public async Task ListAsync_PagesWithoutOverlap_AndReportsTotals()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-paging";
        var baseTime = new DateTime(2099, 7, 31, 8, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel, Enumerable.Range(0, 5)
            .Select(i => (AuditActions.ChannelJoin, baseTime.AddMinutes(i)))
            .ToArray());

        var service = new AuditLogQueryService(db);
        var first = await service.ListAsync(1, 2);
        var second = await service.ListAsync(2, 2);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        // TotalCount is the whole table, not this channel's slice — the admin log is unfiltered today.
        Assert.True(first.TotalCount >= 5);
        Assert.Equal((int)Math.Ceiling(first.TotalCount / 2.0), first.TotalPages);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task ListAsync_ProjectsEveryField_IncludingRawDetailsJson()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-fields";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 9, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncDeleted,
            ChannelName = channel,
            TargetType = "voteSession",
            TargetId = "42",
            DetailsJson = """{"emoteCount": 12}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db).ListAsync(1, 100);

        var dto = Assert.Single(page.Items, i => i.ChannelName == channel);
        Assert.Equal("4711", dto.ActorTwitchUserId);
        Assert.Equal("sensitron", dto.ActorLogin);
        Assert.Equal(AuditActions.EmotesSyncDeleted, dto.Action);
        Assert.Equal("voteSession", dto.TargetType);
        Assert.Equal("42", dto.TargetId);
        // Handed to the client verbatim (jsonb round-trip aside) — the UI parses it defensively.
        Assert.Contains("\"emoteCount\"", dto.DetailsJson);
    }

    [Fact]
    public async Task ListAsync_FiltersByAction_AndCountsOnlyTheFilteredSet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-actionfilter";
        var baseTime = new DateTime(2099, 7, 31, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, baseTime),
            (AuditActions.ChannelLeave, baseTime.AddMinutes(1)),
            (AuditActions.ChannelJoin, baseTime.AddMinutes(2)));

        // Channel narrows to this test's rows; action is the filter under test.
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(AuditActions.ChannelJoin, channel, null));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.All(page.Items, i => Assert.Equal(AuditActions.ChannelJoin, i.Action));
        Assert.All(page.Items, i => Assert.Equal(channel, i.ChannelName));
    }

    [Fact]
    public async Task ListAsync_NormalizesTheChannelFilter_BeforeMatching()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-normalize";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 31, 14, 0, 0, DateTimeKind.Utc)));

        // Raw admin input: Twitch names get typed with capitals and stray whitespace (Regel 9).
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, $"  {channel.ToUpperInvariant()}  ", null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(channel, dto.ChannelName);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_FiltersActorBySubstring_CaseInsensitively()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-actorfilter";
        var baseTime = new DateTime(2099, 7, 31, 15, 0, 0, DateTimeKind.Utc);
        db.AuditLogEntries.AddRange(
            NewEntry(channel, AuditActions.ChannelJoin, baseTime, actorLogin: "handofblood"),
            NewEntry(channel, AuditActions.ChannelJoin, baseTime.AddMinutes(1), actorLogin: "sensitron"));
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, "OFBLO"));

        var dto = Assert.Single(page.Items);
        Assert.Equal("handofblood", dto.ActorLogin);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_TreatsBlankFilterValues_AsNoFilter()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-blank";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 31, 16, 0, 0, DateTimeKind.Utc)));

        // The endpoint already nulls blank query params; the service still must not turn a stray
        // whitespace value into a filter that matches nothing.
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 100, new AuditLogFilter("   ", channel, "   "));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditActions.ChannelJoin, dto.Action);
    }

    private static AuditLogEntry NewEntry(string channelName, string action, DateTime occurredAtUtc, string actorLogin)
        => new()
        {
            OccurredAtUtc = occurredAtUtc,
            ActorTwitchUserId = "4711",
            ActorLogin = actorLogin,
            Action = action,
            ChannelName = channelName
        };

    private static async Task SeedAsync(AppDbContext db, string channelName, params (string Action, DateTime OccurredAtUtc)[] entries)
    {
        foreach (var (action, occurredAtUtc) in entries)
        {
            db.AuditLogEntries.Add(new AuditLogEntry
            {
                OccurredAtUtc = occurredAtUtc,
                ActorTwitchUserId = "4711",
                ActorLogin = "sensitron",
                Action = action,
                ChannelName = channelName
            });
        }

        await db.SaveChangesAsync();
    }
}
