using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The filter matrix for the new <c>/api/seventv/channels/{channelName}/emotes</c> group
/// (foreign-channel-import spec, section 4/AK 14) — the group's whole reason to exist is that it
/// carries no <c>UsageStatsAccessAuthorizationFilter</c>, and this file is what proves the group
/// actually reflects that instead of just claiming it in a comment.
/// </summary>
/// <remarks>
/// The two budget-exhaustion cases pin the ordering the spec calls out explicitly as deliberate:
/// <c>UseRateLimiter</c> is middleware and therefore always runs before the endpoint's
/// <see cref="ChannelNameValidationFilter"/>, so an invalid channel name over a spent budget answers
/// 429, not the 400 it would get with budget still available — the opposite of every other
/// channel-scoped group's contract, and worth its own regression here rather than being inferred from
/// <c>AuthFilterMatrixTests</c>.
/// </remarks>
public class SevenTvForeignEmoteSetEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string InvalidChannel = "!!";

    private readonly ApiFactory _factory;

    public SevenTvForeignEmoteSetEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.ForeignEmoteSet.ClearReceivedCalls();
    }

    [Fact]
    public async Task AnonymousCaller_Gets401()
    {
        var response = await SendAsync(Channel, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoggedInCaller_WithNoRoleInTheChannel_Gets200()
    {
        var emoteSet = new ForeignEmoteSet(
            Channel, "01FRY81K4800085N93FNKSBYXS", "01FRY81K4800085N93FNKSBYXS-set", 1, false,
            [new ForeignEmoteRow("e1", "Alias", "Default", "https://cdn.7tv.app/emote/e1/4x_static.webp", 500, 12)]);
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(emoteSet));

        var response = await SendAsync(Channel, NewUserId());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Channel, body.GetProperty("channelName").GetString());
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());

        // The whole point of this group (spec section 4): no role check of any kind runs. A
        // logged-in caller with zero relationship to the channel still gets 200, and nothing here
        // ever asked the usage-stats authorization service anything.
        await _factory.ChannelAccess.DidNotReceive().CanViewUsageStatsAsync(
            Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ForeignEmoteSetLookupStatus.ChannelNotOnTwitch, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotOnTwitch)]
    [InlineData(ForeignEmoteSetLookupStatus.TwitchUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelTwitchUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.NoSevenTvAccount, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoSevenTvAccount)]
    [InlineData(ForeignEmoteSetLookupStatus.NoActiveEmoteSet, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoActiveEmoteSet)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvRateLimited, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    public async Task EveryFailureStatus_MapsToItsDocumentedResponse(
        ForeignEmoteSetLookupStatus status, HttpStatusCode expectedStatusCode, string expectedErrorCode)
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(status));

        var response = await SendAsync(Channel, NewUserId());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedErrorCode, await ReadErrorCodeAsync(response));
    }

    /// <summary>T2, spec E3: <c>?refresh=true</c> reaches the service as <c>refresh: true</c>.</summary>
    [Fact]
    public async Task RefreshQueryParam_IsPassedThroughToTheService()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        await SendAsync(Channel, NewUserId(), refresh: true);

        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetAsync(Channel, true, Arg.Any<CancellationToken>());
    }

    /// <summary>The counterpart: an absent query string reaches the service as <c>refresh: false</c>,
    /// not as a 400 for a "missing" parameter — the whole point of giving it a C# default.</summary>
    [Fact]
    public async Task AbsentRefreshQueryParam_DefaultsToFalse()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        var response = await SendAsync(Channel, NewUserId());

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetAsync(Channel, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidChannelName_WithBudgetStillAvailable_Gets400()
    {
        var response = await SendAsync(InvalidChannel, NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    /// <summary>
    /// AK 14's central, easy-to-get-backwards case: the rate limiter is middleware and runs before
    /// <see cref="ChannelNameValidationFilter"/> can, so once the budget is spent an invalid name no
    /// longer gets the 400 the previous test just proved it gets with budget to spare — it gets 429
    /// like everything else. This is documented as deliberate (spec section 4), not a bug.
    /// </summary>
    [Fact]
    public async Task InvalidChannelName_OverBudget_Gets429_NotThe400ItWouldGetOtherwise()
    {
        var userId = NewUserId();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        // Spend the ForeignEmoteLookup budget (10/min, RateLimitingOptions.ForeignEmoteLookup) on
        // valid requests first — ChannelNameValidationFilter never rejects these, so every one of
        // them is genuinely a permit spent, not a request the filter would have refused anyway.
        for (var i = 0; i < 10; i++)
        {
            var spend = await SendAsync(Channel, userId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, spend.StatusCode);
        }

        var response = await SendAsync(InvalidChannel, userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>The other reading of the same ordering: a caller who spends the budget on nothing but
    /// requests the group's own handler would happily serve still gets 429 on the next one — the
    /// budget itself, not the name filter, is what stops them.</summary>
    [Fact]
    public async Task ValidChannelName_OverBudget_Gets429()
    {
        var userId = NewUserId();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        for (var i = 0; i < 10; i++)
        {
            await SendAsync(Channel, userId);
        }

        var response = await SendAsync(Channel, userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string channelName, string? userId, bool refresh = false)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var path = $"/api/seventv/channels/{channelName}/emotes" + (refresh ? "?refresh=true" : "");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }
}
