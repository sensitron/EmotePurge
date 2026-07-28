using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteSetOwnershipServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CheckAsync_ReturnsOtherTrackedChannel_WhenActiveSetIdMatches()
    {
        // Tier 2: two channels we already track share the same ActiveEmoteSetId — the cheap,
        // no-network DB cross-check must catch this regardless of what 7TV says.
        await using var db = fixture.CreateDbContext();

        var channelA = new Channel { ChannelName = "sharedsettest_a", TwitchChannelId = "1001", ActiveEmoteSetId = "set-shared" };
        var channelB = new Channel { ChannelName = "sharedsettest_b", TwitchChannelId = "1002", ActiveEmoteSetId = "set-shared" };
        db.Channels.AddRange(channelA, channelB);
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("1001", Arg.Any<CancellationToken>())
            .Returns(new SevenTvIdentity("7tv-user-a", "set-shared"));
        sevenTv.GetEmoteSetOwnerIdAsync("set-shared", Arg.Any<CancellationToken>())
            .Returns("7tv-user-a");

        var helix = Substitute.For<ITwitchHelixClient>();
        var service = new EmoteSetOwnershipService(db, sevenTv, helix, NullLogger<EmoteSetOwnershipService>.Instance);

        var result = await service.CheckAsync("sharedsettest_a", callerTwitchUserId: null, callerAccessToken: null);

        Assert.True(result.Available);
        Assert.True(result.IsOwnSet);
        Assert.Contains("sharedsettest_b", result.OtherTrackedChannelsSharingSet);
        Assert.Empty(result.OtherModeratedChannelsSharingSet);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnavailable_WhenChannelNeverSynced()
    {
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel { ChannelName = "neversynctest", TwitchChannelId = null, ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = new EmoteSetOwnershipService(db, sevenTv, helix, NullLogger<EmoteSetOwnershipService>.Instance);

        var result = await service.CheckAsync("neversynctest", callerTwitchUserId: null, callerAccessToken: null);

        Assert.False(result.Available);
        Assert.False(result.IsOwnSet);
        Assert.Empty(result.OtherTrackedChannelsSharingSet);
        Assert.Empty(result.OtherModeratedChannelsSharingSet);
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsModeratedChannelMatch_ForUntrackedChannel()
    {
        // Tier 3: the caller moderates a channel we've never joined/synced at all (no DB row) —
        // Tier 2 alone can never see this, only a live 7TV lookup against the moderated list can.
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel { ChannelName = "tier3test_owner", TwitchChannelId = "2001", ActiveEmoteSetId = "set-tier3" });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("2001", Arg.Any<CancellationToken>())
            .Returns(new SevenTvIdentity("7tv-user-owner", "set-tier3"));
        sevenTv.GetEmoteSetOwnerIdAsync("set-tier3", Arg.Any<CancellationToken>())
            .Returns("7tv-user-owner");
        // The untracked moderated channel's broadcaster id resolves to the SAME active set.
        sevenTv.ResolveSevenTvIdentityAsync("3002", Arg.Any<CancellationToken>())
            .Returns(new SevenTvIdentity("7tv-user-untracked", "set-tier3"));

        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelsAsync("caller-token", "caller-id", Arg.Any<CancellationToken>())
            .Returns(new List<TwitchModeratedChannelInfo> { new("tier3test_untracked", "3002") });

        var service = new EmoteSetOwnershipService(db, sevenTv, helix, NullLogger<EmoteSetOwnershipService>.Instance);

        var result = await service.CheckAsync("tier3test_owner", callerTwitchUserId: "caller-id", callerAccessToken: "caller-token");

        Assert.Contains("tier3test_untracked", result.OtherModeratedChannelsSharingSet);
        Assert.Empty(result.OtherTrackedChannelsSharingSet);
    }
}
