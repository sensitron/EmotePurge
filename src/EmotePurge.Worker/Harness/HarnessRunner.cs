using System.Diagnostics;
using System.Globalization;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// One harness run of issue #69: read one channel's frozen 30-day window out of the chat-log
/// archive, count it with the live path's own matching rule and bot detector, and write the
/// pre-registered figures next to the live <c>UsageStat</c> numbers.
/// <para>
/// Read-only end to end. It touches Postgres exclusively through <see cref="IChannelService"/> and
/// <see cref="IUsageStatQueryService"/> (Regel 4), never writes a row, and never publishes or
/// subscribes on Redis itself. Everything it produces is a file.
/// </para>
/// <para>
/// <b>It still needs Redis reachable at startup, just not for anything of its own.</b>
/// <see cref="IChannelService"/> takes an <c>IRedisPublisher</c> constructor dependency, which sits
/// on the same eager-connecting <c>IConnectionMultiplexer</c> singleton the whole worker image
/// shares (<c>ServiceCollectionExtensions.AddEmotePurgeInfrastructure</c>, no
/// <c>abortConnect=false</c>) — resolving this class resolves that singleton, and
/// <c>ConnectionMultiplexer.Connect</c> is synchronous and throws if nothing answers. Not worth
/// carving a Redis-free construction path out of shared DI wiring for a property nobody needs;
/// <c>Program</c> wraps the resolution in a try/catch instead and returns
/// <see cref="ExitUnexpectedError"/> with a German line, so an unreachable Redis at startup gets a
/// documented exit code rather than a runtime-invented one.
/// </para>
/// <para>
/// <b>The archive client is taken once, through the constructor, and held for the whole day
/// loop.</b> Its minimum delay between two requests is instance state, and the typed-client
/// registration is transient: a runner that resolved a fresh client per channel-day would drop the
/// only self-imposed protection against hammering a free third-party service — silently, with no
/// exception and no log line (T3 review finding; see the registration comment in
/// <c>ServiceCollectionExtensions</c>).
/// </para>
/// <para>
/// The return value is the process exit code, and the four non-zero ones say different things on
/// purpose: a violated precondition (3) means the question could not be asked, an abort (4) means
/// it can be asked again from the resume point, and "undecidable" (5) means the approach itself has
/// to be reassessed. None of them is "the numbers were bad" — that verdict is a human reading the
/// report against the pre-registration in #69.
/// </para>
/// </summary>
public sealed class HarnessRunner(
    IChannelService channelService,
    IUsageStatQueryService usageStatQueryService,
    IChatLogArchiveClient archiveClient,
    IBotChatterDetector botChatterDetector,
    HarnessOptions options,
    TimeProvider timeProvider,
    ILogger<HarnessRunner> logger)
{
    /// <summary>
    /// Part of the run identity: a changed counting rule must not resume a file counted by the old
    /// one. Bump it whenever the matching, the day boundaries or the day-line shape change.
    /// </summary>
    public const string AlgorithmVersion = "harness-1";

    /// <summary>The window covered completely; both final reports were written.</summary>
    public const int ExitSuccess = 0;

    /// <summary>
    /// The command line did not parse (see <see cref="HarnessCommandLine.Parse"/>): unknown verb,
    /// missing channel, malformed <c>--days</c>, or extra arguments. Returned by <c>Program</c>
    /// before a host is even built, so it lives here only as a named constant for the exit-code
    /// list to stay in one place — the value itself never runs through <see cref="RunAsync"/>.
    /// </summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>A precondition was violated; the question could not be asked at all.</summary>
    public const int ExitPreconditionViolated = 3;

    /// <summary>Aborted with a resume point: 429, body timeout, byte ceiling, transport, cancellation.</summary>
    public const int ExitAbortedWithResumePoint = 4;

    /// <summary>The logs carry neither badges nor user ids, so the bot split cannot be made.</summary>
    public const int ExitUndecidable = 5;

    /// <summary>
    /// Something inside the harness itself failed — most plausibly the counting callback, whose
    /// exceptions the archive client propagates to us by contract (T3). Deliberately not folded into
    /// <see cref="ExitAbortedWithResumePoint"/>: that code invites "just run it again", and a defect
    /// in our own counting would meet the operator with the same failure a second time. The full
    /// exception is logged; the file on disk stays valid and resumable.
    /// </summary>
    public const int ExitUnexpectedError = 6;

    private const int BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// Runs once and returns the process exit code. Nothing escapes as an exception: the operator of
    /// a one-shot container gets a German line and a defined code, never a stack trace with an
    /// exit status invented by the runtime.
    /// </summary>
    public async Task<int> RunAsync(string channelName, int days, CancellationToken ct)
    {
        try
        {
            return await ExecuteAsync(channelName, days, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled outside the day loop — during a query, say. The loop has its own handler that
            // still gets an event line written; here there is no day in flight to record.
            logger.LogError("Der Harness-Lauf für Kanal '{Kanal}' wurde abgebrochen.", channelName);
            return ExitAbortedWithResumePoint;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Der Harness-Lauf für Kanal '{Kanal}' ist mit einem unerwarteten Fehler abgebrochen. Bereits geschriebene Tageszeilen bleiben gültig; ein erneuter Aufruf setzt dort fort.",
                channelName);
            return ExitUnexpectedError;
        }
    }

    private async Task<int> ExecuteAsync(string channelName, int days, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        // Both sources of this number end up here: the command line, which HarnessCommandLine has
        // already clamped, and Harness:WindowDays, which nothing else checks — an environment
        // variable in the compose file would otherwise buy a 500-day run past the same limit.
        if (days < HarnessCommandLine.MinDays || days > HarnessCommandLine.MaxDays)
        {
            logger.LogError(
                "Die Fensterlänge {Tage} liegt außerhalb der erlaubten {Min} bis {Max} Tage; per '--days' oder über 'Harness:WindowDays' korrigieren.",
                days, HarnessCommandLine.MinDays, HarnessCommandLine.MaxDays);
            return ExitPreconditionViolated;
        }

        var stopwatch = Stopwatch.StartNew();
        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var channel = await channelService.GetByNameAsync(channelName, ct);
        if (channel is null)
        {
            logger.LogError("Kanal '{Kanal}' ist nicht erfasst; ohne Kanalzeile gibt es nichts zu vergleichen.", channelName);
            return ExitPreconditionViolated;
        }

        // No login fallback, deliberately (#34/#44): the archive is addressed by the immutable
        // Twitch id, and falling back to the login is exactly how a renamed channel ended up as two
        // rows from two sources.
        if (string.IsNullOrEmpty(channel.TwitchChannelId))
        {
            logger.LogError(
                "Kanal '{Kanal}' hat keine Twitch-ID; der Log-Abruf läuft ausschließlich über die unveränderliche ID (#34/#44).",
                channel.ChannelName);
            return ExitPreconditionViolated;
        }

        // The window ends on the last fully finished UTC day (Codex-adversarial: today is still
        // being written by the live worker), and starts one day after tracking resumed, because the
        // join day itself is only partially measured.
        var to = DateOnly.FromDateTime(startedAtUtc).AddDays(-1);
        var trackedSince = TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt);
        var firstFullyTrackedDay = DateOnly.FromDateTime(trackedSince).AddDays(1);
        var from = Later(firstFullyTrackedDay, to.AddDays(-(days - 1)));
        var availableDays = to.DayNumber - from.DayNumber + 1;
        if (availableDays != days)
        {
            logger.LogError(
                "Kanal '{Kanal}' hat ab dem ersten vollständig gemessenen Tag ({Start}) nur {Vorhanden} Tage Messung bis {Ende}; verlangt sind {Verlangt}.",
                channel.ChannelName, Iso(firstFullyTrackedDay), Math.Max(availableDays, 0), Iso(to), days);
            return ExitPreconditionViolated;
        }

        var lifetimes = await usageStatQueryService.GetEmoteLifetimesAsync(channel.Id, ct);
        var liveRowDtos = await usageStatQueryService.GetRowsAsync([.. lifetimes.Select(e => e.Id)], from, to, ct);
        var botSplitCutover = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, ct);
        var botAccountIds = botChatterDetector.KnownBotAccountIds;
        var loadedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var identity = new HarnessRunIdentity(
            channel.Id,
            channel.TwitchChannelId,
            channel.ChannelName,
            from,
            to,
            botSplitCutover,
            [.. botAccountIds.Order(StringComparer.Ordinal)],
            AlgorithmVersion,
            HarnessInputHash.Compute(lifetimes, liveRowDtos, botAccountIds, from));

        var file = new HarnessReportFile(Path.Combine(options.OutputDirectory, HarnessReportFile.BuildFileName(identity)));
        HarnessReportContent existing;
        bool resumed;
        if (file.Exists)
        {
            try
            {
                file.ReadHeader(identity);
                existing = file.ReadDays();
            }
            catch (HarnessReportFileException ex)
            {
                // A foreign identity or a damaged line in the middle of the file. Both mean the same
                // thing — this file cannot be continued — and both end the same way: a German line
                // and exit code 3, never a stack trace. (A truncated *last* line is not damage; the
                // reader drops it, because that day was never finished.)
                logger.LogError(ex, "Die vorhandene Berichtsdatei '{Datei}' kann nicht fortgesetzt werden.", file.Path);
                return ExitPreconditionViolated;
            }

            resumed = true;
        }
        else
        {
            file.WriteHeader(new HarnessReportHeader(identity, loadedAtUtc));
            existing = new HarnessReportContent([], []);
            resumed = false;
        }

        // Deliberately its own type, not the query DTO — see the remark on ReplayEmote.
        var emotes = lifetimes
            .Select(e => new ReplayEmote(e.Id, e.Name, e.IsArchived, e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt))
            .ToList();
        var liveRows = liveRowDtos
            .Select(r => new ReplayUsageRow(r.EmoteId, r.Date, r.UseCount, r.BotUseCount))
            .ToList();
        var window = new ReplayWindow(from, to, botSplitCutover);

        var dayLines = existing.Days.ToDictionary(d => d.Day);
        // Both finished days and aborted-but-received attempts count against the cap: a day that
        // ended in ByteCapExceeded/BodyTimeout/TransportFailure still pulled bytes out of the archive,
        // it just never became a day line. Without the event half, five resumes could each burn a
        // fresh cap's worth of bytes against a service that never agreed to any of it (DECISIONS: the
        // cap is a foreign-load brake "über alle Tage und alle Resumes eines Laufs").
        var bytesUsed = existing.Days.Sum(d => d.Bytes) + existing.Events.Sum(e => e.Bytes);
        var capBytes = (long)options.MaxMegabytesPerRun * BytesPerMegabyte;

        // Plan-Entscheidung 13: the window-wide chatter set is transient and never written, so a
        // resumed run cannot know it. The per-(emote, day) k-distribution is unaffected — it is
        // closed inside each day line.
        var distinctChatters = resumed ? null : new HashSet<string>(StringComparer.Ordinal);
        var fallbackChecked = false;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (dayLines.ContainsKey(day))
            {
                continue;
            }

            var remainingBytes = capBytes - bytesUsed;
            if (remainingBytes <= 0)
            {
                AppendAbort(file, day, "ByteCapExceeded", null,
                    $"Byte-Obergrenze von {options.MaxMegabytesPerRun} MB erreicht.");
                LogResumePoint(dayLines, "Byte-Obergrenze erreicht", day);
                return ExitAbortedWithResumePoint;
            }

            var counter = new ReplayDayCounter(day, emotes, botChatterDetector.IsBot);
            var sawUserId = false;
            var sawBadges = false;

            ChatLogDayResult result;
            try
            {
                result = await archiveClient.ReadDayAsync(
                    channel.TwitchChannelId,
                    day,
                    remainingBytes,
                    message =>
                    {
                        if (!string.IsNullOrEmpty(message.UserId))
                        {
                            sawUserId = true;
                            distinctChatters?.Add(message.UserId);
                        }

                        if (message.Badges.Count > 0)
                        {
                            sawBadges = true;
                        }

                        counter.Count(
                            message.SentAtUtc, message.UserId, message.Badges, message.RoomId, message.SourceRoomId, message.Text);
                        return ValueTask.CompletedTask;
                    },
                    ct);
            }
            catch (OperationCanceledException)
            {
                // docker stop / Ctrl-C. Everything up to the previous day is on disk already, so
                // this is an ordinary resume point rather than a loss.
                AppendAbort(file, day, "Cancelled", null, "Lauf abgebrochen.");
                LogResumePoint(dayLines, "abgebrochen", day);
                return ExitAbortedWithResumePoint;
            }

            switch (result.Status)
            {
                case ChatLogDayStatus.Complete:
                    // Design "Rückfall": logs without any badge and without any user id make the bot
                    // split impossible, and a report without it would answer a different question
                    // than the one #69 asks. No day line is written, so a later run re-fetches this
                    // day and reaches the same verdict instead of quietly building on it. The day was
                    // still read in full, though, so its bytes go on the event line — same as the
                    // `default:` branch below — or a resume would see the cap as untouched.
                    if (!fallbackChecked && result.MessageCount > 0)
                    {
                        fallbackChecked = true;
                        if (!sawUserId && !sawBadges)
                        {
                            AppendAbort(file, day, "NoBadgesNoUserIds", result.HttpStatusCode,
                                "Logs ohne Badges und ohne user-id.", result.BytesReceived);
                            logger.LogError(
                                "Die Logs für Kanal '{Kanal}', Tag {Tag} tragen weder Badges noch user-id; der Bot-Split ist damit nicht möglich und der Ansatz neu zu bewerten.",
                                channel.ChannelName, Iso(day));
                            return ExitUndecidable;
                        }
                    }

                    AppendDay(file, dayLines, counter.Finish(
                        ReplayDayStatuses.Complete,
                        result.BytesReceived,
                        result.BodySha256Hex,
                        result.NonPrivmsgLines,
                        result.MalformedLines));
                    bytesUsed += result.BytesReceived;
                    break;

                case ChatLogDayStatus.NoLogDay:
                    AppendDay(file, dayLines, counter.Finish(
                        ReplayDayStatuses.NoLog, result.BytesReceived, null, result.NonPrivmsgLines, result.MalformedLines));
                    bytesUsed += result.BytesReceived;
                    break;

                default:
                    AppendAbort(file, day, result.Status.ToString(), result.HttpStatusCode,
                        $"Log-Archiv-Abruf endete mit {result.Status}.", result.BytesReceived);
                    LogResumePoint(dayLines, result.Status.ToString(), day);
                    return ExitAbortedWithResumePoint;
            }
        }

        var allDays = dayLines.Values.OrderBy(d => d.Day).ToList();

        // Log coverage is checked here rather than before the first request, because the archive
        // offers no HEAD and a 404 is its normal answer for a quiet day (T8). The file stays: it is
        // the evidence that the requests were made.
        if (allDays.Count > 0 && allDays.TrueForAll(d => d.Status == ReplayDayStatuses.NoLog))
        {
            logger.LogError(
                "Für keinen der {Tage} Tage von {Von} bis {Bis} hat das Archiv ein Log; für Kanal '{Kanal}' ist der Vergleich nicht durchführbar.",
                allDays.Count, Iso(from), Iso(to), channel.ChannelName);
            return ExitPreconditionViolated;
        }

        // The 429 count lives in the event lines, never in the day lines: a throttled day gets no day
        // line at all, because a day line means "finished" and would make the resume skip the day
        // forever. That is why both this number and the resume point are handed to the calculator
        // instead of derived from the day lines — derived, they would read 0 and "window end" in
        // every report ever written.
        var rateLimitedDays = existing.Events.Count(e => e.HttpStatusCode == 429);
        var resumePoint = allDays.Count == 0 ? (DateOnly?)null : allDays[^1].Day;

        var report = ReplayFidelityCalculator.Compute(
            window, emotes, liveRows, allDays, days, runComplete: true, rateLimitedDays, resumePoint);

        file.WriteFinalReportAtomically(report, BuildMarkdown(
            identity, report, allDays, liveRows, loadedAtUtc, timeProvider.GetUtcNow().UtcDateTime,
            stopwatch.Elapsed, bytesUsed, rateLimitedDays, distinctChatters?.Count));

        logger.LogInformation(
            "Harness-Lauf für Kanal '{Kanal}' abgeschlossen: {Tage} Tage, {Bytes} Bytes, Bericht in '{Datei}'.",
            channel.ChannelName, allDays.Count, bytesUsed, file.ReportMarkdownPath);
        return ExitSuccess;
    }

    private void AppendAbort(
        HarnessReportFile file, DateOnly day, string status, int? httpStatusCode, string message, long bytes = 0) =>
        file.AppendEvent(new HarnessEventLine(timeProvider.GetUtcNow().UtcDateTime, day, status, httpStatusCode, message, bytes));

    private void LogResumePoint(Dictionary<DateOnly, ReplayDayLine> dayLines, string reason, DateOnly day)
    {
        var resumePoint = dayLines.Count == 0 ? (DateOnly?)null : dayLines.Keys.Max();
        logger.LogError(
            "Harness-Lauf bei Tag {Tag} beendet ({Grund}). Letzter vollständiger Tag: {Wiederaufnahme}. Ein erneuter Aufruf mit denselben Argumenten setzt dort fort.",
            Iso(day), reason, resumePoint is { } value ? Iso(value) : "keiner");
    }

    private static DateOnly Later(DateOnly left, DateOnly right) => left > right ? left : right;

    // ISO 8601 rather than DateOnly's culture-dependent default ToString(): a container's invariant
    // culture renders that as MM/dd/yyyy, which read as an ordinary (if odd) US date in a German
    // log line until Task 8's live verification (#69) actually compared it against the day the
    // archive itself had answered for.
    private static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void AppendDay(HarnessReportFile file, Dictionary<DateOnly, ReplayDayLine> dayLines, ReplayDayLine line)
    {
        file.AppendDay(line);
        dayLines[line.Day] = line;
    }

    /// <summary>
    /// The readable half of the report. The pre-registered figures come first and unaltered — the
    /// thresholds live in issue #69, this table only puts the numbers next to them so a reader
    /// needs no interpretation step (and this class computes no verdict of its own).
    /// </summary>
    private static string BuildMarkdown(
        HarnessRunIdentity identity,
        ReplayFinalReport report,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        DateTime loadedAtUtc,
        DateTime generatedAtUtc,
        TimeSpan elapsed,
        long bytes,
        int rateLimitedDays,
        int? distinctChatters)
    {
        var gate = report.Gate;
        var diagnostics = report.Diagnostics;
        var text = new StringBuilder();

        text.Append(Invariant($"# Harness-Bericht: {identity.ChannelName}\n\n"));
        text.Append(
            "**Replay-Treue, nicht Zuordnungsgenauigkeit.** Beide Seiten benutzen dieselbe Namensregel; "
            + "Übereinstimmung belegt, dass ein Import die eigene Live-Messung reproduzieren würde — "
            + "nicht, dass ein Name dem richtigen historischen Emote gehört. Die Zuordnungsfrage "
            + "beantworten allein die Diagnostikzahlen weiter unten.\n\n");
        text.Append(
            "> Live-Lücken: unabhängiger Beleg sind Worker-Log und Uptime Kuma, nicht dieser Bericht.\n\n");

        text.Append("## Lauf\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "Kanal", Invariant($"{identity.ChannelName} (`{identity.ChannelId}`, Twitch-ID `{identity.TwitchChannelId}`)"));
        Row(text, "Fenster", Invariant($"{identity.WindowFrom:yyyy-MM-dd} bis {identity.WindowTo:yyyy-MM-dd} ({report.Run.WindowDays} Tage)"));
        Row(text, "Bot-Split-Stichtag", identity.BotSplitCutover?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "keiner (kein Bot je gesehen)");
        Row(text, "Human-only-Tage im Fenster", Invariant($"{diagnostics.HumanOnlyDays}"));
        Row(text, "Tage mit Log / ohne Log", Invariant($"{diagnostics.LogDays} / {diagnostics.NoLogDays}"));
        Row(text, "Bot-IDs", identity.BotAccountIds.Count == 0 ? "keine" : string.Join(", ", identity.BotAccountIds.Select(id => "`" + id + "`")));
        Row(text, "Ladezeitpunkt der Vergleichsdaten (UTC)", Invariant($"{loadedAtUtc:yyyy-MM-dd HH:mm:ss}"));
        Row(text, "Bericht erzeugt (UTC)", Invariant($"{generatedAtUtc:yyyy-MM-dd HH:mm:ss}"));
        Row(text, "Laufzeit dieses Laufs", Invariant($"{elapsed:hh\\:mm\\:ss}"));
        Row(text, "Übertragene Bytes (alle Läufe dieser Datei)", Invariant($"{bytes}"));
        Row(text, "HTTP 429", Invariant($"{rateLimitedDays}"));
        Row(text, "Distinkte Chatter im Fenster", distinctChatters is { } count
            ? Invariant($"{count}")
            : "nicht verfügbar (wiederaufgenommen)");
        Row(text, "Algorithmus-Version", "`" + identity.AlgorithmVersion + "`");
        Row(text, "Input-Hash", "`" + identity.InputHash + "`");
        Row(text, "Lauf vollständig", report.Run.RunComplete ? "ja" : "nein");
        Row(text, "Gate-tauglich", gate.GateEligible
            ? "ja"
            : "nein — " + (gate.GateIneligibleReasons.Count == 0 ? "ohne Grund" : string.Join(", ", gate.GateIneligibleReasons)));

        text.Append("\n## Präregistrierte Kennzahlen (#69)\n\n| Kennzahl | Wert | Präregistrierte Schwelle |\n| --- | --- | --- |\n");
        Row(text, "Gesamtabweichung Σ\\|Log − Live\\| / ΣLive", Ratio(gate.TotalDeviation), "≤ 0.10");
        Row(text, "Top-20-Recall", Ratio(gate.Top20Recall), "≥ 0.90");
        Row(text, "Unteres-Quartil-Precision", Ratio(gate.BottomQuartilePrecision), "≥ 0.80");
        Row(text, "Gewertete Tage",
            Invariant($"{gate.RatedDays} (davon {diagnostics.SignallessRatedDays} signallos)"), "≥ 20");
        Row(text, "Fensterlänge", Invariant($"{report.Run.WindowDays}"), "= 30");

        text.Append(Invariant($"\nImport-Population: {gate.PopulationSize} Emotes · ΣLog (human) {gate.HumanLogTotal} · ΣLive (human) {gate.HumanLiveTotal}"));
        text.Append(Invariant($" · Top-20-Größe {gate.Top20Size} · Quartilsgröße {gate.BottomQuartileSize}"));
        text.Append(Invariant(
            $", davon {gate.BottomQuartileLiveTieCount} auf dem Live-Grenzwert und {gate.BottomQuartileLogTieCount} auf dem Log-Grenzwert"));
        text.Append(Invariant($" · davon signallose gewertete Tage {diagnostics.SignallessRatedDays}\n"));

        text.Append("\n## Plausibilität (mit Bots, alle Tage mit Log)\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "ΣLog inkl. Bots", Invariant($"{report.Plausibility.LogTotalWithBots}"));
        Row(text, "ΣLive inkl. Bots", Invariant($"{report.Plausibility.LiveTotalWithBots}"));
        Row(text, "Verhältnis", Ratio(report.Plausibility.Ratio));
        Row(text, "Differenz", Invariant($"{report.Plausibility.Difference}"));

        text.Append("\n## Diagnostik (nicht bindend)\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "Stabile Teilmenge: Größe / qualifiziert / entscheidungsfähig", Invariant(
            $"{diagnostics.StableSubsetSize} / {diagnostics.QualifiedEmoteCount} (N={diagnostics.MinLiveUsesN}, M={diagnostics.RequiredQualifiedM}) / {(diagnostics.StableSubsetDecisive ? "ja" : "nein")}"));
        Row(text, "Stabile Teilmenge: Median / p90 / Spearman", Invariant(
            $"{Ratio(diagnostics.StableSubsetMedianDeviation)} / {Ratio(diagnostics.StableSubsetP90Deviation)} / {Ratio(diagnostics.StableSubsetSpearman)}"));
        Row(text, "Alle Emotes: Median / p90 / Spearman", Invariant(
            $"{Ratio(diagnostics.AllEmotesMedianDeviation)} / {Ratio(diagnostics.AllEmotesP90Deviation)} / {Ratio(diagnostics.AllEmotesSpearman)}"));
        Row(text, "Nur im Log / nur live gesehen", Invariant(
            $"{diagnostics.LogOnlyEmoteCount} ({Ratio(diagnostics.LogOnlyShareOfLogTotal)} von ΣLog) / {diagnostics.LiveOnlyEmoteCount} ({Ratio(diagnostics.LiveOnlyShareOfLiveTotal)} von ΣLive)"));
        Row(text, "Gesamtabweichung inkl. markierter Tage", Invariant(
            $"{Ratio(diagnostics.TotalDeviationIncludingFlaggedDays)} über {diagnostics.FlaggedIncludedDays} Tage"));
        Row(text, "Tagesdiff / mit ±1-Tag-Toleranz", Invariant(
            $"{Ratio(diagnostics.DailyDeviation)} / {Ratio(diagnostics.DailyDeviationWithOneDayTolerance)}"));
        Row(text, "Nicht zuordenbar: unbekannt / mehrdeutig / vor FirstSeenAt / nach ArchivedAt", Invariant(
            $"{diagnostics.UnknownNameHits} / {diagnostics.AmbiguousNameHits} / {diagnostics.BeforeFirstSeenHits} / {diagnostics.AfterArchivedHits}"));
        Row(text, "FirstSeenAt unbekannt (gezählt und markiert) / mehrdeutige Namen / archiviert ohne Datum", Invariant(
            $"{diagnostics.FirstSeenUnknownHits} / {diagnostics.AmbiguousNameCount} / {diagnostics.ArchivedWithoutDateCount}"));
        Row(text, "Nachrichten gesamt / Bots / Shared Chat / außerhalb des Tages", Invariant(
            $"{diagnostics.TotalMessages} / {diagnostics.BotMessages} / {diagnostics.SharedChatMessages} / {diagnostics.OutsideDayCount}"));
        Row(text, "Nicht-PRIVMSG-Zeilen / unlesbare Zeilen", Invariant(
            $"{diagnostics.NonPrivmsgLines} / {diagnostics.MalformedLines}"));
        Row(text, "Datenschutz: Anteil (Emote, Tag)-Zellen mit k = 1", Invariant(
            $"{Ratio(diagnostics.SingleChatterCellShare)} von {diagnostics.CellCount} Zellen"));
        Row(text, "k-Verteilung (1 … 10+)", string.Join(", ", diagnostics.KHistogram.Skip(1).Select(v => v.ToString(CultureInfo.InvariantCulture))));
        Row(text, "Median des Tagesverhältnisses", Ratio(diagnostics.DayRatioMedian));
        Row(text, "Tage mit vermuteter Live-Lücke", diagnostics.LiveGapDays.Count == 0
            ? "keine"
            : string.Join(", ", diagnostics.LiveGapDays.Select(d => d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        Row(text, "Tage mit fraglicher Abdeckung", diagnostics.CoverageQuestionableDays.Count == 0
            ? "keine"
            : string.Join(", ", diagnostics.CoverageQuestionableDays.Select(d => d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

        var liveByDay = new Dictionary<DateOnly, long>();
        foreach (var row in liveRows)
        {
            liveByDay[row.Date] = liveByDay.GetValueOrDefault(row.Date) + row.UseCount;
        }

        text.Append("\n## Tage\n\n| Tag | Status | Bytes | Nachrichten | davon Bots | Log-Treffer (human) | Live (human) |\n| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var day in days)
        {
            var logHits = day.HumanCounts.Values.Sum();
            text.Append(Invariant(
                $"| {day.Day:yyyy-MM-dd} | {day.Status} | {day.Bytes} | {day.MessageCount} | {day.BotMessageCount} | {logHits} | {liveByDay.GetValueOrDefault(day.Day)} |\n"));
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, string label, string value) =>
        text.Append(Invariant($"| {label} | {value} |\n"));

    private static void Row(StringBuilder text, string label, string value, string threshold) =>
        text.Append(Invariant($"| {label} | {value} | {threshold} |\n"));

    /// <summary>
    /// Four decimals, invariant, and an explicit dash for "not computable" — a missing denominator
    /// is a statement of its own and must never render as 0.
    /// </summary>
    private static string Ratio(double? value) =>
        value is { } number ? number.ToString("0.0000", CultureInfo.InvariantCulture) : "—";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
