using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The JSONL protocol is what makes a killed run cost one day instead of the whole window, and what
// makes the closing step repeatable. Both promises are file-level, so these tests use a real
// temporary directory rather than an abstraction over it — an in-memory file system would test the
// abstraction, not the promise.
public class HarnessReportFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "emotepurge-harness-tests-" + Guid.NewGuid().ToString("N"));

    public HarnessReportFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AWrittenHeader_ReadsBackWithTheSameIdentity()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc)));

        var header = file.ReadHeader(identity);

        Assert.Equal(identity.InputHash, header.Identity.InputHash);
        Assert.Equal(identity.WindowFrom, header.Identity.WindowFrom);
        Assert.Equal(identity.WindowTo, header.Identity.WindowTo);
        Assert.Equal(identity.BotAccountIds, header.Identity.BotAccountIds);
        Assert.Equal(new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc), header.LoadedAtUtc);
    }

    [Fact]
    public void AForeignIdentity_IsRefused()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        // Same file name, different snapshot — only reachable by renaming a file by hand, which is
        // exactly the case the head comparison exists for.
        var foreign = identity with { InputHash = new string('b', 64) };

        Assert.Throws<HarnessReportIdentityMismatchException>(() => file.ReadHeader(foreign));
    }

    [Fact]
    public void ATruncatedLastLine_IsDiscardedAndTheCompleteOnesSurvive()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        file.AppendDay(Day(new DateOnly(2026, 9, 3)));
        File.AppendAllText(file.Path, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-09-04\",\"stat");

        var content = file.ReadDays();

        Assert.Equal(2, content.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 2), content.Days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 3), content.Days[1].Day);
    }

    [Fact]
    public void ADayLine_KeepsItsCountsAcrossTheRoundTrip()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)) with
        {
            HumanCounts = new Dictionary<string, int> { ["e1"] = 4 },
            BotCounts = new Dictionary<string, int> { ["e1"] = 1 },
            KHistogram = [0, 2, 1],
            Bytes = 1234,
            BodySha256Hex = "abc"
        });

        var day = Assert.Single(file.ReadDays().Days);

        Assert.Equal(4, day.HumanCounts["e1"]);
        Assert.Equal(1, day.BotCounts["e1"]);
        Assert.Equal(new[] { 0, 2, 1 }, day.KHistogram);
        Assert.Equal(1234, day.Bytes);
        Assert.Equal("abc", day.BodySha256Hex);
    }

    [Fact]
    public void ADamagedLineInTheMiddle_IsRefusedRatherThanSilentlySkipped()
    {
        // Unlike a truncated last line, this is not an unfinished day — skipping it would leave a
        // gap that the final report would then present as a complete window.
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        File.AppendAllText(file.Path, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-09-\n");
        file.AppendDay(Day(new DateOnly(2026, 9, 4)));

        Assert.Throws<HarnessReportCorruptException>(() => file.ReadDays());
    }

    [Fact]
    public void EventLines_AreReadBackNextToTheDayLines()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        file.AppendEvent(new HarnessEventLine(
            DateTime.UtcNow, new DateOnly(2026, 9, 3), "RateLimited", 429, "gedrosselt"));

        var content = file.ReadDays();

        Assert.Single(content.Days);
        var line = Assert.Single(content.Events);
        Assert.Equal(429, line.HttpStatusCode);
        Assert.Equal(new DateOnly(2026, 9, 3), line.Day);
    }

    [Fact]
    public void TheFinalReport_IsWrittenAtomicallyAndLeavesNoTempFile()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        var report = ReplayFidelityCalculator.Compute(
            new ReplayWindow(identity.WindowFrom, identity.WindowTo, null), [], [], [], 3,
            runComplete: true, rateLimitedDays: 0, resumePoint: null);
        file.WriteFinalReportAtomically(report, "# Bericht\n");

        Assert.True(File.Exists(file.ReportJsonPath));
        Assert.True(File.Exists(file.ReportMarkdownPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Contains("\"run\"", File.ReadAllText(file.ReportJsonPath));
        Assert.Equal("# Bericht\n", File.ReadAllText(file.ReportMarkdownPath));
    }

    [Fact]
    public void TheFileName_CarriesChannelWindowAndIdentityDigest()
    {
        var identity = Identity();

        var name = HarnessReportFile.BuildFileName(identity);

        Assert.StartsWith("brudivoeller_tv-2026-09-02-2026-09-04-", name);
        Assert.EndsWith(".jsonl", name);
        // Two snapshots that differ only in their input hash must never share a file.
        Assert.NotEqual(name, HarnessReportFile.BuildFileName(identity with { InputHash = new string('c', 64) }));
    }

    private HarnessReportFile NewFile(HarnessRunIdentity identity) =>
        new(Path.Combine(_directory, HarnessReportFile.BuildFileName(identity)));

    private static HarnessRunIdentity Identity() =>
        new(
            "channel-guid",
            "12345",
            "brudivoeller_tv",
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 4),
            new DateOnly(2026, 9, 1),
            ["19264788", "402337290"],
            HarnessRunner.AlgorithmVersion,
            new string('a', 64));

    private static ReplayDayLine Day(DateOnly day) =>
        new(
            day,
            ReplayDayStatuses.Complete,
            0,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            0,
            [],
            0,
            0);
}
