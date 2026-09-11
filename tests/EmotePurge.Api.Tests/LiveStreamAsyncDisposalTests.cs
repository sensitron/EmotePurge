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
/// </summary>
public sealed class LiveStreamAsyncDisposalTests
{
    [Fact]
    public async Task Dispose_RightAfterAKeepaliveYield_DoesNotThrow_AndDisposesTheSubscriptionExactlyOnce()
    {
        var subscription = new BlockingChannelSubscription();
        using var lifetime = new CancellationTokenSource();
        var keepaliveOptions = new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMilliseconds(30) };

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, lifetime.Token);
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

        var enumerable = LiveEndpoints.StreamAsync(subscription, lifetime, keepaliveOptions, lifetime.Token);
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
