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
(Abschnitt 1.3).

Reihenfolge der Arbeit in jedem Task: **Tests zuerst** (rot), dann Umsetzung (grün), dann die im
Task genannte Doku, dann die Gates aus Task 8. Commit und Push je Task ohne Rückfrage, Conventional
Commits, mehrere logische Commits (Regel 1 in der Fassung vom 2026-09-08, Regel 2); **der Merge
gehört dem Nutzer** (Regel 1, Regel 22).

---

## 0. Deploy-Sperre — zuerst lesen

**Bauen, lokal verifizieren und mergen ist im Messfenster frei. Der Deploy wartet bis nach dem
2026-10-08.** Er ist der erste Stack-Update nach dem bindenden Harness-Lauf und wird mit #122
(`stop_grace_period`, Task 5) und allem gebündelt, was bis dahin sonst auf dem Stapel liegt — jeder
Deploy kostet einen Worker-Neustart und damit den Zeitanker aus #117 (Epic #118, Abschnitt 4;
Konzept 5.4).

Was das für jeden Task dieses Plans heißt:

- **Kein `docker compose` gegen `docker-compose.prod.yml`, kein Portainer, kein `ssh vps`** — aus
  keinem Task, auch nicht „nur zum Nachsehen" (globale SSH-Regel). Die Compose-Änderung aus Task 5
  wird committet und **nicht** ausgerollt.
- Lokal (Compose-Projekt `emote-purge-dev`, Container `emotepurge-dev-worker`) ist alles erlaubt,
  auch `up -d --build worker` — das ist die Verifikation aus Task 9, kein Deploy.
- Der PR-Text (Task 9) nennt die Sperre ausdrücklich: „mergen ja, deployen erst nach dem
  2026-10-08, zusammen mit #122". Der Merge selbst setzt die Uhr aus #118 **nicht** zurück
  (kein `AlgorithmVersion`-Bump, keine Migration, keine Zählregel — Konzept 5.4); erst der
  Stack-Update zählt.
- Vor dem späteren Deploy sichert der Nutzer die #117-Zahlen als Baseline (Konzept 6.6); das
  steht als Übergabe am Ende von Task 9, nicht als Task.

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
   Unwiren neutralisiert, nicht durch den Slot.
