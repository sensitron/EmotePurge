using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ForeignEmoteSetProviderBudget"/> in isolation (spec 2026-09-09, E5b) — no Redis, no
/// decorator: just the concurrency gate and the rolling rate window. The cross-user, cross-channel
/// claim ("a third concurrent lookup for a *different* channel still waits") is
/// <c>HardenedForeignEmoteSetServiceTests</c>'s job in <c>Integration/</c>, since that needs the
/// decorator's real Redis cache dependency; this file only pins the budget's own two limits.
/// </summary>
public class ForeignEmoteSetProviderBudgetTests
{
    [Fact]
    public async Task WithinBothLimits_AcquiresImmediately()
    {
        using var budget = new ForeignEmoteSetProviderBudget(NewClock());

        var permit = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(permit);
        permit.Dispose();
    }

    /// <summary>
    /// A third caller beyond <see cref="ForeignEmoteSetProviderBudget.MaxConcurrent"/> waits rather
    /// than failing outright — and succeeds the moment an earlier permit is released, well inside the
    /// timeout it was given.
    /// </summary>
    [Fact]
    public async Task BeyondConcurrencyLimit_WaitsForARelease_ThenAcquires()
    {
        using var budget = new ForeignEmoteSetProviderBudget(NewClock());
        var first = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var thirdTask = budget.TryAcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50); // give the third caller a real chance to (wrongly) return early
        Assert.False(thirdTask.IsCompleted);

        first!.Dispose();
        var third = await thirdTask;

        Assert.NotNull(third);
        third!.Dispose();
        second!.Dispose();
    }

    /// <summary>A caller that is still waiting when its timeout elapses gets <c>null</c>, not an exception.</summary>
    [Fact]
    public async Task BeyondConcurrencyLimit_TimesOut_ReturnsNull_NotAnException()
    {
        using var budget = new ForeignEmoteSetProviderBudget(NewClock());
        var first = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var third = await budget.TryAcquireAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(third);
        first!.Dispose();
        second!.Dispose();
    }

    /// <summary>
    /// The rolling rate window (60 requests/minute): once spent within the current window, the next
    /// caller waits rather than getting an immediate extra permit — proven with a short real timeout
    /// rather than a fake clock, since <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>'s
    /// actual wait is governed by the <see cref="TimeProvider"/>'s timer, not by
    /// <see cref="TimeProvider.GetUtcNow"/> alone — a fake clock that only overrides the latter would
    /// not make this test run any faster, only less honest about what it is actually waiting on.
    /// </summary>
    [Fact]
    public async Task BeyondRateWindow_WithinTheSameWindow_WaitsThenTimesOut()
    {
        using var budget = new ForeignEmoteSetProviderBudget(TimeProvider.System);

        // Spend the window one at a time, releasing the concurrency slot immediately each time so
        // only the rate window (not the concurrency gate) is the constraint under test.
        for (var i = 0; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            var permit = await budget.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(permit);
            permit!.Dispose();
        }

        var overBudget = await budget.TryAcquireAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Null(overBudget);
    }

    private static TimeProvider NewClock() => TimeProvider.System;
}
