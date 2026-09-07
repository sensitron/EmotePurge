# Zwei Erfassungsfehler im Worker: Kaltstart-Zähllücke und TwitchLib-Doppelschleife — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code**
> (globale Regel, s. `~/.claude/CLAUDE.md`): jeder Task beschreibt Absicht, Verträge, Grenzfälle
> und Abnahme — Methodenrümpfe und Testkörper entstehen im Task selbst. **Der Plan wird in einem
> unbeaufsichtigten Nachtlauf ausgeführt:** jede Wahl, die offen sein könnte, ist hier getroffen
> und in einem Satz begründet; wer beim Umsetzen auf eine ungeplante Weggabelung stößt, nimmt den
> Weg, der weniger Dateien anfasst, und notiert die Abweichung im Commit-Body.

**Goal:** Ein Kanal zählt ab dem Moment, in dem der Bot seinen Chat betritt — auch wenn 7TV beim
ersten Abruf nach einem Worker-Neustart nicht antwortet (Defekt 1, kein Issue). Und ein
TwitchLib-`RECONNECT` hinterlässt keinen Client mit zwei Lese-Schleifen mehr, der IRC-Zeilen
spleißen und Nutzung dem falschen Kanal zuschreiben kann (Defekt 2, Issue #114). Beide Änderungen
sind **taktneutral** im Sinn von Spec D1 zu #73: Klassifikation, Matching und Flush bleiben
unverändert, die Zählregel wird nur früher hergestellt bzw. vor gefälschten Zeilen geschützt.

**Architecture:** Vier kleine Bausteine, kein neuer Transportweg, kein neues Interface in `Core`.
**(B1)** `SevenTvSyncService.SyncChannelAsync` füllt den `EmoteMatchCache` für einen Kanal aus den
aktiven Postgres-Zeilen, **bevor** es 7TV fragt — nur wenn der Cache für den Kanal leer ist, unter
demselben Kanal-Gate wie der Sync selbst, über die vorhandene `RefreshMatchCacheAsync`. **(B2)**
`ReconnectPolicy` bekommt einen Zustand „Client verbraucht", gesetzt aus `OnReconnected`, gelöscht
nur durch `RegisterClientReplaced`; `Decide` liefert dann `Recreate`. **(B3)**
`TwitchWatchdogPolicy` erzwingt bei verbrauchtem Client einen Tick, der über den bestehenden Pfad
`ForceReconnectAsync → RecreateClientAsync` läuft. **(B4)** Ein reiner Sentinel erkennt eine
gespleißte Zeile an einem zweiten `@` im Tag-Block der Rohzeile; Treffer werden gewarnt und in
`WorkerStats` gezählt, **nie verworfen**.

**Tech Stack:** .NET 10 Worker Service, TwitchLib.Client 4.0.1 / TwitchLib.Communication 2.0.1
(eingefroren), EF Core/Npgsql, xUnit + NSubstitute + Testcontainers. Kein Frontend-Anteil, keine
Migration, keine API-Änderung.

**Spec:** Kein eigenes Spec-Dokument. Grundlage sind GitHub-Issue **#114** (Mechanismus der
Doppelschleife, vollständig) und der Befund unten. Der Lösungsweg für #114 ist Ergebnis eines
Schiedsspruchs und wird **nicht** neu aufgerollt.

## Befund, am Code verifiziert (2026-09-08, `main` auf `f308d18`)

