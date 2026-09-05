using System.Collections;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// One emote of the channel as the harness sees it: the three timestamps that decide which day it
/// may be counted on, plus the archive flag.
/// <para>
/// This is deliberately <b>not</b> the DTO that <c>IUsageStatQueryService</c> hands out
/// (<c>EmoteLifetimeDto</c>), even though the two have the same shape. The pure calculation of the
/// harness must not wait for — or be coupled to — the query layer, so the entry point maps between
/// the two. The duplication is a decision, not an oversight: do not "clean it up" by making this
/// an alias.
/// </para>
/// </summary>
public sealed record ReplayEmote(
    string Id,
    string Name,
    bool IsArchived,
    DateTime? FirstSeenAt,
    DateTime? ArchivedAt,
    DateTime LastSyncedAt);

/// <summary>
/// One live <c>UsageStat</c> row of the window. Same shape as <c>UsageStatRowDto</c> and, like
/// <see cref="ReplayEmote"/>, deliberately its own type — see the remark there.
/// </summary>
public sealed record ReplayUsageRow(string EmoteId, DateOnly Date, int UseCount, int BotUseCount);

/// <summary>
/// The frozen comparison window plus the channel's bot-split cutover, i.e. the earliest day whose
/// live rows can carry a <c>BotUseCount</c> at all. Days before it cannot be compared human-only
/// (bot messages sat inside <c>UseCount</c> back then), so they never enter the gate.
/// <c>null</c> means the channel has no such day yet — then there is no rated day at all.
/// </summary>
public sealed record ReplayWindow(DateOnly From, DateOnly To, DateOnly? BotSplitCutover);

/// <summary>Why a chat token did not count towards an emote on a given day.</summary>
public enum UnmatchedReason
{
    /// <summary>No emote of the channel ever carried that name.</summary>
    UnknownName,

    /// <summary>
    /// The name is carried by more than one emote of the day. The hit still counts on the
    /// coalesced id (exactly as the live path would), and this reason is an additional marker.
    /// </summary>
    AmbiguousName,

    /// <summary>The emote of that name was added to the set after this day.</summary>
    BeforeFirstSeen,

    /// <summary>
    /// The emote of that name was archived before this day — including "archived without a date",
    /// which is excluded from the day map and counted separately in the final report.
    /// </summary>
    AfterArchived,
}

/// <summary>
/// The <see cref="ReplayDayLine.Status"/> values the calculator itself branches on. Any other
/// string is allowed and is treated as "this day has no usable log".
/// </summary>
public static class ReplayDayStatuses
{
    /// <summary>The whole day was fetched and counted.</summary>
    public const string Complete = "Complete";

    /// <summary>The archive has no log for that day (the normal 404 case).</summary>
    public const string NoLog = "NoLog";

    /// <summary>The archive answered 429; the run stops there and keeps a resume point.</summary>
    public const string RateLimited = "RateLimited";
}

/// <summary>Stable tokens for <see cref="ReplayGateMetrics.GateIneligibleReasons"/>.</summary>
public static class ReplayGateIneligibleReasons
{
    /// <summary>The run did not cover the whole window.</summary>
    public const string RunIncomplete = "run-incomplete";

    /// <summary>The window was not the pre-registered 30 days.</summary>
    public const string WindowNotThirtyDays = "window-not-30-days";

    /// <summary>Fewer than the pre-registered 20 rated days.</summary>
    public const string RatedDaysBelowTwenty = "rated-days-below-20";

    /// <summary>No live usage at all over the rated days, so the deviation has no denominator.</summary>
    public const string LiveTotalZero = "live-total-zero";
}

/// <summary>
/// Everything one archive day contributed, as written to the JSONL protocol. The dictionaries are
/// keyed by emote id and by <see cref="UnmatchedReason"/> name; no chatter id ever appears here —
/// the per-cell chatter sets are collapsed into <see cref="KHistogram"/> and dropped.
/// <para>
/// Note for later consumers: the collection members give this record reference equality, so two
/// separately built lines are never <c>Equals</c>. Only <see cref="ReplayFinalReport"/> carries a
/// value-equality guarantee (it is the reproducibility contract of the run).
/// </para>
/// </summary>
public sealed record ReplayDayLine(
    DateOnly Day,
    string Status,
    long Bytes,
    string? BodySha256Hex,
    int MessageCount,
    int BotMessageCount,
    int SharedChatMessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int OutsideDayCount,
    IReadOnlyDictionary<string, int> HumanCounts,
    IReadOnlyDictionary<string, int> BotCounts,
    IReadOnlyDictionary<string, int> UnmatchedByReason,
    int FirstSeenUnknownHits,
    IReadOnlyList<int> KHistogram,
    int CellCount,
    int DistinctChatters);

