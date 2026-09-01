using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="EmoteUsageCounter.PendingEmoteCount"/>, added for the admin monitoring page,
/// and the human/bot split introduced alongside <see cref="IBotChatterDetector"/>.
/// <see cref="EmoteUsageCounter.PendingEmoteCount"/> counts distinct buffered emotes, not the sum
/// of their hits — the tests pin that down, because the two only differ once an emote is seen
/// twice.
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
        counter.Increment("a", isBot: false);
        counter.Increment("a", isBot: false);
        counter.Increment("a", isBot: false);
        counter.Increment("b", isBot: false);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void DrainAndReset_EmptiesThePendingCount()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: false);
        counter.Increment("b", isBot: false);

        counter.DrainAndReset();

        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void Merge_RestoresThePendingCount()
    {
        // The requeue path after a failed flush: the monitoring page must show the backlog again,
        // otherwise a failing flush looks like an idle worker.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: false);
        counter.Increment("b", isBot: false);
        var drained = counter.DrainAndReset();

        counter.Merge(drained);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void Increment_KeepsHumanAndBotHitsOfTheSameEmoteSeparate()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: false);
        counter.Increment("a", isBot: false);
        counter.Increment("a", isBot: true);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 2, Bot: 1), drained["a"]);
    }

    [Fact]
    public void Merge_AddsBothComponentsOntoExistingEntries()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: false);
        counter.Increment("a", isBot: true);

        counter.Merge(new Dictionary<string, EmoteUsageCounts>
        {
            ["a"] = new EmoteUsageCounts(Human: 3, Bot: 2),
        });

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 4, Bot: 3), drained["a"]);
    }

    [Fact]
    public void DrainAndReset_ReturnsBothComponentsAndThenEmptiesTheCounter()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: false);
        counter.Increment("b", isBot: true);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 1, Bot: 0), drained["a"]);
        Assert.Equal(new EmoteUsageCounts(Human: 0, Bot: 1), drained["b"]);
        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsAnEmoteSeenOnlyFromBotsAsOneEmote()
    {
        // Semantics unchanged by the split: PendingEmoteCount is the number of distinct emotes
        // buffered, regardless of whether every hit so far came from a bot.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", isBot: true);
        counter.Increment("a", isBot: true);

        Assert.Equal(1, counter.PendingEmoteCount);
    }
}
