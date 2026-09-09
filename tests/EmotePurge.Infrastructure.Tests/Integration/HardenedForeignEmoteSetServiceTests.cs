using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The hardening decorator (spec 2026-09-09, section 6/T2) against real Redis: the 60 s cache (E3),
/// <c>refresh=true</c> bypassing it, request coalescing, and the provider-wide budget (E5b) actually
/// being shared across different source channels rather than partitioned like the per-user ASP.NET
/// policy. The breaker (E4) has its own pure, container-free suite
/// (<c>ForeignSevenTvBreakerPolicyTests</c>) — here it only needs to stay out of the way, so every
/// test uses a fresh, always-allowing breaker.
/// </summary>
[Collection("Redis")]
public class HardenedForeignEmoteSetServiceTests(RedisFixture fixture)
{
    [Fact]
    public async Task ASuccessfulLookup_IsCached_AndTheSecondCallNeverReachesTheInnerChain()
    {
        var channel = NewChannel();
        var inner = InnerReturning(channel, callCount: out var calls);
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetAsync(channel);
        var second = await service.GetForeignEmoteSetAsync(channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, first.Status);
        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, second.Status);
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task ACacheEntry_CarriesRoughlyTheSpecdSixtySecondTtl()
    {
        var channel = NewChannel();
        var inner = InnerReturning(channel, callCount: out _);
        var service = CreateService(inner);

        await service.GetForeignEmoteSetAsync(channel);

        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync($"7tvforeign:{ChannelName.Normalize(channel)}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task Refresh_BypassesTheCache_AndOverwritesItWithTheFreshAnswer()
    {
        var channel = NewChannel();
        var callCount = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);
                return Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: n)));
            });
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetAsync(channel);
        var refreshed = await service.GetForeignEmoteSetAsync(channel, refresh: true);
        var thirdPlain = await service.GetForeignEmoteSetAsync(channel);

        Assert.Equal(1, first.EmoteSet!.TotalCount);
        // refresh=true reached the inner chain a second time instead of answering from the cache
        // primed by the first call.
        Assert.Equal(2, refreshed.EmoteSet!.TotalCount);
        Assert.Equal(2, callCount);
        // And the refresh's answer overwrote the cache — a later plain call sees it, not the stale
        // first answer, and does not cost a third inner call.
        Assert.Equal(2, thirdPlain.EmoteSet!.TotalCount);
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// Parallel identical lookups for the same channel share one upstream execution (spec section 6,
    /// "Koaleszierung") — proven with an inner chain that blocks until released, so every concurrent
    /// caller is genuinely in flight at once rather than serialized by accident.
    /// </summary>
    [Fact]
    public async Task ConcurrentLookupsForTheSameChannel_ShareOneUpstreamExecution()
    {
        var channel = NewChannel();
        var gate = new TaskCompletionSource();
        var callCount = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref callCount);
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var tasks = Enumerable.Range(0, 5).Select(_ => service.GetForeignEmoteSetAsync(channel)).ToArray();
        await Task.Delay(200); // let every caller genuinely arrive and coalesce before releasing
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, callCount);
        Assert.All(results, r => Assert.Equal(ForeignEmoteSetLookupStatus.Ok, r.Status));
    }

    /// <summary>
    /// AK 9 / E5b: the provider-wide budget is shared across *different* channels — the dimension the
    /// per-user ASP.NET policy cannot see at all. Three distinct channels (standing in for three
    /// different users' lookups) against a two-slot budget: the third genuinely waits instead of
    /// reaching the inner chain immediately.
    /// </summary>
    [Fact]
    public async Task ThirdConcurrentChannel_WaitsForABudgetSlot_InsteadOfCallingTheInnerChainImmediately()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var channelC = NewChannel();
        var gate = new TaskCompletionSource();
        var inFlight = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref inFlight);
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var taskA = service.GetForeignEmoteSetAsync(channelA);
        var taskB = service.GetForeignEmoteSetAsync(channelB);
        await Task.Delay(200); // let A and B genuinely occupy both budget slots

        Assert.Equal(2, Volatile.Read(ref inFlight));
        var taskC = service.GetForeignEmoteSetAsync(channelC);
        await Task.Delay(200); // C must still be waiting for a slot, not calling the inner chain
        Assert.Equal(2, Volatile.Read(ref inFlight));

        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB, taskC);

        Assert.Equal(3, Volatile.Read(ref inFlight));
        Assert.All(results, r => Assert.Equal(ForeignEmoteSetLookupStatus.Ok, r.Status));
    }

    /// <summary>The other half of AK 9: once a slot frees, the waiting channel actually proceeds and
    /// completes successfully rather than timing out just because it had to wait at all.</summary>
    [Fact]
    public async Task AfterASlotFrees_TheWaitingChannel_CompletesSuccessfully()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var releaseFirstOnly = new TaskCompletionSource();
        var neverReleased = new TaskCompletionSource();
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(channelA, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await releaseFirstOnly.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        inner.GetForeignEmoteSetAsync(channelB, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await neverReleased.Task; // occupies the second slot for the whole test
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var taskA = service.GetForeignEmoteSetAsync(channelA);
        var taskB = service.GetForeignEmoteSetAsync(channelB);
        await Task.Delay(200);
        releaseFirstOnly.SetResult();

        var resultA = await taskA;
        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, resultA.Status);

        neverReleased.SetResult();
        await taskB;
    }

    private HardenedForeignEmoteSetService CreateService(IForeignEmoteSetService inner) => new(
        inner,
        new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance),
        new ForeignEmoteSetRequestCoalescer(),
        new ForeignSevenTvBreakerPolicy(),
        new ForeignEmoteSetProviderBudget(),
        new RecordingRateLimitTelemetry(),
        NullLogger<HardenedForeignEmoteSetService>.Instance);

    private static IForeignEmoteSetService InnerReturning(string channel, out Func<int> callCount)
    {
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1)));
            });
        callCount = () => Volatile.Read(ref calls);
        return inner;
    }

    private static ForeignEmoteSet NewEmoteSet(string channelName, int totalCount) => new(
        ChannelName.Normalize(channelName),
        "01FRY81K4800085N93FNKSBYXS",
        "01FRY81K4800085N93FNKSBYXS-set",
        totalCount,
        false,
        []);

    // A fresh, random channel per test so tests sharing one Redis container can never collide on the
    // same cache key.
    private static string NewChannel() => $"foreign-{Guid.NewGuid():N}"[..24];
}
