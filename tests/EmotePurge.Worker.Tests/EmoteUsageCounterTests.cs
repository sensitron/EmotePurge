using EmotePurge.Core.Chat;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="EmoteUsageCounter.PendingEmoteCount"/>, added for the admin monitoring page,
/// and the three-way category split (#73) introduced alongside <see cref="IBotChatterDetector"/> and
/// Shared Chat. <see cref="EmoteUsageCounter.PendingEmoteCount"/> counts distinct buffered emotes,
/// not the sum of their hits — the tests pin that down, because the two only differ once an emote is
/// seen twice. <see cref="UsageCategoryRule"/> is covered here too: it lives in the same file as
/// <see cref="UsageCategory"/> and is the one place the room-vs-bot precedence is decided.
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
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("b", UsageCategory.Human);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void DrainAndReset_EmptiesThePendingCount()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("b", UsageCategory.Human);

        counter.DrainAndReset();

        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void Merge_RestoresThePendingCount()
    {
        // The requeue path after a failed flush: the monitoring page must show the backlog again,
        // otherwise a failing flush looks like an idle worker.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("b", UsageCategory.Human);
        var drained = counter.DrainAndReset();

        counter.Merge(drained);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void Increment_KeepsAllThreeCategoriesOfTheSameEmoteSeparate()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("a", UsageCategory.Bot);
        counter.Increment("a", UsageCategory.SharedChat);
        counter.Increment("a", UsageCategory.SharedChat);
        counter.Increment("a", UsageCategory.SharedChat);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 2, Bot: 1, SharedChat: 3), drained["a"]);
    }

    [Fact]
    public void Merge_AddsAllThreeComponentsOntoExistingEntries()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("a", UsageCategory.Bot);
        counter.Increment("a", UsageCategory.SharedChat);

        counter.Merge(new Dictionary<string, EmoteUsageCounts>
        {
            ["a"] = new EmoteUsageCounts(Human: 3, Bot: 2, SharedChat: 4),
        });

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 4, Bot: 3, SharedChat: 5), drained["a"]);
    }

    [Fact]
    public void DrainAndReset_ReturnsAllThreeComponentsAndThenEmptiesTheCounter()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Human);
        counter.Increment("b", UsageCategory.Bot);
        counter.Increment("c", UsageCategory.SharedChat);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 1, Bot: 0, SharedChat: 0), drained["a"]);
        Assert.Equal(new EmoteUsageCounts(Human: 0, Bot: 1, SharedChat: 0), drained["b"]);
        Assert.Equal(new EmoteUsageCounts(Human: 0, Bot: 0, SharedChat: 1), drained["c"]);
        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsAnEmoteSeenOnlyFromBotsAsOneEmote()
    {
        // Semantics unchanged by the split: PendingEmoteCount is the number of distinct emotes
        // buffered, regardless of whether every hit so far came from a bot.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.Bot);
        counter.Increment("a", UsageCategory.Bot);

        Assert.Equal(1, counter.PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsAnEmoteSeenOnlyFromForeignRoomsAsOneEmote()
    {
        // Same guarantee as the bot-only case above, now for the room split: an emote only ever
        // matched in a Shared Chat mirror still counts as one buffered emote.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", UsageCategory.SharedChat);
        counter.Increment("a", UsageCategory.SharedChat);

        Assert.Equal(1, counter.PendingEmoteCount);
    }

    [Theory]
    [InlineData(MessageOrigin.Own, false, UsageCategory.Human)]
    [InlineData(MessageOrigin.Own, true, UsageCategory.Bot)]
    [InlineData(MessageOrigin.Foreign, false, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Foreign, true, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Indeterminate, false, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Indeterminate, true, UsageCategory.SharedChat)]
    public void Resolve_PrioritizesRoomOverBot_AndFoldsIndeterminateIntoSharedChat(
        MessageOrigin origin, bool isBot, UsageCategory expected)
    {
        Assert.Equal(expected, UsageCategoryRule.Resolve(origin, isBot));
    }
}
