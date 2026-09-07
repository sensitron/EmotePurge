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
    public void DaysBeforeTheSharedChatCutover_CountForPlausibilityButNotForTheGate()
    {
        // harness-2 (#73, B5): HumanOnly is gated by the shared-chat cutover alone. The
        // bot-split cutover this used to key off of stays at its default (From) and no longer has
        // any bearing on RatedDays/HumanOnlyDays — Plausibility keeps summing every day with a log
        // regardless of either cutover.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, sharedChatCutover: From.AddDays(10));

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

        var report = Compute(emotes, rows, days, sharedChatCutover: From.AddDays(11));

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
    public void MissingBotSplitCutover_NoLongerBlocksAnyRatedDay()
    {
        // Inverted case of the pre-#73 test below (harness-2, B5): HumanOnly depends on the
        // shared-chat cutover alone now, so a channel that never saw a bot (BotSplitCutover =
        // null, the D2 null case) can no longer be shut out of the gate by that alone — exactly
        // the correction D4/B5 makes over the old rule.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, null, From), emotes, rows, days, 30, true, 0, 0, To, diagnostic: false);

        Assert.Equal(30, report.Diagnostics.HumanOnlyDays);
        Assert.Equal(30, report.Gate.RatedDays);
        Assert.True(report.Gate.GateEligible);
    }

    [Fact]
    public void MissingSharedChatCutover_LeavesNoRatedDay()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, From, null), emotes, rows, days, 30, true, 0, 0, To, diagnostic: false);

        Assert.Equal(0, report.Diagnostics.HumanOnlyDays);
        Assert.Equal(0, report.Gate.RatedDays);
        Assert.False(report.Gate.GateEligible);
        Assert.Null(report.Gate.TotalDeviation);
        Assert.Contains(ReplayGateIneligibleReasons.LiveTotalZero, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void DiagnosticMode_ComputesTheNumbersButWithholdsTheVerdict()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days, diagnostic: true);

        // The numbers are exactly what a binding run over the same inputs would produce...
        Assert.Equal(30, report.Gate.RatedDays);
        Assert.NotNull(report.Gate.TotalDeviation);
        // ...but the gate withholds a verdict regardless of whether the numbers would otherwise
        // have passed every threshold.
        Assert.False(report.Gate.GateEligible);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, report.Gate.GateIneligibleReasons);
        Assert.True(report.Run.Diagnostic);
    }

    [Fact]
    public void ReplayRunInfo_CarriesTheSharedChatCutoverAndTheRunMode()
    {
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var bound = Compute(emotes, rows, days, sharedChatCutover: From.AddDays(3));
        // The helper's "?? From" default cannot express an explicit null, so this one calls the
        // calculator directly — exactly why MissingSharedChatCutover_LeavesNoRatedDay does the same.
        var diagnosticReport = ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, From, null), emotes, rows, days, 30, true, 0, 0, To, diagnostic: true);

        Assert.Equal(From.AddDays(3), bound.Run.SharedChatCutover);
        Assert.False(bound.Run.Diagnostic);
        Assert.Null(diagnosticReport.Run.SharedChatCutover);
        Assert.True(diagnosticReport.Run.Diagnostic);
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

        var report = Compute(emotes, rows, days, sharedChatCutover: From.AddDays(10));

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
        // Visibility into the tie-break, not a second gate (Abschluss-Review): the live side's
        // bottom-quartile cut sits on the a/b tie at 20, so both of them share the boundary value
        // even though the quartile itself only fits one; the log side's cut sits on a's lone 10.
        Assert.Equal(2, report.Gate.BottomQuartileLiveTieCount);
        Assert.Equal(1, report.Gate.BottomQuartileLogTieCount);
    }

    [Fact]
    public void Plausibility_ComparesBothSidesIncludingBotsAndSharedChat()
    {
        // Three components on both sides (D3, second pair). Log per day: 10 human + 5 bot + 2 shared
        // = 17, over ten days = 170. Live per day: 10 UseCount + 4 BotUseCount + 1 SharedChatUseCount
        // = 15, over ten days = 150. The three components are deliberately all different on both
        // sides, so dropping any one of them from either sum changes the numbers below.
        var emotes = new List<ReplayEmote> { Emote("x") };
        var days = new List<ReplayDayLine>();
        var rows = new List<ReplayUsageRow>();
        for (var i = 0; i < 10; i++)
        {
            var day = From.AddDays(i);
            days.Add(DayLine(day, Counts(("x", 10)), Counts(("x", 5)), sharedChatCounts: Counts(("x", 2))));
            rows.Add(new ReplayUsageRow("x", day, 10, 4, 1));
        }

        var report = Compute(emotes, rows, days);

        Assert.Equal(170, report.Plausibility.LogTotalWithBots);
        Assert.Equal(150, report.Plausibility.LiveTotalWithBots);
        Assert.Equal(20, report.Plausibility.Difference);
        Assert.Equal(1.1333, report.Plausibility.Ratio!.Value, 6);
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
                histogram: Histogram((1, 3), (2, 1)), cellCount: 4, distinctChatters: 9,
                indeterminateMessageCount: 5),
            DayLine(From.AddDays(1), Counts(("x", 3)), unmatched: unmatched, firstSeenUnknownHits: 1, bytes: 200,
                messageCount: 10, botMessageCount: 1, sharedChat: 3, outsideDay: 2, nonPrivmsg: 1, malformed: 4,
                histogram: Histogram((1, 1)), cellCount: 1, distinctChatters: 2,
                indeterminateMessageCount: 6),
            DayLine(From.AddDays(2), Empty, status: ReplayDayStatuses.RateLimited, bytes: 5),
        };
        var rows = new List<ReplayUsageRow> { new("x", From, 3, 0, 0), new("x", From.AddDays(1), 3, 0, 0) };

        var report = Compute(emotes, rows, days, rateLimitedDays: 1, resumePoint: From.AddDays(2));

        Assert.Equal(14, report.Diagnostics.UnknownNameHits);
        Assert.Equal(4, report.Diagnostics.AmbiguousNameHits);
        Assert.Equal(0, report.Diagnostics.BeforeFirstSeenHits);
        Assert.Equal(5, report.Diagnostics.FirstSeenUnknownHits);
        Assert.Equal(1, report.Diagnostics.ArchivedWithoutDateCount);
        Assert.Equal(60, report.Diagnostics.TotalMessages);
        Assert.Equal(7, report.Diagnostics.BotMessages);
        Assert.Equal(5, report.Diagnostics.SharedChatMessages);
        // Its own counter, deliberately not folded into SharedChatMessages (B1/D2): a message whose
        // tag set says "part of a session" without saying whose is counted apart, even though its
        // hits share the shared-chat column.
        Assert.Equal(11, report.Diagnostics.IndeterminateMessages);
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
    public void RunInfo_TakesTheRateLimitCountAndTheResumePointFromTheCaller()
    {
        // The point of the parameters: a throttled day never becomes a day line (that would mark it
        // finished and make the resume skip it forever), so anything derived from `days` here would
        // read 0 in every report ever written. These two numbers are the caller's to state.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var reported = Compute(emotes, rows, days, rateLimitedDays: 7, resumePoint: From.AddDays(4));

        Assert.Equal(7, reported.Run.RateLimitedDays);
        Assert.Equal(From.AddDays(4), reported.Run.ResumePoint);

        // A day line carrying the status is explicitly not a source for the count any more.
        var withThrottledLine = new List<ReplayDayLine>(days)
        {
            DayLine(To.AddDays(1), Empty, status: ReplayDayStatuses.RateLimited),
        };

        Assert.Equal(0, Compute(emotes, rows, withThrottledLine).Run.RateLimitedDays);
        Assert.Null(Compute(emotes, rows, withThrottledLine).Run.ResumePoint);
    }

    [Fact]
    public void ComputeTwice_ReturnsEqualReports()
    {
        // Carries shared chat on both sides so the value equality actually covers SharedChatByDay —
        // an all-zero list would be equal under any implementation, including a non-deterministic one.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = BuildWithSharedChatOnTheFirstDay(perDay, 95, 100);

        var first = Compute(emotes, rows, days);
        var second = Compute(emotes, rows, days);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Top20Recall_FallsWhenTheTwoRankingsDisagree()
    {
        // 30 emotes, live count i+1 per day, so the live top 20 is e10..e29. The log lifts e00, e01
        // and e02 to the very top (100, 99, 98) and pushes e10..e14 to the bottom (1 each), which
        // makes the log top 20 {e00, e01, e02} + {e15..e29} (15) + {e08, e09} (2). The intersection
        // with the live top 20 is e15..e29, i.e. 15 of 20 -> recall 0.75.
        // The number is deliberately neither 1.0 nor what the same code would produce off the wrong
        // end of the rankings: the bottom-20 overlap of this scenario is 17, i.e. 0.85.
        var emotes = new List<ReplayEmote>();
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        var log = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 30; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            live[id] = i + 1;
            log[id] = i + 1;
        }

        log["e00"] = 100;
        log["e01"] = 99;
        log["e02"] = 98;
        for (var i = 10; i <= 14; i++)
        {
            log[$"e{i:00}"] = 1;
        }

        var (days, rows) = Build(30, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(30, report.Gate.PopulationSize);
        Assert.Equal(20, report.Gate.Top20Size);
        Assert.Equal(0.75, report.Gate.Top20Recall!.Value, 6);
    }

    [Fact]
    public void BottomQuartile_ReportsAPartialOverlap()
    {
        // Live counts 1..12, so the live bottom quartile (floor(12/4) = 3) is {e00, e01, e02}. The
        // log lifts e02 to 20 and pushes e05 down to 3, so its bottom three are {e00, e01, e05}.
        // Two of the three overlap -> precision 2/3 = 0.6667.
        var emotes = new List<ReplayEmote>();
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        var log = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 12; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            live[id] = i + 1;
            log[id] = i + 1;
        }

        log["e02"] = 20;
        log["e05"] = 3;

        var (days, rows) = Build(30, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(3, report.Gate.BottomQuartileSize);
        Assert.Equal(0.6667, report.Gate.BottomQuartilePrecision!.Value, 6);
    }

    [Fact]
    public void CoverageMedian_IsTakenOverTheLogDaysOnly()
    {
        // Pins the population of the coverage median (design D2). 14 intact log days at ratio 1 and
        // 16 days that have live rows but no log at all. Over *every* day with a defined ratio the
        // sorted ratios would be sixteen 0s followed by fourteen 1s, the nearest-rank median would
        // be 0, and every intact day would then be flagged questionable (1 > 2 * 0) -- the gate
        // would silently lose all of them. Over the log days alone the median is 1 and nothing is
        // flagged. Do not "fix" the median back to the literal wording without breaking this first.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);
        for (var i = 14; i < 30; i++)
        {
            days[i] = DayLine(From.AddDays(i), Empty, status: ReplayDayStatuses.NoLog);
        }

        var report = Compute(emotes, rows, days);

        Assert.Equal(1d, report.Diagnostics.DayRatioMedian!.Value, 6);
        Assert.Empty(report.Diagnostics.CoverageQuestionableDays);
        Assert.Equal(14, report.Gate.RatedDays);
        Assert.Equal(16, report.Diagnostics.NoLogDays);
    }

    [Fact]
    public void DayDiff_ForgivesUsageThatTheFlushMovedToTheNeighbouringDay()
    {
        // "x" is used ten times on each of three days, but its live rows carry nothing on the first
        // day and twenty on the second -- the flush-day shift. Exact daily diff: 10 + 10 + 0 = 20
        // over a live total of 330 (three days of "y" at 100 plus 30 of "x") = 0.0606. With the
        // one-day tolerance the first day's ten hits settle against the second day's surplus and
        // nothing is left over. The window sums are equal on both sides, so the gate deviation is 0
        // -- which is exactly why the day diff is worth reporting next to it.
        var emotes = new List<ReplayEmote> { Emote("x"), Emote("y") };
        var days = new List<ReplayDayLine>();
        var rows = new List<ReplayUsageRow>();
        int[] liveX = [0, 20, 10];
        for (var i = 0; i < 3; i++)
        {
            var day = From.AddDays(i);
            days.Add(DayLine(day, Counts(("x", 10), ("y", 100))));
            rows.Add(new ReplayUsageRow("x", day, liveX[i], 0, 0));
            rows.Add(new ReplayUsageRow("y", day, 100, 0, 0));
        }

        var report = Compute(emotes, rows, days);

        Assert.Equal(3, report.Gate.RatedDays);
        Assert.Equal(0d, report.Gate.TotalDeviation!.Value, 6);
        Assert.Equal(0.0606, report.Diagnostics.DailyDeviation!.Value, 6);
        Assert.Equal(0d, report.Diagnostics.DailyDeviationWithOneDayTolerance!.Value, 6);
    }

    [Fact]
    public void RatedDayWithoutAnySignal_IsCountedSeparately()
    {
        // A complete log day on which neither side saw anything passes every rating condition and
        // raises RatedDays towards the pre-registered minimum of 20 without contributing a single
        // comparison. The gate definition stays as published; this count makes the day visible.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);
        var deadDay = From.AddDays(29);
        days[29] = DayLine(deadDay, Empty);
        rows.RemoveAll(r => r.Date == deadDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(30, report.Gate.RatedDays);
        Assert.Equal(1, report.Diagnostics.SignallessRatedDays);
        Assert.Empty(report.Diagnostics.LiveGapDays);
    }

    [Fact]
    public void DayTotals_CountLogAndLiveOverAllThreeComponents()
    {
        // D3, first pair: the day total asks "does the archive have this day at all", so it sums
        // human + bot + shared chat against UseCount + BotUseCount + SharedChatUseCount. The middle
        // day here carries *nothing but* shared chat on either side — under a two-component sum both
        // of its totals would collapse to zero, the day would silently become a signalless rated day
        // instead of a matched one, and Plausibility would lose the same five hits twice over.
        var emotes = new List<ReplayEmote> { Emote("x") };
        var days = new List<ReplayDayLine>
        {
            DayLine(From, Counts(("x", 10))),
            DayLine(From.AddDays(1), sharedChatCounts: Counts(("x", 5))),
            DayLine(From.AddDays(2), Counts(("x", 10))),
        };
        var rows = new List<ReplayUsageRow>
        {
            new("x", From, 10, 0, 0),
            new("x", From.AddDays(1), 0, 0, 5),
            new("x", From.AddDays(2), 10, 0, 0),
        };

        var report = Compute(emotes, rows, days);

        Assert.Equal(3, report.Gate.RatedDays);
        Assert.Equal(0, report.Diagnostics.SignallessRatedDays);
        Assert.Empty(report.Diagnostics.LiveGapDays);
        Assert.Empty(report.Diagnostics.CoverageQuestionableDays);
        Assert.Equal(1d, report.Diagnostics.DayRatioMedian!.Value, 6);
        // D3, third pair: the gate itself does not move. The five foreign hits are in neither
        // denominator nor numerator.
        Assert.Equal(20, report.Gate.HumanLogTotal);
        Assert.Equal(20, report.Gate.HumanLiveTotal);
        Assert.Equal(0d, report.Gate.TotalDeviation!.Value, 6);
        // D3, second pair: plausibility carries all three components on both sides.
        Assert.Equal(25, report.Plausibility.LogTotalWithBots);
        Assert.Equal(25, report.Plausibility.LiveTotalWithBots);
        // Reported apart, symmetric, so the run stays usable.
        Assert.Equal(5, report.Gate.SharedChatLogTotal);
        Assert.Equal(5, report.Gate.SharedChatLiveTotal);
        Assert.DoesNotContain(ReplayGateIneligibleReasons.SharedChatAsymmetric, report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void APreDeployDay_KeepsItsRatioButBreaksSymmetry()
    {
        // The first of D3's three signatures: before the live deploy the replay side splits foreign
        // hits out while the live row still carries them inside UseCount. The day total is invariant
        // against that shift — mass moves between columns, none is created — so the day keeps a ratio
        // of 1 and stays out of CoverageQuestionable. Under a log side that counted own humans only,
        // this day would read 2 against 20, i.e. a ratio of 0.1 against a median of 1, and the
        // coverage check would throw it out of the rated set. That is the whole reason the day total
        // has three components while the gate has one.
        var emotes = new List<ReplayEmote> { Emote("x") };
        var days = new List<ReplayDayLine>
        {
            DayLine(From, Counts(("x", 10))),
            DayLine(From.AddDays(1), Counts(("x", 2)), sharedChatCounts: Counts(("x", 18))),
            DayLine(From.AddDays(2), Counts(("x", 10))),
        };
        var rows = new List<ReplayUsageRow>
        {
            new("x", From, 10, 0, 0),
            new("x", From.AddDays(1), 20, 0, 0),
            new("x", From.AddDays(2), 10, 0, 0),
        };

        var report = Compute(emotes, rows, days);

        Assert.Equal(3, report.Gate.RatedDays);
        Assert.Empty(report.Diagnostics.CoverageQuestionableDays);
        Assert.Empty(report.Diagnostics.LiveGapDays);
        Assert.Equal(1d, report.Diagnostics.DayRatioMedian!.Value, 6);
        // ...and the run is still not certifiable, because the two sides disagree about how much
        // shared chat there was: Live = 0 while Log > 0 is asymmetric, not "trivially empty".
        Assert.Equal(18, report.Gate.SharedChatLogTotal);
        Assert.Equal(0, report.Gate.SharedChatLiveTotal);
        Assert.Contains(ReplayGateIneligibleReasons.SharedChatAsymmetric, report.Gate.GateIneligibleReasons);
    }

    [Theory]
    [InlineData(95, 100)]
    [InlineData(110, 100)]
    public void SharedChatWithinTheTolerance_LeavesTheGateEligible(int logShared, int liveShared)
    {
        // |95 - 100| / 100 = 0.05 is inside; |110 - 100| / 100 = 0.10 sits exactly on the boundary
        // and is still inside — the condition is "≤", pinned here so a later "<" cannot slip in.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = BuildWithSharedChatOnTheFirstDay(perDay, logShared, liveShared);

        var report = Compute(emotes, rows, days);

        Assert.Equal(30, report.Gate.RatedDays);
        Assert.Equal(logShared, report.Gate.SharedChatLogTotal);
        Assert.Equal(liveShared, report.Gate.SharedChatLiveTotal);
        Assert.True(report.Gate.GateEligible);
        Assert.Empty(report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void SharedChatOutsideTheTolerance_MakesTheGateIneligible()
    {
        // |80 - 100| / 100 = 0.20. Everything else about this run is perfect, so the single reason
        // is the symmetry condition and nothing else — if the two sides disagree about the
        // classification, the fidelity number computed on top of it measures the wrong thing while
        // looking healthy.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = BuildWithSharedChatOnTheFirstDay(perDay, 80, 100);

        var report = Compute(emotes, rows, days);

        Assert.Equal(80, report.Gate.SharedChatLogTotal);
        Assert.Equal(100, report.Gate.SharedChatLiveTotal);
        Assert.False(report.Gate.GateEligible);
        Assert.Equal(
            ReplayGateIneligibleReasons.SharedChatAsymmetric,
            Assert.Single(report.Gate.GateIneligibleReasons));
    }

    [Fact]
    public void NoSharedChatOnEitherSide_IsTriviallySymmetric()
    {
        // A channel that never had a single Stream-Together session satisfies the condition emptily
        // — 0 against 0 is symmetric, not "live is zero, therefore suspicious".
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);

        var report = Compute(emotes, rows, days);

        Assert.Equal(0, report.Gate.SharedChatLogTotal);
        Assert.Equal(0, report.Gate.SharedChatLiveTotal);
        Assert.True(report.Gate.GateEligible);
        Assert.Empty(report.Gate.GateIneligibleReasons);
    }

    [Fact]
    public void SharedChatAsymmetryBeforeTheCutover_DoesNotReachTheGate()
    {
        // The window sums the condition judges are taken over the *rated* days only, which is what
        // makes the condition compatible with D4's cutover at all: every day before the cutover
        // carries the pre-deploy signature by construction (live 0, log > 0), so a window-wide sum
        // would declare every single first run asymmetric. The day is still reported in the
        // diagnostics list, where a reader can see the signature.
        var (emotes, perDay) = BaseSet();
        var (days, rows) = Build(30, perDay, perDay);
        days[0] = DayLine(From, perDay, sharedChatCounts: Counts(("e00", 200)));

        var report = Compute(emotes, rows, days, sharedChatCutover: From.AddDays(10));

        Assert.Equal(20, report.Gate.RatedDays);
        Assert.Equal(0, report.Gate.SharedChatLogTotal);
        Assert.Equal(0, report.Gate.SharedChatLiveTotal);
        Assert.True(report.Gate.GateEligible);
        Assert.Empty(report.Gate.GateIneligibleReasons);
        var first = report.Diagnostics.SharedChatByDay[0];
        Assert.Equal(From, first.Day);
        Assert.Equal(200, first.LogTotal);
        Assert.Equal(0, first.LiveTotal);
        Assert.Null(first.Ratio);
    }

    [Fact]
    public void SharedChatByDay_ListsEveryLogDayInOrderAndSkipsTheRest()
    {
        // One entry per day *with a log*, ascending, both sides side by side — the three signatures
        // of D3 are read off this list. A day without a log has no replay side to compare against,
        // so it is absent even though it has live rows carrying shared chat.
        var emotes = new List<ReplayEmote> { Emote("x") };
        var days = new List<ReplayDayLine>
        {
            DayLine(From.AddDays(2), Counts(("x", 4))),
            DayLine(From, Counts(("x", 4)), sharedChatCounts: Counts(("x", 3))),
            DayLine(From.AddDays(1), Empty, status: ReplayDayStatuses.NoLog),
        };
        var rows = new List<ReplayUsageRow>
        {
            new("x", From, 4, 0, 2),
            new("x", From.AddDays(1), 0, 0, 5),
            new("x", From.AddDays(2), 4, 0, 0),
        };

        var report = Compute(emotes, rows, days);

        Assert.Collection(
            report.Diagnostics.SharedChatByDay,
            entry =>
            {
                Assert.Equal(From, entry.Day);
                Assert.Equal(3, entry.LogTotal);
                Assert.Equal(2, entry.LiveTotal);
                Assert.Equal(1.5d, entry.Ratio!.Value, 6);
            },
            entry =>
            {
                Assert.Equal(From.AddDays(2), entry.Day);
                Assert.Equal(0, entry.LogTotal);
                Assert.Equal(0, entry.LiveTotal);
                Assert.Null(entry.Ratio);
            });
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
        int malformed = 0,
        IReadOnlyDictionary<string, int>? sharedChatCounts = null,
        int indeterminateMessageCount = 0)
        => new(
            day,
            status,
            bytes,
            null,
            messageCount,
            botMessageCount,
            sharedChat,
            indeterminateMessageCount,
            nonPrivmsg,
            malformed,
            outsideDay,
            human ?? Empty,
            bot ?? Empty,
            sharedChatCounts ?? Empty,
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
                rows.Add(new ReplayUsageRow(id, day, count, 0, 0));
            }
        }

        return (days, rows);
    }

    private static ReplayFinalReport Compute(
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayUsageRow> rows,
        IReadOnlyList<ReplayDayLine> days,
        DateOnly? sharedChatCutover = null,
        int windowDays = 30,
        bool runComplete = true,
        long? totalBytes = null,
        int rateLimitedDays = 0,
        DateOnly? resumePoint = null,
        bool diagnostic = false)
        => ReplayFidelityCalculator.Compute(
            new ReplayWindow(From, To, From, sharedChatCutover ?? From), emotes, rows, days, windowDays,
            runComplete, totalBytes ?? days.Sum(d => d.Bytes), rateLimitedDays, resumePoint, diagnostic);

    /// <summary>
    /// The 30-day base run with one day carrying shared chat on both sides, so a hand-computed
    /// window pair (log against live) can be put in front of the symmetry condition.
    /// </summary>
    private static (List<ReplayDayLine> Days, List<ReplayUsageRow> Rows) BuildWithSharedChatOnTheFirstDay(
        IReadOnlyDictionary<string, int> perDay, int logShared, int liveShared)
    {
        var (days, rows) = Build(30, perDay, perDay);
        days[0] = DayLine(From, perDay, sharedChatCounts: Counts(("e00", logShared)));
        var index = rows.FindIndex(r => r.Date == From && r.EmoteId == "e00");
        rows[index] = rows[index] with { SharedChatUseCount = liveShared };
        return (days, rows);
    }
}
