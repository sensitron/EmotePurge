using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EmotePurge.Api.Endpoints;
using EmotePurge.Core.Messaging;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Drives <c>LiveEndpoints.StreamAsync</c> directly (made <c>internal</c> for exactly this — the Api
/// csproj already has <c>InternalsVisibleTo</c> for this assembly) rather than through HTTP, because
/// the defect this covers only reproduces with a fake subscription whose <see cref="Events"/> is a
/// genuine compiler-generated async-iterator method blocking on a cancellation token — the same shape
/// as <c>RedisLiveEventStream.Subscription.ReadAsync</c> — not a canned NSubstitute return value.
/// <para>
/// The finding: the keepalive branch yields a heartbeat while the inner subscription's
/// <c>MoveNextAsync</c> is still pending (it is deliberately not re-issued on every tick, see
/// <c>StreamAsync</c>'s own comment on <c>pendingMoveNext</c>). If the SSE writer's next write throws
/// because the client is gone — the ordinary way ASP.NET Core learns of an abort — it disposes our
/// iterator right there, and disposing a compiler-generated async-iterator enumerator while a
/// <c>MoveNextAsync</c> on it is still in flight throws. The fix drains that pending call (cancelling
/// <c>lifetime</c> first to unblock it) before disposing the inner enumerator; both tests below drive
/// the enumerator to exactly that suspension point and then dispose.
/// </para>
/// <para>
/// A second, related finding (also issue #128): disposing the enumerator while it is suspended at the
/// very first <c>yield return</c> — the id-carrying first frame, before the inner try/finally above is
/// ever entered — used to leave <c>lifetime</c> uncancelled: the outer finally only disposed it, and
/// the inner finally's <c>lifetime.Cancel()</c> never ran because execution never reached it.
/// <see cref="LiveStreamConnectionRegistry.Register"/> wires its cleanup to <c>lifetime</c> being
/// cancelled, not disposed, so that path leaked the registry entry forever. The fix makes the outer
/// finally cancel <c>lifetime</c> unconditionally before disposing it; the last two tests below drive
/// the enumerator to exactly that earlier suspension point (one <c>MoveNextAsync</c>, then dispose
/// with no second one) and assert the registry entry is gone either way.
/// </para>
/// </summary>
public sealed class LiveStreamAsyncDisposalTests
{
    /// <summary>Arbitrary — only the two registry-leak tests below use it, and only to prove ownership.</summary>
    private const string SubscriberKey = "subscriber-1";

    [Fact]
    public async Task Dispose_RightAfterAKeepaliveYield_DoesNotThrow_AndDisposesTheSubscriptionExactlyOnce()
    {
        var subscription = new BlockingChannelSubscription();
        using var lifetime = new CancellationTokenSource();
        var keepaliveOptions = new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMilliseconds(30) };

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, "test-connection-id", lifetime.Token);
        var enumerator = enumerable.GetAsyncEnumerator();

        // The immediate first frame — yielded before the inner enumerator even exists.
        Assert.True(await enumerator.MoveNextAsync());

        // Nothing was published, so this is the Api-level keepalive tick: the inner MoveNextAsync
        // (blocked on the never-written channel) is still pending right now — exactly the finding's
        // scenario.
        Assert.True(await enumerator.MoveNextAsync());

        // Dispose without a further MoveNextAsync — the abort case from the review finding. Must not
        // throw, and must release the subscription exactly once.
        await enumerator.DisposeAsync();

        Assert.Equal(1, subscription.DisposeCount);
    }

    [Fact]
    public async Task Dispose_AfterTheLifetimeTokenIsCancelledWhileSuspendedThere_DoesNotThrow_AndDisposesTheSubscriptionExactlyOnce()
    {
        var subscription = new BlockingChannelSubscription();
        using var lifetime = new CancellationTokenSource();
        var keepaliveOptions = new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMilliseconds(30) };

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, "test-connection-id", lifetime.Token);
        var enumerator = enumerable.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync()); // immediate first frame
        Assert.True(await enumerator.MoveNextAsync()); // keepalive tick; inner MoveNextAsync still pending

        // A real client abort or MaxConnectionLifetime firing cancels this same token before the
        // consumer ever calls DisposeAsync — unlike the sibling test, where the cancelling is what the
        // fix itself performs from inside DisposeAsync. Either way, disposal afterwards must be safe.
        await lifetime.CancelAsync();

        await enumerator.DisposeAsync();

        Assert.Equal(1, subscription.DisposeCount);
    }

    [Fact]
    public async Task Dispose_RightAfterTheFirstYield_WhenNothingCancelsTheLifetimeExternally_StillRemovesTheRegistryEntry()
    {
        var subscription = new BlockingChannelSubscription();
        var registry = new LiveStreamConnectionRegistry();
        using var lifetime = new CancellationTokenSource();
        var connectionId = registry.Register(SubscriberKey, lifetime);
        var keepaliveOptions = new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMilliseconds(30) };

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, connectionId, lifetime.Token);
        var enumerator = enumerable.GetAsyncEnumerator();

        // The id-carrying first frame — nothing has entered the inner try/finally yet, so before the
        // fix nothing here would ever call lifetime.Cancel().
        Assert.True(await enumerator.MoveNextAsync());

        // Dispose right there, with no second MoveNextAsync and — the point of this variant — without
        // this test (standing in for RequestAborted or MaxConnectionLifetime) ever cancelling
        // `lifetime` itself. Only the outer finally's own unconditional Cancel() can make this work.
        await enumerator.DisposeAsync();

        Assert.Equal(1, subscription.DisposeCount);

        // The direct check, not just TryRelease's return value: TryRelease also answers false when the
        // entry is still present but Cancel() throws on an already-disposed CTS (see its own comment),
        // so that return value alone cannot tell "removed" apart from "leaked but unreleasable" — which
        // is exactly the distinction this test exists to make.
        Assert.False(registry.Contains(connectionId));
        Assert.False(registry.TryRelease(connectionId, SubscriberKey));
    }

    [Fact]
    public async Task Dispose_RightAfterTheFirstYield_WhenTheLifetimeWasAlreadyCancelledExternally_StillRemovesTheRegistryEntryExactlyOnce()
    {
        var subscription = new BlockingChannelSubscription();
        var registry = new LiveStreamConnectionRegistry();
        using var lifetime = new CancellationTokenSource();
        var connectionId = registry.Register(SubscriberKey, lifetime);
        var keepaliveOptions = new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMilliseconds(30) };

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, connectionId, lifetime.Token);
        var enumerator = enumerable.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync()); // the id-carrying first frame

        // Stands in for a real client abort landing at exactly this suspension point, before the
        // consumer calls DisposeAsync — proving the outer finally's Cancel() is safe (a documented
        // no-op) even when something external already cancelled the same token.
        await lifetime.CancelAsync();

        await enumerator.DisposeAsync();

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(registry.Contains(connectionId));
        Assert.False(registry.TryRelease(connectionId, SubscriberKey));
    }

    /// <summary>
    /// A hand-written <see cref="ILiveEventSubscription"/> whose <see cref="Events"/> is a real
    /// compiler-generated async-iterator method, written to match the shape of
    /// <c>RedisLiveEventStream.Subscription.ReadAsync</c>: it blocks on an unbounded channel that is
    /// never written to, using the token <c>GetAsyncEnumerator</c> was called with, so a
    /// <c>MoveNextAsync</c> against it stays genuinely pending until cancelled — not merely "returns a
    /// pre-completed task", which would not reproduce the defect.
    /// </summary>
    private sealed class BlockingChannelSubscription : ILiveEventSubscription
    {
        private readonly Channel<LiveEvent> _channel = Channel.CreateUnbounded<LiveEvent>();
        private int _disposeCount;

        public int DisposeCount => _disposeCount;

        public IAsyncEnumerable<LiveEvent> Events => ReadAsync();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<LiveEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                yield return await _channel.Reader.ReadAsync(cancellationToken);
            }
        }
    }
}
