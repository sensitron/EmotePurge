using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class DuplicateEmoteNameTrackerTests
{
    private readonly DuplicateEmoteNameTracker _tracker = new();

    [Fact]
    public void Update_FirstEmptySet_IsNotAChange()
    {
        Assert.False(_tracker.Update("somechannel", []));
    }

    [Fact]
    public void Update_FirstNonEmptySet_IsAChange()
    {
        Assert.True(_tracker.Update("somechannel", ["Dup"]));
    }

    [Fact]
    public void Update_SameSetAgain_IsNotAChange_RegardlessOfOrder()
    {
        _tracker.Update("somechannel", ["Alpha", "Beta"]);

        Assert.False(_tracker.Update("somechannel", ["Beta", "Alpha"]));
    }

    [Fact]
    public void Update_ChangedSet_IsAChange()
    {
        _tracker.Update("somechannel", ["Alpha"]);

        Assert.True(_tracker.Update("somechannel", ["Alpha", "Beta"]));
    }

    [Fact]
    public void Update_EmptyAfterNonEmpty_IsAChangeExactlyOnce()
    {
        _tracker.Update("somechannel", ["Dup"]);

        Assert.True(_tracker.Update("somechannel", []));
        Assert.False(_tracker.Update("somechannel", []));
    }

    [Fact]
    public void Update_RepeatedInputEntries_CountAsOneName()
    {
        _tracker.Update("somechannel", ["Dup", "Dup"]);

        Assert.False(_tracker.Update("somechannel", ["Dup"]));
    }

    [Fact]
    public void Update_ComparesNamesCaseSensitively()
    {
        _tracker.Update("somechannel", ["Dup"]);

        Assert.True(_tracker.Update("somechannel", ["dup"]));
    }

    [Fact]
    public void Update_NormalizesTheChannelName()
    {
        _tracker.Update("SomeChannel", ["Dup"]);

        Assert.False(_tracker.Update("  somechannel ", ["Dup"]));
    }

    [Fact]
    public void Update_TracksChannelsIndependently()
    {
        _tracker.Update("channelone", ["Dup"]);

        Assert.True(_tracker.Update("channeltwo", ["Dup"]));
    }
}
