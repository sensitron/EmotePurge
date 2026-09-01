using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure decision logic, no container and no TwitchChatManager needed — the roster and the active-set
// are both passed in. Every rule under test traces back to issue #41: a lost Redis LEAVE command
// never pruned _desiredChannels, so the worker kept matching chat, holding the 7TV EventAPI
// subscription and reporting the channel forever.
public class RosterPrunePolicyTests
{
    [Fact]
    public void DetermineChannelsToPrune_ConfirmedChannelStaleForTwoConsecutiveTicks_IsPruned()
    {
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, firstTick.StaleChannels);

        Assert.Equal(["handofblood"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ChannelStillActive_IsNotPruned()
    {
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ActiveComparisonIsCaseInsensitive()
    {
        // ChannelService normalizes to lowercase (Regel 9), but nothing here should rely on that —
        // a mismatch in casing between the DB read and the roster must not look like "not active".
        var roster = new[] { new TwitchRosterEntry("HandOfBlood", JoinConfirmed: true, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_UnconfirmedChannelStaleForOnlyOneTick_IsNotPruned()
    {
        // The guard against the race where a channel just joined (unconfirmed) can appear in the
        // roster moments after this tick's DB snapshot was taken but before it reflects the new row
        // — see the class comment on RosterPrunePolicy. A single stale tick must not prune it: that
        // would drop a channel that was never actually inactive.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: false, LastMessageUtc: null) };

        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
        Assert.Equal(["handofblood"], toPrune.StaleChannels);
    }

    [Fact]
    public void DetermineChannelsToPrune_UnconfirmedChannelStaleForTwoConsecutiveTicks_IsPrunedAnyway()
    {
        // The fix for the hole the pure JoinConfirmed guard left open: an entry that never confirms
        // (banned bot account, deleted channel, a hanging JOIN) must still be pruned eventually, not
        // held forever just because nothing ever flips JoinConfirmed to true.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: false, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, firstTick.StaleChannels);

        Assert.Equal(["handofblood"], secondTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_ChannelBecomesActiveAgainBetweenStaleTicks_ClearsItsStaleState()
    {
        // A channel stale on tick 1 that is active again on tick 2 must not be pruned on tick 2, and
        // must not carry a leftover stale marker into tick 3 that would prune it despite being active
        // in between.
        var roster = new[] { new TwitchRosterEntry("handofblood", JoinConfirmed: true, LastMessageUtc: null) };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, previouslyStaleChannels: []);
        Assert.Equal(["handofblood"], firstTick.StaleChannels);

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster, firstTick.StaleChannels);
        Assert.Empty(secondTick.ChannelsToPrune);
        Assert.Empty(secondTick.StaleChannels);

        var thirdTick = RosterPrunePolicy.DetermineChannelsToPrune(activeChannels: [], roster, secondTick.StaleChannels);
        Assert.Empty(thirdTick.ChannelsToPrune);
    }

    [Fact]
    public void DetermineChannelsToPrune_MixedRoster_OnlyPrunesTheEntriesStaleForTwoConsecutiveTicks()
    {
        var roster = new[]
        {
            new TwitchRosterEntry("stillactive", JoinConfirmed: true, LastMessageUtc: null),
            new TwitchRosterEntry("leftbehind", JoinConfirmed: true, LastMessageUtc: null),
            new TwitchRosterEntry("justjoining", JoinConfirmed: false, LastMessageUtc: null),
        };

        var firstTick = RosterPrunePolicy.DetermineChannelsToPrune(["stillactive"], roster, previouslyStaleChannels: []);
        Assert.Empty(firstTick.ChannelsToPrune);
        Assert.Equal(new HashSet<string>(["leftbehind", "justjoining"]), new HashSet<string>(firstTick.StaleChannels));

        var secondTick = RosterPrunePolicy.DetermineChannelsToPrune(["stillactive"], roster, firstTick.StaleChannels);

        Assert.Equal(
            new HashSet<string>(["leftbehind", "justjoining"]),
            new HashSet<string>(secondTick.ChannelsToPrune));
    }

    [Fact]
    public void DetermineChannelsToPrune_EmptyRoster_PrunesNothing()
    {
        var toPrune = RosterPrunePolicy.DetermineChannelsToPrune(["handofblood"], roster: [], previouslyStaleChannels: []);

        Assert.Empty(toPrune.ChannelsToPrune);
        Assert.Empty(toPrune.StaleChannels);
    }
}
