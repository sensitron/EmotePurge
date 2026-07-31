using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="EmoteUsageCounter.PendingEmoteCount"/>, added for the admin monitoring page.
/// It counts distinct buffered emotes, not the sum of their hits — the tests pin that down, because
/// the two only differ once an emote is seen twice.
/// </summary>
public class EmoteUsageCounterTests
{
    [Fact]
    public void PendingEmoteCount_StartsAtZero()
    {
        Assert.Equal(0, new EmoteUsageCounter().PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsDistinctEmotesNotHits()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a");
        counter.Increment("a");
        counter.Increment("a");
        counter.Increment("b");

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void DrainAndReset_EmptiesThePendingCount()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a");
        counter.Increment("b");

        counter.DrainAndReset();

        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void Merge_RestoresThePendingCount()
    {
        // The requeue path after a failed flush: the monitoring page must show the backlog again,
        // otherwise a failing flush looks like an idle worker.
        var counter = new EmoteUsageCounter();
        counter.Increment("a");
        counter.Increment("b");
        var drained = counter.DrainAndReset();

        counter.Merge(drained);

        Assert.Equal(2, counter.PendingEmoteCount);
    }
}
