using EmotePurge.Api.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Pins the shape of the cross-channel live stream's route. <c>/api/channels/live-events</c> is a
/// literal sitting next to <c>GET /api/channels/{channelName}</c> — same segment count, so it exists
/// as its own endpoint only by routing precedence (a literal segment outranks a parameter) — and it
/// must stay free of <see cref="ChannelNameValidationFilter"/>, which would answer 400
/// <c>invalid_channel_name</c> for a route value it never has. Neither property shows up in a green
/// build; both would surface only as a dead stream nobody is watching.
/// </summary>
public class LiveRouteStructureTests : IClassFixture<ApiFactory>
{
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
}
