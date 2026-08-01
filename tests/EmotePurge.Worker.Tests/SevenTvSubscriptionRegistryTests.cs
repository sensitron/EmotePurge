using EmotePurge.Worker.SevenTv;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class SevenTvSubscriptionRegistryTests
{
    private const string SetA = "01SET0000000000000000000A0";
    private const string SetB = "01SET0000000000000000000B0";
    private const string UserA = "01USER000000000000000000A0";
    private const string UserB = "01USER000000000000000000B0";

    [Fact]
    public void SetDesired_NewChannel_ReportsChange()
    {
        var registry = new SevenTvSubscriptionRegistry();

        Assert.True(registry.SetDesired("sensitron", SetA, UserA));
        Assert.False(registry.SetDesired("sensitron", SetA, UserA));
        Assert.True(registry.SetDesired("sensitron", SetB, UserA));
    }

    [Fact]
    public void SetDesired_NormalizesChannelNames()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("  SensiTron  ", SetA, UserA);

        Assert.False(registry.SetDesired("sensitron", SetA, UserA));
        Assert.Equal(["sensitron"], registry.DesiredChannels);
        Assert.True(registry.TryRemove("SENSITRON"));
    }

    [Fact]
    public void GetChannelStates_ReportsOneRowPerChannel_WithAcknowledgements()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.SetDesired("olaf_olaf_son", SetB, UserB);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.UserSubscriptionType, UserA);

        var states = registry.GetChannelStates();

        Assert.Equal(2, states.Count);
        var acked = Assert.Single(states, s => s.ChannelName == "sensitron");
        Assert.True(acked.EmoteSetAcknowledged);
        Assert.True(acked.UserAcknowledged);

        var pending = Assert.Single(states, s => s.ChannelName == "olaf_olaf_son");
        Assert.False(pending.EmoteSetAcknowledged);
        Assert.False(pending.UserAcknowledged);
    }

    [Fact]
    public void GetChannelStates_SharedSet_ReportsBothChannelsAcknowledged()
    {
        // One subscription covers both channels, so neither is missing anything — reporting the
        // second as unacknowledged would invent a deficit that no resubscribe could ever close.
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.SetDesired("olaf_olaf_son", SetA, UserB);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA);

        Assert.All(registry.GetChannelStates(), s => Assert.True(s.EmoteSetAcknowledged));
    }

    [Fact]
    public void GetChannelStates_WithoutAUserId_ReportsNoUserSubscription()
    {
        // A channel whose 7TV user id was never resolved desires no user subscription at all.
        // UserAcknowledged is false, but SevenTvUserId being null is what says "nothing pending" —
        // otherwise the roster shows a permanently outstanding acknowledgement.
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, sevenTvUserId: null);

        var state = Assert.Single(registry.GetChannelStates());
        Assert.Null(state.SevenTvUserId);
        Assert.False(state.UserAcknowledged);
    }

    [Fact]
    public void GetChannelStates_AfterResetAcknowledgements_ReportsEverythingPendingAgain()
    {
        // Every Hello resets acknowledgements before resubscribing; the roster has to follow, or a
        // reconnect that silently failed to resubscribe keeps reading as healthy.
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA);
        registry.ResetAcknowledgements();

        var state = Assert.Single(registry.GetChannelStates());
        Assert.False(state.EmoteSetAcknowledged);
    }

    [Fact]
    public void BuildDesiredSubscriptions_SharedSet_YieldsOneSetSubscription()
    {
        // Two channels sharing one active set is a real, live-observed configuration; the socket
        // must hold exactly one emote_set.* subscription for it.
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.SetDesired("olaf_olaf_son", SetA, UserB);

        var subscriptions = registry.BuildDesiredSubscriptions();

        Assert.Single(subscriptions, s => s.Type == SevenTvSubscriptionRegistry.EmoteSetSubscriptionType);
        Assert.Equal(2, subscriptions.Count(s => s.Type == SevenTvSubscriptionRegistry.UserSubscriptionType));
        Assert.Equal(3, subscriptions.Count);
    }

    [Fact]
    public void BuildDesiredSubscriptions_WithoutUserId_OmitsUserSubscription()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, sevenTvUserId: null);

        var subscriptions = registry.BuildDesiredSubscriptions();

        Assert.Equal([new SevenTvSubscription(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA)], subscriptions);
    }

    [Fact]
    public void BuildDesiredSubscriptions_AfterRemovingOneSharedChannel_KeepsSetSubscription()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.SetDesired("olaf_olaf_son", SetA, UserB);

        registry.TryRemove("olaf_olaf_son");
        var afterFirstRemove = registry.BuildDesiredSubscriptions();
        Assert.Contains(new SevenTvSubscription(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA), afterFirstRemove);
        Assert.DoesNotContain(new SevenTvSubscription(SevenTvSubscriptionRegistry.UserSubscriptionType, UserB), afterFirstRemove);

        registry.TryRemove("sensitron");
        Assert.Empty(registry.BuildDesiredSubscriptions());
    }

    [Fact]
    public void GetChannelsForEmoteSet_SharedSet_ReturnsAllChannels()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.SetDesired("olaf_olaf_son", SetA, UserB);
        registry.SetDesired("other", SetB, null);

        var channels = registry.GetChannelsForEmoteSet(SetA);

        Assert.Equal(["olaf_olaf_son", "sensitron"], [.. channels.Order()]);
        Assert.Empty(registry.GetChannelsForEmoteSet("01UNKNOWN0000000000000000"));
    }

    [Fact]
    public void TryGetChannelForUser_FindsChannel()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);

        Assert.True(registry.TryGetChannelForUser(UserA, out var channel));
        Assert.Equal("sensitron", channel);
        Assert.False(registry.TryGetChannelForUser(UserB, out _));
    }

    [Fact]
    public void UnacknowledgedCount_TracksAcksAgainstDesiredState()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        Assert.Equal(2, registry.UnacknowledgedCount);

        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA);
        Assert.Equal(1, registry.UnacknowledgedCount);

        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.UserSubscriptionType, UserA);
        Assert.Equal(0, registry.UnacknowledgedCount);
    }

    [Fact]
    public void ResetAcknowledgements_NewSessionConfirmsNothing()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("sensitron", SetA, UserA);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.EmoteSetSubscriptionType, SetA);
        registry.MarkAcknowledged(SevenTvSubscriptionRegistry.UserSubscriptionType, UserA);

        registry.ResetAcknowledgements();

        Assert.Equal(2, registry.UnacknowledgedCount);
    }
}
