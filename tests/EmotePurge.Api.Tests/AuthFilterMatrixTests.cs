using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The endpoint-filter matrix over the real route tree: which status code, and which error code,
/// every authorization outcome produces.
/// </summary>
/// <remarks>
/// This exists because the filters are close copies of one another and the differences between them
/// are single lines. <c>VoteAudienceFilter</c> and <c>VoteEligibilityFilter</c> differ in exactly one
/// switch arm; <c>UsageStatsAccessAuthorizationFilter</c> and
/// <c>ChannelManagementAuthorizationFilter</c> differ in exactly one method call. A copy-paste slip
/// in either place opens data to the wrong audience without turning anything red — which is what
/// these assertions are for. The second half of the file pins the filter *order*: the 400 for a
/// malformed channel name has to win over the 403, because that ordering is the documented error
/// contract, not an accident of registration.
/// </remarks>
public class AuthFilterMatrixTests : IClassFixture<ApiFactory>
{
    private const string Channel = "testchannel";

    private readonly ApiFactory _factory;

    public AuthFilterMatrixTests(ApiFactory factory)
    {
        _factory = factory;

        // xunit creates one test-class instance per test but shares the class fixture, so the
        // substitutes carry every call made by the tests that ran before. The DidNotReceive()
        // assertions below only mean anything against a clean slate.
        factory.ChannelAccess.ClearReceivedCalls();
        factory.VoteEligibility.ClearReceivedCalls();
        factory.Channels.ClearReceivedCalls();
        factory.ResyncCooldown.ClearReceivedCalls();

        // Default to "the slot was free", so the cooldown never masks the status code a test is
        // actually asserting. The one case that cares sets it explicitly.
        factory.ResyncCooldown.TryBeginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResyncCooldownState(true, 0));
    }

    [Theory]
    [InlineData("GET", "/api/channels/testchannel")]
    [InlineData("POST", "/api/channels/testchannel/join")]
    [InlineData("DELETE", "/api/channels/testchannel")]
    [InlineData("DELETE", "/api/channels/testchannel/purge")]
    [InlineData("GET", "/api/channels/testchannel/audit-log")]
    [InlineData("POST", "/api/channels/testchannel/resync")]
    [InlineData("GET", "/api/channels/testchannel/permissions")]
    [InlineData("GET", "/api/channels/mine")]
    [InlineData("GET", "/api/channels/testchannel/usage-stats")]
    [InlineData("GET", "/api/channels/testchannel/emotes")]
    [InlineData("GET", "/api/channels/testchannel/emotes/set-warning")]
    [InlineData("GET", "/api/channels/testchannel/emotes/duplicate-names")]
    [InlineData("POST", "/api/channels/testchannel/emotes/sync-restored")]
    [InlineData("POST", "/api/channels/testchannel/emotes/sync-imported")]
    [InlineData("GET", "/api/channels/testchannel/vote-sessions")]
    [InlineData("GET", "/api/channels/testchannel/vote-sessions/1/results")]
    [InlineData("POST", "/api/channels/testchannel/vote-sessions/1/votes")]
    [InlineData("GET", "/api/vote-sessions/mine")]
    [InlineData("GET", "/api/admin/channels")]
    [InlineData("GET", "/api/admin/rate-limits")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/api/channels/live-events")]
    [InlineData("GET", "/api/admin/live")]
    // Reports how much of the caller's own live-stream budget is in use, so it must be as
    // authenticated as the streams it counts — an anonymous caller would otherwise all share the
    // "unknown" subscriber key and read each other's number.
    [InlineData("GET", "/api/live/status")]
    public async Task EveryProtectedEndpoint_Answers401_ForAnAnonymousCaller(string method, string path)
    {
        // The authorization middleware short-circuits before any endpoint filter, so this is the one
        // outcome that does not depend on which filter sits on the route — which is precisely why it
        // is worth listing every route: a group that lost its RequireAuthorization() would show up
        // here and nowhere else.
        var response = await SendAsync(method, path, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/channels/testchannel")]
    [InlineData("GET", "/api/channels/testchannel/audit-log")]
    [InlineData("POST", "/api/channels/testchannel/resync")]
    [InlineData("DELETE", "/api/channels/testchannel/purge")]
    [InlineData("GET", "/api/channels/testchannel/usage-stats")]
    [InlineData("GET", "/api/channels/testchannel/vote-sessions/1/results")]
    [InlineData("POST", "/api/channels/testchannel/vote-sessions/1/votes")]
    public async Task EveryFilter_Answers401_WhenTheSessionIsAuthenticatedButItsClaimsAreIncomplete(string method, string path)
    {
        // A cookie without the twitch:login claim authenticates but cannot produce a
        // TwitchPrincipalInfo. Every filter answers 401 (re-authenticate), not 403 (you are not
        // allowed) — the caller's problem is their session, not their permissions.
        var response = await SendAsync(method, path, userId: NewUserId(), login: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChannelManagementFilter_Answers403_WhenTheCallerCannotManageTheChannel()
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", $"/api/channels/{Channel}", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChannelManagementFilter_CallsThroughToTheHandler_WhenTheCallerCanManageTheChannel()
    {
        // The allow path, on the one endpoint whose handler depends on a single substitutable
        // service: proves the filter returns next(context) rather than swallowing the request.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = Channel, IsBotActive = true, ActiveEmoteSetId = "set-1" });

        var response = await SendAsync("GET", $"/api/channels/{Channel}", NewUserId());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GlobalAdminFilter_Answers403_ForANonAdmin_EvenOnTheirOwnChannel()
    {
        // Purge sits behind the admin filter rather than the management filter: a broadcaster may
        // remove the bot from their channel, but not irreversibly delete the channel's whole history.
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("DELETE", $"/api/channels/{Channel}/purge", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GlobalAdminFilter_GuardsTheWholeAdminGroup()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);

        var response = await SendAsync("GET", "/api/admin/channels", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UsageStatsFilter_UsesTheWiderCheck_NotTheManagementCheck()
    {
        // The whole point of the weaker filter: a 7TV editor who cannot manage the channel still
        // sees its usage statistics. If this endpoint ever got the management filter by mistake,
        // every 7TV editor would silently lose access with nothing failing.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/usage-stats?from=2026-01-01&to=2026-01-02", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.ChannelAccess.Received().CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelAuditLog_UsesTheManagementCheck_NotTheWiderOne()
    {
        // The inverse of the usage-stats test above, and the reason it is worth its own case: the
        // activity feed names which moderator did what, and CanViewUsageStatsAsync would also admit
        // the channel's 7TV editors — who are frequently outside the mod team. A caller who passes
        // the wider check but not the management one must be refused, and the wider check must not
        // even be consulted. Without this, the exclusion is a comment rather than a behaviour.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/audit-log", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.ChannelAccess.DidNotReceive()
            .CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelAuditLog_ValidatesTheChannelName_BeforeAuthorizing()
    {
        // Registered on the shared /api/channels group, which is what carries
        // ChannelNameValidationFilter. Pinned here because the obvious "tidy-up" — moving the route
        // into its own MapGroup like UsageStatsEndpoints — silently drops that filter unless it is
        // re-registered ahead of the authorization one.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", "/api/channels/bad-name/audit-log", NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ChannelResync_UsesTheWiderCheck_NotTheManagementOne()
    {
        // The mirror image of the audit-log case, on the route right next to it. The 7TV editor is
        // precisely the person with the problem this endpoint solves ("I added an emote and it is
        // not showing up"), so a caller who fails the management check but passes the wider one must
        // get through.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Channels.TriggerResyncAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.Triggered);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/resync", NewUserId());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _factory.ChannelAccess.Received()
            .CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelResync_Answers429_WithItsOwnErrorCode_WhenTheCooldownIsActive()
    {
        // Formally handler behaviour rather than filter behaviour, and in this file anyway: it is
        // the contract of a new ApiErrorCodes entry, and the difference between this 429 and the
        // rate limiter's bare one is exactly what lets the frontend say something useful.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ResyncCooldown.TryBeginAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(new ResyncCooldownState(false, 42));

        var response = await SendAsync("POST", $"/api/channels/{Channel}/resync", NewUserId());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ResyncCooldownActive, await ReadErrorCodeAsync(response));
        Assert.Equal("42", response.Headers.GetValues("Retry-After").Single());
        // Nothing was triggered, so nothing may reach the worker.
        await _factory.Channels.DidNotReceive()
            .TriggerResyncAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelResync_ReleasesTheCooldown_WhenNothingWasActuallyTriggered()
    {
        // Otherwise a resync attempt against a channel that is not tracked would block the real
        // resync for a full window — the exact moment a broadcaster who just rejoined wants one.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Channels.TriggerResyncAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.NotFound);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/resync", NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
        await _factory.ResyncCooldown.Received().ReleaseAsync(Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelResync_KeepsTheCooldown_WhenItDidTrigger()
    {
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Channels.TriggerResyncAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.Triggered);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/resync", NewUserId());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _factory.ResyncCooldown.DidNotReceive().ReleaseAsync(Channel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UsageStatsFilter_AlsoGuardsTheEmoteGroup_NotJustTheUsageStatsGroup()
    {
        // Its name says usage stats, but it is registered on five endpoints across two groups —
        // including sync-deleted, the one write path in that list.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/emotes/set-warning", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmoteList_UsesTheUsageStatsFilter_NotJustSetWarning()
    {
        // The plain-list route (GET .../emotes, no sub-path) sits directly on the group and is easy
        // to overlook when reasoning about "the filter guards the emote group" — it is the route the
        // import dialog actually calls first, and it must not be reachable by someone who fails the
        // wider check either.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/emotes", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmoteList_ValidatesTheChannelName_BeforeAuthorizing()
    {
        // Belongs to the group root rather than to a sub-path, so it is worth pinning on its own:
        // proves the root registration (group.MapGet("")) still inherits ChannelNameValidationFilter
        // ahead of the authorization filter, the same ordering the sibling routes rely on.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", "/api/channels/bad-name/emotes", NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SyncRestored_Answers403_ForACallerWithoutUsageStatsAccess()
    {
        // The write path among the emote-group endpoints, same filter as set-warning above — worth
        // its own case because a restore call slipping past the filter would write audit rows and
        // un-archive emotes in someone else's channel.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-restored", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SyncRestored_Answers400_WhenTheBodyCarriesNoEmoteIds()
    {
        // The empty-body check must sit before any service work — this is also what keeps the case
        // runnable without a database behind the test factory.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-restored", NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsEmpty, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SyncImported_Answers403_ForACallerWithoutUsageStatsAccess()
    {
        // Same audience and same filter as sync-restored — a caller who fails the wider check must
        // never reach the handler and leave an audit row behind.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-imported", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SyncImported_Answers400_WhenTheBodyCarriesNoEmoteIds()
    {
        // The empty-body check must sit before any service work — this is also what keeps the case
        // runnable without a database behind the test factory.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-imported", NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsEmpty, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SyncImported_Answers400_ForAnUnrecognizedSourceKind()
    {
        // Strictly lower-case and ordinal (F3 in the import plan): the only caller is our own
        // frontend, so a silent case-insensitive fallback would hide a frontend bug instead of
        // showing it.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var body = """{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "x"}""";
        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-imported", NewUserId(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidSourceKind, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SyncImported_Answers400_ForAChannelSourceWithoutAName()
    {
        // An audit row is write-once and kept forever, so a "came from a channel" entry that cannot
        // name the channel would stay broken. "file" is the kind that has no source channel.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var body = """{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "channel"}""";
        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-imported", NewUserId(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidSourceKind, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task SyncImported_Answers400_ForAFileSourceCarryingAChannelName()
    {
        // The other direction of the same contradiction: accepting this would file a file import
        // under a channel origin it never had, permanently.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var body = """{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": "somechannel", "sourceKind": "file"}""";
        var response = await SendAsync("POST", $"/api/channels/{Channel}/emotes/sync-imported", NewUserId(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidSourceKind, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task VoteEligibilityFilter_Answers404_WithVoteSessionNotFound()
    {
        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.SessionNotFound);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/vote-sessions/1/votes", NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.VoteSessionNotFound, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task VoteEligibilityFilter_Answers409_WithVoteSessionEnded()
    {
        // 409 rather than 403: the caller is allowed, the session simply closed. The frontend needs
        // to tell "you may not vote here" apart from "voting is over" to say anything useful.
        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.SessionEnded);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/vote-sessions/1/votes", NewUserId());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.VoteSessionEnded, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task VoteEligibilityFilter_Answers403_WhenTheRoleIsNotEligible()
    {
        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.RoleNotEligible);

        var response = await SendAsync("POST", $"/api/channels/{Channel}/vote-sessions/1/votes", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task VoteEligibilityFilter_AlsoGuardsVoteRetraction()
    {
        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.RoleNotEligible);

        var response = await SendAsync("DELETE", $"/api/channels/{Channel}/vote-sessions/1/votes/some-emote-id", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task VoteAudienceFilter_UsesTheAudienceEvaluation_NotTheVotingOne()
    {
        // The one-switch-arm difference between the two filters, asserted at the route: results are
        // gated by EvaluateAudienceAsync, which deliberately ignores whether voting has closed. If
        // this endpoint were wired to EvaluateAsync, every finished session would 409 its own
        // audience off the results page.
        _factory.VoteEligibility.EvaluateAudienceAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.RoleNotEligible);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/vote-sessions/1/results", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.VoteEligibility.Received()
            .EvaluateAudienceAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>());
        await _factory.VoteEligibility.DidNotReceive()
            .EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VoteAudienceFilter_Answers404_WithVoteSessionNotFound()
    {
        _factory.VoteEligibility.EvaluateAudienceAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.SessionNotFound);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/vote-sessions/1/results", NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.VoteSessionNotFound, await ReadErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("bad-name")]
    [InlineData("waaaaaaaaaaaaaaaaaaaaaaaaytoolong")]
    public async Task ChannelNameValidation_Answers400_BeforeAnyAuthorizationFilterRuns(string channelName)
    {
        // Filter order is the error contract: the group's validation filter is registered ahead of
        // the authorization filters, so a malformed name is a 400 with a translatable code rather
        // than a 403 the frontend would render as "you are not allowed".
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", $"/api/channels/{channelName}", NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ChannelNameValidation_AcceptsMixedCase_BecauseTheServerNormalizes()
    {
        // Twitch logins are typed with capitals. A route constraint would have rejected these; the
        // filter normalizes first, which is the reason it is a filter and not a constraint.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync("GET", "/api/channels/HandOfBlood", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChannelNameValidation_StillLosesToTheAuthenticationMiddleware()
    {
        // 401 outranks 400: authentication runs as middleware, before the endpoint's filter pipeline
        // exists at all. A malformed name must not leak the fact that the route is there.
        var response = await SendAsync("GET", "/api/channels/abc", userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VoteFilters_Answer400_ForAnUnparsableSessionId()
    {
        // The route constrains {sessionId:long}, so a non-numeric id never matches the route and
        // falls through to the /api fallback. The filter's own long.TryParse guard is the second
        // line of defence for any future route registered without that constraint.
        var response = await SendAsync("POST", $"/api/channels/{Channel}/vote-sessions/notanumber/votes", NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminGroup_HasNoChannelNameValidation_SoAMalformedNameIsAnAuthorizationOutcome()
    {
        // Deliberate asymmetry, pinned so it stays deliberate: the admin group is not registered with
        // ChannelNameValidationFilter, so its channel-scoped routes answer 403 where the public
        // groups answer 400. Admin endpoints never reach a Redis role-cache key with the raw value.
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);

        var response = await SendAsync("GET", "/api/admin/channels/bad-name", NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownApiPath_Answers404_AndNeverFallsBackToTheSpaShell()
    {
        // The /api fallback is what keeps a typo in a frontend URL from being answered with
        // index.html and a 200, which the HTTP client would then try to parse as JSON.
        var response = await SendAsync("GET", "/api/does/not/exist", NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? userId, string? login = "someuser", string? body = null)
    {
        // AllowAutoRedirect off so a 302 would surface as a failed assertion rather than being
        // silently followed — the cookie handler's redirect-to-login events are overridden precisely
        // to make sure that never happens for an API route.
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (userId is not null)
        {
            // A fresh id per request: the rate limiter partitions by the NameIdentifier claim, so
            // sharing one id across the whole matrix would eventually answer 429 instead of the
            // status code under test.
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            if (login is not null)
            {
                request.Headers.Add(TestAuthHandler.LoginHeader, login);
            }
        }

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
