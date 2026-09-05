using EmotePurge.Core.Matching;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// Turns the day lines of a harness run into the final report of issue #69 — a pure function of its
/// arguments, so the same run always yields the same numbers and the report can be rebuilt from the
/// JSONL protocol alone.
/// <para>
/// It computes the pre-registered figures and nothing else: the thresholds (deviation ≤ 10 %,
/// top-20 recall ≥ 0.9, bottom-quartile precision ≥ 0.8, at least 20 rated days, window 30 days)
/// are published in issue #69 and are read against these numbers by a human. Deliberately no
/// verdict is computed here.
/// </para>
/// </summary>
public static class ReplayFidelityCalculator
{
    private const int RequiredWindowDays = 30;
    private const int RequiredRatedDays = 20;
    private const int TopSize = 20;
    private const int RequiredQualifiedEmotes = 30;
    private const int MinLiveUsesPerThirtyDays = 20;
    private const int MinLiveUsesFloor = 5;
    private const int RoundingDigits = 4;
    private const int MinHistogramLength = 11;

    /// <param name="window">The frozen window plus the channel's bot-split cutover.</param>
    /// <param name="emotes">Every emote of the channel, archived ones included.</param>
    /// <param name="liveRows">The live <c>UsageStat</c> rows of the window.</param>
    /// <param name="days">One line per archive day the run produced.</param>
    /// <param name="windowDays">The window length the run was started with (30 for a binding run).</param>
    /// <param name="runComplete">Whether the run covered the whole window; an aborted run never binds.</param>
    public static ReplayFinalReport Compute(
        ReplayWindow window,
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayUsageRow> liveRows,
        IReadOnlyList<ReplayDayLine> days,
        int windowDays,
        bool runComplete)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(emotes);
        ArgumentNullException.ThrowIfNull(liveRows);
        ArgumentNullException.ThrowIfNull(days);

        var ambiguousNames = EmoteNameMatching.Coalesce(
            emotes.Select(e => new KeyValuePair<string, string>(e.Name, e.Id))).AmbiguousNames;

        var (facts, dayRatioMedian) = BuildDayFacts(window, days, liveRows);
        var ratedDays = DaySet(facts, f => f.Rated);
        var humanOnlyLogDays = DaySet(facts, f => f is { HasLog: true, HumanOnly: true });
        var logDays = DaySet(facts, f => f.HasLog);

        var population = BuildPopulation(days, liveRows, ratedDays);
        var gate = BuildGate(population, ratedDays.Count, windowDays, runComplete);
        var plausibility = BuildPlausibility(days, liveRows, logDays);
        var diagnostics = BuildDiagnostics(
            window, emotes, days, liveRows, facts, population, ambiguousNames, humanOnlyLogDays, dayRatioMedian);

        var run = new ReplayRunInfo(
            window.From,
            window.To,
            window.BotSplitCutover,
            windowDays,
            days.Count,
            days.Sum(d => d.Bytes),
            days.Count(d => d.Status == ReplayDayStatuses.RateLimited),
            days.Count == 0 ? null : days.Max(d => d.Day),
            runComplete);

