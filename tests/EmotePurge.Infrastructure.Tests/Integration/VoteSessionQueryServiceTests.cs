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
    public async Task GetResultsAsync_ComputesNetScore_AndOrdersDeleteCandidatesFirst()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votenet1");
        var contested = await SeedEmoteAsync(db, channel.Id, "Contested");
        var doomed = await SeedEmoteAsync(db, channel.Id, "Doomed");
        var quiet = await SeedEmoteAsync(db, channel.Id, "Quiet");
        var loved = await SeedEmoteAsync(db, channel.Id, "Loved");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voterA = await SeedUserAsync(db, "votenet1-a");
        var voterB = await SeedUserAsync(db, "votenet1-b");

        // Usage must no longer influence the score — give the doomed emote plenty of it.
        db.UsageStats.Add(new UsageStat { EmoteId = doomed.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), UseCount = 100 });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = doomed.Id, UserId = voterA.Id, Type = VoteType.Delete });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = doomed.Id, UserId = voterB.Id, Type = VoteType.Delete });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = contested.Id, UserId = voterA.Id, Type = VoteType.Keep });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = contested.Id, UserId = voterB.Id, Type = VoteType.Delete });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = loved.Id, UserId = voterA.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        Assert.NotNull(results);
        var doomedResult = results!.Emotes.Single(r => r.EmoteId == doomed.Id);
        Assert.Equal(-2, doomedResult.Score);
        Assert.Equal(2, doomedResult.DeleteVotes);

        // Ascending net score; the 0-score tie is broken by total votes (contested before quiet).
        Assert.Equal(
            new[] { doomed.Id, contested.Id, quiet.Id, loved.Id },
            results.Emotes.Select(r => r.EmoteId).ToArray());
    }

    [Fact]
    public async Task GetResultsAsync_ReportsUsage_OnlyWithIncludeRawUsage_NullOtherwise()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votenull1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), UseCount = 5 });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));

        var withheld = await service.GetResultsAsync(channel.ChannelName, session.Id, includeRawUsage: false);
        Assert.Null(Assert.Single(withheld!.Emotes).TotalUseCount);

        var included = await service.GetResultsAsync(channel.ChannelName, session.Id, includeRawUsage: true);
        Assert.Equal(5, Assert.Single(included!.Emotes).TotalUseCount);
    }

    [Fact]
    public async Task GetResultsAsync_SubsetSession_ReturnsOnlyBallotMembers()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesubset1");
        var onBallot = await SeedEmoteAsync(db, channel.Id, "OnBallot");
        var alsoOnBallot = await SeedEmoteAsync(db, channel.Id, "AlsoOnBallot");
        await SeedEmoteAsync(db, channel.Id, "NotOnBallot");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        await SeedBallotAsync(db, session.Id, onBallot.Id, alsoOnBallot.Id);

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        Assert.Equal(2, results!.Emotes.Count);
        Assert.Contains(results.Emotes, r => r.EmoteId == onBallot.Id);
        Assert.Contains(results.Emotes, r => r.EmoteId == alsoOnBallot.Id);
    }

    [Fact]
    public async Task GetResultsAsync_SubsetSession_KeepsArchivedMemberVisible_WithVotes_ButNoUsage()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesubset2");
        var emote = await SeedEmoteAsync(db, channel.Id, "SoonGone");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        await SeedBallotAsync(db, session.Id, emote.Id);
        var voter = await SeedUserAsync(db, "votesubset2-voter");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Delete });
        emote.IsArchived = true;
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id, includeRawUsage: true);

        var result = Assert.Single(results!.Emotes);
        Assert.True(result.IsArchived);
        Assert.Equal(1, result.DeleteVotes);
        // Usage totals are not computed for archived emotes — null even for managers, never a fake 0.
        Assert.Null(result.TotalUseCount);
    }

    [Fact]
    public async Task GetResultsAsync_DynamicSession_StillExcludesArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votesubset3");
        var active = await SeedEmoteAsync(db, channel.Id, "Active");
        var archived = await SeedEmoteAsync(db, channel.Id, "Archived");
        archived.IsArchived = true;
        var session = await SeedActiveSessionAsync(db, channel.Id);
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        var result = Assert.Single(results!.Emotes);
        Assert.Equal(active.Id, result.EmoteId);
        Assert.False(result.IsArchived);
    }

    [Fact]
    public async Task GetResultsAsync_CountsDistinctVoters_NotVotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "votecount1");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "EmoteB");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var busyVoter = await SeedUserAsync(db, "votecount1-busy");
        var casualVoter = await SeedUserAsync(db, "votecount1-casual");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emoteA.Id, UserId = busyVoter.Id, Type = VoteType.Keep });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emoteB.Id, UserId = busyVoter.Id, Type = VoteType.Delete });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emoteA.Id, UserId = casualVoter.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var results = await service.GetResultsAsync(channel.ChannelName, session.Id);

        Assert.Equal(2, results!.VoterCount);
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

    [Fact]
    public async Task ListSessionsPagedAsync_ReturnsCorrectPage_AndTotalCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "pagetest1");
        var now = DateTime.UtcNow;
        await SeedActiveSessionAsync(db, channel.Id, startedAt: now.AddMinutes(-3));
        await SeedActiveSessionAsync(db, channel.Id, startedAt: now.AddMinutes(-2));
        await SeedActiveSessionAsync(db, channel.Id, startedAt: now.AddMinutes(-1));

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));

        var firstPage = await service.ListSessionsPagedAsync(channel.ChannelName, page: 1, pageSize: 2);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.TotalPages);

        var secondPage = await service.ListSessionsPagedAsync(channel.ChannelName, page: 2, pageSize: 2);
        Assert.Single(secondPage.Items);
    }

    [Fact]
    public async Task ListSessionsPagedAsync_ReportsEmoteCount_NullForDynamicSessions()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "pagetest3");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "EmoteB");
        var dynamicSession = await SeedActiveSessionAsync(db, channel.Id, startedAt: DateTime.UtcNow.AddMinutes(-2));
        var subsetSession = await SeedActiveSessionAsync(db, channel.Id, startedAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedBallotAsync(db, subsetSession.Id, emoteA.Id, emoteB.Id);

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListSessionsPagedAsync(channel.ChannelName, page: 1, pageSize: 10);

        Assert.Equal(2, page.Items[0].EmoteCount); // subsetSession, newest first
        Assert.Null(page.Items.Single(i => i.Id == dynamicSession.Id).EmoteCount);
    }

    [Fact]
    public async Task ListSessionsPagedAsync_OrdersByStartedAtDescending()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "pagetest2");
        var now = DateTime.UtcNow;
        var older = await SeedActiveSessionAsync(db, channel.Id, startedAt: now.AddMinutes(-10));
        var newer = await SeedActiveSessionAsync(db, channel.Id, startedAt: now.AddMinutes(-1));

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListSessionsPagedAsync(channel.ChannelName, page: 1, pageSize: 10);

        Assert.Equal(newer.Id, page.Items[0].Id);
        Assert.Equal(older.Id, page.Items[1].Id);
    }

    [Fact]
    public async Task ListMyVoteSessionsAsync_ReturnsSessionsAcrossMultipleChannels_ForOneVoter()
    {
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "mvtest1a");
        var channelB = await SeedChannelAsync(db, "mvtest1b");
        var emoteA = await SeedEmoteAsync(db, channelA.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channelB.Id, "EmoteB");
        var sessionA = await SeedActiveSessionAsync(db, channelA.Id);
        var sessionB = await SeedActiveSessionAsync(db, channelB.Id);
        var voter = await SeedUserAsync(db, "mvtest1-voter");
        db.Votes.Add(new Vote { VoteSessionId = sessionA.Id, EmoteId = emoteA.Id, UserId = voter.Id, Type = VoteType.Keep });
        db.Votes.Add(new Vote { VoteSessionId = sessionB.Id, EmoteId = emoteB.Id, UserId = voter.Id, Type = VoteType.Delete });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListMyVoteSessionsAsync(voter.Id, page: 1, pageSize: 20);

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Items, i => i.SessionId == sessionA.Id && i.ChannelName == channelA.ChannelName);
        Assert.Contains(page.Items, i => i.SessionId == sessionB.Id && i.ChannelName == channelB.ChannelName);
    }

    [Fact]
    public async Task ListMyVoteSessionsAsync_ExcludesSessionsTheVoterNeverVotedIn()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "mvtest2");
        var emote = await SeedEmoteAsync(db, channel.Id, "Emote");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var otherVoter = await SeedUserAsync(db, "mvtest2-othervoter");
        var targetVoter = await SeedUserAsync(db, "mvtest2-targetvoter");
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = otherVoter.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListMyVoteSessionsAsync(targetVoter.Id, page: 1, pageSize: 20);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ListMyVoteSessionsAsync_OrdersByLastVotedAtDescending()
    {
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "mvtest3a");
        var channelB = await SeedChannelAsync(db, "mvtest3b");
        var emoteA = await SeedEmoteAsync(db, channelA.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channelB.Id, "EmoteB");
        var sessionA = await SeedActiveSessionAsync(db, channelA.Id);
        var sessionB = await SeedActiveSessionAsync(db, channelB.Id);
        var voter = await SeedUserAsync(db, "mvtest3-voter");
        var now = DateTime.UtcNow;
        db.Votes.Add(new Vote { VoteSessionId = sessionA.Id, EmoteId = emoteA.Id, UserId = voter.Id, Type = VoteType.Keep, UpdatedAt = now.AddMinutes(-10) });
        db.Votes.Add(new Vote { VoteSessionId = sessionB.Id, EmoteId = emoteB.Id, UserId = voter.Id, Type = VoteType.Keep, UpdatedAt = now.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListMyVoteSessionsAsync(voter.Id, page: 1, pageSize: 20);

        Assert.Equal(sessionB.Id, page.Items[0].SessionId);
        Assert.Equal(sessionA.Id, page.Items[1].SessionId);
    }

    [Fact]
    public async Task ListMyVoteSessionsAsync_PopulatesLastVotedAt_AsMaxOfThatVotersVotesInSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "mvtest4");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "EmoteA");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "EmoteB");
        var session = await SeedActiveSessionAsync(db, channel.Id);
        var voter = await SeedUserAsync(db, "mvtest4-voter");
        var now = DateTime.UtcNow;
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emoteA.Id, UserId = voter.Id, Type = VoteType.Keep, UpdatedAt = now.AddMinutes(-5) });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emoteB.Id, UserId = voter.Id, Type = VoteType.Delete, UpdatedAt = now.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));
        var page = await service.ListMyVoteSessionsAsync(voter.Id, page: 1, pageSize: 20);

        var item = Assert.Single(page.Items);
        Assert.Equal(now.AddMinutes(-1), item.LastVotedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ListMyVoteSessionsAsync_Paginates_TotalCountAndPages()
    {
        await using var db = fixture.CreateDbContext();
        var voter = await SeedUserAsync(db, "mvtest5-voter");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var channel = await SeedChannelAsync(db, $"mvtest5-{i}");
            var emote = await SeedEmoteAsync(db, channel.Id, $"Emote{i}");
            var session = await SeedActiveSessionAsync(db, channel.Id);
            db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Keep, UpdatedAt = now.AddMinutes(-i) });
            await db.SaveChangesAsync();
        }

        var service = new VoteSessionQueryService(db, new UsageStatQueryService(db));

        var firstPage = await service.ListMyVoteSessionsAsync(voter.Id, page: 1, pageSize: 2);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.TotalPages);

        var secondPage = await service.ListMyVoteSessionsAsync(voter.Id, page: 2, pageSize: 2);
        Assert.Single(secondPage.Items);
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

    private static async Task<VoteSession> SeedActiveSessionAsync(AppDbContext db, string channelId, DateTime? startedAt = null)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Test Session",
            AllowedVoterRoles = AllowedRoles.Everyone,
            IsActive = true,
            StartedAt = startedAt ?? DateTime.UtcNow
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

    private static async Task SeedBallotAsync(AppDbContext db, long sessionId, params string[] emoteIds)
    {
        db.VoteSessionEmotes.AddRange(emoteIds.Select(id => new VoteSessionEmote { VoteSessionId = sessionId, EmoteId = id }));
        await db.SaveChangesAsync();
    }
}
