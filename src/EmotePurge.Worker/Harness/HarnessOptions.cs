namespace EmotePurge.Worker.Harness;

/// <summary>
/// Bound from configuration section <c>Harness:*</c>. A plain POCO registered as a singleton, the
/// same shape as <c>ChatLogArchiveOptions</c> next to it — nothing here is reloadable, the process
/// lives for exactly one run.
/// <para>
/// Everything that is not the channel and the window length lives here rather than on the command
/// line (Plan-Entscheidung 7): the argument parser has to run before the configuration exists, so
/// each extra flag would be a knob that cannot be set per environment.
/// </para>
/// </summary>
public sealed class HarnessOptions
{
    /// <summary>The window the pre-registration in issue #69 fixed; see <see cref="WindowDays"/>.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>
    /// Where the JSONL protocol and both final reports are written. Relative paths resolve against
    /// the process's working directory; in the one-shot container this is a bind mount.
    /// </summary>
    public string OutputDirectory { get; set; } = "harness-reports";

    /// <summary>
    /// Hard ceiling on the bytes one run may pull from the archive, across all of its days and
    /// across resumes. Configured in megabytes because that is the unit the operator budgets in;
    /// the runner converts to bytes once and never rounds again.
    /// </summary>
    public int MaxMegabytesPerRun { get; set; } = 200;

    /// <summary>
    /// The window length a run uses when <c>--days</c> is omitted. 30 is the pre-registered
    /// figure; a run with any other length reports <c>gateEligible = false</c> with the reason
    /// <c>window-not-30-days</c>, so a short local run can never be read as a gate run.
    /// </summary>
    public int WindowDays { get; set; } = DefaultWindowDays;
}
