using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// What a run is, reduced to the values that make two runs comparable. Everything in here goes into
/// the report file's name (via its digest), so a changed window, a changed bot list or a changed
/// data snapshot cannot continue an older file — it starts a new one.
/// <para>
/// Deliberately without the load timestamp: that changes on every run and would make every resume
/// impossible. It sits next to the identity in <see cref="HarnessReportHeader"/> instead, where it
/// documents when the comparison data was read without pretending to identify it.
/// </para>
/// </summary>
public sealed record HarnessRunIdentity(
    string ChannelId,
    string TwitchChannelId,
    string ChannelName,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    DateOnly? BotSplitCutover,
    IReadOnlyList<string> BotAccountIds,
    string AlgorithmVersion,
    string InputHash);

/// <summary>Line 1 of the protocol.</summary>
public sealed record HarnessReportHeader(HarnessRunIdentity Identity, DateTime LoadedAtUtc);

/// <summary>
/// Something that happened to the run but produced no day: a 429, a body timeout, an exhausted byte
/// budget, a cancellation. Written so the next run can carry the count into its final report — the
/// day lines alone would say nothing about how often the archive refused.
/// </summary>
public sealed record HarnessEventLine(DateTime AtUtc, DateOnly Day, string Status, int? HttpStatusCode, string Message);

/// <summary>Everything the protocol holds below its header.</summary>
public sealed record HarnessReportContent(IReadOnlyList<ReplayDayLine> Days, IReadOnlyList<HarnessEventLine> Events);

/// <summary>
/// A report file that cannot be continued. One base type for the two ways that happens, so the
/// runner answers both the same way: a German line and exit code 3, never a stack trace.
/// </summary>
public class HarnessReportFileException(string message) : Exception(message);

/// <summary>
/// Thrown when a report file's head describes a different run than the one about to continue it.
/// Reachable only by renaming a file by hand — the file name carries the identity digest — which is
/// exactly why the head is compared anyway.
/// </summary>
public sealed class HarnessReportIdentityMismatchException(string message) : HarnessReportFileException(message);

/// <summary>
/// Thrown for a damaged line anywhere but at the very end of the file. The last line is a different
/// case and never lands here: a process that died mid-write leaves an unfinished day, which the
/// reader simply drops.
/// </summary>
public sealed class HarnessReportCorruptException(string message) : HarnessReportFileException(message);

