using System.Net;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The machine-facing health endpoint (review Z1 rest / S3-35): payload-free, 200 only while the
/// worker's Twitch pipeline is connected and fresh, 503 otherwise — so <c>curl -f</c> in a
/// container HEALTHCHECK and the external uptime monitor get their answer from the status code
/// alone. A missing snapshot (expired Redis key, worker gone) is the dead-man case and must read
/// as 503, not as "no data, all fine".
/// </summary>
public class ApiHealthEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiHealthEndpointTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MissingSnapshot_Answers503()
    {
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns((WorkerHealthSnapshot?)null);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task DisconnectedWorker_Answers503()
    {
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkerHealthSnapshot(false, null, null));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StaleConnection_Answers503()
    {
        // Connected flag still true, but no frame for an hour — the silent-freeze case the badge
        // endpoint reports as "stale".
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkerHealthSnapshot(
                true, null, null, TwitchLastFrameUtc: DateTime.UtcNow.AddHours(-1)));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task HealthyWorker_Answers200WithoutPayload()
    {
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkerHealthSnapshot(
                true, null, null, TwitchLastFrameUtc: DateTime.UtcNow));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }
}
