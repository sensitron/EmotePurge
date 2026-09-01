using System.Net;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to Integration/ChannelResyncCooldownTests.cs (real Redis, happy path).
// Reproduces what issue #41 asked to decide explicitly rather than reflexively: what
// POST /{channelName}/resync should do while Redis is unreachable. Resolved fail-open (see
// docs/DECISIONS.md, 2026-09-01) — the per-channel cooldown is only half a guard; the endpoint's own
// per-user ASP.NET rate limiter (RateLimitPolicyNames.ChannelResync) is entirely in-process and keeps
// working regardless of Redis, so losing the per-channel half during an outage is a bounded,
// temporary risk, not a hole. The alternative (fail-closed, a 503) would instead make self-service
// resync entirely unavailable for the whole outage over a guard that is UX cost-control, not a
// security boundary.
public class ChannelResyncCooldownFailureModeTests
{
    [Fact]
    public async Task TryBeginAsync_RedisConnectionFails_AcquiresInsteadOfThrowing()
    {
        var cooldown = CreateCooldownWithFailingRedis();

        var state = await cooldown.TryBeginAsync("handofblood");

        // Fail-open: the resync is let through rather than turning into a 500/503 for the caller.
        Assert.True(state.Acquired);
        Assert.Equal(0, state.RetryAfterSeconds);
    }

    [Fact]
    public async Task ReleaseAsync_RedisConnectionFails_DoesNotThrow()
    {
        var cooldown = CreateCooldownWithFailingRedis();

        // ReleaseAsync sits right after TriggerResyncAsync on the not-triggered path — without this
        // guard the request would still die here, just one line later than TryBeginAsync.
        var exception = await Record.ExceptionAsync(() => cooldown.ReleaseAsync("handofblood"));

        Assert.Null(exception);
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SevenTv:ManualResyncCooldownSeconds"] = "60" })
            .Build();

    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static ChannelResyncCooldown CreateCooldownWithFailingRedis()
    {
        var database = Substitute.For<IDatabase>();
        // The overload TryBeginAsync actually calls: (RedisKey, RedisValue, TimeSpan?, When). A
        // different arity/overload (Expiration + ValueCondition) exists on IDatabaseAsync, so the
        // exact shape here matters — an Arg.Any<Expiration> mock would silently not match this call
        // and the real (unmocked) substitute would just return default(bool) instead of throwing.
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns<bool>(_ => throw BuildConnectionException());
        database.KeyDeleteAsync(Arg.Any<RedisKey>())
            .Returns<bool>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);
        connectionMultiplexer.GetEndPoints().Returns(Array.Empty<EndPoint>());

        return new ChannelResyncCooldown(connectionMultiplexer, BuildConfiguration(), NullLogger<ChannelResyncCooldown>.Instance);
    }
}