2. **Die Schleife ist sequentiell, nicht nebenläufig.** „Wartet auf Signal oder Tick" (3.3) ist als
   *ein* Warten mit Zeitschranke umgesetzt: kommt binnen 60 s kein Signal, ist das der Tick. Während
   eines Wiederaufbaus wird nicht gewartet, also fallen Ticks aus („werden übersprungen", 3.3).
   Folge: Der Eingang `reconnectInFlight` der `TwitchWatchdogPolicy` (3.7, 3.8) ist am Tick
   **konstruktionsbedingt immer falsch**. Der Plan behält ihn samt Tests trotzdem, weil das Konzept
   ihn vorsieht und er die Invariante trägt, falls der Tick je auf einen eigenen Timer wandert —
   das ist eine Zeile und ein Kommentar, kein Mechanismus. Wer ihn für tot hält, streicht ihn in
   Task 3 und 4; das Konzept müsste dann in 3.7/3.8 nachgezogen werden.
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
  (Slot-Regel s. 2.3). Vom Tick und vom Debug-Auslöser genutzt; die TwitchLib-Handler rufen intern
  dasselbe.
- `Task<TwitchReconnectRequest> WaitForReconnectRequestAsync(CancellationToken ct)` — wartet auf ein
  Signal und **konsumiert** es (Slot danach leer). Ein Signal, das vor dem Warten abgelegt wurde,
  wird sofort geliefert (der Boot-Fehlschlag kommt, bevor der Watchdog wartet).
- `Task<TwitchConnectOutcome> ReconnectOnceAsync(CancellationToken ct)` — Schritte 1–5 der
  Recreate-Sequenz (Konzept 3.4): alten Client abkoppeln und im Hintergrund aufräumen, neuen mit
  `NoReconnectionPolicy` bauen, öffnen, `004` abwarten (10 s). Rückgabe sagt Handshake ja/nein,
  bei nein den Grund (`ConnectFailed` / `ConnectFaulted` / `HandshakeTimeout`) und die Dauer.
- `Task<TwitchRejoinOutcome> RejoinDesiredChannelsAsync(CancellationToken ct)` — Schritt 6:
  gedrosselte Runde über einen Snapshot von `_desiredChannels`, dann bis zu 5 s Warten auf die
  Bestätigungen; Rückgabe N gewünscht / M bestätigt / K offen / abgebrochen (Verbindung weg oder
  Token).
- `Task SimulateServerReconnectAsync()` — der Debug-Auslöser aus Entscheidung 7.5 (Task 6): reicht
  `:tmi.twitch.tv RECONNECT` über `OnReadLineTestAsync` in den aktuellen Client. Der Manager prüft
  keine Freigabe; die liegt allein am Kommando-Dispatcher in `Worker`.

Unverändert: `Initialize`, `JoinChannelAsync`, `EnsureJoinedAsync`, `LeaveChannelAsync`,
`IsConnected`, `LastMessageReceivedUtc`, `LastFrameReceivedUtc`, `ConnectAttemptedUtc`,
`GetRoster`.

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
- `TwitchReconnectRequest(TwitchSessionEndReason Reason, string Detail, TimeSpan? SessionDuration, DateTime RequestedUtc)`
  — was der Slot hält. Der Manager füllt `SessionDuration` aus seinem `004`-Zeitstempel.
- `TwitchConnectOutcome(bool HandshakeCompleted, TwitchSessionEndReason? FailureReason, TimeSpan Elapsed)`.
- `TwitchRejoinOutcome(int Desired, int Confirmed, int Open, bool Aborted)`.

Wo sie liegen: die beiden Policy-Typen (`TwitchSessionEndReason`, `TwitchSessionResult`) in der Datei
der Policy (Muster `SevenTvBackoffPolicy.cs`), die drei Manager-Typen neben `TwitchRosterEntry` in
`ITwitchChatManager.cs`.

### 2.3 Der Signal-Slot (im Manager)

Kapazität 1, latchend, nicht flankengetriggert. `RequestReconnect` auf leeren Slot: ablegen, Warter
wecken, Information-Log. Auf vollen Slot: **nur den Grund und das Detail ersetzen** (Konzept 4.7),
Zeitstempel und Sitzungsdauer bleiben vom ersten Signal; Information-Log „… zusammengefasst".
`WaitForReconnectRequestAsync` leert den Slot beim Liefern. Die TwitchLib-Handler
(`OnDisconnected`, `OnConnectionError`, `OnReconnected`) rufen `RequestReconnect` nur, wenn
`sender` der aktuelle `_client` ist **und** dieser Client den Handshake hatte (1.3, Punkt 1);
sonst loggen sie und tun nichts. Kein Handler baut auf, wartet oder rejoint (E1).

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

`Decide(bool reconnectInFlight, bool isConnected, TimeSpan? sinceOpenAttempt, TimeSpan? sinceLastFrame, TimeSpan? sinceLastForcedReconnect)`.
Erster Zweig: `reconnectInFlight` → nichts. Danach unverändert: Disconnected-Zweig mit 1-min-Cooldown
(jetzt der Backstop, Grund-Text unverändert „TwitchClient meldet sich als getrennt."), Stale-Zweig
15 min mit 15-min-Cooldown. Der `clientSpent`-Zweig samt Parameter entfällt.

### 2.6 Die Schleife in `TwitchConnectionWatchdog`

Trägerin bleibt der Watchdog, Name bleibt (Entscheidung 7.3). Ein sequentielles `ExecuteAsync`,
fehlerisoliert je Durchlauf (Muster `SevenTvEventWorker`), nie den Host mitreißend:

```
warten(Signal, ≤ 60 s)
  ├─ Zeitschranke → Tick: Decide(...) → ggf. LogLiveContext + RequestReconnect(FrameStale | DisconnectedBackstop)
  └─ Signal → Wiederaufbau: delay = NextDelay(Signal) → [Delay(ct) → ReconnectOnceAsync(ct) → bei Fehlschlag delay = NextDelay(Fehlversuch), wiederholen] → RejoinDesiredChannelsAsync(ct) → zurück zu warten
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
| `TwitchClient meldet Verbindungsfehler` … `Fatal network error` | **Information** | `OnConnectionError` — erwartete Folgezeile eines Verlusts, genau einmal je Verlust (3.7) |
| `Twitch-Verbindung verloren` … `Wiederaufbau #1 in {Delay}s` | Warning | Signal konsumiert; Grund und Sitzungsdauer in der Zeile |
| `Wiederaufbau #{n} fehlgeschlagen` … `nächster Versuch in {Delay}s` | Warning | je Fehlversuch mit Grund (`ConnectAsync` false / Exception / `Handshake nicht abgeschlossen`) |
| `TwitchClient verbunden` | Information | `OnConnected` (`004`), wie heute |
| `Twitch-Verbindung steht nach {Seconds}s (Versuch #{n})` | Information | nach dem Handshake; **SLO-1-Zeile** (6.6) |
| `Rejoine {Count} gewünschte(n) Channel(s)` | Information | Start der Runde, wie heute |
| `Channel {Channel} gejoint` | Information | wie heute |
| `Rejoin abgeschlossen: {N} gewünscht, {M} bestätigt, {K} offen, {T}s seit Verlust` + `seit Prozessstart: {Rebuilds} Wiederaufbauten, {Failures} Fehlversuche` | Information; **Warning bei K > 0 oder Abbruch** | Ende der Runde; **SLO-2-Zeile** |
| `Join für {Channel} aufgeschoben` | **Information** (war Warning; Entscheidung 7.7) | `TryJoinAsync` bei `!_isConnected` |
| `Weiteres Verlust-Signal` … `zusammengefasst` | Information | `RequestReconnect` auf vollen Slot |
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
  `justinfan` nicht.
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
daneben mitgeschrieben, sind aber nicht der Primärbeleg). Zwei Kadenzen müssen wählbar sein:
**Queue-Kadenz** (TwitchLibs `JoinChannelAsync`, sendet den nächsten JOIN auf die Bestätigung des
vorherigen, ≈ 200 ms — der Pfad, den Prod bis heute fährt) und **gedrosselt** (600 ms, unser
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
| 5 | 120 | gedrosselt 600 ms | F2 über der Decke |

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
  10-s-Fenster (die Drosselung isoliert die Frage).
- F2, Decke **gilt:** JOIN #101 (und folgende) ohne `366` **und** die Kontrolle: nach `PART` von
  fünf bestätigten Kanälen werden fünf neue JOINs bestätigt (es war der Bestand, nicht die Rate) —
  oder eine `NOTICE`, wörtlich.
- F3: Serie von fünf Verbindungsaufbauten binnen 60 s (Connect → `001`/`004` → ein JOIN → `366` →
  trennen), dann zehn binnen 60 s, dann zwanzig; Abbruch bei der ersten Serie, in der ein Aufbau
  kein `001` liefert oder Twitch die Verbindung schließt — mit der Kontrolle, dass nach 60 s Ruhe
  ein Aufbau wieder gelingt. Positiver Beleg je Serie: n × `001` mit Zeitstempeln. Zur Einordnung
  im Befund: die Schleife aus Task 4 erzeugt im schlimmsten Fall (Dauer-Flapping, 5-s-Boden) zwölf
  Aufbauten pro Minute — die zweite Serie liegt darüber, die dritte deutlich.

**Ergebnisverwertung — der Task endet mit einem Befund, nicht mit einer Logdatei.**

1. Der Befund geht als Kommentar an **#68** (per `gh issue comment`; `gh issue view/edit` stolpern
   über Projects-Classic, das Kommentieren nicht): je Frage die Antwort mit dem positiven Beleg, die
   Tabellen der gefahrenen Stufen, das größte 10-s-Fenster je Stufe, welche Stufen ungemessen blieben
   und warum, das Rohlog als Anhang oder Pfad. Dazu die Einordnung: **ein Datenpunkt von einer
   Wohn-IP, gültig nur für den anonymen Betrieb** — bei einem Wechsel auf einen benannten Bot-Account
   (#125) ist die Messung **neu zu erheben**, weil Twitch die Limits als Account-Limits dokumentiert.
2. Der Befund sagt **ausdrücklich**, ob und wie er verändert: (a) **Konzept 7.2 / `WorkerCapacity.cs:26`**
   — bleibt die 20 mit der neuen Begründung aus Task 7 (lineare Rejoin-Dauer, Decke, 7TV-Grenze),
   oder gibt es eine gemessene Zahl, die die Begründung ersetzt; (b) **R3 in Abschnitt 5** — ist der
   0-s-Erstversuch durch F3 gedeckt oder muss Task 1 den ersten Delay auf 1–2 s und den Flap-Boden
   auf die zweite Sitzung setzen (das Konzept nennt genau diese beiden Stellschrauben, R3); (c) die
   **Dauerauflage aus Epic #118** („keine Ausweitung über ~20 gejointe Kanäle") — trägt ihre
   Begründung (der ungedrosselte Rejoin sprengt das Fenster) angesichts von F1 noch, und was gilt
   nach dem Deploy dieses Plans, wenn es den ungedrosselten Pfad nicht mehr gibt. Die Entscheidung
   über die Auflage trifft der Nutzer in #118; der Befund liefert die Zahl.
3. Was der Befund **nicht** ändert, egal wie er ausfällt: das Modell (E1–E5), die Drosselung auf
   600 ms (7.6), die Deploy-Sperre (Abschnitt 0). Die Sonde misst Twitch, nicht unser Design — sie
   entscheidet Zahlen, keine Form.

**Fertig.** Kommentar an #68 steht; Task 1 startet erst, wenn die Konsequenz für R3 (Punkt 2b) im
Befund steht; Task 7 übernimmt die Konsequenz für 7.2 (Punkt 2a) in den Kommentar an
`WorkerCapacity.cs`. Kein Commit (nichts im Repo berührt).

### Task 1 — `TwitchReconnectBackoffPolicy` und die Sitzungs-Typen (rein, getestet)

**Zweck.** Die Backoff-Kurve aus Konzept 3.6 samt G1-Korrektur als reine Klasse nach dem Muster
`SevenTvBackoffPolicy`, bevor irgendein Transportcode sie braucht.

**Betroffene Dateien.** Neu: `src/EmotePurge.Worker/TwitchReconnectBackoffPolicy.cs` (Policy plus
`TwitchSessionEndReason` und `TwitchSessionResult`, 2.2), `tests/EmotePurge.Worker.Tests/TwitchReconnectBackoffPolicyTests.cs`.
Lesen: `SevenTv/SevenTvBackoffPolicy.cs`, `tests/…/SevenTvBackoffPolicyTests.cs`.

**Voraussetzung.** Punkt 2b des Befunds aus Task 0 liegt vor. Sagt er, dass Twitch schnelle
anonyme Wiederverbindungen ablehnt (F3), gelten statt der Konzeptzahlen die beiden Stellschrauben
aus R3: erster Versuch nach 1–2 s statt 0 s, Flap-Boden ab der **zweiten** kurzen Sitzung — zwei
Konstanten, zwei Testerwartungen (Fälle 1 und 8), sonst nichts. Sagt er nichts dergleichen, bleiben
die Konzeptzahlen.

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

**Fertig.** Tests grün, Datei ohne `using TwitchLib.*`, Build ohne neue Warnung. Commit
`feat(worker): add the pure Twitch reconnect backoff policy`.

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

### Task 3 — `TwitchWatchdogPolicy`: `clientSpent` → `reconnectInFlight`

**Zweck.** Der Spent-Zweig wird gegenstandslos (Konzept 3.1/3.7); an seine Stelle tritt der
In-Flight-Eingang.

**Betroffene Dateien.** `src/EmotePurge.Worker/TwitchWatchdogPolicy.cs`,
`tests/EmotePurge.Worker.Tests/TwitchWatchdogPolicyTests.cs`, **und** die eine Aufrufstelle
`src/EmotePurge.Worker/TwitchConnectionWatchdog.cs:33-38` (positionales Argument, kompiliert sonst
nicht).

**Vertrag.** 2.5. Die XML-Doku des neuen Parameters sagt: während die Wiederaufbau-Schleife läuft,
entscheidet der Tick nichts; und dass die Schleife nach Task 4 sequentiell ist, der Wert am Tick
also konstruktionsbedingt falsch ist (1.3, Punkt 2). Der Klassenkommentar verweist nicht mehr auf
`ReconnectPolicy` (Vorgriff auf Task 4, sonst `CS1574`).

**Übergangszustand — bewusst und benannt.** Bis Task 4 übergibt der Watchdog `false` (er ist
synchron, nichts kann in flight sein) und ruft weiter `ForceReconnectAsync`. Damit fehlt zwischen
Task 3 und Task 4 der Ersatz eines verbrauchten Clients (#114). Die Solution baut, alle Tests sind
grün, aber **dieser Zwischenstand darf nicht live laufen und nicht allein gemergt werden** — Task 3
und 4 gehören in denselben PR. Das ist die einzige Stelle im Plan, an der „baut und Tests grün"
nicht „funktioniert" heißt.

**Tests.** Die zehn bestehenden Fälle mit `reconnectInFlight: false` statt `clientSpent: false`;
die beiden `ClientSpent…`-Fälle entfallen. Neu: (a) getrennt, außerhalb des Cooldowns, in flight →
nichts; (b) verbunden, Frames 16 min alt, außerhalb des Cooldowns, in flight → nichts.

**Fertig.** 12 Fälle grün; `ReconnectPolicy.cs` in diesem Task **nicht** angefasst. Commit
`refactor(worker): replace the spent-client branch of the watchdog policy with an in-flight input`.

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
- `TryJoinAsync(channel, ct)`: `_joinGate.WaitAsync(ct)`, die 600-ms-Pause mit `ct`; **nach** dem
  Gate, unmittelbar vor dem Senden, erneut prüfen, ob der Kanal in `_desiredChannels` steht — sonst
  kein JOIN, Debug-Log (4.5, G5, R9). Die „aufgeschoben"-Zeile auf Information (7.7). Aufrufer
  `JoinChannelAsync`/`EnsureJoinedAsync` reichen `CancellationToken.None` (Interface unverändert);
  nur die Runde reicht das Stopping-Token.
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
Slot, die Schleife findet es beim nächsten Warten).

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
7.4, 7.5 in je einem Satz; (12) die Deploy-Sperre aus Abschnitt 0 und was der Prod-Beleg für Fall B
ist (6.5); (13) „Brücke, nicht Endstation" — Conduits in drei Sätzen mit Verweis auf Konzept 9, und
dass die Entscheidung transportfrei liegt (E5). Die Slot-Regel, die Sequentialität und die anderen
Punkte aus 1.3 stehen als Umsetzungsentscheidungen dabei.

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
ist ein zweites Signal auf einen vollen Slot oder ein Signal während eines laufenden Wiederaufbaus
— beides harmlos, beides geloggt. Kommando im Harness-Einstiegspunkt → unerreichbar, der Harness
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
- `CLAUDE.md:85` (Testprojekt-Liste): `ReconnectPolicy` → `TwitchReconnectBackoffPolicy`.
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
   (keine neue Warnung, insbesondere kein `CS1574`), `dotnet test EmotePurge.slnx` (braucht Docker
   für die Infrastructure-Tests). Erwartung: Baseline 1.047 Fälle (Projektnotiz 2026-09-08) minus 13
   (`ReconnectPolicyTests`) minus 2 (`ClientSpent…`) plus 14 (Task 1) plus ≥ 3 (Task 2) plus 2
   (Task 3) plus ≤ 1 (Task 6).
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
  Gegenprobe), `Worker__Debug__AllowTwitchReconnectTrigger=true`, und **nur in Lauf A**
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
- Vor jedem Szenario den `EnsureJoinedAsync`-Minutentakt abwarten, bis alle Kanäle bestätigt sind
  (Roster im Admin-Bereich); **N** ist die Zahl der bestätigten Kanäle zum Zeitpunkt t₀. SLO-2-Grenze
  = 6 s + 0,6 s × (N − 1), vor dem Lauf ausrechnen und hinschreiben.
- Läuft parallel eine Api auf `:5151`, ist das hier egal — E2E fährt dieser Task nicht.

**Szenarien (Konzept 6.3), jedes mit seinem Widerleger aus 6.2.**

- **S1 — Socket-Abbruch (Fall A), fünfmal, ≥ 2 min Abstand, Lauf B (einmal zusätzlich Lauf A für
  Schritt 7).** Kill über `nsenter … ss -K dport = :443` im Netz-Namespace des Containers (der Kernel
  kann es, 1.1). Reihenfolge der Belege: (1) „TwitchClient getrennt" ≤ 1 s nach t₀; (2)
  „Wiederaufbau #1 in 0 s"; (3) „Fatal network error" als Information, **genau einmal**; (4)
  „Twitch-Verbindung steht nach … s" — Δ zu t₀ ist SLO-1; (5) **genau N** „Joining channel"-Zeilen
  (TwitchLib) und N „Channel … gejoint" (wir), erstere im 600-ms-Raster ± 100 ms, letztere je
  ~200 ms dahinter, keine „nicht bestätigt"-Zeile; (6) „Rejoin abgeschlossen: N gewünscht, N
  bestätigt, 0 offen, T s" — Δ letzte Bestätigung zu t₀ ist SLO-2; (7) Lauf A: genau eine
  „ListenTaskActionAsync"-Trace-Zeile für den neuen Client, kein „TwitchClient reconnected".
  Widerlegt durch: mehr als N Joining-Zeilen, ein Abstand < 400 ms, N Zeilen binnen < 0,4 × N s,
  Median SLO-1 > 3 s oder p95 > 5 s über die fünf Läufe, **SLO-2 in einem Lauf gerissen oder K > 0**.
  Ergebnis: eine Tabelle mit fünf Zeilen — t_Signal, t_004, t_letzte Bestätigung, K, Zahl der
  Joining-Zeilen, kleinster und größter Abstand, Log-T.
- **S2 — `RECONNECT` (Fall B) über den Debug-Auslöser (Task 6).** Ohne Freigabe: nur die
  „ignoriert"-Zeile, Verbindung steht. Mit Freigabe: „wird injiziert", dann `OnDisconnected`, genau
  ein „Fatal network error", **kein** „TwitchClient reconnected", danach S1-Schritte 2–6. Ehrlich
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
  vierter Kill → wieder 0 s; „Fatal network error" viermal. Nebenbeobachtung für R3: schlägt ein
  Aufbau in dieser Serie fehl, ist das der einzige lokale Hinweis auf Twitch-seitiges Verhalten (von
  der Wohn-IP).
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
Zeile grün / rot / nicht messbar, mit der Zahl. Dazu die S1-Tabelle, die S3-Abstände, die S4-Folge,
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
   Slot-Regel (1.3, Punkt 1), die Token-Kette der Shutdown-Zusage, die Wunschzustandsprüfung nach dem
   Gate, das Verwerfen halbfertiger Clients. Findings sind Input, kein Auftrag; widersprechen sich
   Opus-Review und Codex bei einem P1/P2, entscheidet Fable. „Reviewer failed to output a response"
   mit Exit 1 ist das Kontingent, kein Absturz.
2. PR-Text nennt: **Deploy-Sperre** (mergen ja, deployen erst nach dem 2026-10-08, gebündelt mit
   #122); die SLO-Ergebnisse; was lokal nicht beweisbar ist; die Coverage-Zahl aus Task 8 mit der
   R8-Einordnung; keine Migration, kein Frontend, kein neuer Fehlercode; die Umsetzungsentscheidungen
   aus 1.3.
3. **Übergabe an den Nutzer für die Zeit nach dem Deploy** (Konzept 6.6, kein Agent): vor dem Deploy
   die #117-Zahlen als Baseline sichern; 24 h ab Containerstart zählen: „Wiederaufbau #1"
   (Ereignisse), „Wiederaufbau #≥ 2" (Fehlversuche), p50/p95 von „Twitch-Verbindung steht nach"
   (SLO-1) und von „Rejoin abgeschlossen … T s" (SLO-2), Summe der „K offen", 0 × Stolperdraht, 0 ×
   Backstop, 0 × Sentinel; **Fall-B-Beleg:** TwitchLib „Reconnecting to Twitch" unmittelbar gefolgt
   von „TwitchClient getrennt", „Wiederaufbau #1", „steht", ohne „TwitchClient reconnected".
   Abbruchkriterium: SLO-2-p95 gerissen, ein Wiederaufbau mit K > 0, oder > ~2 Ereignisse/h über
   mehrere Stunden → Rollback bzw. Cooldown nachziehen. Danach der Harness-Vergleich Σ|Log − Live| /
   ΣLive vor/nach (R7).

**Fertig.** Alle sieben Szenarien mit positivem Beleg, SLO-2 fünfmal gehalten mit K = 0, Codex
vorgelegt, PR offen. **Der Merge selbst bleibt beim Nutzer.**

---

## 4. Reihenfolge und Abhängigkeiten

0 (Messung, zuerst — ihr Befund kann die Konstanten in Task 1 und die Begründung in Task 7 ändern)
→ 1 ∥ 2 ∥ 3 (unabhängig, parallel startbar) → 4 (braucht alle drei) → 5 ∥ 6 ∥ 7 (brauchen 4: Task 5
und 6 hängen ihren Absatz an den Eintrag aus 4, Task 7 beschreibt den Zustand nach 4 und übernimmt
die 7.2-Konsequenz aus Task 0) → 8 → 9. Task 2 und 3 hängen fachlich nicht an Task 0; wer die
Messung nicht abwarten will, darf sie parallel fahren — nur Task 1 wartet auf Punkt 2b des Befunds.

Die Hauptsession prüft nach jedem Task die Fertig-Bedingung, bevor der nächste startet. Task 3 und 4
sind ein Paar (Übergangszustand, s. Task 3); sie werden nie einzeln gemergt — und ohnehin wird erst
nach Task 9 gemergt.

Commits (Conventional Commits, je Task): `feat(worker): add the pure Twitch reconnect backoff policy`
· `feat(worker): count Twitch rebuilds and failed attempts in WorkerStats` ·
`refactor(worker): replace the spent-client branch of the watchdog policy with an in-flight input` ·
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
| R3 | Twitch wertet den 0-s-Wiederaufbau oder die S4-Serie als Missbrauch | Task 9, S4; danach nur Prod | Fehlschlag ab dem zweiten/dritten Kill / S4 grün; Prod-24-h ohne Serie. Falls bestätigt: erster Delay 1–2 s, Flap-Boden ab der zweiten Sitzung — beides Policy-Konstanten (Task 1), kein Transport |
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
| P2 | Stale `<see cref="ReconnectPolicy"/>` erzeugt `CS1574`, unsichtbar ohne `--no-incremental` | Task 4 zieht alle sieben Stellen; Task 8 baut `--no-incremental` | neue Warnung im Build |
| P3 | Ein Signal aus einem fehlgeschlagenen Versuch bleibt im Slot und löst einen zweiten Wiederaufbau aus | Task 4, Slot-Regel (1.3, Punkt 1) | „Wiederaufbau #1" ohne vorangehendes „getrennt" nach einem gelungenen Aufbau in S3 |
| P4 | `WorkerBootSequenceTests` bricht am `Worker`-Konstruktor | Task 6 | Compile-Fehler im Testprojekt |
| P5 | `appsettings.Development.json` überschreibt 7.10 bei lokalem `dotnet run` | Task 4 (beide Dateien) | Kontrollfrage aus 6.1 fällt bei `dotnet run` durch |
| P6 | Die Host-Kommandos in S1/S3 brauchen `sudo` mit Passwort | Task 9, Rollen | Sonde hängt; nach zwei Fehlschlägen abbrechen und den Nutzer fragen (Projektnotiz) |
| P7 | **Annahme:** Twitch drosselt oder sperrt IP-seitig — die JOIN-Limit-Sonde von der Wohn-IP träfe dann die laufende #73-Negativprobe im Dev-Worker mit | Task 0 (Stufen, Abbruchkriterium, Dev-Worker-Log läuft mit) | Dev-Worker loggt während der Sonde „Join … nicht bestätigt" oder „TwitchClient getrennt" → sofort stoppen, im Befund festhalten; bleibt er still, ist die Annahme für diesen Lauf nicht bestätigt — nicht widerlegt |
| P8 | Der Befund aus Task 0 verändert Zahlen, nachdem Task 1 schon gebaut ist | Reihenfolge in Abschnitt 4 | Task 1 startet erst mit Punkt 2b des Befunds; ändert sich R3, sind es zwei Konstanten und zwei Testerwartungen in Task 1, kein Umbau |

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
- Er deployt nicht (Abschnitt 0).

**Vorgelegt, weil der Plan hier über den Wortlaut des Konzepts hinaus entscheiden musste** (1.3):
die Slot-Regel (Handler signalisieren nur nach Handshake, Slot leert beim Konsumieren), die
sequentielle Schleife mit dem dadurch am Tick immer falschen `reconnectInFlight`, kein Trennen beim
Shutdown, der Flap-Boden als untere Schranke auch für Fehlversuche, 2 s nach einem gescheiterten
Boot-Connect, zwei Uhren für T, S7 in Lauf A, beide `appsettings`-Dateien. Kippt der Nutzer eine
davon, ändert sich der genannte Task, sonst nichts.
