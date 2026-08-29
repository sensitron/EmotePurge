using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The wire contract the Angular app mirrors (Regel 7). Pinned here rather than left to review
// discipline: the codes travel through two DTOs, two locale files and a key builder, and a typo in
// any of them degrades silently to "no reason at all" — which is exactly the state issue #32 was.
public class SevenTvSyncFailureReasonsTests
{
    [Fact]
    public void FromStatus_Ok_HasNoReason()
    {
        // A success must never carry a code: null is what makes "the last attempt worked" and
        // "nothing has been attempted yet" readable as the same absence downstream.
        Assert.Null(SevenTvSyncFailureReasons.FromStatus(SevenTvLookupStatus.Ok));
    }

    [Theory]
    [InlineData(SevenTvLookupStatus.NoSevenTvAccount, "no_seventv_account")]
    [InlineData(SevenTvLookupStatus.NoActiveEmoteSet, "no_active_emote_set")]
    [InlineData(SevenTvLookupStatus.Unavailable, "seventv_unavailable")]
    public void FromStatus_MapsEveryFailureToItsWireCode(SevenTvLookupStatus status, string expected)
    {
        Assert.Equal(expected, SevenTvSyncFailureReasons.FromStatus(status));
    }

    [Fact]
    public void FromStatus_UnknownStatus_Throws()
    {
        // A future enum member must not silently become "no failure" — that would put a channel
        // back into the mute state this whole change removes.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SevenTvSyncFailureReasons.FromStatus((SevenTvLookupStatus)99));
    }
}
