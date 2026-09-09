using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
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


    /// <summary>
    /// E5b, at this layer: the two upstream calls this class makes itself each cost their own permit.
    /// The pages of the set read cost theirs inside the client, where their number is known
    /// (<c>SevenTvApiClientEmoteSetPreviewTests.EveryPage_ChargesExactlyOneRequestPermit</c>) — which
    /// is why the substituted client here charges nothing and the count is two, not twelve.
    /// </summary>
    [Fact]
    public async Task EachUpstreamCallOfTheChain_ChargesItsOwnRequestPermit()
    {
        var sevenTv = SevenTvClientReturning(SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Ok(new SevenTvEmoteSetPreview(0, false, [])));
        var budget = new RecordingForeignUpstreamRequestBudget();
        var service = CreateService(FoundIdentityService(), sevenTv, budget);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, result.Status);
        Assert.Equal(2, budget.Charges);
    }

    /// <summary>A refused permit stops the chain before Helix is asked at all — the permit is taken
    /// first precisely so that a refusal means no request happened.</summary>
    [Fact]
    public async Task NoPermitAtAll_StopsBeforeHelix()
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var service = CreateService(identityService, sevenTv, new RecordingForeignUpstreamRequestBudget(grantCount: 0));

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, result.Status);
        await identityService.DidNotReceive().LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The same one step later: Helix was affordable, the 7TV identity call was not.</summary>
    [Fact]
    public async Task APermitForHelixButNotForTheIdentityCall_StopsThere()
    {
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var service = CreateService(FoundIdentityService(), sevenTv, new RecordingForeignUpstreamRequestBudget(grantCount: 1));

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, result.Status);
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A budget exhaustion reported by the set read keeps its identity all the way up: it is our own
    /// throttle, never a 7TV failure, and the decorator above must be able to tell them apart so
    /// self-inflicted congestion cannot trip the circuit breaker.
    /// </summary>
    [Fact]
    public async Task BudgetExhaustedDuringTheSetRead_MapsToProviderBudgetExhausted_NotSevenTvUnavailable()
    {
        var sevenTv = SevenTvClientReturning(SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.BudgetExhausted));
        var service = CreateService(FoundIdentityService(), sevenTv);

        var result = await service.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, result.Status);
        Assert.NotEqual(ForeignEmoteSetLookupStatus.SevenTvUnavailable, result.Status);
    }

    /// <summary>
    /// The number the whole finding was about, against the real budget: a resolution costs one permit
    /// per upstream request — here the two calls above plus the ten pages the client charges — so a
    /// minute's worth of 60 permits admits five resolutions. Charged once per resolution instead, the
    /// same minute would have admitted sixty, i.e. up to 720 upstream requests against a limit of 60.
    /// The stand-in client charges its ten pages exactly as the real one does
    /// (<c>SevenTvApiClientEmoteSetPreviewTests</c> pins that end).
    /// </summary>
    [Fact]
    public async Task AResolutionCostsOnePermitPerRequest_SoAMinuteAdmitsFiveOfThem_NotSixty()
    {
        using var budget = new ForeignEmoteSetProviderBudget(TimeProvider.System);
        var sevenTv = SevenTvClientReturning(SevenTvIdentityResult.Ok(new SevenTvIdentity(SevenTvUserId, EmoteSetId)));
        sevenTv.GetEmoteSetPreviewAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                for (var page = 0; page < 10; page++)
                {
                    if (!await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None))
                    {
                        return SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.BudgetExhausted);
                    }
                }

                return SevenTvEmoteSetPreviewResult.Ok(new SevenTvEmoteSetPreview(0, false, []));
            });
        var service = CreateService(FoundIdentityService(), sevenTv, new WaitlessBudget(budget));

        var statuses = new List<ForeignEmoteSetLookupStatus>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await service.GetForeignEmoteSetAsync(Channel)).Status);
        }

        Assert.Equal(5, statuses.Count(s => s == ForeignEmoteSetLookupStatus.Ok));
        Assert.Equal(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, statuses[5]);
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

    private static ForeignEmoteSetService CreateService(
        IChannelIdentityService identityService,
        ISevenTvApiClient sevenTvApiClient,
        IForeignUpstreamRequestBudget? requestBudget = null) =>
        new(
            identityService,
            sevenTvApiClient,
            requestBudget ?? new RecordingForeignUpstreamRequestBudget(),
            NullLogger<ForeignEmoteSetService>.Instance);

    // The real budget, asked not to wait: TryChargeRequestAsync's own default would block this test
    // on wall-clock time for the rest of the minute once the budget is spent, and the point here is
    // how many permits a resolution costs, not how long a caller is willing to wait for one.
    private sealed class WaitlessBudget(ForeignEmoteSetProviderBudget inner) : IForeignUpstreamRequestBudget
    {
        public Task<bool> TryChargeRequestAsync(CancellationToken cancellationToken = default) =>
            inner.TryChargeRequestAsync(TimeSpan.Zero, cancellationToken);
    }
}