| Ort | Befund |
|---|---|
| `src/EmotePurge.Infrastructure/Services/EmoteMatchCache.cs` | Rein In-Memory, `ConcurrentDictionary` je normalisiertem Kanalnamen; `GetChannelEmotes` liefert für unbekannte **und** leere Kanäle dasselbe `Empty`. |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:367` | Einziger Aufrufer von `ReplaceChannel` ist `RefreshMatchCacheAsync` (:334-368), erreichbar nur nach erfolgreichem Vollsync (:145) oder angewendetem Delta (:260). Sie liest `db.Emotes` mit `!IsArchived`, koalesziert per `EmoteNameMatching.Coalesce`, pflegt den `DuplicateEmoteNameTracker` und schreibt unter `channel.ChannelName`. |
| `SevenTvSyncService.SyncChannelAsync` :20-154 | Reihenfolge: Namens-Gate :26 → `LoadChannelAsync` :28 → Row-Gate mit Re-Read :35 → ggf. `ResolveTwitchUserIdAsync` (**7TV-Call**) :50 → `GetChannelStateForTwitchUserAsync` (**7TV-Call**) :74 → Guards → Reconcile :143 → `SaveChanges` :144 → `RefreshMatchCacheAsync` :145. Jeder Fehlpfad endet in `RecordFailedAttemptAsync` und `return null`, ohne den Cache anzufassen (:95-101 begründet die Asymmetrie). Beide 7TV-Calls laufen mit 10-s-Timeout (`ServiceCollectionExtensions.cs:74`). |
| `src/EmotePurge.Worker/Worker.cs:80-98` | Boot-Recovery joint (:85) und synct (:86) je Kanal sequenziell; JOIN- (:37-38) und RESYNC-Kommando (:54-55) ebenso. `SevenTvPeriodicResyncWorker` (Default 60 s) ist der einzige Wiederholer, ohne Backoff. |
| `src/EmotePurge.Worker/TwitchChatManager.cs:509-513` | `OnMessageReceived` steigt bei leerem Set aus, **nach** der Watchdog-Buchführung (:500-504) — die Lücke ist für Watchdog und Health unsichtbar. |
| `TwitchChatManager.cs:421-432`, `OnReconnected` | Setzt `_isConnected`, `RegisterConnected()`, loggt; **kein** Rejoin (TwitchLib hat ihn inline schon getan, s. DECISIONS 2026-07-24/-30). Läuft laut #114 innerhalb der Lese-Schleife L1. |
| `TwitchChatManager.cs:100-134, 213-249` | `ForceReconnectAsync` entscheidet über `_reconnectPolicy.Decide(openRunningFor)` zwischen `Wait`/`Recreate`/`Reconnect`; `RecreateClientAsync` unwired den alten Client, `DisconnectAsync` best-effort, neuer `WebSocketClient` (genau eine Schleife), `RegisterClientReplaced()`, Rejoin nur über `OnConnected` des frischen Clients (`_joinsIssuedForCurrentClient`). |
| `src/EmotePurge.Worker/ReconnectPolicy.cs` | Pur, uhrfrei, zwei `Interlocked`-Zähler; `Decide`: Fehlerserie ≥ 3 → `Recreate`; kein Open in Flight → `Reconnect`; Open < 10 min → `Wait`; sonst `Recreate`. Tests in `tests/EmotePurge.Worker.Tests/ReconnectPolicyTests.cs`. |
| `src/EmotePurge.Worker/TwitchWatchdogPolicy.cs`, `TwitchConnectionWatchdog.cs` | `Decide(isConnected, sinceOpenAttempt, sinceLastFrame, sinceLastForcedReconnect)`; Tick jede Minute; bei gesunder Verbindung (Frames < 15 min) **nie** ein `ForceReconnectAsync` — ein verbrauchter Client würde heute nie ersetzt. Tests in `TwitchWatchdogPolicyTests.cs`. |
| `src/EmotePurge.Worker/WorkerStats.cs:322-392`, `UsageFlushWorker.cs:442-448` | Vorbild für einen Hot-Path-Zähler: `Interlocked`-`long`, `Record…()` ohne Log, `Take…SinceLastFlush()` per `Exchange`, Ausgabe einmal je Flush **nur bei > 0**. Tests in `WorkerStatsTests.cs`. |
| TwitchLib.Client 4.0.1, `ChatMessage` | Trägt die Rohzeile als `RawIRC` (Metadaten-Probe am installierten Paket: `get_RawIRC`/`set_RawIRC`; Name beim Umsetzen per Compiler bestätigen). `UndocumentedTags` ist `null` ohne unbekannte Tags (belegt in `SharedChatRuleTwitchLibTests`). |
| `src/EmotePurge.Worker/appsettings.json` | `TwitchLib.Client.TwitchClient` auf `Error` — Fragmente (`Unknown`/„Unaccounted for") sind im Normalbetrieb unsichtbar. |
| `.github/dependabot.yml` | `ignore` für `TwitchLib.*` bis zum Ende des bindenden #69-Laufs (2026-10-08). |
| Freeze-Liste (`docs/superpowers/plans/2026-09-06-shared-chat-zaehlung-73.md`, „Freeze-Liste") | `EmoteNameMatching`, `BotChatterDetector` + Bot-Liste, `BotSplitCutover`, Calculator-Schwellen, Tagesgrenzen, Report-Feldnamen, `TwitchLib.*`-Versionen. **Keine** der in diesem Plan geänderten Dateien steht darauf; `EmoteNameMatching.Coalesce` wird nur wie bisher *aufgerufen*. |

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Nachtlauf, keine Rückfragen, kein `git push`.** Arbeit auf Branch `fix/worker-erfassungsfehler-114`
  ab `main`. Ein Commit je Task (Conventional Commits, englisch), **lokal**; die CI läuft nicht.
  Regel 1 (Rückfrage vor Commit) ist für diesen Lauf durch die Freigabe des Plans abgedeckt;
  Regel 16 (Live-Verifikation vor Commit) ist im Nachtlauf **nicht** erfüllbar und wird durch
  Task 6 als Merge-Gate nachgeholt — der Branch wird bis dahin nicht gemergt und nicht gepusht.
- **Regel 3:** Beide Topologie-/Vertragsänderungen tragen ihren `docs/DECISIONS.md`-Eintrag **im
  selben Commit** (Task 1 und Task 3). Form: `### 2026-09-08 — <Titel>` + `**Betrifft:**`-Zeile,
  neuester Eintrag direkt unter dem `---` im Kopf der Datei. Kein Eintrag für Task 2 und 4 (reine
  Logik bzw. Log-only-Zähler ohne Vertrag).
- **Regel 5 / Schichtentreue:** Kein neuer `AppDbContext`-Zugriff im Worker. Der Warmstart lebt
  in `Infrastructure`, wo `AppDbContext` und `IEmoteMatchCache` schon injiziert sind; `Core`
  bleibt unberührt.
