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
    /// </summary>
    public sealed record RunHarness(string ChannelName, int? Days) : HarnessCommandLineResult;

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
/// <c>harness &lt;channel&gt; [--days &lt;n&gt;]</c> means harness, and every other shape is a
/// refusal with exit code 2 rather than a guess.
/// </para>
/// </summary>
public static class HarnessCommandLine
{
    /// <summary>The verb that selects the harness. Belongs in the compose service's entrypoint.</summary>
    public const string HarnessVerb = "harness";

    /// <summary>The only option the harness takes; everything else comes from configuration.</summary>
    public const string DaysOption = "--days";

    public const int MinDays = 1;

    /// <summary>
    /// User decision D3 of 2026-09-05: 90, not the pre-registration's 30. The binding run uses 30;
    /// a larger window is allowed so a later question about deeper history does not need a code
    /// change, and the report always names the window it actually used.
    /// </summary>
    public const int MaxDays = 90;

    private const string Usage = "Aufruf: 'harness <kanal> [--days <n>]' oder gar kein Argument für den Worker.";

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

        if (args.Length == 2)
        {
            return new HarnessCommandLineResult.RunHarness(channelName, null);
        }

        if (!string.Equals(args[2], DaysOption, StringComparison.Ordinal))
        {
            return Invalid($"Unbekanntes Argument '{args[2]}'. {Usage}");
        }

        if (args.Length == 3)
        {
            return Invalid($"'{DaysOption}' braucht eine Zahl von {MinDays} bis {MaxDays}. {Usage}");
        }

        if (args.Length > 4)
        {
            return Invalid($"Zu viele Argumente ab '{args[4]}'. {Usage}");
        }

        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days < MinDays
            || days > MaxDays)
        {
            return Invalid($"'{args[3]}' ist keine Fensterlänge von {MinDays} bis {MaxDays} Tagen. {Usage}");
        }

        return new HarnessCommandLineResult.RunHarness(channelName, days);
    }

    private static HarnessCommandLineResult.Invalid Invalid(string message) => new(message);
}
