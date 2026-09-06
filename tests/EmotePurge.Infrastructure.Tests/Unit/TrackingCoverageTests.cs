using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container needed. TrackingCoverage is BCL-only in Core, and
// CoreAssemblyReferenceTests guards that; this test only exercises the one-line rule itself.
public class TrackingCoverageTests
{
    [Fact]
    public void TrackedSince_NoResumption_FallsBackToCreation()
    {
        var createdAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);

        var trackedSince = TrackingCoverage.TrackedSince(null, createdAt);

        Assert.Equal(createdAt, trackedSince);
    }

    [Fact]
    public void TrackedSince_Resumed_PrefersTheResumption()
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var resumedAt = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        var trackedSince = TrackingCoverage.TrackedSince(resumedAt, createdAt);

        Assert.Equal(resumedAt, trackedSince);
    }

    [Fact]
    public void TrackedSince_ResumptionBeforeCreation_StillWins()
    {
        // The function does not judge plausibility — it only picks which of the two timestamps to
        // trust. A resumption stamped before the creation date is a data question for someone else,
        // not a case for this function to special-case.
        var createdAt = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var resumedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var trackedSince = TrackingCoverage.TrackedSince(resumedAt, createdAt);

        Assert.Equal(resumedAt, trackedSince);
    }
}
