using System.Text.Json;

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
    public async Task ListAsync_ProjectsEveryField_AndWhitelistsTheDetails()
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
        Assert.Equal("sensitron", dto.ActorLogin);
        Assert.Equal(AuditActions.EmotesSyncDeleted, dto.Action);
        Assert.Equal("voteSession", dto.TargetType);
        Assert.Equal("42", dto.TargetId);
        // Never the raw column: the client receives a closed shape it cannot be surprised by.
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 12, null), dto.Detail);
    }

    [Theory]
    // The three recognized shapes, one row each.
    [InlineData("""{"emoteCount": 12}""", AuditLogDetail.Kinds.EmoteCount, 12L, null)]
    [InlineData("""{"removedEntries": 3}""", AuditLogDetail.Kinds.RemovedEntries, 3L, null)]
    [InlineData("""{"title": "Sommer-Purge"}""", AuditLogDetail.Kinds.Title, null, "Sommer-Purge")]
    // Fixed precedence when a payload carries more than one known key.
    [InlineData("""{"title": "x", "emoteCount": 7}""", AuditLogDetail.Kinds.EmoteCount, 7L, null)]
    // Everything unrecognized degrades to "no detail" instead of leaking or throwing. `login` is
    // the one that matters: it is present on the user-scoped actions and must never render.
    [InlineData("""{"login": "handofblood"}""", null, null, null)]
    [InlineData("""{"ip": "203.0.113.7"}""", null, null, null)]
    [InlineData("""{"emoteCount": "twelve"}""", null, null, null)]
    [InlineData("""[1, 2]""", null, null, null)]
    [InlineData("""17""", null, null, null)]
    [InlineData(null, null, null, null)]
    // No case for syntactically invalid JSON: the column is jsonb, so Postgres rejects it on insert
    // and it cannot reach the reader through this path. ProjectDetail still catches JsonException —
    // the guard costs nothing and is what keeps the column type from being load-bearing.
    public async Task ListAsync_ProjectsDetails_DefensivelyAndByWhitelist(
        string? detailsJson, string? expectedKind, long? expectedCount, string? expectedText)
    {
        await using var db = fixture.CreateDbContext();
        // Truncated to the column's 25 characters — one channel per theory case, so the cases stay
        // independent inside the shared database.
        var channel = $"{ChannelPrefix}-d{Guid.NewGuid():N}"[..25];
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 10, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.VoteSessionDelete,
            ChannelName = channel,
            DetailsJson = detailsJson
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        if (expectedKind is null)
        {
            Assert.Null(dto.Detail);
            return;
        }

        Assert.Equal(new AuditLogDetail(expectedKind, expectedCount, expectedText), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromAChannel_OnBothCountAndSource()
    {
        // The precedence pin (R1): the payload also carries emoteCount, which would win and drop the
        // provenance if the sourceKind check did not run first.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-import-channel";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 5, "sourceChannelName": "otherchannel", "sourceKind": "channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromChannel, 5, "otherchannel"), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromAFile_WithNoSource()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-import-file";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 30, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 3, "sourceChannelName": null, "sourceKind": "file"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, 3, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheFileKind_WhenAChannelSourceCarriesNoName()
    {
        // The endpoint rejects this combination, so it should never reach the column. If it ever
        // does, the reader still has to degrade sensibly: the channel kind's translation
        // interpolates the source name, and claiming a channel origin without one would render
        // "3 emotes from ". Belt and braces, on a column that is written by ten call sites.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-noname";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 45, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 3, "sourceChannelName": null, "sourceKind": "channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, 3, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_LeavesABareEmoteCountProjectionUnchanged_WhenThereIsNoSourceKind()
    {
        // The gegenprobe for the precedence change: an unrelated action that also carries a bare
        // emoteCount (e.g. emotes.syncRestored) must not be caught by the new sourceKind check.
        await using var db = fixture.CreateDbContext();
        // "gegenprobe" was one character over the ChannelName column's 25-char limit; kept short.
        var channel = $"{ChannelPrefix}-import-bare";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncRestored,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 9}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 9, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_TruncatesDetailText_BecauseTitlesAreUserInput()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-detail-long";
        var title = new string('a', 500);
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 11, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.VoteSessionDelete,
            ChannelName = channel,
            DetailsJson = JsonSerializer.Serialize(new { title })
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        Assert.Equal(200, Assert.Single(page.Items).Detail?.Text?.Length);
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
