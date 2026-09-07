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
public sealed record ReplayUsageRow(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount);

/// <summary>
/// The frozen comparison window plus the channel's bot-split cutover and the explicitly configured
/// shared-chat cutover (D4).
/// <para>
/// <see cref="BotSplitCutover"/> is the earliest day whose live rows can carry a
/// <c>BotUseCount</c> at all; it stays in the identity, the report header and the report line
/// unchanged (Freeze), but as of <c>harness-2</c> it no longer decides which days the gate rates —
/// see <see cref="SharedChatCutover"/>.
/// </para>
/// <para>
/// <see cref="SharedChatCutover"/> is the day from which <c>HumanOnly</c> is true, and the only
/// input to that decision (B5): <c>Day &gt;= SharedChatCutover</c>. This is not merely convenient,
/// it is more correct than gating on <see cref="BotSplitCutover"/> ever was — the bot split
/// deployed on 2026-09-01, before #73, so every day at or after the shared-chat cutover
/// necessarily also lies after the bot-split deploy and carries bots separately, independent of
/// when this particular channel happened to show its first bot. It is the rolled-out *contract*
/// that decides, not the first sighting in the data — exactly the E4 imprecision the bot cutover
/// carries. <c>null</c> means no cutover was configured; days before it (and every day if it is
/// <c>null</c>) cannot be compared human-only and never enter the gate.
/// </para>
/// </summary>
public sealed record ReplayWindow(DateOnly From, DateOnly To, DateOnly? BotSplitCutover, DateOnly? SharedChatCutover);

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

    /// <summary>
    /// The archive answered 429. Vocabulary only: the harness deliberately never writes a day line
    /// with this status, because a day line means "finished" and the resume would then skip that day
    /// forever. It writes an event line instead, and the run-level count reaches the report through
    /// <see cref="ReplayFidelityCalculator.Compute"/>'s <c>rateLimitedDays</c> parameter. Should such
    /// a line ever appear anyway, the calculator treats it like every other non-Complete status: the
    /// day has no usable log.
    /// </summary>
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

    /// <summary>
    /// A diagnostic run (<c>--diagnostic</c>): the numbers are computed as usual, but the run had
    /// no shared-chat cutover to gate against, or the operator explicitly asked for numbers without
    /// a verdict. Set unconditionally on a diagnostic run, in addition to whatever other reasons
    /// apply (D4/Plan-Entscheidung 7).
    /// </summary>
    public const string DiagnosticRun = "diagnostic-run";
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
/// <para>
/// <see cref="SharedChatMessageCount"/> and <see cref="SharedChatCounts"/> (#73) are easy to
/// confuse and count different things. The former is a message-level control number and counts
/// only unambiguously foreign messages (<c>MessageOrigin.Foreign</c>) — it does not move for an
/// indeterminate one, which has its own counter, <see cref="IndeterminateMessageCount"/>. The
/// latter is an emote-hit dictionary and receives hits from <b>both</b> foreign and indeterminate
/// messages (<c>UsageCategory.SharedChat</c> folds the two together, D2/B2), because the rule that
/// hides the shared-chat *component* from being counted as this channel's own use is the same for
/// both — a hit is a hit. The control sum: <c>Σ SharedChatCounts &gt; 0</c> implies
/// <c>SharedChatMessageCount + IndeterminateMessageCount &gt; 0</c>, never
/// <c>SharedChatMessageCount &gt; 0</c> alone.
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
    int IndeterminateMessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int OutsideDayCount,
    IReadOnlyDictionary<string, int> HumanCounts,
    IReadOnlyDictionary<string, int> BotCounts,
    IReadOnlyDictionary<string, int> SharedChatCounts,
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
/// <para>
/// <c>HumanLogTotal</c> and <c>HumanLiveTotal</c> carry "human" in the name on purpose: they are
/// the denominators of the gate and exclude bots on both sides (live is <c>UseCount</c> alone, log
/// is the human hit count), whereas <see cref="ReplayPlausibility"/> right next to them in the
/// report sums both sides <b>with</b> bots. The three numbers are not comparable.
/// </para>
/// <para>
/// <c>BottomQuartileLiveTieCount</c> and <c>BottomQuartileLogTieCount</c> are read-only visibility
/// into the tie-break, never a second gate: both rankings break ties ordinally by emote id, so
/// <c>TakeLast(BottomQuartileSize)</c> can cut in the middle of a block of equal counts, and which
/// side of the cut an id lands on is then decided by its GUID rather than by anything about its
/// usage. Each field counts how many population entries share the exact count value sitting at that
/// ranking's cut point — a long-tailed population (many emotes tied at, say, one use over the whole
/// window) can make this most of <c>BottomQuartileSize</c>, at which point
/// <c>BottomQuartilePrecision</c> is largely tie-break noise rather than a measured rank deviation.
/// The pre-registered threshold and the ranking itself are unchanged; this is purely a reading aid.
/// </para>
/// </summary>
public sealed record ReplayGateMetrics(
    int RatedDays,
    int PopulationSize,
    long HumanLogTotal,
    long HumanLiveTotal,
    double? TotalDeviation,
    double? Top20Recall,
    int Top20Size,
    double? BottomQuartilePrecision,
    int BottomQuartileSize,
    int BottomQuartileLiveTieCount,
    int BottomQuartileLogTieCount,
    bool GateEligible,
    ValueList<string> GateIneligibleReasons);

