using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class TwitchTokenRefreshGateTests
{
    [Fact]
    public async Task AcquireAsync_SameUser_SerialisesAccess()
    {
        var gate = new TwitchTokenRefreshGate();

        var first = await gate.AcquireAsync("user-1");
        var secondAttempt = gate.AcquireAsync("user-1");

        Assert.False(secondAttempt.IsCompleted);
        first.Dispose();
        (await secondAttempt).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DifferentUsers_DoNotBlockEachOther()
    {
        var gate = new TwitchTokenRefreshGate();

        using var first = await gate.AcquireAsync("user-a");
        var secondAttempt = gate.AcquireAsync("user-b");

        Assert.True(secondAttempt.IsCompleted);
        (await secondAttempt).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ManyParallelCallers_NeverOverlapInTheCriticalSection()
    {
        var gate = new TwitchTokenRefreshGate();
        var inside = 0;
        var maxInside = 0;

        var callers = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var lease = await gate.AcquireAsync("user-flight");
            var now = Interlocked.Increment(ref inside);
            InterlockedExtensions.Max(ref maxInside, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref inside);
        });

        await Task.WhenAll(callers);

        Assert.Equal(1, maxInside);
    }

    [Fact]
    public void NeedsValidation_BeforeAnyMark_IsTrue()
    {
        var gate = new TwitchTokenRefreshGate();

        Assert.True(gate.NeedsValidation("user-v", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void NeedsValidation_AfterMark_IsFalseWithinMaxAge_TrueAtZeroMaxAge()
    {
        var gate = new TwitchTokenRefreshGate();

        gate.MarkValidated("user-v");

        Assert.False(gate.NeedsValidation("user-v", TimeSpan.FromHours(1)));
        // Zero max age means "always revalidate" — stands in for the elapsed hour without a clock.
        Assert.True(gate.NeedsValidation("user-v", TimeSpan.Zero));
    }

    [Fact]
    public void NeedsValidation_IsPerUser()
    {
        var gate = new TwitchTokenRefreshGate();

        gate.MarkValidated("user-x");

        Assert.True(gate.NeedsValidation("user-y", TimeSpan.FromHours(1)));
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
