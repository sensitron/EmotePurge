using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Same GroupBy-over-Postgres risk category as UsageStatQueryServiceTests (vote tallies here,
// usage totals via the real UsageStatQueryService dependency — not mocked, so the whole
// aggregation chain gets exercised against the real database).
[Collection("Postgres")]
public class VoteSessionQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetResultsAsync_ComputesScore_FromNormalizedUsageAndVoteDelta()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votetest1");
        var popular = await SeedEmoteAsync(db, channel.Id, "Popular");
        var unused = await SeedEmoteAsync(db, channel.Id, "Unused");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "voter-1");

        db.UsageStats.Add(new UsageStat { EmoteId = popular.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), UseCount = 100 });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = popular.Id, UserId = voter.Id, Type = VoteType.Delete });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        Assert.NotNull(results);
        var popularResult = results!.Emotes.Single(r => r.EmoteId == popular.Id);
        var unusedResult = results.Emotes.Single(r => r.EmoteId == unused.Id);

        // popular has the only usage → normalized to 100, unused stays at the min (0).
        Assert.Equal(100d, popularResult.NormalizedUsageScore);
        Assert.Equal(0d, unusedResult.NormalizedUsageScore);
        Assert.Equal(1, popularResult.DeleteVotes);
        Assert.Equal(99d, popularResult.Score); // 100 (usage) - 1 (delete vote)
        Assert.True(popularResult.Score > unusedResult.Score);
    }

    [Fact]
    public async Task GetResultsAsync_MaxEqualsMin_NormalizesAllToZero_InsteadOfDividingByZero()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votetest2");
        await SeedEmoteAsync(db, channel.Id, "A");
        await SeedEmoteAsync(db, channel.Id, "B");
        var session = await SeedActiveSessionAsync(db, channel.Id);

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        Assert.NotNull(results);
        Assert.All(results!.Emotes, r => Assert.Equal(0d, r.NormalizedUsageScore));
    }

    [Fact]
    public async Task GetResultsAsync_PopulatesMyVote_ForGivenViewer()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votetest3");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var viewer = await SeedUserAsync(db, "viewer-1");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = viewer.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id, viewerTwitchUserId: viewer.Id);

        var result = Assert.Single(results!.Emotes);
        Assert.Equal(VoteType.Keep, result.MyVote);
    }

    [Fact]
    public async Task GetResultsAsync_ReturnsNull_ForUnknownSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votetest4");

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, sessionId: 999_999);

        Assert.Null(results);
    }

    [Fact]
    public async Task GetResultsAsync_ReturnsNull_ForUnknownChannel()
    {
        await using var db = fixture.CreateDbContext();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync("does-not-exist", sessionId: 1);

        Assert.Null(results);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelId, string name)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<VoteSession> SeedActiveSessionAsync(AppDbContext db, string channelId)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Test Session",
            AllowedVoterRoles = AllowedRoles.Everyone,
            IsActive = true
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<User> SeedUserAsync(AppDbContext db, string twitchUserId)
    {
        var user = new User { Id = twitchUserId, TwitchUsername = twitchUserId, DisplayName = twitchUserId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
