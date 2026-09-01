using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Messaging;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Pins the shape of the cross-channel live stream's route. <c>/api/channels/live-events</c> is a
/// literal sitting next to <c>GET /api/channels/{channelName}</c> — same segment count, so it exists
/// as its own endpoint only by routing precedence (a literal segment outranks a parameter) — and it
/// must stay free of <see cref="ChannelNameValidationFilter"/>, which would answer 400
/// <c>invalid_channel_name</c> for a route value it never has. Neither property shows up in a green
/// build; both would surface only as a dead stream nobody is watching.
/// <para>
/// Also pins the 503/429 split <c>LiveEndpoints.OpenAsync</c> renders for the two ways
/// <c>ILiveEventStream.SubscribeAsync</c> can fail (issue #42) — before this, both collapsed onto the
/// same bare 503. <see cref="ApiFactory.LiveEventStream"/> is driven directly rather than starving a
/// real <c>RedisLiveEventStream</c> of Redis, which is the shape the two Infrastructure-level test
/// classes already cover.
/// </para>
/// </summary>
public class LiveRouteStructureTests : IClassFixture<ApiFactory>
{
    private const string LiveEventsPath = "/api/channels/live-events";

    private readonly ApiFactory _factory;

    public LiveRouteStructureTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void LiveEventsRoute_ExistsAsItsOwnParameterFreeEndpoint()
    {
        // Resolving from Services boots the host; the endpoints exist only afterwards.
        var endpointDataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(candidate =>
                string.Equals(
                    candidate.RoutePattern.RawText?.TrimStart('/'),
                    "api/channels/live-events",
                    StringComparison.Ordinal));

        Assert.NotNull(endpoint);

        // Endpoint filters registered via AddEndpointFilter<T>() are wrapped in the request delegate
        // and never surface as metadata — verified against the running host, not assumed. The check
        // stays as documentation of the intent; the assertion with teeth is the one below.
        Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is ChannelNameValidationFilter);

        // What actually keeps the filter harmless here: the pattern binds no route parameter at all,
        // so there is no channelName for ChannelNameValidationFilter to reject. A future refactor
        // that folded this route into the /api/channels/{channelName} shape would break here.
        Assert.Empty(endpoint.RoutePattern.Parameters);
    }

    [Fact]
    public async Task LiveEventsRoute_Answers503WithErrorCode_WhenInfrastructureIsUnavailable()
    {
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.InfrastructureUnavailable));

        using var client = _factory.CreateClient();
        using var response = await SendAsync(client, "live-route-infra");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.LiveStreamUnavailable,
            body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task LiveEventsRoute_Answers429WithRetryAfterAndErrorCode_WhenTheQuotaIsExhausted()
    {
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.QuotaExhausted));

        using var client = _factory.CreateClient();
        using var response = await SendAsync(client, "live-route-quota");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfterSeconds = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.True(retryAfterSeconds > 0);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.LiveStreamQuotaExhausted,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryAfterSeconds, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LiveEventsPath);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }
}
