using System.Reflection;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Issue #55: "Channel is non-null iff Status is Joined" used to be a doc comment on a record, and a
// doc comment is not an invariant — Failed(ChannelJoinStatus.Joined) built the forbidden value in
// one call, and `with` built it in another. These cases pin the three ways in: the failure factory,
// the constructor, and the record clone.
public class ChannelJoinResultTests
{
    [Fact]
    public void Failed_WithTheSuccessStatus_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ChannelJoinResult.Failed(ChannelJoinStatus.Joined));

        Assert.Equal("status", ex.ParamName);
    }

    [Fact]
    public void Failed_WithAnUndefinedStatus_Throws()
    {
        // The cast the join endpoint's switch cannot match, and the reason CS8524 is silenced there:
        // it can no longer reach the type at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelJoinResult.Failed((ChannelJoinStatus)7));
    }

    [Fact]
    public void Joined_WithoutAChannel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChannelJoinResult.Joined(null!));
    }

    [Fact]
    public void Factories_ProduceTheOnlyTwoLegalShapes()
    {
        var joined = ChannelJoinResult.Joined(new Channel { ChannelName = "somechannel" });
        Assert.Equal(ChannelJoinStatus.Joined, joined.Status);
        Assert.NotNull(joined.Channel);

        var failed = ChannelJoinResult.Failed(ChannelJoinStatus.ChannelNotOnTwitch);
        Assert.Equal(ChannelJoinStatus.ChannelNotOnTwitch, failed.Status);
        Assert.Null(failed.Channel);
    }

    // The two escape hatches a caller does not have to call a factory to use. Asserted by reflection
    // rather than by a commented-out line of code, because the point is that they stay shut as the
    // type is edited: making it a record again, or widening the constructor, turns these red.
    [Fact]
    public void NoPublicConstructor_AndNoWithExpression()
    {
        Assert.Empty(typeof(ChannelJoinResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // `with` compiles to a call to the compiler-generated clone method every record carries; a
        // type without it cannot be the target of a with-expression.
        Assert.Null(typeof(ChannelJoinResult).GetMethod(
            "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
