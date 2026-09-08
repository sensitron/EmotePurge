# Plan #68 — Der Worker stellt seine Twitch-Verbindung selbst wieder her

Umsetzungsplan zu [`docs/Konzept-Worker-Reconnect-2026-09-08.md`](../Konzept-Worker-Reconnect-2026-09-08.md)
(Issue #68, im Zusammenhang mit #114 und #122). Erstellt am 2026-09-08 auf `feat/worker-reconnect-68`
gegen `main` = `86eedcd`. Quellen: das Konzept einschließlich der beantworteten Gegenrede
(Abschnitt 10) und der Anhänge A/B, die Entscheidungen des Nutzers vom 2026-09-08 zu den offenen
Punkten 7.3/7.4/7.5/7.7/7.10 und #122 sowie sein Nachtrag, die JOIN-Limit-Messung vorzuziehen
(Task 0), `CLAUDE.md` (Regeln 1–22), `docs/DECISIONS.md` (Eintrag
2026-09-08 zu #114), die Issues #68, #117, #118, #122, der betroffene Code, und der installierte
Binärstand `TwitchLib.Client 4.0.1` / `TwitchLib.Communication 2.0.1` (dekompiliert mit `ilspycmd`,
nur dort, wo der Plan eine Annahme des Konzepts selbst berührt). Alle Zeilenangaben sind auf
`86eedcd` verifiziert.

**Der Plan enthält keinen Code.** Signaturen stehen als einzeilige Verträge, Zustände als
dreizeiliges Schema, alles andere ist Absicht, Grenzfall und Reihenfolge. Jeder Task ist für einen
Subagent mit frischem Kontext geschrieben; er liest vor der Arbeit das Konzept **ganz** und die
unter „Betroffene Dateien" genannten Dateien ganz. Der Plan setzt das Konzept um, er entscheidet
nichts neu — wo er eine Lücke des Konzepts schließen musste, steht das ausdrücklich dabei
(Abschnitt 1.3). Die Fassung vom Abend des 2026-09-08 ist die Antwort auf die adversariale
Gegenrede zum Plan (Abschnitt 7); ihre Änderungen sind in die betroffenen Abschnitte eingearbeitet,
nicht nur dort notiert.

Reihenfolge der Arbeit in jedem Task: **Tests zuerst** (rot), dann Umsetzung (grün), dann die im
Task genannte Doku, dann die Gates aus Task 8. Commit und Push je Task ohne Rückfrage, Conventional
Commits, mehrere logische Commits (Regel 1 in der Fassung vom 2026-09-08, Regel 2); **der Merge
gehört dem Nutzer** (Regel 1, Regel 22); Zeitpunkt und Bündelung stehen in Abschnitt 0.

---

## 0. Merge- und Deploy-Fenster — zuerst lesen

**Die Merge- und Deploy-Sperre bis 2026-10-08 ist aufgehoben.** Beschluss des Nutzers vom Abend des
2026-09-08 (Epic #118, „Nachtrag 2026-09-08 abends"), s. Abschnitt 7, PG5, für die ursprüngliche
Sperre und ihre Mechanik.

- **Deploy am 2026-09-09 nach 12:48 lokal**, nach Auswertung der 24-h-Nachkontrolle aus #117 (Start
  2026-09-08 10:48 UTC).
- Gebündelt mit #122 (`stop_grace_period`, Task 5) und #129 (Pfadfilter je Image, eigener Branch der
  Parallelsitzung).
- Begründung: Das Messfenster aus #118 begann am 2026-09-08 selbst. Ein Reset an Tag 1 kostet null
  Fenstertage; der Preis steigt mit jedem Wartetag. Ob #68 überhaupt ein Reset ist, ist offen (kein
  `AlgorithmVersion`-Bump, keine Schemaänderung, kein neuer Stichtag, kein Rollback).
- **Unverändert:** Task 9 bleibt Merge-Blocker (SLO-2, K = 0), beide Test-Gates, die
  Codex-Zweitmeinung (Regel 22). Der Merge selbst bleibt beim Nutzer.

Die Mechanik, die die frühere Sperre begründete, bleibt als Kontext gültig: `.github/workflows/publish.yml`
läuft bei `push` auf `main` und veröffentlicht die Images unter dem mutablen Tag `:latest`
(Zeile ~135); `docker-compose.prod.yml` referenziert dieses Tag ohne `pull_policy`. Ein Merge auf
`main` setzt also mit dem nächsten Redeploy den #117-Zeitanker zurück — genau das ist ab dem
2026-09-09 nach 12:48 lokal gewollt, nicht mehr zu vermeiden. Vor dem Deploy sichert der Nutzer die
#117-Zahlen als Baseline (Konzept 6.6); das steht als Übergabe am Ende von Task 9, nicht als Task.
Kein Task in diesem Plan verbindet sich selbst zu `vps`/`nas` oder rollt über Portainer aus
(globale SSH-Regel) — der Deploy bleibt eine vom Nutzer ausgeführte, getrennte Handlung (Regel 1).

---

## 1. Verifikation des Ist-Zustands

### 1.1 Was das Konzept behauptet und der Code bestätigt

| Behauptung (Konzept) | Stimmt? | Befund auf `86eedcd` |
|---|---|---|
| `ReconnectPolicy` hat drei Regeln, 13 Tests | ja | `src/EmotePurge.Worker/ReconnectPolicy.cs` (150 Zeilen, `Decide` `:109-149`), `tests/EmotePurge.Worker.Tests/ReconnectPolicyTests.cs` — 13 `[Fact]`, davon 5 zum „spent"-Zustand |
| `TwitchWatchdogPolicy.Decide` hat den `clientSpent`-Zweig bei `:63-68` | ja | `TwitchWatchdogPolicy.cs:63-68`; Tests: 12 Fälle, davon 2 `ClientSpent…` (`TwitchWatchdogPolicyTests.cs:152-180`) |
| `ITwitchChatManager` trägt `IsClientSpent` und `ForceReconnectAsync` | ja | `ITwitchChatManager.cs:49` und `:55`; einzige Aufrufer beider Mitglieder: `TwitchConnectionWatchdog.cs:34` und `:48` |
| Unser Rejoin läuft inline in `Handle004` → `OnConnected` | ja | `TwitchChatManager.cs:382-385` (`RejoinDesiredChannelsAsync` aus dem Handler, hinter `_joinsIssuedForCurrentClient`) |
| `RecreateClientAsync` awaitet `oldClient.DisconnectAsync()` | ja | `TwitchChatManager.cs:231-238` |
| `TryJoinAsync` hat kein Token, keine Wunschzustandsprüfung nach dem Gate | ja | `TwitchChatManager.cs:303-338`: `_joinGate.WaitAsync()` und `Task.Delay(...)` ohne Token, danach direkt `_client.JoinChannelAsync` |
| „Join für … aufgeschoben" ist Warning | ja | `TwitchChatManager.cs:307-309` |
| `CreateClient` setzt `new ReconnectionPolicy()` | ja | `TwitchChatManager.cs:582` |
| `TwitchLib.Client.TwitchClient` steht auf `Error` | ja, **an zwei Stellen** | `appsettings.json:8` **und** `appsettings.Development.json:6` — Letzteres nennt das Konzept nicht (s. 1.3) |
| Hosted Services stoppen sequenziell, Watchdog vor `UsageFlushWorker` | ja | `WorkerServiceRegistration.cs:52-64`: Watchdog fünfter, Flush zweiter; `WorkerServiceRegistrationTests.cs:31-39` pinnt **genau neun** Hosted Services |
| `stop_grace_period` fehlt in beiden Compose-Dateien | ja | kein Treffer in `docker-compose.yml` und `docker-compose.prod.yml`; `worker:` bei `:83` bzw. `:85` |
| `OnReadLineTestAsync(rawIrc)` existiert und löst bei `RECONNECT` `ReconnectAsync()` aus | ja, **am Binärstand geprüft** | `TwitchClient.OnReadLineTestAsync(string)` ist `public`, ruft `HandleIrcMessageAsync(IrcParser.ParseMessage(rawIrc))`; der Switch dort hat `case Reconnect → ReconnectAsync()` |
| `NoReconnectionPolicy`, `TimeOutEstablishConnection` existieren in Communication 2.0.1 | ja | Symbole in der DLL vorhanden; die Semantik (`ReconnectionPolicy(0, maxAttempts: 1)`, `Reset(true)` ohne Zählerreset) übernimmt der Plan aus dem Konzept (3.1, belegt) und prüft sie nicht erneut |
| `ss --kill` braucht `CONFIG_INET_DIAG_DESTROY` — „offen, ob die Devbox ihn hat" | **jetzt belegt: ja** | `/boot/config-$(uname -r)`: `CONFIG_INET_DIAG_DESTROY=y`; `ss` (iproute2 6.15) und `nsenter` sind installiert. **Aber:** `sudo` verlangt ein Passwort — die Host-Kommandos in S1/S3 führt der Nutzer aus oder gibt sie frei (Task 9) |
| `WorkerHealthSnapshot` ist ein Api-seitiger Vertrag | ja | `Core/Services/IWorkerHealthReader.cs:19-…`, gefüllt in `WorkerHealthPublisher.cs:59-85` — bleibt unangetastet (Entscheidung 7.4) |

### 1.2 Was der Plan über das Konzept hinaus vorgefunden hat

- **`WorkerBootSequenceTests.cs:32-58`** substituiert `ITwitchChatManager` und konstruiert `Worker`
  mit acht Argumenten. Jede Signaturänderung an `ITwitchChatManager` kompiliert dort dank
  NSubstitute weiter; eine Änderung am **Konstruktor** von `Worker` (Task 6 gibt ihm
  `IConfiguration`) bricht den Test und wird im selben Task mitgezogen.
- **Drei `<see cref="…ReconnectPolicy"/>`-Verweise** außerhalb der Klasse: `TwitchWatchdogPolicy.cs:7`,
  `BotChatterDetector.cs:7` und `SevenTv/SevenTvBackoffPolicy.cs:31` — Letzterer **voll qualifiziert**
  als `cref="EmotePurge.Worker.ReconnectPolicy"`, ein `grep` auf `cref="ReconnectPolicy` findet ihn
  nicht. Nur diese drei werden zu `CS1574`. Rein inhaltlich nachzuziehen, ohne Warnung: `<c>`-Erwähnungen
  in `WorkerStats.cs:10` und `TwitchWatchdogPolicy.cs:38` (`ReconnectPolicy.RegisterClientReplaced`)
  sowie Prosa in `TwitchWatchdogPolicyTests.cs:5`, `BotChatterDetectorTests.cs:6`.
  Nach dem Löschen der Klasse werden die `cref`s zu `CS1574`-Warnungen — sichtbar nur mit
  `dotnet build --no-incremental` (Projektnotiz vom 2026-09-07). Anhang B des Konzepts nennt sie
  nicht; Task 4 zieht sie mit.