/// <summary>
/// The JSONL protocol of one harness run: one header line, one line per finished archive day, one
/// line per event, and the two final reports beside it.
/// <para>
/// Append-per-line with an immediate flush is the whole crash story (design Failure Mode "Abschluss
/// / Prozesstod"): a killed process costs the day that was in flight and nothing else, and the
/// closing step is a pure function of the lines on disk, so running it twice produces the same
/// <c>.report.json</c> byte for byte.
/// </para>
/// <para>
/// No chatter id, no message text and no chatter login is ever written here — the day counter
/// collapses its per-cell chatter sets before it hands a line over, and nothing on this path can
/// reintroduce them.
/// </para>
/// </summary>
public sealed class HarnessReportFile
{
    private const string HeaderKind = "header";
    private const string DayKind = "day";
    private const string EventKind = "event";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public HarnessReportFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        var stem = System.IO.Path.ChangeExtension(path, null);
        ReportJsonPath = stem + ".report.json";
        ReportMarkdownPath = stem + ".report.md";
    }

    public string Path { get; }

    public string ReportJsonPath { get; }

    public string ReportMarkdownPath { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// The file name a run of this identity has to use. The digest at the end is what makes
    /// "same head ⇒ resume, different head ⇒ new file" a property of the file system rather than of
    /// a comparison someone has to remember to do (Plan-Entscheidung 8).
    /// </summary>
    public static string BuildFileName(HarnessRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(SerializeIdentity(identity))))[..12];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Sanitize(identity.ChannelName)}-{identity.WindowFrom:yyyy-MM-dd}-{identity.WindowTo:yyyy-MM-dd}-{digest}.jsonl");
    }

    /// <summary>The canonical text form the identity is compared and hashed by.</summary>
    public static string SerializeIdentity(HarnessRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.Serialize(identity, LineOptions);
    }

    public void WriteHeader(HarnessReportHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, Serialize(new HarnessJsonLine(HeaderKind, header, null, null)));
    }

    /// <summary>
    /// Reads line 1 and refuses anything that is not the expected run, byte for byte on the
    /// canonical identity form.
    /// </summary>
    public HarnessReportHeader ReadHeader(HarnessRunIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        var firstLine = File.ReadLines(Path).FirstOrDefault()
            ?? throw new HarnessReportIdentityMismatchException(
                $"Die Berichtsdatei '{Path}' ist leer und trägt keinen Kopf.");

        HarnessJsonLine? line;
        try
        {
            line = JsonSerializer.Deserialize<HarnessJsonLine>(firstLine, LineOptions);
        }
        catch (JsonException ex)
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die erste Zeile der Berichtsdatei '{Path}' ist kein lesbarer Kopf: {ex.Message}");
        }

        if (line?.Kind != HeaderKind || line.Header is null)
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die erste Zeile der Berichtsdatei '{Path}' ist kein Kopf.");
        }

        var expected = SerializeIdentity(expectedIdentity);
        var actual = SerializeIdentity(line.Header.Identity);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die Berichtsdatei '{Path}' trägt eine fremde Identität; ein Lauf mit anderem Fenster, "
                + "anderer Bot-Liste oder anderem Datenstand darf sie nicht fortsetzen.");
        }

        return line.Header;
    }

    /// <summary>
    /// Every finished day and every event below the header. A truncated last line — the process
    /// died mid-write — is discarded, because that day was never finished; a truncated line
    /// anywhere else is corruption and throws, since silently skipping it would leave a gap the
    /// final report would then present as a complete window.
    /// </summary>
    public HarnessReportContent ReadDays()
    {
        var days = new List<ReplayDayLine>();
        var events = new List<HarnessEventLine>();

        if (!Exists)
        {
            return new HarnessReportContent(days, events);
        }

        var lines = File.ReadAllLines(Path);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            HarnessJsonLine? line;
            try
            {
                line = JsonSerializer.Deserialize<HarnessJsonLine>(raw, LineOptions);
            }
            catch (JsonException ex)
            {
                // The last line is the crash case: a process killed mid-write leaves an unfinished
                // day, which is dropped because that day never happened. Anywhere else the same
                // damage is a hole in the middle of the window, and continuing would let the final
                // report present an incomplete run as a complete one.
                if (i == lines.Length - 1)
                {
                    break;
                }

                throw new HarnessReportCorruptException(
                    $"Zeile {i + 1} der Berichtsdatei '{Path}' ist unlesbar; die Datei ist beschädigt: {ex.Message}");
            }

            switch (line?.Kind)
            {
                case HeaderKind:
                    break;
                case DayKind when line.Day is not null:
                    days.Add(line.Day);
                    break;
                case EventKind when line.Event is not null:
                    events.Add(line.Event);
                    break;
                default:
                    if (i == lines.Length - 1)
                    {
                        break;
                    }

                    throw new HarnessReportCorruptException(
                        $"Zeile {i + 1} der Berichtsdatei '{Path}' ist unlesbar; die Datei ist beschädigt.");
            }
        }

        return new HarnessReportContent(days, events);
    }

    public void AppendDay(ReplayDayLine day)
    {
        ArgumentNullException.ThrowIfNull(day);
        Append(new HarnessJsonLine(DayKind, null, day, null));
    }

    public void AppendEvent(HarnessEventLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Append(new HarnessJsonLine(EventKind, null, null, line));
    }

    /// <summary>
    /// Writes both final reports through a temp file plus rename, so a reader never sees half a
    /// report and a crash mid-write leaves the previous one intact.
    /// </summary>
    public void WriteFinalReportAtomically(ReplayFinalReport report, string markdown)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(markdown);

        WriteAtomically(ReportJsonPath, JsonSerializer.Serialize(report, ReportOptions));
        WriteAtomically(ReportMarkdownPath, markdown);
    }

    private void Append(HarnessJsonLine line) => File.AppendAllText(Path, Serialize(line));

    private static string Serialize(HarnessJsonLine line) => JsonSerializer.Serialize(line, LineOptions) + "\n";

    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string Sanitize(string channelName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(channelName.Length);
        foreach (var character in channelName)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The one envelope every protocol line is written as. One type rather than three keeps reading
    /// to a single deserialize call, and <c>kind</c> stays the first property so a line is
    /// recognizable by eye in the file.
    /// </summary>
    private sealed record HarnessJsonLine(
        string Kind,
        HarnessReportHeader? Header,
        ReplayDayLine? Day,
        HarnessEventLine? Event);
}