- **Regel 11:** Reine Worker-Logik (`ReconnectPolicy`, `TwitchWatchdogPolicy`, `WorkerStats`,
  der Sentinel) wird in `tests/EmotePurge.Worker.Tests` getestet; `SevenTvSyncService` in
  `tests/EmotePurge.Infrastructure.Tests/Integration` (Testcontainers). **`TwitchChatManager`,
  `TwitchConnectionWatchdog` und `UsageFlushWorker` bekommen keine Fake-Tests** — ihre neuen
  Zeilen bleiben Ein-Zeilen-Delegationen an getestete Klassen.
- **Regel 15/16:** Kein `docker compose up` im Nachtlauf nötig; Live-Verifikation ist Task 6.
- **Regel 18/19:** `dotnet format EmotePurge.slnx` vor jedem Commit; Member-Reihenfolge wie im
  Bestand (Konstanten → Felder → Properties → öffentliche Methoden → private).
- **Sprache:** Bezeichner und Kommentare englisch, Log-Texte deutsch, DECISIONS deutsch.
- **Freeze:** Kein Versionswechsel von `TwitchLib.*`; keine Änderung an Klassifikation,
  Matching, Flush-SQL, Harness. Eine gespleißte Zeile wird **gezählt wie heute** — nur zusätzlich
  markiert.

## Entscheidungen dieses Plans

- **E1 — Warmstart im Sync-Service, nicht im Worker.** Ein Aufruf in `SyncChannelAsync` deckt
  Boot-Recovery, JOIN, RESYNC, periodischen Resync und den Gap-Filling-Sync des EventAPI-Clients
  auf einmal ab, läuft unter dem Kanal-Gate (kein Wettlauf mit einem Sync) und braucht kein neues
  Core-Interface. Verworfen: ein Aufruf in `Worker.RunBootRecoveryAsync` plus JOIN-Pfad (zwei
  Stellen, neues Interface, ungegated).
- **E2 — Warmstart vor dem ersten 7TV-Call, nicht erst im Fehlerpfad.** Beide 7TV-Calls können
  10 s hängen; ein Warmstart erst nach dem Timeout verlöre genau diese Sekunden im lautesten
  Moment (Boot). Vor dem Call kostet er eine Postgres-Abfrage, die der Sync ohnehin gleich macht.
- **E3 — Bedingung ist „Cache leer für diesen Kanal", nicht „Prozess frisch".** Damit gilt die
  bestehende Asymmetrie (gefüllter Cache wird bei Sync-Fehler nie angefasst) ohne zweiten
  Zustand; ein Kanal mit legitim null aktiven Emotes zahlt eine Abfrage pro Tick — akzeptiert.
- **E4 — Der verbrauchte Client wird vom Watchdog-Tick ersetzt, nicht inline.** Vorgabe des
  Schiedsspruchs; der Versatz von bis zu 60 s trennt TwitchLibs ungedrosselten Rejoin von unserem
  gedrosselten (JOIN-Limit, `TwitchChatManager.cs:28-39`).
- **E5 — Jedes `OnReconnected` verbraucht den Client, auch das aus unserem eigenen
  `ReconnectClientAsync`.** Der Doppelschleifen-Mechanismus hängt am Objekt, nicht am Auslöser;
  „Reconnect" wird damit faktisch zu „Reconnect, einen Tick später Recreate". Das ist die
  Topologie-Aussage des DECISIONS-Eintrags.
- **E6 — Sentinel liest die Rohzeile (`RawIRC`), nicht `UndocumentedTags`.** Liegt der Schnitt im
  Wert eines *typisierten* Tags (`subscriber=0@badge-info=`), schluckt TwitchLibs Parser das `@`;
  nur die Rohzeile ist vollständig. Regel: Zeile beginnt mit `@` **und** das Segment bis zum
  ersten Leerzeichen enthält ein zweites `@`. Prefix (`nick!user@host`) und Text (`@user`) liegen
  hinter dem ersten Leerzeichen und lösen nie aus.
- **E7 — Zwei Log-Ebenen für den Sentinel.** Je Treffer eine `Warning` mit Kanal, `room-id` und
  dem Tag-Block (ohne Nachrichtentext, max. 512 Zeichen) — selten genug (beobachtet: 1 auf
  Tausende); dazu je Flush eine `Warning` mit der Summe, nur bei > 0, nach dem Vorbild des
  Indeterminate-Zählers. Kein Health-Feld (s. Nicht-Ziele).

## Nicht-Ziele (bewusst so entschieden)

- Kein Retry/Backoff für einen fehlgeschlagenen ersten 7TV-Sync — der Warmstart macht ihn
  unkritisch, der periodische Resync holt ihn nach.