/// <summary>
/// Plausibility check (a): both sides summed <b>including</b> bots over every day that has a log.
/// Covers the days before the shared-chat cutover, which the gate cannot use.
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
/// <para>
/// Two day counts here are easy to confuse. <c>HumanOnlyDays</c> counts every day of the run at or
/// after the shared-chat cutover, <b>including days without a log</b> — it says how far the
/// human-only comparison could reach at all. How many days metric (b) actually covers is
/// <c>FlaggedIncludedDays</c> (has a log and is human-only) and, after the coverage and gap
/// markers, <c>ReplayGateMetrics.RatedDays</c>.
/// </para>
/// <para>
/// <c>DailyDeviation</c> and <c>DailyDeviationWithOneDayTolerance</c> are the secondary day diff.
/// They exist because <c>UsageStat.Date</c> is the day of the <i>flush</i>, not of the message:
/// the 30-second batch and its requeue rounds push usage around midnight onto the following day,
/// which inflates the daily numbers without the window sums moving at all. The tolerant variant
/// lets a log day be explained by the neighbouring live days; the gap between the two figures is
/// the size of that artefact, and it is what tells a later reader whether a bad coverage ratio was
/// a data problem or a day offset.
/// </para>
/// <para>
/// <c>SignallessRatedDays</c> counts rated days on which neither side saw anything at all (a dead
/// channel day: log total and live total both zero). Such a day passes every rating condition and
/// raises <c>RatedDays</c> towards the pre-registered minimum of 20 without contributing a single
/// comparison. The gate definition is published in #69 and stays as it is; this field exists so a
/// reader can subtract those days instead of being quietly misled by the count.
/// </para>
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
    double? DailyDeviation,
    double? DailyDeviationWithOneDayTolerance,
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
    long IndeterminateMessages,
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
    int SignallessRatedDays,
    double? DayRatioMedian,
    ValueList<ReplayDayRatio> LiveGapDays,
    ValueList<ReplayDayRatio> CoverageQuestionableDays);

/// <summary>How the run itself went — the part of the report that is about the fetch, not the numbers.</summary>
/// <param name="RateLimitedDays">
/// How many channel-day requests the archive throttled with a 429, across every run that wrote into
/// this report file. Supplied by the caller rather than counted from <paramref name="DayLineCount"/>'s
/// lines: a throttled day produces no day line at all (see
/// <see cref="ReplayDayStatuses.RateLimited"/>), so deriving it here would report 0 forever.
/// </param>
/// <param name="ResumePoint">
/// The last archive day that has a line. Also supplied by the caller. On a complete run — the only
/// kind that currently produces a final report — this equals <paramref name="WindowTo"/> and is a
/// statement of where the run ended, not an instruction to continue anywhere.
/// </param>
/// <param name="TotalBytes">
/// Also supplied by the caller, and for the same reason as <see cref="RateLimitedDays"/>: an aborted
/// attempt's bytes live only on an event line, never on a day line, so deriving this from
/// <see cref="DayLineCount"/>'s lines alone would under-report every run an abort ever touched.
/// </param>
public sealed record ReplayRunInfo(
    DateOnly WindowFrom,
    DateOnly WindowTo,
    DateOnly? BotSplitCutover,
    DateOnly? SharedChatCutover,
    int WindowDays,
    int DayLineCount,
    long TotalBytes,
    int RateLimitedDays,
    DateOnly? ResumePoint,
    bool RunComplete,
    // Not part of HarnessRunIdentity (Plan-Entscheidung 7, D4): a diagnostic run counts exactly the
    // same as a binding one, it only carries no gate verdict. A binding run may therefore resume a
    // diagnostic file of the same identity and simply rewrite the report without this marker — that
    // is intended, not a bug in the resume logic.
    bool Diagnostic);

/// <summary>
/// The final report: two runs of <see cref="ReplayFidelityCalculator.Compute"/> over the same
/// arguments are <c>Equals</c>. Every measured number comes from the day lines; only the two
/// run-level counters on <see cref="ReplayRunInfo"/> that no day line can carry are passed in.
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
