using System.Globalization;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// What the worker image was asked to be this time. A closed hierarchy — the private constructor
/// plus the nested cases mean no fourth case can be invented at a call site, so the switch in
/// <c>Program</c> stays exhaustive by construction.
/// </summary>
public abstract record HarnessCommandLineResult
{
    private HarnessCommandLineResult()
    {
    }

    /// <summary>No arguments: the normal worker, with all nine hosted services.</summary>
    public sealed record RunWorker : HarnessCommandLineResult;

    /// <summary>
    /// The accuracy harness for one channel.
    /// <para>
    /// <see cref="Days"/> is <c>null</c> when <c>--days</c> was omitted. It cannot be filled in
    /// here: argument checking deliberately runs before <c>Host.CreateApplicationBuilder</c>, so
    /// there is no configuration yet, and the configured default (<c>Harness:WindowDays</c>) is
    /// applied by the entry point instead.
    /// </para>
    /// <para>
    /// <see cref="Diagnostic"/> is the escape hatch from the shared-chat cutover's fail-closed rule
    /// (D4): a run without <c>--diagnostic</c> and without a parsable
    /// <c>Harness:SharedChatCutover</c> refuses to start at all, and only a diagnostic run may. It
    /// is not part of the run identity — see the remark at its use in <c>HarnessRunner</c>.
    /// </para>
    /// </summary>
    public sealed record RunHarness(string ChannelName, int? Days, bool Diagnostic) : HarnessCommandLineResult;

    /// <summary>Anything else. <see cref="Message"/> is the single German line for stderr.</summary>
    public sealed record Invalid(string Message) : HarnessCommandLineResult;
}

/// <summary>
/// The whole grammar of the worker image's two entry points, as a pure function of
/// <c>args</c> — no configuration, no host, no I/O, so it can run as the very first statement of
/// <c>Program</c>.
/// <para>
/// That position is the point. <c>docker compose run</c> replaces a service's <c>command</c>, not
/// its <c>entrypoint</c>: a harness service whose verb sat in the <c>command</c> would hand
/// <c>Program</c> nothing but the channel name, and a lenient parser would then start the full
/// worker — a second IRC counter next to the production one, doubling every usage row through the
/// additive UPSERT (Codex-adversarial "Fail-open CLI"). Hence: no arguments means worker, exactly
/// <c>harness &lt;channel&gt; [--days &lt;n&gt;] [--diagnostic]</c> means harness (the two options
/// in either order, each at most once), and every other shape is a refusal with exit code 2 rather
/// than a guess.
/// </para>
/// </summary>
public static class HarnessCommandLine
{
    /// <summary>The verb that selects the harness. Belongs in the compose service's entrypoint.</summary>
    public const string HarnessVerb = "harness";

    /// <summary>Takes a value; everything else the harness needs comes from configuration.</summary>
    public const string DaysOption = "--days";

    /// <summary>
    /// A flag, no value. Runs the harness without an explicit shared-chat cutover and without a
    /// gate verdict (D4) — the only case in which a run may proceed without one.
    /// </summary>
    public const string DiagnosticOption = "--diagnostic";

    public const int MinDays = 1;

    /// <summary>
    /// User decision D3 of 2026-09-05: 90, not the pre-registration's 30. The binding run uses 30;
    /// a larger window is allowed so a later question about deeper history does not need a code
    /// change, and the report always names the window it actually used.
    /// </summary>
    public const int MaxDays = 90;

    private const string Usage =
        "Aufruf: 'harness <kanal> [--days <n>] [--diagnostic]' oder gar kein Argument für den Worker.";

    public static HarnessCommandLineResult Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new HarnessCommandLineResult.RunWorker();
        }

        if (!string.Equals(args[0], HarnessVerb, StringComparison.Ordinal))
        {
            return Invalid($"Unbekanntes Argument '{args[0]}'. {Usage}");
        }

        if (args.Length < 2)
        {
            return Invalid($"Dem Kommando '{HarnessVerb}' fehlt der Kanalname. {Usage}");
        }

        var channelName = args[1];
        if (string.IsNullOrWhiteSpace(channelName) || channelName.StartsWith('-'))
        {
            return Invalid($"'{channelName}' ist kein Kanalname. {Usage}");
        }

        int? days = null;
        var diagnostic = false;

        var index = 2;
        while (index < args.Length)
        {
            var token = args[index];

            if (string.Equals(token, DaysOption, StringComparison.Ordinal))
            {
                if (days is not null)
                {
                    return Invalid($"'{DaysOption}' darf nur einmal angegeben werden. {Usage}");
                }

                if (index + 1 >= args.Length)
                {
                    return Invalid($"'{DaysOption}' braucht eine Zahl von {MinDays} bis {MaxDays}. {Usage}");
                }

                var value = args[index + 1];
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDays)
                    || parsedDays < MinDays
                    || parsedDays > MaxDays)
                {
                    return Invalid($"'{value}' ist keine Fensterlänge von {MinDays} bis {MaxDays} Tagen. {Usage}");
                }

                days = parsedDays;
                index += 2;
                continue;
            }

            if (string.Equals(token, DiagnosticOption, StringComparison.Ordinal))
            {
                if (diagnostic)
                {
                    return Invalid($"'{DiagnosticOption}' darf nur einmal angegeben werden. {Usage}");
                }

                diagnostic = true;
                index += 1;
                continue;
            }

            return Invalid($"Unbekanntes Argument '{token}'. {Usage}");
        }

        return new HarnessCommandLineResult.RunHarness(channelName, days, diagnostic);
    }

    private static HarnessCommandLineResult.Invalid Invalid(string message) => new(message);
}
