using Xunit;

namespace EmotePurge.Worker.Tests;

public class WorkerStatsTests
{
    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Initially_ReportsNothing()
    {
        var stats = new WorkerStats();

        Assert.Equal(0, stats.ConsecutiveFlushFailures);
        Assert.Null(stats.LastFlushSuccessUtc);
        Assert.Null(stats.LastFlushRowCount);
    }

    [Fact]
    public void RecordFlushSuccess_RecordsRowCountAndTimestamp()
    {
        var stats = new WorkerStats();

        stats.RecordFlushSuccess(42, Now);

        Assert.Equal(Now, stats.LastFlushSuccessUtc);
        Assert.Equal(42, stats.LastFlushRowCount);
        Assert.Equal(0, stats.ConsecutiveFlushFailures);
    }

    [Fact]
    public void RecordFlushFailure_IncrementsAndReturnsTheStreak()
    {
        var stats = new WorkerStats();

        Assert.Equal(1, stats.RecordFlushFailure());
        Assert.Equal(2, stats.RecordFlushFailure());
        Assert.Equal(3, stats.RecordFlushFailure());
        Assert.Equal(3, stats.ConsecutiveFlushFailures);
    }

    [Fact]
    public void RecordFlushSuccess_ResetsTheFailureStreak()
    {
        // The streak drives UsageFlushWorker's requeue-vs-drop decision, so a recovered flush must
        // hand the next outage the full five attempts again rather than resuming mid-count.
        var stats = new WorkerStats();
        stats.RecordFlushFailure();
        stats.RecordFlushFailure();

        stats.RecordFlushSuccess(7, Now);

        Assert.Equal(0, stats.ConsecutiveFlushFailures);
        Assert.Equal(1, stats.RecordFlushFailure());
    }

    [Fact]
    public void RecordFlushFailure_LeavesTheLastSuccessIntact()
    {
        // The monitoring page's "last successful flush" must keep pointing at the last success, not
        // blank out the moment the current cycle fails — that timestamp is how long the outage is.
        var stats = new WorkerStats();
        stats.RecordFlushSuccess(5, Now);

        stats.RecordFlushFailure();

        Assert.Equal(Now, stats.LastFlushSuccessUtc);
        Assert.Equal(5, stats.LastFlushRowCount);
    }

    [Fact]
    public void RecordFlushSuccess_OverwritesTheEarlierSuccess()
    {
        var stats = new WorkerStats();
        stats.RecordFlushSuccess(5, Now);

        var later = Now.AddSeconds(30);
        stats.RecordFlushSuccess(0, later);

        Assert.Equal(later, stats.LastFlushSuccessUtc);
        Assert.Equal(0, stats.LastFlushRowCount);
    }
}
