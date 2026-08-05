using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure like the other worker policies. The null-baseline case is the load-bearing one: without it,
// the first poll after a cold start (no Redis snapshot survived) would fire one event per live
// channel and make every open tab refetch for transitions that never happened.
public class LiveStatusDiffTests
{
    [Fact]
    public void Compute_WithoutBaseline_ReportsNoChanges()
    {
        var changes = LiveStatusDiff.Compute(null, ["alpha", "beta"]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Compute_ChannelWentLive_ReportsIt()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string>(), ["alpha"]);

        Assert.Equal(["alpha"], changes.WentLive);
        Assert.Empty(changes.WentOffline);
    }

    [Fact]
    public void Compute_ChannelWentOffline_ReportsIt()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha" }, []);

        Assert.Empty(changes.WentLive);
        Assert.Equal(["alpha"], changes.WentOffline);
    }

    [Fact]
    public void Compute_UnchangedState_ReportsNothing()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha" }, ["alpha"]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Compute_BothDirectionsAtOnce_ReportsBoth()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string> { "alpha", "beta" }, ["beta", "gamma"]);

        Assert.Equal(["gamma"], changes.WentLive);
        Assert.Equal(["alpha"], changes.WentOffline);
    }

    [Fact]
    public void Compute_EmptyToEmpty_ReportsNothing()
    {
        var changes = LiveStatusDiff.Compute(new HashSet<string>(), []);

        Assert.True(changes.IsEmpty);
    }
}
