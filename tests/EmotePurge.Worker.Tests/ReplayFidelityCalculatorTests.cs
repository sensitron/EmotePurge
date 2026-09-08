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
    public void RankTies_NoLongerDecideTheQuartileSelection()
    {
        // Before #97 this test was named *AreBrokenByEmoteIdOrdinal* and pinned the old defect:
        // Ranking()'s ordinal id tie-break picked exactly one of the tied a/b pair for the live
        // bottom quartile (floor(4/4) = 1) -- specifically "b", the larger id -- while the log side
        // had no tie and picked "a" outright, so the two singleton selections never overlapped and
        // precision read 0. That 0 said nothing about the counts: it was an artifact of which of a/b
        // happened to sort last.
        //
        // The value-based cutoff (#97) does not pick a single winner out of the tie at all: both a
        // and b sit at the live cutoff value (20) and both belong to QuartileLiveSet, i.e. BOTH are
        // live-tail, not just whichever the GUID favoured. The log's lone cutoff entry is "a" (10),
        // which is also a member of that live-tail set, so the overlap is now correctly 1 out of a
        // 1-entry log quartile: precision 1.0. Top20Recall and TotalDeviation are untouched by #97
        // and keep their old values.
        var emotes = new List<ReplayEmote> { Emote("a"), Emote("b"), Emote("c"), Emote("d") };
        var live = Counts(("a", 20), ("b", 20), ("c", 40), ("d", 60));
        var log = Counts(("a", 10), ("b", 30), ("c", 40), ("d", 60));
        var (days, rows) = Build(20, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(1, report.Gate.BottomQuartileSize);
        Assert.Equal(1d, report.Gate.BottomQuartilePrecision!.Value, 6);
        // The live tail is the whole a/b tie block (both at 20), not just the nominal one slot --
        // the log tail stays a genuine singleton, since 10 is not tied with anything.
        Assert.Equal(2, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(1, report.Gate.BottomQuartileLogSize);
        Assert.Equal(4, report.Gate.Top20Size);
        Assert.Equal(1d, report.Gate.Top20Recall!.Value, 6);
        Assert.Equal(0.1429, report.Gate.TotalDeviation!.Value, 6);
        // Visibility into the tie-break, still purely descriptive: the live side's cutoff sits on
        // the a/b tie at 20, so both of them share the boundary value; the log side's cutoff sits on
        // a's lone 10.
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
        Assert.Equal(0, report.Gate.Top20LiveTieCount);
        Assert.Equal(0, report.Gate.Top20LogTieCount);
        Assert.Null(report.Gate.BottomQuartilePrecision);
        Assert.Equal(0, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(0, report.Gate.BottomQuartileLogSize);
        Assert.Null(report.Gate.TailDeviation);
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

        // No tie sits on either cutoff here, so the #97 rebuild changes nothing about this fixture:
        // both value-defined sets have exactly the nominal size.
        Assert.Equal(3, report.Gate.BottomQuartileSize);
        Assert.Equal(3, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(3, report.Gate.BottomQuartileLogSize);
        Assert.Equal(0.6667, report.Gate.BottomQuartilePrecision!.Value, 6);
    }

    [Fact]
    public void QuartilePrecision_IsIndependentOfWhichEntityHoldsWhichEmoteId()
    {
        // Regression guard for #97 — the defect itself, not just its symptom. Live totals a/b 400
        // (tied), c 800, d 1200 (the RankTies_NoLongerDecideTheQuartileSelection fixture, scaled by
        // the same 20-day Build). Only the LOG values of the tied pair are swapped between the two
        // calls below; every count that is not swapped (c, d, and both of the live values) is
        // identical.
        //
        // Under the pre-#97 algorithm this swap alone flipped the reported precision between 0.0 and
        // 1.0 — the id decided which of the tied pair the *live* ranking's TakeLast kept, and the
        // very same id (now attached to a different log count) decided the *log* ranking's TakeLast
        // too, so the overlap tracked the id rather than the counts. The value-based quartile of #97
        // does not consult an id to decide membership, so both variants below report the identical,
        // correct precision.
        Assert.Equal(1d, QuartilePrecisionFor(logA: 10, logB: 30), 6);
        Assert.Equal(1d, QuartilePrecisionFor(logA: 30, logB: 10), 6);

        double QuartilePrecisionFor(int logA, int logB)
        {
            var emotes = new List<ReplayEmote> { Emote("a"), Emote("b"), Emote("c"), Emote("d") };
            var live = Counts(("a", 20), ("b", 20), ("c", 40), ("d", 60));
            var log = Counts(("a", logA), ("b", logB), ("c", 40), ("d", 60));
            var (days, rows) = Build(20, log, live);

            return Compute(emotes, rows, days).Gate.BottomQuartilePrecision!.Value;
        }
    }

    [Fact]
    public void PerfectLog_WithLargeTieBlock_YieldsExactPrecisionOfOne()
    {
        // A tie block wider than the nominal quartile straddles the cutoff on both sides identically
        // when Log == Live everywhere: liveCutoff and logCutoff land on the same value, so
        // QuartileLiveSet and QuartileLogSet are the same five-entry plateau (not just the nominal
        // three) — precision is exactly 1.0 regardless of how large the plateau is or which ids sit
        // in it. This is what keeps the pre-registered 0.8 threshold meaningful under #97: a run that
        // truly reproduces the live counts still reads 1.0.
        var emotes = new List<ReplayEmote>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 5; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            counts[id] = 1;
        }

        for (var i = 5; i < 12; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            counts[id] = i - 3; // 2..8: distinct and above the tie block
        }

        var (days, rows) = Build(30, counts, counts);

        var report = Compute(emotes, rows, days);

        Assert.Equal(12, report.Gate.PopulationSize);
        Assert.Equal(3, report.Gate.BottomQuartileSize);
        Assert.Equal(5, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(5, report.Gate.BottomQuartileLogSize);
        Assert.Equal(1d, report.Gate.BottomQuartilePrecision!.Value, 6);
        Assert.Equal(0d, report.Gate.TailDeviation!.Value, 6);
    }

    [Fact]
    public void UniformTailLoss_IsInvisibleToPrecisionButVisibleToTailDeviation()
    {
        // The literal scenario named in the #97 decision and the reason TailDeviation exists as a
        // separate, non-gate figure: population 8, nominal quartile size 2. Live 5,5,4,3,1,1,1,1;
        // Log 5,5,4,3,0,0,0,0 — the bottom four emotes keep their rank (all four are still tied at
        // the bottom on both sides, so QuartileLiveSet == QuartileLogSet == the same four ids) but
        // lose their entire live volume in the log. A set-membership comparison structurally cannot
        // see a uniform tail loss like this: precision stays exactly 1.0. TailDeviation, computed
        // over the actual counts instead of set membership, reports the loss in full: 1.0, i.e. 100 %
        // of the live tail's volume is gone from the log. Neither number is wrong; they answer
        // different questions, and that division of labour is the point of D-97.
        var emotes = new List<ReplayEmote>
        {
            Emote("e00"), Emote("e01"), Emote("e02"), Emote("e03"),
            Emote("e04"), Emote("e05"), Emote("e06"), Emote("e07"),
        };
        var live = Counts(
            ("e00", 5), ("e01", 5), ("e02", 4), ("e03", 3), ("e04", 1), ("e05", 1), ("e06", 1), ("e07", 1));
        var log = Counts(
            ("e00", 5), ("e01", 5), ("e02", 4), ("e03", 3), ("e04", 0), ("e05", 0), ("e06", 0), ("e07", 0));
        var (days, rows) = Build(1, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(8, report.Gate.PopulationSize);
        Assert.Equal(2, report.Gate.BottomQuartileSize);
        Assert.Equal(4, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(4, report.Gate.BottomQuartileLogSize);
        Assert.Equal(1d, report.Gate.BottomQuartilePrecision!.Value, 6);
        Assert.Equal(1d, report.Gate.TailDeviation!.Value, 6);
    }

    [Fact]
    public void LogTailLargerThanLiveTail_PullsPrecisionBelowOne()
    {
        // The denominator is |QuartileLogSet| (#97 decision): when the log's tail is a genuine
        // four-wide plateau but the live tail is a clean two-entry cut, the log-side set is larger
        // than the live-side set and precision must fall below 1 even though every live-tail id is
        // also inside the log tail.
        var emotes = new List<ReplayEmote>
        {
            Emote("e00"), Emote("e01"), Emote("e02"), Emote("e03"),
            Emote("e04"), Emote("e05"), Emote("e06"), Emote("e07"),
        };
        var live = Counts(
            ("e00", 8), ("e01", 7), ("e02", 6), ("e03", 5), ("e04", 4), ("e05", 3), ("e06", 2), ("e07", 1));
        var log = Counts(
            ("e00", 8), ("e01", 7), ("e02", 6), ("e03", 5), ("e04", 1), ("e05", 1), ("e06", 1), ("e07", 1));
        var (days, rows) = Build(1, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(2, report.Gate.BottomQuartileSize);
        Assert.Equal(2, report.Gate.BottomQuartileLiveSize);
        Assert.Equal(4, report.Gate.BottomQuartileLogSize);
        Assert.Equal(0.5, report.Gate.BottomQuartilePrecision!.Value, 6);
    }

    [Fact]
    public void TailDeviation_IsNullWhenTheLiveTailHasNoVolume()
    {
        // liveCutoff can be 0 when at least BottomQuartileSize emotes never occurred live at all
        // (they are log-only). Σ Live over QuartileLiveSet is then 0 — "no denominator", which must
        // read as null, never as 0 (0 would silently claim a perfect tail) and never throw.
        var emotes = new List<ReplayEmote>
        {
            Emote("e00"), Emote("e01"), Emote("e02"), Emote("e03"),
            Emote("e04"), Emote("e05"), Emote("e06"), Emote("e07"),
        };
        var live = Counts(("e00", 8), ("e01", 7), ("e02", 6), ("e03", 5));
        var log = Counts(
            ("e00", 8), ("e01", 7), ("e02", 6), ("e03", 5), ("e04", 3), ("e05", 3), ("e06", 3), ("e07", 3));
        var (days, rows) = Build(1, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(8, report.Gate.PopulationSize);
        Assert.Equal(4, report.Gate.BottomQuartileLiveSize);
        Assert.Null(report.Gate.TailDeviation);
    }

    [Fact]
    public void Top20TieCounts_ReportTheBlockSizeAtTheTopCutoffOverTheWholePopulation()
    {
        // Visibility, not a gate (#97): Top20Recall's formula is untouched, but a reader can now see
        // whether the top-20 cutoff sits on a tie at all. 25 emotes; Live ties four-wide at the
        // cutoff (ids e18..e21, all at 50), Log ties two-wide at its own, different cutoff (ids
        // e18..e19, both at 90). Two of the four live-tied ids (e20, e21) sit outside the nominal top
        // 20 by *position* — the tie count still covers the whole four-wide block, not just the
        // in-range members, exactly like CountTiesAtQuartileBoundary already did for the bottom.
        var emotes = new List<ReplayEmote>();
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        var log = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 18; i++)
        {
            var id = $"e{i:00}";
            emotes.Add(Emote(id));
            live[id] = 100 - i;
            log[id] = 200 - i;
        }

        foreach (var id in new[] { "e18", "e19", "e20", "e21" })
        {
            emotes.Add(Emote(id));
            live[id] = 50;
        }

        log["e18"] = 90;
        log["e19"] = 90;
        log["e20"] = 20;
        log["e21"] = 19;

        emotes.Add(Emote("e22"));
        emotes.Add(Emote("e23"));
        emotes.Add(Emote("e24"));
        live["e22"] = 10;
        live["e23"] = 9;
        live["e24"] = 8;
        log["e22"] = 18;
        log["e23"] = 17;
        log["e24"] = 16;

        var (days, rows) = Build(1, log, live);

        var report = Compute(emotes, rows, days);

        Assert.Equal(25, report.Gate.PopulationSize);
        Assert.Equal(20, report.Gate.Top20Size);
        Assert.Equal(4, report.Gate.Top20LiveTieCount);
        Assert.Equal(2, report.Gate.Top20LogTieCount);
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
