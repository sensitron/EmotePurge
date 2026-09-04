using System.Reflection;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Issue #61, the nachlese to #55: the same five result types that #55 held back. "Payload is
// non-null iff Status is Ok" was a comment on a record, and a comment is not an invariant —
// Failed(SevenTvLookupStatus.Ok) built the forbidden value in one call and `with` built it in
// another. These cases pin the three ways in, per type: the failure factory, the constructor, and
// the record clone.
//
// The reflection cases are the ones that survive a careless edit: turning any of these back into a
// record re-opens both doors silently, and only NoPublicConstructor_AndNoWithExpression notices.
public class SevenTvResultTypesTests
{
    public static TheoryData<Type> ClosedResultTypes =>
    [
        typeof(SevenTvChannelStateResult),
        typeof(SevenTvTwitchUserIdResult),
        typeof(SevenTvIdentityResult),
        typeof(SevenTvEditorGrantsResult),
        typeof(SevenTvEditorGrantsLookupResult),
    ];

    [Theory]
    [MemberData(nameof(ClosedResultTypes))]
    public void NoPublicConstructor_AndNoWithExpression(Type type)
    {
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // `with` compiles to a call to the compiler-generated clone method every record carries; a
        // type without it cannot be the target of a with-expression.
        Assert.Null(type.GetMethod(
            "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void Failed_WithTheSuccessStatus_Throws()
    {
        AssertRejectsStatus(() => SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Ok));
        AssertRejectsStatus(() => SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Ok));
        AssertRejectsStatus(() => SevenTvIdentityResult.Failed(SevenTvLookupStatus.Ok));
        AssertRejectsStatus(() => SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Ok));
        AssertRejectsStatus(() => SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Ok));
    }

    [Fact]
    public void Failed_WithAnUndefinedStatus_Throws()
    {
        // The cast that matches no arm of any caller's switch over SevenTvLookupStatus — it can no
        // longer reach the type at all.
        const SevenTvLookupStatus Undefined = (SevenTvLookupStatus)9;

        AssertRejectsStatus(() => SevenTvChannelStateResult.Failed(Undefined));
        AssertRejectsStatus(() => SevenTvTwitchUserIdResult.Failed(Undefined));
        AssertRejectsStatus(() => SevenTvIdentityResult.Failed(Undefined));
        AssertRejectsStatus(() => SevenTvEditorGrantsResult.Failed(Undefined));
        AssertRejectsStatus(() => SevenTvEditorGrantsLookupResult.Failed(Undefined));
    }

    [Fact]
    public void SuccessFactories_WithoutAPayload_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SevenTvChannelStateResult.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => SevenTvTwitchUserIdResult.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => SevenTvIdentityResult.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => SevenTvEditorGrantsResult.Ok(null!));
        Assert.Throws<ArgumentNullException>(() => SevenTvEditorGrantsLookupResult.Ok(null!));
    }

    [Fact]
    public void Factories_ProduceTheOnlyTwoLegalShapes()
    {
        var state = new SevenTvChannelState("7tvuser", new SevenTvEmoteSet("set", []));
        var channelState = SevenTvChannelStateResult.Ok(state);
        Assert.Equal(SevenTvLookupStatus.Ok, channelState.Status);
        Assert.Same(state, channelState.State);
        Assert.Null(SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet).State);

        var twitchUserId = SevenTvTwitchUserIdResult.Ok("123");
        Assert.Equal(SevenTvLookupStatus.Ok, twitchUserId.Status);
        Assert.Equal("123", twitchUserId.TwitchUserId);
        Assert.Null(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable).TwitchUserId);

        var identity = new SevenTvIdentity("7tvuser", "set");
        var identityResult = SevenTvIdentityResult.Ok(identity);
        Assert.Equal(SevenTvLookupStatus.Ok, identityResult.Status);
        Assert.Same(identity, identityResult.Identity);
        Assert.Null(SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount).Identity);

        // An empty grant set is a legitimate Ok on both grant types — "answered: this user edits
        // nothing" is not a failure, and the null guard must not turn it into one.
        var grants = SevenTvEditorGrantsResult.Ok([]);
        Assert.Equal(SevenTvLookupStatus.Ok, grants.Status);
        Assert.Empty(grants.Grants!);
        Assert.Null(SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Unavailable).Grants);

        var lookupGrants = SevenTvEditorGrantsLookupResult.Ok(
            new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>()));
        Assert.Equal(SevenTvLookupStatus.Ok, lookupGrants.Status);
        Assert.Empty(lookupGrants.Grants!.ChannelLogins);
        Assert.Null(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable).Grants);
    }

    private static void AssertRejectsStatus(Func<object> factory)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => factory());
        Assert.Equal("status", ex.ParamName);
    }
}
