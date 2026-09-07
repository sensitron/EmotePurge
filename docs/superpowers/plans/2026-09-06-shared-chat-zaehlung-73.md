# Shared Chat in der Nutzungszählung, Zug 1 — Umsetzungsplan (Issue #73)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code**
> (globale Regel, s. `~/.claude/CLAUDE.md`): jeder Task beschreibt Absicht, Verträge, Grenzfälle
> und Prüfbedingungen — Methodenrümpfe, Migrations-Bodies, SQL und Testmethoden entstehen im Task
> selbst. Jeder Task läuft als eigener Subagent mit frischem Kontext und sieht nur seinen Task;
> deshalb trägt jeder Task einen `Interfaces`-Block mit den exakten Namen, die er konsumiert und
> die spätere Tasks von ihm erwarten.

**Goal:** Chat-Nachrichten aus fremden Räumen einer Twitch-Stream-Together-Session („Shared
Chat") werden ab dem Prod-Deploy in `UsageStats` in einer eigenen Spalte `SharedChatUseCount`
verbucht statt in `UseCount`; der Harness (#69) zählt auf der Replay-Seite nach derselben Regel
und vergleicht ab einem explizit konfigurierten Stichtag; die Oberfläche zeigt **unverändert** die
gewohnte Summe (D5), bis Zug 2 den Lesepfad umdreht. Mit dem Prod-Deploy D beginnt die
30-Tage-Uhr des bindenden Laufs (erster sauberer Fenstertag D+1, frühester Lauf D+31) — jeder Tag
Verzögerung verschiebt den Oktober-Lauf um denselben Tag.

**Architecture:** Fünf Bausteine entlang des bestehenden Datenflusses, alle aus der Spec. **(B1)**
Eine pure Raum-Regel mit drei Ausgängen (eigen / fremd / unbestimmbar) in `EmotePurge.Core`,
gerufen von Live-Pfad und Harness — herausgezogen aus `ReplayDayCounter.Count`, nicht neu
geschrieben; die Live-Extraktion aus `ChatMessage.RoomId` + `UndocumentedTags` ist **null-sicher**.
**(B2)** Ein dreiwertiges Zählkategorie-Enum statt zweier Bools, `EmoteUsageCounts` mit drittem
Feld, `Increment` nimmt die Kategorie; **keine Defaultwerte** in den betroffenen positionellen
Records — der Compiler findet jede Aufrufstelle. **(B3)** `UsageStat.SharedChatUseCount`
(`NOT NULL DEFAULT 0` auf der Spalte), drittes `UNNEST`-Array, dritte `ON CONFLICT`-Addition.
**(B4/D5)** Die produktiven Lesequeries liefern und filtern übergangsweise über
`UseCount + SharedChatUseCount`; `GetRowsAsync` bleibt roh; ein Test nagelt die Übergangssumme
fest. **(B5/D3/D4)** Harness: dreiwertige Verzweigung auf Nachrichtenebene, drittes
Zähl-Dictionary, `AlgorithmVersion` → `harness-2`, expliziter fail-closed `SharedChatCutover`,
`HumanOnly` allein daraus, Shared-Chat-Summen beider Seiten berichtet, Symmetrie als
Eignungsbedingung, Diagnosemodus per `--diagnostic`.

**Tech Stack:** .NET 10 (Worker Service mit TwitchLib.Client 4.0.1, Minimal API, EF Core/Npgsql,
xUnit + NSubstitute + Testcontainers). Kein Frontend-Anteil in Zug 1.

**Spec:** [`docs/superpowers/specs/2026-09-06-shared-chat-zaehlung-73-design.md`](../specs/2026-09-06-shared-chat-zaehlung-73-design.md)
(Commit `020b5bf`) — **verbindlich**. D1–D5 sind entschieden und werden hier nicht neu abgewogen.
Wo die Spec dem Plan eine Entscheidung überlässt („Offene Fragen" 1–6, Vehikel des Live-Zählers),
steht sie unter „Entscheidungen dieses Plans" und am jeweiligen Task. Echte Widersprüche zwischen
Spec und Code stehen unter „Anmerkungen zur Spec" am Ende, nicht als stille Umentscheidung.

## Ist-Zustand, am Code verifiziert (2026-09-06, Branch `feat/shared-chat-zaehlung-73` auf `020b5bf`)

Die Spec-Tabelle stimmt; hier die Namen, die der Implementer tatsächlich vorfindet:

| Ort | Befund |
|---|---|
| `src/EmotePurge.Worker/TwitchChatManager.cs:489-530`, `OnMessageReceived` | Reihenfolge: `Interlocked.Exchange` auf `_lastMessageReceivedUtcTicks` → `_lastMessageByChannelTicks[e.ChatMessage.Channel]` → Debug-Log → `emoteMatchCache.GetChannelEmotes` mit frühem Return bei leerem Set → **eine** `botChatterDetector.IsBot(e.ChatMessage.UserId, e.ChatMessage.Badges)` → `HashSet<string> matchedThisMessage` → `EmoteNameMatching.MatchEmoteIds(...)` → je Treffer `usageCounter.Increment(emoteId, isBot)`. Kommentar „Do not reorder" zur Watchdog-Buchführung steht. Primärkonstruktor: `ILogger<TwitchChatManager>`, `ILoggerFactory`, `IEmoteMatchCache`, `IEmoteUsageCounter`, `IBotChatterDetector` — **kein** `WorkerStats`. `RoomId`/`UndocumentedTags` kommen in der Datei nicht vor. |
| TwitchLib.Client 4.0.1 (`EmotePurge.Worker.csproj:12`, dazu `TwitchLib.Communication` 2.0.1 `:15`) | Per Reflection am installierten Paket geprüft (Probe im Scratchpad, nicht im Repo): `TwitchLib.Client.Parsing.IrcParser.ParseMessage(string)` ist **public static** und liefert `TwitchLib.Client.Models.Internal.IrcMessage`; `ChatMessage` hat den **public** Konstruktor `(string botUsername, IrcMessage ircMessage, MessageEmoteCollection emoteCollection, bool replaceEmotes, string prefix, string suffix)`; Properties `RoomId` (`string`), `UserId`, `Badges` (`List<KeyValuePair<string,string>>`), `UndocumentedTags` (`Dictionary<string,string>`, laut Spec `null` ohne unbekannte Tags). Damit sind Fixtures aus rohen IRC-Zeilen ohne eigenen Parser baubar. `tests/EmotePurge.Worker.Tests` referenziert das Worker-Projekt und sieht TwitchLib transitiv (heute nutzt nur `BotChatterDetectorTests` einen TwitchLib-Typ). |
| `src/EmotePurge.Worker/EmoteUsageCounter.cs`, `IEmoteUsageCounter.cs` | `Increment(string emoteId, bool isBot)` per `AddOrUpdate` mit `TArg`-Overload und zwei statischen Lambdas; `Merge(IReadOnlyDictionary<string, EmoteUsageCounts>)` addiert komponentenweise; `DrainAndReset()` per `Interlocked.Exchange`; `PendingEmoteCount` per `Volatile.Read(...).Count`. |
| `src/EmotePurge.Core/Services/IUsageStatFlushService.cs:15` | `public readonly record struct EmoteUsageCounts(int Human, int Bot);` mit XML-Doc; `FlushAsync(IReadOnlyDictionary<string, EmoteUsageCounts>, ct)` → `IReadOnlyCollection<string>` (Channel-Namen). |
| `src/EmotePurge.Core/Entities/UsageStat.cs` | `Id`, `EmoteId`, `Date`, `UseCount`, `BotUseCount` mit Kommentar zur Bot-only-Zeile („the read queries in UsageStatQueryService filter on UseCount > 0"). |
| `src/EmotePurge.Infrastructure/Services/UsageStatFlushService.cs:48-79` | Arrays `useCounts`/`botUseCounts` aus `validIds`-Reihenfolge; `const string sql` mit `UNNEST(@emoteIds, @useCounts, @botUseCounts)`, `ON CONFLICT ("EmoteId","Date") DO UPDATE SET` je Spalte; vier `NpgsqlParameter`. Atomaritäts-Kommentar :51-60 („Holds for both columns"). |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs:40-44` | `HasIndex(u => new { u.EmoteId, u.Date }).IsUnique().IncludeProperties(u => u.UseCount)` mit Index-Only-Scan-Kommentar. |
| `src/EmotePurge.Infrastructure/Migrations/` | jüngste `20260901193259_AddUsageStatBotUseCount.cs` — `AddColumn<int>(nullable: false, defaultValue: 0)` mit dem Katalog-only-/Redeploy-Kommentar, das Formvorbild. |
| `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` | `GetUsageContextAsync` (:63-73, drei Aggregate, `LastUsedDate = g.Max(u => u.UseCount > 0 ? …)` :71), `GetDailySeriesAsync` (:123-127 `days` mit `u.UseCount > 0` :124; `bounds` :132-140 mit `u.UseCount > 0` :133), `GetChannelSeriesAsync` (:211-215 mit `u.UseCount > 0` :212), `GetTotalsByEmoteIdsAsync` (:244-248, reine Summe), `GetEarliestBotUsageDateAsync` (:251-270, `BotUseCount > 0`), `GetEmoteLifetimesAsync`, `GetRowsAsync` (:292-318, roh, `UsageStatRowDto(u.EmoteId, u.Date, u.UseCount, u.BotUseCount)` :316). `GetUsageStatsAsync` (:10-19) ist die Debug-Rohliste. Alle Kommentare berufen sich auf den Index-Only-Scan über die Include-Spalte. |
| `src/EmotePurge.Core/Services/IUsageStatQueryService.cs:138` | `public record UsageStatRowDto(string EmoteId, DateOnly Date, int UseCount, int BotUseCount);` — einzige Konstruktionsstelle im Produktivcode ist `GetRowsAsync`; Tests: `HarnessInputHashTests.Row(...)`, `HarnessRunnerTests.Rows()`. |
| `src/EmotePurge.Core/ChatLogArchive/ChatLogArchiveModels.cs:11-17` | `record ChatLogMessage(DateTime SentAtUtc, string? UserId, IReadOnlyList<KeyValuePair<string,string>> Badges, string? RoomId, string? SourceRoomId, string Text)`. Konstruktionsstellen: `JustlogRawLineParser.TryParse` (Produktivcode, einzige) und `HarnessRunnerTests` (:114, :139, :515 sowie der `Message(...)`-Helfer). |
| `src/EmotePurge.Infrastructure/ChatLogArchive/JustlogRawLineParser.cs:115-126` | liest `room-id` und `source-room-id` aus dem Tag-Dictionary `tags`, normalisiert leer → `null`; kennt keinen weiteren `source-*`-Marker. Tests in `tests/EmotePurge.Infrastructure.Tests/Unit/JustlogRawLineParserTests.cs` (u. a. `TryParse_SharedChatWithDifferentSourceRoomId_SetsBothRoomIds` :106 mit einer synthetischen Zeile `room-id=200000001;source-room-id=200000009`). |
| `src/EmotePurge.Worker/Harness/ReplayDayCounter.cs` | Felder `_humanCounts`, `_botCounts`, `_cells`, `_humanChatters`, `_messageCount`, `_botMessageCount`, `_sharedChatMessageCount`, `_outsideDayCount`, `_firstSeenUnknownHits`. `Count(DateTime sentAtUtc, string? userId, IReadOnlyList<KeyValuePair<string,string>> badges, string? roomId, string? sourceRoomId, string text)` ist `void`; Shared-Chat-Marker :116-119 rein diagnostisch; `_botMessageCount++` / `_humanChatters.Add` :128-135 **pro Nachricht**; Verzweigung :137 `isBot ? _botCounts : _humanCounts`; Zellen :147-155 nur für Nicht-Bots; `ClassifyTokens(text)` :158 für jede Nachricht. `Finish(...)` :166-203 baut `ReplayDayLine` positional. Docstring :98-102 sagt „A shared-chat message also still counts". |
| `src/EmotePurge.Worker/Harness/ReplayModels.cs` | `ReplayUsageRow(EmoteId, Date, UseCount, BotUseCount)` :28; `ReplayWindow(From, To, DateOnly? BotSplitCutover)` :36 mit Docstring über den Bot-Stichtag; `ReplayGateIneligibleReasons` :84-97 (`run-incomplete`, `window-not-30-days`, `rated-days-below-20`, `live-total-zero`); `ReplayDayLine` :109-126 (17 Felder, `SharedChatMessageCount` an Position 7, `HumanCounts`/`BotCounts` an 11/12, `DistinctChatters` zuletzt); `ReplayDayRatio(Day, LogTotal, LiveTotal, Ratio)` :129; `ReplayGateMetrics` :154-167 (`HumanLogTotal`/`HumanLiveTotal` an 3/4); `ReplayPlausibility` :173; `ReplayDiagnostics` :207-253 (46 Felder, `SharedChatMessages` an Position 32); `ReplayRunInfo(WindowFrom, WindowTo, BotSplitCutover, WindowDays, DayLineCount, TotalBytes, RateLimitedDays, ResumePoint, RunComplete)` :272-281; `ReplayFinalReport` :288; `ValueList<T>` :300. |
| `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs` | Konstanten :18-25 (`RequiredWindowDays=30`, `RequiredRatedDays=20`, `TopSize=20`, `RequiredQualifiedEmotes=30`, `MinLiveUsesPerThirtyDays=20`, `MinLiveUsesFloor=5`, `RoundingDigits=4`, `MinHistogramLength=11`) — **die 10 % existieren nur im Klassen-Docstring :10-13 und im Issue, nicht als Konstante** (s. „Anmerkungen zur Spec"). `Compute(window, emotes, liveRows, days, windowDays, runComplete, totalBytes, rateLimitedDays, resumePoint)` :52-94. `BuildDayFacts` :107-152 (`liveByDay` = `UseCount + BotUseCount` :115, `logTotal` = `Sum(HumanCounts) + Sum(BotCounts)` :121, `HumanOnly = window.BotSplitCutover is { } cutover && line.Day >= cutover` :127, `Rated` :148). `BuildPopulation` :160-196 human-only (Docstring „what the grid actually shows" :155-158). `BuildGate` :198-262 baut `reasons`. `BuildPlausibility` :283-307. `BuildDiagnostics` :309-400 (`SharedChatMessages` = `days.Sum(d => (long)d.SharedChatMessageCount)` :385; `HumanOnlyDays` :393). `DayFact` ist eine private Klasse mit `HumanOnly`, `Rated` etc. |
| `src/EmotePurge.Worker/Harness/HarnessRunner.cs` | `AlgorithmVersion = "harness-1"` :60 mit Bump-Docstring :56-59; Exit-Codes :63-89; `RunAsync(string channelName, int days, CancellationToken ct)` :110; `ExecuteAsync` :133 mit der Reihenfolge Tage-Check :140 → Kanal :151 → Twitch-ID :161 → Fenster :173-192 → Queries :194-196 (`GetEarliestBotUsageDateAsync` :196) → `HarnessRunIdentity` :200-209 → Datei/Resume :211-238 → `ReplayUsageRow`-Mapping :244-246 → `ReplayWindow` :247 → `distinctChatters` :261 → Tagesschleife mit Callback :291-307 (`distinctChatters?.Add(message.UserId)` :296 **vor** `counter.Count(...)` :304-305) → `Compute(...)` :390-391 → `BuildMarkdown` :393-395. `FindFrozenWindow` :422-492 vergleicht `AlgorithmVersion` :446. `BuildMarkdown` :525-639: Zeile „Bot-Split-Stichtag" :553, „Human-only-Tage im Fenster" :554, „Distinkte Chatter im Fenster" :562, „Gate-tauglich" :568-570, Diagnostik-Zeile „Nachrichten gesamt / Bots / Shared Chat / außerhalb des Tages" :609-610, `liveByDay` :624-628 summiert nur `UseCount`, Tagestabelle :630-636. |
| `src/EmotePurge.Worker/Harness/HarnessReportFile.cs` | `HarnessRunIdentity(ChannelId, TwitchChannelId, ChannelName, WindowFrom, WindowTo, DateOnly? BotSplitCutover, IReadOnlyList<string> BotAccountIds, AlgorithmVersion, InputHash)` :19-28; `SerializeIdentity` camelCase-JSON, byte-genau in `ReadHeader` :172-209 verglichen (`HarnessReportIdentityMismatchException` → Exit 3 in `ExecuteAsync` :221-229); `BuildFileName` :136-146 hasht die Identität in den Dateinamen; `HarnessJsonLine(Kind, Header, Day, Event)` mit `LineOptions` (camelCase, `WhenWritingNull`). |
| `src/EmotePurge.Worker/Harness/HarnessInputHash.cs:225-233` | Zeilen-Signatur `r\|<EmoteId>\|<Date:o>\|<UseCount>\|<BotUseCount>`; Test `HarnessInputHashTests.AChangedBotUseCount_ChangesTheHash` :85 und `Row(...)`-Helfer :117. |
| `src/EmotePurge.Worker/Harness/HarnessOptions.cs` | POCO, `OutputDirectory`, `MaxMegabytesPerRun`, `WindowDays`; gebunden in `WorkerServiceRegistration.AddHarness` :74-76 per `configuration.GetSection("Harness").Bind(...)` — **vor** `host.Build()`, also außerhalb jeder Exit-Code-Behandlung von `RunAsync`. Tests `WorkerServiceRegistrationTests.HarnessOptions_*` :66-95. |
| `src/EmotePurge.Worker/Harness/HarnessCommandLine.cs` | `HarnessCommandLineResult.RunHarness(string ChannelName, int? Days)` :65; Grammatik exakt `harness <kanal> [--days <n>]` :104-155, `Usage`-Konstante :102, `DaysOption = "--days"` :91. `Program.cs:18-19, 45-101` reicht `request.ChannelName` und `request.Days ?? options.WindowDays` an `runner.RunAsync`. Tests `HarnessCommandLineTests` (`EverythingElse_IsInvalidWithAGermanMessage` als Theory :64). |
| `src/EmotePurge.Worker/WorkerStats.cs` | Singleton (`WorkerServiceRegistration.cs:36`), ein `Lock`, drei Flush-Felder, `RecordFlushSuccess(int rows, DateTime nowUtc)`, `RecordFlushFailure()`; konsumiert von `UsageFlushWorker` und `WorkerHealthPublisher`. |
| `src/EmotePurge.Worker/UsageFlushWorker.cs:41-104` | `FlushOnceAsync`: `DrainAndReset` → `FlushAsync` → `stats.RecordFlushSuccess(counts.Count, …)`; Requeue-Kommentar :83-89 nennt „both UseCount and BotUseCount". |
| Konfiguration | `docker-compose.yml:122-168` und `docker-compose.prod.yml:121-…` haben den `harness`-Dienst mit `Harness__OutputDirectory`, `Harness__MaxMegabytesPerRun=${HARNESS_MAX_MEGABYTES_PER_RUN:-200}`; `.env.example:24-37` dokumentiert `HARNESS_MAX_MEGABYTES_PER_RUN` und den Aufruf `docker compose --profile harness run --build --rm harness <kanal>`. `.github/dependabot.yml:39-41` hat eine `ignore`-Liste (nur `Microsoft.OpenApi`, major). |
| Tests (Bestand) | `tests/EmotePurge.Worker.Tests/` flach: `EmoteUsageCounterTests` (8 Fälle, `bool`-Signatur), `ReplayDayCounterTests` (Helfer `Emote`, `Counter(emotes, isBot)`, `Say(counter, text, userId, at, roomId, sourceRoomId)`, `Finish`, `Reason`, `Strings`; `SharedChatMessage_IsCountedAndMarked` :125-136 erwartet heute `HumanCounts["e1"] == 2`), `ReplayFidelityCalculatorTests` (Helfer `BaseSet`, `Build`, `Compute(emotes, rows, days, cutover: …)`; `DaysBeforeTheBotSplitCutover_CountForPlausibilityButNotForTheGate` :113, `MissingBotSplitCutover_LeavesNoRatedDay` :166, `Plausibility_ComparesBothSidesIncludingBots` :304, `Diagnostics_AggregateThePerDayCounters` :339), `HarnessRunnerTests` (Substitutes für `IChannelService`, `IUsageStatQueryService`, `IChatLogArchiveClient`, `IBotChatterDetector`; `HarnessOptions` per Objektinitialisierer :680; `ACompleteRun_WritesBothReportsAndTheGateFieldsOfTheCalculator` :350 prüft `"sharedChatMessages": 1`, `"ratedDays": 3`, `"humanLiveTotal": 3`), `HarnessReportFileTests` (`ADayLine_KeepsItsCountsAcrossTheRoundTrip` :103, `Identity()`/`Day()`-Helfer), `HarnessInputHashTests`, `HarnessCommandLineTests`, `WorkerServiceRegistrationTests`. `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatFlushServiceTests` (11 Fälle, u. a. `FlushAsync_PairsAllThreeArraysCorrectly_AcrossMultipleEmotes`, `FlushAsync_WritesBotOnlyRow_WithZeroHumanCount`), `UsageStatQueryServiceTests` (Bot-only-Fälle :126-163, :302-345, :507-543; `GetRowsAsync_IncludesBotOnlyRows` :768; `GetUsageContextAsync_TranslatesServerSide_ForAFullSizedEmoteSet` :182). Core-Typen werden in `tests/EmotePurge.Infrastructure.Tests/Unit/` getestet (Präzedenz `EmoteNameMatchingTests`, `CoreAssemblyReferenceTests`). |
| `docs/Architectur.md` | `:80` „getrennt nach Mensch und Bot (`UseCount`/`BotUseCount`, s. DECISIONS 2026-09-01)"; `:232` „`BotUseCount` … steht bewusst **nicht** im Include, weil ihn heute keine Aggregat-Query liest"; `:270-280` Modell-Listing von `UsageStat` (führt `UseCount`, nicht einmal `BotUseCount`). |
| `docs/DECISIONS.md` | Kopf mit Sortierregel (neuester Eintrag zuoberst, direkt nach `---` :11); Form `### <Datum> — <Titel>` + `**Betrifft:**`-Zeile + fett gesetzte Absatz-Anker; jüngster Eintrag „2026-09-06 — Der Harness ist ein zweiter Einstiegspunkt…" :13. |

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1:** vor jedem `git commit` erst den Nutzer fragen — auch unter freigegebenem Plan. Die
  Commit-Zeilen unten sind Vorschläge für die Rückfrage, keine Automatismen.
- **Regel 2 / Commit-Zuschnitt:** sieben Code-Commits (Tabelle unten). Tasks 2 und 3 bilden
  **einen** Commit, weil sie eine Kompiliereinheit sind: `EmoteUsageCounts` bekommt in Task 2 sein
  drittes Feld, und `EmoteUsageCounter` (Worker) konstruiert den Typ positional — ohne Task 3 baut
  der Worker nicht. Der Task-2-Subagent meldet deshalb „Infrastructure-Projekt baut,
  Infrastructure-Tests grün", nicht „Solution grün".
- **Regel 3:** Der Commit mit der Schemaänderung (Tasks 2+3) enthält den `docs/DECISIONS.md`-Eintrag
  **vollständig** und die `docs/Architectur.md`-Stellen `:80` und `:270`. Es gibt **einen** Eintrag
  für #73; die Commits der Tasks 4–7 ergänzen dessen `**Betrifft:**`-Zeile (Task 4 zusätzlich
  einen Satz zur Index-Entscheidung und `Architectur.md:232`), statt einen zweiten Eintrag zu
  schreiben. Task 1 committet vor dem Eintrag — dieselbe Reihenfolge wie beim #31-Plan (Detektor
  vor Schema-Commit).
- **Regel 4 / Schichtentreue:** `EmotePurge.Core` bleibt BCL-only (`CoreAssemblyReferenceTests`
  wacht) — die Raum-Regel dort ist reine String-/Dictionary-Logik. Kein `AppDbContext` außerhalb
  von Infrastructure. Der Harness bleibt read-only (nur `IChannelService`/`IUsageStatQueryService`).
- **Regel 5:** kein neues Interface — die Regel ist eine statische pure Funktion (wie
  `EmoteNameMatching`), das Enum ein Werttyp; `WorkerStats` bleibt konkreter Singleton (bestehende
  Begründung im Klassenkommentar).
- **Regel 7:** kein neuer Fehlercode, keine i18n-Änderung — kein Api-Vertrag ändert sich.
- **Regel 10:** die Summen `UseCount + SharedChatUseCount` kommen in bestehende Lambdas über der
  einen Tabelle; kein Zuschnitt ändert sich. Ob Npgsql den Ausdruck im `GroupBy`-Aggregat
  übersetzt, **prüft** der bestehende Vollgrößen-Test, nicht eine Annahme.
- **Regel 11:** pure Regel-Tests in `tests/EmotePurge.Infrastructure.Tests/Unit/` (Core-Typ,
  Präzedenz `EmoteNameMatchingTests`); Zähler-, Harness- und die **TwitchLib-gebundenen**
  Fixture-Tests im container-freien `tests/EmotePurge.Worker.Tests/` (flach); Upsert- und
  Query-Tests in `tests/EmotePurge.Infrastructure.Tests/Integration/`. **Kein** Fall in
  `tests/EmotePurge.Api.Tests`: keine Route, kein `IEndpointFilter`, keine Filter-Reihenfolge.
  `TwitchChatManager` wird bewusst **live** verifiziert (Regel 16), nicht gegen Fakes.
- **Regel 15/16:** vor jedem Compose-Test `--build`; die Negativprobe (Task 8) läuft lokal auf der
  Devbox gegen echte Twitch-Kanäle — **ohne** VPS-Zugang, und „läuft durch" ist kein Nachweis.
- **Regel 18:** vor jedem Commit `dotnet format EmotePurge.slnx`.
- **Regel 19:** C#-Memberreihenfolge `const`/`static readonly` → `readonly` → Felder → Properties →
  öffentliche → private Methoden → `private static`; verschachtelte Typen ans Ende.
- **Sprache:** Bezeichner und Kommentare englisch, Log-/`throw`-Messages deutsch, DECISIONS deutsch.
- **Keine Defaultwerte** (Spec B2) in den positionellen Records `EmoteUsageCounts`,
  `UsageStatRowDto`, `ChatLogMessage`, `ReplayUsageRow`, `ReplayDayLine`, `ReplayWindow`,
  `ReplayRunInfo`, `ReplayDiagnostics`, `ReplayGateMetrics`, `HarnessRunIdentity`,
  `HarnessCommandLineResult.RunHarness` und in den neuen Parametern von `Count`, `Compute`,
  `RunAsync`. Der Compiler ist die Suchhilfe; ein Task, dem die Solution nach seiner Änderung noch
  baut, obwohl er ein Record erweitert hat, hat einen Default eingebaut — prüfen.
- **„Fertig" heißt:** `dotnet test EmotePurge.slnx` (Docker läuft, Testcontainers) und
  `npm --prefix web test -- --watch=false` grün. **E2E entfällt in Zug 1 bewusst:** kein Frontend,
  keine i18n-Schlüssel, kein Api-Vertrag — es gibt nichts, was Playwright anders rendern würde
  (Spec B7, letzter Absatz). Das ist eine Entscheidung, keine Vergesslichkeit.
- **Befehle:**
  - ein xUnit-Test: `dotnet test tests/EmotePurge.Worker.Tests/EmotePurge.Worker.Tests.csproj --filter "FullyQualifiedName~ReplayDayCounterTests"`
  - nur Infrastructure-Tests (wenn der Worker gerade nicht baut): `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~UsageStatFlushServiceTests"`
  - Migration: `dotnet ef migrations add AddUsageStatSharedChatUseCount --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`
  - Dev-DB nachziehen: `docker compose up -d postgres redis`, dann `dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`
  - Harness lokal: `docker compose --profile harness run --build --rm harness <kanal> --days <n> --diagnostic`

## Reihenfolge und Commits

| Task | Inhalt | Commit | Modell |
|---|---|---|---|
| 1 | Raum-Regel `SharedChatRule` + `MessageOrigin` (Core), Marker-Bool in `ChatLogMessage`/Parser, pure Tests, TwitchLib-gebundene Fixtures, Dependabot-Freeze | `feat(core): classify chat messages by room origin` | sonnet |
| 2 | `EmoteUsageCounts` drittes Feld, `UsageStat.SharedChatUseCount`, Migration, Upsert, `UsageStatRowDto`/`GetRowsAsync`, Tests, DECISIONS, Architectur.md `:80`/`:270` | — (Working Tree) | sonnet |
| 3 | `UsageCategory`, Zähler nimmt die Kategorie, `TwitchChatManager` klassifiziert null-sicher, `WorkerStats`-Zähler + Flush-Log, `HarnessInputHash` dritte Spalte, Tests | `feat(usage): count shared-chat emote usage apart from own usage` (Tasks 2+3) | sonnet |
| 4 | D5-Übergangssumme im Lesepfad, Übergangstest, `EXPLAIN`-Entscheidung zum Covering-Index (ggf. zweite Migration), Architectur.md `:232` | `feat(usage): read own and shared-chat usage as one total until the display splits them` | sonnet |
| 5 | Harness-Replay: `ReplayDayCounter` dreiwertig auf Nachrichtenebene, `ReplayDayLine` + `ReplayUsageRow` wachsen, Runner-Callback, `AlgorithmVersion` → `harness-2`, Round-Trip | `feat(harness): count shared-chat hits apart on the replay side` | sonnet |
| 6 | D4: `Harness:SharedChatCutover` fail-closed, `--diagnostic`, Identität/Kopf/Dateiname, `HumanOnly` allein aus dem Stichtag, Berichtszeilen, Compose/`.env.example` | `feat(harness): gate only days after an explicit shared-chat cutover` | sonnet |
| 7 | D3: Tagessummen und Plausibilität über drei Komponenten, Shared-Chat-Summen beider Seiten (tageweise + Fenster), Symmetrie als Eignungsbedingung, Diagnostik-Felder, Markdown | `feat(harness): report shared chat on both sides and require symmetry` | opus |
| 8 | Gates, Coverage-Vorabschätzung, Gegenprobe altes Image, Negativprobe live, Symmetrieprobe, Prod-Übergabe | kein Code-Commit | opus |
| 9 | Codex-Sol-Zweitmeinung, ggf. Fable als Schiedsrichter | — | (Codex) |

**Abhängigkeiten:** 2 → 3 (eine Kompiliereinheit) → 4 (braucht die Spalte und den Upsert für
die Nullzeile) → 5 (braucht `UsageStatRowDto` mit dritter Spalte aus Task 2 und `UsageCategory`
aus Task 3) → 6 (ändert `ReplayWindow`/Identität, die Task 5 noch unangetastet lässt; ändert
`HarnessRunner`, den Task 5 ebenfalls anfasst) → 7 (baut auf `ReplayDayLine.SharedChatCounts` aus
Task 5 und `ReplayWindow.SharedChatCutover` aus Task 6 auf) → 8 → 9. Task 1 hat bis Task 3
keinen Konsumenten im Live-Pfad und bis Task 5 keinen im Harness — gewollt, wie der Detektor in
#31.

**Parallelisierbarkeit:** Task 1 ist von Task 2 dateidisjunkt (Core/Chat, Parser,
`ChatLogArchiveModels.cs`, `HarnessRunnerTests`-Helfer gegen `IUsageStatFlushService.cs`,
`UsageStat.cs`, Flush-Service, Query-Service, Migration) und darf **parallel zu Task 2** laufen —
aber nur in einem eigenen Worktree, weil Task 2 bis Task 3 uncommittet bleibt und zwei Subagents
auf einem Working Tree denselben Snapshot nicht teilen können. Alles ab Task 3 ist strikt
sequenziell: Tasks 3, 5, 6 und 7 fassen alle `HarnessRunner.cs` bzw. `ReplayModels.cs` an, und
jeder Task setzt die Namen des vorigen voraus. Auf **einem** Working Tree gilt: 1 → 2 → 3 → 4 →
5 → 6 → 7 → 8 → 9.

## File Structure

```
src/EmotePurge.Core/
  Chat/MessageOrigin.cs                          (C: Enum eigen / fremd / unbestimmbar)
  Chat/SharedChatRule.cs                         (C: Tag-Namen, Classify, HasOtherSourceMarkers, FromTags)
  ChatLogArchive/ChatLogArchiveModels.cs         (M: ChatLogMessage.HasOtherSourceMarkers, Task 1)
  Entities/UsageStat.cs                          (M: SharedChatUseCount, Task 2)
  Services/IUsageStatFlushService.cs             (M: EmoteUsageCounts(Human, Bot, SharedChat), Task 2)
  Services/IUsageStatQueryService.cs             (M: UsageStatRowDto fünfstellig; Kommentare zur Übergangssumme, Tasks 2/4)
src/EmotePurge.Infrastructure/
  ChatLogArchive/JustlogRawLineParser.cs         (M: Marker-Bool, Task 1)
  Services/UsageStatFlushService.cs              (M: drittes Array, dritte Addition, Task 2)
  Services/UsageStatQueryService.cs              (M: GetRowsAsync dritte Spalte Task 2; Übergangssumme Task 4)
  Persistence/AppDbContext.cs                    (M nur, wenn Task 4 den Index erweitert)
  Migrations/<Zeitstempel>_AddUsageStatSharedChatUseCount.cs + Snapshot (C/M, generiert, Task 2)
  Migrations/<Zeitstempel>_AddSharedChatUseCountToUsageStatIndex.cs (C nur, wenn Task 4 den Index erweitert)
src/EmotePurge.Worker/
  UsageCategory.cs                               (C: Enum + UsageCategoryRule.Resolve, Task 3)
  IEmoteUsageCounter.cs, EmoteUsageCounter.cs    (M: Increment(string, UsageCategory), Task 3)
  TwitchChatManager.cs                           (M: WorkerStats injiziert, Raum-Klassifikation vor Bot, Task 3)
  WorkerStats.cs                                 (M: Zähler unbestimmbarer Nachrichten, Task 3)
  UsageFlushWorker.cs                            (M: Log-Zeile je Flush, Kommentar, Task 3)
  Harness/HarnessInputHash.cs                    (M: dritte Spalte, Task 3)
  Harness/ReplayDayCounter.cs                    (M: dreiwertig, Count gibt UsageCategory zurück, Task 5)
  Harness/ReplayModels.cs                        (M: Task 5 ReplayUsageRow/ReplayDayLine; Task 6 ReplayWindow/ReplayRunInfo/Reasons; Task 7 Gate/Diagnostics)
  Harness/HarnessRunner.cs                       (M: Task 5 Callback/Mapping/AlgorithmVersion; Task 6 Stichtag/Diagnose/Identität/Markdown; Task 7 Markdown)
  Harness/ReplayFidelityCalculator.cs            (M: Task 6 HumanOnly/diagnostic; Task 7 Dreikomponenten/Symmetrie)
  Harness/HarnessOptions.cs                      (M: SharedChatCutover, Task 6)
  Harness/HarnessCommandLine.cs                  (M: --diagnostic, Task 6)
  Harness/HarnessReportFile.cs                   (M: HarnessRunIdentity.SharedChatCutover, Task 6)
  Program.cs                                     (M: Diagnostic durchreichen, Task 6)
tests/EmotePurge.Infrastructure.Tests/
  Unit/SharedChatRuleTests.cs                    (C, Task 1)
  Unit/JustlogRawLineParserTests.cs              (M, Task 1)
  Integration/UsageStatFlushServiceTests.cs      (M, Task 2)
  Integration/UsageStatQueryServiceTests.cs      (M: GetRowsAsync Task 2; Übergangstest Task 4)
tests/EmotePurge.Worker.Tests/
  SharedChatRuleTwitchLibTests.cs                (C: Fixtures aus echten IRC-Zeilen, Task 1)
  HarnessRunnerTests.cs                          (M: Task 1 ChatLogMessage-Konstruktionen; Task 5–7 Fälle)
  EmoteUsageCounterTests.cs                      (M, Task 3)
  HarnessInputHashTests.cs                       (M, Task 3)
  ReplayDayCounterTests.cs                       (M, Task 5)
  HarnessReportFileTests.cs                      (M: Task 5 Tageszeile; Task 6 Kopf)
  ReplayFidelityCalculatorTests.cs               (M: Task 5 Helfer; Task 6 HumanOnly/diagnostic; Task 7 Dreikomponenten/Symmetrie)
  HarnessCommandLineTests.cs                     (M, Task 6)
  WorkerServiceRegistrationTests.cs              (M, Task 6)
docker-compose.yml, docker-compose.prod.yml, .env.example   (M: Harness__SharedChatCutover, Task 6)
.github/dependabot.yml                           (M: TwitchLib.* ignorieren, Task 1)
docs/DECISIONS.md                                (M: Eintrag im Commit der Tasks 2+3; Betrifft-Zeile in 4–7)
docs/Architectur.md                              (M: :80, :270 in Task 2; :232 in Task 4)
```

## Entscheidungen dieses Plans (wo die Spec Spielraum ließ)

1. **Regel und Enum liegen in `EmotePurge.Core` (`Core/Chat/`), das Zählkategorie-Enum im
   Worker** (Spec, offene Frage 1). Ausschlaggebend ist nicht der Live-Pfad, sondern der Parser:
   `JustlogRawLineParser` (Infrastructure) muss die Anwesenheit der übrigen `source-*`-Marker
   feststellen und braucht dafür die Tag-Namen — die einzige Schicht, die beide Seiten sehen, ist
   Core. Die Regel steht neben ihrem Vokabular, nicht getrennt davon; `ChatLogMessage` liegt
   ohnehin in Core. Das Enum `UsageCategory` (Mensch / Bot / Shared Chat) braucht dagegen nur der
   Worker (`IEmoteUsageCounter`, `ReplayDayCounter`): `EmoteUsageCounts` bleibt in Core, weil
   `FlushAsync` es trägt, aber die Eingabeseite des Zählers ist Worker-intern.
   `CoreAssemblyReferenceTests` bleibt trivial grün — alles ist BCL.
2. **Die Tag-Extraktion ist als Funktion über `IReadOnlyDictionary<string,string>?` geteilt, das
   Lesen der Properties bleibt seitenlokal.** Beide Seiten halten am Ende ein Tag-Dictionary in
   der Hand (TwitchLib `UndocumentedTags`, Justlog `tags`), also gehört der null-sichere
   Dictionary-Gang in die Regel (`SharedChatRule.FromTags`), nicht zweimal in die Aufrufer. Was
   seitenlokal bleibt, ist der Zugriff auf `ChatMessage.RoomId`/`.UndocumentedTags` in
   `TwitchChatManager` und das Befüllen des Marker-Bools im Parser. Die puren Tests der
   „Live-Extraktion" aus Spec B7 testen `FromTags` mit `null` und handgebauten Dictionaries; die
   TwitchLib-gebundenen Fixtures schicken echte `ChatMessage`-Properties durch dieselbe Methode.
3. **`Count` gibt die Kategorie zurück, statt dass der Runner ein zweites Mal klassifiziert.**
   Spec B5 verlangt, dass der fensterweite `distinctChatters`-HashSet im `HarnessRunner`-Callback
   derselben Regel folgt — die Klassifikation muss dort vor dem `Add` bekannt sein. Zwei Aufrufe
   der Regel pro Nachricht (Runner und Counter) wären billig, aber zwei Stellen. Stattdessen
   klassifiziert `ReplayDayCounter.Count` einmal und gibt `UsageCategory` zurück; der Callback
   fügt den Chatter nur bei `Human` hinzu. Reihenfolge im Callback: erst `Count`, dann `Add`.
4. **Unbestimmbare Nachrichten werden im Harness in einem eigenen Nachrichtenzähler geführt
   (`IndeterminateMessageCount`), ihre Treffer landen in `SharedChatCounts`;
   `SharedChatMessageCount` zählt weiterhin nur eindeutig fremde Nachrichten** (offene Frage 3:
   `ReplayDiagnostics.SharedChatMessages` behält Namen und Bedeutung, Neues tritt daneben).
   Kontrollrechnung, die der Test benennt: `Σ SharedChatCounts > 0` ⇒
   `SharedChatMessageCount + IndeterminateMessageCount > 0`.
5. **Fremde und unbestimmbare Nachrichten werden nicht tokenisiert** (offene Frage 4) und tragen
   auch nicht zu `FirstSeenUnknownHits`, Zellen, `_humanChatters` oder `_botMessageCount` bei.
   Regel in einem Satz: eine nicht-eigene Nachricht bewegt `MessageCount`, `OutsideDayCount`,
   ihren eigenen Nachrichtenzähler und `SharedChatCounts` — sonst nichts. `UnknownName` beschriebe
   sonst fremde Chats gegen die eigene Day-Map; `FirstSeenUnknownHits` erklärt Gate-Abweichungen
   in `HumanCounts`, an denen fremde Treffer nicht teilnehmen.
6. **Der Stichtag wird als roher `string?` gebunden und im Runner geparst.** `HarnessOptions`
   wird in `AddHarness` per `Bind` gefüllt — **vor** `host.Build()` und außerhalb von
   `RunAsync`. Ein `DateOnly?`-Property würde bei einem Tippfehler in der Konfiguration dort
   werfen, aus `Main` heraus, mit einem vom Runtime erfundenen Exit-Status. Ein `string?` kommt
   immer durch; `ExecuteAsync` parst strikt `yyyy-MM-dd` invariant und beendet mit
   `ExitPreconditionViolated` (3) und deutscher Zeile, die `Harness:SharedChatCutover` nennt.
   In die Identität geht der geparste `DateOnly?`.
7. **Diagnosemodus als `--diagnostic`**, Grammatik `harness <kanal> [--days <n>] [--diagnostic]`
   in beliebiger Reihenfolge der beiden Optionen, jede höchstens einmal; alles andere bleibt
   Exit 2. `RunHarness(string ChannelName, int? Days, bool Diagnostic)` ohne Default. Der Modus
   ist **nicht** Teil der Identität: er ändert die Zählung nicht, nur das Urteil — eine
   Diagnose-Datei mit gesetztem Stichtag darf ein bindender Lauf fortsetzen und dabei den
   Bericht ohne Diagnose-Marker neu schreiben; das ist gewollt und im Runner-Kommentar zu nennen.
   Im Bericht: `ReplayRunInfo.Diagnostic`, `GateEligible = false` mit Grund `diagnostic-run`,
   Markdown „Gate-tauglich: nein — diagnostic-run".
8. **Symmetrie-Grund heißt `shared-chat-asymmetric`**, Konstante
   `ReplayGateIneligibleReasons.SharedChatAsymmetric`. Die Toleranz ist eine neue **Konstante**
   `MaxSharedChatAsymmetry = 0.10` im Calculator neben `RequiredRatedDays` — derselbe Wert wie die
   präregistrierte Abweichungsschwelle, aber die Spec-Formulierung „dieselbe Konstante" trifft
   den Code nicht: die 10 % stehen heute nur im Docstring und im Issue (s. „Anmerkungen zur
   Spec"). Definition der Symmetrie über die **bewerteten** Tage: beide Fenstersummen 0 ⇒
   symmetrisch; Live > 0 und `|Log − Live| / Live ≤ 0.10` ⇒ symmetrisch; alles andere (auch
   Live = 0 bei Log > 0) ⇒ Grund gesetzt.
9. **Vehikel für den Live-Zähler unbestimmbarer Nachrichten: `WorkerStats` plus eine
   Log-Zeile je Flush.** `TwitchChatManager` erhöht per `Interlocked` (kein Lock auf dem Hot
   Path), `UsageFlushWorker` liest nach einem erfolgreichen Flush die Differenz seit dem letzten
   Flush ab und loggt sie auf Information — **nur wenn sie größer als null ist**, damit der
   Normalbetrieb still bleibt. Kein Health-Feld, kein Admin-Vertrag (Spec-Minimum).
10. **Der Deploy-Tag bekommt einen Konfig-Platzhalter** (offene Frage 6): `Harness__SharedChatCutover`
    in beiden Compose-Dateien (`${HARNESS_SHARED_CHAT_CUTOVER:-}`) und
    `HARNESS_SHARED_CHAT_CUTOVER=` in `.env.example` mit Kommentar (UTC-Tag nach dem Prod-Deploy,
    ISO-Form, leer ⇒ nur `--diagnostic` läuft). Das Datum selbst steht als Kommentar im Issue #69
    (DoD), der Platzhalter sorgt dafür, dass niemand den Schlüsselnamen erraten muss.
11. **Covering-Index: Messregel statt Vorentscheidung** (offene Frage 2). Task 4 misst per
    `EXPLAIN (ANALYZE, BUFFERS)` die drei Aggregat-Queries gegen die Dev-DB am größten lokalen
    Kanal über 30 Tage, einmal wie sie nach der Übergangssumme sind, einmal mit dem Index
    testweise um `SharedChatUseCount` erweitert. Regel: Der Index wird **nur** erweitert, wenn die
    Übergangssumme den Index-Only-Scan verliert **und** der Unterschied am größten Kanal über
    **20 ms** pro Query liegt (dieselbe Schwelle wie die `MIN(Date)`-Messung in #31). Sonst wird
    der Heap-Zugriff für die Übergangszeit hingenommen und dokumentiert. Grund für die Neigung
    zum Hinnehmen: eine Index-Erweiterung ist auf Prod ein Neubau des Index der größten Tabelle
    (`CREATE INDEX` ohne `CONCURRENTLY` sperrt Schreibzugriffe; der Flush hat fünf Requeue-Runden
    ≈ 2,5 min Toleranz), und Zug 2 baut ihn wieder zurück, sofern er keine Drilldown-Serie über
    die Spalte bringt.
12. **Ein Task pro Kompiliereinheit, nicht pro Datei.** Task 5 fasst `ReplayDayLine`,
    `ReplayUsageRow`, den Counter, den Runner-Callback und den Versions-Bump zusammen, weil die
    Formänderung der Tageszeile alle Konstruktionsstellen zugleich aufbricht; die Trennung von
    Task 6 (Stichtag) und Task 7 (Dreikomponenten/Symmetrie) folgt der Spec-Gliederung D4 gegen D3.

---

### Task 1: Die Raum-Regel — einmal, drei Ausgänge, geprüft gegen TwitchLibs Parser

**Files:**
- Create: `src/EmotePurge.Core/Chat/MessageOrigin.cs`, `src/EmotePurge.Core/Chat/SharedChatRule.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/SharedChatRuleTests.cs`,
  `tests/EmotePurge.Worker.Tests/SharedChatRuleTwitchLibTests.cs`
- Modify: `src/EmotePurge.Core/ChatLogArchive/ChatLogArchiveModels.cs` (`ChatLogMessage`),
  `src/EmotePurge.Infrastructure/ChatLogArchive/JustlogRawLineParser.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/JustlogRawLineParserTests.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessRunnerTests.cs` (nur die `ChatLogMessage`-Konstruktionen
  :114, :139, :515 und der `Message(...)`-Helfer — sonst nichts), `.github/dependabot.yml`

**Vorab lesen:** Spec B1 vollständig (Tabelle der vier Ausgänge, Randfälle, Fixture-Absatz) und
B8 (TwitchLib-Falle); `ReplayDayCounter.cs:116-119` (die Regel, die herausgezogen wird);
`JustlogRawLineParser.cs:100-140` und die Klassendoku dort; `EmoteNameMatching.cs` als Muster
für eine statische Core-Regel; `EmoteNameMatchingTests.cs` als Muster für Core-Tests in
`Infrastructure.Tests/Unit`; `BotChatterDetectorTests.cs` (einziger bestehender TwitchLib-Bezug
in `Worker.Tests`); `dependabot.yml:33-41`.

**Interfaces (verbindlich):**
- `namespace EmotePurge.Core.Chat` — `public enum MessageOrigin { Own, Foreign, Indeterminate }`.
- `public static class SharedChatRule` mit den Konstanten `SourceRoomIdTag = "source-room-id"`,
  `SourceIdTag = "source-id"`, `SourceBadgesTag = "source-badges"`,
  `SourceBadgeInfoTag = "source-badge-info"` und drei Methoden:
  `MessageOrigin Classify(string? roomId, string? sourceRoomId, bool hasOtherSourceMarkers)`,
  `bool HasOtherSourceMarkers(IReadOnlyDictionary<string, string>? tags)`,
  `MessageOrigin FromTags(string? roomId, IReadOnlyDictionary<string, string>? tags)`.
- `ChatLogMessage(DateTime SentAtUtc, string? UserId, IReadOnlyList<KeyValuePair<string,string>> Badges, string? RoomId, string? SourceRoomId, bool HasOtherSourceMarkers, string Text)`
  — das Bool **vor** `Text`, ohne Default.
- Spätere Tasks erwarten: Task 3 ruft `SharedChatRule.FromTags(e.ChatMessage.RoomId, e.ChatMessage.UndocumentedTags)`;
  Task 5 ruft `SharedChatRule.Classify(roomId, sourceRoomId, hasOtherSourceMarkers)` in
  `ReplayDayCounter.Count` und reicht `message.HasOtherSourceMarkers` aus dem Runner durch.

**Absicht und Verträge:**
- `Classify` ist die Tabelle aus Spec B1, wörtlich: `sourceRoomId` nicht leer und ordinal ungleich
  `roomId` ⇒ `Foreign` (auch wenn `roomId` `null` ist — ein Vergleich gegen nichts ist nie
  „gleich", als Vertrag zu testen); nicht leer und gleich ⇒ `Own`; leer/`null` und keine anderen
  Marker ⇒ `Own`; leer/`null` und andere Marker ⇒ `Indeterminate`. Leerer String und `null` sind
  in **beiden** Argumenten gleichbedeutend; das Trimmen von Whitespace gehört **nicht** in die
  Regel (Twitch-IDs sind Ziffernstrings, ein Leerzeichen wäre ein Parserfehler, kein Wert).
- `HasOtherSourceMarkers` prüft nur die **Anwesenheit** der drei Schlüssel `source-id`,
  `source-badges`, `source-badge-info` — nicht ihre Werte, auch ein leerer Wert zählt als
  anwesend. `null`-Dictionary ⇒ `false`.
- `FromTags` ist die null-sichere Live-Extraktion: `tags == null` ⇒ `Own`, keine Ausnahme, kein
  Lookup; sonst `TryGetValue` auf `source-room-id`, leer wie fehlend, dann `Classify`. Keine
  Allokation; kein Logging. Der Klassenkommentar nennt den Grund für die Null-Sicherheit
  (TwitchLib 4.0.1 legt `UndocumentedTags` nur bei mindestens einem unbekannten Tag an).
- **Parser:** `HasOtherSourceMarkers` wird aus dem `tags`-Dictionary per `SharedChatRule.HasOtherSourceMarkers`
  befüllt — dieselbe Funktion, kein zweiter Schlüsselsatz; `room-id`/`source-room-id` werden wie
  heute gelesen.
- **`HarnessRunnerTests`:** die drei `new ChatLogMessage(...)`-Stellen und der `Message(...)`-Helfer
  bekommen das Bool (`false`), damit das Projekt baut; Fälle werden hier nicht geändert.
- **Dependabot-Freeze (Spec B8, Konfigvariante):** `ignore`-Eintrag für `dependency-name:
  "TwitchLib.*"` ohne `update-types`-Einschränkung, mit Kommentar: Grund (ein Update könnte
  `source-room-id` typisieren und die Live-Extraktion still blind machen, ohne dass Berichtskopf
  oder Input-Hash sich bewegen), Entfernungsauslöser (Ende des bindenden #69-Laufs), Verweis auf
  den DECISIONS-Eintrag „Shared Chat" vom 2026-09-06 (kommt im Commit der Tasks 2+3).
- **TwitchLib-gebundene Fixtures:** Roh-`PRIVMSG`-Zeilen (synthetisch: erfundene IDs und Logins,
  kein aufgezeichneter Chat — dieselbe Regel wie in `ReplayDayCounterTests`), geparst über
  `TwitchLib.Client.Parsing.IrcParser.ParseMessage`, zu `ChatMessage` gemacht über den
  Konstruktor `(botUsername, ircMessage, emoteCollection, replaceEmotes, prefix, suffix)` mit
  einer leeren `MessageEmoteCollection`, dann `FromTags(chatMessage.RoomId, chatMessage.UndocumentedTags)`.
  Der Klassenkommentar sagt ausdrücklich, dass diese Datei die Ausnahme von „Policies
  TwitchLib-frei" ist und warum (das Bibliotheksverhalten ist der Prüfgegenstand), und nennt die
  Version 4.0.1.

- [ ] **Step 1 (Tests zuerst, `SharedChatRuleTests`, pur):** je ein Fall belegt: kein Marker ⇒
  eigen · gleich ⇒ eigen · ungleich ⇒ fremd · leerer `source-room-id` ohne Marker ⇒ eigen ·
  leerer `source-room-id` mit Marker ⇒ unbestimmbar · `room-id` fehlt bei gesetztem
  `source-room-id` ⇒ fremd · `HasOtherSourceMarkers` für jeden der drei Schlüssel einzeln (Theory)
  und für `null` · `FromTags(null-Dictionary)` ⇒ eigen ohne Ausnahme · Dictionary ohne
  `source-room-id` und ohne Marker ⇒ eigen · Dictionary mit `source-room-id` ungleich ⇒ fremd ·
  Dictionary nur mit Nebenmarker ⇒ unbestimmbar.
- [ ] **Step 2 (Tests zuerst, `SharedChatRuleTwitchLibTests`, TwitchLib-gebunden):** drei
  Zeilen, jede belegt einen Ausgang **und** eine Parser-Eigenschaft: (a) Shared-Chat-Zeile mit
  vollem `source-*`-Satz und fremder `source-room-id` ⇒ `Foreign`, und `UndocumentedTags` enthält
  `source-room-id` (Nachweis, dass 4.0.1 den Tag nicht typisiert — genau der Vertrag, den ein
  Update brechen könnte); (b) gewöhnliche Zeile **nur** mit den typisierten Tags (`badges`,
  `color`, `display-name`, `id`, `room-id`, `tmi-sent-ts`, `user-id`, `user-type`, `mod`,
  `subscriber`, `turbo` — keine `client-nonce`, keine `flags`) ⇒ `Own`, und der Test prüft
  vorher, dass `UndocumentedTags` tatsächlich `null` ist (sonst ist die Null-Probe keine); (c)
  Zeile mit `source-id`/`source-badges`, aber ohne `source-room-id` ⇒ `Indeterminate`.
- [ ] **Step 3 (Tests zuerst, `JustlogRawLineParserTests`):** Marker gesetzt ⇒ Bool `true` ·
  Marker nicht gesetzt ⇒ `false` · `source-room-id` leer bei gesetzten Markern ⇒ `SourceRoomId`
  `null` **und** Bool `true` (die Kombination, die den unbestimmbaren Fall trägt).
- [ ] **Step 4: rot laufen lassen.** Filter `SharedChatRuleTests|SharedChatRuleTwitchLibTests|JustlogRawLineParserTests`;
  Expected: Compilerfehler.
- [ ] **Step 5: implementieren** — Enum, Regel, `ChatLogMessage`, Parser, `HarnessRunnerTests`-Konstruktionen,
  `dependabot.yml`.
- [ ] **Step 6: grün laufen lassen.** Gleicher Filter plus `CoreAssemblyReferenceTests`, dann
  `dotnet build EmotePurge.slnx`. Expected: PASS, Solution baut (der Worker nutzt das neue Feld
  noch nicht — das ist der gewollte Zwischenstand).
- [ ] **Step 7:** `dotnet format EmotePurge.slnx`; Nutzer fragen; Commit
  `feat(core): classify chat messages by room origin`.

**Fertig-Bedingung:** die drei Testklassen grün; `CoreAssemblyReferenceTests` grün; Solution
baut; `ReplayDayCounter.cs:116-119` ist **noch unverändert** (Umstellung ist Task 5); die
Dependabot-Datei nennt `TwitchLib.*`.

**Ausdrücklich nicht:** keine Änderung an `ReplayDayCounter`, `TwitchChatManager` oder dem
Zähler; kein eigenes IRC-Parsing; kein Vergleich gegen `Channel.TwitchChannelId`.

**Modell: sonnet** — vollständig aufgezählte Randfälle; die einzige Unschärfe (TwitchLib-API)
ist per Reflection belegt und steht in der Ist-Tabelle.

---

### Task 2: Persistenz — drittes Feld, `SharedChatUseCount`, Migration, Upsert, Rohzeilen, DECISIONS

**Files:**
- Modify: `src/EmotePurge.Core/Services/IUsageStatFlushService.cs` (`EmoteUsageCounts`, XML-Doc,
  `<summary>` von `FlushAsync`), `src/EmotePurge.Core/Entities/UsageStat.cs`,
  `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` (`UsageStatRowDto` + `<param>`),
  `src/EmotePurge.Infrastructure/Services/UsageStatFlushService.cs`,
  `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` (**nur** `GetRowsAsync`),
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatFlushServiceTests.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs` (nur
  `GetRowsAsync`-Fälle), `docs/DECISIONS.md`, `docs/Architectur.md` (`:80`, `:270-280`)
- Create (generiert): `src/EmotePurge.Infrastructure/Migrations/<Zeitstempel>_AddUsageStatSharedChatUseCount.cs`
  + aktualisierter `AppDbContextModelSnapshot.cs`

**Vorab lesen:** Spec D2 (Bedeutung der drei Spalten, Nebenwirkung auf `BotUseCount`), B3
vollständig, B4 (Rohzeilen), D5 (nur die zwei Nebenwirkungen — der Lesepfad selbst ist Task 4),
DoD-Liste der Pflichtinhalte des DECISIONS-Eintrags; `20260901193259_AddUsageStatBotUseCount.cs`
als Form-Vorbild; `UsageStatFlushService.cs` samt Atomaritäts-Kommentar; `AppDbContext.cs:40-44`
(**nicht anfassen** — die Index-Frage ist Task 4); `UsageStatFlushServiceTests`-Helfer;
`docs/DECISIONS.md` Kopf und die jüngsten drei Einträge (Form); den #31-Eintrag „Bot-Nutzung
bekommt eine zweite Spalte, keine zweite Zeile" als inhaltliches Vorbild.

**Interfaces (verbindlich):**
- `public readonly record struct EmoteUsageCounts(int Human, int Bot, int SharedChat);` — XML-Doc
  für `SharedChat`: alles aus fremden Räumen, Bots inklusive, plus unbestimmbare Nachrichten
  (D2/B1); `Human`/`Bot` heißen ab jetzt „im eigenen Raum".
- `UsageStat.SharedChatUseCount` (`int`), Kommentar erweitert den bestehenden zur Bot-only-Zeile
  auf die Shared-only-Zeile und sagt, dass die Lesequeries sie **bis Zug 2** als benutzt lesen
  (D5) — nicht dupliziert, erweitert.
- `public record UsageStatRowDto(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount);`
- `IUsageStatFlushService.FlushAsync` — Signatur unverändert, `<summary>` sagt „(human, bot, shared chat)".
- Migrationsname **`AddUsageStatSharedChatUseCount`**; Task 8 erwartet ihn in der `(Pending)`-Liste.
- Spätere Tasks erwarten: `usageCounts[id].SharedChat` (Task 3 im Zähler), `r.SharedChatUseCount`
  auf dem DTO (Task 3 Hash, Task 5 Mapping).

**Absicht und Verträge:**
- **Migration:** genau ein `AddColumn<int>` auf `UsageStats`, `nullable: false`, `defaultValue: 0`,
  mit demselben Kommentar wie das Vorbild (Katalog-only in Postgres ≥ 11; das noch laufende alte
  Image schreibt per `UNNEST` ohne diese Spalte und braucht den Default aus der Spalte). Enthält
  die generierte Datei **irgendetwas anderes**, per `dotnet ef migrations remove` verwerfen und
  die Ursache klären. Der Snapshot führt den Index **weiterhin** mit `IncludeProperties … "UseCount"`
  und nur damit.
- **Upsert:** drittes Array `@sharedChatUseCounts` aus derselben `validIds`-Reihenfolge, dritte
  Spalte im `INSERT`, dritte Addition im `DO UPDATE SET`. Der Atomaritäts-Kommentar wird auf drei
  Spalten erweitert; der „No filter"-Kommentar nennt die Shared-only-Zeile
  (`UseCount = 0, BotUseCount = 0, SharedChatUseCount = n`) als gewollt — sie ist die
  Negativprobe aus Spec B6.
- **`GetRowsAsync`:** reicht die dritte Spalte roh durch; der Interface-Kommentar, der die
  fehlende `UseCount > 0`-Filterung begründet, bekommt den Halbsatz, dass das für die dritte
  Spalte genauso gilt. Sonst **keine** Änderung an `UsageStatQueryService` — die Übergangssumme
  ist Task 4 mit eigenem Commit.
- **DECISIONS-Eintrag** (deutsch, Datum 2026-09-06, Titel sinngemäß „Shared Chat bekommt eine
  dritte Spalte, und die Oberfläche summiert übergangsweise weiter"), Pflichtinhalte aus der
  Spec-DoD, jeder als eigener fett gesetzter Absatz-Anker: der **zweite Historienbruch** (Zahlen
  vor dem Deploy enthalten fremde Nutzung, nicht rekonstruierbar); **D2** mit der Nebenwirkung
  auf `BotUseCount`/`GetEarliestBotUsageDateAsync`/Bot-Caption (ein nach dem Deploy erstmals
  getrackter Kanal mit nur gespiegelten Bots zeigt nie die Bot-Caption) und der **Begründung
  gegen die vierte Spalte** (Preis in Migration, Upsert, Record, Tageszeile, DTOs, Hash, Tests
  — für eine Unterscheidung, die keine Lesequery stellt; E1 hat das schon einmal verworfen);
  **D5** mit beiden Nebenwirkungen (fremde Bots erscheinen übergangsweise in der Summe; der
  Covering-Index deckt die Summe nicht — Entscheidung folgt in Task 4, Satz wird dort ergänzt)
  und dem **Entfernungsauslöser** (Zug 2 dreht den Lesepfad um und bricht den Übergangstest
  bewusst); **B5**: Bedeutungsänderung von `ReplayDayLine.DistinctChatters` und der
  fensterweiten Chatter-Zahl (eigene Menschen), `HumanOnly` ab `harness-2` allein aus dem
  Shared-Chat-Stichtag (zulässig, weil der Bot-Split am 2026-09-01 vor #73 deployt wurde);
  **D3**: **Begründung der geteilten Metrik** — beide Seiten leiten unabhängig her, ein
  Auseinanderlaufen der Shared-Chat-Summen macht eine falsche Regel messbar, deshalb Berichten
  plus Symmetrie als Eignungsbedingung statt stummem Ausschluss, und warum kein viertes Gate;
  **D4**: expliziter fail-closed Stichtag statt Ableitung aus den Daten (0-%-Kanäle blieben
  sonst dauerhaft ohne bewerteten Tag), Rollback-Verbot im Messfenster samt Folge; **B1**: der
  unbestimmbare Fall und seine Abbildung auf die Shared-Chat-Komponente; **B8**: die
  TwitchLib-Freeze-Falle und die stille Bot-Heuristik-Falle; „ausdrücklich nicht gebaut" (Liste
  der Spec). `**Betrifft:**` nennt die Dateien der Tasks 1–3 und `docs/Architectur.md`. Der
  Eintrag **begründet**, der Plan beschreibt — nichts aus diesem Plan abschreiben.
- **Architectur.md:** `:80` „getrennt nach Mensch und Bot" wird um die dritte Kategorie
  (`SharedChatUseCount`, fremde Räume einer Shared-Chat-Session, s. DECISIONS 2026-09-06)
  ergänzt; `:270-280` das `UsageStat`-Listing führt `BotUseCount` und `SharedChatUseCount` mit je
  einem Halbsatz-Kommentar. `:232` bleibt für Task 4.

- [ ] **Step 1 (Tests zuerst, `UsageStatFlushServiceTests`):** bestehende Fälle auf den
  dreistelligen Typ umstellen (drittes Feld 0 — inhaltlich gleich); neue Fälle: (a) neue Zeile
  mit allen drei Werten; (b) zwei Flushes am selben Tag addieren in **alle drei** Spalten
  getrennt; (c) `FlushAsync_PairsAllThreeArraysCorrectly_AcrossMultipleEmotes` wird zum
  Vier-Array-Fall mit paarweise verschiedenen Tripeln — Guard gegen Vertauschung; (d) Batch, in
  dem ein Emote **ausschließlich** fremde Treffer hat ⇒ Zeile `UseCount = 0, BotUseCount = 0,
  SharedChatUseCount = n` existiert.
- [ ] **Step 2 (Tests zuerst, `UsageStatQueryServiceTests`):** `GetRowsAsync` liefert die dritte
  Spalte roh — eine Shared-only-Zeile kommt mit ihrem Wert zurück (Erweiterung von
  `GetRowsAsync_IncludesBotOnlyRows` oder eigener Fall).
- [ ] **Step 3: rot laufen lassen.** Infrastructure-Testprojekt allein, Filter
  `UsageStatFlushServiceTests|UsageStatQueryServiceTests`; Expected: Compilerfehler.
- [ ] **Step 4: Typ, Entität, DTO, Migration erzeugen und prüfen, Upsert und `GetRowsAsync` umbauen.**
- [ ] **Step 5: grün laufen lassen.** Gleicher Filter plus `CoreAssemblyReferenceTests`; die
  `PostgresFixture` migriert mit den echten Migrationen — grün heißt, die Migration läuft durch.
- [ ] **Step 6: lokale Dev-DB nachziehen** (`docker compose up -d postgres`, `dotnet ef database update …`).
  **Nicht** `docker compose up -d --build api worker` — Task 8 braucht die gecachten alten
  Images für die Gegenprobe.
- [ ] **Step 7: DECISIONS-Eintrag und Architectur.md schreiben.** `docs/DECISIONS.md` unmittelbar
  vorher neu einlesen (Sortierregel: neuester Eintrag zuoberst).
- [ ] **Step 8:** `dotnet build src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj`
  grün; **kein Commit** — der Worker baut erst nach Task 3.

**Fertig-Bedingung:** Infrastructure-Projekt baut; die genannten Integrationstests und
`CoreAssemblyReferenceTests` grün; Migration enthält genau eine Spalte mit `defaultValue: 0`;
Snapshot-Index unverändert; DECISIONS-Eintrag steht vollständig; Dev-DB migriert; der Worker
baut **nicht** (erwartet).

**Ausdrücklich nicht:** kein Anfassen der `IncludeProperties`; keine Änderung an den
Aggregat-Queries oder ihren Filtern; kein `HasDefaultValue` im EF-Modell; kein Default im Record.

**Modell: sonnet** — mechanische Erweiterung nach Vorbild; die Sorgfalt steckt im DECISIONS-Text,
dessen Pflichtinhalte oben vollständig aufgezählt sind.

---

### Task 3: Der Zähler trägt die Kategorie, `TwitchChatManager` klassifiziert null-sicher

**Files:**
- Create: `src/EmotePurge.Worker/UsageCategory.cs`
- Modify: `src/EmotePurge.Worker/IEmoteUsageCounter.cs`, `src/EmotePurge.Worker/EmoteUsageCounter.cs`,
  `src/EmotePurge.Worker/TwitchChatManager.cs` (Primärkonstruktor + `OnMessageReceived`),
  `src/EmotePurge.Worker/WorkerStats.cs`, `src/EmotePurge.Worker/UsageFlushWorker.cs`,
  `src/EmotePurge.Worker/Harness/HarnessInputHash.cs`,
  `tests/EmotePurge.Worker.Tests/EmoteUsageCounterTests.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessInputHashTests.cs`

**Vorab lesen:** Spec B1 (Reihenfolge der Klassifikation: erst Raum, dann Bot; Vehikel des
Zählers unbestimmbarer Nachrichten), B2 vollständig (Enum statt zweier Bools, Hot-Path-Kosten,
Reihenfolge in `OnMessageReceived`), B4 (Input-Hash); `TwitchChatManager.OnMessageReceived`
mit den Kommentaren zur Watchdog-Buchführung und zum Hot Path; `OnSendReceiveData` (warum
Handler allokations- und ausnahmefrei bleiben); `EmoteUsageCounter.cs` (der `TArg`-Kommentar);
`WorkerStats.cs` (Lock-Begründung); `UsageFlushWorker.FlushOnceAsync`; `HarnessInputHash.cs:225-233`;
Task 1 und 2 dieses Plans (Namen).

**Interfaces (verbindlich):**
- Consumes: `EmotePurge.Core.Chat.SharedChatRule.FromTags(string? roomId, IReadOnlyDictionary<string,string>? tags)`
  → `MessageOrigin { Own, Foreign, Indeterminate }` (Task 1); `EmoteUsageCounts(int Human, int Bot, int SharedChat)`
  und `UsageStatRowDto(EmoteId, Date, UseCount, BotUseCount, SharedChatUseCount)` (Task 2).
- Produces: `namespace EmotePurge.Worker` — `public enum UsageCategory { Human, Bot, SharedChat }`
  und `public static class UsageCategoryRule` mit genau einer Methode
  `UsageCategory Resolve(MessageOrigin origin, bool isBot)`.
- `IEmoteUsageCounter.Increment(string emoteId, UsageCategory category)`; `Merge`, `DrainAndReset`,
  `PendingEmoteCount` mit unveränderten Signaturen (Werttyp jetzt dreistellig).
- `WorkerStats`: `void RecordIndeterminateSharedChatMessage()` (Interlocked, lockfrei) und
  `long TakeIndeterminateSharedChatMessagesSinceLastFlush()` (Interlocked.Exchange auf 0).
- `TwitchChatManager` bekommt `WorkerStats stats` als weiteren Konstruktorparameter (Singleton aus
  `WorkerServiceRegistration.AddWorkerCore`).
- Spätere Tasks erwarten: Task 5 ruft `UsageCategoryRule.Resolve(origin, isBot)` in
  `ReplayDayCounter.Count` und gibt `UsageCategory` zurück.

**Absicht und Verträge:**
- **`Resolve` ist die eine Stelle des Vorrangs aus D2 und der Abbildung aus B1:** `Foreign` und
  `Indeterminate` ⇒ `SharedChat` (unabhängig von `isBot`); `Own` ⇒ `isBot ? Bot : Human`. Der
  vierte Zustand „bot && shared" ist damit nicht repräsentierbar. Beide Seiten (Live in diesem
  Task, Replay in Task 5) rufen diese Funktion; kein zweites `if` irgendwo.
- **Gefahrenstelle 1 — Reihenfolge in `OnMessageReceived`** (unverändert aus #31): Watchdog-Schreibvorgänge
  → Debug-Log → früher Return bei leerem Set → **eine** Klassifikation → Token-Schleife. Die
  Raum-Prüfung steht **vor** `IsBot` und beide nach dem frühen Return (eine Nachricht in einem
  Kanal ohne Emotes braucht keine Klassifikation). Der bestehende „Do not reorder"-Kommentar wird
  um die Raum-Prüfung ergänzt: eine gespiegelte Nachricht beweist genauso, dass der Socket lebt.
- **Gefahrenstelle 2 — null-sicher, einmal pro Nachricht:** `SharedChatRule.FromTags(e.ChatMessage.RoomId, e.ChatMessage.UndocumentedTags)`
  wird genau einmal gerufen; `UndocumentedTags` ist bei gewöhnlichen Nachrichten `null`
  (Ist-Zustand), und `FromTags` behandelt das (Task 1). Kein Indexer, kein direktes `TryGetValue`
  auf der Property. Ergebnis geht durch `Resolve` in jeden `Increment` der Schleife.
- **Unbestimmbar-Zähler:** bei `MessageOrigin.Indeterminate` einmal `stats.RecordIndeterminateSharedChatMessage()`
  — ein `Interlocked.Increment` auf einem `long`, kein Lock (die drei Flush-Felder behalten ihr
  Lock; das ist ein anderer Pfad mit anderer Frequenz). Kein Log pro Nachricht.
- **Zähler:** `ConcurrentDictionary<string, EmoteUsageCounts>` bleibt; `Increment` erhöht die
  passende von drei Komponenten per `with`, weiterhin über den `TArg`-Overload mit statischen
  Lambdas (das Enum ist Werttyp, boxt nicht — der Kommentar sagt das); `Merge` addiert drei
  Komponenten; `DrainAndReset` unverändert. `PendingEmoteCount`-Kommentar: „Unchanged by the
  human/bot split" wird auf „and by the shared-chat split" erweitert.
- **`UsageFlushWorker`:** nach `RecordFlushSuccess` die Differenz per
  `TakeIndeterminateSharedChatMessagesSinceLastFlush()` abholen und **nur bei > 0** eine
  Information-Zeile loggen (deutsch, nennt die Anzahl und dass sie als fremd gezählt wurde). Der
  Requeue-Kommentar („both UseCount and BotUseCount") nennt jetzt alle drei Spalten.
- **`HarnessInputHash`:** die Zeilen-Signatur wird `r|<EmoteId>|<Date:o>|<UseCount>|<BotUseCount>|<SharedChatUseCount>`;
  der Klassenkommentar nennt die dritte Spalte als weiteren Grund für einen neuen Lauf.
- `WorkerHealthPublisher` bleibt unangetastet.

- [ ] **Step 1 (Tests zuerst, `EmoteUsageCounterTests`):** bestehende acht Fälle auf die neue
  Signatur umstellen (`Human`/`Bot` statt `false`/`true`); neue Fälle: (a) drei Kategorien
  desselben Emotes landen getrennt im gedrainten Tripel; (b) `Merge` erhält alle drei
  Komponenten und addiert auf bestehende Einträge; (c) Drain gibt alle drei zurück und leert;
  (d) ein nur aus fremden Räumen gesehenes Emote zählt in `PendingEmoteCount` als ein Emote;
  (e) `UsageCategoryRule.Resolve` als Theory über alle sechs Kombinationen — belegt den Vorrang
  fremd vor Bot und die Abbildung unbestimmbar → Shared Chat. Ein Fall für `WorkerStats`:
  zweimal `Record…`, einmal `Take…` ⇒ 2, danach `Take…` ⇒ 0 (in `WorkerStatsTests`, existiert).
- [ ] **Step 2 (Tests zuerst, `HarnessInputHashTests`):** eine geänderte dritte Spalte bewegt den
  Hash (`Row`-Helfer :117 wird fünfstellig; Fall neben `AChangedBotUseCount_ChangesTheHash`).
- [ ] **Step 3: rot laufen lassen.** Filter `EmoteUsageCounterTests|HarnessInputHashTests|WorkerStatsTests`;
  Expected: Compilerfehler.
- [ ] **Step 4: Enum, Regel, Zähler, Interface, `WorkerStats`, `UsageFlushWorker`, `TwitchChatManager`, Hash umstellen.**
- [ ] **Step 5: alles grün.** `dotnet build EmotePurge.slnx`, dann `dotnet test EmotePurge.slnx`
  (Docker läuft). Expected: PASS über alle drei Backend-Testprojekte — die erste Stelle seit
  Task 2, an der die Solution wieder als Ganzes baut. `git diff` zeigt in `AppDbContext.cs`
  keine Änderung.
- [ ] **Step 6:** `dotnet format EmotePurge.slnx`; Nutzer fragen; **ein** Commit für Tasks 2+3
  inkl. `docs/DECISIONS.md`, `docs/Architectur.md`, Migration und Snapshot:
  `feat(usage): count shared-chat emote usage apart from own usage`.

**Fertig-Bedingung:** `dotnet test EmotePurge.slnx` grün; `OnMessageReceived` hat die Reihenfolge
Watchdog → Log → Early-Return → Raum-Klassifikation → Bot-Klassifikation → Schleife, mit genau
einem Aufruf je Klassifikation; kein Default in `EmoteUsageCounts`; Hash-Test grün.

**Ausdrücklich nicht:** kein Log pro Nachricht; keine Änderung an Roster-/Health-Verträgen; keine
Klassifikation in `OnSendReceiveData`; keine Änderung an `ReplayDayCounter` (Task 5).

**Modell: sonnet** — kleiner, klar begrenzter Umbau; die zwei Gefahrenstellen sind benannt.

---

### Task 4: Der Lesepfad summiert übergangsweise weiter (D5) — und die Index-Frage wird gemessen

**Files:**
- Modify: `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` (`GetUsageContextAsync`,
  `GetDailySeriesAsync`, `GetChannelSeriesAsync`, `GetTotalsByEmoteIdsAsync`),
  `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` (nur `<param>`-/`<summary>`-Kommentare),
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs`,
  `docs/DECISIONS.md` (Betrifft-Zeile + ein Satz zur Index-Entscheidung), `docs/Architectur.md` (`:232`)
- Modify **nur bei positivem Messergebnis:** `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs`
  (`IncludeProperties`), Create (generiert) `Migrations/<Zeitstempel>_AddSharedChatUseCountToUsageStatIndex.cs` + Snapshot

**Vorab lesen:** Spec D5 vollständig (Übergangssumme, beide Nebenwirkungen, Entfernungsauslöser,
Übergangstest), B4, offene Frage 2; „Entscheidungen dieses Plans" Nr. 11 (Messregel);
`UsageStatQueryService.cs` mit allen Index-Only-Scan-Kommentaren; `AppDbContext.cs:40-44`;
`UsageStatQueryServiceTests` (Bot-only-Fälle :126-163, :302-345, :507-543 — dieselben Seeds,
jetzt mit einer Shared-only-Zeile); `GetUsageContextAsync_TranslatesServerSide_ForAFullSizedEmoteSet`
:182 (der Übersetzungsnachweis).

**Interfaces (verbindlich):**
- Consumes: `UsageStat.SharedChatUseCount` (Task 2). Keine Signaturänderung, kein DTO ändert
  sich — das Frontend merkt von Zug 1 nichts.
- Produces: den **Übergangstest** `UsageStatQueryServiceTests.SharedOnlyRow_ReadsAsUsedUntilZug2`
  (Name verbindlich, Zug 2 sucht ihn), dessen Klassenkommentar sagt, dass Zug 2 ihn bewusst
  bricht und umdreht.

**Absicht und Verträge:**
- **Summen:** in `GetUsageContextAsync` (`TotalUseCount`, `PreviousWindowUseCount`),
  `GetDailySeriesAsync` (`EmoteDailyUsageDto.UseCount` je Tag und die Summe), `GetChannelSeriesAsync`
  (der Wert im `[offset, count]`-Paar) und `GetTotalsByEmoteIdsAsync` läuft alles über
  `UseCount + SharedChatUseCount`. **Filter:** die drei `UseCount > 0`-Prädikate (:71, :124, :212)
  und das `bounds`-Prädikat (:133) prüfen `UseCount + SharedChatUseCount > 0`. Bot-only-Zeilen
  lesen sich damit weiter als unbenutzt, Shared-only-Zeilen als benutzt (D5).
- **Unverändert:** `GetRowsAsync` (roh, Task 2), `GetEarliestBotUsageDateAsync`,
  `GetEmoteLifetimesAsync`, `GetUsageStatsAsync` (Debug-Rohliste zeigt weiter nur `UseCount` —
  bewusst, sie ist kein Produktpfad; ein Halbsatz im Kommentar).
- **Regel 10:** kein Zuschnitt ändert sich; der Ausdruck kommt in bestehende Lambdas. Ob Npgsql
  `Sum(cond ? a + b : 0)` und `Max(a + b > 0 ? date : null)` übersetzt, **prüft** der
  Vollgrößen-Test — grün heißt übersetzt, ein Client-Eval-Fallback wirft dort.
- **Die Kommentare zum Index-Only-Scan werden wahr gehalten:** je nach Messergebnis sagen sie
  entweder, dass die Summe die Include-Spalte verlässt und der Heap-Zugriff für die Übergangszeit
  hingenommen ist (mit Messwert), oder dass `SharedChatUseCount` befristet im Include steht.
- **Messung (Nr. 11):** SQL der drei Aggregat-Queries per `ToQueryString()` in einem
  **nicht committeten** Wegwerf-Test oder per EF-Command-Logging (`Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information`)
  an der lokalen Api abgreifen; `EXPLAIN (ANALYZE, BUFFERS)` per
  `docker compose exec postgres psql -U emotepurge -d emotepurge` am größten lokalen Kanal
  (HandOfBlood, ~900 Emotes) über 30 Tage; einmal mit dem Ist-Index, einmal nach testweisem
  `CREATE INDEX` mit erweitertem `INCLUDE` (danach wieder `DROP`). Entscheidung nach der Regel:
  Index-Only-Scan verloren **und** > 20 ms Unterschied ⇒ erweitern (EF-Modell + Migration,
  Kommentar am Index nennt Zug 2 als Rückbau-Auslöser); sonst hinnehmen. Laufzeiten und Pläne
  gehören in die Task-Rückmeldung und als ein Satz in den DECISIONS-Eintrag.
- **Architectur.md `:232`:** der Satz „`BotUseCount` steht bewusst nicht im Include, weil ihn keine
  Aggregat-Query liest" wird um `SharedChatUseCount` und die getroffene Entscheidung ergänzt.

- [ ] **Step 1 (Tests zuerst, `UsageStatQueryServiceTests`):** `SharedOnlyRow_ReadsAsUsedUntilZug2`
  seedet eine Zeile `UseCount = 0, BotUseCount = 0, SharedChatUseCount = 3` an einem jüngeren Tag
  neben einer Human-Zeile an einem älteren und belegt in **jedem** Lesepfad: `GetUsageContextAsync`
  liefert den jüngeren Tag als `LastUsedDate` und die 3 in `TotalUseCount`; `GetDailySeriesAsync`
  führt den Tag in `Days` mit Wert 3 und als `LastUsedDate`; `GetChannelSeriesAsync` listet das
  Emote mit dem Tag; `GetTotalsByEmoteIdsAsync` enthält die 3. Dazu: eine gemischte Zeile
  `UseCount = 2, SharedChatUseCount = 3` liefert 5; die bestehenden Bot-only-Fälle bleiben
  **unverändert grün** (Bot-only bleibt unbenutzt); ein Emote mit ausschließlich Shared-only-Zeilen
  hat `TotalUseCount > 0` (Regressionsschutz gegen ein vergessenes Prädikat).
- [ ] **Step 2: rot laufen lassen.** Filter `UsageStatQueryServiceTests`; Expected: die neuen Fälle
  FAIL mit 0 statt 3 — der Beleg, dass ohne diesen Task der Sturz aus D5 real wäre.
- [ ] **Step 3: Summen und Prädikate umstellen, Kommentare nachziehen.**
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter inkl. Vollgrößen-Test, dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 5: `EXPLAIN`-Messung** nach Nr. 11; Entscheidung treffen; bei „erweitern" EF-Modell
  und Migration `AddSharedChatUseCountToUsageStatIndex` erzeugen (genau `DropIndex`/`CreateIndex`,
  nichts anderes), Dev-DB nachziehen, Integrationstests erneut grün.
- [ ] **Step 6:** DECISIONS-Betrifft-Zeile + Index-Satz, `Architectur.md:232`; `dotnet format`;
  Nutzer fragen (mit dem Messergebnis); Commit
  `feat(usage): read own and shared-chat usage as one total until the display splits them`.

**Fertig-Bedingung:** Backend-Suite grün; `git diff` von `UsageStatQueryService.cs` zeigt nur
Summen/Prädikate/Kommentare, keinen neuen Zuschnitt; Messwerte liegen vor; die Index-Entscheidung
steht in DECISIONS und Architectur.md.

**Ausdrücklich nicht:** kein neues DTO-Feld, kein Api-Vertrag, kein `GetEarliestSharedChatUsageDateAsync`
(Zug 2); keine Änderung an `GetRowsAsync`.

**Modell: sonnet** — vier Summen, vier Prädikate, ein Test; die Messregel ist eindeutig.

---

### Task 5: Der Harness zählt fremd auf Nachrichtenebene — `harness-2`

**Files:**
- Modify: `src/EmotePurge.Worker/Harness/ReplayDayCounter.cs`, `src/EmotePurge.Worker/Harness/ReplayModels.cs`
  (`ReplayUsageRow`, `ReplayDayLine`), `src/EmotePurge.Worker/Harness/HarnessRunner.cs`
  (`AlgorithmVersion`, Callback :291-307, Mapping :244-246, `BuildMarkdown`-Diagnostikzeile :609-610),
  `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs` (**nur** `BuildDiagnostics`:
  `IndeterminateMessages` durchreichen — s. u.), `tests/EmotePurge.Worker.Tests/ReplayDayCounterTests.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessReportFileTests.cs`, `tests/EmotePurge.Worker.Tests/HarnessRunnerTests.cs`,
  `tests/EmotePurge.Worker.Tests/ReplayFidelityCalculatorTests.cs` (nur Helfer, die `ReplayDayLine` bauen),
  `docs/DECISIONS.md` (Betrifft-Zeile)

**Vorab lesen:** Spec B5 vollständig (Nachrichtenebene, `_sharedChatMessageCount` bleibt,
beide Chatter-Zahlen, `AlgorithmVersion`), B1 (unbestimmbar), „Entscheidungen dieses Plans"
Nr. 3, 4, 5; `ReplayDayCounter.cs` ganz; `HarnessRunner.cs:56-60, 240-262, 280-308`;
`ReplayModels.cs:24-28, 99-126`; `HarnessReportFile.cs` (Docstring zu `HarnessEventLine.Bytes`
— das Gegenbeispiel: dort war ein Default richtig, hier nicht); die vier Testdateien.

**Interfaces (verbindlich):**
- Consumes: `SharedChatRule.Classify(string? roomId, string? sourceRoomId, bool hasOtherSourceMarkers)`
  und `ChatLogMessage.HasOtherSourceMarkers` (Task 1); `UsageCategory`, `UsageCategoryRule.Resolve(MessageOrigin, bool)`
  (Task 3); `UsageStatRowDto.SharedChatUseCount` (Task 2).
- Produces:
  `public UsageCategory Count(DateTime sentAtUtc, string? userId, IReadOnlyList<KeyValuePair<string,string>> badges, string? roomId, string? sourceRoomId, bool hasOtherSourceMarkers, string text)`
  — Rückgabe statt `void`, Bool vor `text`.
  `ReplayUsageRow(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount)`.
  `ReplayDayLine` wächst um `int IndeterminateMessageCount` **direkt nach** `SharedChatMessageCount`
  und `IReadOnlyDictionary<string,int> SharedChatCounts` **direkt nach** `BotCounts` (JSON:
  `indeterminateMessageCount`, `sharedChatCounts`). `HarnessRunner.AlgorithmVersion = "harness-2"`.
  `ReplayDiagnostics` bekommt `long IndeterminateMessages` direkt nach `SharedChatMessages`
  (das ist der einzige Calculator-Eingriff dieses Tasks — die Tageszeile trägt das Feld, und
  ein Bericht, der es verschluckt, wäre ein halber Vertrag).
- Spätere Tasks erwarten: Task 7 summiert `ReplayDayLine.SharedChatCounts` und liest
  `ReplayUsageRow.SharedChatUseCount`.

**Absicht und Verträge:**
- **`Count`, neue Reihenfolge:** `_messageCount++` → Außerhalb-Tag-Zählung (für alle) →
  `origin = SharedChatRule.Classify(roomId, sourceRoomId, hasOtherSourceMarkers)` →
  `Foreign` ⇒ `_sharedChatMessageCount++`, `Indeterminate` ⇒ `_indeterminateMessageCount++` →
  `isBot = _isBot(userId, badges)` → `category = UsageCategoryRule.Resolve(origin, isBot)` →
  Verzweigung **dreiwertig** über das Enum: `Human` ⇒ `_humanChatters.Add`, Treffer nach
  `_humanCounts`, Zellen, `FirstSeenUnknownHits`, `ClassifyTokens`; `Bot` ⇒ `_botMessageCount++`,
  Treffer nach `_botCounts`, `FirstSeenUnknownHits`, `ClassifyTokens` (wie heute); `SharedChat` ⇒
  Treffer nach `_sharedChatCounts` und **sonst nichts** (Nr. 5). Rückgabe `category`. Der
  Docstring :98-102 wird umgeschrieben: eine Shared-Chat-Nachricht zählt in der dritten
  Komponente, wie der Live-Worker ab #73 zählt.
- **Bot-Erkennung auch bei fremden Nachrichten aufrufen?** Nein — `Resolve` ignoriert `isBot`
  bei `Foreign`/`Indeterminate`, aber `_isBot` ist ein Fremdaufruf pro Nachricht; die
  Implementierung darf ihn bei nicht-eigenen Nachrichten überspringen, muss aber dann `Resolve`
  mit `false` rufen, damit die eine Vorrangstelle bleibt. Kommentar dazu.
- **`Finish`** reicht das dritte Dictionary (Kopie, `StringComparer.Ordinal`) und den neuen
  Zähler positional durch; die Privacy-Zusage (kein Chatter-Id in der Zeile) gilt für das dritte
  Dictionary genauso — `Finish_ExposesNoChatterIds` prüft per Reflection über alle Properties und
  deckt es automatisch ab.
- **Runner-Callback:** erst `var category = counter.Count(..., message.HasOtherSourceMarkers, message.Text)`,
  dann `distinctChatters?.Add(message.UserId)` **nur** bei `category == UsageCategory.Human` und
  nicht-leerer `UserId`; `sawUserId`/`sawBadges` wie bisher für jede Nachricht (sie belegen, ob
  die Logs überhaupt Felder tragen — unabhängig vom Raum). Der Kommentar nennt Spec B5: beide
  Chatter-Zahlen meinen ab `harness-2` eigene Menschen.
- **Mapping :244-246:** `ReplayUsageRow` bekommt `r.SharedChatUseCount`.
- **`AlgorithmVersion = "harness-2"`**, Docstring ergänzt um „and the shared-chat rule (#73)".
  Damit werden `harness-1`-Dateien in `FindFrozenWindow` nicht mehr als Kandidat gesehen und in
  `ReadHeader` abgewiesen — beides bestehende Mechanik.
- **Markdown :609-610:** die Diagnostikzeile wird „Nachrichten gesamt / Bots / Shared Chat /
  unbestimmbar / außerhalb des Tages".
- **Keine Defaults** in `ReplayDayLine` und `ReplayUsageRow`: `HarnessReportFileTests.Day()`,
  `ReplayFidelityCalculatorTests.Build(...)` und `HarnessRunnerTests.Rows()` brechen auf und werden
  mit leerem Dictionary / 0 ergänzt. Eine `harness-1`-`.jsonl` ohne die neuen Felder wird nie
  deserialisiert (Identität weist sie vorher ab) — der einzige Ort, an dem ein Default verlockend
  wäre, braucht keinen.

- [ ] **Step 1 (Tests zuerst, `ReplayDayCounterTests`):** `Say`-Helfer bekommt
  `bool hasOtherSourceMarkers = false` (Testhelfer dürfen Defaults) und gibt die Kategorie zurück.
  `SharedChatMessage_IsCountedAndMarked` **kehrt sich um**: die fremde Nachricht zählt in
  `SharedChatCounts["e1"]`, nicht in `HumanCounts`, `SharedChatMessageCount` bleibt 1, die
  gleichraumige zweite Nachricht zählt in `HumanCounts`. Neue Fälle: fremder Bot zählt weder in
  `BotMessageCount` noch in `BotCounts`, sondern in `SharedChatCounts`; fremder Chatter erscheint
  nicht in `DistinctChatters` und erzeugt keine Zelle; fremde Nachricht erzeugt keinen
  `UnknownName`-Eintrag und keinen `FirstSeenUnknownHits`-Treffer; unbestimmbare Nachricht
  (Marker ohne `source-room-id`): Treffer in `SharedChatCounts`, `IndeterminateMessageCount` 1,
  `SharedChatMessageCount` 0; fehlende `room-id` bei gesetzter `source-room-id` ⇒ fremd;
  `Count` gibt für die drei Fälle `Human`/`Bot`/`SharedChat` zurück; Kontrollrechnung aus Nr. 4.
- [ ] **Step 2 (Tests zuerst, `HarnessReportFileTests`):** `ADayLine_KeepsItsCountsAcrossTheRoundTrip`
  trägt zusätzlich `SharedChatCounts` und `IndeterminateMessageCount` und liest beide zurück;
  Feldnamen `sharedChatCounts`/`indeterminateMessageCount` stehen wörtlich in der Datei (ein
  `Assert.Contains` auf den Rohtext, damit der camelCase-Vertrag geprüft ist, nicht nur die
  Symmetrie von Schreiben und Lesen).
- [ ] **Step 3 (Tests zuerst, `HarnessRunnerTests`):** (a) `ACompleteRun_WritesBothReportsAndTheGateFieldsOfTheCalculator`
  belegt zusätzlich `"algorithmVersion": "harness-2"` im Kopf und dass die Tageszeile
  `sharedChatCounts` trägt, wobei Tag 2s fremde Nachricht **nicht** mehr in `humanLogTotal`
  landet (Erwartung 3 → 2 — hier bricht der Fall bewusst und wird umgedreht); (b) eine mit
  `harness-1` im Kopf angelegte Datei wird nicht wiederaufgenommen — der Lauf beginnt eine
  neue Datei (Erweiterung von `AChangedDataSnapshot_StartsANewFileInsteadOfContinuingTheOldOne`
  oder eigener Fall); (c) die fensterweite Chatter-Zahl zählt nur eigene Menschen: drei
  Nachrichten von drei Chattern, einer fremd, einer Bot ⇒ „Distinkte Chatter im Fenster" = 1.
- [ ] **Step 4: rot laufen lassen.** Filter `ReplayDayCounterTests|HarnessReportFileTests|HarnessRunnerTests`;
  Expected: Compilerfehler.
- [ ] **Step 5: Counter, Records, Runner, Diagnostik-Feld, Testhelfer umstellen.**
- [ ] **Step 6: grün laufen lassen.** `dotnet test tests/EmotePurge.Worker.Tests/EmotePurge.Worker.Tests.csproj`
  komplett (die Calculator-Tests müssen mit den erweiterten Helfern unverändert grün bleiben —
  sie rechnen noch zweikomponentig, das ist Task 7), dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 7:** Betrifft-Zeile; `dotnet format`; Nutzer fragen; Commit
  `feat(harness): count shared-chat hits apart on the replay side`.

**Fertig-Bedingung:** Backend-Suite grün; `ReplayDayCounter.Count` gibt `UsageCategory` zurück
und enthält genau einen `Resolve`-Aufruf; `AlgorithmVersion` ist `harness-2`; die Tageszeile
serialisiert `sharedChatCounts` und `indeterminateMessageCount`; kein Default in den Records.

**Ausdrücklich nicht:** keine Änderung an `BuildDayFacts`, `BuildPopulation`, `BuildGate`,
`BuildPlausibility` (Task 7); kein Stichtag, keine Identitätsänderung (Task 6); kein
Umbenennen von `SharedChatMessageCount`/`SharedChatMessages`.

**Modell: sonnet** — die Regel ist in Task 1/3 fertig; hier wird sie an fünf benannten Stellen
eingehängt, mit Fällen, die jede Stelle einzeln treffen.

---

### Task 6: Der Shared-Chat-Stichtag — explizit, fail-closed, Teil der Identität; Diagnosemodus

**Files:**
- Modify: `src/EmotePurge.Worker/Harness/HarnessOptions.cs`, `src/EmotePurge.Worker/Harness/HarnessCommandLine.cs`,
  `src/EmotePurge.Worker/Program.cs`, `src/EmotePurge.Worker/Harness/HarnessReportFile.cs`
  (`HarnessRunIdentity`), `src/EmotePurge.Worker/Harness/ReplayModels.cs` (`ReplayWindow`,
  `ReplayRunInfo`, `ReplayGateIneligibleReasons`), `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs`
  (`Compute`-Signatur, `BuildDayFacts.HumanOnly`, `BuildGate`-Grund, `BuildPopulation`-Docstring),
  `src/EmotePurge.Worker/Harness/HarnessRunner.cs` (`RunAsync`/`ExecuteAsync`, Identität,
  `ReplayWindow`, `Compute`-Aufruf, `BuildMarkdown`), `docker-compose.yml`, `docker-compose.prod.yml`,
  `.env.example`, `tests/EmotePurge.Worker.Tests/HarnessCommandLineTests.cs`,
  `tests/EmotePurge.Worker.Tests/WorkerServiceRegistrationTests.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessReportFileTests.cs`, `tests/EmotePurge.Worker.Tests/HarnessRunnerTests.cs`,
  `tests/EmotePurge.Worker.Tests/ReplayFidelityCalculatorTests.cs`, `docs/DECISIONS.md` (Betrifft-Zeile)

**Vorab lesen:** Spec D4 vollständig (drei Verschärfungen, fail-closed-Begründung, Restrisiko),
B5 Absatz „`HumanOnly` gegen den Shared-Chat-Stichtag allein", D3-Tabelle der drei Signaturen;
„Entscheidungen dieses Plans" Nr. 6, 7, 10; `HarnessOptions.cs`, `HarnessCommandLine.cs`,
`Program.cs:45-101`, `HarnessRunner.cs:133-209, 247, 390-395, 550-570`, `HarnessReportFile.cs:19-28`,
`ReplayModels.cs:30-36, 84-97, 272-281`, `ReplayFidelityCalculator.cs:52-94, 107-152, 227-246`;
die Compose-`harness`-Blöcke; `.env.example:24-37`; `HarnessRunnerTests`-Konstruktor (:38-51) und
`Run(...)`-Helfer (:680).

**Interfaces (verbindlich):**
- Consumes: `AlgorithmVersion = "harness-2"` und die Tageszeile aus Task 5.
- Produces:
  - `HarnessOptions.SharedChatCutover` (`string?`, Default `null`), Konfigschlüssel
    **`Harness:SharedChatCutover`**, Env `Harness__SharedChatCutover`, Compose-Variable
    `HARNESS_SHARED_CHAT_CUTOVER`.
  - `HarnessCommandLine.DiagnosticOption = "--diagnostic"`;
    `HarnessCommandLineResult.RunHarness(string ChannelName, int? Days, bool Diagnostic)`.
  - `HarnessRunner.RunAsync(string channelName, int days, bool diagnostic, CancellationToken ct)`.
  - `HarnessRunIdentity(ChannelId, TwitchChannelId, ChannelName, WindowFrom, WindowTo, DateOnly? BotSplitCutover, DateOnly? SharedChatCutover, IReadOnlyList<string> BotAccountIds, AlgorithmVersion, InputHash)`
    (JSON `sharedChatCutover`).
  - `ReplayWindow(DateOnly From, DateOnly To, DateOnly? BotSplitCutover, DateOnly? SharedChatCutover)`.
  - `ReplayRunInfo` wächst um `DateOnly? SharedChatCutover` direkt nach `BotSplitCutover` und
    `bool Diagnostic` am Ende.
  - `ReplayGateIneligibleReasons.DiagnosticRun = "diagnostic-run"`.
  - `ReplayFidelityCalculator.Compute(window, emotes, liveRows, days, windowDays, runComplete, totalBytes, rateLimitedDays, resumePoint, bool diagnostic)`.
- Spätere Tasks erwarten: Task 7 liest `window.SharedChatCutover` nicht selbst (die Symmetrie
  läuft über die bewerteten Tage, die dieser Task definiert) und ergänzt `ReplayGateIneligibleReasons`
  um einen weiteren Grund.

**Absicht und Verträge:**
- **Fail-closed im Runner, nicht beim Binden** (Nr. 6): `ExecuteAsync` parst `options.SharedChatCutover`
  direkt nach der Tage-Prüfung (:140-146) und **vor** der Kanal-Abfrage — strikt `yyyy-MM-dd`,
  invariant, `DateOnly.TryParseExact`. Fehlend, leer/Whitespace oder nicht parsebar sind
  gleichbedeutend: ohne `diagnostic` ⇒ `logger.LogError` mit deutscher Zeile, die den Schlüssel
  `Harness:SharedChatCutover` und die erwartete Form nennt, Rückgabe `ExitPreconditionViolated`,
  **kein** Zugriff auf Datenbank oder Archiv, keine Datei. Mit `diagnostic` ⇒ Stichtag `null`,
  Warnung im Log, Lauf geht weiter. Ein **gesetzter, aber unparsebarer** Wert ist auch im
  Diagnosemodus ein Fehler (Exit 3): ein Tippfehler soll nie still als „kein Stichtag" durchgehen.
- **Identität:** `SharedChatCutover` nach `BotSplitCutover` in `HarnessRunIdentity` — geht damit
  über `SerializeIdentity` in Kopf, byte-genauen Vergleich und Dateinamen. `BotSplitCutover`
  bleibt unverändert in Identität, Kopf, Berichtszeile und Query (Freeze).
- **`HumanOnly = window.SharedChatCutover is { } c && line.Day >= c`** — `BotSplitCutover` geht
  in die Bedingung nicht mehr ein. Docstrings umschreiben, nicht ergänzen: `ReplayWindow` (:30-35),
  `ReplayDiagnostics.HumanOnlyDays` (:184-188), `BuildPopulation` (:155-158, „what the grid
  actually shows" → der Zielvertrag: eigene Menschen, das, was das Raster nach Zug 2 zeigt —
  D3), `ReplayPlausibility` (:170-171 „before the bot-split cutover" → „before the shared-chat
  cutover"). Begründung im `ReplayWindow`-Docstring: der Bot-Split ist am 2026-09-01 deployt, jeder
  Tag ab dem Shared-Chat-Stichtag liegt danach; der ausgerollte Vertrag entscheidet, nicht die
  erste Sichtung.
- **Diagnosemodus:** `Compute(..., diagnostic)` setzt in `BuildGate` bei `diagnostic` den Grund
  `diagnostic-run` (zusätzlich zu allen anderen — die Zahlen werden weiter berechnet, das Urteil
  fehlt); `ReplayRunInfo.Diagnostic` trägt es in den JSON-Bericht. Der Modus ist **nicht** in der
  Identität (Nr. 7); Runner-Kommentar nennt das und dass ein bindender Lauf eine Diagnose-Datei
  gleicher Identität fortsetzt und den Bericht neu schreibt.
- **`HarnessCommandLine`:** Grammatik `harness <kanal> [--days <n>] [--diagnostic]`, die zwei
  Optionen in beliebiger Reihenfolge, jede höchstens einmal; `--days` weiterhin mit Zahl
  1–90; jede andere Form `Invalid` mit deutscher Zeile und aktualisierter `Usage`-Konstante.
  `Program.cs` reicht `harness.Diagnostic` an `RunAsync` durch; der Klassenkommentar der
  Grammatik (:71-83) nennt die neue Option.
- **Markdown:** neue Zeile „Shared-Chat-Stichtag" direkt nach „Bot-Split-Stichtag" (Wert
  ISO-Datum oder „keiner (Diagnoselauf)"); Zeile „Lauf-Modus: Diagnose / bindend"; die
  Gate-Zeile trägt bei Diagnose automatisch `diagnostic-run` als Grund. „Human-only-Tage im
  Fenster" behält Label und Bedeutung (Tage ab Stichtag).
- **Konfiguration:** beide Compose-Dateien `Harness__SharedChatCutover=${HARNESS_SHARED_CHAT_CUTOVER:-}`
  neben `Harness__MaxMegabytesPerRun`; `.env.example`: `HARNESS_SHARED_CHAT_CUTOVER=` mit
  Kommentar — UTC-Tag **nach** dem Prod-Deploy von #73 (D+1), ISO `yyyy-MM-dd`; leer ⇒ nur
  `--diagnostic` läuft, ein bindender Lauf bricht mit Exit 3 ab; nach einem Rollback im
  Messfenster neu auf den Tag nach dem erneuten Deploy setzen (D4). Aufrufbeispiel um
  `--diagnostic` ergänzen.
- **Tests im Bestand:** `HarnessRunnerTests` setzt im `Run(...)`-Helfer `SharedChatCutover` auf
  `"2026-09-01"` (vor `Day1`), damit `"ratedDays": 3` und die Gate-Erwartungen bestehen bleiben;
  `ReplayFidelityCalculatorTests.Compute(...)`-Helfer bekommt `sharedChatCutover` mit Default
  `From` (Testhelfer), und `cutover:` behält seine Bot-Bedeutung.

- [ ] **Step 1 (Tests zuerst, `HarnessCommandLineTests`):** `--diagnostic` allein ⇒ `Diagnostic`
  true, `Days` null · `--days 3 --diagnostic` und `--diagnostic --days 3` ⇒ beides gesetzt ·
  bestehende Fälle ⇒ `Diagnostic` false · `--diagnostic` doppelt, `--diagnostic 5`, `--diagnose`
  ⇒ `Invalid` (Theory :64 erweitern) · `TheDayBounds_AreTheOnesThePlanFixed` unverändert.
- [ ] **Step 2 (Tests zuerst, `WorkerServiceRegistrationTests`):** `HarnessOptions_ComeFromTheHarnessSection`
  belegt, dass `Harness:SharedChatCutover` roh als String ankommt und ein unparsebarer Wert
  **nicht** beim Binden wirft; Default `null`.
- [ ] **Step 3 (Tests zuerst, `HarnessReportFileTests`):** Round-Trip des Kopfes mit gesetztem
  Stichtag (`sharedChatCutover` wörtlich in Zeile 1) und mit `null` (Feld fehlt wegen
  `WhenWritingNull`, Identität trotzdem gleich); zwei Identitäten, die sich nur im Stichtag
  unterscheiden, ergeben verschiedene Dateinamen.
- [ ] **Step 4 (Tests zuerst, `ReplayFidelityCalculatorTests`):** `HumanOnly` allein aus dem
  Shared-Chat-Stichtag: `DaysBeforeTheBotSplitCutover_CountForPlausibilityButNotForTheGate`
  wird zum Shared-Chat-Fall (Stichtag `From + 10` ⇒ 20 bewertete Tage);
  `MissingBotSplitCutover_LeavesNoRatedDay` kehrt sich um zu „`BotSplitCutover = null` sperrt
  keinen Tag mehr"; neuer Fall `MissingSharedChatCutover_LeavesNoRatedDay`; Diagnosemodus ⇒
  `GateEligible` false mit `diagnostic-run`, Zahlen trotzdem berechnet; `ReplayRunInfo` trägt
  Stichtag und Modus; `ComputeTwice_ReturnsEqualReports` bleibt grün.
- [ ] **Step 5 (Tests zuerst, `HarnessRunnerTests`):** fehlender Stichtag ohne `--diagnostic` ⇒
  Exit 3, Log nennt `Harness:SharedChatCutover`, **keine** Datei im Ausgabeverzeichnis, kein
  Aufruf am Archiv-Substitute · leerer String ⇒ dasselbe · `"2026-13-01"` ⇒ dasselbe, auch mit
  `diagnostic` · fehlender Stichtag **mit** `diagnostic` ⇒ Exit 0, Bericht mit `diagnostic-run`
  und `"sharedChatCutover"` abwesend/`null` · anderer Stichtag ⇒ andere Datei, alte bleibt
  unabgeschlossen liegen · Markdown enthält „Shared-Chat-Stichtag" mit dem Datum ·
  `ACompleteRun_…` prüft zusätzlich `"sharedChatCutover": "2026-09-01"`.
- [ ] **Step 6: rot laufen lassen.** Worker-Testprojekt komplett; Expected: Compilerfehler.
- [ ] **Step 7: Optionen, CLI, `Program`, Identität, `ReplayWindow`/`ReplayRunInfo`, Calculator, Runner, Markdown, Compose, `.env.example`.**
- [ ] **Step 8: grün laufen lassen.** Worker-Testprojekt, dann `dotnet test EmotePurge.slnx`; dazu
  einmal lokal `docker compose --profile harness run --build --rm harness <kanal> --days 1`
  **ohne** gesetzte Variable ⇒ Exit 3 mit der deutschen Zeile (die Konfig-Kette End-to-End), und
  mit `--diagnostic` ⇒ läuft an (Exit 0 oder 3 wegen zu kurzer lokaler Messung — beides belegt,
  dass der Stichtag nicht mehr die Vorbedingung ist).
- [ ] **Step 9:** Betrifft-Zeile; `dotnet format`; Nutzer fragen; Commit
  `feat(harness): gate only days after an explicit shared-chat cutover`.

**Fertig-Bedingung:** Backend-Suite grün; ein Lauf ohne Stichtag und ohne `--diagnostic` endet
mit Exit 3 vor dem ersten Archivzugriff; `BuildDayFacts` referenziert `BotSplitCutover` nicht
mehr; Identität und Dateiname tragen den Stichtag; beide Compose-Dateien und `.env.example`
nennen die Variable.

**Ausdrücklich nicht:** kein datenabgeleiteter Stichtag, keine Writer-Versionen, keine Änderung
an `GetEarliestBotUsageDateAsync`; kein Entfernen von `BotSplitCutover`; keine Dreikomponenten-
Summen (Task 7).

**Modell: sonnet** — viele Dateien, aber jede Änderung ist ein benannter Vertrag mit benanntem
Test; die eine Designfrage (wo geparst wird) ist in Nr. 6 entschieden.

---

### Task 7: Drei Komponenten in den Tagessummen, Shared Chat auf beiden Seiten berichtet, Symmetrie als Eignungsbedingung (D3)

**Files:**
- Modify: `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs` (`BuildDayFacts`,
  `BuildPlausibility`, `BuildGate`, `BuildDiagnostics`, Konstanten, Klassen-Docstring),
  `src/EmotePurge.Worker/Harness/ReplayModels.cs` (`ReplayGateMetrics`, `ReplayDiagnostics`,
  `ReplayGateIneligibleReasons`, Docstrings), `src/EmotePurge.Worker/Harness/HarnessRunner.cs`
  (`BuildMarkdown`), `tests/EmotePurge.Worker.Tests/ReplayFidelityCalculatorTests.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessRunnerTests.cs`, `docs/DECISIONS.md` (Betrifft-Zeile)

**Vorab lesen:** Spec D3 vollständig (Tabelle der drei Paare, Symmetriebedingung, „Was das Gate
prüft und was nicht", die Drei-Signaturen-Tabelle, verworfene Optionen), B5 Absatz
„Shared-Chat-Symmetrie als Eignungsbedingung"; „Entscheidungen dieses Plans" Nr. 8;
`ReplayFidelityCalculator.cs` ganz (besonders :107-152, :198-262, :283-307, :309-400 und die
`DayFact`-/`PopulationEntry`-Typen am Ende); `ReplayModels.cs:128-129, 154-167, 207-253`;
`HarnessRunner.cs:572-636`; `ReplayFidelityCalculatorTests` (Helfer `BaseSet`, `Build`, `Compute`;
`Plausibility_ComparesBothSidesIncludingBots` :304, `Diagnostics_AggregateThePerDayCounters` :339,
`ComputeTwice_ReturnsEqualReports` :414).

**Interfaces (verbindlich):**
- Consumes: `ReplayDayLine.SharedChatCounts`, `ReplayDayLine.IndeterminateMessageCount`,
  `ReplayUsageRow.SharedChatUseCount` (Task 5); `ReplayWindow.SharedChatCutover`, das
  `diagnostic`-Argument von `Compute` und `ReplayGateIneligibleReasons.DiagnosticRun` (Task 6);
  `ReplayDayRatio(DateOnly Day, long LogTotal, long LiveTotal, double? Ratio)` (Bestand).
- Produces:
  - `ReplayGateIneligibleReasons.SharedChatAsymmetric = "shared-chat-asymmetric"`.
  - `private const double MaxSharedChatAsymmetry = 0.10;` neben `RequiredRatedDays`.
  - `ReplayGateMetrics` wächst um `long SharedChatLogTotal` und `long SharedChatLiveTotal`
    **direkt nach** `HumanLiveTotal` (JSON `sharedChatLogTotal`, `sharedChatLiveTotal`) — über
    die **bewerteten** Tage.
  - `ReplayDiagnostics` wächst am Ende um `ValueList<ReplayDayRatio> SharedChatByDay` (JSON
    `sharedChatByDay`): ein Eintrag je Tag **mit Log**, `LogTotal` = Σ `SharedChatCounts` des
    Tages, `LiveTotal` = Σ `SharedChatUseCount` der Live-Zeilen des Tages, `Ratio` wie in
    `ReplayDayRatio` (`null` bei `LiveTotal == 0`).
- Spätere Tasks erwarten: Task 8 liest `sharedChatByDay` und die beiden Gate-Summen aus dem
  `.report.json` für die Symmetrieprobe; Zug 2 ändert hier nichts.

**Absicht und Verträge:**
- **Paar 1, `BuildDayFacts`:** `liveByDay` summiert `UseCount + BotUseCount + SharedChatUseCount`,
  `logTotal` = `Sum(HumanCounts) + Sum(BotCounts) + Sum(SharedChatCounts)`. `Ratio`,
  `LiveGapSuspected`, `CoverageQuestionable` folgen daraus unverändert. Docstring: die Tagessumme
  ist invariant gegenüber beiden Splits (Masse wird verschoben, nicht erzeugt), deshalb bleibt
  `Ratio` auch an Vor-Deploy-Tagen aussagekräftig.
- **Paar 2, `BuildPlausibility`:** beide Seiten über drei Komponenten; Feldnamen
  `LogTotalWithBots`/`LiveTotalWithBots` bleiben (Freeze der Feldnamen — s. Freeze-Liste), der
  Docstring sagt „with bots and shared chat".
- **Paar 3, `BuildPopulation`/`BuildGate`:** **unverändert human-only** — `HumanCounts` gegen
  `UseCount`. Kein Code-Diff außer dem Docstring (schon in Task 6 umformuliert).
- **Symmetrie in `BuildGate`:** zusätzlich zu `population` bekommt `BuildGate` die beiden
  Shared-Chat-Fenstersummen über die bewerteten Tage (Log: Σ `SharedChatCounts` der bewerteten
  Tage; Live: Σ `SharedChatUseCount` der Live-Zeilen an bewerteten Tagen). Regel aus Nr. 8:
  beide 0 ⇒ symmetrisch; Live > 0 und `|Log − Live| / Live ≤ MaxSharedChatAsymmetry` ⇒
  symmetrisch; sonst Grund `shared-chat-asymmetric`. Die Reihenfolge der Gründe in der Liste:
  nach `live-total-zero`, vor `diagnostic-run`. Kommentar am Konstantenblock: derselbe Wert wie
  die präregistrierte Abweichungsschwelle in #69, aber eine **Eignungsbedingung** (wie
  `RequiredRatedDays`), kein viertes Gate — und die Bedingung ist vor dem bindenden Lauf in #69
  nachzuregistrieren (DoD, Task 8).
- **Was ungeprüft bleibt, steht im Docstring von `ReplayGateMetrics`:** die Bot/Fremd-Grenze
  innerhalb des Fremden bewegt keine bindende Zahl — produktseitig folgenlos (Spec D3). Die
  Grenze eigener Mensch gegen fremder Mensch prüft das human-only-Gate direkt.
- **Diagnostik:** `SharedChatByDay` über alle Tage mit Log (nicht nur bewertete), damit die
  drei Signaturen aus D3 (vor Deploy: Live 0, Log > 0 · Deploy-Tag: 0 < Live < Log · danach:
  gleich) im Bericht lesbar sind. `SharedChatMessages` und `IndeterminateMessages` (Task 5)
  bleiben daneben.
- **Markdown:** im Abschnitt „Präregistrierte Kennzahlen" eine Zeile „Shared Chat ΣLog / ΣLive
  (bewertete Tage)" **unter** den präregistrierten Zeilen, ohne Schwellenspalte, mit dem
  Hinweis „Eignungsbedingung, kein Gate"; die Tagestabelle bekommt zwei Spalten „Shared Chat
  (Log) | Shared Chat (Live)" hinter „Live (human)" — dafür eine zweite Tageskarte aus
  `SharedChatUseCount` neben `liveByDay` (:624-628), das weiterhin nur `UseCount` summiert.
  Die drei Signaturen werden so je Tag sichtbar; ein Rollback-Tag hätte die mittlere.
- **Determinismus:** `ValueList` für die neue Liste, sortiert nach Tag — `ComputeTwice_ReturnsEqualReports`
  bleibt der Beleg.

- [ ] **Step 1 (Tests zuerst, `ReplayFidelityCalculatorTests`), mit handgerechneten Werten:**
  (a) Tagessumme über drei Komponenten: ein Tag mit `SharedChatCounts` = 5 und
  `SharedChatUseCount` = 5 hat `Ratio` 1 und ist weder Lücke noch fraglich; (b) das Gate bleibt
  human-only: dieselben 5 fremden Treffer bewegen `HumanLogTotal`/`TotalDeviation` nicht;
  (c) die drei Signaturen: ein Tag mit Log-Shared 5 und Live-Shared 0, aber gleich großer
  Gesamtsumme auf beiden Seiten (die Masse steckt live in `UseCount`), behält `Ratio` 1 —
  `CoverageQuestionable` false; (d) Symmetrie innerhalb der Toleranz (Log 95, Live 100 über die
  bewerteten Tage) ⇒ eligible; außerhalb (Log 80, Live 100) ⇒ ineligible mit genau
  `shared-chat-asymmetric`; (e) leerer Fall beide 0 ⇒ kein Grund; (f) Live 0, Log > 0 ⇒ Grund;
  (g) nur bewertete Tage zählen: Asymmetrie ausschließlich an Tagen vor dem Stichtag setzt keinen
  Grund; (h) `SharedChatByDay` führt jeden Log-Tag mit beiden Summen, sortiert, und
  `ComputeTwice_ReturnsEqualReports` deckt die neue Liste ab; (i) `Plausibility_ComparesBothSidesIncludingBots`
  wird zum Dreikomponenten-Fall; (j) `Diagnostics_AggregateThePerDayCounters` prüft zusätzlich
  `IndeterminateMessages`.
- [ ] **Step 2 (Tests zuerst, `HarnessRunnerTests`):** End-to-End: Tag 2 trägt eine fremde
  Nachricht mit Treffer und die Live-Zeile des Tags `SharedChatUseCount = 1` ⇒ `.report.json`
  enthält `"sharedChatLogTotal": 1`, `"sharedChatLiveTotal": 1`, kein `shared-chat-asymmetric`;
  mit `SharedChatUseCount = 0` an dem Tag ⇒ Grund gesetzt; das Markdown enthält die
  Spaltenköpfe „Shared Chat (Log)" und „Shared Chat (Live)".
- [ ] **Step 3: rot laufen lassen.** Filter `ReplayFidelityCalculatorTests|HarnessRunnerTests`;
  Expected: Compilerfehler (Records) bzw. FAIL (Summen).
- [ ] **Step 4: Calculator, Records, Markdown umstellen.**
- [ ] **Step 5: grün laufen lassen.** Worker-Testprojekt, dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 6:** Betrifft-Zeile; `dotnet format`; Nutzer fragen; Commit
  `feat(harness): report shared chat on both sides and require symmetry`.

**Fertig-Bedingung:** Backend-Suite grün; `BuildPopulation` ist im Diff nicht angefasst
(Docstring ausgenommen); `git grep -n "0.10\|MaxSharedChatAsymmetry"` zeigt genau eine Konstante;
`.report.json` eines Testlaufs trägt `sharedChatLogTotal`, `sharedChatLiveTotal`, `sharedChatByDay`.

**Ausdrücklich nicht:** kein viertes präregistriertes Gate, keine Änderung an den sechs
bestehenden Konstanten oder an den Rankings; keine Änderung an `ReplayDayCounter`.

**Modell: opus** — handgerechnete Erwartungswerte über drei Komponenten, bewertete gegen
unbewertete Tage und eine Eignungsbedingung mit Nullfällen; hier ist ein leiser Rechenfehler
teuer und schwer zu sehen.

---

### Task 8: Verifikation — Gates, Gegenprobe, Negativprobe live, Symmetrieprobe, Prod-Übergabe

**Files:** keine Code-Änderungen erwartet. Findet die Live-Verifikation einen Defekt, geht er
als eigener Fix-Task an einen Subagenten zurück — nicht „schnell hier".

**Vorab lesen:** Spec B6 vollständig (fünf Punkte, warum Punkt 2 der Nachweis ist,
Symmetrieprobe, Gegenprobe altes Image); CLAUDE.md „Tests", „Prod-Migration (manuell, über
SSH-Tunnel)"; `~/.claude/CLAUDE.md` „Fernzugriff (SSH)" — **nie selbst auf `vps` verbinden**,
Befehle werden dem Nutzer aufbereitet; die Memory-Notizen `feedback_prod_migration_handover`,
`project_prod_redeploy_stale_latest_image`, `project_coverage_local_needs_commit`,
`project_e2e_red_from_memory_pressure` (E2E läuft hier nicht, aber die Laufzeit-Regel gilt für
lange Backend-Läufe sinngemäß).

**Interfaces:** konsumiert alles aus Tasks 1–7; produziert die Übergabe an den Nutzer (Befehle,
Messwerte, SQL-Ausgaben), keinen Code.

- [ ] **Step 1 — Gates komplett:** `dotnet format EmotePurge.slnx --verify-no-changes`,
  `dotnet test EmotePurge.slnx` (Docker läuft), `npm --prefix web test -- --watch=false`
  (unverändert, aber Teil von „fertig"). **Kein E2E:** Zug 1 ändert weder Frontend noch
  i18n noch einen Api-Vertrag; es gibt nichts, was Playwright anders sähe.
- [ ] **Step 2 — Coverage-Vorabschätzung:** `node scripts/coverage-local.mjs --backend-only`
  **nach** dem letzten Commit — das Skript misst nur Committetes; „0 geänderte Datei(en)" ist
  eine Nichtmessung, keine Entwarnung. Erwartung: die Migration(en), die neuen Harness-Felder
  und die Regel sind neue Zeilen in gut gedeckten Dateien; unter 80 % heißt, ein Fall aus B7
  fehlt — zurück an den betreffenden Task.
- [ ] **Step 3 — Gegenprobe „altes Image gegen migriertes Schema"** (die Annahme hinter der
  Prod-Reihenfolge, Spec B3/B6). Die Dev-DB ist seit Task 2 (ggf. Task 4) migriert. Prüfen, ob
  die gecachten `emote-purge-dev-api`/`-worker`-Images noch der `main`-Stand sind
  (`docker image inspect --format '{{.Created}}' …` gegen das Datum von Task 1); wenn ja
  **bewusst ohne `--build`** `docker compose up -d api worker`. Nachweise: `GET /api/health`
  200; der alte Worker bootet (sein `IPendingMigrationGuard` sieht keine ausstehende Migration —
  die DB ist weiter als sein Build); nach ≥ 30 s Chat in einem gejointen Kanal loggt er keinen
  `Usage-Stat-Flush fehlgeschlagen`, und neue Zeilen tragen `SharedChatUseCount = 0` aus dem
  Spalten-Default. Sind die Images schon Branch-Stand: alten Stand aus
  `git worktree add <scratch>/main main` mit kopierter `.env` bauen und die Probe wiederholen.
- [ ] **Step 4 — neuer Stand hoch:** `docker compose up -d --build api worker` (Regel 15).
  Temporär `Logging__LogLevel__EmotePurge.Worker.TwitchChatManager=Debug` am Worker (zeigt
  Kanal, Username, Text jeder Nachricht). Die vier Kanäle `brudivoeller_tv`, `ronnyberger`,
  `knirpz`, `papaplatte` lokal joinen (Login unter `http://localhost:8080`, Join über die
  Oberfläche oder `POST /api/channels/{name}/join` mit der Session) — der Worker joint den
  **echten** Twitch-Chat von der Devbox aus. Das lokale Postgres ist die Quelle aller
  Nachweise; **kein VPS-Zugriff nötig** (ein Reviewer hatte das Gegenteil angenommen).
- [ ] **Step 5 — Negativprobe (Spec B6, Punkte 1–5), per `docker compose exec postgres psql -U emotepurge -d emotepurge`:**
  1. Solange in `brudivoeller_tv` oder `ronnyberger` eine Shared-Chat-Session läuft (auf Twitch
     sichtbar: „Stream Together"), **müssen** Zeilen mit `SharedChatUseCount > 0` entstehen —
     `SELECT` über `UsageStats` gejoint auf `Emotes`/`Channels` mit `SharedChatUseCount > 0`
     und heutigem Datum.
  2. In `knirpz` und `papaplatte` darf, solange sie live sind, **keine** Zeile mit
     `SharedChatUseCount > 0` entstehen, während `UseCount` wächst — derselbe `SELECT`, leer.
     Das ist der eigentliche Nachweis: eine vertauschte Regel besteht Punkt 1 und fällt hier
     durch. Voraussetzung: der 0-%-Kanal ist im Prüfzeitraum live; ein Kanal ohne Session
     beweist nichts. Ist keiner der 0-%-Kanäle live, einen anderen aktiven Kanal ohne Session
     nehmen und das im Bericht nennen.
  3. Keine Zeile wächst aus derselben Nachricht in zwei Kategorien: ein 30-s-Flushfenster im
     Debug-Log mit genau einer Nachricht, die ein Emote trifft, bewegt an dieser
     `(EmoteId, Date)`-Zeile genau eine der drei Spalten um 1.
  4. Kein Sprung in der Oberfläche (D5): Usage-Seite eines betroffenen Kanals vor Step 4 und
     danach — Summen, Bänder, Sortierung unterscheiden sich nur um das Gechattete; eine Zeile
     aus Punkt 1 mit `UseCount = 0` erscheint als benutzt (Screenshot in die Rückmeldung).
  5. Die Flush-Log-Zeile für unbestimmbare Nachrichten (Task 3) erscheint in keinem der Kanäle
     oder erklärt sich; ein Anschlagen ist ein Befund über Twitchs Tagsatz und gehört als
     Kommentar ins Issue, nicht als Fix in diesen Branch.
  Zusätzlich die Gefahrenstelle aus Task 3: im Admin-Monitoring läuft `LastMessageUtc` des
  Shared-Chat-Kanals weiter — der Watchdog sieht gespiegelte Nachrichten als Lebenszeichen.
- [ ] **Step 6 — Symmetrieprobe des Harness:** braucht mindestens zwei volle lokale UTC-Tage
  Tracking (`firstFullyTrackedDay` + ein abgeschlossener Tag). Deshalb den lokalen Worker mit
  Branch-Stand **so früh wie möglich** nach Task 3 durchlaufen lassen. Ist das erreicht:
  `HARNESS_SHARED_CHAT_CUTOVER` in `.env` auf den ersten vollen lokalen Tag setzen, dann
  `docker compose --profile harness run --build --rm harness brudivoeller_tv --days 1 --diagnostic`
  ⇒ im `.report.json` deckt sich `sharedChatByDay[…].logTotal` mit `liveTotal` bis auf
  Archiv-/Live-Lücken; dasselbe für `papaplatte` ⇒ beide 0. Reicht die lokale Messung nicht,
  wandert die Probe als Punkt 4 der Prod-Reihenfolge nach Prod (vom Nutzer, frühestens D+2 mit
  `--days 1 --diagnostic`) — das im Bericht ausdrücklich sagen, nicht still weglassen.
- [ ] **Step 7 — Prod-Übergabe an den Nutzer** (Befehle vorbereiten, **nicht** ausführen; die
  achtstufige Reihenfolge steht unter „Verifikation und Abschluss"): Tunnel und die drei
  `dotnet ef`-Aufrufe wörtlich aus CLAUDE.md mit `<PROD-PW>`-Platzhalter; Erwartung beim ersten
  `list`: genau die Migrationen dieses Branches als `(Pending)` (`AddUsageStatSharedChatUseCount`,
  plus `AddSharedChatUseCountToUsageStatIndex`, falls Task 4 sie erzeugt hat) — mehr heißt, Prod
  hängt hinterher; Portainer-Redeploy mit erzwungenem Pull; der Stichtag als
  `HARNESS_SHARED_CHAT_CUTOVER=<D+1>` im Prod-Stack; die Gegenproben-SELECTs; der
  Issue-Kommentar für #69 mit D, D+1, D+31, der Symmetriebedingung und dem Rollback-Verbot.
- [ ] **Step 8 — Rückmeldung** mit Messwerten (Task 4), SQL-Ausgaben aus Step 5, Screenshot aus
  Punkt 4, Harness-Report aus Step 6 (oder der Begründung, warum er nach Prod wandert), und
  den offenen Punkten. Kein Backlog-Eintrag: #73 ist ein Issue, der Stand steht am Issue.

**Fertig-Bedingung:** alle Gates grün; Coverage-Schätzung ≥ 80 % auf neuem Code; Gegenprobe
bestanden; Punkt 1 **und** Punkt 2 der Negativprobe belegt; Prod-Befehle und Issue-Kommentar
übergeben.

**Ausdrücklich nicht:** kein `ssh vps`, kein Tunnel, kein Prod-SELECT durch einen Agenten;
kein Produktivcode.

**Modell: opus** — Live-Debugging gegen Twitch, Postgres und das Log-Archiv mit
Interpretationsbedarf, kein Schreiben von Code.

---

### Task 9: Zweitmeinung vor dem Merge (Regel 22)

**Files:** keine.

**Interfaces:** konsumiert den fertigen Branch (sieben Commits, Tasks 1–7) und den Bericht aus
Task 8; produziert Findings für den Nutzer, keinen Code.

- [ ] **Step 1:** `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — alle
  Flags in **einem** String (das Plugin liest getrennte Argumente als Fokustext); `--scope
  branch` ausdrücklich (ohne ihn reviewt Codex den Working Tree und meldet bei sauberem Tree
  eine falsche Entwarnung). „Reviewer failed to output a response" mit Exit 1 ist das
  Kontingent, kein Absturz — Job-Log lesen, nicht neu starten. Einmal je Branch, nie zweimal für
  dasselbe unveränderte Diff.
- [ ] **Step 2:** Findings unverändert an den Nutzer. Widerspricht Codex einem Opus-Review
  (P1/P2, das die andere Seite nicht sieht; gegensätzliche Bewertung derselben Stelle;
  unvereinbare Fixes), entscheidet **Fable** als Schiedsrichter — nur die strittigen Findings
  plus die betroffenen Stellen, nicht das ganze Diff. Reine Ergänzungen sind kein Widerspruch.
- [ ] **Step 3:** Merge auf `main` und Push macht der Nutzer; danach die Prod-Reihenfolge.

**Fertig-Bedingung:** Review-Ergebnis liegt vor und ist an den Nutzer weitergegeben; Fixes aus
Findings laufen als eigene Subagent-Tasks mit erneutem Gate-Lauf, nicht als Nachbesserung im
Review-Schritt.

**Ausdrücklich nicht:** kein `/codex:rescue` für Review-Zwecke; kein stilles Übernehmen einer der
beiden Meinungen bei Widerspruch.

**Modell:** (Codex Sol; Fable nur als Schiedsrichter)

---

## Verifikation und Abschluss

Kein Task, sondern der Maßstab, an dem alle Tasks gemessen werden.

**„Fertig" heißt in diesem Branch:**

- `dotnet test EmotePurge.slnx` grün — braucht laufendes Docker (Testcontainers für Postgres und
  Redis in `Infrastructure.Tests`; `Worker.Tests` und `Api.Tests` bleiben container-frei).
- `npm --prefix web test -- --watch=false` grün — Zug 1 ändert das Frontend nicht, die Suite
  läuft trotzdem einmal, damit „fertig" dieselbe Bedeutung hat wie in jedem anderen Branch.
- **E2E (`npm --prefix web run e2e`) entfällt in Zug 1 — mit Absicht, nicht aus
  Vergesslichkeit:** keine UI-Änderung, keine i18n-Schlüssel, kein Api-Vertrag (Spec B4/B7). Es
  gibt nichts, was Playwright anders rendern könnte; ein Lauf wäre Zeit ohne Aussage. Zug 2
  (Toggle/Hinweis) bringt E2E zurück — dann nur ohne laufende Api auf `:5151`.
- **Vor dem PR:** `node scripts/coverage-local.mjs --backend-only` — **erst committen, dann
  laufen lassen**, das Skript misst nur Committetes; „0 geänderte Datei(en) … nichts zu
  bewerten" mit Exit 0 ist eine Nichtmessung. Das Gate (80 % auf neuem Code, `analyze` ist
  required check) beißt, wenn Integrations- oder Harness-Fälle aus B7 fehlen. Das Ergebnis ist
  eine dateigenaue Näherung, in beide Richtungen unscharf — Anlass hinzusehen, kein Urteil.
- **Regel 16, Live-Verifikation — lokal auf der Devbox, ohne VPS:** Worker aus dem Branch-Stand
  gegen das lokale Postgres/Redis, gejoint in die **echten** Twitch-Kanäle. `brudivoeller_tv`
  (84 % Shared Chat) **muss** Zeilen mit `SharedChatUseCount > 0` erzeugen; `papaplatte` (0 %)
  darf **keine einzige** erzeugen, während `UseCount` dort wächst — Punkt 2 ist der Nachweis,
  Punkt 1 besteht auch eine vertauschte Regel. Alles davon ist in der lokalen Datenbank
  beobachtbar; **kein SSH, kein Tunnel, kein Prod-Zugriff nötig** (ein Reviewer hatte das
  Gegenteil angenommen). Details in Task 8.
- **Vor dem Merge:** `/codex:review --model gpt-5.6-sol --scope branch --base origin/main`
  (Task 9). Widerspruch zwischen Opus-Review und Codex entscheidet Fable.
- **Regel 1** gilt für jeden der sieben Commits.

**Prod-Reihenfolge, achtstufig (aus der DoD der Spec):**

1. **Migration von Hand, vor dem Deploy.** Über den SSH-Tunnel und die drei `dotnet ef`-Aufrufe
   aus CLAUDE.md „Prod-Migration" (`migrations list` → `database update` → `migrations list`),
   `--connection` mit `<PROD-PW>`-Platzhalter, in der Shell des Nutzers. **Der Nutzer führt sie
   aus, nicht ein Agent** — Agenten bereiten die Befehle vor. Erwartung beim ersten `list`:
   genau die Migrationen dieses Branches als `(Pending)`; mehr heißt, Prod hängt hinterher.
   Das alte Image läuft dabei weiter und schreibt ohne die Spalte — dafür der Spalten-Default.
2. **Images deployen** (Portainer, Redeploy mit erzwungenem Pull — `:latest` zieht sonst nicht
   neu). Das ist **Tag D**. Der Deploy-Tag ist gemischt (Vormittag alt, Nachmittag neu) und
   fällt strukturell aus der Bewertung.
3. **Stichtag D+1 setzen:** `HARNESS_SHARED_CHAT_CUTOVER=<D+1>` (UTC, `yyyy-MM-dd`) in der
   Prod-Stack-Konfiguration des `harness`-Dienstes. Ohne ihn bricht jeder bindende Lauf mit
   Exit 3 ab — das ist gewollt.
4. **Gegenprobe in Prod** (B6 wiederholt, read-only SELECTs vom Nutzer): Zeilen mit
   `SharedChatUseCount > 0` in den 84-%-/66-%-Kanälen, keine in den 0-%-Kanälen; Oberfläche
   ohne Sprung; frühestens D+2 ein Diagnoselauf `--days 1 --diagnostic` über dieselben Kanäle
   als Symmetrieprobe, falls sie lokal nicht möglich war.
5. **Symmetriebedingung in #69 nachregistrieren** — als Kommentar am Issue, mit dem Wortlaut
   der Regel (bewertete Tage, beide Summen, 10 %, Grund `shared-chat-asymmetric`), **vor** dem
   bindenden Lauf; sonst wäre sie eine nach dem Lauf erfundene Regel.
6. **Rollback-Verbot** für das Worker-Image ab D bis zum Ende des bindenden Laufs. Passiert es
   doch: Lauf ungültig, Stichtag neu auf den Tag nach dem erneuten Deploy, Uhr neu. Die
   Tagestabelle des Berichts (drei Signaturen) macht einen solchen Tag im Nachhinein sichtbar,
   verhindert ihn aber nicht.
7. **Frühester bindender Lauf D+31**, ohne `--diagnostic`, mit gesetztem Stichtag, 30 Tage.
8. **Zug 2 einplanen** — als Termin im Issue, nicht als Absicht. Bis dahin steht die
   Übergangssumme, und `SharedOnlyRow_ReadsAsUsedUntilZug2` ist grün — der Beleg, dass die
   Brücke noch drin ist.

D, D+1, D+31, das Rollback-Verbot und der Zug-2-Termin gehören als **ein Kommentar in #69**,
nicht ins Gedächtnis.

## Freeze-Liste

Unverändert von Zug 1 bis zum Ende des bindenden Laufs, mit Absicht. Wer eines davon anfassen
muss, bumpt `AlgorithmVersion`, setzt den Stichtag neu und die Uhr zurück.

| Eingefroren | Warum |
|---|---|
| `EmoteNameMatching` (Core) | die geteilte Matching-Regel beider Seiten; jede Änderung misst sich selbst |
| `BotChatterDetector`, die statische Bot-Liste, `Twitch:AdditionalBotAccountIds` | die ID-Liste steht in der Identität — **eine Bot-Heuristik ohne ID-Bezug (Badges, Nachrichtenmuster) täte es nicht**: sie änderte die Zählung, ohne Berichtskopf oder `InputHash` zu bewegen; der Harness verbuchte sie als Genauigkeitsverlust |
| `BotSplitCutover` samt `GetEarliestBotUsageDateAsync` | bleibt in Identität, Kopf und Berichtszeile; wird für das Gate nicht mehr gebraucht, aber nicht entfernt |
| die präregistrierten Schwellen und Konstanten des Calculators (`RequiredWindowDays`, `RequiredRatedDays`, `TopSize`, `RequiredQualifiedEmotes`, `MinLiveUsesPerThirtyDays`, `MinLiveUsesFloor`, 10 % / 0,9 / 0,8) | publiziert in #69; `MaxSharedChatAsymmetry` kommt einmal dazu (Task 7) und ist danach ebenfalls eingefroren |
| die Tagesgrenzen (UTC-Tag, `to = heute − 1`, `from = trackedSince + 1`) | Teil der Zählregel |
| die `.report.json`-/`.jsonl`-Feldnamen (camelCase, `WhenWritingNull`), inklusive der neuen `sharedChatCounts`, `indeterminateMessageCount`, `sharedChatCutover`, `sharedChatLogTotal`, `sharedChatLiveTotal`, `sharedChatByDay`, `diagnostic` | ein Lesender vergleicht Berichte über Wochen; `LogTotalWithBots`/`LiveTotalWithBots` behalten deshalb auch ihren jetzt ungenauen Namen |
| **`TwitchLib.Client` 4.0.1 und `TwitchLib.Communication` 2.0.1** | **die stille Falle:** ein Update könnte `source-room-id` (oder einen der drei anderen Marker) von „undocumented" zu **typisiert** machen. Dann stünde der Wert nicht mehr in `UndocumentedTags`, `SharedChatRule.FromTags` sähe ihn nicht, jede Session-Nachricht würde „eigen" — und **weder Berichtskopf noch Input-Hash bewegten sich**. Die TwitchLib-gebundenen Fixtures (Task 1) würden rot, aber nur, wenn jemand sie laufen lässt, bevor das Image gebaut ist. Deshalb der Dependabot-`ignore` für `TwitchLib.*` (Task 1) — er hängt nicht am Gedächtnis. Entfernungsauslöser: Ende des bindenden Laufs |

## Was Zug 2 erbt

Nichts davon wird in Zug 1 entschieden oder gebaut; es steht hier, damit Zug 1 keine Tür
zuschlägt — und weil D5 Zug 2 zur **Pflicht** macht.

- **Der Entfernungsauslöser für die D5-Übergangssumme.** Erster Task von Zug 2: Summen und
  Filter in `GetUsageContextAsync`, `GetDailySeriesAsync`, `GetChannelSeriesAsync`,
  `GetTotalsByEmoteIdsAsync` zurück auf `UseCount` allein. Der Test
  `UsageStatQueryServiceTests.SharedOnlyRow_ReadsAsUsedUntilZug2` (Task 4) wird dabei **bewusst
  gebrochen und umgedreht** (Shared-only-Zeile liest sich als unbenutzt, wie Bot-only) — er ist
  der Marker, nicht ein Kommentar. Dieser Task geht **nicht ohne** den zweiten (Anzeige), sonst
  gibt es den unerklärten Sturz, den D5 vermeidet. Die Index-Entscheidung aus Task 4 wird dabei
  nachgezogen: ein befristetes `INCLUDE` wird zurückgebaut, es sei denn, Zug 2 baut eine
  Drilldown-Serie über die Spalte.
- **Toggle gegen reinen Hinweissatz — offen.** Das Issue skizziert einen Toggle; E3 und die
  Frontend-Zurückhaltung sprechen für einen Hinweis nach dem Muster der Bot-Caption
  (`bots-excluded-caption.ts`). Unterschied zu #31: bei 84 % Fremdanteil ist die fremde Zahl für
  einen Manager vielleicht tatsächlich interessant.
- **`SharedChatSeparatedSince`** als datenabgeleitetes Datum erbt die E4-Unschärfe und den
  0-%-Sonderfall (dauerhaft `null`, obwohl die Trennung gilt); ob Zug 2 die Ableitung nimmt oder
  das Deploy-Datum aus D4 wiederverwendet, ist dann zu entscheiden. Mit D5 meint die Caption das
  Deploy von **Zug 2**, nicht das von Zug 1. Ein `GetEarliestSharedChatUsageDateAsync` kommt erst
  dort.
- **Eigene Drilldown-Serie** über `SharedChatUseCount` — dann Covering-Index dauerhaft neu bewerten.
- **Darstellung** `12× (+3 Shared Chat)` neben der Karte; **Bänder und Sortierung** rechnen nach
  Zug 2 nur mit eigener Nutzung — Konsequenz aus dem Zielvertrag, keine offene Frage.
- **E2E kehrt zurück**, und mit ihm die `:5151`-Regel.

## Tests der Spec → Task-Zuordnung

| Spec-Zeile (B7) | Task | Datei |
|---|---|---|
| geteilte Regel, pur: kein Marker · gleich · ungleich · leer wie fehlend · `room-id` fehlt · Nebenmarker ohne `source-room-id` | 1 | `Infrastructure.Tests/Unit/SharedChatRuleTests.cs` |
| Live-Extraktion, pur: `null`-Dictionary · Schlüssel fehlt · Schlüssel vorhanden · nur Nebenmarker | 1 | `Infrastructure.Tests/Unit/SharedChatRuleTests.cs` (`FromTags`) |
| Live-Extraktion, TwitchLib-gebunden: voller `source-*`-Satz · ohne einen unbekannten Tag · Marker ohne `source-room-id` | 1 | `Worker.Tests/SharedChatRuleTwitchLibTests.cs` |
| `JustlogRawLineParser`: Marker gesetzt/nicht · `source-room-id` leer bei Markern | 1 | `Infrastructure.Tests/Unit/JustlogRawLineParserTests.cs` |
| Zähler: drei Kategorien · Vorrang fremd vor Bot, unbestimmbar → Shared Chat · `Merge` · Drain · `PendingEmoteCount` shared-only | 3 | `Worker.Tests/EmoteUsageCounterTests.cs`, `WorkerStatsTests.cs` |
| `HarnessInputHash`: dritte Spalte bewegt den Hash | 3 | `Worker.Tests/HarnessInputHashTests.cs` |
| Upsert: dritte Spalte akkumuliert · Konflikt addiert in alle drei · Batch nur fremd | 2 | `Integration/UsageStatFlushServiceTests.cs` |
| `GetRowsAsync` liefert die dritte Spalte roh | 2 | `Integration/UsageStatQueryServiceTests.cs` |
| Übergangstest D5: Shared-only gilt als benutzt, Summen enthalten n · Bot-only weiter unbenutzt | 4 | `Integration/UsageStatQueryServiceTests.cs` (`SharedOnlyRow_ReadsAsUsedUntilZug2`) |
| `ReplayDayCounter`: Umkehr von `SharedChatMessage_IsCountedAndMarked` · fremder Bot · fremder Chatter · unbestimmbar | 5 | `Worker.Tests/ReplayDayCounterTests.cs` |
| `HarnessReportFile`: Round-Trip Tageszeile | 5 | `Worker.Tests/HarnessReportFileTests.cs` |
| `HarnessReportFile`: Round-Trip Kopf (Stichtag) | 6 | `Worker.Tests/HarnessReportFileTests.cs` |
| `HarnessCommandLine`: Diagnosemodus erkannt · alles andere Exit 2 | 6 | `Worker.Tests/HarnessCommandLineTests.cs` |
| `HarnessRunner`: `harness-1` nicht wiederaufgenommen · fensterweite Chatter nur eigene Menschen · Tageszeile mit neuen Feldern | 5 | `Worker.Tests/HarnessRunnerTests.cs` |
| `HarnessRunner`: fehlender/ungültiger Stichtag ⇒ Exit 3 ohne Bericht · Diagnose ohne Stichtag läuft ohne Urteil · anderer Stichtag ⇒ andere Identität · Berichtszeile | 6 | `Worker.Tests/HarnessRunnerTests.cs` |
| `HarnessRunner`: Shared-Chat-Summen beider Seiten End-to-End, Symmetriegrund | 7 | `Worker.Tests/HarnessRunnerTests.cs` |
| Calculator: `HumanOnly` allein aus dem Shared-Chat-Stichtag · `BotSplitCutover = null` sperrt nichts | 6 | `Worker.Tests/ReplayFidelityCalculatorTests.cs` |
| Calculator: Tagessumme drei Komponenten · Gate human-only · Summen beider Seiten · drei Signaturen ändern `Ratio` nicht · Symmetrie innerhalb/außerhalb · leerer Fall | 7 | `Worker.Tests/ReplayFidelityCalculatorTests.cs` |
| keine Api-Testfälle | — | keine Route, kein Filter, keine Reihenfolge |
| kein E2E | — | keine UI-Änderung |

## Selbstprüfung (beim Schreiben dieses Plans)

- **Spec-Deckung:** B1 → Task 1 (Regel, Null-Sicherheit, Fixtures, unbestimmbar) + Task 3
  (Vehikel des Live-Zählers); B2 → Task 3 (Enum, Zähler) + „keine Defaults" in jedem
  Record-Task (2, 5, 6, 7); B3 → Task 2 (Spalte, Migration, Upsert, Entity-Kommentar) + Task 4
  (Index-Frage per `EXPLAIN`); B4 → Task 2 (Rohzeilen, DTO) + Task 3 (Hash) + Task 4 (D5);
  B5 → Task 5 (Nachrichtenebene, Chatter-Zahlen, `harness-2`) + Task 6 (`HumanOnly`) + Task 7
  (Symmetrie); D3 → Task 7; D4 → Task 6; D5 → Task 4 mit Übergangstest; B6 → Task 8; B7 →
  Tabelle oben; B8 → Task 1 (Dependabot) + Freeze-Liste; DoD → „Verifikation und Abschluss";
  „Zug 2" → „Was Zug 2 erbt". Alle sechs offenen Fragen der Spec sind unter „Entscheidungen
  dieses Plans" beantwortet (1 → Nr. 1/2, 2 → Nr. 11, 3 → Nr. 4, 4 → Nr. 5, 5 → Nr. 6/7/8/9,
  6 → Nr. 10).
- **Keine Platzhalter:** jeder Konfigschlüssel, jede Konstante, jeder Grund, jeder Feldname und
  jede Signatur ist benannt; kein „analog zu Task N", kein „geeignete Fehlerbehandlung".
- **Namenskonsistenz über die Tasks:** `SharedChatRule.Classify/HasOtherSourceMarkers/FromTags`
  und `MessageOrigin { Own, Foreign, Indeterminate }` (1 → 3 → 5); `ChatLogMessage.HasOtherSourceMarkers`
  (1 → 5); `EmoteUsageCounts(Human, Bot, SharedChat)` (2 → 3); `UsageStat.SharedChatUseCount`
  (2 → 4 → 8); `UsageStatRowDto(…, SharedChatUseCount)` (2 → 3 → 5); `UsageCategory { Human, Bot, SharedChat }`
  und `UsageCategoryRule.Resolve` (3 → 5); `ReplayDayLine.SharedChatCounts`/`IndeterminateMessageCount`
  (5 → 7); `ReplayUsageRow.SharedChatUseCount` (5 → 7); `AlgorithmVersion = "harness-2"` (5 → 6);
  `Harness:SharedChatCutover` / `HARNESS_SHARED_CHAT_CUTOVER` / `HarnessOptions.SharedChatCutover`
  (6 → 8); `--diagnostic` / `RunHarness(…, bool Diagnostic)` / `RunAsync(…, bool diagnostic, …)`
  (6 → 8); `ReplayWindow.SharedChatCutover`, `ReplayRunInfo.Diagnostic`, `diagnostic-run`
  (6 → 7); `shared-chat-asymmetric`, `SharedChatLogTotal`/`SharedChatLiveTotal`, `SharedChatByDay`
  (7 → 8); `SharedOnlyRow_ReadsAsUsedUntilZug2` (4 → Zug 2); Migration
  `AddUsageStatSharedChatUseCount` (2 → 8).
- **Kein fertiger Code:** Signaturen als Einzeiler, SQL- und Query-Absichten in Worten,
  Migrations-Inhalt als Prüfbedingung, Tests als Aussagen in einem Satz. Die IRC-Fixture-Zeilen
  sind als Tag-Listen beschrieben, nicht ausgeschrieben.

## Anmerkungen zur Spec (beim Lesen des Codes aufgefallen)

Keine davon ändert eine Entscheidung; sie stehen hier, damit sie nicht als stille Abweichung im
Task landen.

1. **„Dieselbe 10-%-Konstante" existiert im Code nicht.** Die Spec (D3/B5) sagt, die
   Symmetrietoleranz sei „dieselbe 10-%-Konstante, keine neue Schwelle". Im Calculator gibt es
   keine Konstante für die Abweichungsschwelle — sie steht nur im Klassen-Docstring und im Issue,
   weil die Klasse bewusst kein Urteil berechnet. Die Eignungsbedingungen dagegen tragen ihre
   Zahlen als Konstanten (`RequiredRatedDays`, `RequiredWindowDays`). Der Plan führt deshalb
   `MaxSharedChatAsymmetry = 0.10` als **Eignungskonstante** ein (Nr. 8) — derselbe Wert, aber
   eine neue Zeile Code, was die Spec so nicht vorhergesehen hat.
2. **Die Spec schreibt die Extraktion `TwitchChatManager` zu, der Plan teilt den
   Dictionary-Gang.** B1 sagt „Die Tag-Extraktion bleibt seitenlokal … in `TwitchChatManager`".
   Der Plan liest die Properties dort, legt aber den null-sicheren Dictionary-Gang in
   `SharedChatRule.FromTags`, weil sonst die in B7 verlangten puren Tests der Live-Extraktion
   keinen Prüfgegenstand hätten (Nr. 2). Absicht der Spec (Null-Sicherheit, ein Vertrag) bleibt
   gewahrt; der Ort ist anders.
3. **`HarnessOptions` wird vor jeder Exit-Code-Behandlung gebunden.** D4 verlangt Exit 3 bei
   unparsebarem Stichtag. Ein `DateOnly?`-Property würde beim `Bind` in `AddHarness` werfen —
   vor `RunAsync`, aus `Main` heraus. Deshalb `string?` und Parsen im Runner (Nr. 6). Die Spec
   hat den Ort nicht festgelegt, aber die naive Umsetzung wäre falsch gewesen.
4. **B6 Punkt 3 ist live nur indirekt belegbar.** „Dieselbe Zeile darf nicht in zwei Kategorien
   aus derselben Nachricht wachsen" lässt sich in echtem Chat nur über ein isoliertes
   30-s-Fenster mit genau einer treffenden Nachricht zeigen (Task 8, Step 5.3); der eigentliche
   Beweis ist strukturell — `UsageCategoryRule.Resolve` liefert genau einen Wert, und
   `Increment` erhöht genau eine Komponente (Task 3, Tests).
5. **Die lokale Symmetrieprobe braucht Kalendertage.** B6 nennt den Diagnoselauf als Teil der
   lokalen Verifikation; der Harness verlangt aber mindestens einen vollständig gemessenen
   lokalen UTC-Tag nach dem Join-Tag. Der Plan verlangt deshalb, den lokalen Worker früh
   durchlaufen zu lassen, und benennt Prod (D+2, vom Nutzer) als Ausweichort (Task 8, Step 6).
6. **Der `ReplayDayLine`-Docstring „A shared-chat message also still counts"** und der
   `BuildPopulation`-Docstring „what the grid actually shows" sind nach Zug 1 falsch; beide werden
   umgeschrieben (Task 5 bzw. 6), was die Spec für den zweiten ausdrücklich verlangt und für den
   ersten nicht erwähnt.