- **`CLAUDE.md:85`** (Liste der getesteten Policies nennt `ReconnectPolicy`) und **`CLAUDE.md:208`**
  („TwitchLibs eigener Rejoin nach einem Reconnect nicht" gedrosselt) werden mit dem Umbau falsch;
  Anhang B nennt nur den Architektur-Absatz `:141`. Task 7 zieht alle drei.
- **`docs/Architectur.md` A.1 (`:82-86`)** hat heute **keinen** Satz zu Verbindungsaufbau oder
  Watchdog — es gibt dort nichts zu ändern, nur etwas zu ergänzen (Task 7).
- **Regel 1 hat sich am 2026-09-08 geändert** (`DECISIONS.md:13`, `CLAUDE.md:164`): Commit, Push und
  PR laufen ohne Rückfrage, der Merge nicht. Die Geschwisterpläne #70/#91 tragen noch die alte
  Fassung („vor jedem Commit fragen"); dieser Plan folgt der neuen.
- **Das Issue #68 selbst** ist als Messfrage formuliert („JOIN-Budget klären"); seine Kommentare vom
  2026-09-08 halten fest, dass „die Drosselung der Weg bleibt" und der verifizierte Bot-Account ein
  eigenes Projekt nach dem Fenster ist. Das Konzept lässt die Messfrage ausdrücklich offen (7.1:
  „ob Twitch die 20/10-s-Grenze auf anonyme Verbindungen überhaupt durchsetzt"; 7.6: „entscheidet
  eine Messung, nicht dieses Papier"; 7.2 und 7.8) und führt die 100-Chatroom-Decke an zwei Stellen
  (4.5, 7.2) als bleibende Grenze, obwohl sie für `justinfan` nie gemessen wurde. **Der Plan zieht
  die Messung als Task 0 vor die Implementierung** (Nachtrag des Nutzers vom 2026-09-08), weil ihr
  Ergebnis den Entwurf verändern kann: die Budget-Zahl in `WorkerCapacity.cs` (7.2), die
  Risikoeinstufung R3 und die Dauerauflage „keine Ausweitung über ~20 Kanäle" aus Epic #118. Die
  einzige bisherige Messung (`docs/Review-2026-07-29-Umsetzung.md:423`, 2026-07-30) reichte bis
  **28** Kanäle: 28 JOINs in 5,0 s, 0 × `OnFailureToReceiveJoinConfirmation` — die Grenze biss nicht.
  Eine Sonde, die nicht über 28 hinausgeht, brächte nichts Neues.

### 1.3 Wo der Plan eine Lücke des Konzepts schließen musste

Alles hier ist Umsetzungsentscheidung im Rahmen des Konzepts, keine Abweichung von E1–E5, den
Zahlen oder den SLOs. Der Nutzer kann jede davon kippen; dann ändert sich der jeweils genannte Task.

1. **Wann der Signal-Slot leert und welche Ereignisse überhaupt signalisieren.** Das Konzept sagt
   „ein Slot mit Kapazität 1, neue Signale überschreiben nur den Grund" (4.7) **und** „ein Verlust
   während der Rejoin-Runde ist ein neues Signal" (3.3) — beides zusammen verlangt eine Regel, die
   das Konzept nicht nennt: Ohne sie bliebe ein Signal aus einem **fehlgeschlagenen** Versuch
   (`OnDisconnected` eines Clients, der nie `004` sah) im Slot liegen und löste nach dem
   schließlich gelungenen Aufbau einen zweiten, grundlosen Wiederaufbau aus. Plan (Task 4): Der
   Slot leert sich beim **Konsumieren** durch die Schleife. Handler signalisieren **nur** für den
   aktuellen Client **und nur, wenn dieser den Handshake hatte** (`_isConnected` war wahr); ein
   Client ohne Handshake meldet seinen Fehlschlag über den Rückgabewert des Versuchs, nicht über
   den Slot. Die zweite `OnDisconnected` aus Fall A wird — wie das Konzept sagt — durch das
   Unwiren neutralisiert, nicht durch den Slot. **Nachtrag aus der Gegenrede (Abschnitt 7, PG1):**
   das reicht nur bei 0 s Verzögerung. Liegt zwischen Konsumieren und Ersatz ein Boden (5 s, 10 s),
   ist der alte Client noch verdrahtet, hatte den Handshake und füllt den geleerten Slot mit seinem
   `OnConnectionError` (≈ 2 s) erneut — nach dem gelungenen Rejoin risse die Schleife die frische
   Verbindung wieder ab. Deshalb trägt jeder Client eine **Generation**, jedes Signal die Generation
   seines Clients, und das Konsumieren **verurteilt** diese Generation: Signale einer verurteilten
   Generation werden verworfen, nicht abgelegt (2.3). Der Slot ist dafür eine reine, getestete
   Klasse (Task 1).
2. **Die Schleife ist sequentiell, nicht nebenläufig.** „Wartet auf Signal oder Tick" (3.3) ist als
   *ein* Warten mit Zeitschranke umgesetzt: kommt binnen 60 s kein Signal, ist das der Tick. Während
   eines Wiederaufbaus wird nicht gewartet, also fallen Ticks aus („werden übersprungen", 3.3).
   Folge: Der Eingang `reconnectInFlight`, den das Konzept der `TwitchWatchdogPolicy` gibt (3.7,
   3.8), wäre am Tick **konstruktionsbedingt immer falsch** — die Schleife ist der einzige Aufrufer
   und tickt nur, während sie wartet. Die erste Fassung dieses Plans behielt ihn „für den Fall, dass
   der Tick je auf einen eigenen Timer wandert". **Gestrichen** (Abschnitt 7, Nebenbefund): ein
   Parameter, der an seiner einzigen Aufrufstelle für immer `false` ist, plus zwei Tests für einen
   Zweig ohne Aufrufer, sind toter Code mit Alibi-Tests — genau das, was Regel 11 und 12 nicht
   wollen. Wandert der Tick je auf einen eigenen Timer, ist das ein Umbau der Schleife und der
   Eingang kommt mit ihm zurück; eine Zeile im Klassenkommentar der Policy hält das fest. Task 3
   entfernt den `clientSpent`-Zweig ersatzlos; das Konzept trägt dazu einen Nachtrag am Ende von
   Abschnitt 10.
3. **Kein explizites Trennen beim Shutdown.** 3.3 sagt für `Stopped`: „der aktuelle Client wird
   getrennt". `DisconnectAsync` enthält ≥ 1,9 s eingebaute Wartezeiten (2.2, Punkt 3) und stünde
   damit gegen die Shutdown-Zusage ≤ 1 s (3.4, 4.6). Plan: beim Shutdown wird **nichts** getrennt —
   wie heute; der Socket stirbt mit dem Prozess, und Twitch räumt einen `justinfan` ohne PART auf.
   Getrennt wird nur, was ersetzt wird (E4).
4. **Flap-Boden gegen Fehlversuch-Backoff.** 3.6 nennt den Boden „vor dem nächsten Aufbau". Was
   gilt, wenn dieser Aufbau scheitert und der Fehlversuch-Backoff (2 s) unter dem Boden (5 s) liegt,
   sagt das Konzept nicht. Plan (Task 1): Solange die Dämpfung aktiv ist, ist der Boden eine
   **untere Schranke jeder** Verzögerung (Maximum aus beidem); ein Fehlversuch ist keine Sitzung —
   er zählt weder als kurze Sitzung noch hebt er die Dämpfung auf. Nur eine Sitzung ≥ 60 s hebt sie
   auf.
5. **Erster Versuch nach einem gescheiterten Boot-Connect.** 3.6 setzt „Verzögerung des ersten
   Versuchs = 0 s"; 4.8 beschreibt den Boot, ohne die Verzögerung zu nennen. Plan: der gescheiterte
   Boot-Connect **ist** der erste Versuch; die Schleife startet mit Streak 1, also nach 2 s. Das ist
   dieselbe Kurve, nur um den bereits verbrauchten Versuch verschoben.
6. **Zwei Uhren für T.** Die Abschlusszeile „… T s seit Verlust" (3.5) kann nur den Zeitstempel des
   **Signals** kennen; t₀ der lokalen Messung ist das Kill-Kommando auf dem Host (6.1), auf Prod gibt
   es nur das Signal (6.6). Plan (Task 9): SLO-1/SLO-2 lokal gegen den Host-t₀ rechnen, die Log-Zahl
   daneben notieren; die Differenz (≈ 0,4–0,6 s, das Signal-Delay aus 3.2) steht im Bericht, damit
   die Prod-Zahlen später vergleichbar sind.
7. **S7 gehört in Lauf A.** Der 16-Minuten-Netzschnitt trennt auch Postgres und Redis; nach fünf
   fehlgeschlagenen Flushes (≈ 2,5 min) **verwirft** `UsageFlushWorker` gezählte Nutzung
   (`UsageFlushWorker.cs:19`, `:97-103`). In Lauf B (lauter Kanal) verfälschte das die
   Zähler aus 6.4. Das Konzept ordnet S7 keinem Lauf zu; der Plan legt es in Lauf A.
8. **Beide `appsettings`-Dateien.** 7.10 nennt `appsettings.json:8`. `appsettings.Development.json:6`
   setzt denselben Schlüssel auf `Error` und gewinnt bei lokalem `dotnet run`; die Kontrollfrage aus
   6.1 („erscheint ‚Joining channel'?") fiele dort still durch. Plan (Task 4): beide Dateien.

---

## 2. Verträge

### 2.1 `ITwitchChatManager` nach dem Umbau

Entfällt: `bool IsClientSpent`, `Task ForceReconnectAsync()`. Geändert: `ConnectAsync` nimmt das
Stopping-Token. Neu: die Mitglieder, über die die Schleife und der Tick mit dem Manager sprechen —
und **nur** über sie (Konzept 9.3, E5); die Schleife abonniert keine TwitchLib-Ereignisse.

- `Task ConnectAsync(CancellationToken ct)` — der Boot-Versuch: genau ein Öffnen des initialen
  Clients plus Handshake-Warten (≤ ~25 s), bei Fehlschlag Signal `InitialConnectFailed` statt
  Hintergrundschleife; kehrt beim Token sofort zurück.
- `void RequestReconnect(TwitchSessionEndReason reason, string detail)` — legt ein Signal ab
  (Slot-Regel s. 2.3), gestempelt mit der Generation des aktuellen Clients. Vom Tick und vom
  Debug-Auslöser genutzt; die TwitchLib-Handler rufen intern dasselbe mit der Generation ihres
  `sender`.
- `Task<TwitchReconnectRequest> WaitForReconnectRequestAsync(CancellationToken ct)` — wartet auf ein
  Signal, **konsumiert** es (Slot danach leer) und **verurteilt** die Generation des Signals: alles,
  was dieser Client danach noch meldet, wird verworfen (2.3). Ein Signal, das vor dem Warten
  abgelegt wurde, wird sofort geliefert (der Boot-Fehlschlag kommt, bevor der Watchdog wartet).
- `Task<TwitchConnectOutcome> ReconnectOnceAsync(CancellationToken ct)` — Schritte 1–5 der
  Recreate-Sequenz (Konzept 3.4): alten Client abkoppeln und im Hintergrund aufräumen, neuen mit
  `NoReconnectionPolicy` bauen, öffnen, `004` abwarten (10 s). Rückgabe sagt Handshake ja/nein,
  bei nein den Grund (`ConnectFailed` / `ConnectFaulted` / `HandshakeTimeout`), die Dauer und die
  Generation des neuen Clients (für die Logzeilen, 2.7).
- `Task<TwitchRejoinOutcome> RejoinDesiredChannelsAsync(CancellationToken ct)` — Schritt 6:
  gedrosselte Runde über einen Snapshot von `_desiredChannels`, dann bis zu 5 s Warten auf die
  Bestätigungen; Rückgabe N gewünscht / M bestätigt / K offen / abgebrochen (Verbindung weg oder
  Token).
- `Task SimulateServerReconnectAsync()` — der Debug-Auslöser aus Entscheidung 7.5 (Task 6): reicht
  `:tmi.twitch.tv RECONNECT` über `OnReadLineTestAsync` in den aktuellen Client. Der Manager prüft
  keine Freigabe; die liegt allein am Kommando-Dispatcher in `Worker`.

Unverändert: `Initialize`, `JoinChannelAsync`, `EnsureJoinedAsync`, `LeaveChannelAsync`,
`IsConnected`, `LastMessageReceivedUtc`, `LastFrameReceivedUtc`, `ConnectAttemptedUtc`,
`GetRoster`. Intern tragen die drei JOIN-Pfade eine **Quelle** (`TwitchJoinSource`, 2.2):
`JoinChannelAsync` → `Command` (Boot-Recovery und Redis-`JOIN`), `EnsureJoinedAsync` →
`ConvergenceNet` (Resync-Tick und Redis-`RESYNC`), die Rejoin-Runde → `Rejoin`. Die Quelle
erscheint in der Ursprungszeile jedes JOIN (2.7) und nirgends sonst — sie ist das Messinstrument
aus Abschnitt 7, PG3, kein Vertrag nach außen.

Damit trägt die Schnittstelle zwischen Watchdog und Manager **vier** Mitglieder (Wait, ReconnectOnce,
Rejoin, RequestReconnect) statt der drei, die 9.3 zählt — `RequestReconnect` ist der Eingang des
Ticks, nicht der Schleife; der Debug-Auslöser ist ein fünftes, das nur `Worker` ruft. Beides ist in
Abschnitt 6 als Befund festgehalten.

### 2.2 Typen (alle im Worker-Projekt, kein TwitchLib-Typ darin)

- `enum TwitchSessionEndReason`: `Disconnected` (TwitchLibs `OnDisconnected`), `ConnectionError`
  (`OnConnectionError` allein), `FrameStale` (Tick, 15 min), `DisconnectedBackstop` (Tick, darf nie
  feuern), `InitialConnectFailed` (Boot), `ConnectFailed` (`ConnectAsync` lieferte `false`),
  `ConnectFaulted` (Exception beim Öffnen), `HandshakeTimeout` (Socket offen, kein `004` in 10 s),
  `UnexpectedInPlaceReconnect` (Stolperdraht `OnReconnected`), `DebugTrigger` (Task 6).
- `TwitchSessionResult(TwitchSessionEndReason Reason, TimeSpan? SessionDuration)` — Eingang der
  Backoff-Policy, analog `SevenTvSessionResult`. `SessionDuration` ist die Zeit vom `004` bis zum
  Verlust; **`null` heißt: es gab keinen Handshake** (Fehlversuch).
- `TwitchReconnectRequest(TwitchSessionEndReason Reason, string Detail, TimeSpan? SessionDuration, DateTime RequestedUtc, int ClientGeneration)`
  — was der Slot hält. Der Manager füllt `SessionDuration` aus seinem `004`-Zeitstempel und
  `ClientGeneration` aus dem Client, der das Signal ausgelöst hat (beim Tick und beim Boot: der
  aktuelle).
- `TwitchConnectOutcome(bool HandshakeCompleted, TwitchSessionEndReason? FailureReason, TimeSpan Elapsed, int ClientGeneration)`.
- `TwitchRejoinOutcome(int Desired, int Confirmed, int Open, bool Aborted)`.
- `enum TwitchJoinSource`: `Command`, `ConvergenceNet`, `Rejoin` — manager-intern (2.1), nicht auf
  dem Interface.
- **Client-Generation:** der Manager nummeriert jeden erzeugten `TwitchClient` fortlaufend ab 1
  (der Boot-Client ist #1, jeder Versuch der Schleife erzeugt die nächste Nummer). Die Nummer ist
  der Schlüssel der Slot-Regel (2.3) und die „Rebuild-ID" der Logzeilen (2.7); sie wird nie
  zurückgesetzt.

Wo sie liegen: die beiden Policy-Typen (`TwitchSessionEndReason`, `TwitchSessionResult`) in der Datei
der Policy (Muster `SevenTvBackoffPolicy.cs`), die drei Manager-Typen neben `TwitchRosterEntry` in
`ITwitchChatManager.cs` (den Request darf Task 1 vorläufig beim Slot ablegen, Task 4 verschiebt
ihn), `TwitchJoinSource` im Manager, der Slot als eigene Datei
`src/EmotePurge.Worker/TwitchReconnectSignalSlot.cs` (Task 1).

### 2.3 Der Signal-Slot (reine Klasse, vom Manager gehalten)

`TwitchReconnectSignalSlot` — TwitchLib-frei, uhrfrei (Zeitstempel kommen von außen), getestet in
Task 1. Kapazität 1, latchend, nicht flankengetriggert. Drei Operationen, in Prosa:

- **Anbieten** (`RequestReconnect` ruft es): Ist die Generation des Signals **≤ der zuletzt
  verurteilten**, wird das Signal **verworfen** — Rückgabe sagt das, der Manager loggt Information
  „… verworfen, Client #g ist bereits ersetzt". Sonst auf leeren Slot: ablegen, Warter wecken,
  Information-Log. Auf vollen Slot: **nur den Grund und das Detail ersetzen** (Konzept 4.7),
  Zeitstempel, Sitzungsdauer und Generation bleiben vom ersten Signal; Information-Log „…
  zusammengefasst".
- **Nehmen** (`WaitForReconnectRequestAsync`): wartet, bis ein Signal liegt, liefert es, leert den
  Slot **und verurteilt die Generation des gelieferten Signals** — beides unter demselben Lock, als
  ein Schritt, sonst kann ein Handler auf dem Lese-Thread zwischen Leeren und Verurteilen ablegen.
- **Verurteilt** ist die höchste bisher genommene Generation; sie wächst monoton.

Warum das nötig ist (Abschnitt 7, PG1): Fall A liefert bis zu zwei `OnDisconnected` und ein
`OnConnectionError` binnen 2,5 s (Konzept 3.2). Bei 0 s Verzögerung ist der alte Client ersetzt,
bevor die Nachzügler kommen, und sie fallen ins Leere. Bei 5 s (Flap-Boden) oder 10 s
(Stolperdraht) ist er noch verdrahtet: ohne die Generationsregel füllte sein `OnConnectionError`
den gerade geleerten Slot, und die Schleife risse nach dem gelungenen Rejoin die frische Verbindung
ab — „viele Signale = ein Wiederaufbau" wäre verletzt. Die Regel macht das Verhalten
zeitunabhängig; die drei Logzeilen aus Konzept 4.7 bleiben, wo der Boden sie zulässt.

Die TwitchLib-Handler (`OnDisconnected`, `OnConnectionError`, `OnReconnected`) rufen
`RequestReconnect` nur, wenn `sender` der aktuelle `_client` ist **und** dieser Client den
Handshake hatte (1.3, Punkt 1) — die Handshake-Regel bleibt neben der Generationsregel nötig, weil
ein Versuchs-Client ohne `004` eine **höhere** Generation als die verurteilte trägt und sein
`OnDisconnected` sonst als echtes Signal läge; sonst loggen sie und tun nichts. Kein Handler baut
auf, wartet oder rejoint (E1).

### 2.4 `TwitchReconnectBackoffPolicy` (rein, uhrfrei, injizierbarer Jitter)

Ein Eingang: `TimeSpan NextDelay(TwitchSessionResult result)`. Konstanten öffentlich, damit die
Tests keine Zahlen duplizieren: erster Versuch 0 s; Basis 2 s verdoppelnd; Deckel 30 s
**einschließlich** Jitter (Jitter ± 20 % auf den Rohwert, danach auf 30 s gekappt); Exponent-Deckel 5;
Flap-Schwelle 60 s, Flap-Boden 5 s ab der dritten kurzen Sitzung in Folge; Stolperdraht-Boden 10 s.

Verhalten, in Prosa:

- **Sitzung beendet** (`SessionDuration` gesetzt): Fehlversuch-Streak → 0. Dauer < 60 s zählt eine
  kurze Sitzung, ≥ 60 s setzt die kurzen Sitzungen auf 0. Rückgabe 0 s; ab der dritten kurzen
  Sitzung in Folge 5 s. Grund `UnexpectedInPlaceReconnect` hebt auf mindestens 10 s.
- **Fehlversuch** (`SessionDuration` null): Streak → min(Streak + 1, 5); Rückgabe
  2 s × 2^(Streak−1) mit Jitter, gekappt auf 30 s. Eine kurze Sitzung wird dadurch weder gezählt noch
  vergessen (1.3, Punkt 4).
- **Dämpfung aktiv** (drei kurze Sitzungen, noch keine ≥ 60 s): jede Rückgabe ist mindestens 5 s.
- Keine Versuchsobergrenze, kein `Reset()` von außen nötig — der Handshake ist der Reset.

Eine kurze Sitzung ist ein **Sitzungsergebnis**, kein Fehlversuch — das ist G1 der Gegenrede und der
Unterschied zur ersten Konzeptfassung; die Tests in Task 1 pinnen genau das.

### 2.5 `TwitchWatchdogPolicy`

`Decide(bool isConnected, TimeSpan? sinceOpenAttempt, TimeSpan? sinceLastFrame, TimeSpan? sinceLastForcedReconnect)`.
Unverändert: Disconnected-Zweig mit 1-min-Cooldown (jetzt der Backstop, Grund-Text unverändert
„TwitchClient meldet sich als getrennt."), Stale-Zweig 15 min mit 15-min-Cooldown. Der
`clientSpent`-Zweig samt Parameter entfällt **ersatzlos** — kein `reconnectInFlight` (1.3, Punkt 2;
Abschnitt 7, Nebenbefund). Der Klassenkommentar sagt, warum es keinen In-Flight-Eingang gibt: die
Schleife ist der einzige Aufrufer und tickt nur, während sie wartet; ein solcher Eingang käme mit
einem eigenen Tick-Timer zurück, nicht vorher.

### 2.6 Die Schleife in `TwitchConnectionWatchdog`

Trägerin bleibt der Watchdog, Name bleibt (Entscheidung 7.3). Ein sequentielles `ExecuteAsync`,
fehlerisoliert je Durchlauf (Muster `SevenTvEventWorker`), nie den Host mitreißend:

```
warten(Signal, ≤ 60 s)
  ├─ Zeitschranke → Tick: Decide(...) → ggf. LogLiveContext + RequestReconnect(FrameStale | DisconnectedBackstop)
  └─ Signal (Nehmen verurteilt dessen Generation) → Wiederaufbau: delay = NextDelay(Signal) → [Delay(ct) → ReconnectOnceAsync(ct) → bei Fehlschlag delay = NextDelay(Fehlversuch), wiederholen] → RejoinDesiredChannelsAsync(ct) → zurück zu warten
```

- Zähler: je Signal `WorkerStats.RecordTwitchRebuild()`, je Fehlversuch
  `RecordTwitchRebuildAttemptFailure()` (Task 2). Beide kumulativ seit Prozessstart, erscheinen in
  der Abschlusszeile — und **nirgends sonst** (Entscheidung 7.4).
- `LogLiveContextAsync` (Helix-Kontext) bleibt und läuft **nur** vor einem Tick-Signal — seine
  Begründung („erklärt Stille, beweist nichts") gilt nur für den Stale-Fall; der ereignisgetriebene
  Pfad darf nicht auf Helix warten.
- `_lastForcedReconnectUtc` wird gestempelt, wenn der Tick ein Signal ablegt.
- Shutdown-Zusage: jedes Warten — Slot, Delay, `ConnectAsync().WaitAsync`, `004`, `_joinGate`,
  600-ms-Pause, Bestätigungswarten — nimmt das Stopping-Token. `StopAsync` kehrt ≤ 1 s nach dem
  Token zurück, in jedem Zustand. Ein verlassener Connect wird im Hintergrund beobachtet (Exception
  geloggt, Client verworfen), nie abgewartet.

### 2.7 Logzeilen-Vertrag

Die Logzeilen sind das Messinstrument aus Konzept 6 (G4-Lehre: der Beleg ist positiv, nicht die
Abwesenheit). Die Anker müssen deshalb so heißen; Platzhalter und Satzbau entscheidet der
Implementer. Log-Messages deutsch (Sprachregel).

| Anker (grep-fähig) | Ebene | Wann |
|---|---|---|
| `TwitchClient getrennt` | Warning | `OnDisconnected` des aktuellen Clients mit Handshake |
| `TwitchClient meldet Verbindungsfehler` … `Fatal network error` | **Information** | `OnConnectionError` — erwartete Folgezeile eines Verlusts, **höchstens** einmal je Verlust: sie kommt ≈ 2 s nach `OnDisconnected` (Konzept 3.2) und erreicht uns nur, wenn der alte Client dann noch verdrahtet ist — bei 0 s Verzögerung regulär **nicht**, bei 5-/10-s-Boden ja (Abschnitt 7, Widerspruch W1) |
| `Twitch-Verbindung verloren` … `Wiederaufbau #1 in {Delay}s` … `Client #{Generation}` | Warning | Signal konsumiert; Grund, Sitzungsdauer und Generation des verlorenen Clients in der Zeile |
| `Wiederaufbau #{n} fehlgeschlagen` … `nächster Versuch in {Delay}s` | Warning | je Fehlversuch mit Grund (`ConnectAsync` false / Exception / `Handshake nicht abgeschlossen`) |
| `TwitchClient verbunden` | Information | `OnConnected` (`004`), wie heute |
| `Twitch-Verbindung steht nach {Seconds}s (Versuch #{n}, Client #{Generation})` | Information | nach dem Handshake; **SLO-1-Zeile** (6.6) |
| `JOIN für {Channel} angestoßen (Quelle {Source}, Client #{Generation})` | Information | `TryJoinAsync` unmittelbar vor `_client.JoinChannelAsync`, nach Gate und Wunschzustandsprüfung; die **Ursprungszeile** — sie sagt, wer den JOIN wollte, TwitchLibs „Joining channel" sagt, dass er gesendet wurde (Abschnitt 7, PG3) |
| `Rejoine {Count} gewünschte(n) Channel(s)` | Information | Start der Runde, wie heute |
| `Channel {Channel} gejoint` | Information | wie heute |
| `Rejoin abgeschlossen (Client #{Generation}): {N} gewünscht, {M} bestätigt, {K} offen, {T}s seit Verlust` + `seit Prozessstart: {Rebuilds} Wiederaufbauten, {Failures} Fehlversuche` | Information; **Warning bei K > 0 oder Abbruch** | Ende der Runde; **SLO-2-Zeile** |
| `Join für {Channel} aufgeschoben` | **Information** (war Warning; Entscheidung 7.7) | `TryJoinAsync` bei `!_isConnected` |
| `Weiteres Verlust-Signal` … `zusammengefasst` | Information | `RequestReconnect` auf vollen Slot |
| `Verlust-Signal verworfen` … `Client #{Generation} ist bereits ersetzt` | Information | `RequestReconnect` mit verurteilter Generation (2.3) — der positive Beleg für PG1 in S4 |
| `Erzwinge Reconnect: {Reason}` | Warning | Tick; Reason ist `Kein IRC-Frame seit …` oder `TwitchClient meldet sich als getrennt.` (Backstop — darf nie erscheinen, R1) |
| `TwitchClient reconnected` … `darf mit NoReconnectionPolicy nicht auftreten` | **Error** | Stolperdraht `OnReconnected` |
| `Aufräumen des alten TwitchClient` … `fehlgeschlagen (ignoriert)` | Warning | Hintergrund-Cleanup (E4, R5) |
| `Debug-Auslöser: Twitch-RECONNECT wird injiziert` / `Debug-Auslöser ignoriert` | Warning | Task 6, freigeschaltet / nicht freigeschaltet |

TwitchLib selbst liefert nach Task 4 auf `Information`: „Joining channel: …", „Leaving channel: …",
„Connecting Twitch Chat Client…", „Reconnecting to Twitch" (Binärstand `LogExtensions`, Konzept 6.1).

### 2.8 Konfiguration und Compose

- `appsettings.json` **und** `appsettings.Development.json`: `TwitchLib.Client.TwitchClient` von
  `Error` auf `Information` (Entscheidung 7.10, im Commit von Task 4).
- `Worker:Debug:AllowTwitchReconnectTrigger` (bool, Default `false`; per Env
  `Worker__Debug__AllowTwitchReconnectTrigger=true`) — schaltet den Debug-Auslöser frei (Task 6).
  Kommt **nicht** in `docker-compose.yml`, nicht in `.env.example`; wird nur für Lauf A/B über eine
  lokale Override-Datei gesetzt (Task 9).
- `SevenTv__ResyncIntervalSeconds` (bestehend, Default 60) wird für die Messläufe in Task 9 über
  dieselbe Override-Datei auf **3600** gesetzt, damit der Konvergenz-Tick (`EnsureJoinedAsync`)
  nicht in ein Messfenster fällt (Abschnitt 7, PG3). Nichts davon ändert Code oder Defaults.
- `docker-compose.yml` und `docker-compose.prod.yml`: `stop_grace_period: 60s` am `worker`
  (Task 5). `ShutdownTimeout` des Hosts bleibt beim .NET-Default 30 s; die 60 s decken ihn mit
  Reserve (#122).

---

## 3. Tasks

Modellwahl: Task 0 `sonnet` (Sonde bauen und fahren), Auswertung des Befunds `opus`; Task 1–3, 5,
6, 7 `sonnet`; Task 4 `opus` (Transport und Schleife, das größte Fehlerrisiko); Task 8 `sonnet`;
Task 9 `opus` mit dem Nutzer für die Host-Kommandos. Jeder Code-Task endet mit
`dotnet build EmotePurge.slnx --no-incremental` ohne neue Warnungen,
`dotnet format EmotePurge.slnx --verify-no-changes` und `dotnet test tests/EmotePurge.Worker.Tests`
grün; die volle Suite fährt Task 8. Task 0 berührt das Repo nicht.

### Task 0 — JOIN-Limit-Sonde (Messung, vorgezogen; kein Repo-Code)

**Zweck.** Drei Fragen, die das Konzept offen lässt und deren Antwort den Entwurf verändern kann
(1.2), **vor** der Implementierung mit positiv formulierten Belegen beantworten:

- **F1 — die Rate.** Ab welcher Kanalzahl bzw. JOIN-Rate greift Twitchs Grenze von 20 JOINs pro
  10 s auf einer anonymen `justinfan`-Verbindung, und **wie** greift sie: einzelne Kanäle ohne
  Bestätigung (die Annahme des Konzepts, 1.1: „zufällige Bestandskanäle verlieren ihren Join" —
  unbelegt), eine `NOTICE`, ein Verbindungsabbruch, oder ein stiller Drop?
- **F2 — die Decke.** Gilt die Decke von 100 gleichzeitig gejointen Chatrooms für eine anonyme
  Verbindung? Das Konzept führt sie in 4.5 und 7.2 als bleibende Grenze; gemessen ist sie für
  `justinfan` nicht. **Geltungsbereich, der in jeden Satz über F2 gehört:** Die Sonde verbindet
  anonym. Sie kann zeigen, **ob** Twitch die Decke durchsetzt und **wie** sie sich meldet — sie
  belegt **nicht**, dass für einen benannten Account dasselbe gilt. Das ist die zweite offene Frage
  aus #68 (hängt die Grenze am Account oder an der Client-ID?), und die Sonde beantwortet sie nicht.
  Ein benannter Gegentest ist seit dem 2026-09-08 technisch möglich (Account `EmotePurgeBot` mit 2FA
  besteht), gehört aber nicht in diesen Task: der Worker verbindet weiterhin anonym, und das ändert
  sich frühestens nach dem 2026-10-08.
- **F3 — Flapping (Konzept 7.8).** Lehnt Twitch schnell wiederholte anonyme Verbindungsaufbauten
  ab, und ab welcher Rate? Die Zuschreibung vom 2026-07-27 ist überholt; es gibt keinen belegten
  Fall.

**Aufbau — ein eigener, kurzlebiger Sondenprozess.** Ein Konsolenprojekt **im Scratchpad**, nicht
im Repo, mit derselben `TwitchLib.Client 4.0.1` / `TwitchLib.Communication 2.0.1` wie der Worker,
`ClientOptions(new NoReconnectionPolicy())` und parameterlosen `ConnectionCredentials()` (anonym,
wie `TwitchChatManager.cs:255`). Es tut drei Dinge: verbinden, JOINs in einer vorgegebenen Kadenz
senden, und **jede** empfangene IRC-Zeile mit Host-Zeitstempel in eine Datei schreiben
(`OnSendReceiveData`, Richtung `Received` — nur so sind `NOTICE`-Zeilen und die `366`-Bestätigungen
als Rohtext greifbar; TwitchLibs `OnJoinedChannel`/`OnFailureToReceiveJoinConfirmation` werden
daneben mitgeschrieben, sind aber nicht der Primärbeleg).

> **Korrektur vom 2026-09-08, am Binärstand belegt:** Für die **Sende**richtung ist
> `OnSendReceiveData` **nicht** verwendbar. Das Event hat genau zwei Aufrufstellen; `Sent` feuert
> ausschließlich aus `SendRawAsync`, während `JoinChannelAsync` über die interne Queue
> (`QueueingJoinCheckAsync`) direkt `SendAsync` auf dem Socket-Client aufruft und das Event damit
> umgeht. Ein Join-only-Lauf erzeugt strukturell **null** `Sent`-Zeilen. Belastbarer Ersatz ist
> TwitchLibs eigener strukturierter Logeintrag `LogJoiningChannel`, der synchron unmittelbar vor
> dem `SendAsync` abgesetzt wird (dazwischen nur ein unbelasteter Semaphor). Der Sendezeitpunkt
> wird daraus gestempelt; die eigene Zeile „JOIN angestoßen" ist **nicht** der Sendezeitpunkt,
> weil `JoinChannelAsync` sofort zurückkehrt. Das gilt auch für Task 9: Wer dort Sendezeitpunkte
> braucht, nimmt dieselbe Quelle.

Zwei Kadenzen müssen wählbar sein:
**Queue-Kadenz** (TwitchLibs `JoinChannelAsync`, sendet den nächsten JOIN auf die Bestätigung des
vorherigen — **gemessen 126–231 ms Median je Lauf**, nicht die zuvor angenommenen konstanten
≈ 200 ms; der Pfad, den Prod bis heute fährt) und **gedrosselt** (600 ms, unser
`TryJoinAsync`-Abstand). Das ist alles; der Prozess schreibt weder in Postgres noch in Redis und
kennt keine Projektabhängigkeit.

**Was er nicht darf.** Nicht den laufenden `emotepurge-dev-worker` benutzen, stoppen oder neu
bauen; nicht in die geteilte Dev-Datenbank schreiben — dort läuft die #73-Negativprobe. Nicht die
Kanäle des Dev-Workers joinen (ein sauberes Bild, nicht wegen Twitch). Nichts committen — das
Ergebnis ist der Befund, nicht der Sondencode.

**Kanalnamen.** Für F2 braucht es > 100 **echte** Kanäle: über Helix `GET /helix/streams` mit
`first=100` und dem `after`-Cursor der Antwort (Pagination) sind 120+ aktuell live sendende Logins
in zwei Aufrufen da. Das App-Access-Token dafür beschafft die Sonde selbst per
Client-Credentials-Flow gegen `id.twitch.tv/oauth2/token` mit `TWITCH_CLIENT_ID`/`TWITCH_CLIENT_SECRET`
aus der lokalen `.env` (dieselbe App wie `TwitchLivePollWorker`, derselbe Token-Typ; nichts davon
ins Repo, Regel 17). Live-Kanäle sind laut — für die Sonde egal, sie zählt nichts; sie kosten nur
Bytes. Die Liste wird einmal gezogen und für alle Stufen wiederverwendet (Reihenfolge fest, damit
„Kanal k" über die Stufen dieselbe Bedeutung hat).

**Gestaffeltes Hochfahren — Sicherheitsauflage.** Die Sonde läuft von **derselben Wohn-IP** wie der
Dev-Worker. **Annahme, nicht Tatsache:** sollte Twitch IP-seitig drosseln oder sperren, träfe das
die laufende #73-Negativprobe mit. Deshalb Stufen statt Höchstzahl, und ein Abbruchkriterium, das
vor jedem Schritt gilt:

| Stufe | Kanäle | Kadenz | Frage |
|---|---|---|---|
| 1 | 28 | Queue | Reproduktion des 2026-07-30 — die Sonde muss das Bekannte zeigen, bevor sie Neues behauptet |
| 2 | 40 | Queue | F1 |
| 3 | 60 | Queue | F1 |
| 4 | 100 | **gedrosselt 600 ms** | F2 — gedrosselt, damit die Decke von der Rate getrennt ist (≈ 60 s für 100 JOINs, unter 20/10 s) |
| 5 | 120 | gedrosselt 600 ms | F2 über der Decke — Ergebnis gilt **anonym**, s. Geltungsbereich bei F2 |

Je Stufe eine **frische** Verbindung; zwischen den Stufen Verbindung trennen und **≥ 2 min** warten.
**Abbruchkriterium:** sobald in einer Stufe ein JOIN binnen 10 s unbestätigt bleibt, eine `NOTICE`
mit Bezug auf Joins oder Limits kommt, die Verbindung abreißt, **oder** der Dev-Worker parallel
„Join für … nicht bestätigt" / „TwitchClient getrennt" loggt (`docker logs -f emotepurge-dev-worker`
läuft die ganze Zeit mit — das ist der Indikator für die IP-Annahme), wird **nicht** weiter
hochgefahren. Dann folgt die Kontrolle (s. u.), der Befund wird geschrieben, die höheren Stufen
bleiben ungemessen und stehen als solche im Befund. Nach zwei Fehlschlägen der Sonde selbst
(Verbindung kommt nicht zustande, Helix antwortet nicht) abbrechen und den Nutzer fragen, statt
weiter zu probieren (Projektnotiz vom 2026-08-30).

**Positive Belege — je Frage (G4: „Zeile fehlt" beweist nichts).**

- F1, Grenze **biss nicht:** Für die Stufe liegt eine Tabelle vor — je Kanal Sendezeit des JOIN und
  Zeit der `366` — **und** das größte 10-s-Fenster enthält nachweislich **mehr als 20** gesendete
  JOINs (die Zahl steht im Befund; ohne sie hat die Stufe die Grenze nicht berührt und beweist
  nichts), **und** N von N bestätigt binnen 10 s.
- F1, Grenze **biss:** mindestens ein JOIN ohne `366` binnen 10 s **und** die Kontrolle: derselbe
  Kanal, nach 15 s Pause einzeln erneut gesendet, wird bestätigt (der Kanal war es nicht, die Rate
  war es) — **oder** eine `NOTICE`-Zeile im Rohlog, wörtlich zitiert. Dazu die Form: welche Kanäle
  (Position in der Sendefolge — die ersten? die letzten? verstreute?), ob die Verbindung stand
  (nächste Server-Nachricht nach dem Ereignis, mit Zeit), ob eine Bestätigung später doch kam.
- F2, Decke **gilt nicht** (oder liegt höher): 120 von 120 bestätigt, bei nachweislich ≤ 20 JOINs je
  10-s-Fenster (die Drosselung isoliert die Frage). **Dieser Satz wird nur mit seinem Geltungsbereich
  notiert** — „für eine anonyme Verbindung von einer Wohn-IP, an diesem Tag". Ohne ihn wird daraus
  später versehentlich „die Decke ist kein Problem", und genau das trägt der Beleg nicht.
- F2, Decke **gilt:** JOIN #101 (und folgende) ohne `366` **und** die Kontrolle: nach `PART` von
  fünf bestätigten Kanälen werden fünf neue JOINs bestätigt (es war der Bestand, nicht die Rate) —
  oder eine `NOTICE`, wörtlich.
- F3: Serie von fünf Verbindungsaufbauten binnen 60 s (Connect → `001`/`004` → ein JOIN → `366` →
  trennen), dann zehn binnen 60 s, dann zwanzig; Abbruch bei der ersten Serie, in der ein Aufbau
  kein `001` liefert oder Twitch die Verbindung schließt. Positiver Beleg je Serie: n × `001` mit
  Zeitstempeln. **Drei mögliche Ergebnisse, und nur eines davon ist eine Zuschreibung** (Abschnitt
  7, PG4 — ein fehlendes `001` ist kein Kausalbeleg, so wenig wie eine fehlende „Joining"-Zeile in
  G4 einer war): (a) **kein Befund** — jede Serie liefert n × `001`; (b) **zugeschrieben** — nur
  mit expliziter Servermeldung: eine `NOTICE`- oder Fehlerzeile im Rohlog, wörtlich zitiert, **oder**
  ein serverseitiges Schließen **nach** gelungenem TLS-/WebSocket-Aufbau (Close-Frame bzw. `001`
  bleibt bei offenem Socket aus), das an **derselben Seriengrenze in zwei unabhängigen Durchgängen
  ≥ 5 min auseinander** wiederkehrt, **und** eine zwischengeschaltete Niedrigraten-Kontrolle (ein
  einzelner Aufbau zwischen zwei Serienversuchen) gelingt sofort — sie zeigt, dass es die Rate war
  und nicht die Leitung; (c) **inconclusive** — alles andere: ein einzelner Aufbau ohne `001`, ein
  DNS-/TLS-/Timeout-Fehler ohne Servermeldung, ein Fehler, der sich nicht reproduziert. Die
  Kontrolle „nach 60 s Ruhe gelingt ein Aufbau wieder" bleibt Teil des Protokolls, belegt aber nur
  die Erholung, keine Ursache. Zur Einordnung im Befund: die Schleife aus Task 4 erzeugt im
  schlimmsten Fall (Dauer-Flapping, 5-s-Boden) zwölf Aufbauten pro Minute — die zweite Serie liegt
  darüber, die dritte deutlich.

**Ergebnisverwertung — der Task endet mit einem Befund, nicht mit einer Logdatei.**

1. Der Befund geht als Kommentar an **#68** (per `gh issue comment`; `gh issue view/edit` stolpern
   über Projects-Classic, das Kommentieren nicht): je Frage die Antwort mit dem positiven Beleg, die
   Tabellen der gefahrenen Stufen, das größte 10-s-Fenster je Stufe, welche Stufen ungemessen blieben
   und warum, das Rohlog als Anhang oder Pfad. Dazu die Einordnung: **ein Datenpunkt von einer
   Wohn-IP, gültig nur für den anonymen Betrieb** — bei einem Wechsel auf einen benannten Bot-Account
   ist die Messung **neu zu erheben**, weil Twitch die Limits als Account-Limits dokumentiert. Ob sie
   am Account oder an der Client-ID hängen, ist die zweite offene Frage aus #68 und bleibt nach dieser
   Sonde offen; sie ist mit `EmotePurgeBot` messbar, aber nicht in diesem Task.
2. Der Befund sagt **ausdrücklich**, ob und wie er verändert: (a) **Konzept 7.2 / `WorkerCapacity.cs:26`**
   — bleibt die 20 mit der neuen Begründung aus Task 7 (lineare Rejoin-Dauer, Decke, 7TV-Grenze),
   oder gibt es eine gemessene Zahl, die die Begründung ersetzt; (b) **R3 in Abschnitt 5 — als
   Go/No-Go, nicht als Zahl.** Ergebnis (a) oder (c) aus F3 heißt **Go**: Task 1 baut die
   Konzeptzahlen aus 2.4 (0 s, Boden ab der dritten kurzen Sitzung), und ein inconclusive-Befund
   steht mit seinem Wortlaut in R3 — der R3-Beleg bleibt Prod (24-h-Zahlen, Konzept 6.5). Ergebnis
   (b) heißt **Stop**: Task 1 startet nicht; der Nutzer entscheidet, das Konzept bekommt in 3.6
   einen Nachtrag, und **dieser Plan wird vor Task 1 in einer neuen Fassung durchgezogen** — 2.4,
   Task 1, Task 4-Doku, S1/S3/S4 in Task 9, R3 — mit den beiden Stellschrauben aus R3 (erster Delay
   1–2 s, Boden ab der zweiten Sitzung) als **einem** konkreten Wert je Konstante. Die erste Fassung
   ließ Task 1 die Konstanten allein ändern, während Task 9 auf 0 s abnahm — das war ein
   eingebauter Widerspruch (Abschnitt 7, PG2). Innerhalb dieses Plans ändert F3 **keine** Konstante;
   (c) die
   **Dauerauflage aus Epic #118** („keine Ausweitung über ~20 gejointe Kanäle") — trägt ihre
   Begründung (der ungedrosselte Rejoin sprengt das Fenster) angesichts von F1 noch, und was gilt
   nach dem Deploy dieses Plans, wenn es den ungedrosselten Pfad nicht mehr gibt. Die Entscheidung
   über die Auflage trifft der Nutzer in #118; der Befund liefert die Zahl.
3. Was der Befund **nicht** ändert, egal wie er ausfällt: das Modell (E1–E5), die Drosselung auf
   600 ms (7.6), das Deploy-Fenster (Abschnitt 0), und — innerhalb dieses Plans — die
   Konstanten in 2.4 (Punkt 2b). Die Sonde misst Twitch, nicht unser Design — sie liefert Zahlen
   für Begründungen (2a, 2c) und ein Go/No-Go (2b), keine Form.

### Task 0 — Ergebnis vom 2026-09-08 (F1 und F2 gemessen, F3 offen)

**Gefahren:** sieben Läufe von der Devbox (Wohn-IP), Belege in zwei Kommentaren an #68.

- **F1 — die 20-JOINs-pro-10-s-Grenze griff nicht.** 208 JOINs anonym über fünf Läufe, alle
  bestätigt, Höchstwert **45 JOINs in einem 10-s-Fenster** (Faktor 2,2), keine limitbezogene
  `NOTICE`. **Go** für Punkt 2b: Task 1 bleibt bei den Konzeptzahlen.
- **F2 — die 100er-Decke existiert, gilt aber dem Account.** A/B mit identischem Aufbau und nur
  getauschter Identität: anonym **120/120**, als `emotepurgebot` Schluss bei Kanal 101 mit
  `msg_concurrent_channel_limit_reached` — eine explizite Servermeldung, kein Rückschluss aus einer
  fehlenden Zeile. **Damit hängt die Decke am Account, nicht an der Client-ID.**
- **F3 — nicht gefahren.** Bleibt offen; R3 und der 0-s-Erstversuch sind dadurch unberührt.

**Die Sendekadenz ist keine Konstante.** Der ungedrosselte Queue-Pfad — derselbe Mechanismus, den
TwitchLibs Rejoin nach einem Reconnect benutzt — lag im Median je Lauf zwischen **126 und 231 ms**,
nicht bei den zuvor angenommenen festen ≈ 200 ms. Für die SLO-2-Formel heißt das: `0,6 s × (N−1)`
beschreibt die **gedrosselte** Runde und bleibt gültig; wer die ungedrosselte Dauer abschätzt, darf
keine feste Konstante unterstellen. Bei unseren 15 Kanälen liegt die ungedrosselte Rejoin-Runde bei
rund 2–3,5 s und damit strukturell unter 20 JOINs je Fenster — die Rate-Grenze ist bei heutiger
Größe kein Risiko, unabhängig von der Drosselung.

**Konsequenz für 7.2 (Task 7):** Die Zahl 20 bleibt, aber **beide** bisherigen Begründungen tragen
für den anonymen Betrieb nicht mehr — weder die Rate noch die Decke wurde bis 120 Kanäle erreicht.
Was bleibt, ist die lineare Rejoin-Dauer und das, was diese Sonde **nicht** misst: Dauerbetrieb über
Stunden, Chat-Volumen, Worker-Speicher. Der Kommentar an `WorkerCapacity.cs` muss das so sagen und
darf die Messung nicht als Freibrief lesen.

**Konsequenz außerhalb dieses Plans:** Ein Umstieg auf einen benannten Login (#125, EventSub-
Conduits) **führt die 100er-Decke ein**, die der heutige anonyme Betrieb nicht hat. Ob die
Bot-Verification sie hebt, ist unbelegt — das Formular spricht von *rate limits*, getroffen hätte
uns ein *concurrency*-Limit. Nicht als gegeben behandeln.

**Nicht belegt, aber beobachtet:** Ein anonymer Lauf brach bei 80 ab, 2 min nach einem Lauf mit
100 Kanälen; nach zwei Stunden Ruhe lieferte derselbe Lauf 120/120. Das legt eine **verzögerte
Freigabe der Kanäle einer eben getrennten Verbindung** nahe. Falls das zutrifft, beträfe es den
Rejoin unmittelbar — vor dem bindenden Lauf nicht darauf verlassen, sondern eigens messen.

**Fertig.** Kommentar an #68 steht; Task 1 startet erst, wenn Punkt 2b **Go** sagt (bei Stop:
neue Planfassung zuerst); Task 7 übernimmt die Konsequenz für 7.2 (Punkt 2a) in den Kommentar an
`WorkerCapacity.cs`. Kein Commit (nichts im Repo berührt).

### Task 1 — `TwitchReconnectBackoffPolicy`, die Sitzungs-Typen und der Signal-Slot (rein, getestet)

**Zweck.** Die Backoff-Kurve aus Konzept 3.6 samt G1-Korrektur als reine Klasse nach dem Muster
`SevenTvBackoffPolicy`, und der Signal-Slot mit der Generationsregel (2.3) als zweite reine Klasse
— beides, bevor irgendein Transportcode sie braucht. Der Slot ist hier und nicht in Task 4, weil
Task 4 nach Regel 11/16 keine Fake-Tests bekommt, die Generationsregel aber genau den
deterministischen Zustandstest braucht, den die Gegenrede verlangt (Abschnitt 7, PG1).

**Betroffene Dateien.** Neu: `src/EmotePurge.Worker/TwitchReconnectBackoffPolicy.cs` (Policy plus
`TwitchSessionEndReason` und `TwitchSessionResult`, 2.2), `tests/EmotePurge.Worker.Tests/TwitchReconnectBackoffPolicyTests.cs`;
`src/EmotePurge.Worker/TwitchReconnectSignalSlot.cs`, `tests/EmotePurge.Worker.Tests/TwitchReconnectSignalSlotTests.cs`.
`TwitchReconnectRequest` (2.2) zieht mit dem Slot in dessen Datei, wenn `ITwitchChatManager.cs`
in diesem Task noch unangetastet bleiben soll; Task 4 darf ihn dorthin verschieben.
Lesen: `SevenTv/SevenTvBackoffPolicy.cs`, `tests/…/SevenTvBackoffPolicyTests.cs`.

**Voraussetzung.** Punkt 2b des Befunds aus Task 0 sagt **Go**. Die Konstanten sind die aus 2.4,
ohne Variante — ein Stop-Befund erzeugt erst eine neue Planfassung, nie eine abweichende Task-1-
Implementierung gegen unveränderte Gates in Task 9 (Abschnitt 7, PG2).

**Vertrag.** 2.4. Klassenkommentar (englisch) nennt: warum 30 s und nicht 60 s wie 7TV (Deckel =
maximale zusätzliche Zähllücke nach Ende eines Twitch-Ausfalls), warum der Streak nur Fehlversuche
zählt (G1: die Stabilitätsregel hätte anhaltendes Flapping 34–47 s je Ereignis gekostet, und ihre
Abuse-Begründung war seit dem 2026-07-30 überholt), warum die Flap-Dämpfung ein Boden ist und kein
zweiter Backoff (≤ 720 Aufbauten/h statt 1.800), und dass die 5 s Vorsicht sind, keine Kalibrierung
(7.8). Regel 19 für die Member-Reihenfolge; Jitter injizierbar, `jitter() == 0.5` ergibt den
Rohwert.

**Tests (Verhaltensbeschreibungen, neutraler Jitter, sofern nicht anders gesagt).**

1. Nach einer beendeten Sitzung von zehn Minuten ist die erste Verzögerung 0 s.
2. Aufeinanderfolgende Fehlversuche ergeben 2, 4, 8, 16, 30, 30 s.
3. Am Deckel ist der Jitter gekappt: mit Jitter-Faktor 1,0 (oberes Band) liefert der fünfte
   Fehlversuch exakt 30 s, nicht 36 s.
4. Unterhalb des Deckels liegt der Jitter im ± 20-%-Band (erster Fehlversuch: 1,6 s bzw. 2,4 s).
5. Zwanzig Fehlversuche in Folge liefern weiterhin 30 s — kein Überlauf, der Exponent ist gedeckelt.
6. Eine beendete Sitzung setzt den Fehlversuch-Streak zurück: drei Fehlversuche, eine lange
   Sitzung, dann ein Fehlversuch → 2 s.
7. Kurze Sitzungen eskalieren den Streak nicht: zwei Sitzungen von 10 s → 0 s und 0 s.
8. Die dritte kurze Sitzung in Folge liefert 5 s, die vierte ebenfalls.
9. Eine Sitzung ≥ 60 s hebt die Dämpfung auf: danach wieder 0 s.
10. Bei aktiver Dämpfung ist der Boden Schranke für Fehlversuche: erster Fehlversuch 5 s (nicht 2),
    zweiter 5 s (nicht 4), dritter 8 s.
11. Ein Fehlversuch zwischen kurzen Sitzungen zählt nicht als Sitzung und setzt nichts zurück: zwei
    kurze Sitzungen, ein Fehlversuch, eine kurze Sitzung → 5 s.
12. `HandshakeTimeout` zählt als Fehlversuch → 2 s.
13. `UnexpectedInPlaceReconnect` nach einer langen Sitzung liefert 10 s.
14. `FrameStale` mit einer Sitzung von 20 min liefert 0 s und hebt eine vorher aktive Dämpfung auf.

**Vertrag Slot.** 2.3. Uhrfrei: der Zeitstempel kommt im Request mit. Der Klassenkommentar
(englisch) erklärt die Generationsregel an dem Ablauf, der sie nötig macht (Signal genommen →
Boden → Nachzügler des alten Clients → gelungener Rejoin → ohne die Regel ein zweiter Abriss).
Kein TwitchLib-Typ, keine Logger-Abhängigkeit — der Slot **meldet** das Ergebnis des Anbietens
(abgelegt / zusammengefasst / verworfen), der Manager loggt.

**Tests Slot (Verhaltensbeschreibungen).**

1. Anbieten auf leeren Slot legt ab; Nehmen liefert genau dieses Signal und leert den Slot.
2. Anbieten auf vollen Slot ersetzt nur Grund und Detail; Zeitstempel, Sitzungsdauer und
   Generation bleiben vom ersten Signal; das Ergebnis heißt „zusammengefasst".
3. Ein Signal, das vor dem Warten abgelegt wurde, wird beim Nehmen sofort geliefert (Boot-Fall).
4. Nehmen weckt einen wartenden Nehmer; Abbruch über das Token beendet das Warten ohne Signal.
5. **Der PG1-Ablauf:** Signal der Generation 1 genommen → weiteres Signal der Generation 1
   (`ConnectionError`) wird **verworfen**, der Slot bleibt leer → ein Nehmen mit Zeitschranke läuft
   in die Schranke, nicht in ein Signal.
6. Nach dem Ablauf aus 5 legt ein Signal der Generation 2 ab (die neue Verbindung ist ein neuer
   Client) — die Regel verwirft nur Vergangenes.
7. Die verurteilte Generation wächst monoton: nach Nehmen von Generation 3 wird auch ein Signal
   der Generation 2 verworfen.
8. Anbieten auf vollen Slot mit einer verurteilten Generation wird verworfen, nicht zusammengefasst
   (die Reihenfolge der Prüfungen: erst Generation, dann Slot).

**Fertig.** Tests grün, beide Dateien ohne `using TwitchLib.*`, Build ohne neue Warnung. Commits
`feat(worker): add the pure Twitch reconnect backoff policy` und
`feat(worker): add the pure Twitch reconnect signal slot`.

### Task 2 — Zwei kumulative Zähler in `WorkerStats` (rein, getestet)

**Zweck.** Entscheidung 7.4: Wiederaufbauten und Fehlversuche werden nur gezählt und geloggt — kein
Snapshot-Feld, kein Api-Vertrag, kein Admin-Monitoring.

**Betroffene Dateien.** `src/EmotePurge.Worker/WorkerStats.cs`, `tests/EmotePurge.Worker.Tests/WorkerStatsTests.cs`.

**Vertrag.** `RecordTwitchRebuild()` / `TwitchRebuildCount` und `RecordTwitchRebuildAttemptFailure()`
/ `TwitchRebuildAttemptFailureCount`. Kumulativ seit Prozessstart, **nie zurückgesetzt** (kein
`Take…`-Muster — das wäre je Flush, hier zählt der Prozess), `Interlocked` wie die beiden Hot-Path-
Zähler, kein Lock. Kommentar sagt, warum sie nicht im `WorkerHealthSnapshot` stehen (7.4: eine
zweite Vertragsänderung, bewusst verweigert wie schon im #114-Plan) und wer sie liest (die
Abschlusszeile des Wiederaufbaus).

**Tests.** Anfangs 0; jede Aufzeichnung erhöht um eins und die Lesart bleibt nach dem Lesen gleich
(kein Reset); `RecordFlushSuccess` und `RecordFlushFailure` lassen beide Zähler unberührt.

**Fertig.** Tests grün; `WorkerHealthPublisher.cs` und `IWorkerHealthReader.cs` **unverändert**
(`git diff` leer). Commit `feat(worker): count Twitch rebuilds and failed attempts in WorkerStats`.

### Task 3 — `TwitchWatchdogPolicy`: der `clientSpent`-Zweig entfällt ersatzlos

**Zweck.** Der Spent-Zweig wird gegenstandslos (Konzept 3.1/3.7). Ein Ersatz-Eingang
`reconnectInFlight`, wie das Konzept ihn vorsieht, kommt **nicht** — er wäre an seiner einzigen
Aufrufstelle für immer `false` (1.3, Punkt 2; Abschnitt 7, Nebenbefund).

**Betroffene Dateien.** `src/EmotePurge.Worker/TwitchWatchdogPolicy.cs`,
`tests/EmotePurge.Worker.Tests/TwitchWatchdogPolicyTests.cs`, **und** die eine Aufrufstelle
`src/EmotePurge.Worker/TwitchConnectionWatchdog.cs:33-38` (positionales Argument, kompiliert sonst
nicht).

**Vertrag.** 2.5. Der Klassenkommentar sagt in zwei Sätzen, warum die Policy keinen
In-Flight-Eingang hat (sequentielle Schleife, Tick nur beim Warten) und dass er mit einem eigenen
Tick-Timer zurückkäme. Der Klassenkommentar verweist nicht mehr auf `ReconnectPolicy` (Vorgriff auf
Task 4, sonst `CS1574`).

**Übergangszustand — bewusst und benannt.** Bis Task 4 ruft der Watchdog `Decide` ohne den
Spent-Eingang und weiter `ForceReconnectAsync`. Damit fehlt zwischen Task 3 und Task 4 der Ersatz
eines verbrauchten Clients (#114). Die Solution baut, alle Tests sind grün, aber **dieser
Zwischenstand darf nicht live laufen und nicht allein gemergt werden** — Task 3 und 4 gehören in
denselben PR. Das ist die einzige Stelle im Plan, an der „baut und Tests grün" nicht „funktioniert"
heißt.

**Tests.** Die zehn bestehenden Fälle ohne das `clientSpent`-Argument; die beiden
`ClientSpent…`-Fälle entfallen. **Keine** neuen Fälle — es gibt keinen neuen Zweig.

**Fertig.** 10 Fälle grün; `ReconnectPolicy.cs` in diesem Task **nicht** angefasst. Commit
`refactor(worker): drop the spent-client branch from the watchdog policy`.

### Task 4 — Transport und Schleife (atomar): `TwitchChatManager`, `ITwitchChatManager`, `TwitchConnectionWatchdog`, `ReconnectPolicy` weg

**Zweck.** Das Modell aus Konzept 3 (E1–E5) im Code, in **einem** Schritt, weil der Vertragsbruch
(`IsClientSpent`, `ForceReconnectAsync`, `Decide`-Aufruf, `ReconnectPolicy`) keine baubare
Zwischenstufe zulässt, die nicht toten Code oder eine zweite Schleife bräuchte. Enthält die
Log-Ebenen aus 7.7 und 7.10 und den DECISIONS-Eintrag (Regel 3).

**Betroffene Dateien.** `src/EmotePurge.Worker/TwitchChatManager.cs`, `ITwitchChatManager.cs`,
`TwitchConnectionWatchdog.cs`, `Worker.cs` (Aufruf `ConnectAsync(stoppingToken)` und der Kommentar
`:23-25`), `appsettings.json`, `appsettings.Development.json`, `EmotePurge.Worker.csproj` (nur
der Kommentar `:11-12` zur `TwitchLib.Communication`-Referenz — er bleibt wahr, nennt aber jetzt
`NoReconnectionPolicy`), `docs/DECISIONS.md`. **Löschen:** `ReconnectPolicy.cs`,
`tests/EmotePurge.Worker.Tests/ReconnectPolicyTests.cs`. **Kommentare nachziehen** (1.2):
`BotChatterDetector.cs:7`, `SevenTv/SevenTvBackoffPolicy.cs:31` (dort auch inhaltlich: die
Abgrenzung „nicht `ReconnectPolicy`, weil …" wird zur Abgrenzung von `TwitchReconnectBackoffPolicy`
— gleiche Form, anderer Deckel und andere Reset-Regel, Konzept 9.3), `WorkerStats.cs:10`,
`TwitchWatchdogPolicyTests.cs:5`, `BotChatterDetectorTests.cs:6`. Vorher gelesen: Task 1–3, das
ganze Konzept, `SevenTvEventWorker.cs` (Schleifenmuster), `UsageFlushWorker.cs:30-39` (warum die
Stoppdauer zählt).

**Vertrag Manager.** 2.1–2.3, dazu:

- `CreateClient` baut mit `ClientOptions(new NoReconnectionPolicy())`. Der Kommentar dort erklärt,
  warum das nicht „kein Reconnect", sondern „genau ein Versuch je Objekt" heißt, warum
  `ReconnectAsync` an so einem Objekt beweisbar scheitert (3.1) und warum das die
  Zehn-Versuche-Falle vom 2026-07-26 strukturell ausschließt.
- `ReconnectOnceAsync`: Schritt 1 unwire + `_isConnected = false` + `MarkAllChannelsUnconfirmed`;
  Schritt 2 `DisconnectAsync` des alten Clients in einen beobachteten Hintergrund-Task, Exceptions
  gefangen und als Warning geloggt (E4, R5); Schritt 3 neuer Client, wiren, `_client` ersetzen;
  Schritt 4 `ConnectAsync()` mit `WaitAsync(ct)` — `false` oder Exception ist ein Fehlschlag, das
  Token verlässt den Aufruf, der halbfertige Client wird wie in Schritt 2 verworfen; Schritt 5
  `004` binnen 10 s über eine je Versuch neue `TaskCompletionSource`, die `OnConnected` erfüllt —
  sonst `HandshakeTimeout`. `ConnectAttemptedUtc` je Versuch stempeln (Health-Snapshot und
  Stale-Fallback lesen es weiter). Unter dem bestehenden `_reconnectLock`.
- **Generation und Slot:** `CreateClient` vergibt die nächste Generation (2.2); der Manager hält
  die Zuordnung Client → Generation (ein Feld für den aktuellen reicht, wenn die Handler `sender`
  gegen `_client` prüfen, bevor sie die Generation lesen). Der Slot ist die Klasse aus Task 1;
  `RequestReconnect` stempelt die Generation, loggt das Ergebnis des Anbietens (abgelegt /
  zusammengefasst / verworfen — drei Zeilen aus 2.7); `WaitForReconnectRequestAsync` delegiert an
  das Nehmen.
- `OnConnected`: `_isConnected = true`, `004`-Zeitstempel für die Sitzungsdauer, TCS erfüllen, Log.
  **Kein Rejoin** (E2); `_joinsIssuedForCurrentClient` entfällt.
- `OnDisconnected`: `_isConnected = false`, `MarkAllChannelsUnconfirmed`, Log, Signal nach 2.3.
- `OnConnectionError`: Information-Log, Signal nach 2.3 (koalesziert); kein Streak mehr. Der
  Kommentar sagt, dass die Zeile mit `NoReconnectionPolicy` **einmal je Verlust** kommt und keinen
  Fehler mehr bedeutet.
- `OnReconnected`: Stolperdraht — Error-Log, Signal `UnexpectedInPlaceReconnect` (Policy hebt auf
  10 s). Kommentar: warum das nicht mehr feuern kann und was es hieße, wenn doch.
- `OnFailureToReceiveJoinConfirmation`: Verhalten unverändert, Kommentar neu — nach dem Umbau
  bedeutet die Zeile wieder, was sie sagt (die Runde blockiert die Lese-Schleife nicht mehr, 3.5).
- `TryJoinAsync(channel, source, ct)`: `_joinGate.WaitAsync(ct)`, die 600-ms-Pause mit `ct`;
  **nach** dem Gate, unmittelbar vor dem Senden, erneut prüfen, ob der Kanal in `_desiredChannels`
  steht — sonst kein JOIN, Debug-Log (4.5, G5, R9); dann die **Ursprungszeile** mit Quelle und
  Generation (2.7), dann `_client.JoinChannelAsync`. Die „aufgeschoben"-Zeile auf Information
  (7.7). Aufrufer `JoinChannelAsync` (`Command`) / `EnsureJoinedAsync` (`ConvergenceNet`) reichen
  `CancellationToken.None` (Interface unverändert); nur die Runde (`Rejoin`) reicht das
  Stopping-Token. Am Binärstand belegt: TwitchLibs `JoinChannelAsync` dedupliziert nur gegen
  `JoinedChannels` (dort landet ein Kanal unmittelbar nach dem Senden, vor der Bestätigung) — ein
  zweiter Aufruf für einen gesendeten, unbestätigten Kanal sendet **nicht** erneut, stempelt aber
  unser `_lastJoinIssuedUtc` und dehnt das Raster. Deshalb ist die Ursprungszeile die einzige
  Stelle, an der sich ein JOIN des Konvergenznetzes von einem der Runde unterscheiden lässt
  (Abschnitt 7, PG3).
- `RejoinDesiredChannelsAsync(ct)`: Snapshot der Keys; je Kanal vorher `_isConnected` und `ct`
  prüfen, sonst Abbruch (keine N Aufschub-Zeilen); nach dem letzten Aufruf bis zu 5 s auf die
  Bestätigung aller Snapshot-Kanäle warten (Polling im 100–200-ms-Raster über `_desiredChannels`
  reicht; auch dieses Warten am Token und an `_isConnected`); Rückgabe 2.2. Die Runde loggt
  **nicht** selbst „abgeschlossen" — das tut die Schleife, die T kennt.
- `ConnectAsync(ct)`: Boot-Versuch auf dem initialen Client (Schritte 4–5 ohne Ersatz); bei
  Fehlschlag `RequestReconnect(InitialConnectFailed, …)` und Rückkehr. `Worker.ExecuteAsync`
  verhält sich nach außen wie heute (4.8).
- Entfällt ersatzlos: `OpenWaitTimeout`, `ObserveInBackground`, `LogOpenStillRunning`,
  `ReconnectClientAsync`, `RecreateClientAsync` (geht in `ReconnectOnceAsync` auf), `MarkOpenStarted`,
  das Feld `_reconnectPolicy`, `IsClientSpent`, `ForceReconnectAsync`.
- Klassenkommentar `:22-39` neu: Der zweite Absatz („This cannot cover TwitchLib's own rejoin …")
  ist nach dem Umbau falsch — es gibt keinen ungedrosselten Pfad mehr; die 600 ms bleiben (7.6),
  ihre Begründung wird die 20/10-s-Grenze allein.

**Vertrag Watchdog.** 2.6 und 2.7. Der Klassenkommentar (heute `:5-8`, deutsch) wird englisch neu
geschrieben: zwei Aufgaben (Netz gegen stille Verbindungen, Trägerin der Wiederaufbau-Schleife),
warum die Schleife hier liegt und nicht im Manager oder in einem dritten Dienst (Konzept 3.8,
verworfene Alternativen), warum sequentiell (1.3, Punkt 2), und die Shutdown-Zusage mit Verweis auf
#122.

**Grenzfälle, die der Implementer abdecken muss** (Konzept 4): Twitch über Stunden weg (4.1 —
≈ 80 Warnings/h, kein Sonderpfad); Wiederaufbau während eines laufenden Joins (4.4 — der JOIN geht
an den aktuellen `_client`, Fehler ist Warning, Kanal bleibt unbestätigt); Redis-`JOIN`/`LEAVE`
während eines Wiederaufbaus (4.5); zwei Signale in kurzer Folge (4.7); Shutdown in jedem Zustand
(4.6); Verlust während der Runde (Runde bricht ab, das Signal des neuen Clients liegt schon im
Slot, die Schleife findet es beim nächsten Warten); **Nachzügler des ersetzten Clients während
eines Bodens** (Signal genommen → 5 s Boden → `OnConnectionError` und zweites `OnDisconnected`
des alten, noch verdrahteten Clients → beide „verworfen" → nach dem Rejoin wartet die Schleife auf
einen leeren Slot, PG1).

**Konfiguration.** Beide `appsettings`-Dateien: `TwitchLib.Client.TwitchClient` auf `Information`
(7.10). Kommentar in `appsettings.json` ist nicht möglich (JSON) — die Begründung steht im
DECISIONS-Eintrag.

**Doku im selben Commit (Regel 3).** Neuer oberster Eintrag in `docs/DECISIONS.md`,
„2026-09-XX — Der Worker stellt seine Twitch-Verbindung selbst wieder her: `NoReconnectionPolicy`,
ereignisgetriebener Ersatz, gedrosselter Rejoin außerhalb der Lese-Schleife (#68, #114, #122)", mit
`**Betrifft:**`-Zeile über alle Dateien der Tasks 1–7. Inhalt, in dieser Reihenfolge: (1) der
Anlass in zwei Sätzen (Schwall und Spleiß sind dasselbe Ereignis, seit #114 zwei Lücken je
Ereignis); (2) das Modell E1–E5 mit dem Zustandsschema aus 2.6; (3) die Zahlen aus 2.4 mit ihrer
Begründung, ausdrücklich die G1-Korrektur; (4) **die Herkunft der drei `ReconnectPolicy`-Regeln
und warum jede im neuen Modell gegenstandslos ist** (Fehlerstreak ≥ 3 → 2026-07-26, jeder Versuch
ist ein Recreate; Open ≥ 10 min → ein Versuch ist auf ~25 s beschränkt; `_clientSpent` → #114,
`OnReconnected` kann nicht mehr feuern), damit der nächste Ausfall sie nicht neu erfindet; (5) warum
`ReconnectAsync` mit `NoReconnectionPolicy` strukturell scheitert (Anhang A, Punkt 2); (6) der
Rejoin aus der Lese-Schleife heraus und was die „nicht bestätigt"-Warnung vorher wirklich bedeutete;
(7) die Wunschzustandsprüfung nach dem Gate (G5); (8) die Shutdown-Zusage und warum #122 in
denselben Deploy gehört (G3); (9) die beiden SLOs als Abnahmekriterien, mit dem Satz, dass eine
Verfehlung ein Befund und kein Anlass zur Korrektur der Zahl ist (G2, R10); (10) die drei
Log-Entscheidungen (Fatal-Zeile Information, „aufgeschoben" Information, TwitchLib auf
Information — und warum das Volumen vernachlässigbar ist); (11) die gesetzten Entscheidungen 7.3,
7.4, 7.5 in je einem Satz; (12) die Merge- und Deploy-Sperre aus Abschnitt 0 und was der Prod-Beleg für Fall B
ist (6.5); (13) „Brücke, nicht Endstation" — Conduits in drei Sätzen mit Verweis auf Konzept 9, und
dass die Entscheidung transportfrei liegt (E5). Die Slot-Regel, die Sequentialität und die anderen
Punkte aus 1.3 stehen als Umsetzungsentscheidungen dabei — die Generationsregel des Slots, die
Ursprungszeile je JOIN und das gestrichene `reconnectInFlight` mit ihrer Herkunft aus der
Gegenrede zum Plan (Abschnitt 7).

**Der #114-Eintrag (`DECISIONS.md:132-225`) wird nicht gelöscht und nicht umgeschrieben.** Er bekommt
am Ende einen **Nachtrag** nach dem Muster von `DECISIONS.md:4771` („Nachtrag 2026-07-30 — die
Begründung … ist überholt"): welche seiner Aussagen der neue Eintrag ersetzt — E4 („Ersatz im nächsten
Watchdog-Tick, nicht sofort") und E5 („`Reconnect` heißt reconnect jetzt, recreate einen Tick
später", die Topologie-Aussage) samt der Spent-Marke — und welche bestehen bleiben: der Mechanismus
der Doppelschleife, „einer zur Zeit", „ersetzen statt reparieren", und der Sentinel
`IrcLineSpliceRule` als Negativkontrolle. Ein Verweis auf den neuen Eintrag, drei bis fünf Sätze,
nicht mehr.

**Fertig.** Solution baut ohne neue Warnungen (`--no-incremental`), `grep -rn "ReconnectPolicy\|IsClientSpent\|ForceReconnectAsync\|RecreateClientAsync\|ReconnectClientAsync" src tests --include=*.cs`
liefert nichts (der Name `TwitchReconnectBackoffPolicy` enthält den Suchbegriff nicht); Worker-Tests grün (13 Fälle weniger,
Task 1–3 dazu); `WorkerServiceRegistrationTests` unverändert grün (weiter neun Hosted Services);
`docker compose up -d --build worker` startet, loggt beim ersten Join „Joining channel: …" (die
Kontrollfrage aus 6.1 — sie belegt zugleich, dass 7.10 wirkt) und „TwitchClient verbunden"; ein
`docker compose stop worker` dauert **deutlich unter 10 s** (erste Probe der Zusage, S6 misst
genau). Commits: `feat(worker): let the worker drive its own Twitch reconnect` (Transport +
Watchdog + Interface + DECISIONS) und `chore(worker): log TwitchLib.Client at Information` (7.10) —
oder eines, wenn der Implementer die Trennung für künstlich hält; der DECISIONS-Eintrag liegt in
jedem Fall beim Vertragsbruch.

### Task 5 — `stop_grace_period` für den Worker (#122)

**Zweck.** Ohne Grace-Budget nützt die Shutdown-Zusage dem Abschluss-Flush nichts, ohne die Zusage
nützt das Budget nichts (Konzept 4.6, G3). Beides zusammen macht den Flush planbar — und beides
kommt in denselben Deploy (Abschnitt 0).

**Betroffene Dateien.** `docker-compose.yml:83-114` (`worker`), `docker-compose.prod.yml:85-…`
(`worker`), `docs/DECISIONS.md` (Absatz im Eintrag aus Task 4).

**Vertrag.** `stop_grace_period: 60s` am `worker` in beiden Dateien, mit einem Kommentar, der die
Rechnung nennt: Docker-Default 10 s, .NET-`ShutdownTimeout` 30 s, sieben Dienste stoppen vor dem
Flush, die 60 s decken den .NET-Wert mit Reserve; `ShutdownTimeout` bleibt bewusst Default (ein
zweites Budget im Code kauft nichts, solange Docker das größere hält). Nicht am `api`, nicht am
`harness` (der fängt SIGTERM selbst, `Program.cs:62-69`).

**Doku.** Ein Absatz im Eintrag aus Task 4: „#122 im selben Deploy" — warum keines der beiden ohne
das andere reicht, und dass die Einstellung taktneutral für #118 ist, aber ein Stack-Update.

**Fertig.** `docker compose config` beider Dateien zeigt den Wert am Worker; lokal nach
`docker compose up -d worker` (kein Rebuild nötig, aber der Container muss neu erstellt werden —
`up` tut das bei geänderter Konfiguration) meldet
`docker inspect emotepurge-dev-worker --format '{{.HostConfig.StopTimeout}}'` die 60; `null` hieße:
nicht übernommen. **Nicht** gegen Prod prüfen (Abschnitt 0). Commit
`chore(compose): give the worker a 60s stop grace period (#122)`.

### Task 6 — Debug-Auslöser für den `RECONNECT`-Pfad (Entscheidung 7.5)

**Zweck.** Fall B (Twitch sendet `RECONNECT`) ist lokal sonst nicht auslösbar. Der Haken macht die
**Policy-Reaktion** beweisbar (S2); den Thread-Kontext beweist er nicht (6.5) — das steht so im
Kommentar und im DECISIONS-Absatz.

**Betroffene Dateien.** `src/EmotePurge.Worker/Worker.cs` (Konstruktor bekommt `IConfiguration`,
Kommando-Dispatcher bekommt den dritten Zweig), `ITwitchChatManager.cs` und `TwitchChatManager.cs`
(`SimulateServerReconnectAsync`), `tests/EmotePurge.Worker.Tests/WorkerBootSequenceTests.cs`
(Konstruktoraufruf `:50-58`, eine leere `ConfigurationBuilder().Build()` reicht),
`docs/DECISIONS.md` (Absatz).

**Vertrag.** Kommando `DEBUG:TWITCH-RECONNECT` auf `channel:bot:commands`, Konstante **worker-lokal**
(die Api sendet es nie; `BotCommands` in Core bleibt der Api↔Worker-Vertrag). Freigabe nur über
`Worker:Debug:AllowTwitchReconnectTrigger` (2.8), gelesen im Konstruktor wie
`Twitch:LivePollIntervalSeconds` in `TwitchLivePollWorker.cs:26`. Nicht freigeschaltet →
Warning „Debug-Auslöser ignoriert …" und nichts weiter — die Zeile ist der **positive** Beleg, dass
das Gate hält. Freigeschaltet → Warning „… wird injiziert", dann `SimulateServerReconnectAsync`, das
`OnReadLineTestAsync(":tmi.twitch.tv RECONNECT")` am aktuellen Client aufruft. Der Aufruf dauert
≈ 2 s (ClosePrivate 0,4 s + 1,5 s Wartezeit in TwitchLib) und blockiert solange den
Kommando-Handler — für einen Debug-Pfad in Ordnung, im Kommentar benannt. Was danach passiert, ist
Fall B minus Thread-Kontext: `OnDisconnected` (Signal), später `OnConnectionError` am schon
unwireten alten Client (ins Leere), kein `OnReconnected`.

**Grenzfälle.** Kommando bei getrenntem Client → `OnReadLineTestAsync` läuft trotzdem; das Ergebnis
ist ein zweites Signal auf einen vollen Slot, ein verworfenes Signal eines bereits verurteilten
Clients (2.3) oder — ist der Client schon unwired — gar nichts; alles harmlos, das Sichtbare
geloggt. Kommando im Harness-Einstiegspunkt → unerreichbar, der Harness
abonniert keine Kommandos (`AddHarness` ohne Hosted Service).

**Tests.** `WorkerBootSequenceTests` weiter grün. Kein Test für den Auslöser selbst — er ist
Transport (Regel 11/16); S2 in Task 9 ist sein Nachweis. Ein Fall in `WorkerBootSequenceTests`,
dass ein `DEBUG:`-Kommando ohne Freigabe **nicht** `SimulateServerReconnectAsync` ruft, ist
erlaubt und billig (der Subscriber-Callback ist dort schon greifbar).

**Doku.** Absatz im Eintrag aus Task 4: Kommando, Gate, was er beweist und was nicht.

**Fertig.** Build, Worker-Tests grün; lokal ohne Env: `redis-cli PUBLISH channel:bot:commands DEBUG:TWITCH-RECONNECT`
→ nur die „ignoriert"-Zeile, Verbindung bleibt stehen. Commit
`feat(worker): env-gated debug trigger for the Twitch RECONNECT path`.

### Task 7 — Doku-Mitzieher außerhalb von DECISIONS

**Zweck.** Alles, was nach Task 4 falsch ist, ohne Vertrag zu sein (Anhang B plus die Funde aus
1.2). Nur Ist-Stand, keine Vorgeschichte (Konvention seit 2026-08-07); die Geschichte steht in
DECISIONS.

**Betroffene Dateien und was sich ändert.**

- `CLAUDE.md:141` (Architektur-Absatz Worker): der Satz ab „„Einen" heißt seit 2026-09-08 …" wird
  ersetzt — ein Client zur Zeit; TwitchLib reconnectet nicht mehr (`NoReconnectionPolicy`); jeder
  Verlust ist ein Signal, der Watchdog trägt die Wiederaufbau-Schleife (Ersatz des Objekts, `004`,
  gedrosselter Rejoin außerhalb der Lese-Schleife); Entscheidung in `TwitchReconnectBackoffPolicy`
  und `TwitchWatchdogPolicy`, beide pur und getestet; Verweis auf DECISIONS.
- `CLAUDE.md:85` (Testprojekt-Liste): `ReconnectPolicy` → `TwitchReconnectBackoffPolicy`,
  `TwitchReconnectSignalSlot`.
- `CLAUDE.md:208` (Bekannte offene Grenzen, JOIN-Limits): „TwitchLibs eigener Rejoin nach einem
  Reconnect nicht" entfällt; neu: alle JOIN-Pfade sind gedrosselt, die Rejoin-Dauer wächst linear
  (0,6 s × N), die Grenzen sind jetzt Twitchs 100 Chatrooms und die 7TV-EventAPI (Konzept 7.2). Die
  Messung vom 2026-07-30 bleibt erwähnt, aber als Messung eines Pfads, den es nicht mehr gibt.
- `docs/Architectur.md:82-86` (A.1): ein neuer Aufzählungspunkt „Verbindungsaufbau und
  Wiederaufbau" mit den drei Rollen aus Konzept 3.3 (Transport, Signalgeber, Schleife) und dem
  Watchdog-Netz (15 min ohne Frame) in vier Sätzen; kein Zahlenwerk (das steht in DECISIONS).
- `src/EmotePurge.Api/Health/WorkerCapacity.cs:18-26`: **die Zahl 20 bleibt**, die Begründung wird
  neu geschrieben (Konzept 7.2): nicht mehr TwitchLibs ungedrosselter Rejoin, sondern die lineare
  Rejoin-Dauer je Ereignis (bei 100 Kanälen 60 s Lücke für den letzten), Twitchs 100-Chatroom-Decke
  und die 7TV-Subscription-Grenze; und der Satz, dass eine Verschiebung eine eigene Messung braucht.
  **Punkt 2a des Befunds aus Task 0 fließt hier ein:** Hat die Sonde die Decke für `justinfan`
  bestätigt oder widerlegt, steht das mit Datum und Verweis auf den #68-Kommentar im Kommentar; hat
  sie eine Zahl geliefert, die die 20 ersetzen könnte, wird sie **genannt, nicht gesetzt** — die
  Verschiebung bleibt eine eigene Entscheidung (7.2). Kein Verhalten, nur Kommentar; das
  Api-Projekt bleibt sonst unberührt.
- `docs/Review-2026-07-29-Umsetzung.md:407-426` („Offenes Skalierungsthema") und
  `docs/Untersuchung-Twitch-EventSub-2026-08-01.md`: **unverändert** — historische Dokumente.
- `.github/dependabot.yml:48`: unverändert (kein Versionswechsel, der Ignore-Eintrag bleibt bis nach
  dem bindenden Lauf).

Danach ein Grep über `CLAUDE.md`, `docs/Architectur.md`, `web/`, `src/`, `tests/` nach
`IsClientSpent`, `ForceReconnectAsync`, `RecreateClientAsync`, `ReconnectPolicy` (ohne
`TwitchReconnectBackoffPolicy`), „verbraucht", „In-Place-Reconnect", „nächsten Watchdog-Tick" —
Treffer nur noch in `docs/DECISIONS.md`, `docs/Konzept-Worker-Reconnect-2026-09-08.md`, den
Review-/Untersuchungs-Dokumenten und `docs/superpowers/plans/`.

**Fertig.** Trefferliste wie beschrieben; `dotnet build` unverändert grün (der Kommentar in
`WorkerCapacity.cs` ist die einzige Codeberührung). Commit `docs: describe the self-driven Twitch reconnect`.

### Task 8 — Gates und Coverage

**Zweck.** „Fertig" im Sinne der Projekt-`CLAUDE.md` — vor der Live-Verifikation, damit Task 9 auf
einem Stand läuft, der die Gates schon hält.

1. `dotnet format EmotePurge.slnx --verify-no-changes`, `dotnet build EmotePurge.slnx --no-incremental`
   (keine neue Warnung — `--no-incremental` bleibt dafür sinnvoll, taugt aber **nicht** als Wächter
   gegen tote `cref`-Verweise auf die gelöschte `ReconnectPolicy`: `GenerateDocumentationFile` ist im
   Repo nirgends gesetzt, Roslyn validiert `cref`s deshalb nicht, `CS1574` kann nicht feuern — dafür
   ist der Kontroll-`grep` aus Task 7 der Wächter, s. P2), `dotnet test EmotePurge.slnx` (braucht
   Docker für die Infrastructure-Tests). Erwartung: Baseline 1.047 Fälle (Projektnotiz 2026-09-08) minus 13
   (`ReconnectPolicyTests`) minus 2 (`ClientSpent…`) plus 14 + 8 (Task 1: Policy und Slot) plus ≥ 3
   (Task 2) plus 0 (Task 3) plus ≤ 1 (Task 6).
2. Frontend unberührt: `git diff --stat origin/main -- web` ist leer; keine E2E nötig (keine
   UI-Änderung).
3. **Coverage — erst nach den Commits** (`scripts/coverage-local.mjs` misst nur Committetes):
   `node scripts/coverage-local.mjs --backend-only`. Erwartung: **niedrig**, weil `TwitchChatManager`
   und `TwitchConnectionWatchdog` nach Regel 11 keine Fake-Tests bekommen und mit vollem Gewicht im
   Nenner stehen (R8; PR #116 stand bei 48 % lokal, das Sonar-Gate war grün — Projektnotiz „Lokale
   Coverage ist pessimistischer als Sonar"). **Keine Alibi-Tests schreiben.** Der erlaubte Hebel ist,
   Entscheidungslogik aus dem Transport in die Policies zu ziehen — der ist mit Task 1–3 angewendet.
   Fällt Sonar am PR dennoch rot, ist die Antwort eine gezielte Ausnahme mit Begründung, kein Test
   gegen Fakes.
4. Handzählung für den Bericht: wie viele der neuen Zeilen in den beiden Transportklassen liegen und
   wie viele in den getesteten Policies — die Zahl, die Sonars `new_coverage` grob vorhersagt.

**Fertig.** Alle vier Punkte belegt, Zahlen im Bericht an die Hauptsession.

### Task 9 — Live-Verifikation (Regel 16) und Merge-Vorbereitung — **der Merge-Blocker**

**Zweck.** Konzept 6 in Task-Form. Jede Behauptung bekommt ihre Beobachtung, jede Beobachtung
ihren Widerleger (6.2); **der Beleg ist positiv** — „Zeile fehlt" beweist nichts (G4). **SLO-2 ist
das Abbruchkriterium: reißt es in einem von fünf S1-Läufen, oder endet ein Wiederaufbau mit K > 0,
wird nicht gemergt, bis die Ursache benannt ist** (R10). Eine Zielzahl ändert sich nur vor dem
nächsten Lauf, mit Begründung, nie danach.

**Rollen.** Der Subagent bereitet vor, beobachtet, wertet aus und schreibt den Bericht. Die
Host-Kommandos mit `sudo` (`nsenter`/`ss --kill`, `iptables`) führt der Nutzer aus — `sudo` verlangt
auf der Devbox ein Passwort (1.1). Der Subagent legt sie ihm fertig vor, mit Zeitstempel-Aufruf
davor.

**Aufbau (Konzept 6.1).**

- Aus dem **Haupt-Checkout**, nie aus einem Worktree (`.env` fehlt dort, Compose reißt den Stack ab).
  `docker compose up -d --build worker` (Regel 15).
- Log-Ebenen und der Debug-Schalter kommen aus einer Override-Datei **im Scratchpad**, angehängt mit
  `-f docker-compose.yml -f <scratchpad>/reconnect-verify.override.yml` (der `.env`-Pfad hängt an
  der ersten Datei, deshalb aus dem Haupt-Checkout starten). Sie setzt am `worker`:
  `Logging__LogLevel__TwitchLib.Client.TwitchClient=Information` (seit Task 4 ohnehin, hier als
  Gegenprobe), `Worker__Debug__AllowTwitchReconnectTrigger=true`, `SevenTv__ResyncIntervalSeconds=3600`
  (der Konvergenz-Tick darf in kein Messfenster fallen, PG3 — die 7TV-Reconciliation verliert
  dadurch für die Dauer der Läufe nichts, was die Zähler aus 6.4 berührt: der Emote-Cache startet
  warm aus Postgres und läuft live über die EventAPI), und **nur in Lauf A**
  `Logging__LogLevel__TwitchLib.Communication=Trace`. Nichts davon wird committet.
- **Kontrollfrage vor jedem Lauf:** erscheint beim ersten Join nach dem Containerstart
  „Joining channel: …"? Wenn nicht, ist die Ebene nicht wirksam und der Lauf zählt nicht.
- **Lauf A — Struktur:** Communication auf Trace, **nur stille Testkanäle** (≥ 20, per SQL in
  `Channels` mit `IsBotActive = true`, wie am 2026-07-30 — Rezept in
  `docs/Review-2026-07-29-Umsetzung.md:407-426`). Beweist: ein Versuch je Aufbau, eine Lese-Schleife
  je Client, Backoff-Kurve, Shutdown-Zusage, S7.
- **Lauf B — Zählung und Timing:** Communication auf Warning, dieselben Testkanäle plus mindestens
  ein lauter echter Kanal. Beweist: SLO-1, SLO-2, Bestätigungen, Sentinel, Flush.
- **t₀** ist der Host-Zeitstempel des Kill-Kommandos (`date +%T.%N` unmittelbar davor); Container
  und Host teilen die Uhr. Die Log-Zahl „T s seit Verlust" wird daneben notiert (1.3, Punkt 6).
- Vor jedem Szenario im Roster (Admin-Bereich) prüfen, dass alle Kanäle bestätigt sind — mit dem
  Intervall-Override gibt es keinen Minutentakt mehr, der das nachzöge; nach Task 4 stellt die
  Boot-Recovery beim Start und die Rejoin-Runde nach jedem Ereignis das selbst sicher, und ein
  unbestätigter Kanal vor t₀ ist bereits der Befund des vorherigen Laufs (K > 0), kein Startfehler.
  **N** ist die Zahl der bestätigten Kanäle zum Zeitpunkt t₀. SLO-2-Grenze = 6 s + 0,6 s × (N − 1),
  vor dem Lauf ausrechnen und hinschreiben.
- **Gültigkeitsregel für jeden SLO-Lauf (PG3):** Der Beleg für SLO-2 ist nur dann einer, wenn
  **jede** Bestätigung im Fenster [t₀, letzte Bestätigung] positiv der Rejoin-Runde zugeordnet ist:
  genau N Ursprungszeilen mit Quelle `Rejoin` und der Generation aus der „steht"-Zeile, **null**
  Ursprungszeilen mit Quelle `ConvergenceNet` oder `Command` im Fenster, und jede „Joining
  channel"-Zeile folgt unmittelbar auf eine Rejoin-Ursprungszeile. Steht eine fremde Ursprungszeile
  im Fenster, ist der Lauf **ungültig** — weder grün noch rot — und wird wiederholt; er zählt nicht
  zu den fünf. Ohne diese Regel hätte der Minutentakt (`EnsureJoinedAsync`, Konvergenznetz des
  Resync-Workers) ausgelassene Kanäle nachziehen und einen defekten Rejoin-Pfad mit K = 0 und N
  Zeilen beglaubigen können — dasselbe Muster wie beim Audit-Harness am 2026-09-07 („Selbstprüfung
  am falschen Ort"), und der Merge-Blocker wäre keiner gewesen.
- Läuft parallel eine Api auf `:5151`, ist das hier egal — E2E fährt dieser Task nicht.

**Szenarien (Konzept 6.3), jedes mit seinem Widerleger aus 6.2.**

- **S1 — Socket-Abbruch (Fall A), fünfmal, ≥ 2 min Abstand, Lauf B (einmal zusätzlich Lauf A für
  Schritt 7).** Kill über `nsenter … ss -K dport = :443` im Netz-Namespace des Containers (der Kernel
  kann es, 1.1). Reihenfolge der Belege: (1) „TwitchClient getrennt" ≤ 1 s nach t₀; (2)
  „Wiederaufbau #1 in 0 s, Client #g"; (3) **keine** „Fatal network error"-Zeile — der alte Client
  ist bei ≈ t₀ + 0,6 s ersetzt, sein `OnConnectionError` kommt erst bei ≈ t₀ + 2,1 s und fällt ins
  Leere (2.7; Abschnitt 7, W1 — die erste Fassung erwartete hier „genau einmal", und das hätte den
  Lauf grundlos rot gemacht); erscheint sie doch, folgt ihr eine „verworfen"-Zeile und **kein**
  zweiter Wiederaufbau; (4) „Twitch-Verbindung steht nach … s (Client #g+1)" — Δ zu t₀ ist SLO-1;
  (5) **genau N** Ursprungszeilen mit Quelle `Rejoin` und Client #g+1, **genau N** „Joining
  channel"-Zeilen (TwitchLib) und N „Channel … gejoint" (wir), Joining-Zeilen im 600-ms-Raster
  ± 100 ms, Bestätigungen je ~200 ms dahinter, keine „nicht bestätigt"-Zeile, **null**
  Ursprungszeilen anderer Quelle im Fenster (Gültigkeitsregel); (6) „Rejoin abgeschlossen (Client
  #g+1): N gewünscht, N bestätigt, 0 offen, T s" — Δ letzte Bestätigung zu t₀ ist SLO-2; (7) Lauf
  A: genau eine „ListenTaskActionAsync"-Trace-Zeile für den neuen Client, kein „TwitchClient
  reconnected".
  Widerlegt durch: mehr als N Joining-Zeilen, ein Abstand < 400 ms, N Zeilen binnen < 0,4 × N s,
  Median SLO-1 > 3 s oder p95 > 5 s über die fünf Läufe, **SLO-2 in einem Lauf gerissen oder K > 0**.
  Ungültig (wiederholen, nicht zählen) durch: eine fremde Ursprungszeile im Fenster.
  Ergebnis: eine Tabelle mit fünf gültigen Zeilen — t_Signal, t_004, t_letzte Bestätigung, K, Zahl
  der Joining-Zeilen, Ursprungszeilen je Quelle im Fenster, kleinster und größter Abstand, Log-T;
  ungültige Läufe stehen darunter mit dem Grund.
- **S2 — `RECONNECT` (Fall B) über den Debug-Auslöser (Task 6).** Ohne Freigabe: nur die
  „ignoriert"-Zeile, Verbindung steht. Mit Freigabe: „wird injiziert", dann `OnDisconnected`,
  **kein** „TwitchClient reconnected", danach S1-Schritte 2–6 — einschließlich Schritt 3 in seiner
  korrigierten Form: die „Fatal network error"-Zeile bleibt aus, weil der alte Client bei 0 s vor
  ihrem Eintreffen ersetzt ist (Task 6 sagt das selbst: „ins Leere"; die erste Fassung dieses
  Szenarios erwartete sie trotzdem „genau einmal", W1). Ehrlich
  festhalten: das beweist die Policy-Reaktion, **nicht** den Thread-Kontext — E1 („Handler tun in
  der sterbenden Schleife nichts") wird **per Code-Lesen** abgenommen, als eigener Punkt im Bericht
  mit Zeilenangabe der drei Handler.
- **S3 — Twitch über Minuten weg, zwei Varianten, je ≥ 3 min, Lauf A.** Erst Kill wie S1, **dann**
  neue Verbindungen blockieren (die erste Konzeptfassung droppte eine offene Verbindung und maß Fall
  C — G4). `iptables -I DOCKER-USER -s <container-ip> -p tcp --dport 443 -j REJECT --reject-with tcp-reset`
  (S3-REJECT: Kurve 0 / 2 / 4 / 8 / 16 / 30 / 30 s in ~1,5 min ablesbar, Abstände ± 20 %) bzw.
  `-j DROP` (S3-DROP: jeder Versuch läuft in TwitchLibs 15-s-Timeout, Fehlschlag-Warning ≤ 26 s nach
  „Wiederaufbau #n"). Belege: Warning je Versuch mit Streak-Nummer und Grund; kein Abstand > 30 s +
  Versuchsdauer; **kein** „Erzwinge Reconnect" aus dem Tick (Backstop, R1); Stale-Zweig feuert nicht;
  nach Entfernen der Regel „Twitch-Verbindung steht" ≤ 30 s + 25 s, dann S1-Schritte 5–6.
  Container-IP: `docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' emotepurge-dev-worker`.
- **S4 — Flapping.** S1 dreimal binnen 90 s: Verzögerungen **0 s, 0 s, 5 s**; dann 2 min Ruhe,
  vierter Kill → wieder 0 s. **Der PG1-Beleg liegt im dritten Ereignis:** während des 5-s-Bodens
  ist der alte Client noch verdrahtet, sein `OnConnectionError` (≈ 2 s) und sein zweites
  `OnDisconnected` (≈ 2,5 s) treffen ein — erwartet: genau **eine** „Fatal network error"-Zeile
  (Information) und **mindestens eine** „Verlust-Signal verworfen … Client #g ist bereits
  ersetzt"-Zeile, danach „Rejoin abgeschlossen" und dann **Stille**: kein „Twitch-Verbindung
  verloren" ohne vorangehenden Kill. Widerlegt durch: ein Wiederaufbau ohne Kill in den 2 min Ruhe
  (der Slot hat einen Nachzügler gespeichert), oder eine „Fatal"-Zeile ohne „verworfen"-Zeile beim
  dritten Ereignis (die Generationsregel greift nicht). Bei den Ereignissen mit 0 s gilt S1
  Schritt 3: keine „Fatal"-Zeile. Nebenbeobachtung für R3: schlägt ein Aufbau in dieser Serie
  fehl, ist das der einzige lokale Hinweis auf Twitch-seitiges Verhalten (von der Wohn-IP) — und
  nach Task 0, F3, auch nur ein Hinweis, keine Zuschreibung.
- **S5 — Redis-Kommandos.** (a) Während S3: `PUBLISH channel:bot:commands JOIN:<kanal-a>` und
  `LEAVE:<kanal-b>` — nach der Rückkehr ein JOIN für a, keiner für b; die Aufschub-Zeile für a ist
  Information (7.7). (b) **Während einer Rejoin-Runde** (R9): in S1 unmittelbar nach „Twitch-Verbindung
  steht" ein `LEAVE:` für den Kanal, der in der Snapshot-Reihenfolge eines früheren Laufs zuletzt
  kam — Beleg: keine „Joining channel"-Zeile für ihn nach dem `LEAVE`, er fehlt im Roster, und die
  Abschlusszeile zählt N − 1 gewünscht oder N gewünscht / N − 1 bestätigt / 0 offen — welche der
  beiden Formen, entscheidet der Implementer in Task 4 und schreibt es in den Kommentar; hier wird
  nur geprüft, dass sie stimmt.
- **S6 — Shutdown in drei Zuständen, `time docker compose stop worker`.** (a) im Backoff (während
  S3-REJECT); (b) im Connect (S3-DROP, Stop binnen 15 s nach einem „Wiederaufbau #n"); (c) im Rejoin
  (S1 mit N ≥ 20, Stop binnen 5 s nach „steht"). Belege je Zustand: Wanduhrzeit **deutlich unter 10 s**
  — mit Task 5 gilt der Grace von 60 s, der Widerleger ist deshalb die **Stoppdauer des Watchdogs
  allein**, ablesbar als Δ zwischen „Application is shutting down" und der nächsten Stopp-Zeile eines
  anderen Dienstes; nach „Application is shutting down" keine „Wiederaufbau"- und keine „try to
  connect"-Zeile; keine „Usage-Stat-Flush fehlgeschlagen"-Zeile; Exit-Code 0 (R12).
- **S7 — Stille Verbindung (Fall C), einmal, Lauf A.** `docker network disconnect emote-purge-dev_emotepurge-network emotepurge-dev-worker`,
  **> 15 min** halten, dann `connect`. Erwartung: TwitchLib meldet nichts, der Watchdog feuert bei
  ~15–16 min „Kein IRC-Frame seit …", danach S1-Schritte 2–6. Warum Lauf A: 1.3, Punkt 7.

**Zähler, die Lauf B begleiten (6.4).** `UsageStats`-Zeilen des lauten Kanals vor und nach jedem
Kill (Flush läuft weiter, kein Tag mit Null); Indeterminate- und Sentinel-Summe je Flush 0;
„Wiederaufbauten seit Prozessstart" am Ende gleich der Zahl der provozierten Ereignisse, nicht mehr.

**Beweistabelle im Bericht.** Die Tabelle aus Konzept 6.2, um eine Spalte „Ergebnis" ergänzt: je
Zeile grün / rot / nicht messbar, mit der Zahl, plus zwei Zeilen, die das Konzept nicht hat: „ein
Signal je Wiederaufbau auch über einen Boden" (PG1, S4) und „jede Bestätigung der Runde
zugeordnet" (PG3, Gültigkeitsregel, je S1-Lauf). Dazu die S1-Tabelle, die S3-Abstände, die S4-Folge,
die drei S6-Stoppdauern, die S7-Zeit. Was **lokal nicht beweisbar** ist, steht als eigene Liste
(6.5): Fall B in der echten Lese-Schleife, Twitchs Verhalten bei schnellen Wiederverbindungen,
SLO-1 unter Prod-Latenz, der Zähleffekt, die Ereignisrate — jeweils mit dem Prod-Beleg, wie er
aussähe.

**Messung in DECISIONS.** Die fünf S1-Zeilen, die S3-Kurve und die S6-Dauern kommen als Absatz
„Messung vom 2026-09-XX" an den Eintrag aus Task 4 — so, wie andere Einträge ihre Messwerte tragen.
Commit `docs: record the local verification of the Twitch reconnect`.

**Danach, vor dem PR.**

1. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — **immer mit `--scope`**
   (ohne reviewt Codex den Working Tree und entwarnt bei sauberem Tree falsch), aus der Session-CWD
   des Haupt-Checkouts (im Worktree diffte es `main` gegen `origin/main`). Fokus im Prompt: die
   Slot-Regel samt Generation (1.3, Punkt 1; 2.3 — Nehmen und Verurteilen unter einem Lock), die
   Token-Kette der Shutdown-Zusage, die Wunschzustandsprüfung nach dem Gate, das Verwerfen
   halbfertiger Clients, die Ursprungszeile an genau einer Stelle. Findings sind Input, kein Auftrag; widersprechen sich
   Opus-Review und Codex bei einem P1/P2, entscheidet Fable. „Reviewer failed to output a response"
   mit Exit 1 ist das Kontingent, kein Absturz.
2. PR-Text nennt: **Deploy-Fenster** (Deploy am 2026-09-09 nach 12:48 lokal, nach Auswertung der
   24-h-Nachkontrolle aus #117, gebündelt mit #122 und #129; Abschnitt 0); die SLO-Ergebnisse mit
   der Zahl der ungültigen Läufe; was lokal nicht beweisbar ist; die Coverage-Zahl aus Task 8 mit
   der R8-Einordnung; keine Migration, kein Frontend, kein neuer Fehlercode; die
   Umsetzungsentscheidungen aus 1.3 und Abschnitt 7.
3. **Übergabe an den Nutzer für die Zeit nach dem Deploy** (Konzept 6.6, kein Agent): vor dem Deploy
   die #117-Zahlen als Baseline sichern; 24 h ab Containerstart zählen: „Wiederaufbau #1"
   (Ereignisse), „Wiederaufbau #≥ 2" (Fehlversuche), p50/p95 von „Twitch-Verbindung steht nach"
   (SLO-1) und von „Rejoin abgeschlossen … T s" (SLO-2), Summe der „K offen", 0 × Stolperdraht, 0 ×
   Backstop, 0 × Sentinel; **Fall-B-Beleg:** TwitchLib „Reconnecting to Twitch" unmittelbar gefolgt
   von „TwitchClient getrennt", „Wiederaufbau #1", „steht", ohne „TwitchClient reconnected".
   Abbruchkriterium: SLO-2-p95 gerissen, ein Wiederaufbau mit K > 0, oder > ~2 Ereignisse/h über
   mehrere Stunden → Rollback bzw. Cooldown nachziehen. Danach der Harness-Vergleich Σ|Log − Live| /
   ΣLive vor/nach (R7).

**Fertig.** Alle sieben Szenarien mit positivem Beleg, SLO-2 in fünf **gültigen** Läufen gehalten
mit K = 0, Codex vorgelegt, PR offen. **Der Merge selbst bleibt beim Nutzer** (Abschnitt 0).

---

## 4. Reihenfolge und Abhängigkeiten

0 (Messung, zuerst — ihr Befund kann die Konstanten in Task 1 und die Begründung in Task 7 ändern)
→ 1 ∥ 2 ∥ 3 (unabhängig, parallel startbar) → 4 (braucht alle drei) → 5 ∥ 6 ∥ 7 (brauchen 4: Task 5
und 6 hängen ihren Absatz an den Eintrag aus 4, Task 7 beschreibt den Zustand nach 4 und übernimmt
die 7.2-Konsequenz aus Task 0) → 8 → 9. Task 2 und 3 hängen fachlich nicht an Task 0; wer die
Messung nicht abwarten will, darf sie parallel fahren — nur Task 1 wartet auf das **Go** aus Punkt
2b des Befunds (ein Stop erzeugt zuerst eine neue Planfassung, Task 0).

Die Hauptsession prüft nach jedem Task die Fertig-Bedingung, bevor der nächste startet. Task 3 und 4
sind ein Paar (Übergangszustand, s. Task 3); sie werden nie einzeln gemergt — und ohnehin wird erst
nach Task 9 gemergt (Abschnitt 0).

Commits (Conventional Commits, je Task): `feat(worker): add the pure Twitch reconnect backoff policy`
· `feat(worker): add the pure Twitch reconnect signal slot`
· `feat(worker): count Twitch rebuilds and failed attempts in WorkerStats` ·
`refactor(worker): drop the spent-client branch from the watchdog policy` ·
`feat(worker): let the worker drive its own Twitch reconnect` (+ ggf. `chore(worker): log TwitchLib.Client at Information`) ·
`chore(compose): give the worker a 60s stop grace period (#122)` ·
`feat(worker): env-gated debug trigger for the Twitch RECONNECT path` ·
`docs: describe the self-driven Twitch reconnect` ·
`docs: record the local verification of the Twitch reconnect`.

---

## 5. Risiken — und welcher Task sie trägt

Aus Konzept 8, ergänzt um die Risiken des Plans selbst.

| # | Risiko | Adressiert in | Bestätigt durch / ausgeräumt durch |
|---|---|---|---|
| R1 | TwitchLib liefert in einem Verlustmodus kein Signal; nur der 15-min-Watchdog fängt es | Task 4 (Backstop-Zeile bleibt als Fehlerindikator), Task 9 | „TwitchClient meldet sich als getrennt" aus dem Tick / S1, S3, S4 liefern ≤ 1 s ein Signal; Prod 24 h 0 × Backstop |
| R2 | Unser Recreate scheitert, wo TwitchLibs Schleife durchgekommen wäre | Task 9, S3 | nach Freigabe kein Erfolg in 30 + 25 s / S3 grün, fünfmal |
| R3 | Twitch wertet den 0-s-Wiederaufbau oder die S4-Serie als Missbrauch | Task 0, F3 (Go/No-Go, keine Zahl); Task 9, S4 (Hinweis, keine Zuschreibung); danach nur Prod | Nur eine Zuschreibung nach Task 0 (b) — Servermeldung oder reproduziertes serverseitiges Schließen mit Niedrigraten-Kontrolle — ist ein Stop; ein einzelner Fehlschlag ist inconclusive (PG4). Prod-24-h ohne Fehlschlag-Serie räumt es aus. Falls je bestätigt: erster Delay 1–2 s, Flap-Boden ab der zweiten Sitzung — beides Policy-Konstanten, aber **nur über eine neue Planfassung**, nie durch Task 1 allein (PG2) |
| R4 | Rejoin außerhalb der Lese-Schleife kippt TwitchLibs Queue-Zustand | Task 4 (E2), Task 9 S1 Schritt 5–6 | Raster ≠ 600 ms, „nicht bestätigt"-Zeilen, unbestätigte Kanäle > 60 s / S1 grün, Roster vollständig |
| R5 | Hintergrund-Aufräumen des alten Clients rennt gegen den neuen | Task 4 (Exceptions gefangen und geloggt), Task 9 | „Aufräumen … fehlgeschlagen" häufig, Prozessabsturz / Lauf ohne diese Zeilen |
| R6 | Log-Flut bei langem Ausfall (≈ 80 Warnings/h) | Task 9 S3 misst die Zeilenzahl | wenn nötig: ab Streak 5 nur jeder fünfte Versuch Warning — Policy-Entscheidung, nicht Teil dieses Plans |
| R7 | Die Einordnung in Konzept 5.4 ist falsch, der Umbau setzt die Uhr doch zurück | Nutzer, nach dem Deploy (Task 9, Übergabe) | Sprung von Σ\|Log − Live\| nach **oben** / nach unten oder keiner |
| R8 | Sonar-Gate rot, weil die neuen Zeilen im ungetesteten Transport liegen | Task 8 | `analyze` rot / Policies tragen die Logik; wie #116: PR-Befund abwarten, gezielte Ausnahme statt Alibi-Tests |
| R9 | `LEAVE` während der Runde belegt dauerhaft einen Chatroom-Platz | Task 4 (Prüfung nach dem Gate), Task 9 S5 (b) | JOIN nach dem `LEAVE` / S5 (b) grün, die Prüfung ist im Code sichtbar |
| R10 | Zeitleiste zu optimistisch (TLS, `004`-Latenz, Bestätigungslatenz) | Task 9 — **Abbruchkriterium, kein Lattenverschieben** | SLO-1 p95 > 5 s oder SLO-2 in einem von fünf Läufen gerissen → kein Merge, bis die Ursache benannt ist |
| R11 | Rejoin sendet, Twitch bestätigt nicht; das Log sagt trotzdem „abgeschlossen" | Task 4 (Abschlusszeile zählt Bestätigungen, K > 0 ist Warning), Task 9 | K > 0 oder M < N ohne Warning / S1 fünfmal K = 0 |
| R12 | Shutdown-Zusage hält nicht, der Abschluss-Flush kommt nicht dran | Task 4 (Token in jedem Warten), Task 5, Task 9 S6 | Stoppdauer ≥ 10 s oder Flush-Fehler beim Stop / S6 grün in allen drei Zuständen |
| P1 | Übergangszustand zwischen Task 3 und 4 ohne Spent-Ersatz | Task 3 benennt ihn; Task 4 folgt unmittelbar | nie einzeln gemergt, nie live gefahren |
| P2 | Tote Doku-Verweise (`<see cref>`, Prosa) auf die gelöschte Klasse `ReconnectPolicy` bleiben stehen — **nur per `grep` auffindbar, nicht über den Build**: `GenerateDocumentationFile` ist im Repo nirgends gesetzt (weder `Directory.Build.props` noch ein `.csproj`), Roslyn validiert `cref`s deshalb überhaupt nicht; ein absichtlich kaputter `cref` baute mit `--no-incremental` mit 0 Warnungen durch (positiv geprüft, 2026-09-08) | Task 4 zieht alle sieben Stellen; Task 7 kontrolliert per `grep` | ein Treffer des Kontroll-`grep` außerhalb der erlaubten Dateien |
| P3 | Ein Signal aus einem fehlgeschlagenen Versuch **oder ein Nachzügler des ersetzten Clients während eines Bodens** bleibt im Slot und löst einen zweiten Wiederaufbau aus | Task 1 (Slot mit Generationsregel, Test 5–8), Task 4 (Handshake-Regel), Task 9 S3 und S4 | „Twitch-Verbindung verloren" ohne vorangehenden Kill nach einem gelungenen Aufbau in S3 oder S4; „Fatal"-Zeile ohne „verworfen"-Zeile beim dritten S4-Ereignis (PG1) |
| P4 | `WorkerBootSequenceTests` bricht am `Worker`-Konstruktor | Task 6 | Compile-Fehler im Testprojekt |
| P5 | `appsettings.Development.json` überschreibt 7.10 bei lokalem `dotnet run` | Task 4 (beide Dateien) | Kontrollfrage aus 6.1 fällt bei `dotnet run` durch |
| P6 | Die Host-Kommandos in S1/S3 brauchen `sudo` mit Passwort | Task 9, Rollen | Sonde hängt; nach zwei Fehlschlägen abbrechen und den Nutzer fragen (Projektnotiz) |
| P7 | **Annahme:** Twitch drosselt oder sperrt IP-seitig — die JOIN-Limit-Sonde von der Wohn-IP träfe dann die laufende #73-Negativprobe im Dev-Worker mit | Task 0 (Stufen, Abbruchkriterium, Dev-Worker-Log läuft mit) | Dev-Worker loggt während der Sonde „Join … nicht bestätigt" oder „TwitchClient getrennt" → sofort stoppen, im Befund festhalten; bleibt er still, ist die Annahme für diesen Lauf nicht bestätigt — nicht widerlegt |
| P8 | Der Befund aus Task 0 verändert Zahlen, nachdem Task 1 schon gebaut ist — oder Task 1 baut andere Zahlen, als Task 9 abnimmt | Reihenfolge in Abschnitt 4; Task 0 Punkt 2b als Go/No-Go | Task 1 startet erst mit dem Go; ein Stop erzeugt eine neue Planfassung, in der 2.4, Task 1, Task 4-Doku, S1/S3/S4 und R3 **gemeinsam** geändert sind — nie eine Konstante allein (PG2) |
| P9 | Der Konvergenz-Tick (`EnsureJoinedAsync`, jede Minute) zieht während eines SLO-Laufs ausgelassene Kanäle nach; K = 0 und N Zeilen beglaubigen einen defekten Rejoin-Pfad | Task 4 (Ursprungszeile je JOIN mit Quelle und Generation), Task 9 (Intervall-Override 3600 s, Gültigkeitsregel) | eine Ursprungszeile mit Quelle ≠ `Rejoin` im Messfenster → Lauf ungültig, wiederholen; fünf gültige Läufe mit N Rejoin-Ursprungszeilen räumen es aus (PG3) |
| P10 | Ein Code-Merge veröffentlicht ein neues `:latest`-Worker-Image; ein Deploy setzt den #117-Anker zurück | Abschnitt 0 (Deploy am 2026-09-09 nach 12:48 lokal, nach Auswertung der 24-h-Nachkontrolle aus #117; gebündelt mit #122/#129) | Deploy-Zeitpunkt eingehalten, #117-Baseline vor dem Deploy gesichert (PG5, Nachtrag) |

---

## 6. Was dieser Plan bewusst nicht tut — und was er dem Nutzer vorlegt

- Er ändert die 600 ms nicht (7.6), die 15-min-Schwelle nicht (4.2), `TwitchJoinBudgetChannels`
  nicht (7.2 — nur die Begründung), `WorkerHealthSnapshot` nicht (7.4), den Klassennamen nicht (7.3).
- Er benennt den Watchdog nicht um, fügt keinen Hosted Service hinzu (neun bleiben neun) und schreibt
  keine Fake-Tests für `TwitchChatManager` oder `TwitchConnectionWatchdog` (Regel 11/16, R8).
- Er beantwortet die Messfrage aus #68 in Task 0 **für den anonymen Betrieb von einer Wohn-IP** —
  ein Datenpunkt, kein Gesetz. Für Prod (andere IP) und für einen benannten Bot-Account (#125)
  gilt der Befund nicht; die Sonde ändert Zahlen im Plan, nicht seine Form.
- Er bereitet den Transportwechsel auf Conduits (Konzept 9) nur insofern vor, als die Entscheidung
  transportfrei liegt (E5); er baut nichts davon.
- Er merged keinen Code selbst und verbindet sich zu keinem Zeitpunkt selbst zu `vps`/`nas`; Merge-
  und Deploy-Zeitpunkt (2026-09-09 nach 12:48 lokal) stehen in Abschnitt 0.

**Vorgelegt, weil der Plan hier über den Wortlaut des Konzepts hinaus entscheiden musste** (1.3):
die Slot-Regel (Handler signalisieren nur nach Handshake, Slot leert beim Konsumieren und verurteilt
die Generation), die sequentielle Schleife — und deshalb **kein** `reconnectInFlight`-Eingang
(gestrichen, Abschnitt 7) —, kein Trennen beim
Shutdown, der Flap-Boden als untere Schranke auch für Fehlversuche, 2 s nach einem gescheiterten
Boot-Connect, zwei Uhren für T, S7 in Lauf A, beide `appsettings`-Dateien. Kippt der Nutzer eine
davon, ändert sich der genannte Task, sonst nichts.

---

## 7. Gegenrede zum Plan und was daraus folgt (2026-09-08)

Die Fassung des Plans in `115fc2d`/`0268a34` wurde derselben adversarialen Gegenrede unterzogen wie
das Konzept (Codex Sol, `/codex:adversarial-review`): Verdikt „needs-attention", vier Befunde
`high`, einer `medium`. Wie in Konzept 10 steht hier je Befund, was angenommen und was
zurückgewiesen wurde, und was sich dadurch **im Plan** geändert hat — die Änderungen stehen an den
betroffenen Stellen, dieser Abschnitt ist die Begründung, nicht der Ort. Dazu ein Nebenbefund, den
die Gegenrede nicht gemeldet hat, und ein Widerspruch, der beim Beantworten auffiel. Findings sind
Input, kein Auftrag; jeder ist am Plantext, am Code auf `86eedcd` oder am Binärstand geprüft.

**PG1 [high] — Der beim Konsumieren geleerte Slot koalesziert nicht über den Backoff.**
*Angenommen.* Der Ablauf tritt am Plantext ein: 2.3 leerte den Slot beim Liefern, 2.6 legte den
Delay **vor** `ReconnectOnceAsync`, und erst dessen Schritt 1 unwired den alten Client. Fall A
liefert nach dem ersten `OnDisconnected` (≈ 0,6 s) noch ein `OnConnectionError` (≈ 2,1 s) und ein
zweites `OnDisconnected` (≈ 2,5 s) desselben Clients (Konzept 3.2, am Binärstand belegt). Bei 0 s
Verzögerung ist der Client vorher ersetzt und die Nachzügler fallen ins Leere — bei 5 s (Flap-Boden
ab der dritten kurzen Sitzung) oder 10 s (Stolperdraht) ist er noch verdrahtet, hatte den
Handshake, ist der aktuelle `_client`: die Handler-Regel aus 1.3, Punkt 1, ließ ihn durch, der
leere Slot nahm ihn an, und nach dem gelungenen Rejoin hätte die Schleife die frische Verbindung
abgerissen. Die Regel aus 1.3 war gegen Signale **fehlgeschlagener** Versuche gebaut und hat den
Fall des **ersetzten, aber noch verdrahteten** Clients nicht gesehen — der Plan hatte 1.3, Punkt 1,
als geschlossene Lücke markiert, sie war es nicht. Von den beiden Auswegen der Gegenrede ist der
zweite („alten Client vor dem Delay entwiren") nicht hinreichend: das Unwiren ist kein atomarer
Schnitt gegen einen Handler, der auf dem Lese-Thread gerade läuft, und es kostete die drei
Logzeilen aus Konzept 4.7, die die Beobachtung sind. Gewählt: die **Client-Generation** — jeder
Client trägt eine Nummer, jedes Signal die Nummer seines Clients, das Nehmen aus dem Slot verurteilt
die Nummer unter demselben Lock, und ein Signal einer verurteilten Nummer wird verworfen (2.3). Die
Handshake-Regel bleibt daneben nötig (ein Versuchs-Client ohne `004` hat eine höhere Nummer). Das
passt zu E1 (Handler signalisieren nur — sie signalisieren jetzt mit Absender) und E3/E4 (Ersatz
statt Reparatur, Aufräumen im Hintergrund — das Aufräumen darf jetzt beliebig lang dauern, ohne
dass der alte Client noch etwas bewirken kann). Die Nummer ist zugleich die Rebuild-ID, die PG3
braucht. Geändert: 1.3 Punkt 1 (Nachtrag), 2.1, 2.2 (Generation, `ClientGeneration` in Request und
Outcome), 2.3 (Slot als reine Klasse mit der Regel), 2.6, 2.7 (Generation in vier Zeilen, neue
„verworfen"-Zeile), Task 1 (Slot mit acht Verhaltensfällen — Fall 5 ist der Ablauf der Gegenrede,
deterministisch), Task 4 (Grenzfall), Task 9 S4 (der Live-Beleg: „Fatal"-Zeile plus
„verworfen"-Zeile beim dritten Ereignis, danach Stille), P3.

**PG2 [high] — Die von Task 0 erlaubte Backoff-Variante widerspricht den Gates in Task 9.**
*Angenommen.* Nachgeprüft: Task 0, Punkt 2b, und Task 1 („Voraussetzung") erlaubten bei einem
F3-Befund „1–2 s statt 0 s" und den Boden ab der zweiten Sitzung; 2.4, S1 („Wiederaufbau #1 in
0 s"), S3 (0/2/4/…), S4 (0/0/5), R3 und der DECISIONS-Auftrag in Task 4 blieben auf den
Konzeptzahlen. Eine vertragsgemäße Task-1-Implementierung wäre in Task 9 zwingend rot geworden —
oder hätte zum nachträglichen Verschieben der Abnahme verleitet, also genau zu dem, was das Konzept
bei R10 zurückgenommen hat. Von den beiden Auswegen gewählt: **F3 ist ein Go/No-Go, keine Zahl.**
Innerhalb dieses Plans ändert Task 0 keine Konstante; ein Stop-Befund erzeugt zuerst eine neue
Planfassung, in der 2.4, Task 1, Task 4-Doku, S1/S3/S4 und R3 gemeinsam auf je **einen** Wert je
Konstante gezogen sind, und das Konzept bekommt in 3.6 einen Nachtrag. Nicht gewählt: jetzt einen
konkreten Alternativwert festzulegen und beide Zweige durch den Plan zu ziehen — das hieße, zwei
Pläne zu pflegen für einen Befund, den PG4 ohnehin auf einen engen Fall eingrenzt, und eine
Zielzahl vor der ersten Messung zu ändern, ohne dass die Messung stattgefunden hat. Geändert: Task
0 (Punkt 2b und 3, „Fertig"), Task 1 („Voraussetzung"), Abschnitt 4, R3 und P8.

**PG3 [high] — SLO-2 kann durch das periodische Konvergenznetz falsch grün werden.** *Angenommen,
und der Befund ist der schwerste der fünf, weil er den Merge-Blocker selbst entwertet.* Am Code
geprüft: `SevenTvPeriodicResyncWorker` ruft jede Minute (`SevenTv:ResyncIntervalSeconds`, Default
60) für jeden aktiven Kanal `EnsureJoinedAsync`; das geht für jeden unbestätigten Kanal durch
`JoinChannelAsync` → `TryJoinAsync` → `_client.JoinChannelAsync` — derselbe Pfad, dieselbe
„Joining channel"-Zeile von TwitchLib, dieselbe Bestätigung in `_desiredChannels` und dieselbe
„Channel … gejoint"-Zeile wie die Rejoin-Runde; nach einem Wiederaufbau sind alle Kanäle
unbestätigt (`MarkAllChannelsUnconfirmed`), also zieht ein Tick im Fenster jeden Kanal nach, den
die Runde ausließ. Am Binärstand belegt: TwitchLibs `JoinChannelAsync` dedupliziert nur gegen
`JoinedChannels`, in das ein Kanal unmittelbar nach dem Senden fällt — ein Doppelaufruf sendet
also nicht doppelt, hinterlässt aber auch keine Spur. Das Fenster einer Runde (≈ 8–17 s) trifft bei
Minutentakt in rund jedem fünften Lauf auf einen Tick; über fünf Läufe ist das kein Randfall. Auch
der Redis-`RESYNC:`-Pfad ruft `EnsureJoinedAsync`. Dasselbe Muster hatte der Audit-Harness am
2026-09-07 (Projektnotiz „Selbstprüfung am falschen Ort"). Beide Vorschläge der Gegenrede sind
übernommen, weil keiner allein reicht: der Intervall-Override (3600 s) macht den Tick im Fenster
unwahrscheinlich, beweist aber nichts; die **Ursprungszeile** je JOIN mit Quelle und Generation
(2.7) macht die Herkunft jeder Bestätigung positiv belegbar — und daraus folgt die
**Gültigkeitsregel** in Task 9: ein Lauf mit einer fremden Ursprungszeile im Fenster ist ungültig
und wird wiederholt, er ist weder grün noch rot. Bewusst nicht gemacht: den Resync-Worker für die
Läufe abzuschalten oder das Konvergenznetz im Code zu unterbrechen — es ist auf Prod aktiv, und
die Messung soll das Prod-Verhalten zeigen, nur mit belegbarer Herkunft. Geändert: 2.1 (Quelle je
Pfad), 2.2 (`TwitchJoinSource`), 2.7 (Ursprungszeile), 2.8 (Override), Task 4 (`TryJoinAsync` mit
Quelle; Binärbefund zur Deduplizierung), Task 9 (Override, Roster-Prüfung statt Minutentakt,
Gültigkeitsregel, S1 Schritt 5, Tabelle, Beweistabelle, PR-Text), P9.

**PG4 [medium] — F3 wertet ein fehlendes `001` als Kausalbeleg.** *Angenommen.* Ein einzelner
Aufbau ohne `001`, gefolgt von Erfolg nach 60 s Ruhe, unterscheidet Twitch-seitige Drosselung
nicht von DNS, TLS oder einem Netzfehler — und genau dieser Befund durfte über Punkt 2b Konstanten
ändern. Das ist G4 auf Kausalitätsebene. Geändert: F3 hat drei Ergebnisklassen (kein Befund /
zugeschrieben / inconclusive); **zugeschrieben** verlangt eine Servermeldung im Rohlog oder ein
serverseitiges Schließen nach gelungenem Aufbau, reproduziert an derselben Seriengrenze in zwei
unabhängigen Durchgängen, plus eine zwischengeschaltete Niedrigraten-Kontrolle, die sofort gelingt;
die 60-s-Kontrolle bleibt als Erholungsbeleg, nicht als Ursache. Zusammen mit PG2 heißt das: nur
eine Zuschreibung ist ein Stop, und auch ein Stop ändert keine Zahl innerhalb dieses Plans. S4 in
Task 9 nennt einen Fehlschlag jetzt ausdrücklich Hinweis, nicht Zuschreibung. R3 nachgezogen.

**PG5 [high] — Die Deploy-Sperre schützt den automatisch veröffentlichten `latest`-Stand nicht.**
*Angenommen, mit einer Differenzierung, die der Gegenrede fehlte.* Der Mechanismus stimmt:
`publish.yml` läuft bei `push` auf `main` und pusht `:latest`, `docker-compose.prod.yml`
referenziert das Tag ohne `pull_policy` — ein Merge hätte den Umbau in jeden späteren
Portainer-Redeploy gelegt, und „mergen frei, deployen nicht" war damit eine Sperre aus Text ohne
Mechanik. **Aber** derselbe Workflow trägt `paths-ignore: ["**.md", "docs/**"]`: ein Merge, der nur
Dokumente berührt, veröffentlicht nichts. Das Risiko beginnt mit dem ersten gemergten Commit unter
`src/`, `tests/` oder in einer Compose-Datei — also ab **Task 1** (auch eine noch ungenutzte Policy
baut ein Image mit neuem Digest, und der Workflow-Kommentar nennt den blinden Re-Pull als Folge),
nicht erst ab dem Vertragsbruch in Task 4. Entscheidung des Nutzers (2026-09-08, gesetzt): der
Worker-Code wird **vor dem 2026-10-08 nicht gemergt**; der Branch bleibt offen und wird lokal
fertig verifiziert; die CI wird nicht umgebaut (kein SHA-only-Publish, keine Promotion-Stufe — die
Sperre ist die Reihenfolge). Der zweite Vorschlag der Gegenrede (technische Promotion-Sperre) ist
damit nicht abgelehnt, sondern unnötig geworden: für ein Fenster von vier Wochen ist ein nicht
gemergter Branch die billigere und prüfbarere Sperre als ein Workflow-Umbau, der danach wieder
zurückgebaut werden müsste. Geändert: Abschnitt 0 (ganz neu), Kopfzeile, Task 0 Punkt 3, Task 4
DECISIONS-Punkt 12, Task 9 (PR-Text, „Fertig"), Abschnitt 4, Abschnitt 6, P10.

**Nachtrag vom Abend desselben Tages:** Der Nutzer hat diese Sperre in Epic #118 („Nachtrag
2026-09-08 abends") wieder aufgehoben — Deploy am 2026-09-09 nach 12:48 lokal, nach Auswertung der
24-h-Nachkontrolle aus #117, gebündelt mit #122 und #129; Abschnitt 0 trägt den neuen Stand. Die
hier festgehaltene Mechanik (`publish.yml`, `paths-ignore`, das `:latest`-Tag ohne `pull_policy`)
bleibt als Begründung gültig, nur die Terminentscheidung ändert sich.

**Nebenbefund, nicht gemeldet — `reconnectInFlight`.** Die Gegenrede war gezielt danach gefragt
worden; 1.3, Punkt 2, hatte den Eingang selbst als „am Tick konstruktionsbedingt immer falsch"
markiert und ihn trotzdem behalten, weil das Konzept ihn nennt. Dass ein Review ihn nicht
beanstandet, macht ihn nicht lebendig. Entscheidung: **gestrichen.** Begründung, damit die Frage
nicht in jeder Folgesitzung neu aufgeht: die Schleife ist der einzige Aufrufer der Policy und
tickt nur, während sie wartet — der Wert ist an seiner einzigen Aufrufstelle für immer `false`;
die beiden Tests aus Task 3 hätten einen Zweig ohne Aufrufer geprüft, und das ist ein Alibi-Test
im Sinne von Regel 11/12, nicht Abdeckung; „falls der Tick je auf einen eigenen Timer wandert" ist
ein Umbau der Schleife, der den Eingang mitbringt, und keine Invariante, die man vorhält. Das
Konzept (3.7, 3.8) sieht den Eingang vor; es bekommt dazu einen Nachtrag am Ende von Abschnitt 10,
kein Umschreiben. Geändert: 1.3 Punkt 2, 2.5, Task 3 (jetzt „entfällt ersatzlos", 10 Fälle, kein
neuer), Task 8 (Zählung), Abschnitt 4 (Commit-Message), Abschnitt 6.

**W1 — ein Widerspruch, der beim Beantworten auffiel: „Fatal network error genau einmal".** 2.7,
S1 Schritt 3 und S2 erwarteten die `OnConnectionError`-Zeile genau einmal je Verlust (aus Konzept
3.7 und 6.3). Am Binärstand: die Zeile ist unser Handler, TwitchLib selbst loggt sie nur als
Trace-Methodenaufruf; sie kommt ≈ 2,1 s nach dem ersten `OnDisconnected`. Bei 0 s Verzögerung ist
der alte Client bei ≈ 0,6 s unwired — die Zeile **kann** in S1 und S2 regulär nicht erscheinen,
und ein korrekter Lauf wäre an diesem Schritt rot geworden; Task 6 sagte für S2 bereits „ins
Leere" und S2 erwartete sie trotzdem. Konzept 3.3 („kommt sie nach dem Ersatz, fällt sie ins
Leere") und Konzept 6.3/4.7 („genau einmal", „drei Logzeilen") standen von Anfang an
gegeneinander; der Plan hat es übernommen. Korrigiert: 2.7 („höchstens einmal", mit Bedingung), S1
Schritt 3 (keine Zeile; erscheint sie, folgt „verworfen" und kein zweiter Wiederaufbau), S2, und
die Beobachtung wandert nach S4, wo der 5-s-Boden sie erzwingt — dort ist sie zugleich der
PG1-Beleg. Das Konzept bekommt denselben Nachtrag wie zum Nebenbefund.

**Was unverändert blieb, und warum.** Das Modell (E1–E5), alle Zahlen aus 2.4, die SLOs, die
Trägerin der Schleife, die Reihenfolge der Tasks, die Sonde in Task 0 bis auf F3, die Regel „keine
Fake-Tests für den Transport" — der Slot ist deshalb eine eigene reine Klasse geworden, nicht ein
Test gegen `TwitchChatManager`. Kein Befund der Gegenrede berührte sie.