/// <summary>One day's log-to-live ratio, used to report gap days and questionable-coverage days.</summary>
public sealed record ReplayDayRatio(DateOnly Day, long LogTotal, long LiveTotal, double? Ratio);

/// <summary>
/// The pre-registered numbers of issue #69, over rated days only, human-only, and over the full
/// import population (every emote with live &gt; 0 or log &gt; 0, archived and renamed ones
/// included — Codex-adversarial decision D1). The thresholds themselves live in the issue, never
/// here: this record hands out the values, the human reads them against #69.
/// </summary>
public sealed record ReplayGateMetrics(
    int RatedDays,
    int PopulationSize,
    long LogTotal,
    long LiveTotal,
    double? TotalDeviation,
    double? Top20Recall,
    int Top20Size,
    double? BottomQuartilePrecision,
    int BottomQuartileSize,
    bool GateEligible,
    ValueList<string> GateIneligibleReasons);

/// <summary>
/// Plausibility check (a): both sides summed <b>including</b> bots over every day that has a log.
/// Covers the days before the bot-split cutover, which the gate cannot use.
/// </summary>
public sealed record ReplayPlausibility(
    long LogTotalWithBots,
    long LiveTotalWithBots,
    double? Ratio,
    long Difference);

/// <summary>
/// Everything that explains the gate numbers without binding anything: the stable-subset
/// diagnostics of the original pre-registration, the shape of the population, the matching
/// reasons, the message-level counters and the privacy figures.
/// </summary>
public sealed record ReplayDiagnostics(
    int StableSubsetSize,
    int QualifiedEmoteCount,
    int MinLiveUsesN,
    int RequiredQualifiedM,
    bool StableSubsetDecisive,
    double? StableSubsetMedianDeviation,
    double? StableSubsetP90Deviation,
    double? StableSubsetSpearman,
    int StableSubsetZeroLogCount,
    int AllEmotesQualifiedCount,
    double? AllEmotesMedianDeviation,
    double? AllEmotesP90Deviation,
    double? AllEmotesSpearman,
    int AllEmotesZeroLogCount,
    int LogOnlyEmoteCount,
    int LiveOnlyEmoteCount,
    double? LogOnlyShareOfLogTotal,
    double? LiveOnlyShareOfLiveTotal,
    double? TotalDeviationIncludingFlaggedDays,
    int FlaggedIncludedDays,
    long UnknownNameHits,
    long AmbiguousNameHits,
    long BeforeFirstSeenHits,
    long AfterArchivedHits,
    long FirstSeenUnknownHits,
    int AmbiguousNameCount,
    int ArchivedWithoutDateCount,
    long TotalMessages,
    long BotMessages,
    long SharedChatMessages,
    long OutsideDayCount,
    long NonPrivmsgLines,
    long MalformedLines,
    double? SingleChatterCellShare,
    ValueList<int> KHistogram,
    long CellCount,
    long DistinctChatterDaySum,
    int HumanOnlyDays,
    int LogDays,
    int NoLogDays,
    double? DayRatioMedian,
    ValueList<ReplayDayRatio> LiveGapDays,
    ValueList<ReplayDayRatio> CoverageQuestionableDays);

/// <summary>How the run itself went — the part of the report that is about the fetch, not the numbers.</summary>
public sealed record ReplayRunInfo(
    DateOnly WindowFrom,
    DateOnly WindowTo,
    DateOnly? BotSplitCutover,
    int WindowDays,
    int DayLineCount,
    long TotalBytes,
    int RateLimitedDays,
    DateOnly? ResumePoint,
    bool RunComplete);

/// <summary>
/// The final report, reproducible from the day lines alone: two runs of
/// <see cref="ReplayFidelityCalculator.Compute"/> over the same arguments are <c>Equals</c>.
/// </summary>
public sealed record ReplayFinalReport(
    ReplayRunInfo Run,
    ReplayGateMetrics Gate,
    ReplayPlausibility Plausibility,
    ReplayDiagnostics Diagnostics);

/// <summary>
/// A read-only list with value equality. Exists for one reason: the report must satisfy "two
/// <c>Compute</c> calls produce equal objects", and a record's generated <c>Equals</c> compares
/// list members by reference — <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// included, whose own equality is reference equality of the backing array.
/// </summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    public ValueList(IEnumerable<T> items) => _items = [.. items];

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(ValueList<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return _items.SequenceEqual(other._items);
    }

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
