using System.Reflection;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Same defect, same fix as ChannelJoinResult (issue #55): a "found" lookup with no identity behind
// it is the one value every caller of this type assumes cannot exist, so no construction path may
// produce it.
public class TwitchUserLookupTests
{
    [Fact]
    public void Failed_WithTheSuccessStatus_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TwitchUserLookup.Failed(TwitchUserLookupStatus.Found));

        Assert.Equal("status", ex.ParamName);
    }

    [Fact]
    public void Failed_WithAnUndefinedStatus_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TwitchUserLookup.Failed((TwitchUserLookupStatus)9));
    }

    [Fact]
    public void Found_WithoutAnIdentity_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TwitchUserLookup.Found(null!));
    }

    [Fact]
    public void Factories_ProduceTheOnlyTwoLegalShapes()
    {
        var found = TwitchUserLookup.Found(new TwitchUserIdentity("770001", "somechannel"));
        Assert.Equal(TwitchUserLookupStatus.Found, found.Status);
        Assert.NotNull(found.User);

        // Unavailable rather than NotFound on purpose: the two failure statuses must both be legal
        // here, since collapsing them is what the type exists to prevent.
        var unavailable = TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable);
        Assert.Equal(TwitchUserLookupStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.User);
    }

    [Fact]
    public void NoPublicConstructor_AndNoWithExpression()
    {
        Assert.Empty(typeof(TwitchUserLookup).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(TwitchUserLookup).GetMethod(
            "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
