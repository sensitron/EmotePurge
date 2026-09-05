using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="ReplayFidelityCalculator"/>, the final-report half of the chat-log backfill
/// harness (#69). The pre-registered gate numbers (total deviation, top-20 recall, bottom-quartile
/// precision, rated days, window length) are published in issue #69 and are not negotiable, so
/// every one of them is pinned here against a hand-computed value rather than against whatever the
/// implementation happens to produce.
/// </summary>
public class ReplayFidelityCalculatorTests
{
    private const int HistogramLength = 11;
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, int> Empty = new(StringComparer.Ordinal);

    [Fact]
    public void IdenticalCounts_YieldPerfectScores()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(30, report.Gate.RatedDays);
        Assert.Equal(32, report.Gate.PopulationSize);
        Assert.Equal(0d, report.Gate.TotalDeviation!.Value, 6);
        Assert.Equal(1d, report.Gate.Top20Recall!.Value, 6);
        Assert.Equal(1d, report.Gate.BottomQuartilePrecision!.Value, 6);
        Assert.True(report.Gate.GateEligible);
        Assert.Empty(report.Gate.GateIneligibleReasons);
        Assert.Equal(1d, report.Diagnostics.StableSubsetSpearman!.Value, 6);
        Assert.Equal(0d, report.Diagnostics.StableSubsetMedianDeviation!.Value, 6);
        Assert.Equal(0d, report.Diagnostics.StableSubsetP90Deviation!.Value, 6);
        Assert.True(report.Diagnostics.StableSubsetDecisive);
    }

    [Fact]
    public void LogOnlyEmote_RaisesTotalDeviation()
    {
        var (emotes, perDay) = BaseSet();
        emotes.Add(Emote("z"));
        var log = new Dictionary<string, int>(perDay, StringComparer.Ordinal) { ["z"] = 1 };
        var (days, rows) = Build(30, log, perDay);

        var report = Compute(emotes, rows, days);

        // Sum live = 30 * (1+..+32) = 15840; the log-only emote adds 30 to the absolute difference.
        Assert.Equal(33, report.Gate.PopulationSize);
        Assert.Equal(0.0019, report.Gate.TotalDeviation!.Value, 6);
        Assert.Equal(1, report.Diagnostics.LogOnlyEmoteCount);
        Assert.Equal(30d / 15870d, report.Diagnostics.LogOnlyShareOfLogTotal!.Value, 4);
    }

    [Fact]
    public void LiveOnlyEmote_RaisesTotalDeviation()
    {
        var (emotes, perDay) = BaseSet();
        emotes.Add(Emote("y"));
        var live = new Dictionary<string, int>(perDay, StringComparer.Ordinal) { ["y"] = 5 };
        var (days, rows) = Build(30, perDay, live);

        var report = Compute(emotes, rows, days);

        // Sum live = 15840 + 150 = 15990; the live-only emote contributes 150 absolute difference.
        Assert.Equal(33, report.Gate.PopulationSize);
        Assert.Equal(0.0094, report.Gate.TotalDeviation!.Value, 6);
        Assert.Equal(1, report.Diagnostics.LiveOnlyEmoteCount);
        Assert.Equal(150d / 15990d, report.Diagnostics.LiveOnlyShareOfLiveTotal!.Value, 4);
    }

    [Fact]
    public void DayWithoutAnyLiveUsage_IsFlaggedAsGapAndLeftOutOfTheGate()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);
        var lastDay = From.AddDays(29);
        rows.RemoveAll(r => r.Date == lastDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(29, report.Gate.RatedDays);
        Assert.Equal(lastDay, Assert.Single(report.Diagnostics.LiveGapDays).Day);
        Assert.Equal(0d, report.Gate.TotalDeviation!.Value, 6);
        // 30 log days against 29 live days: 528 surplus hits over 29 * 528 live hits.
        Assert.Equal(0.0345, report.Diagnostics.TotalDeviationIncludingFlaggedDays!.Value, 6);
        Assert.Equal(30, report.Diagnostics.FlaggedIncludedDays);
    }

    [Fact]
    public void DayWithTripleRatio_IsFlaggedAsQuestionableCoverage()
    {
        var (emotes, perDay) = BaseSet();
        var tripled = perDay.ToDictionary(p => p.Key, p => p.Value * 3, StringComparer.Ordinal);
        var (days, rows) = Build(30, perDay, perDay);
        var oddDay = From.AddDays(10);
        days[10] = DayLine(oddDay, tripled);

        var report = Compute(emotes, rows, days);

        Assert.Equal(29, report.Gate.RatedDays);
        Assert.Equal(1d, report.Diagnostics.DayRatioMedian!.Value, 6);
        var flagged = Assert.Single(report.Diagnostics.CoverageQuestionableDays);
        Assert.Equal(oddDay, flagged.Day);
        Assert.Equal(3d, flagged.Ratio!.Value, 6);
    }

    [Fact]
    public void DaysBeforeTheBotSplitCutover_CountForPlausibilityButNotForTheGate()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, cutover: From.AddDays(10));

        Assert.Equal(20, report.Gate.RatedDays);
        Assert.Equal(20, report.Diagnostics.HumanOnlyDays);
        Assert.Equal(30, report.Diagnostics.LogDays);
        Assert.Equal(30 * 528, report.Plausibility.LogTotalWithBots);
        Assert.Equal(30 * 528, report.Plausibility.LiveTotalWithBots);
        Assert.True(report.Gate.GateEligible);
    }

    [Fact]
    public void FewerThanTwentyRatedDays_MakeTheGateIneligible()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, cutover: From.AddDays(11));

        Assert.Equal(19, report.Gate.RatedDays);
        Assert.False(report.Gate.GateEligible);
        Assert.Contains(ReplayGateIneligibleReasons.RatedDaysBelowTwenty, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void ShortWindow_MakesTheGateIneligible()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, windowDays: 3);

        Assert.False(report.Gate.GateEligible);
        Assert.Contains(ReplayGateIneligibleReasons.WindowNotThirtyDays, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void IncompleteRun_MakesTheGateIneligible()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, runComplete: false);

        Assert.False(report.Gate.GateEligible);
        Assert.Contains(ReplayGateIneligibleReasons.RunIncomplete, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void MissingBotSplitCutover_LeavesNoRatedDay()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, null), emotes, rows, days, 30, true);

        Assert.Equal(0, report.Diagnostics.HumanOnlyDays);
        Assert.Equal(0, report.Gate.RatedDays);
        Assert.False(report.Gate.GateEligible);
        Assert.Null(report.Gate.TotalDeviation);
        Assert.Contains(ReplayGateIneligibleReasons.LiveTotalZero, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void StableSubset_ExcludesRecentSyncRecentArchiveAndAmbiguousNames()
    {
        var (emotes, perDay) = BaseSet();
        emotes.Add(Emote("s1", lastSyncedAt: From.ToDateTime(TimeOnly.MinValue).AddDays(5)));
        emotes.Add(Emote("s2", archivedAt: From.ToDateTime(TimeOnly.MinValue).AddDays(5)));
        emotes.Add(Emote("amb1", name: "dup"));
        emotes.Add(Emote("amb2", name: "dup"));
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(36, emotes.Count);
        Assert.Equal(32, report.Diagnostics.StableSubsetSize);
        Assert.Equal(1, report.Diagnostics.AmbiguousNameCount);
    }

    [Fact]
    public void MinimumLiveUses_ScalesWithRatedDays()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, cutover: From.AddDays(10));

        // ceil(20 * 20 / 30) = 14
        Assert.Equal(20, report.Gate.RatedDays);
        Assert.Equal(14, report.Diagnostics.MinLiveUsesN);
    }

    [Fact]
    public void MinimumLiveUses_HasAFloorOfFive()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(5, perDay, perDay);

        var report = Compute(emotes, rows, days);

        // ceil(20 * 5 / 30) = 4, raised to the floor of 5
        Assert.Equal(5, report.Gate.RatedDays);
        Assert.Equal(5, report.Diagnostics.MinLiveUsesN);
    }

    [Fact]
    public void FewerThanThirtyQualifiedEmotes_MakeTheStableSubsetIndecisive()
    {
        var (emotes, perDay) = BaseSet(6);
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(6, report.Diagnostics.QualifiedEmoteCount);
        Assert.Equal(30, report.Diagnostics.RequiredQualifiedM);
        Assert.False(report.Diagnostics.StableSubsetDecisive);
    }

    [Fact]
    public void QualifiedEmoteWithoutAnyLogHits_CountsAsFullDeviation()
    {
        var (emotes, perDay) = BaseSet();
        var log = new Dictionary<string, int>(perDay, StringComparer.Ordinal);
        foreach (var id in new[] { "e00", "e01", "e02", "e03" })
        {
            log.Remove(id);
        }

        var (days, rows) = Build(30, log, perDay);

        var report = Compute(emotes, rows, days);

        // 28 deviations of 0 and four of 1.0: nearest-rank median is 0, nearest-rank p90 is 1.0.
        Assert.Equal(32, report.Diagnostics.QualifiedEmoteCount);
        Assert.Equal(4, report.Diagnostics.StableSubsetZeroLogCount);
        Assert.Equal(0d, report.Diagnostics.StableSubsetMedianDeviation!.Value, 6);
        Assert.Equal(1d, report.Diagnostics.StableSubsetP90Deviation!.Value, 6);
    }

    [Fact]
    public void Spearman_UsesMidRanksOnTies()
    {
        // Hand computation. Live totals over 20 days: e0 20, e1 20, e2 40, e3 60, e4 80, e5 100
        // -> mid ranks 1.5, 1.5, 3, 4, 5, 6. Log totals: e0 20, e1 40, e2 20, e3 60, e4 100, e5 80
        // -> mid ranks 1.5, 3, 1.5, 4, 6, 5. Both rank vectors have mean 3.5.
        // Sum dx*dy = 13.75, sum dx^2 = sum dy^2 = 17  ->  r = 13.75 / 17 = 0.808824.
        var emotes = new List<ReplayEmote> { Emote("e0"), Emote("e1"), Emote("e2"), Emote("e3"), Emote("e4"), Emote("e5") };
        var live = Counts(("e0", 1), ("e1", 1), ("e2", 2), ("e3", 3), ("e4", 4), ("e5", 5));
        var log = Counts(("e0", 1), ("e1", 2), ("e2", 1), ("e3", 3), ("e4", 5), ("e5", 4));
        var (days, rows) = Build(20, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(14, report.Diagnostics.MinLiveUsesN);
        Assert.Equal(6, report.Diagnostics.QualifiedEmoteCount);
        Assert.Equal(0.8088, report.Diagnostics.StableSubsetSpearman!.Value, 6);
        Assert.Equal(0.8088, report.Diagnostics.AllEmotesSpearman!.Value, 6);
    }

    [Fact]
    public void RankTies_AreBrokenByEmoteIdOrdinal()
    {
        // Live totals a 400, b 400, c 800, d 1200; descending with ordinal id as tie-break gives
        // d, c, a, b, so the bottom quartile (floor(4/4) = 1) is {b}. Log totals a 200, b 600,
        // c 800, d 1200 have no tie and their bottom quartile is {a} -> precision 0.
        var emotes = new List<ReplayEmote> { Emote("a"), Emote("b"), Emote("c"), Emote("d") };
        var live = Counts(("a", 20), ("b", 20), ("c", 40), ("d", 60));
        var log = Counts(("a", 10), ("b", 30), ("c", 40), ("d", 60));
        var (days, rows) = Build(20, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(1, report.Gate.BottomQuartileSize);
        Assert.Equal(0d, report.Gate.BottomQuartilePrecision!.Value, 6);
        Assert.Equal(4, report.Gate.Top20Size);
        Assert.Equal(1d, report.Gate.Top20Recall!.Value, 6);
        Assert.Equal(0.1429, report.Gate.TotalDeviation!.Value, 6);
    }

    [Fact]
    public void Plausibility_ComparesBothSidesIncludingBots()
    {
        var emotes = new List<ReplayEmote> { Emote("x") };
        var days = new List<ReplayDayLine>();
        var rows = new List<ReplayUsageRow>();
        for (var i = 0; i < 10; i++)
        {
            var day = From.AddDays(i);
            days.Add(DayLine(day, Counts(("x", 10)), Counts(("x", 5))));
            rows.Add(new ReplayUsageRow("x", day, 10, 4));
        }

        var report = Compute(emotes, rows, days);

        Assert.Equal(150, report.Plausibility.LogTotalWithBots);
        Assert.Equal(140, report.Plausibility.LiveTotalWithBots);
        Assert.Equal(10, report.Plausibility.Difference);
        Assert.Equal(1.0714, report.Plausibility.Ratio!.Value, 6);
    }

    [Fact]
    public void EmptyRun_LeavesEveryGateNumberUndefined()
    {
        var report = Compute([], [], []);

        Assert.Equal(0, report.Gate.PopulationSize);
        Assert.Null(report.Gate.TotalDeviation);
        Assert.Null(report.Gate.Top20Recall);
        Assert.Null(report.Gate.BottomQuartilePrecision);
        Assert.Null(report.Diagnostics.DayRatioMedian);
        Assert.Null(report.Run.ResumePoint);
        Assert.False(report.Gate.GateEligible);
    }

    [Fact]
    public void Diagnostics_AggregateThePerDayCounters()
    {
        var emotes = new List<ReplayEmote>
        {
            Emote("x"),
            Emote("orphan", isArchivedWithoutDate: true),
        };
        var unmatched = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [UnmatchedReason.UnknownName.ToString()] = 7,
            [UnmatchedReason.AmbiguousName.ToString()] = 2,
        };
        var days = new List<ReplayDayLine>
        {
            DayLine(From, Counts(("x", 3)), unmatched: unmatched, firstSeenUnknownHits: 4, bytes: 100,
                messageCount: 50, botMessageCount: 6, sharedChat: 2, outsideDay: 1, nonPrivmsg: 8, malformed: 1,
                histogram: Histogram((1, 3), (2, 1)), cellCount: 4, distinctChatters: 9),
            DayLine(From.AddDays(1), Counts(("x", 3)), unmatched: unmatched, firstSeenUnknownHits: 1, bytes: 200,
                messageCount: 10, botMessageCount: 1, sharedChat: 3, outsideDay: 2, nonPrivmsg: 1, malformed: 4,
                histogram: Histogram((1, 1)), cellCount: 1, distinctChatters: 2),
            DayLine(From.AddDays(2), Empty, status: ReplayDayStatuses.RateLimited, bytes: 5),
        };
        var rows = new List<ReplayUsageRow> { new("x", From, 3, 0), new("x", From.AddDays(1), 3, 0) };

        var report = Compute(emotes, rows, days);

        Assert.Equal(14, report.Diagnostics.UnknownNameHits);
        Assert.Equal(4, report.Diagnostics.AmbiguousNameHits);
        Assert.Equal(0, report.Diagnostics.BeforeFirstSeenHits);
        Assert.Equal(5, report.Diagnostics.FirstSeenUnknownHits);
        Assert.Equal(1, report.Diagnostics.ArchivedWithoutDateCount);
        Assert.Equal(60, report.Diagnostics.TotalMessages);
        Assert.Equal(7, report.Diagnostics.BotMessages);
        Assert.Equal(5, report.Diagnostics.SharedChatMessages);
        Assert.Equal(3, report.Diagnostics.OutsideDayCount);
        Assert.Equal(9, report.Diagnostics.NonPrivmsgLines);
        Assert.Equal(5, report.Diagnostics.MalformedLines);
        Assert.Equal(11, report.Diagnostics.DistinctChatterDaySum);
        Assert.Equal(5, report.Diagnostics.CellCount);
        Assert.Equal(4, report.Diagnostics.KHistogram[1]);
        Assert.Equal(1, report.Diagnostics.KHistogram[2]);
        Assert.Equal(0.8, report.Diagnostics.SingleChatterCellShare!.Value, 6);
        Assert.Equal(2, report.Diagnostics.LogDays);
        Assert.Equal(1, report.Diagnostics.NoLogDays);
        Assert.Equal(305, report.Run.TotalBytes);
        Assert.Equal(1, report.Run.RateLimitedDays);
        Assert.Equal(From.AddDays(2), report.Run.ResumePoint);
        Assert.Equal(3, report.Run.DayLineCount);
    }

    [Fact]
    public void ComputeTwice_ReturnsEqualReports()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var first = Compute(emotes, rows, days);
        var second = Compute(emotes, rows, days);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    private static (List<ReplayEmote> Emotes, Dictionary<string, int> PerDay) BaseSet(int count = 32)
    {
        var emotes = new List<ReplayEmote>();
        var perDay = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            perDay[id] = i + 1;
        }

        return (emotes, perDay);
    }

    private static ReplayEmote Emote(
        string id,
        string? name = null,
        DateTime? lastSyncedAt = null,
        DateTime? archivedAt = null,
        bool isArchivedWithoutDate = false)
        => new(id, name ?? id, archivedAt is not null || isArchivedWithoutDate, Old, archivedAt, lastSyncedAt ?? Old);

    private static Dictionary<string, int> Counts(params (string Id, int Count)[] counts)
        => counts.ToDictionary(c => c.Id, c => c.Count, StringComparer.Ordinal);

    private static int[] Histogram(params (int K, int Cells)[] entries)
    {
        var histogram = new int[HistogramLength];
        foreach (var (k, cells) in entries)
        {
            histogram[k] = cells;
        }

        return histogram;
    }

    private static ReplayDayLine DayLine(
        DateOnly day,
        IReadOnlyDictionary<string, int>? human = null,
        IReadOnlyDictionary<string, int>? bot = null,
        string status = ReplayDayStatuses.Complete,
        IReadOnlyDictionary<string, int>? unmatched = null,
        int firstSeenUnknownHits = 0,
        IReadOnlyList<int>? histogram = null,
        int cellCount = 0,
        int distinctChatters = 0,
        long bytes = 0,
        int messageCount = 0,
        int botMessageCount = 0,
        int sharedChat = 0,
        int outsideDay = 0,
        int nonPrivmsg = 0,
        int malformed = 0)
        => new(
            day,
            status,
            bytes,
            null,
            messageCount,
            botMessageCount,
            sharedChat,
            nonPrivmsg,
            malformed,
            outsideDay,
            human ?? Empty,
            bot ?? Empty,
            unmatched ?? Empty,
            firstSeenUnknownHits,
            histogram ?? new int[HistogramLength],
            cellCount,
            distinctChatters);

    private static (List<ReplayDayLine> Days, List<ReplayUsageRow> Rows) Build(
        int dayCount,
        IReadOnlyDictionary<string, int> logPerDay,
        IReadOnlyDictionary<string, int> livePerDay)
    {
        var days = new List<ReplayDayLine>();
        var rows = new List<ReplayUsageRow>();
        for (var i = 0; i < dayCount; i++)
        {
            var day = From.AddDays(i);
            days.Add(DayLine(day, logPerDay));
            foreach (var (id, count) in livePerDay)
            {
                rows.Add(new ReplayUsageRow(id, day, count, 0));
            }
        }

        return (days, rows);
    }

    private static ReplayFinalReport Compute(
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayUsageRow> rows,
        IReadOnlyList<ReplayDayLine> days,
        DateOnly? cutover = null,
        int windowDays = 30,
        bool runComplete = true)
        => ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, cutover ?? From), emotes, rows, days, windowDays, runComplete);
}