- Kein Verwerfen gespleißter Zeilen (Zählregel im laufenden #69-Messfenster), keine Reparatur
  des Spleißes.
- Kein `WorkerHealthSnapshot`-Feld für Sentinel-Zähler oder „Client verbraucht" — der Health-
  Vertrag ist Api-seitig verdrahtet und wäre ein zweiter Vertragswechsel; das Log genügt für
  Task 6 und den Prod-Nachweis.
- Keine Änderung der TwitchLib-Loglevel in `appsettings.json` (bleibt Diagnose-Handgriff in
  Task 6).
- Kein Warmstart in `ApplyEmoteSetUpdateAsync` (Delta-Pfad setzt einen Zustand voraus, den der
  nächste Vollsync ohnehin herstellt).

---

### Task 1: Der Match-Cache wird aus Postgres vorgewärmt, bevor 7TV gefragt wird

**Absicht.** Ein Kanal mit bekannten Emote-Zeilen zählt ab dem Join, auch wenn 7TV gerade nicht
antwortet. Der 7TV-Sync korrigiert danach wie bisher; ein veralteter DB-Stand kann einen
erfolgreichen Sync nie zurücksetzen.

**Dateien.** `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs` ·
`tests/EmotePurge.Infrastructure.Tests/Integration/SevenTvSyncServiceTests.cs` ·
`docs/DECISIONS.md` · `docs/Architectur.md` (nur der Halbsatz in Zeile 80, der beschreibt, wann
der Cache gefüllt wird).

**Vertrag.**
- In `SyncChannelAsync`, unmittelbar nachdem das Row-Gate steht (nach dem Re-Read, also mit dem
  gültigen `channel.ChannelName`) und **vor** der `TwitchChannelId`-Auflösung: ist
  `emoteMatchCache.GetChannelEmotes(channel.ChannelName).Count == 0`, wird die vorhandene
  `RefreshMatchCacheAsync(channel, ct)` aufgerufen. Kein neuer Ladepfad — dieselbe Abfrage,
  dieselbe Koaleszierung, derselbe Duplikat-Tracker.
- Danach eine `Information`-Zeile, **nur** wenn der Cache jetzt ≥ 1 Namen hält (deutsch, mit
  Kanal und Anzahl, Hinweis „7TV-Sync folgt"). Damit erscheint sie höchstens einmal je Kanal und
  Prozessleben (plus nach LEAVE/JOIN), nie im Minutentakt.
- Alles Weitere unverändert: Erfolgspfad überschreibt über `RefreshMatchCacheAsync` :145,
  Fehlpfade fassen den Cache nicht an.

**Grenzfälle.**
- Kanal ohne Emote-Zeilen (Erst-Join) oder nur archivierte Zeilen: Cache bleibt leer, keine
  Logzeile, Sync läuft wie heute.
- Kanal mit legitim null aktiven Emotes: `Count == 0` bei jedem Tick → eine billige Abfrage je
  Tick (E3), kein Log.
- Rename-Handover: Cache-Schlüssel ist der re-gelesene `channel.ChannelName`, identisch zu
  `RefreshMatchCacheAsync`.
- Postgres nicht erreichbar: der Warmstart wirft wie jede andere Abfrage im Sync; die Aufrufer
  fangen das bereits (Boot-Recovery :88-97, Resync :277-282).
- Nebenläufigkeit: Delta-Pfad und Vollsync teilen `ChannelSyncGate`; ein Warmstart kann nie einen
  Sync überholen, der gerade schreibt.
- Harness: nicht betroffen (ruft `SyncChannelAsync` nicht).

**Tests** (Integration, bestehende Helfer `SeedChannelAsync`, `CreateService`, `ISevenTvApiClient`-
Substitute mit Status ≠ `Ok`):
- `SyncChannel_SevenTvUnavailable_EmptyCache_IsWarmedFromPostgres` — als `Theory` über beide
  7TV-Ausfallstellen (Set-Abruf schlägt fehl; Kanal ohne `TwitchChannelId` und
  Identitätsauflösung schlägt fehl): Ergebnis `null`, Cache hält die aktiven Namen, **nicht** die
  archivierte Zeile.
- `SyncChannel_SevenTvUnavailable_FilledCache_IsLeftUntouched` — vorbefüllter Markerinhalt bleibt
  byte-gleich (Asymmetrie).
- `SyncChannel_WarmStartDoesNotOutliveASuccessfulSync` — DB kennt `oldname`, 7TV liefert nur
  `newname`: Cache enthält danach `newname` und nicht `oldname`.
- `SyncChannel_ChannelWithoutRows_LeavesCacheEmpty` — keine Exception, Cache leer.

**DECISIONS-Eintrag** (im selben Commit): Titel „Der Match-Cache wird aus Postgres vorgewärmt,
bevor 7TV gefragt wird". Inhalt: die stille Lücke (Join vor Sync, leeres Set steigt aus, kein
Retry, Watchdog blind), die Stelle und warum dort (E1/E2), die erhaltene Asymmetrie (E3), die
Tick-Kosten leerer Kanäle, Nicht-Ziel Retry/Backoff.

**Abnahme.** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~SevenTvSyncService"`
grün (Docker); `dotnet format EmotePurge.slnx` ohne Diff; Commit
`fix(sync): warm the emote match cache from Postgres before the first 7TV call`.

- [ ] Warmstart in `SyncChannelAsync` eingebaut (Stelle, Bedingung, Logzeile)
- [ ] vier Integrationstests grün
- [ ] DECISIONS-Eintrag + Architectur-Halbsatz
- [ ] Format sauber, Commit gesetzt

---

### Task 2: `ReconnectPolicy` kennt einen verbrauchten Client

**Absicht.** Die Entscheidung „nach einem Reconnect am selben Objekt ist der Client zu ersetzen"
liegt pur und getestet in der Policy, nicht im Transport.

**Dateien.** `src/EmotePurge.Worker/ReconnectPolicy.cs` ·
`tests/EmotePurge.Worker.Tests/ReconnectPolicyTests.cs`.

**Vertrag.**
- Neuer `Interlocked`-Zustand mit `RegisterInPlaceReconnect()` (setzt) und Property
  `IsClientSpent` (liest). **Nur** `RegisterClientReplaced()` löscht ihn; `RegisterConnected()`
  ausdrücklich nicht — `Handle004` feuert `OnConnected` auch auf dem verbrauchten Client.
- `Decide`-Reihenfolge: Fehlerserie ≥ 3 → `Recreate` (unverändert); **verbraucht → `Recreate`**
  mit Grund, der Issue #114 nennt; dann die bestehende In-Flight-Logik. Verbraucht schlägt
  In-Flight, weil ein laufender Open zum verbrauchten Objekt gehört und `RecreateClientAsync` den
  In-Flight-Marker ohnehin abräumt.
- Klassen-Docstring um den neuen Grund ergänzen (der bisherige Text nennt drei Produktionsausfälle
  als Quelle jeder Regel; #114 ist der vierte).

**Tests** (neu, container-frei): frisch → nicht verbraucht; nach `RegisterInPlaceReconnect` →
`Recreate`; verbraucht + Open in Flight unter der Schwelle → `Recreate`, nicht `Wait`;
`RegisterConnected` nach dem Setzen → weiterhin verbraucht; `RegisterClientReplaced` → gelöscht,
`Decide` liefert wieder `Reconnect`.

**Abnahme.** `dotnet test tests/EmotePurge.Worker.Tests` grün; Commit
`feat(worker): let ReconnectPolicy mark an in-place reconnected client as spent (#114)`.

- [ ] Zustand, Setter, Property, `Decide`-Zweig
- [ ] fünf Tests grün, bestehende unverändert grün
- [ ] Commit gesetzt

---

### Task 3: Der Watchdog-Tick ersetzt den verbrauchten Client

**Absicht.** `OnReconnected` markiert, der nächste Tick fährt über `ForceReconnectAsync →
RecreateClientAsync` — der seit 2026-07-26 produktive und seit 2026-07-30 auf „Rejoin nur auf
frischem Client" korrigierte Pfad. Neu sind ein Zustand und ein Zweig, kein Transportweg.

**Dateien.** `src/EmotePurge.Worker/TwitchChatManager.cs` · `ITwitchChatManager.cs` ·
`TwitchWatchdogPolicy.cs` · `TwitchConnectionWatchdog.cs` ·
`tests/EmotePurge.Worker.Tests/TwitchWatchdogPolicyTests.cs` · `docs/DECISIONS.md` · `CLAUDE.md`
(Architektur-Absatz zum Worker: der Satz „genau **einen** langlebigen `TwitchLib.Client` … nie pro
Channel/Join neu instanziieren" bekommt den Zusatz, dass der Client nach jedem Reconnect am selben
Objekt ersetzt wird, Issue #114).

**Vertrag.**
- `TwitchChatManager.OnReconnected`: zusätzlich `_reconnectPolicy.RegisterInPlaceReconnect()`;
  Logzeile sagt, dass der Client beim nächsten Watchdog-Tick ersetzt wird (#114). Der Handler
  bleibt synchron und exception-frei — er läuft in der Schleife, die das Problem ist. Kommentar
  :426-429 („No rejoin here") um den Grund für den Versatz ergänzen (E4).
- `ITwitchChatManager.IsClientSpent` (Property, delegiert an die Policy), mit Kommentar analog zu
  `LastFrameReceivedUtc`.
- `TwitchWatchdogPolicy.Decide` bekommt einen fünften Parameter `bool clientSpent`, **zuerst**
  geprüft: `true` → `ForceReconnect` mit eigenem Grund, unabhängig von `isConnected`, Frame-Alter
  und beiden Cooldowns. Kein Cooldown nötig: `RecreateClientAsync` löscht den Zustand über
  `RegisterClientReplaced`, ein Fehlschlag des neuen Open kann keine Tick-Schleife über diesen
  Zweig erzeugen.
- `TwitchConnectionWatchdog.CheckOnceAsync` reicht `twitchChatManager.IsClientSpent` durch;
  `LogLiveContextAsync` und `_lastForcedReconnectUtc` bleiben, wie sie sind (Diagnose bzw.
  Cooldown der anderen Zweige).
- `RecreateClientAsync` bleibt unverändert — `UnwireClient` schneidet die beiden alten Schleifen
  von unseren Handlern ab, `DisconnectAsync` beendet sie, der neue `WebSocketClient` hat genau
  eine.

**Grenzfälle.**
- Unser eigener Watchdog-`Reconnect` löst ebenfalls `OnReconnected` aus → einen Tick später
  Recreate (E5, gewollt).
- `OnReconnected` auf dem *neuen* Client (TwitchLibs eigene Reconnection-Policy) → erneut
  verbraucht → erneut Recreate; gleiche Begründung, gleiche Kosten (ein Rejoin-Burst, gedrosselt).
- Recreate läuft gerade (Semaphor belegt): der Tick wird übersprungen wie heute; der Zustand ist
  dann schon gelöscht oder wird es mit dem laufenden Recreate.
- Bis zum Tick (≤ 60 s) liest der verbrauchte Client weiter — das ist das akzeptierte Fenster;
  Task 4 macht sichtbar, was darin passiert.
- Der Watchdog setzt `_lastForcedReconnectUtc` auch auf diesem Zweig → der Frame-Stale-Zweig
  ist danach 15 min in Cooldown; der 1-min-Disconnected-Zweig nicht. Akzeptiert: die Verbindung
  ist gerade neu.

**Tests** (`TwitchWatchdogPolicyTests`): verbraucht + frischer Frame + innerhalb Cooldown →
`ForceReconnect`; verbraucht + getrennt vor erstem Open → `ForceReconnect`; alle bestehenden
Fälle mit `clientSpent: false` unverändert. `WorkerBootSequenceTests` bleibt grün (Substitute).

**DECISIONS-Eintrag** (im selben Commit): Titel „Nach jedem Reconnect am selben Objekt wird der
TwitchLib-Client ersetzt (#114)". Inhalt: Mechanismus in einem Absatz (Verweis auf #114), warum
nicht inline (E4), warum jedes `OnReconnected` (E5), dass „genau ein langlebiger Client" wahr
bleibt (einer zur Zeit, ersetzt statt repariert), der Sentinel aus Task 4 als Nachweisinstrument,
Freeze/Dependabot-Bezug (Fix umgeht den Bibliotheksfehler, kein Versionswechsel), und dass die
Live-Verifikation (Task 6) vor dem Merge aussteht.

**Abnahme.** `dotnet build EmotePurge.slnx` warnungsfrei in den geänderten Dateien;
`dotnet test tests/EmotePurge.Worker.Tests` grün; Commit
`fix(worker): replace the TwitchLib client after every in-place reconnect (#114)` inkl.
DECISIONS + CLAUDE.md.

- [ ] Handler, Property, Policy-Parameter, Watchdog-Durchreichung
- [ ] Tests grün
- [ ] DECISIONS-Eintrag + CLAUDE.md-Satz
- [ ] Commit gesetzt

---

### Task 4: Sentinel für gespleißte IRC-Zeilen — warnen und zählen, nie verwerfen

**Absicht.** Ein Beweisinstrument: jede Zeile, die nur durch einen Spleiß entstanden sein kann,
wird erkannt und gezählt. Ohne Fix müssen Treffer auftreten, mit Fix nach dem Recreate keine mehr
(Task 6). Die Zählung selbst bleibt unverändert.

**Dateien.** `src/EmotePurge.Worker/IrcLineSpliceRule.cs` (neu, `static`, pur) ·
`WorkerStats.cs` · `TwitchChatManager.cs` · `UsageFlushWorker.cs` ·
`tests/EmotePurge.Worker.Tests/IrcLineSpliceRuleTests.cs` (neu) ·
`tests/EmotePurge.Worker.Tests/IrcLineSpliceRuleTwitchLibTests.cs` (neu, TwitchLib-gebunden nach
dem Vorbild `SharedChatRuleTwitchLibTests`) · `WorkerStatsTests.cs`.

**Vertrag.**
- `IrcLineSpliceRule.IsSpliced(string? rawLine)` nach E6: `null`/leer → `false`; Zeile beginnt
  nicht mit `@` → `false`; sonst Segment bis zum ersten Leerzeichen (oder die ganze Zeile, wenn
  keines existiert) enthält ab Index 1 ein weiteres `@` → `true`. Allokationsfrei (`IndexOf`).
- `WorkerStats.RecordSplicedIrcLine()` / `TakeSplicedIrcLinesSinceLastFlush()` — exakt das
  Muster des Indeterminate-Zählers (Interlocked, kein Log im Record).
- `TwitchChatManager.OnMessageReceived`: nach den beiden Watchdog-Schreibzugriffen (:500-504) und
  **vor** dem Ausstieg bei leerem Set: `IsSpliced(e.ChatMessage.RawIRC)` → `Record…` +
  `LogWarning` (Kanal, `RoomId`, Tag-Block bis zum ersten Leerzeichen, auf 512 Zeichen gekappt;
  **kein** Nachrichtentext). Danach läuft die Nachricht unverändert weiter.
- `UsageFlushWorker.FlushOnceAsync`: direkt neben dem Indeterminate-Block (:442-448) den
  Sentinel-Zähler ziehen und bei > 0 als `Warning` ausgeben, mit Verweis auf #114.

**Grenzfälle.** `@user` im Text, `nick!user@host` im Prefix, Zeilen ohne Tags (`:` am Anfang),
Server-`PING` — alle `false`. Ein Spleiß, der die Zeile unparsebar macht, erreicht
`OnMessageReceived` nie und wird nicht gezählt (bekannte Untergrenze, im DECISIONS-Eintrag von
Task 3 erwähnt).

**Tests.**
- `IrcLineSpliceRuleTests` (pur): die sechs Fälle oben, je ein Fact/Theory-Case.
- `IrcLineSpliceRuleTwitchLibTests`: eine synthetische gespleißte Zeile (Schnitt im Wert eines
  typisierten Tags, z. B. `subscriber=0@badge-info=…`, erfundene IDs) durch `IrcParser.ParseMessage`
  und den `ChatMessage`-Konstruktor: `RawIRC` trägt das zweite `@`, die Regel schlägt an — und
  `UndocumentedTags` zeigt es **nicht** (das ist der Beleg für E6). Ein Fall genügt.
- `WorkerStatsTests`: Record erhöht, Take liefert und nullt, zweites Take liefert 0.

**Abnahme.** `dotnet test tests/EmotePurge.Worker.Tests` grün; Commit
`feat(worker): warn on and count spliced IRC lines, never drop them (#114)`.

- [ ] Regel, Zähler, Handler-Aufruf, Flush-Ausgabe
- [ ] Tests grün (pur + TwitchLib-gebunden + Stats)
- [ ] Commit gesetzt

---

### Task 5: Lokale Gates des Gesamtpakets

**Absicht.** Alles, was ohne CI und ohne Streamer prüfbar ist, ist geprüft — in der Reihenfolge,
in der die Werkzeuge aussagekräftig sind.

**Schritte.**
1. `dotnet format EmotePurge.slnx --verify-no-changes` — ohne Befund.
2. `dotnet test EmotePurge.slnx` — alle drei Backend-Suiten grün (Docker für Testcontainers).
3. Sicherstellen, dass **alle vier Task-Commits gesetzt sind** (`git status` sauber,
   `git log origin/main..HEAD` zeigt vier Commits). Erst dann:
4. `node scripts/coverage-local.mjs --backend-only` — Schwelle 80 % auf neuem Code.
   **Das Skript misst ausschließlich Committetes.** Eine Ausgabe wie „0 geänderte Datei(en) …
   nichts zu bewerten" mit Exit 0 ist eine **Nichtmessung, keine Entwarnung** — dann ist Schritt 3
   nicht erfüllt, nicht das Gate. Liegt die Quote unter 80 %, ist der Hebel, Logik aus
   `TwitchChatManager`/`UsageFlushWorker` in die puren Klassen zu verschieben — **nicht**, den
   Transport gegen Fakes zu testen.
5. Kein `git push`, kein Merge. Der Branch bleibt lokal bis Task 6.

**Abnahme.** Die vier Ausgaben (Format, Tests, `git log`, Coverage-Quote) stehen im Abschlussbericht
des Laufs, mit Zahlen.

- [ ] Format ohne Befund
- [ ] `dotnet test EmotePurge.slnx` grün
- [ ] vier Commits, sauberer Tree
- [ ] Coverage gemessen (nicht „nichts zu bewerten"), Quote ≥ 80 % oder Hebel angewendet

---

### Task 6: Live-Verifikation — **nicht im Nachtlauf ausführbar**

**Warum getrennt.** Regel 16 verlangt Verifikation gegen echtes Twitch/7TV. Beide Defekte
brauchen laufenden Chat; nachts sind keine Streamer online. Dieser Task ist das **Merge-Gate**
des Pakets und wird vom Nutzer (oder einer beaufsichtigten Sitzung) tagsüber gefahren; er ändert
keinen Produktivcode.

**Aufbau (Wegwerf, nicht committen).**
- Lokaler Stack (`docker compose up -d postgres redis`, Worker per `dotnet run` oder
  `docker compose up -d --build worker`), Worker-Logging für den Lauf auf
  `TwitchLib.Client.TwitchClient: Warning` und `TwitchLib.Communication: Trace` (macht Fragmente
  als „Unaccounted for" und gestartete Lese-Schleifen sichtbar).
- Ein lauter Kanal ist gejoint (Ziel: > 1 Nachricht/s über 30 min, z. B. `papaplatte` oder
  `handofblood` live).
- **Wegwerf-Hook:** eine per Umgebungsvariable geschaltete Stelle in
  `TwitchChatManager.OnMessageReceived`, die **einmal**, nach N empfangenen Nachrichten,
  `_client.ReconnectAsync()` aus dem Empfangspfad heraus aufruft — derselbe Inline-Pfad wie
  Twitchs `RECONNECT` (`_client_OnMessage → ReconnectAsync`). Zweite Variable schaltet für den
  Negativlauf das Setzen des „verbraucht"-Zustands ab, damit beide Läufe auf demselben Build
  laufen.

**Messung, je Lauf 30 min ab dem ausgelösten Reconnect, gleicher Kanal, vergleichbare
Nachrichtenrate.**

| Lauf | Erwartung | Nachweis scheitert, wenn |
|---|---|---|
| **Ohne Fix** (Zustand abgeschaltet) | `Trace` zeigt nach dem Reconnect eine **zweite** gestartete Lese-Schleife; im Fenster ≥ 1 Sentinel-`Warning` **oder** ≥ 1 „Unaccounted for"/Parse-Fehler mit mitten in der Zeile beginnendem Fragment | in 30 min weder zweite Schleife noch Fragment: Reproduktion nicht hergestellt → Rate zu niedrig, lauteren Kanal oder längeres Fenster wählen; **nicht** als „Bug existiert nicht" werten |
| **Mit Fix** | binnen ≤ 60 s nach dem Reconnect die Zeile „TwitchClient wird komplett neu instanziiert … #114"; danach genau **eine** Join-Bestätigung je gewünschtem Kanal, keine `OnFailureToReceiveJoinConfirmation`; **null** Sentinel-Treffer und null Fragmente vom Recreate bis Fensterende; Health/Roster bleiben gesund | Recreate bleibt aus → Watchdog-Verdrahtung (Task 3) defekt; Treffer **nach** dem Recreate → die Doppelschleife überlebt den Neuaufbau, Hypothese falsch → **nicht mergen**, neu bewerten; doppelte Join-Bestätigungen → Rejoin-Regel von 2026-07-30 verletzt |

**Defekt 1 zusätzlich** (kann derselbe Tag sein, braucht nur eigenen Chat, z. B. `sensitron`):
7TV für den Worker unerreichbar machen (Compose-Override mit `extra_hosts: 7tv.io:127.0.0.1`,
nicht committen), Worker neu starten, in den eigenen Chat ein bekanntes Emote tippen. Erwartung:
Logzeile „Match-Cache … aus Postgres vorgewärmt", Sync-Fehler `seventv_unavailable` in der
Kanalzeile, und binnen ≤ 30 s (Flush) eine wachsende `UsageStats`-Zeile. Scheitert, wenn die
Zeile ausbleibt, obwohl die Warmstart-Logzeile da war (dann Cache-Schlüssel prüfen), oder wenn
die Warmstart-Zeile fehlt (Stelle im Sync vor dem ersten 7TV-Call prüfen).

**Abnahme.** Beide Tabellenzeilen mit Zahlen (Nachrichten im Fenster, Treffer vorher/nachher,
Zeit bis Recreate, Join-Bestätigungen) als Kommentar in #114; Wegwerf-Hook und Override
entfernt; erst dann Codex-Zweitmeinung (`/codex:review --model gpt-5.6-sol --scope branch --base origin/main`),
Push, PR.

- [ ] Negativlauf reproduziert (zweite Schleife und/oder Fragment/Sentinel)
- [ ] Positivlauf: Recreate ≤ 60 s, ein Join je Kanal, null Treffer danach
- [ ] Defekt-1-Probe: Zählung ohne 7TV
- [ ] Hook/Override entfernt, Befund in #114

---

## Abnahme des Gesamtpakets

Reihenfolge ist Teil der Abnahme — die Coverage-Messung ist erst nach den Commits aussagekräftig.

1. `dotnet format EmotePurge.slnx --verify-no-changes` — ohne Befund.
2. `dotnet test EmotePurge.slnx` — grün (Docker läuft; Testcontainers ziehen Postgres/Redis).
3. Vier Task-Commits auf `fix/worker-erfassungsfehler-114`, Working Tree sauber.
4. `node scripts/coverage-local.mjs --backend-only` — **misst nur Committetes**; „0 geänderte
   Datei(en) … nichts zu bewerten" ist eine Nichtmessung und bedeutet, dass Schritt 3 fehlt.
   Zielwert ≥ 80 % auf neuem Code (dateigenaue Näherung; Sonar misst am PR zeilengenau).
5. Kein Push, kein Merge im Nachtlauf. Merge-Gate ist Task 6 plus Codex-Zweitmeinung.
6. Abschlussbericht des Laufs nennt: Commit-Hashes, Testzahlen je Suite, Coverage-Quote, und
   jede Abweichung vom Plan mit Begründung.

## Bewusst nicht Teil des Pakets

- Ein `TwitchLib.*`-Versionswechsel (Dependabot-`ignore` bis 2026-10-08; der Fix umgeht den
  Bibliotheksfehler, statt ihn zu beheben).
- Retry/Backoff für den ersten 7TV-Sync eines Kanals; Konfigurierbarkeit des Warmstarts.
- Verwerfen oder Reparieren gespleißter Zeilen; Bereinigung bereits geschriebener
  `UsageStats`-Zeilen (beobachtet: eine Zeile mit `SharedChatUseCount = 1`).
- Health-/Admin-Sichtbarkeit von Sentinel-Zähler oder „Client verbraucht"
  (`WorkerHealthSnapshot`, Monitoring-Seite).
- Dauerhafte Anhebung der TwitchLib-Loglevel in `appsettings.json`.
- Die offene Frage aus #69, ob der 2–6-%-Überhang der Log-Seite auf diese Doppelschleife
  zurückgeht — das entscheidet erst der bindende Lauf nach dem Deploy dieses Pakets.
- Prod-Deploy und Kuma/Log-Beobachtung danach (kein Migrationsschritt nötig; Rollback-Verbot
  für das Worker-Image aus #73 gilt weiter — dieses Paket ist ein Vorwärts-Deploy).
