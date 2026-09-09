using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="ForeignEmoteSetService"/>'s status mapping against every row of the state table
/// in the foreign-channel-import spec (section 5), plus the AK 11 guarantee that this path never
/// touches 7TV's search endpoint. Both collaborators are interfaces (Regel 4/5), so this needs no
/// container — the same shape as <c>ChannelAccessServiceTests</c>.
/// </summary>
public class ForeignEmoteSetServiceTests
{
    private const string Channel = "HandOfBlood";
    private const string NormalizedChannel = "handofblood";
    private const string TwitchUserId = "36340781";
    private const string SevenTvUserId = "01FRY81K4800085N93FNKSBYXS";
    private const string EmoteSetId = "01FRY81K4800085N93FNKSBYXS-set";

    [Fact]
    public async Task ChannelNotOnTwitch_MapsToChannelNotOnTwitch()
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(NormalizedChannel, Arg.Any<CancellationToken>())
            .Returns(TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound));
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var service = CreateService(identityService, sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.ChannelNotOnTwitch, result.Status);
        Assert.Null(result.EmoteSet);
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwitchUnavailable_MapsToTwitchUnavailable_NotToARejection()
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(NormalizedChannel, Arg.Any<CancellationToken>())
            .Returns(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable));
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var service = CreateService(identityService, sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        // Unlike ChannelService.JoinAsync, this read has no existing row to "carry on" with — an
        // unreachable Helix must surface as a failure here, not be swallowed.
        Assert.Equal(ForeignEmoteSetLookupStatus.TwitchUnavailable, result.Status);
        Assert.Null(result.EmoteSet);
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSevenTvAccount_MapsToNoSevenTvAccount()
    {
        var service = CreateService(
            FoundIdentityService(),
            SevenTvClientReturning(SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount)));

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.NoSevenTvAccount, result.Status);
        Assert.Null(result.EmoteSet);
    }

    [Fact]
    public async Task SevenTvIdentityUnavailable_MapsToSevenTvUnavailable()
    {
        var service = CreateService(
            FoundIdentityService(),
            SevenTvClientReturning(SevenTvIdentityResult.Failed(SevenTvLookupStatus.Unavailable)));

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvUnavailable, result.Status);
        Assert.Null(result.EmoteSet);
    }

    /// <summary>
    /// F2, the trap the spec calls out explicitly: "account exists, no active set" is <c>Ok</c> with
    /// a null <c>ActiveEmoteSetId</c> on this path, never <c>SevenTvLookupStatus.NoActiveEmoteSet</c>
    /// (which only the unused v3 REST path can produce). A naive implementation that checked for that
    /// status here would never see this branch fire at all.
    /// </summary>
    [Fact]
    public async Task AccountWithoutActiveSet_IsOkWithNullEmoteSetId_MapsToNoActiveEmoteSet()
    {
        var sevenTv = SevenTvClientReturning(
            SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, ActiveEmoteSetId: null)));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.NoActiveEmoteSet, result.Status);
        Assert.Null(result.EmoteSet);
        await sevenTv.DidNotReceive().GetEmoteSetPreviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyActiveSet_IsOk_NotAnError()
    {
        var sevenTv = SevenTvClientReturning(
            SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Ok(new SevenTvEmoteSetPreview(0, false, [])));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, result.Status);
        Assert.NotNull(result.EmoteSet);
        Assert.Empty(result.EmoteSet!.Emotes);
        Assert.Equal(0, result.EmoteSet.TotalCount);
        Assert.False(result.EmoteSet.Truncated);
    }

    [Fact]
    public async Task SevenTvRateLimited_MapsToSevenTvRateLimited_DistinctFromGenericUnavailable()
    {
        var sevenTv = SevenTvClientReturning(
            SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.RateLimited));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        // AK 7/8: kept apart from plain SevenTvUnavailable at this layer so a hardening decorator can
        // open its breaker immediately instead of counting toward the five-failure threshold — even
        // though the API endpoint maps both onto the same wire error code.
        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvRateLimited, result.Status);
        Assert.NotEqual(ForeignEmoteSetLookupStatus.SevenTvUnavailable, result.Status);
    }

    [Fact]
    public async Task SevenTvSetPreviewUnavailable_MapsToSevenTvUnavailable()
    {
        var sevenTv = SevenTvClientReturning(
            SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.Unavailable));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvUnavailable, result.Status);
    }

    [Fact]
    public async Task FullOkPath_MapsRowsAndNeverCallsTheSearchEndpoint()
    {
        var sevenTv = SevenTvClientReturning(
            SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        var previewItem = new SevenTvEmoteSetPreviewItem("emote-1", "PogU", "PogChamp", "https://cdn.example/1.webp", 500, 12);
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Ok(new SevenTvEmoteSetPreview(1, false, [previewItem])));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, result.Status);
        var emoteSet = result.EmoteSet!;
        Assert.Equal(NormalizedChannel, emoteSet.ChannelName);
        Assert.Equal(SevenTvUserId, emoteSet.SevenTvUserId);
        Assert.Equal(EmoteSetId, emoteSet.EmoteSetId);
        var row = Assert.Single(emoteSet.Emotes);
        Assert.Equal("emote-1", row.SevenTvEmoteId);
        Assert.Equal("PogU", row.Name);
        Assert.Equal("PogChamp", row.DefaultName);
        Assert.Equal(500, row.TopAllTime);
        Assert.Equal(12, row.Trending);

        // AK 11: the whole point of F1 is that this path never falls back to 7TV's users(query:)
        // search endpoint, which the same interface still exposes for SevenTvSyncService's own use.
        await sevenTv.DidNotReceive().ResolveTwitchUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static IChannelIdentityService FoundIdentityService()
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(NormalizedChannel, Arg.Any<CancellationToken>())
            .Returns(TwitchUserLookup.Found(new TwitchUserIdentity(TwitchUserId, NormalizedChannel)));
        return identityService;
    }

    private static ISevenTvApiClient SevenTvClientReturning(SevenTvIdentityResult identityResult)
    {
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync(TwitchUserId, Arg.Any<CancellationToken>()).Returns(identityResult);
        return sevenTv;
    }

    private static ForeignEmoteSetService CreateService(IChannelIdentityService identityService, ISevenTvApiClient sevenTvApiClient) =>
        new(identityService, sevenTvApiClient, NullLogger<ForeignEmoteSetService>.Instance);
}