        return new ReplayFinalReport(run, gate, plausibility, diagnostics);
    }

    /// <summary>
    /// Classifies every day: does it have a log, is it comparable human-only, what is its
    /// log-to-live ratio, is a live gap suspected, is its coverage questionable, does it count.
    /// <para>
    /// The median that the coverage check (Codex-adversarial D2) compares against is taken over the
    /// days that <b>have a log</b> and a defined ratio. Days without a log have a log total of zero
    /// and would drag the median towards zero, which would then flag the intact days as
    /// questionable — the opposite of what the check is for. This is a deliberate reading of the
    /// design's intent; see the task report.
    /// </para>
    /// </summary>
    private static (List<DayFact> Facts, double? RatioMedian) BuildDayFacts(
        ReplayWindow window,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows)
    {
        var liveByDay = new Dictionary<DateOnly, long>();
        foreach (var row in liveRows)
        {
            liveByDay[row.Date] = liveByDay.GetValueOrDefault(row.Date) + row.UseCount + row.BotUseCount;
        }

        var facts = new List<DayFact>(days.Count);
        foreach (var line in days.OrderBy(d => d.Day))
        {
            var logTotal = Sum(line.HumanCounts) + Sum(line.BotCounts);
            var liveTotal = liveByDay.GetValueOrDefault(line.Day);
            facts.Add(new DayFact
            {
                Day = line.Day,
                HasLog = line.Status == ReplayDayStatuses.Complete,
                HumanOnly = window.BotSplitCutover is { } cutover && line.Day >= cutover,
                LogTotal = logTotal,
                LiveTotal = liveTotal,
                Ratio = liveTotal == 0 ? null : (double)logTotal / liveTotal,
                LiveGapSuspected = liveTotal == 0 && logTotal > 0,
            });
        }

        var ratios = facts
            .Where(f => f.HasLog && f.Ratio is not null)
            .Select(f => f.Ratio!.Value)
            .Order()
            .ToList();
        var median = Percentile(ratios, 0.5);

        foreach (var fact in facts)
        {
            fact.CoverageQuestionable = fact.HasLog
                && median is { } m
                && fact.Ratio is { } ratio
                && (ratio < m / 2 || ratio > 2 * m);
            fact.Rated = fact is { HasLog: true, HumanOnly: true, LiveGapSuspected: false, CoverageQuestionable: false };
        }

        return (facts, median);
    }

    /// <summary>
    /// The full import population over the given days, human-only on both sides: live is
    /// <c>UseCount</c> (what the grid actually shows), log is the human hit count. Every emote that
    /// appears on at least one side is in, log-only and live-only included — that is exactly the
    /// case the gate must not hide (Codex-adversarial D1).
    /// </summary>
    private static List<PopulationEntry> BuildPopulation(
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        HashSet<DateOnly> daySet)
    {
        var log = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in days)
        {
            if (!daySet.Contains(line.Day))
            {
                continue;
            }

            foreach (var (emoteId, count) in line.HumanCounts)
            {
                log[emoteId] = log.GetValueOrDefault(emoteId) + count;
            }
        }

        var live = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in liveRows)
        {
            if (daySet.Contains(row.Date))
            {
                live[row.EmoteId] = live.GetValueOrDefault(row.EmoteId) + row.UseCount;
            }
        }

        var ids = new HashSet<string>(log.Keys, StringComparer.Ordinal);
        ids.UnionWith(live.Keys);

        return ids
            .Select(id => new PopulationEntry(id, live.GetValueOrDefault(id), log.GetValueOrDefault(id)))
            .Where(e => e.Live > 0 || e.Log > 0)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static ReplayGateMetrics BuildGate(
        List<PopulationEntry> population,
        int ratedDays,
        int windowDays,
        bool runComplete)
    {
        var logTotal = population.Sum(e => e.Log);
        var liveTotal = population.Sum(e => e.Live);
        var totalDeviation = liveTotal == 0
            ? null
            : (double?)population.Sum(e => Math.Abs(e.Log - e.Live)) / liveTotal;

        var liveRanking = Ranking(population, e => e.Live);
        var logRanking = Ranking(population, e => e.Log);
        var count = population.Count;

        var topSize = Math.Min(TopSize, count);
        double? top20Recall = topSize == 0
            ? null
            : (double)Overlap(liveRanking.Take(topSize), logRanking.Take(topSize)) / topSize;

        var quartileSize = count == 0 ? 0 : Math.Max(1, count / 4);
        double? bottomQuartilePrecision = quartileSize == 0
            ? null
            : (double)Overlap(liveRanking.TakeLast(quartileSize), logRanking.TakeLast(quartileSize)) / quartileSize;

        var reasons = new List<string>();
        if (!runComplete)
        {
            reasons.Add(ReplayGateIneligibleReasons.RunIncomplete);
        }

        if (windowDays != RequiredWindowDays)
        {
            reasons.Add(ReplayGateIneligibleReasons.WindowNotThirtyDays);
        }

        if (ratedDays < RequiredRatedDays)
        {
            reasons.Add(ReplayGateIneligibleReasons.RatedDaysBelowTwenty);
        }

        if (totalDeviation is null)
        {
            reasons.Add(ReplayGateIneligibleReasons.LiveTotalZero);
        }

        return new ReplayGateMetrics(
            ratedDays,
            count,
            logTotal,
            liveTotal,
            Round(totalDeviation),
            Round(top20Recall),
            topSize,
            Round(bottomQuartilePrecision),
            quartileSize,
            reasons.Count == 0,
            new ValueList<string>(reasons));
    }

    private static ReplayPlausibility BuildPlausibility(
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        HashSet<DateOnly> logDays)
    {
        long log = 0;
        foreach (var line in days)
        {
            if (logDays.Contains(line.Day))
            {
                log += Sum(line.HumanCounts) + Sum(line.BotCounts);
            }
        }

        long live = 0;
        foreach (var row in liveRows)
        {
            if (logDays.Contains(row.Date))
            {
                live += row.UseCount + row.BotUseCount;
            }
        }

        return new ReplayPlausibility(log, live, Round(live == 0 ? null : (double?)log / live), log - live);
    }

    private static ReplayDiagnostics BuildDiagnostics(
        ReplayWindow window,
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        List<DayFact> facts,
        List<PopulationEntry> population,
        IReadOnlySet<string> ambiguousNames,
        HashSet<DateOnly> humanOnlyLogDays,
        double? dayRatioMedian)
    {
        var ratedDays = facts.Count(f => f.Rated);
        var minLiveUses = Math.Max(
            MinLiveUsesFloor,
            (int)Math.Ceiling((double)MinLiveUsesPerThirtyDays * ratedDays / RequiredWindowDays));

        var stableIds = emotes
            .Where(e => IsStable(e, window.From, ambiguousNames))
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);

        var qualifiedStable = population.Where(e => e.Live >= minLiveUses && stableIds.Contains(e.Id)).ToList();
        var qualifiedAll = population.Where(e => e.Live >= minLiveUses).ToList();
        var stable = Spread(qualifiedStable);
        var all = Spread(qualifiedAll);

        var logTotal = population.Sum(e => e.Log);
        var liveTotal = population.Sum(e => e.Live);
        var logOnly = population.Where(e => e.Live == 0).ToList();
        var liveOnly = population.Where(e => e.Log == 0).ToList();

        var histogram = new int[Math.Max(MinHistogramLength, days.Count == 0 ? 0 : days.Max(d => d.KHistogram.Count))];
        foreach (var line in days)
        {
            for (var k = 0; k < line.KHistogram.Count; k++)
            {
                histogram[k] += line.KHistogram[k];
            }
        }

        long cellCount = days.Sum(d => (long)d.CellCount);

        return new ReplayDiagnostics(
            stableIds.Count,
            qualifiedStable.Count,
            minLiveUses,
            RequiredQualifiedEmotes,
            qualifiedStable.Count >= RequiredQualifiedEmotes,
            Round(stable.Median),
            Round(stable.P90),
            Round(stable.Spearman),
            stable.ZeroLogCount,
            qualifiedAll.Count,
            Round(all.Median),
            Round(all.P90),
            Round(all.Spearman),
            all.ZeroLogCount,
            logOnly.Count,
            liveOnly.Count,
            Round(logTotal == 0 ? null : (double?)logOnly.Sum(e => e.Log) / logTotal),
            Round(liveTotal == 0 ? null : (double?)liveOnly.Sum(e => e.Live) / liveTotal),
            Round(Deviation(BuildPopulation(days, liveRows, humanOnlyLogDays))),
            humanOnlyLogDays.Count,
            Reason(days, UnmatchedReason.UnknownName),
            Reason(days, UnmatchedReason.AmbiguousName),
            Reason(days, UnmatchedReason.BeforeFirstSeen),
            Reason(days, UnmatchedReason.AfterArchived),
            days.Sum(d => (long)d.FirstSeenUnknownHits),
            ambiguousNames.Count,
            emotes.Count(e => e.IsArchived && e.ArchivedAt is null),
            days.Sum(d => (long)d.MessageCount),
            days.Sum(d => (long)d.BotMessageCount),
            days.Sum(d => (long)d.SharedChatMessageCount),
            days.Sum(d => (long)d.OutsideDayCount),
            days.Sum(d => (long)d.NonPrivmsgLines),
            days.Sum(d => (long)d.MalformedLines),
            Round(cellCount == 0 ? null : (double?)histogram[1] / cellCount),
            new ValueList<int>(histogram),
            cellCount,
            days.Sum(d => (long)d.DistinctChatters),
            facts.Count(f => f.HumanOnly),
            facts.Count(f => f.HasLog),
            facts.Count(f => !f.HasLog),
            Round(dayRatioMedian),
            Ratios(facts, f => f.LiveGapSuspected),
            Ratios(facts, f => f.CoverageQuestionable));
    }

    /// <summary>
    /// The stable subset: last synced before the window <b>and</b> either never archived or archived
    /// before the window (the REST resync archives without stamping <c>LastSyncedAt</c>, so the
    /// first condition alone would not do), and not carrying an ambiguous name.
    /// </summary>
    private static bool IsStable(ReplayEmote emote, DateOnly windowFrom, IReadOnlySet<string> ambiguousNames)
        => DateOnly.FromDateTime(emote.LastSyncedAt) < windowFrom
            && (emote.ArchivedAt is not { } archivedAt || DateOnly.FromDateTime(archivedAt) < windowFrom)
            && !ambiguousNames.Contains(emote.Name);

    /// <summary>
    /// Median, p90, Spearman and the count of emotes the log never saw, over one qualified set.
    /// Every entry has <c>Live &gt;= N &gt; 0</c>, so the relative deviation always has a
    /// denominator; an emote the log missed entirely lands at exactly 1.0, i.e. 100 %.
    /// </summary>
    private static SubsetSpread Spread(List<PopulationEntry> entries)
    {
        var deviations = entries
            .Select(e => (double)Math.Abs(e.Log - e.Live) / e.Live)
            .Order()
            .ToList();

        return new SubsetSpread(
            Percentile(deviations, 0.5),
            Percentile(deviations, 0.9),
            Spearman(entries),
            entries.Count(e => e.Log == 0));
    }

    private static double? Deviation(List<PopulationEntry> population)
    {
        var live = population.Sum(e => e.Live);
        return live == 0 ? null : (double?)population.Sum(e => Math.Abs(e.Log - e.Live)) / live;
    }

    /// <summary>
    /// Descending by count, ties broken ordinally by emote id, so both rankings are total orders and
    /// the top and bottom slices are reproducible.
    /// </summary>
    private static List<string> Ranking(List<PopulationEntry> population, Func<PopulationEntry, long> value)
        => population
            .OrderByDescending(value)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => e.Id)
            .ToList();

    private static int Overlap(IEnumerable<string> left, IEnumerable<string> right)
        => left.ToHashSet(StringComparer.Ordinal).Intersect(right, StringComparer.Ordinal).Count();

    /// <summary>
    /// Spearman's rank correlation as Pearson over mid-ranks; <c>null</c> when there are fewer than
    /// two emotes or one side is constant (no variance, hence no correlation to report).
    /// </summary>
    private static double? Spearman(List<PopulationEntry> entries)
    {
        if (entries.Count < 2)
        {
            return null;
        }

        var liveRanks = MidRanks([.. entries.Select(e => (double)e.Live)]);
        var logRanks = MidRanks([.. entries.Select(e => (double)e.Log)]);

        double meanLive = liveRanks.Average();
        double meanLog = logRanks.Average();
        double covariance = 0;
        double liveVariance = 0;
        double logVariance = 0;
        for (var i = 0; i < liveRanks.Length; i++)
        {
            var dx = liveRanks[i] - meanLive;
            var dy = logRanks[i] - meanLog;
            covariance += dx * dy;
            liveVariance += dx * dx;
            logVariance += dy * dy;
        }

        return liveVariance == 0 || logVariance == 0 ? null : covariance / Math.Sqrt(liveVariance * logVariance);
    }

    private static double[] MidRanks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        var start = 0;
        while (start < order.Length)
        {
            var end = start;
            while (end + 1 < order.Length && values[order[end + 1]] == values[order[start]])
            {
                end++;
            }

            var midRank = ((start + end) / 2.0) + 1;
            for (var i = start; i <= end; i++)
            {
                ranks[order[i]] = midRank;
            }

            start = end + 1;
        }

        return ranks;
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending list: rank ⌈p·n⌉, no interpolation between
    /// neighbours. Interpolating would invent a value that no emote and no day actually had.
    /// </summary>
    private static double? Percentile(List<double> ascending, double p)
    {
        if (ascending.Count == 0)
        {
            return null;
        }

        var rank = Math.Clamp((int)Math.Ceiling(p * ascending.Count), 1, ascending.Count);
        return ascending[rank - 1];
    }

    private static ValueList<ReplayDayRatio> Ratios(List<DayFact> facts, Func<DayFact, bool> predicate)
        => new(facts
            .Where(predicate)
            .OrderBy(f => f.Day)
            .Select(f => new ReplayDayRatio(f.Day, f.LogTotal, f.LiveTotal, Round(f.Ratio))));

    private static HashSet<DateOnly> DaySet(List<DayFact> facts, Func<DayFact, bool> predicate)
        => facts.Where(predicate).Select(f => f.Day).ToHashSet();

    private static long Reason(IReadOnlyList<ReplayDayLine> days, UnmatchedReason reason)
    {
        var key = reason.ToString();
        return days.Sum(d => (long)d.UnmatchedByReason.GetValueOrDefault(key));
    }

    private static long Sum(IReadOnlyDictionary<string, int> counts)
    {
        long total = 0;
        foreach (var count in counts.Values)
        {
            total += count;
        }

        return total;
    }

    private static double? Round(double? value)
        => value is null ? null : Math.Round(value.Value, RoundingDigits, MidpointRounding.AwayFromZero);

    private sealed class DayFact
    {
        public DateOnly Day { get; init; }

        public bool HasLog { get; init; }

        public bool HumanOnly { get; init; }

        public long LogTotal { get; init; }

        public long LiveTotal { get; init; }

        public double? Ratio { get; init; }

        public bool LiveGapSuspected { get; init; }

        public bool CoverageQuestionable { get; set; }

        public bool Rated { get; set; }
    }

    private sealed record PopulationEntry(string Id, long Live, long Log);

    private sealed record SubsetSpread(double? Median, double? P90, double? Spearman, int ZeroLogCount);
}
