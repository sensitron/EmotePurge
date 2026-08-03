using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The three-way decision both list endpoints share. The case worth pinning is the asymmetry:
// absence from the live set only means "offline" for a channel that was actually polled — for
// everything else (no snapshot, bot inactive) it must stay "unknown".
public class ChannelLiveStatesTests
{
    [Fact]
    public void Derive_ReturnsUnknown_WhenNoSnapshotExists()
    {
        Assert.Equal(ChannelLiveStates.Unknown, ChannelLiveStates.Derive(null, "handofblood", wasPolled: true));
    }

    [Fact]
    public void Derive_ReturnsLive_WhenTheChannelIsInTheLiveSet()
    {
        var live = new HashSet<string> { "handofblood" };

        Assert.Equal(ChannelLiveStates.Live, ChannelLiveStates.Derive(live, "handofblood", wasPolled: true));
    }

    [Fact]
    public void Derive_ReturnsOffline_OnlyForAPolledChannelAbsentFromTheLiveSet()
    {
        var live = new HashSet<string> { "someoneelse" };

        Assert.Equal(ChannelLiveStates.Offline, ChannelLiveStates.Derive(live, "handofblood", wasPolled: true));
    }

    [Fact]
    public void Derive_ReturnsUnknown_ForAChannelTheWorkerNeverPolled()
    {
        // An inactive channel is not in the poll set — absence from the live set proves nothing.
        var live = new HashSet<string> { "someoneelse" };

        Assert.Equal(ChannelLiveStates.Unknown, ChannelLiveStates.Derive(live, "handofblood", wasPolled: false));
    }
}
