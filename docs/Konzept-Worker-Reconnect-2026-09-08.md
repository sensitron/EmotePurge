# Konzept: Der Worker stellt seine Twitch-IRC-Verbindung selbst wieder her (2026-09-08)

Auftrag: Entwurf des Modells, nach dem `EmotePurge.Worker` seine Twitch-IRC-Verbindung künftig
wiederherstellt, wenn TwitchLibs eigener Reconnect abgeschaltet wird — Issue #68 (JOIN-Schwall nach
einem Reconnect), im Zusammenhang mit #114 (gespleißte IRC-Zeilen nach einem In-Place-Reconnect).
Kein Plan, kein Code: Verträge, Zustandsübergänge, Zahlen mit Begründung, Grenzfälle, Beweisführung.
Methodik: vollständiger Read des Verbindungspfads im Worker (`TwitchChatManager`, `ReconnectPolicy`,
`TwitchWatchdogPolicy`, `TwitchConnectionWatchdog`, `Worker`, `SevenTvPeriodicResyncWorker`,
`SevenTvBackoffPolicy`), Abgleich mit den Issues #68/#114/#117/#118/#122 und den
DECISIONS-Einträgen vom 2026-07-26/27, 2026-08-03 und 2026-09-08, und — weil die beiden heutigen
Untersuchungen sich an einer Stelle widersprachen — ein **Read der installierten Binärstände**
`TwitchLib.Client 4.0.1` und `TwitchLib.Communication 2.0.1` aus `~/.nuget/packages`, dekompiliert
mit `ilspycmd 11.0.0` (gleicher Stand wie im Container, s. `EmotePurge.Worker.csproj:12,15`).
Kennzeichnung wie in den Untersuchungen vom 2026-07-30 und 2026-08-01: **belegt** (Codestelle,
Binärstand oder Messung) / **plausibel** (eigene Ableitung aus Belegtem) / **offen** (nicht
überprüft oder nicht entschieden).

Prod wird nicht berührt; der Deploy wartet bis nach dem **2026-10-08** (Messfenster, #118 —
Einordnung in Abschnitt 5.4). Kein `DECISIONS.md`-Eintrag in diesem Papier — der kommt mit dem
Commit, der die Topologie ändert (Regel 3).

---

## 0. Fazit vorab

**TwitchLibs Eigenreconnect abschalten, die Wiederherstellung selbst fahren — ereignisgetrieben,
nicht per Minutentakt.** Der Auslöser ist TwitchLibs eigenes `OnDisconnected`, das die Bibliothek
auch ohne Reconnect-Policy binnen ~0,6 s nach einem Verbindungsverlust liefert (belegt am
Binärstand, Abschnitt 3.2). Ein Wiederaufbau ist immer ein **Ersatz des Client-Objekts** (der Pfad,
der seit 2026-07-26 produktiv ist), nie ein Reconnect am selben Objekt — Letzteres ist mit
`NoReconnectionPolicy` nicht bloß unnötig, sondern **strukturell unmöglich** (Abschnitt 3.1).
Der Watchdog behält genau eine Aufgabe: das Netz gegen stille Verbindungen (15 min ohne Frame), und
er wird zum Träger der neuen Wiederaufbau-Schleife.

Zwei Zielgrößen, gegen die später gemessen wird (Abschnitt 3.6): **SLO-1, Verlust → `004`**
(Verbindung steht): Median ≤ 3 s, p95 ≤ 5 s, harte Schranke 25 s je Versuch — diagnostisch.
**SLO-2, Verlust → letzte Join-Bestätigung (`OnJoinedChannel`) aller N gewünschten Kanäle:
≤ 6 s + 0,6 s × (N − 1)**, bei 13 Kanälen also ≤ 13,2 s, bei 20 ≤ 17,4 s, und **null
unbestätigte Kanäle** am Ende der Runde — SLO-2 trägt das Abbruchkriterium, weil es die
Wiederaufnahme der Zählung misst, nicht das Senden. Heute (seit #114) sind es pro Ereignis
**zwei** Lücken statt einer, und die zweite davon ist ohnehin der Recreate, den dieses Papier zum
einzigen Weg macht. Die Fassung vom Abend des 2026-09-08 hatte nur eine Zielgröße (Verlust →
letzter gesendeter JOIN); die Gegenrede hat sie zu Recht als Messung des Sendens statt der
Zählung verworfen (Abschnitt 10).

Der wichtigste Nebenbefund, der in keiner der beiden Untersuchungen stand: **unser gedrosselter
Rejoin läuft heute inline in der Lese-Schleife des frischen Clients** — `Handle004` awaitet unser
`OnConnected`, und das awaitet 600 ms × N. Solange die Schleife steht, verarbeitet TwitchLib keine
Join-Bestätigung, und ab 5 s meldet es Joins als fehlgeschlagen, die längst im Socket-Puffer
bestätigt liegen (Abschnitt 3.5). Das Konzept holt den Rejoin deshalb aus der Schleife heraus.

Der im Auftrag als offen markierte Quelltext-Widerspruch (1000-ms-Timer vs. 28 JOINs in 5 s) ist
am Binärstand **auflösbar** — die Messung stimmt, die Lesung nicht (Abschnitt 7.1) — und berührt
die Entscheidung ohnehin nicht.

**Brücke, nicht Endstation.** Die Drosselung beseitigt das Problem nicht, sie beherrscht es. Der
Weg, der es strukturell beseitigt, ist belegt (Abschnitt 9): EventSub über **Conduits** hängt die
Kanal-Subscriptions an den Conduit statt an die Verbindung — ein abgerissener Shard wird mit
**einem** Helix-`PATCH` wieder eingehängt, unabhängig von der Kanalzahl, und mit App-Access-Token
entfallen beide Twitch-Limits. Der Preis ist das Ende des anonymen Betriebs (Bot-Account,
Zustimmung je Kanal) — eine Produktentscheidung, die nicht in dieses Papier gehört. Das hier
entworfene Modell ist so geschnitten, dass die **Wiederaufbau-Entscheidung** (wann, wie oft, mit
welchem Backoff) transportfrei bleibt und für einen Conduit-Shard wiederverwendbar ist; nur die
**Wiederaufbau-Aktion** (Objekt ersetzen, `004` abwarten, Kanäle rejoinen) ist TwitchLib-spezifisch
und würde ausgetauscht (Abschnitt 9.3, Entscheidung E5).

---

## 1. Anlass und Entscheidungsfrage

**Was heute schiefgeht.** TwitchLib rejoint nach einem Reconnect jeden gehaltenen Kanal selbst und
ungedrosselt: gemessen am 2026-07-30 **28 JOINs in 5,0 s** (Log 17:47:13–17:47:18, s.
`docs/Review-2026-07-29-Umsetzung.md`, „Offenes Skalierungsthema"), gegen Twitchs dokumentierte
Grenze von 20 JOINs pro 10 s. Bei dieser Messung biss die Grenze nicht (0 ×
`OnFailureToReceiveJoinConfirmation`), aber sie ist eine Einzelmessung von einer Wohn-IP; das
Betriebsbudget `TwitchJoinBudgetChannels = 20` (`src/EmotePurge.Api/Health/WorkerCapacity.cs:26`)
existiert allein wegen dieses Pfads. Oberhalb von ~20 Kanälen verlieren bei einem Reconnect
**zufällige Bestandskanäle** ihren Join bis zur nächsten `EnsureJoinedAsync`-Runde (≤ 60 s) — das
einzige bekannte Risiko, das alle Kanäle gleichzeitig trifft (#118, Dauerauflage 2). Seit #114 kommt
hinzu: TwitchLibs Reconnect am selben Objekt hinterlässt zwei Lese-Schleifen am selben Socket, die
IRC-Zeilen spleißen können; der Client wird deshalb nach jedem Reconnect vom nächsten
Watchdog-Tick ersetzt — und **jeder Kanal pro Ereignis zweimal gejoint**, einmal ungedrosselt von
TwitchLib, einmal gedrosselt von uns (Abschnitt 2.2).

**Die Entscheidungsfrage.** Die einzige Stellschraube, die den ungedrosselten Pfad abschaltet, ist
`NoReconnectionPolicy` im `ClientOptions` des `WebSocketClient` — nur zum Konstruktionszeitpunkt
setzbar (`IClient.Options` hat keinen Setter, belegt am Binärstand). Dann reconnectet TwitchLib
nicht mehr, und **wir** bestimmen das Timing. Heute stellt TwitchLib die Verbindung nach ~1,5–3 s
wieder her; unser Watchdog tickt im Minutentakt. Entschiede nur der Watchdog, würde aus einer
Lücke von wenigen Sekunden eine von bis zu 60 s — und jede Sekunde ohne Verbindung ist eine
Zähllücke. Das Papier muss also zeigen, **wer** den Wiederaufbau auslöst, **wann**, und mit welcher
Zahl das später gemessen wird.

---

## 2. Was TwitchLibs Eigenreconnect uns bringt und kostet

### 2.1 Was er bringt

Genau eines: **Schnelligkeit ohne eigenes Zutun.** Mit der parameterlosen `ReconnectionPolicy`
(`TwitchChatManager.cs:571-584`) überwacht TwitchLibs `ConnectionWatchDog` den Socket im
200-ms-Takt (`MonitorTaskDelayInMilliseconds = 200`, belegt), stößt bei `!IsConnected` einen
`ReconnectInternalAsync` an, wartet `DisconnectWait` = 1.500 ms, verbindet neu und versucht es
bei Fehlschlag mit 3 s → 6 s → … → 30 s unbegrenzt weiter (`ReconnectionPolicy()`:
`_reconnectStepInterval = 3000`, `_maxReconnectInterval = 30000`, `_maxAttempts = null`, belegt).
Ein Verbindungsverlust kostet damit im Normalfall ~2–4 s, ohne dass unser Code etwas tut.

### 2.2 Was er kostet

1. **Den JOIN-Schwall (#68).** `_client_OnReconnected` reiht alle Kanäle aus
   `_joinedChannelManager` in `_joinChannelQueue` ein und ruft `QueueingJoinCheckAsync()` — das
   sendet den ersten JOIN; jeden weiteren sendet `Handle366`, also die Bestätigung des vorherigen
   (belegt, Abschnitt 7.1). Kadenz ist damit der Round-Trip zu Twitch, gemessen ~180–200 ms. Kein
   Pfad, den wir drosseln können.
2. **Die gespleißten Zeilen (#114).** Twitchs `RECONNECT` läuft inline aus der Lese-Schleife
   (`HandleIrcMessageAsync` → `ReconnectAsync()`), `ClosePrivateAsync` beendet die Schleife nicht,
   `OpenPrivateAsync(true)` → `_networkServices.Start()` startet eine zweite. Belegt am
   Binärstand, deckungsgleich mit #114.
3. **Die zweite Lücke.** Seit #114 folgt auf jeden In-Place-Reconnect der Recreate im nächsten
   Watchdog-Tick (≤ 60 s). `RecreateClientAsync` (`TwitchChatManager.cs:215-251`) **awaitet**
   `oldClient.DisconnectAsync()`, bevor es den neuen öffnet — und `DisconnectAsync` enthält
   allein zwei feste Wartezeiten (400 ms `ConnectionWatchDog.StopAsync` + 1.500 ms
   `DisconnectWait`, belegt). Die zweite Lücke ist also ≈ 2 s + Verbindungsaufbau + 0,6 s je Kanal.
4. **Bis zu 60 s Doppelschleifen-Fenster** je Ereignis, in dem Spleiße möglich sind.
5. **Verstecktes Komplexitätskonto:** Weil ein Open-Loop beliebig lang laufen kann, braucht der
   Manager `OpenWaitTimeout` (30 s), `ObserveInBackground`, `LogOpenStillRunning`, den
   `Wait`-Zweig mit `StuckOpenThreshold` (10 min) und die Fehlerstreak-Regel (≥ 3 → Recreate) —
   alles Werkzeug, um einem Prozess zuzusehen, den wir nicht steuern.

### 2.3 Bringt er nach #114 überhaupt noch etwas?

**Nein — messbar nichts, was der Recreate nicht auch liefert.** Der In-Place-Reconnect ist seit
#114 nur noch die *erste Hälfte* eines Ereignisses, dessen zweite Hälfte ohnehin ein Recreate ist.
Er verkürzt die Gesamtlücke nicht, sondern **verlängert** sie: Lücke 1 (~2–4 s + Schwall) plus
Lücke 2 (~2 s + 0,6 s × N) statt einer Lücke. Was er heute noch leistet, ist das *Bemerken* des
Verlusts binnen 200 ms — und genau das leistet er mit `NoReconnectionPolicy` weiterhin
(Abschnitt 3.2), nur ohne die Folgen 1–5. Die Bilanz ist eindeutig.

---

## 3. Das vorgeschlagene Modell

### 3.1 Was `NoReconnectionPolicy` wirklich tut — und was daraus zwingend folgt

Der Binärstand ist eindeutiger als die XML-Doku („should be used to omit reconnect-attempts"):

- `NoReconnectionPolicy` ist `ReconnectionPolicy(reconnectInterval: 0, maxAttempts: 1)`. **Belegt.**
- `OpenPrivateAsync(isReconnect)` ruft `Reset(isReconnect)`; `Reset(true)` **setzt `_attemptsMade`
  nicht zurück** (nur `Reset(false)` tut das). Nach dem einen erfolgreichen Erstverbindungsversuch
  steht `_attemptsMade == 1 == _maxAttempts`. **Belegt** — dieselbe Eigenschaft, die am 2026-07-26
  die Zehn-Versuche-Falle erzeugt hat (Kommentar in `CreateClient`).
- Folge: **`ReconnectAsync()` an einem `NoReconnectionPolicy`-Client kann nie gelingen.**
  `ReconnectInternalAsync` → `ClosePrivateAsync` → `OpenPrivateAsync(true)`: die Schleife
  `while (!IsConnected && !AreAttemptsComplete())` läuft **null Mal**, danach `RaiseFatal()` →
  `OnFatality` → TwitchClient `_client_OnFatality` → unser `OnConnectionError` mit „Fatal network
  error.". Rückgabe `false`. **Belegt.**
- `OpenAsync()` (isReconnect: false) dagegen setzt zurück und **könnte** dasselbe Objekt erneut
  verbinden. Das Konzept nutzt das bewusst **nicht** (Abschnitt 3.4, Entscheidung E3).

Damit ist die Aktion `ReconnectAction.Reconnect` nicht „überflüssig", sondern **kaputt**: Sie muss
entfallen, und `_clientSpent` wird gegenstandslos — `RaiseReconnected` steht hinter einem
erfolgreichen `OpenPrivateAsync(true)`, den es nicht mehr gibt, `OnReconnected` kann also nie mehr
feuern (Abschnitt 3.7).

### 3.2 Was TwitchLib ohne Reconnect-Policy bei einem Verbindungsverlust noch liefert

Das ist die Grundlage für „ereignisgetrieben". Drei Fälle, alle am Binärstand nachvollzogen:

**Fall A — Socket bricht (RST, Abort, Fehler in `ReceiveAsync`).** Die Lese-Schleife fängt die
Exception, ruft `RaiseError`, bricht ab. `IsConnected` (= `ClientWebSocket.State == Open`) wird
falsch. Der `ConnectionWatchDog` sieht das binnen 200 ms → `ReconnectInternalAsync` →
`ClosePrivateAsync`: `_networkServices.StopAsync()` (400 ms), Token-Cancel, `Abort`,
**`RaiseDisconnected` → unser `OnDisconnected`** (≈ t₀ + 0,6 s), neuer CTS, 1.500 ms Warten.
Dann `OpenPrivateAsync(true)` → kein Versuch → `RaiseFatal` → **unser `OnConnectionError`**
(≈ t₀ + 2,1 s), Rückgabe `false`. Der Watchdog ruft `CloseAsync()` → ein **zweites
`OnDisconnected`** (≈ t₀ + 2,5 s) → `break`; die Monitor-Schleife endet. **Belegt.**

**Fall B — Twitch sendet `RECONNECT`.** `HandleIrcMessageAsync` → `ReconnectAsync()` inline in der
Lese-Schleife → `ReconnectInternalAsync` wie oben: `OnDisconnected` nach ≈ 0,4 s, dann 1,5 s,
dann `OnConnectionError`, `false`. Die Lese-Schleife kehrt zu `while (IsConnected)` zurück, der
Socket ist abgebrochen → sie endet. **Genau eine Schleife, kein Spleiß, kein Schwall.** Belegt.
Zu beachten: unser `OnDisconnected`-Handler läuft hier **in** der Lese-Schleife des sterbenden
Clients — er muss deshalb nur signalisieren, nicht selbst wiederaufbauen (E1 unten).

**Fall C — stille Verbindung (kein FIN/RST, z. B. Netzabriss ohne Fehler).** `State` bleibt `Open`,
`ReceiveAsync` hängt, der `ConnectionWatchDog` sieht nichts. TwitchLib liefert **kein** Ereignis.
Beobachtet am 2026-07-30 (6,5-min-Netzschnitt: erst unser Watchdog bei 303 s Stille reagierte).
Das bleibt die Aufgabe unseres Watchdogs (15 min ohne Frame). **Belegt** für das Verhalten von
damals; **offen**, ob .NETs WebSocket-Keep-Alive den Fall in bestimmten Konstellationen früher
erkennt — für das Konzept unerheblich, weil der Watchdog ihn ohnehin abdeckt.

Merksatz: **In den Fällen A und B kommt das Signal binnen einer Sekunde, nicht binnen einer Minute.**
Der Minutentakt des Watchdogs ist nur noch für Fall C relevant — und dort war er es schon immer.

### 3.3 Zustände, Auslöser, Zuständigkeit

Drei Rollen, klar getrennt:

| Rolle | Wer | Aufgabe |
|---|---|---|
| **Transport** | `TwitchChatManager` | hält genau einen `TwitchClient`, wired/unwired ihn, stempelt Frames, verwaltet `_desiredChannels`, führt Joins mit 600-ms-Drossel aus. Trifft **keine** Timing-Entscheidung. |
| **Signalgeber** | TwitchLib-Ereignisse (`OnDisconnected`, `OnConnectionError`), der Watchdog-Tick (stale), ein Handshake-Timeout | jedes Signal = „Verbindung ist weg oder verdächtig", mit Grund, **koalesziert** (viele Signale in kurzer Folge = ein Wiederaufbau). |
| **Wiederaufbau-Schleife** | `TwitchConnectionWatchdog` (erweitert; Umbenennung offen, Abschnitt 7.3) | wartet auf *Signal oder Minutentick*; bei Signal: Recreate-Sequenz mit Backoff, bis die Verbindung steht; bei Tick: `TwitchWatchdogPolicy.Decide` wie heute (ohne `clientSpent`). |

**Entscheidung E1 — Handler signalisieren nur.** Kein TwitchLib-Handler (`OnDisconnected`,
`OnConnectionError`, `OnConnected`, `OnReconnected`) baut selbst auf, wartet oder rejoint. Jeder
setzt Zustand, stempelt Zeit und legt höchstens ein Signal ab; die Arbeit macht die Schleife auf
ihrem eigenen Task. Grund: `OnDisconnected` läuft in Fall B in der Lese-Schleife des sterbenden
Clients, `OnConnected` in der des neuen (Abschnitt 3.5) — Arbeit in Handlern ist genau die
Kopplung, die #114 erzeugt hat, und sie wäre nicht testbar (Regel 11).

Zustandsdiagramm der Schleife (Text, weil Prosa hier unklar bliebe):

```
            Signal(reason)                    Handshake ok (004)
 Connected ────────────────► Reconnecting ─────────────────────► Rejoining ──► Connected
    ▲                        │ ▲                                    │
    │  Tick: stale ≥ 15 min  │ │ Versuch fehlgeschlagen             │ Disconnect mittendrin
    └────────────────────────┘ └── warte NextDelay(streak) ◄────────┘  (= neues Signal)
                                              │
                                     Shutdown-Token ──► Stopped (kein neuer Versuch)
```

- **`Connected`:** Handshake (`004`) empfangen, `_isConnected = true`. Ticks prüfen nur Stale.
- **`Reconnecting`:** ein Versuch läuft oder wartet. Weitere Signale werden **verschluckt**
  (Information-Log, kein zweiter Aufbau) — heute schon so (`_reconnectLock` mit `WaitAsync(0)`).
  Ticks werden übersprungen.
- **`Rejoining`:** Verbindung steht, die gedrosselte JOIN-Runde läuft **außerhalb** der
  Lese-Schleife (E2). Ein Disconnect währenddessen bricht die Runde ab und ist ein neues Signal.
- **`Stopped`:** Host fährt herunter. Kein neuer Client wird geöffnet; ein laufendes Warten wird
  abgebrochen; der aktuelle Client wird getrennt.

**Auslöser im Einzelnen:**

| Auslöser | Quelle | Verzögerung bis zum Signal | Grund-Text |
|---|---|---|---|
| `OnDisconnected` des **aktuellen** Clients | TwitchLib (Fälle A/B) | ≈ 0,4–0,6 s | „TwitchClient getrennt" |
| `OnConnectionError` des aktuellen Clients | TwitchLib (`Fatal`) | ≈ 2 s | koalesziert mit dem vorigen; allein nur relevant, falls `OnDisconnected` ausbleibt |
| Stale: kein Frame seit 15 min | Watchdog-Tick | ≤ 60 s nach Schwelle | wie heute |
| Handshake-Timeout: Socket offen, aber kein `004` binnen 10 s | Schleife selbst | 10 s | „Handshake nicht abgeschlossen" |
| Disconnected-Backstop: `!IsConnected`, kein Versuch aktiv, > 1 min seit letztem | Watchdog-Tick | ≤ 60 s | **darf nie feuern** — Warning-Log als Fehlerindikator (Abschnitt 8, R1) |
| `OnReconnected` (Stolperdraht) | TwitchLib | — | „darf nicht passieren" — Error-Log + Signal mit 10-s-Boden (Abschnitt 3.7) |

„Aktueller Client" heißt: das Objekt, das im Manager gerade als `_client` steht. Ereignisse eines
schon ersetzten Clients erreichen uns nicht, weil er beim Ersatz unwired wird — das ist heute so
und bleibt der Mechanismus, der die zweite `OnDisconnected` aus Fall A unschädlich macht (sie kommt
nach dem Ersatz und fällt ins Leere; kommt sie **vor** dem Ersatz, wird sie koalesziert).

### 3.4 Die Recreate-Sequenz (Vertrag, kein Code)

Einmal pro Versuch, unter dem bestehenden `_reconnectLock`:

1. **Alten Client abkoppeln:** unwire, `_isConnected = false`, alle Kanäle unbestätigt markieren
   (`MarkAllChannelsUnconfirmed`, wie heute). Ab hier ist er für uns tot.
2. **Alten Client im Hintergrund aufräumen, nicht abwarten** (E4): `DisconnectAsync()` in einen
   beobachteten Hintergrund-Task, Exceptions geloggt und verschluckt. Begründung: sein Socket ist
   bereits abgebrochen, das Warten kostet ≥ 1,9 s eingebauter Delays (Abschnitt 2.2, Punkt 3)
   und gewinnt nichts. **Plausibel**, Risiko in Abschnitt 8 (R5).
3. **Neuen Client bauen** mit `ClientOptions(new NoReconnectionPolicy())`, wiren, als `_client`
   einsetzen, `_joinsIssuedForCurrentClient = 0`.
4. **Öffnen:** `ConnectAsync()`. Mit `NoReconnectionPolicy` ist das **genau ein** Versuch, durch
   TwitchLibs `TimeOutEstablishConnection` = 15 s beschränkt (belegt). Rückgabe `false` oder
   Exception = Fehlschlag. Gewartet wird mit `WaitAsync(stoppingToken)`: TwitchLibs Connect ist
   nicht abbrechbar, er wird beim Shutdown **verlassen**, nicht abgewartet — der halbfertige Client
   wird wie in Schritt 2 im Hintergrund verworfen.
5. **Auf den Handshake warten:** `OnConnected` (`Handle004`) binnen 10 s nach erfolgreichem
   Öffnen, sonst Fehlschlag (Signal „Handshake nicht abgeschlossen"); ebenfalls auf dem
   Stopping-Token. Der Handler selbst tut nur noch: `_isConnected = true`, Zeitstempel, ein
   `TaskCompletionSource` erfüllen. **Kein Rejoin im Handler** (E2).
6. **Erfolg:** Streak an die Backoff-Policy melden (Streak → 0 beim `004`, Abschnitt 3.6),
   Zustand `Rejoining`, dann `RejoinDesiredChannelsAsync(stoppingToken)` aus der Schleife heraus
   (Abschnitt 3.5).
7. **Fehlschlag:** Streak erhöhen, `NextDelay` abwarten (auf dem Stopping-Token), zurück zu 1.

**Shutdown-Zusage der Sequenz:** Jeder wartende Schritt — 4, 5, 6, 7 und das `_joinGate` in
`TryJoinAsync` — nimmt das Stopping-Token. `TwitchConnectionWatchdog.StopAsync` kehrt damit
**≤ 1 s** nach dem Token zurück, unabhängig davon, in welchem Zustand die Sequenz steht.
Warum das eine Zusage und keine Selbstverständlichkeit ist, steht in 4.6 (Wechselwirkung mit #122).

Damit entfallen: `OpenWaitTimeout` (30 s) samt `ObserveInBackground`/`LogOpenStillRunning` — ein
Versuch endet jetzt beweisbar binnen ~15 s + 10 s —, der `Wait`-Zweig mit `StuckOpenThreshold`,
die Fehlerstreak-Regel `MaxConsecutiveConnectionErrors = 3`, und `ReconnectClientAsync` komplett.

**Entscheidung E3 — Ersatz statt `OpenAsync()` am selben Objekt.** `OpenAsync()` würde die Policy
zurücksetzen und das alte Objekt könnte neu verbinden. Verworfen, weil das alte Objekt Zustand
mitschleppt, den wir nicht sehen: `_currentlyJoiningChannels` kann bei einem Abbruch mitten im Join
auf `true` hängen bleiben (heilt erst nach dem 5-s-Timeout des `_joinTimer`), `_awaitingJoins` und
`_joinedChannelManager` sind halb geleert. Ein frisches Objekt hat garantiert eine leere Queue und
genau eine Lese-Schleife; der Pfad ist seit 2026-07-26 produktiv erprobt. Preis: eine Allokation
pro Ereignis — vernachlässigbar.

### 3.5 Der Rejoin — und warum er aus der Lese-Schleife heraus muss (E2)

Befund am Binärstand: `Handle004` gibt `OnConnected.TryInvoke(...)` zurück, `TryInvoke` gibt den
Task des Handlers zurück, `HandleIrcMessageAsync` wird von `_client_OnMessage` awaitet, das von
`RaiseMessage` und das von `ListenTaskActionAsync`. **Unser `OnConnected`-Handler läuft also
synchron in der Lese-Schleife, und `RejoinDesiredChannelsAsync` blockiert sie für 0,6 s × N.**

Folgen heute (**plausibel**, aus dem Binärstand abgeleitet, live nicht gemessen):

- Während der Blockade wird kein `366` verarbeitet. Unser zweiter `JoinChannelAsync` (nach 600 ms)
  trifft auf `_currentlyJoiningChannels == true` und wird von TwitchLib nur **eingereiht**, nicht
  gesendet. Unsere 600-ms-Drossel drosselt damit nicht das Senden, sondern das Einreihen; gesendet
  wird nach dem Ende der Blockade in TwitchLibs eigener Kadenz (`Handle366`-Kette, ~200 ms).
- Dauert die Blockade > 5 s (ab 9 Kanälen), meldet der `_joinTimer` den **ersten** Join als
  fehlgeschlagen (`OnFailureToReceiveJoinConfirmation`) und entfernt ihn aus TwitchLibs Manager,
  obwohl seine Bestätigung längst im Puffer liegt. Für uns harmlos (`_desiredChannels` wird beim
  verspäteten `HandleJoin` doch bestätigt), aber eine irreführende Warnung — und ein Hinweis, dass
  die Warnung „Twitch hat den Join für … nicht bestätigt" heute nicht bedeutet, was sie sagt.

Vertrag neu: Der Rejoin ist **Schritt 6 der Recreate-Sequenz**, ausgeführt von der
Wiederaufbau-Schleife, nicht vom Handler. `TryJoinAsync` behält sein 600-ms-Gate über alle
Aufrufer, bekommt aber zwei Änderungen: (1) das Token (Shutdown-Zusage aus 3.4) und (2) **nach**
dem Erwerb des Gates, unmittelbar vor dem Senden, eine erneute Prüfung, ob der Kanal noch in
`_desiredChannels` steht — sonst kein JOIN (Grenzfall 4.5). Die Runde bricht ab, sobald
`_isConnected` falsch wird oder das Token feuert, statt N Warnungen „Join aufgeschoben" zu
produzieren.

**Was `TryJoinAsync` nicht weiß — und die Abschlusszeile deshalb wissen muss.** `TryJoinAsync`
kehrt zurück, wenn TwitchLibs `JoinChannelAsync` zurückkehrt, und das ist **nach dem Senden oder
nach dem Einreihen**, nie nach der Bestätigung (belegt: `QueueingJoinCheckAsync` awaitet nur
`SendAsync`; ein Sendefehler wird von TwitchLib als `false` verschluckt). Eine Zeile „Rejoin
abgeschlossen: N" nach der letzten Rückkehr bewiese also nur, dass N Aufrufe stattgefunden haben.
Vertrag: Die Runde wartet nach dem letzten Aufruf **bis zu 5 s** (die Timeout-Spanne von TwitchLibs
`_joinTimer`) auf die Bestätigungen und loggt dann „Rejoin abgeschlossen: N gewünscht, M bestätigt,
K offen, T s seit Verlust" — mit **Warning statt Information, sobald K > 0**. K ist die Zahl, die
SLO-2 (Abschnitt 3.6) verlangt; die K offenen Kanäle bleiben unbestätigt in `_desiredChannels`
und werden von `EnsureJoinedAsync` ≤ 60 s später nachgeholt, wie heute — aber jetzt **sichtbar**.

Was das für den 2026-07-30-Messwert heißt: „alle 25 im 600-ms-Takt sauber gejoint" wurde über den
`EnsureJoinedAsync`-Pfad gemessen, der **nicht** in der Lese-Schleife läuft. Der Rejoin-Pfad nach
einem Recreate wurde nie einzeln vermessen; Abschnitt 6 holt das nach.

### 3.6 Backoff: Zahlen mit Begründung

| Größe | Wert | Begründung |
|---|---|---|
| Verzögerung des **ersten** Versuchs | **0 s** | Das Signal ist verlässlich (Fälle A/B), und im `RECONNECT`-Fall hat Twitch selbst um den Neuaufbau gebeten. Jede Wartezeit hier ist reine Zähllücke. TwitchLibs eigene 1,5 s (`DisconnectWait`) waren Vorsicht vor dem Wiederverwenden desselben Objekts — bei einem frischen Objekt gegenstandslos. |
| Basis nach Fehlschlag | **2 s**, verdoppelnd | Wie `SevenTvBackoffPolicy` (`BaseDelay = 2 s`) — dasselbe Muster, damit ein Leser im Repo nur eine Kurve kennen muss. |
| Deckel | **30 s** | Der Deckel ist die **maximale zusätzliche Zähllücke nach Ende eines Twitch-Ausfalls**: kommt Twitch zurück, merken wir es spätestens nach einem Deckel-Intervall plus Versuchsdauer. TwitchLib fuhr 30 s, und das hat auf Twitch nie Ärger gemacht. `SevenTvBackoffPolicy` nimmt 60 s — dort kostet ein 7TV-Ausfall keine Zählung (der REST-Resync deckt ihn). Bewusst abweichend. |
| Exponent-Deckel | 5 (2 → 4 → 8 → 16 → 30) | Streak-Zähler wächst nicht unbegrenzt (wie `MaxFailureStreak` in 7TV). |
| Jitter | ± 20 %, **am Deckel gekappt** | Wie 7TV; ein zweiter Worker (Harness, künftiges Sharding) soll nicht im Gleichschritt wiederkommen. Gekappt heißt: der Jitter wird auf den Rohwert angewandt und das Ergebnis auf 30 s begrenzt — 30 s ist ein echter Deckel, nicht 36 s. |
| Versuchsobergrenze | **keine** | S2-1-Lehre: ein still erschöpftes Budget hat die Verbindung am 2026-07-26 für > 45 min gekostet. |
| **Reset-Bedingung** | Streak → 0 **beim Handshake (`004`)** | Der Streak zählt **Fehlversuche**, nichts anderes. Eine erfolgreich aufgebaute Sitzung, die kurz danach wieder abreißt, ist kein Fehlversuch — sie eskaliert den Fehlversuchs-Backoff nicht. Die erste Fassung dieses Papiers hatte hier eine 60-s-Stabilitätsregel (Sitzungen < 60 s zählten wie Fehlschläge); die Gegenrede hat vorgerechnet, dass anhaltendes Flapping damit 34–47 s Lücke je Ereignis kostete statt heute ~14 s, und die Regel stützte sich auf die Abuse-Hypothese vom 2026-07-27, die das Repo selbst am 2026-07-30 durch die Zehn-Versuche-Falle ersetzt hat (`TwitchChatManager.cs:573-581`). Gestrichen (Abschnitt 10, G1). |
| **Flap-Dämpfung** (getrennt vom Backoff) | nach **3** aufeinanderfolgenden Sitzungen < 60 s: fester Boden **5 s** vor dem nächsten Aufbau; aufgehoben, sobald eine Sitzung ≥ 60 s hält | Ohne jede Dämpfung wäre eine Verbindung, die Twitch unmittelbar nach dem Handshake wieder trennt, eine Schleife mit ~2-s-Periode — ≈ 1.800 Aufbauten und Log-Zeilen pro Stunde. Fünf Sekunden begrenzen das auf ≤ 720/h und kosten im Flapping-Fall **5 s** je Ereignis statt bis zu 36 s. Kein Exponent, kein Wachstum: die Dämpfung ist eine Schranke gegen eine Schleife, kein zweiter Backoff. Die Zahl ist Vorsicht, nicht Kalibrierung (7.8). |

**Zielgrößen** (zwei, getrennt — die erste Fassung hatte eine, und sie maß das Falsche):

| | Was gemessen wird | Ziel | Rolle |
|---|---|---|---|
| **SLO-1** | Verlust (Zeitstempel des Signals) → `004` des neuen Clients | Median ≤ 3 s, p95 ≤ 5 s; harte Schranke 25 s je Versuch (15 s Connect + 10 s Handshake) | **diagnostisch** — sagt, ob eine Verfehlung von SLO-2 im Aufbau oder im Rejoin liegt |
| **SLO-2** | Verlust → letzte Bestätigung (`OnJoinedChannel`) aller N zum Zeitpunkt des Verlusts gewünschten Kanäle, **und** K = 0 offene am Ende der Runde | ≤ **6 s + 0,6 s × (N − 1)**: N = 13 → 13,2 s; N = 20 → 17,4 s | **Abbruchkriterium** — lokal ein Merge-Blocker, wenn einer von fünf Läufen ihn reißt (6.3); auf Prod ein Rollback- bzw. Cooldown-Auslöser, wenn p95 über 24 h ihn reißt oder ein Wiederaufbau mit K > 0 endet (6.6) |

Eine Verfehlung ist ein Befund, **kein Anlass, die Zahl zu korrigieren**. Wer eine Zielgröße
ändert, tut das vor dem nächsten Lauf mit Begründung — wie T11/T12 an #69 —, nicht danach.

Zeitleiste im Normalfall (ein Verlust, erster Versuch gelingt; **plausibel**, Messung in
Abschnitt 6 — der Unterschied zwischen „plausibel" und der harten Schranke ist genau der Grund,
warum SLO-1 eine Verteilung nennt und nicht einen Wert):

| Schritt | Zeit ab Verlust t₀ |
|---|---|
| `OnDisconnected` (Fall A) / (Fall B) | ≈ 0,6 s / ≈ 0,4 s |
| neuer Client geöffnet (TLS + WS) | ≈ 1,0–1,5 s |
| `004` empfangen, `Connected` | **≈ 1,5–2,5 s** (SLO-1) |
| erster JOIN gesendet / bestätigt | ≈ 2–3 s / ≈ 2,2–3,5 s |
| JOIN für Kanal k (0-basiert) bestätigt | ≈ 2,2–3,5 s + 0,6 s × k |
| letzter von 13 / 20 Kanälen bestätigt | ≈ 10–11 s / ≈ 14–15,5 s (SLO-2: ≤ 13,2 / ≤ 17,4 s) |

Zum Vergleich heute, seit #114, pro Ereignis (Fall B): Lücke 1 ≈ 1,5 s + Aufbau + 0,2 s × k;
dann bis zu 60 s Doppelschleife; dann Lücke 2 ≈ 1,9 s + Aufbau + 0,6 s × k. Für k = 12 also
≈ 4 s + ≈ 10 s = **≈ 14 s** statt **≈ 10 s**, und zwei Zeitfenster statt einem.

### 3.7 Was aus `ReconnectPolicy`, `_clientSpent` und dem `IsClientSpent`-Zweig wird

- **`ReconnectPolicy` wird zurückgezogen**, nicht umgebaut. Ihre drei Regeln stammen aus Ausfällen,
  deren Mechanismen es im neuen Modell nicht mehr gibt: (a) Fehlerstreak ≥ 3 → Recreate (2026-07-26,
  festgefahrenes Objekt) — jeder Versuch ist ein Recreate; (b) Open hängt ≥ 10 min → Recreate —
  ein Versuch ist auf ~25 s beschränkt; (c) `_clientSpent` → Recreate (#114) — `OnReconnected` kann
  nicht mehr feuern (Abschnitt 3.1). Die Herkunft der Regeln gehört in den DECISIONS-Eintrag, damit
  der nächste Ausfall nicht dieselben Regeln neu erfindet. Ihre Tests
  (`ReconnectPolicyTests`, 13 Fälle) entfallen mit ihr.
- **`ReconnectAction.Reconnect` entfällt ersatzlos** — nicht „neue Bedeutung", sondern gestrichen,
  weil der zugrunde liegende Aufruf mit `NoReconnectionPolicy` beweisbar scheitert.
- **`IsClientSpent`** verschwindet aus `ITwitchChatManager`, `TwitchWatchdogPolicy.Decide` verliert
  den Parameter `clientSpent` samt erstem Zweig (`TwitchWatchdogPolicy.cs:63-68` — im Auftrag als
  `:213-218` zitiert, das waren Zeilen einer zusammengesetzten Ausgabe) und die beiden
  `ClientSpent…`-Tests.
- **`OnReconnected` bleibt als Stolperdraht verdrahtet:** feuert es doch (Bibliotheks-Upgrade,
  Fehlannahme), Error-Log „darf mit NoReconnectionPolicy nicht auftreten" plus Signal mit
  **10-s-Boden** — der Boden, weil TwitchLib in diesem Fall seinen Schwall bereits gesendet hätte
  und unser Rejoin nicht ins selbe 10-s-Fenster fallen soll (E4-Gedanke aus dem #114-Eintrag,
  reduziert auf den Fall, den es eigentlich nicht mehr gibt).
- **`OnConnectionError` („Fatal network error.") wird zur Normalzeile**: Es kommt mit
  `NoReconnectionPolicy` **einmal pro Verlust** (Abschnitt 3.2). Der Handler zählt keinen Streak
  mehr, loggt auf Information und koalesziert ins Signal. Wer nach dem Umbau die Zeile im Log
  sieht, sieht keinen Fehler mehr — das gehört in die Doku-Zeile des Handlers.
- **`TwitchWatchdogPolicy` behält beide übrigen Zweige** (Disconnected mit 1-min-Cooldown; Stale
  15 min mit 15-min-Cooldown), bekommt aber den Eingang `reconnectInFlight`: läuft die Schleife
  schon, ist jede Tick-Entscheidung ein No-op. Der Disconnected-Zweig wird damit zum Backstop, der
  im Normalbetrieb nie feuert (Abschnitt 8, R1).

### 3.8 Klassen, die das voraussichtlich braucht (Regel 5 und 11)

Nichts davon ausprogrammiert; Namen sind Vorschläge.

| Klasse | Art | Test |
|---|---|---|
| `TwitchReconnectBackoffPolicy` (neu) | rein, uhr- und TwitchLib-frei; Eingang: Sitzungsergebnis (Handshake ja/nein, Sitzungsdauer, Grund), Ausgang: nächste Verzögerung; injizierbarer Jitter wie `SevenTvBackoffPolicy` | `tests/EmotePurge.Worker.Tests`: erster Delay 0; Verdopplung nur bei Fehlversuchen; Deckel 30 s **inklusive** Jitter; Exponent-Deckel; Handshake setzt den Streak zurück; kurze Sitzung eskaliert den Streak **nicht**; Flap-Boden 5 s ab der dritten kurzen Sitzung, aufgehoben nach einer Sitzung ≥ 60 s; Handshake-Timeout zählt als Fehlversuch |
| `TwitchWatchdogPolicy` (geändert) | rein wie heute, ohne `clientSpent`, mit `reconnectInFlight` | bestehende Fälle minus zwei, plus „in flight → nichts" für beide Zweige |
| `ReconnectPolicy` (entfällt) | — | Tests entfallen |
| `TwitchChatManager` (Transport) | erhält: Signal-Quelle (koaleszierend, mit Grund), `ReconnectOnceAsync()` = Schritte 1–5, `RejoinDesiredChannelsAsync` öffentlich für die Schleife; verliert: `ForceReconnectAsync`-Entscheidung, `ReconnectClientAsync`, Open-Beobachtung | **keine Fake-Tests** (Regel 11/16: live verifiziert, Abschnitt 6) — Konsequenz für die Sonar-Schwelle in Abschnitt 8, R8 |
| `TwitchConnectionWatchdog` (erweitert) | `BackgroundService`, wartet auf Signal **oder** Tick; führt die Schleife aus Abschnitt 3.3 | keine Fake-Tests; Verhalten ist Ein-Zeilen-Delegation an die beiden Policies |
| `ITwitchChatManager` (Vertrag) | `IsClientSpent` weg; `ForceReconnectAsync` wird zu `RequestReconnect(reason)` (nur Signal); die Schleife spricht mit dem Manager über genau drei Mitglieder — `WaitForReconnectRequestAsync(ct)`, `ReconnectOnceAsync()` → Sitzungsergebnis, `RejoinDesiredChannelsAsync()` (Abschnitt 9.3); `ConnectAsync()` = erster Versuch, awaitet ≤ ~25 s, bei Fehlschlag Signal statt Hintergrundschleife | Vertragsänderung → DECISIONS-Eintrag |

Verworfene Alternative zur Schleifen-Trägerin: eine eigene Schleife in `TwitchChatManager` (Task ab
`Initialize()`). Verworfen, weil der Manager kein Hosted Service ist und das Shutdown-Token nicht
kennt — der Watchdog hat es, tickt ohnehin, und ruft heute schon `ForceReconnectAsync`. Zweite
Alternative, ein neuer Hosted Service `TwitchConnectionSupervisor` neben dem Watchdog: sauberer
getrennt, aber zwei Dienste, die denselben Manager zum Wiederaufbau bewegen — ein Lock, zwei
Auslöser, dieselbe Doppelung, die dieses Papier abschaffen will.

---

## 4. Grenzfälle

**4.1 Twitch über Stunden weg.** Versuche bei 0, 2, 4, 8, 16, 30, 30, … s (± 20 %), jeder ≤ ~25 s
lang: rund **80 Versuche pro Stunde**, eine Warning-Zeile je Versuch mit Streak-Nummer. Heute: TwitchLibs
30-s-Schleife plus alle 10 min ein Recreate durch den `Wait`-Zweig — vergleichbare Rate. Health:
`IsConnected = false` → `GET /api/health` 503 → Kuma alarmiert, wie heute. Kein Zählverlust über den
Ausfall hinaus; nach Rückkehr ≤ 30 s + Versuchsdauer bis zur Verbindung (Abschnitt 3.6, Deckel).

**4.2 Verbindung steht, liefert aber nichts (stille Verbindung, Fall C).** Unverändert: 15 min ohne
Frame (Twitchs ~5-min-PING eingerechnet) → Watchdog-Tick → Signal → Recreate. Der 15-min-Cooldown des
Stale-Zweigs bleibt (2026-07-27-Lehre). Das ist heute die längste Lücke und bleibt es; dieses Papier
verkürzt sie nicht — die Schwelle ist eine eigene Entscheidung (2026-08-03) und gehört nicht hierher.

**4.3 Wiederaufbau schlägt wiederholt fehl, Twitch ist aber erreichbar** (DNS-Flapping, TLS-Fehler,
Handshake ohne `004`). Jeder Fehlschlag eskaliert bis 30 s, ohne Obergrenze. Sichtbar: Warning je
Versuch mit Grund (`ConnectAsync` false / Exception / Handshake-Timeout), TwitchLib-Trace „Client
couldn't establish a connection". Kein Sonderpfad — die 2026-07-26-Falle (erschöpftes Budget) kann
nicht mehr eintreten, weil jeder Versuch ein neues Objekt mit frischer Policy ist.

**4.4 Wiederaufbau während eines laufenden Joins.** `TryJoinAsync` hält `_joinGate` und sendet an
`_client`. Wird `_client` zwischendrin ersetzt, geht der JOIN an den neuen Client (verbunden: fein;
nicht verbunden: TwitchLib wirft, wir loggen Warning, der Kanal bleibt unbestätigt in
`_desiredChannels` und wird vom Rejoin der Sequenz erfasst). Wird die Verbindung **während** der
Rejoin-Runde verloren, bricht die Runde beim nächsten `_isConnected == false` ab; der neue Versuch
rejoint aus `_desiredChannels` neu. Kein Kanal geht verloren, weil Absicht und Bestätigung getrennt
geführt werden (Kommentar an `_desiredChannels`, `TwitchChatManager.cs:41-44`).

**4.5 Redis-Kommando trifft während eines Wiederaufbaus ein.** `JOIN:` → `JoinChannelAsync` trägt
die Absicht ein, `TryJoinAsync` sieht `!_isConnected` → „Join aufgeschoben" (Warning heute; sollte
Information werden, weil es im neuen Modell ein erwarteter Zustand ist) → der Rejoin der laufenden
Sequenz nimmt den Kanal mit, sofern er die Absicht **vor** dem Snapshot (`Keys.ToArray()`) sieht;
sonst greift `EnsureJoinedAsync` ≤ 60 s später (unverändertes Netz). `LEAVE:` → Absicht wird
entfernt, PART wird übersprungen; der neue Client joint ihn nie. Ein `LEAVE`, das **nach** dem
Snapshot (`Keys.ToArray()`), aber **vor** dem JOIN des Kanals eintrifft, würde ohne Gegenmaßnahme zu
einem ungewollten Join führen — und der bliebe, bis der nächste Wiederaufbau ihn beseitigt, was Tage
dauern kann; solange belegt der Kanal einen der 100 Chatroom-Plätze und wird von niemandem
verlassen, weil `_desiredChannels` ihn nicht mehr kennt. Die erste Fassung hatte das als
„Restlücke, kein Handlungsbedarf" abgetan; die Gegenrede hat zu Recht widersprochen (Abschnitt 10,
G5). Vertrag: `TryJoinAsync` prüft **nach** dem Erwerb des `_joinGate`, unmittelbar vor dem Senden,
ob der Kanal noch gewünscht ist (Abschnitt 3.5). Der Snapshot bleibt (die Runde muss über eine
stabile Liste laufen), aber jeder einzelne JOIN wird gegen den aktuellen Wunschzustand entschieden.
Was bleibt: ein `LEAVE`, das zwischen dieser Prüfung und dem Eintreffen des JOIN bei Twitch liegt
(Millisekunden) — dann folgt der PART von `LeaveChannelAsync` selbst, weil `_isConnected` wahr ist.

**4.6 Prozess-Shutdown — in jedem Zustand der Sequenz, nicht nur im Backoff.** Die erste Fassung
hatte nur das Backoff-Warten als abbrechbar beschrieben; die Gegenrede hat die Lücke benannt
(Abschnitt 10, G3): ein Connect kann 25 s dauern, ein Rejoin bei 20 Kanälen ≥ 11,4 s, und beides
lief ohne Token. Das kollidiert mit #122 — und zwar in beide Richtungen:

- Hosted Services stoppen sequenziell in umgekehrter Registrierungsreihenfolge
  (`WorkerServiceRegistration.cs:54-62`; `ServicesStopConcurrently` ist nicht gesetzt). Der Watchdog
  ist als fünfter registriert und stoppt damit **vor** `UsageFlushWorker` (zweiter). Jede Sekunde,
  die sein `StopAsync` braucht, fehlt dem Abschluss-Flush.
- Docker killt nach `stop_grace_period`, Default 10 s (#122). Ein Watchdog, der 25 s auf einen
  Connect wartet, bedeutet: der Flush kommt **nie** dran — bis zu 30 s bereits gezählter Nutzung
  gehen verloren, genau die Signatur des offenen Verdachts aus #97.
- Das ist **heute schon so**: `CheckOnceAsync` awaitet `ForceReconnectAsync` → `RecreateClientAsync`
  → `OpenAsync` bis zu 30 s ohne Token (`TwitchChatManager.cs:138-164`). Das Konzept erfindet das
  Problem nicht, aber es hätte es ohne die Zusage aus 3.4 verlängert (Connect + Rejoin statt nur
  Connect).

Vertrag: Die Shutdown-Zusage aus 3.4 — `TwitchConnectionWatchdog.StopAsync` kehrt ≤ 1 s nach dem
Token zurück, in jedem Zustand (Backoff, Connect, Handshake, Rejoin). Ein laufender Connect wird
verlassen und der Client im Hintergrund verworfen; eine laufende Rejoin-Runde bricht ab (die
restlichen Kanäle sind egal, der Prozess stirbt). Nachweis in 6.3, S6, in allen drei Zuständen.
**Und** #122 gehört in denselben Deploy: `stop_grace_period ≥ 30 s`. Ohne die Zusage nützt das
Grace-Budget wenig (25 + 12 s verbrauchten es vor dem Flush); ohne das Grace-Budget nützt die
Zusage dem Flush nichts, wenn die übrigen sieben Dienste die 10 s aufbrauchen. Beides zusammen macht
den Abschluss-Flush erst planbar.

**4.7 Zwei Signale in kurzer Folge** (Fall A liefert bis zu zwei `OnDisconnected` plus ein
`OnConnectionError` binnen 2,5 s). Koaleszenz: ein Signal-Slot mit Kapazität 1, neue Signale
überschreiben nur den Grund. Ergebnis: ein Wiederaufbau, drei Logzeilen — die Logzeilen sind
gewollt, sie sind die Beobachtung aus Abschnitt 6.

**4.8 Boot.** `Worker.ExecuteAsync` ruft `ConnectAsync()` vor der Boot-Recovery (`Worker.cs:26`).
Neu: `ConnectAsync()` = ein Versuch, awaitet ≤ ~25 s. Gelingt er, laufen die Boot-Recovery-Joins
wie heute direkt; scheitert er, geht das Signal an die Schleife, die Joins werden aufgeschoben und
vom Rejoin des ersten erfolgreichen Versuchs nachgeholt. Verhalten nach außen wie heute; die
Kommentare in `Worker.cs:23-25` („retries indefinitely in the background") werden falsch und müssen
mit.

---

## 5. Auswirkung auf die Zählung

### 5.1 Pro Ereignis, im Vergleich

| Szenario | Heute (seit #114) | Neu | Bewertung |
|---|---|---|---|
| Twitch `RECONNECT` (Fall B), N = 13 | Lücke 1 ≈ 4 s (Schwall), dann ≤ 60 s Doppelschleife mit Spleiß-Risiko, dann Lücke 2 ≈ 10 s | eine Lücke ≈ 10–11 s | **besser**: ~4 s weniger, kein Spleiß-Fenster |
| Socket-Abbruch (Fall A), N = 13 | wie oben | wie oben | **besser** |
| dasselbe bei N = 20 / 30 | Lücke 1 ≈ 4–6 s mit Schwall über der 20/10-s-Grenze: zufällige Kanäle bis 60 s stumm (wenn Twitch durchsetzt); dann Lücke 2 ≈ 14 s / 20 s | ≈ 15 s / 21 s, kein Schwall | **besser** und **kalkulierbar**: die Lücke wächst linear mit N, statt an einer Grenze zu kippen |
| stille Verbindung (Fall C) | 15 min + Recreate | 15 min + Recreate | **gleich** |
| Twitch-Ausfall, Rückkehr nach T | ≤ 30 s (TwitchLib) bzw. bis 10 min (`Wait`-Zweig) | ≤ 30 s ± 20 % + Versuch | **gleich bis besser** |
| flappende Verbindung (3 Verluste in 2 min) | 3 × (Lücke 1 + Lücke 2) ≈ 3 × 14 s | Verluste 1 und 2 wie im Normalfall (≈ 10 s), ab dem dritten +5 s Flap-Boden | **besser** bei den ersten zwei, **≈ gleich** ab dem dritten (15 s statt 14 s). Die erste Fassung hätte hier 34–47 s je Ereignis gekostet (Abschnitt 10, G1) |
| Twitch trennt unmittelbar nach jedem Handshake (Dauer-Flapping) | TwitchLib: 1,5 s je Runde, kein Wachstum; dazu je Runde ein Recreate ≤ 60 s später | 5 s je Runde ab der dritten (Flap-Boden) | **gleich bis besser**: begrenzte Rate, keine Doppelschleife |
| Twitch lehnt schnelle Neuverbindung ab (Abuse-Erkennung) | TwitchLib 1,5 s, dann bei Fehlschlag 3 s → 30 s | 0 s, dann bei Fehlschlag 2 s → 30 s | **offen** — R3 in Abschnitt 8; der Unterschied liegt in 1,5 s beim ersten Versuch |

Wo es **schlechter** wird, sind es Sekunden im Flapping-Fall; wo es besser wird, sind es Sekunden pro
Ereignis **und** ein ganzes Risiko (zufällige Kanäle für bis zu 60 s, Spleiße). Der Zählgewinn in
absoluten Zahlen ist klein — zehn Sekunden Chat je Reconnect-Ereignis — und das Papier behauptet
nichts anderes. Die Begründung des Umbaus ist die Risikoseite, nicht die Sekunden.

### 5.2 Wie oft passiert das überhaupt?

**Offen.** Die Ereignisrate auf Prod ist nicht gemessen; #117 liefert ab dem 2026-09-09 die erste
Zahl (Recreates pro Stunde über 24 h, Abbruchgrenze 2/h). Erst mit ihr lässt sich die
Gesamtwirkung beziffern: Rate × (Lücke heute − Lücke neu) × mittlere Nachrichtenrate. Das Papier
setzt keine Zahl ein, die es nicht hat.

### 5.3 Was sich nicht ändert

Zählregel, Klassifikation (`SharedChatRule`, `BotChatterDetector`), Matching, Flush-SQL, Harness,
`AlgorithmVersion`: unberührt. Der Sentinel `IrcLineSpliceRule` bleibt als Negativkontrolle (er
soll nach dem Umbau strukturell nie mehr anschlagen — Abschnitt 6.4).

### 5.4 Ist das eine „zählrelevante Änderung" im Sinne von #118?

Zwei Fragen, zwei Antworten:

- **Setzt sie die Uhr zurück?** Nein. #118 nennt als Rücksetzer: Rollback des Worker-Images,
  Änderung der Zählregel, neuer Stichtag. Nichts davon trifft zu; kein `AlgorithmVersion`-Bump,
  keine Migration.
- **Fällt sie unter Dauerauflage 3 („keine zählrelevante Codeänderung ohne Not")?** Ja — und
  zwar aus einem Grund, der über den Wortlaut hinausgeht: Sie ändert die **Erfassungsdeckung**, nicht
  die Regel. Σ|Log − Live| / ΣLive ist die Gate-Kennzahl; die Live-Seite verliert mit dem Umbau je
  Reconnect weniger. Ein Deploy mitten im Fenster machte die beiden Fensterhälften unvergleichbar
  — der Fehlerterm wäre nicht mehr stationär —, ohne dass irgendein Gate das anzeigt. Die Änderung
  kann ein Gate **nicht vortäuschen** (sie senkt die Abweichung nur), aber sie würde die Messung
  entwerten, die gerade prüfen soll, wie groß die Abweichung ohne sie ist.

**Folge für den Zeitpunkt:** Bauen, lokal verifizieren, mergen ist im Fenster frei (Mergen zählt
nicht, nur der Stack-Update — #118, Abschnitt 4). Deploy als **erster Deploy nach dem bindenden
Lauf** (ab 2026-10-08), gebündelt mit #122 (`stop_grace_period`) und allem, was bis dahin sonst
auf dem Stapel liegt — jeder Deploy kostet einen Worker-Neustart, also bündeln. Vor dem Deploy die
#117-Zahlen als Baseline sichern; nach dem Deploy dieselbe Zählung wiederholen (Abschnitt 6.6).

---

## 6. Wie ein lokaler Lauf beweist, dass es funktioniert (Regel 16)

Prinzip dieser Fassung: **Jede Behauptung des Papiers bekommt eine Beobachtung, die sie widerlegen
könnte** (6.2), und jede Beobachtung eine Einstufung — lokal beweisbar, lokal nur teilweise, nur
auf Prod. Was lokal nicht beweisbar ist, steht als solches in 6.5, mit dem Beleg, wie er auf Prod
aussähe. Die erste Fassung hatte an drei Stellen Belege, die grün geworden wären, ohne etwas zu
zeigen (Abschnitt 10, G4). Der schwerste: Sie nahm die **Abwesenheit** von TwitchLibs „Joining
channel"-Zeilen als Beweis für den verschwundenen Schwall — bei einer Log-Ebene, die diese Zeilen
ohnehin unterdrückt hätte. Ein grüner Lauf hätte den Defekt beglaubigt; dasselbe Muster hat den
Audit-Harness am 2026-09-07 getroffen (Projektnotiz „Selbstprüfung am falschen Ort").

### 6.1 Aufbau

- Aus dem **Haupt-Checkout**, nicht aus einem Worktree (`.env` ist gitignored und fehlt dort;
  Compose aus dem Worktree reißt den Stack ab — Projektnotiz vom 2026-09-07). Compose-Projekt
  `emote-purge-dev`, Container `emotepurge-dev-worker`, Netz `emote-purge-dev_emotepurge-network`
  (`docker-compose.yml:4,87,179`).
- `docker compose up -d --build worker` (Regel 15) — `up` allein fährt ein altes Image.
- **Log-Ebenen per Env am Containerstart** (nicht zur Laufzeit umschaltbar):
  - `Logging__LogLevel__TwitchLib.Client.TwitchClient=Information` — **nicht** `Warning`. Am
    Binärstand belegt (`TwitchLib.Client.Extensions.LogExtensions`): „Joining channel: {channel}",
    „Leaving channel", „Connecting Twitch Chat Client…" und „Reconnecting to Twitch" liegen auf
    **Information**; „Received: {line}" liegt auf Trace und bleibt aus. Heute steht der Wert auf
    `Error` (`appsettings.json:8`); ein Lauf ohne diese Anhebung kann über TwitchLibs JOINs
    **nichts** aussagen. **Kontrollfrage vor jedem Lauf:** erscheint beim ersten Join nach dem
    Containerstart „Joining channel: …"? Wenn nicht, ist die Ebene nicht wirksam, und der Lauf
    zählt nicht.
  - `Logging__LogLevel__TwitchLib.Communication=Trace` — **nur in Lauf A** (s. u.):
    `ClientBase.RaiseMessage` traced jede empfangene Zeile, ein lauter Kanal macht daraus eine Flut.
- **Zwei Läufe**, weil sich die Anforderungen widersprechen:
  - **Lauf A — Struktur.** Communication auf Trace, **nur stille Testkanäle** (≥ 20 per SQL in
    `Channels` mit `IsBotActive = true`, Rezept in `docs/Review-2026-07-29-Umsetzung.md`). Beweist:
    ein Versuch je Aufbau, eine Lese-Schleife je Client, Backoff-Kurve, Shutdown-Zusage.
  - **Lauf B — Zählung und Timing.** Communication auf Warning, dieselben Testkanäle plus
    **mindestens ein lauter echter Kanal**. Beweist: SLO-1, SLO-2, Bestätigungen, Sentinel, Flush.
- **t₀ ist der Zeitstempel des Kill-Kommandos auf dem Host** (`date +%T.%N` unmittelbar davor);
  Container und Host teilen die Uhr. Alle Abstände werden gegen t₀ gerechnet, nicht gegen die erste
  Logzeile — sonst misst man die Latenz ab dem Signal statt ab dem Verlust.
- Vor jedem Szenario den `EnsureJoinedAsync`-Minutentakt abwarten, bis alle Kanäle bestätigt sind
  (Roster im Admin-Bereich); N ist die Zahl der bestätigten Kanäle **zum Zeitpunkt t₀**.
- **`:5151` beachten:** Wer parallel E2E fahren will, muss die lokale Api beenden — betrifft
  diesen Lauf nur, wenn beides gleichzeitig läuft.

### 6.2 Beweistabelle

| Behauptung (Abschnitt) | Widerlegt durch | Szenario | Beweisbar |
|---|---|---|---|
| Signal ≤ 1 s nach Verlust (3.2) | erste „TwitchClient getrennt"-Zeile > 1 s nach t₀, oder keine vor dem nächsten Watchdog-Tick | S1 | lokal für Fall A; Fall B **nur Prod** (6.5) |
| Genau ein Verbindungsversuch je Aufbau, keine TwitchLib-eigene Schleife (3.1) | Communication-Trace: zwei „try to connect" ohne ein „Wiederaufbau #n" dazwischen; oder „TwitchClient reconnected" | S1, S3 (Lauf A) | lokal |
| Eine Lese-Schleife je Client (2.2, 3.2) | zwei „ListenTaskActionAsync"-Trace-Zeilen zwischen zwei „Wiederaufbau"-Zeilen | S1 (Lauf A) | lokal für Fall A; für Fall B — den Pfad, der die zweite Schleife erzeugt hat — **nur Prod, und dort nur indirekt** (6.5) |
| Kein Schwall: TwitchLib sendet nur, was wir anstoßen, im 600-ms-Raster (3.5) | **positiv:** je Ereignis genau N „Joining channel"-Zeilen mit Abständen 600 ms ± 100 ms; **widerlegt** durch mehr als N Zeilen, einen Abstand < 400 ms, oder N Zeilen binnen < 0,4 × N s | S1 (Lauf B) | lokal |
| SLO-1 (3.6) | Median > 3 s oder p95 > 5 s über fünf Läufe (t₀ → „TwitchClient verbunden") | S1 × 5 | lokal von der Wohn-IP; Prod-Latenz kann abweichen (6.5) |
| SLO-2 (3.6) | t₀ → letzte „Channel … gejoint" > 6 s + 0,6 s × (N − 1) in **einem** Lauf; oder „K offen" > 0 | S1 × 5 | lokal |
| Rejoin blockiert die Lese-Schleife nicht mehr (3.5) | „Twitch hat den Join für … nicht bestätigt" während einer Runde; oder die „gejoint"-Zeilen kommen als Block nach Ende der Runde statt je ~600 ms | S1 (Lauf B) | lokal |
| Backoff 0 / 2 / 4 / 8 / 16 / 30 s, Deckel echt (3.6) | Abstände der „Wiederaufbau #n"-Zeilen weichen > 20 % ab; ein Abstand > 30 s + Versuchsdauer | S3-REJECT | lokal |
| Fehlversuch-Schranke 25 s (3.4) | Abstand „Wiederaufbau #n" → zugehörige Fehlschlag-Warning > 26 s | S3-DROP | lokal |
| Handshake setzt den Streak zurück; Flap-Boden 5 s ab der 3. kurzen Sitzung (3.6) | Verzögerungen in S4 ≠ 0 / 0 / 5 s; nach einer Sitzung ≥ 60 s ≠ 0 s | S4 | lokal |
| Watchdog-Backstop feuert nie (3.3, R1) | „TwitchClient meldet sich als getrennt" aus dem Tick | alle | lokal und Prod |
| `LEAVE` während der Runde erzeugt keinen JOIN (4.5) | „Joining channel: X" oder „Channel X gejoint" nach „Redis-Kommando: verlasse X" | S5 | lokal |
| Shutdown-Zusage ≤ 1 s in jedem Zustand (3.4, 4.6) | Stoppdauer ≥ 10 s; „try to connect" oder „Wiederaufbau" nach „Application is shutting down"; „Usage-Stat-Flush fehlgeschlagen" beim Stop | S6 × 3 | lokal |
| Stille Verbindung wird weiter nach 15 min erkannt (4.2) | kein Watchdog-Reconnect nach 16 min Schnitt | S7 | lokal |
| „Fatal network error." genau einmal je Verlust (3.7) | Zahl der Zeilen ≠ Zahl der Verluste | S1, S4 | lokal |
| Sentinel 0 (5.3) | „Gespleißte IRC-Zeile erkannt" > 0 | Lauf B, ≥ 2 h lauter Kanal | lokal, aber **schwacher** Beleg — heute ebenfalls ≈ 0; die strukturelle Aussage trägt die Schleifen-Zählung oben |
| Zähleffekt je Ereignis (5.1) | — | — | **nur Prod**: Harness-Vergleich vor/nach (6.5) |
| Twitchs Reaktion auf schnelle Wiederverbindung (R3) | — | — | **nur Prod** (6.5) |

### 6.3 Szenarien

**S1 — Socket-Abbruch (Fall A), der schnelle Pfad.** Die bestehende Verbindung hart beenden, so
dass `ReceiveAsync` einen Fehler sieht statt zu hängen. Erster Weg: `ss --kill` im Netz-Namespace
des Containers —
`sudo nsenter -t $(docker inspect -f '{{.State.Pid}}' emotepurge-dev-worker) -n ss -K dport = :443`
— braucht `CAP_NET_ADMIN` und den Kernel-Schalter `CONFIG_INET_DIAG_DESTROY` (prüfen:
`grep INET_DIAG_DESTROY /boot/config-$(uname -r)`; **offen**, ob der Devbox-Kernel ihn hat). Dann ist
t₀ der Zeitstempel des Kommandos. Zweiter Weg, wenn nicht: auf dem Host eine Regel
`iptables -I DOCKER-USER -s <container-ip> -p tcp --dport 443 -j REJECT --reject-with tcp-reset`,
die beim **nächsten gesendeten Paket** greift (PONG auf Twitchs ~5-min-PING oder ein JOIN) —
t₀ ist dann der Zeitstempel von TwitchLibs Fehlerzeile, also aus dem Log statt vom Host: schwächer,
und so zu vermerken. Regel nach dem Signal sofort entfernen.
Beobachtungen, in dieser Reihenfolge, jede mit ihrem Widerleger in 6.2:
1. „TwitchClient getrennt" ≤ 1 s nach t₀.
2. „Twitch-Verbindung verloren (…) — Wiederaufbau #1 in 0 s".
3. „Fatal network error." als Information — genau einmal.
4. „TwitchClient verbunden" — Δ zu t₀ ist der SLO-1-Wert dieses Laufs.
5. Genau N „Joining channel: …" (TwitchLib) und N „Channel … gejoint" (wir), erstere im
   600-ms-Raster, letztere je ~200 ms dahinter; keine „nicht bestätigt"-Zeile.
6. „Rejoin abgeschlossen: N gewünscht, N bestätigt, 0 offen, T s seit Verlust" — T ist der
   SLO-2-Wert dieses Laufs.
7. Lauf A: genau eine „ListenTaskActionAsync"-Zeile für den neuen Client.
Fünfmal, ≥ 2 min Abstand. Ergebnis ist eine Tabelle mit fünf Zeilen: t_Signal, t_004,
t_letzte Bestätigung, K, Zahl der „Joining channel"-Zeilen, kleinster und größter Abstand.

**S2 — Twitch `RECONNECT` (Fall B), der Inline-Pfad.** Nicht auf Zuruf provozierbar. TwitchLib
bietet `TwitchClient.OnReadLineTestAsync(rawIrc)`; eine Zeile `:tmi.twitch.tv RECONNECT` löst
`ReconnectAsync()` aus. Das beweist die **Policy-Reaktion** — `OnDisconnected`, genau ein „Fatal
network error.", kein „TwitchClient reconnected", danach S1-Schritte 2–6 — aber **nicht den
Thread-Kontext**: der Aufruf läuft auf dem Thread des Aufrufers, nicht in der Lese-Schleife. Die
Eigenschaft, um die es in Fall B geht (unser Handler tut in der sterbenden Schleife nichts, E1), ist
eine Konstruktionseigenschaft und wird **per Code-Lesen abgenommen**, nicht per Lauf. Ein
Debug-Auslöser dafür (env-gated, nie in Prod aktiv) ist Implementierungssache (7.5). Ehrlich: Fall B
ist lokal nicht vollständig beweisbar — s. 6.5.

**S3 — Twitch über Minuten weg (Backoff-Kurve).** Die erste Fassung ließ den Traffic einer
**offenen** Verbindung droppen. Das ist Fall C: TwitchLib merkt nichts, bis der 15-min-Watchdog
feuert — fünf Minuten hätten die Backoff-Schleife nie erreicht (G4). Korrekt braucht es zwei Dinge:
(1) die bestehende Verbindung killen wie in S1 **und** (2) neue Verbindungen scheitern lassen. Zwei
Varianten, beide ≥ 3 min halten:
- **S3-REJECT:** `… -j REJECT --reject-with tcp-reset` auch für SYN → jeder Versuch scheitert in
  Millisekunden; die Kurve 0 / 2 / 4 / 8 / 16 / 30 / 30 s ist in ~1,5 min ablesbar.
- **S3-DROP:** `… -j DROP` → jeder Versuch läuft in TwitchLibs 15-s-Timeout; beweist die
  25-s-Schranke und dass die Schleife auch mit langsamen Fehlschlägen nicht hängt.
Beleg: Warning je Versuch mit Streak-Nummer und Grund; kein Abstand > 30 s + Versuchsdauer; **kein**
„Erzwinge Reconnect" aus dem Watchdog-Tick (Backstop); Stale-Zweig feuert nicht. Nach dem Entfernen
der Regel: „TwitchClient verbunden" ≤ 30 s + 25 s, dann S1-Schritte 5–6.

**S4 — Flapping (Reset und Dämpfung).** S1 dreimal binnen 90 s. Beleg: Verzögerungen **0 s, 0 s,
5 s** (der Streak setzt beim Handshake zurück; der Boden greift ab der dritten kurzen Sitzung);
danach 2 min Ruhe (eine Sitzung ≥ 60 s), vierter Kill → wieder 0 s. „Fatal network error." viermal.
Nebenbeobachtung für R3: schlägt in dieser Serie ein Aufbau fehl, ist das der einzige lokale Hinweis
auf ein Twitch-seitiges Verhalten — von der Wohn-IP.

**S5 — Redis-Kommandos.** Zwei Teile. (a) Während S3:
`docker compose exec redis redis-cli -a <REDIS_PASSWORD> PUBLISH channel:bot:commands JOIN:<testkanal>`
und ein `LEAVE:` für einen anderen Kanal — nach der Rückkehr ein JOIN für den ersten, keiner für den
zweiten. (b) **Während einer Rejoin-Runde** (R9): in S1 unmittelbar nach „TwitchClient verbunden" ein
`LEAVE:` für den Kanal, der in der „Rejoine …"-Reihenfolge eines früheren Laufs **zuletzt** kam —
Beleg: keine „Joining channel"-Zeile für ihn nach dem `LEAVE`, und er fehlt im Roster.

**S6 — Shutdown in drei Zuständen.** `time docker compose stop worker`, jeweils:
(a) im Backoff (während S3-REJECT); (b) im Connect (S3-DROP, Stop binnen 15 s nach einem
„Wiederaufbau #n"); (c) im Rejoin (S1 mit N ≥ 20, Stop binnen 5 s nach „verbunden"). Beleg je
Zustand: Wanduhrzeit des Stops **deutlich unter 10 s** (≥ 10 s heißt: Docker hat gekillt), nach
„Application is shutting down" keine „Wiederaufbau"- und keine „try to connect"-Zeile, keine
„Usage-Stat-Flush fehlgeschlagen"-Zeile, Exit-Code 0. Mit #122 (Grace ≥ 30 s) ist der Widerleger
nicht mehr die 10-s-Grenze, sondern die Stoppdauer des Watchdogs allein — sie steht als Δ zwischen
„Application is shutting down" und der nächsten Stopp-Zeile eines anderen Dienstes im Log.

**S7 — Stille Verbindung (Fall C), unverändert.** `docker network disconnect
emote-purge-dev_emotepurge-network emotepurge-dev-worker`, **> 15 min** halten (nicht > 5 min wie im
Ticket — Schwelle seit 2026-08-03, `TwitchWatchdogPolicy.cs:22`), dann `connect`. Erwartung:
TwitchLib meldet nichts, der Watchdog feuert bei ~15–16 min „Kein IRC-Frame seit …", danach
S1-Schritte 2–6. Dieser Schnitt trennt auch Postgres/Redis (am 2026-07-30 gewollt). Einmal reicht.

### 6.4 Zähler, die Lauf B begleiten

- `UsageStats`-Zeilen des lauten Kanals vor und nach jedem Kill: Flush läuft weiter (der
  30-s-Flush ist vom Verbindungszustand unabhängig); kein Tag mit Null.
- Indeterminate-Zähler und Sentinel-Summe je Flush: 0.
- „Wiederaufbauten seit Prozessstart" (`WorkerStats`, Log-only, 7.4): am Ende gleich der Zahl der
  provozierten Ereignisse, nicht mehr.

### 6.5 Was lokal nicht beweisbar ist — und wie der Beleg auf Prod aussähe

| Eigenschaft | Warum lokal nicht | Prod-Beleg |
|---|---|---|
| Fall B in der echten Lese-Schleife (3.2, E1) | kein Weg, Twitch zu einem `RECONNECT` zu bewegen; der Testhaken läuft auf einem anderen Thread | TwitchLib „Reconnecting to Twitch" (Information — setzt **7.10** voraus), unmittelbar gefolgt von „TwitchClient getrennt", „Wiederaufbau #1", „verbunden", ohne „TwitchClient reconnected". Die Ein-Schleifen-Aussage bleibt auch dort **indirekt** (kein Communication-Trace auf Prod): ihr einziger Zeuge ist der Sentinel, und der ist schwach. Das Papier behauptet Fall B deshalb aus dem Binärstand (3.2), nicht aus einer Messung |
| Twitchs Verhalten bei schnellen Wiederverbindungen (R3, 7.8) | nur von der Wohn-IP, ein Datenpunkt | Fehlschlag-Serien unmittelbar nach Wiederaufbauten in den 24-h-Zahlen (6.6) |
| SLO-1 unter Prod-Latenz | Devbox → Twitch ≠ VPS → Twitch | p50/p95 aus „Twitch-Verbindung steht nach … s" über 24 h |
| Zähleffekt (5.1) | keine Referenz für „was hätte gezählt werden sollen" | Harness-Vergleich Σ\|Log − Live\| / ΣLive vor und nach dem Deploy, gleicher Kanal, gleiche Tageszahl |
| Ereignisrate (5.2) | lokal provoziert, nicht natürlich | #117-Zahlen vor dem Deploy, dieselbe Zählung danach |

### 6.6 Auf Prod nach dem Deploy (Nutzer, kein Agent)

Dasselbe Rezept wie #117, mit den neuen Zeilen, über 24 h ab Containerstart: Anzahl „Wiederaufbau
#1" (Ereignisse), Anzahl „Wiederaufbau #≥ 2" (Fehlversuche), p50/p95 von „Twitch-Verbindung steht
nach … s" (SLO-1) und von „Rejoin abgeschlossen … T s" (SLO-2), Summe der „K offen"-Werte, 0 ×
Stolperdraht, 0 × Backstop, 0 × Sentinel. **Abbruchkriterium:** SLO-2-p95 gerissen, oder ein
Wiederaufbau mit K > 0, oder mehr als ~2 Ereignisse pro Stunde über mehrere Stunden (die
#117-Grenze) → Rollback bzw. Cooldown nachziehen. Baseline sind die #117-Zahlen **vor** dem Deploy.

---

## 7. Was dieses Papier ausdrücklich nicht entscheidet

**7.1 Der Quelltext-Widerspruch (1000-ms-Timer vs. 28 JOINs in 5 s).** Am Binärstand aufgelöst,
hier festgehalten, damit ihn niemand neu untersucht: `QueueingJoinCheckAsync` sendet **einen** JOIN
und startet den `_joinTimer` (1.000 ms). `Handle366` — die Bestätigung — setzt
`_currentlyJoiningChannels = false` und ruft `QueueingJoinCheckAsync` **sofort** wieder auf. Der
Timer-Handler `JoinChannelTimeout` rückt die Queue nur vor, wenn **kein** Join mehr auf Bestätigung
wartet (`if (_awaitingJoins.Any()) { … return; }`), und meldet nach > 5 s Fehlschläge. Die
Bestätigung treibt also die Kette, der Timer ist Fallback. Die Messung (Kadenz ≈ Round-Trip) stimmt;
die Lesung „hängt an einem 1000-ms-Timer" beschreibt den Fallback, nicht den Regelfall. **Belegt.**
Ob das die Entscheidung berührt: **nein** — sie hängt daran, *wer* das Timing besitzt, nicht daran,
ob TwitchLibs Kadenz 200 ms oder 1.000 ms ist. Beide liegen außerhalb unserer Drossel. Was die
Auflösung liefert, ist nur die Erklärung, warum 28 in 5 s überhaupt möglich waren. Was **offen**
bleibt, ist die Frage aus #68, ob Twitch die 20/10-s-Grenze auf anonyme Verbindungen überhaupt
durchsetzt — sie wird mit diesem Umbau irrelevant für den Rejoin-Pfad, nicht beantwortet.

**7.2 Muss `TwitchJoinBudgetChannels` bei 20 bleiben?** Nach dem Umbau gibt es keinen ungedrosselten
JOIN-Pfad mehr; die im Kommentar an der Konstante genannte Begründung entfällt. Was an ihre Stelle
tritt, sind andere Grenzen: Twitchs 100 gleichzeitige Chatrooms, die 7TV-EventAPI (500
Subscriptions ≈ 250 Kanäle je Verbindung), und neu die **lineare Rejoin-Dauer** (0,6 s × N je
Ereignis, bei 100 Kanälen also 60 s Lücke für den letzten). Ob und wohin die Zahl wandert, ist eine
eigene Entscheidung mit eigener Messung — **nicht hier**. Was hier gehört: der Kommentar an der
Konstante wird mit dem Umbau falsch und muss im selben Commit neu begründet werden, auch wenn die
Zahl bleibt.

**7.3 Benennung.** Ob `TwitchConnectionWatchdog` nach dem Umbau `TwitchConnectionSupervisor` heißen
sollte (er wacht nicht mehr nur, er baut auf). Reine Lesbarkeitsfrage, Regel 19 unberührt.

**7.4 Ein Health-Feld für Wiederaufbauten.** `WorkerHealthSnapshot` ist ein Api-seitig verdrahteter
Vertrag; der #114-Plan hat ein Feld bewusst verweigert. Für den Beweis genügt das Log plus ein
Log-only-Zähler in `WorkerStats`. Ob das Admin-Monitoring die Zahl zeigen soll, ist eine zweite
Vertragsänderung — offen.

**7.5 Ein Debug-Auslöser für den `RECONNECT`-Pfad.** Über `OnReadLineTestAsync` machbar (6.2, S2);
ob er als env-gated Kommando in den Worker gehört oder als Harness-Verb, ist offen. Ohne ihn ist
Fall B nur auf Prod im Nachgang belegbar.

**7.6 Die 600 ms selbst.** Bleiben. Ob 16,7 JOINs/10 s die richtige Reserve zu 20 sind oder ob
Twitch anonyme Verbindungen anders behandelt (#68), entscheidet eine Messung, nicht dieses Papier.

**7.7 Log-Ebene der Aufschub-Zeile.** „Join für … aufgeschoben" ist heute Warning; im neuen Modell
ist es ein erwarteter Zustand während weniger Sekunden. Vorschlag Information; Entscheidung beim
Implementieren.

**7.8 Das Flapping-Verhalten von Twitch selbst.** Ob und ab welcher Rate Twitch anonyme
Neuverbindungen ablehnt, ist nie gemessen worden. Die Zuschreibung vom 2026-07-27 („drei in drei
Minuten, dann Fatal" = Abuse-Erkennung) ist durch die spätere Erklärung derselben Ausfälle über die
Zehn-Versuche-Falle der damaligen Default-Policy (`TwitchChatManager.cs:573-581`, Fix vom
2026-07-30) **überholt** — es gibt keinen belegten Fall, in dem Twitch uns wegen Wiederverbindungen
abgewiesen hätte. Die Flap-Dämpfung (5 s ab der dritten kurzen Sitzung) ist deshalb Schutz vor
unserer eigenen Schleife und vor Log-Flut, keine Kalibrierung auf Twitch. Was Twitch tatsächlich
tut, ist nur auf Prod beobachtbar (6.5).

**7.9 Der Transportwechsel auf EventSub-Conduits.** Ob, wann und unter welchen Produktbedingungen
(Bot-Account, Zustimmung je Kanal) er kommt. Nicht hier — aber das Modell ist darauf zugeschnitten,
ihn nicht zu verbauen; Abschnitt 9 sagt, was das konkret heißt und was die Alternative kosten würde.

**7.10 Die Prod-Log-Ebene von `TwitchLib.Client.TwitchClient`.** Heute `Error`
(`appsettings.json:8`). Auf `Information` lägen „Joining channel", „Leaving channel", „Connecting"
und „Reconnecting to Twitch" im Prod-Log — die letzte davon ist der einzige Prod-Zeuge für Fall B
(6.5). Volumen: eine Zeile je JOIN/PART/Verbindungsaufbau, vernachlässigbar. Ob das mit dem Umbau
oder getrennt kommt, ist offen; ohne die Änderung ist Fall B nirgends beobachtbar.

---

## 8. Risiken und was sie widerlegen oder bestätigen würde

| # | Risiko | Bestätigt durch | Ausgeräumt durch |
|---|---|---|---|
| R1 | TwitchLib liefert in einem Verlustmodus **kein** `OnDisconnected`/`OnConnectionError`; wir sind dann auf den 15-min-Watchdog zurückgeworfen — wie heute, aber ohne TwitchLibs 30-s-Schleife als Netz | Backstop-Zeile „TwitchClient meldet sich als getrennt" aus dem Tick (Abschnitt 3.3) taucht auf; S1/S7 zeigen einen Verlust ohne Signal | S1, S3, S4 liefern in allen Fällen ein Signal ≤ 1 s; auf Prod über 24 h 0 × Backstop |
| R2 | Unser Recreate-Versuch scheitert, wo TwitchLibs Schleife durchgekommen wäre (anderes Objekt, gleicher Endpunkt — unplausibel, aber die Annahme ist ungeprüft) | S3: nach Freigabe kein Erfolg binnen 30 s + 25 s; Streak steigt trotz erreichbarem Twitch | S3 grün, fünfmal |
| R3 | Twitch wertet den **sofortigen** Wiederaufbau (0 s) oder die Serie in S4 als Missbrauch und lehnt ab | S4: ab dem zweiten oder dritten Kill scheitert der Aufbau, Trace „couldn't establish a connection", Streak eskaliert — **lokal nur von der Wohn-IP prüfbar**; auf Prod: Fehlschlag-Serien nach Wiederaufbauten in den 24-h-Zahlen (6.6) | S4 grün; Prod-24-h ohne Fehlschlag-Serie. Falls bestätigt: erster Delay auf 1–2 s, Flap-Boden ab der zweiten statt dritten kurzen Sitzung — beide Zahlen liegen in der Policy, nicht im Transport |
| R4 | Der Rejoin außerhalb der Lese-Schleife ändert das Timing so, dass Joins ausbleiben oder TwitchLibs Queue-Zustand kippt (`_currentlyJoiningChannels`) | S1 Schritt 5: Raster ≠ 600 ms, „nicht bestätigt"-Zeilen, Roster mit unbestätigten Kanälen > 60 s | S1 Schritt 5–6 grün; Roster nach jedem Ereignis vollständig bestätigt |
| R5 | Hintergrund-Aufräumen des alten Clients (E4) rennt gegen den neuen: doppelte `Abort`, `ObjectDisposedException`, unbeobachtete Task-Exceptions | „Aufräumen des alten TwitchClient … fehlgeschlagen" häufig, oder Prozessabsturz durch unbeobachtete Exception | Lauf ohne diese Zeilen; Exception-Pfad im Hintergrund-Task ist gefangen und geloggt |
| R6 | Log-Flut bei langem Twitch-Ausfall (≈ 80 Warnings/h) oder bei Flapping | S3 über 30 min: Zeilenzahl | Wenn nötig: ab Streak 5 nur noch jeder fünfte Versuch als Warning, Rest Information — Policy-Entscheidung, kein Transportcode |
| R7 | Die Einordnung in 5.4 ist falsch und der Umbau muss doch die Uhr zurücksetzen | Der Harness-Vergleich vor/nach dem Deploy zeigt einen Sprung in Σ\|Log − Live\| **nach oben** (nur ein Regeländerung könnte das) | Sprung nach unten oder keiner: Erfassungsdeckung, wie erwartet |
| R8 | Sonar-Gate: Die neuen Zeilen liegen zu großem Teil in `TwitchChatManager`/`TwitchConnectionWatchdog`, die nach Regel 11 bewusst keine Fake-Tests bekommen — PR #116 stand deshalb bei 48 % lokal | `analyze` rot | Policies tragen die Logik (3.8), Transport bleibt Delegation; wie bei #116: PR-Befund abwarten, ggf. gezielte Ausnahme statt Alibi-Tests (Projektnotiz vom 2026-09-08) |
| R9 | Ein `LEAVE` während der Rejoin-Runde erzeugt einen ungewollten Join, der einen Chatroom-Platz belegt (4.5) | S5: nach dem `LEAVE` erscheint trotzdem „Channel … gejoint" für den Kanal, oder TwitchLibs `JoinedChannels` enthält ihn | S5 grün: kein JOIN nach dem `LEAVE`; die Prüfung nach dem Gate ist im Code sichtbar |
| R10 | Die Zeitleiste in 3.6 ist zu optimistisch (TLS-Handshake zu Twitch, `004`-Latenz, Bestätigungslatenz) | SLO-1 p95 > 5 s oder SLO-2 gerissen in einem von fünf S1-Läufen | **Abbruchkriterium, nicht Lattenverschieben**: ein gerissenes SLO-2 blockiert den Merge, bis die Ursache benannt ist; eine Zielzahl wird nur vor dem nächsten Lauf mit Begründung geändert (3.6). Die erste Fassung hatte hier „dann werden die Zielzahlen korrigiert" stehen — zurückgenommen (Abschnitt 10, G2) |
| R11 | Der Rejoin sendet, aber Twitch bestätigt nicht (Sendefehler, den TwitchLib verschluckt; Join-Timeout; Rate-Limit greift doch) — die Zählung bleibt bis zu 60 s stumm, während das Log „abgeschlossen" sagt | „Rejoin abgeschlossen: … K offen" mit K > 0, oder M < N ohne Warning | Die Abschlusszeile zählt Bestätigungen, nicht Aufrufe (3.5); K > 0 ist eine Warning und Teil von SLO-2; S1 zeigt fünfmal K = 0 |
| R12 | Die Shutdown-Zusage hält nicht: `StopAsync` des Watchdogs dauert länger als 1 s, der Abschluss-Flush kommt nicht dran | S6 in einem der drei Zustände: Stoppdauer ≥ 10 s (Docker-Kill) oder „Usage-Stat-Flush fehlgeschlagen" beim Stop | S6 grün in allen drei Zuständen; mit #122 zusätzlich Grace ≥ 30 s |

---

## 9. Brücke, nicht Endstation: der Transportwechsel auf EventSub-Conduits

### 9.1 Der Befund (parallele Recherche vom 2026-09-08, Twitch-Doku)

Die Untersuchung vom 2026-08-01 (`docs/Untersuchung-Twitch-EventSub-2026-08-01.md`) hatte
festgehalten, dass EventSub-WebSocket mit User-Token das JOIN-Limit **nicht** löst, und als einzigen
echten Unlock „App Access Token + `channel:bot`-Zustimmung des Broadcasters" benannt, gebunden an
Webhook- oder Conduit-Transport. Die heutige Recherche schärft das an genau der Stelle, um die es in
diesem Papier geht — den Wiederaufbau:

- **EventSub-WebSocket mit User-Token verschiebt #68 nur.** Ein ungeordneter Abriss disabled
  *alle* Subscriptions der Session („all subscriptions associated with that session are
  automatically disabled"), und ihr Neuanlegen fällt unter denselben Deckel wie IRC-JOINs („Joining
  a chat room occurs only when you subscribe to the Channel Chat Message EventSub subscription, or
  use the `JOIN` command in IRC"). Aus N JOINs würden N HTTP-Requests. **Belegt** (Doku-Zitat).
- **Conduits lösen es strukturell.** Subscriptions hängen am Conduit, nicht an der Verbindung; ein
  abgerissener WebSocket-Shard wird mit **einem** `PATCH /helix/eventsub/conduits/shards` wieder
  eingehängt — unabhängig von der Kanalzahl. Mit App-Access-Token gilt zudem: „When using an App
  Access Token for Channel Chat Message EventSub subscription, the above limits are not applicable"
  — beide Twitch-Limits (20 JOINs/10 s, 100 Chatrooms) entfallen, **ohne** verifizierten
  Bot-Account. **Belegt.** (Der Nutzer hat den Doku-Link bereits als Notiz an #68 gehängt:
  `dev.twitch.tv/docs/eventsub/handling-conduit-events/`.)
- **Der Preis:** Ende des anonymen Betriebs. `channel.chat.message` verlangt `user:bot` vom
  lesenden Account und **je Kanal** `channel:bot` vom Broadcaster — oder Mod-Status des Bots im
  Kanal. Das ist eine Produktentscheidung (Bot-Account anlegen, Zustimmungsfluss je Kanal, anderes
  Nachrichtenformat im Zählpfad) und maximal zählrelevant; im Messfenster ausgeschlossen, danach ein
  eigenes Projekt. Der App-Token-Teil existiert im Worker allerdings schon
  (`ITwitchAppTokenProvider`, seit 2026-08-03 vom `TwitchLivePollWorker` benutzt).

### 9.2 Was das für dieses Papier heißt

**Das Reconnect-Modell aus Abschnitt 3 bleibt der Weg.** Es ist im Fenster baubar, lokal
verifizierbar und beseitigt das *Risiko* (zufällige Kanäle stumm, Spleiße) sofort nach dem
2026-10-08. Aber es ist eine **Brücke**: Es drosselt einen Pfad, den Conduits gar nicht mehr hätten.
Wer dieses Papier später liest, soll nicht annehmen, die Drosselung sei das Ende der Überlegung —
sie ist der Zustand, mit dem der Worker lebt, bis die Produktentscheidung zu 9.1 gefallen ist. Die
Grenzen, die nach dem Umbau bleiben (7.2: lineare Rejoin-Dauer, 100 Chatrooms, 250 Kanäle je
7TV-Verbindung), sind die Größen, an denen man die Brücke irgendwann zu teuer findet.

### 9.3 Verbaut das Modell den Wechsel oder bereitet es ihn vor? (Entscheidung E5)

Das ist eine Entwurfsentscheidung, keine Randnotiz. Was ein Conduit-Shard beim Wiederaufbau
braucht, sieht so aus: Signal (WebSocket-Close, `session_reconnect`, Shard-Status ≠ enabled) →
neue WebSocket-Session öffnen → `session_id` erhalten → Shard per `PATCH` umhängen → Erfolg, wenn
der Shard `enabled` meldet; **kein** Rejoin je Kanal. Das ist dieselbe Schleife wie in Abschnitt 3.3
mit einer anderen *Aktion* und ohne Schritt 6.

**Vorgeschlagen: die Wiederaufbau-Entscheidung liegt in transportfreien Klassen, die
Wiederaufbau-Aktion hinter dem bestehenden Interface.** Konkret:

| Schicht | Klasse | Transportfrei? | Für Conduits |
|---|---|---|---|
| Wann/wie oft | `TwitchReconnectBackoffPolicy` — Eingang ist ein *Sitzungsergebnis* (Handshake ja/nein, Dauer, Grund), kein TwitchLib-Typ | **ja**, per Konstruktion (Abschnitt 3.8) | unverändert wiederverwendbar; auch die Flap-Dämpfung trägt (ein Shard, der sofort wieder abreißt, ist dasselbe Muster) |
| Verdacht bei Stille | `TwitchWatchdogPolicy` — Eingänge sind `bool` und Zeitspannen | **ja** | unverändert; „Frame" heißt dann „Keepalive-Nachricht" |
| Die Schleife | `TwitchConnectionWatchdog` — wartet auf Signal oder Tick, ruft drei Mitglieder des `ITwitchChatManager` (`WaitForReconnectRequestAsync`, `ReconnectOnceAsync` → Sitzungsergebnis, `RejoinDesiredChannelsAsync`) | **ja**, sofern sie ausschließlich über diese drei Mitglieder spricht — das ist der Vertrag, den dieses Papier festlegt | unverändert; ein Conduit-Manager implementiert dieselben drei, der dritte ist dort ein No-op |
| Die Aktion | `TwitchChatManager.ReconnectOnceAsync` (Schritte 1–5 aus 3.4), `RejoinDesiredChannelsAsync`, das Koaleszieren der TwitchLib-Ereignisse zum Signal | **nein**, bewusst | wird durch eine Conduit-Implementierung des Interfaces ersetzt |

Was das kostet: Das Sitzungsergebnis ist ein eigener kleiner Typ (analog `SevenTvSessionResult`),
und die Schleife darf **keine** TwitchLib-Ereignisse selbst abonnieren — sie bekommt sie nur als
koalesziertes Signal mit Grund-Text. Das ist heute schon fast so (`TwitchConnectionWatchdog` kennt
nur `ITwitchChatManager`); neu ist nur die Disziplin, dass die drei Mitglieder die ganze Kopplung
sind.

**Die verworfene Variante:** die Wiederaufbau-Schleife direkt an die TwitchLib-Ereignisse hängen —
`OnDisconnected` startet inline den Recreate mit einem `Task.Delay` aus einer Zahl im Handler,
`OnConnected` rejoint wie heute. Weniger Bewegung im Code (kein neues Sitzungsergebnis, keine
Änderung am Watchdog), aber: (a) die Schleife liefe in der Lese-Schleife des sterbenden Clients
(Fall B) und im Handshake-Pfad des neuen (Abschnitt 3.5) — genau die Kopplung, die #114 erzeugt
hat; (b) für Conduits müsste die Schleife samt Backoff neu geschrieben werden, und das Repo hätte
dann **drei** Sitzungsschleifen mit je eigener Backoff-Logik (7TV, TwitchLib, Conduit) statt einer
Form mit zwei Aktionen; (c) die Backoff-Zahlen wären nicht testbar, weil sie im Transport lägen —
Regel 11 wäre verletzt. Der Mehraufwand der vorgeschlagenen Variante ist ein Typ und ein Interface-
Schnitt; der Mehraufwand der verworfenen fiele beim Transportwechsel an, dann aber verdoppelt.

**Was das Modell trotzdem nicht vorwegnimmt:** Ob die Conduit-Schleife den *selben*
`TwitchConnectionWatchdog` benutzt oder eine Kopie, ob `SevenTvBackoffPolicy` und
`TwitchReconnectBackoffPolicy` irgendwann eine parametrisierte Klasse werden (die Kurven
unterscheiden sich nur in Deckel und Reset-Regel), und ob der Match-Cache mit EventSub-Fragmenten
statt IRC-Text arbeiten soll — alles Fragen des Transportprojekts. Dieses Papier stellt nur sicher,
dass die Antwort „die Entscheidung liegt schon transportfrei, nur die Aktion ist neu" dann wahr ist.

---

## 10. Gegenrede und was daraus folgt (2026-09-08)

Die Fassung vom Abend des 2026-09-08 (Commit `3b793e7`) wurde einer adversarialen Gegenrede
unterzogen: Verdikt „needs-attention", vier Befunde `high`, einer `medium`. Hier steht je Befund,
was angenommen und was zurückgewiesen wurde, damit nachvollziehbar bleibt, welche Fassung wodurch
entstanden ist — analog zur Praxis im Entscheidungslog. Nichts davon ist in `DECISIONS.md`
eingetragen; das geschieht mit dem Commit, der die Topologie ändert.

**G1 [high] — Kurze erfolgreiche Sitzungen eskalierten den Backoff.** *Angenommen, und der Befund
war stärker als vorgetragen.* Die 60-s-Stabilitätsregel hätte bei anhaltendem Flapping 34–47 s
Lücke je Ereignis gekostet statt heute ~14 s; die Rechnung der Gegenrede stimmt. Dazu kommt, was die
Gegenrede nicht wusste: Die Regel stützte sich auf die Abuse-Hypothese vom 2026-07-27, und das Repo
hat dieselben Ausfälle am 2026-07-30 durch die Zehn-Versuche-Falle der damaligen Default-Policy
erklärt (`TwitchChatManager.cs:573-581`) — die Regel war also nicht nur ungemessen, ihre
Begründung war überholt. Geändert: Streak zählt nur Fehlversuche und setzt beim `004` zurück;
Flap-Dämpfung getrennt, fester Boden 5 s ab der dritten kurzen Sitzung; Jitter am Deckel gekappt
(3.6). **Nicht** übernommen: eine Dämpfung ganz wegzulassen — eine Verbindung, die Twitch sofort
nach dem Handshake trennt, wäre sonst eine Schleife mit ~2-s-Periode.

**G2 [high] — Die Zielzahlen maßen das Senden, nicht die Zählung.** *Angenommen, beide Teile.*
(a) Die Zeitleiste war als „plausibel" markiert, aber das Ziel war eine einzelne Zahl ohne
Verteilung und ohne harte Schranke — jetzt Median/p95 plus die 25-s-Schranke je Versuch.
(b) `TryJoinAsync` kehrt nach dem Senden oder Einreihen zurück, nie nach der Bestätigung, und
TwitchLib verschluckt Sendefehler (am Binärstand bestätigt: `QueueingJoinCheckAsync` awaitet nur
`SendAsync`). „Rejoin abgeschlossen: N" hätte also ein stummes Set beglaubigen können. Geändert:
zwei Zielgrößen — **SLO-1** Verlust → `004` (diagnostisch), **SLO-2** Verlust → letzte Bestätigung
aller N Kanäle plus K = 0 offene (Abbruchkriterium); die Abschlusszeile wartet bis zu 5 s auf
Bestätigungen und nennt N/M/K (3.5, 3.6). R10 („dann werden die Zielzahlen korrigiert") war
Lattenverschieben und ist zurückgenommen: eine Verfehlung blockiert den Merge; eine Zahl ändert sich
nur vor dem nächsten Lauf mit Begründung, wie T11/T12 an #69.

**G3 [high] — Shutdown war nur im Backoff abbrechbar.** *Angenommen, mit einer Korrektur.* Connect
(bis 25 s) und Rejoin (≥ 11,4 s bei 20 Kanälen) trugen kein Token; der Watchdog stoppt vor dem
`UsageFlushWorker`; Docker killt nach 10 s (#122) — der Abschluss-Flush käme nie dran. Die
Korrektur: **das ist heute schon so** (`CheckOnceAsync` → `RecreateClientAsync` → `OpenAsync` bis
30 s ohne Token), das Konzept hätte es nur verlängert. Geändert: Shutdown-Zusage ≤ 1 s in jedem
Zustand, Token in jedem wartenden Schritt einschließlich `_joinGate`; #122 ausdrücklich in
denselben Deploy gezogen, mit der Begründung, warum keines der beiden ohne das andere reicht (3.4,
4.6); S6 misst alle drei Zustände; R12 neu.

**G4 [high] — Die lokale Verifikation bewies die zentralen Eigenschaften nicht.** *Angenommen in
zwei von drei Teilen; der dritte ist nicht zurückgewiesen, sondern anders eingeordnet.*
(a) Log-Ebene: Das Rezept sagte `Warning`; „Joining channel" liegt auf **Information** (am
Binärstand belegt, `LogExtensions`), die Abwesenheit der Zeilen hätte also nichts bewiesen — ein
grüner Lauf hätte den Defekt beglaubigt. Geändert: `Information` mit Kontrollfrage vor dem Lauf,
und der Beleg ist jetzt **positiv** (genau N Zeilen im 600-ms-Raster), nicht die Abwesenheit (6.1,
6.2). (b) S3 droppte Traffic einer offenen Verbindung und erzeugte damit Fall C statt der
Backoff-Schleife. Geändert: Kill plus Blockade neuer Verbindungen, in zwei Varianten (REJECT für die
Kurve, DROP für die Schranke). (c) S2 läuft nicht in der echten Lese-Schleife — das stand schon in
der ersten Fassung, aber als Fußnote unter einem Rezept, das trotzdem Beweis hieß. Die Antwort ist
kein besseres Rezept, denn es gibt keins: Fall B ist lokal **nicht** beweisbar. Geändert: 6.5
benennt das, mit dem Prod-Beleg (setzt 7.10 voraus) und der ehrlichen Einschränkung, dass die
Ein-Schleifen-Aussage auf Prod nur indirekt bezeugt ist. Die ganze Beweisführung ist umgeschrieben:
jede Behauptung hat jetzt ihren Widerleger und ihre Einstufung (6.2).

**G5 [medium] — Gleichzeitiges `LEAVE` hinterließ dauerhaft einen ungewollten JOIN.** *Angenommen.*
„Der nächste Recreate räumt auf" war keine Schranke; bis dahin belegt der Kanal einen der 100
Chatroom-Plätze und niemand verlässt ihn, weil die Absicht fehlt. Geändert: `TryJoinAsync` prüft
nach dem Erwerb des `_joinGate`, unmittelbar vor dem Senden, den aktuellen Wunschzustand (3.5,
4.5); S5 (b) prüft es; R9 ist kein „kein Blocker" mehr.

**Was sich an den Zielzahlen geändert hat.** Vorher eine Zielgröße: „Verbindung ≤ 3 s, erster JOIN
≤ 4 s, letzter JOIN gesendet ≤ 4 s + 0,6 s × (N − 1)". Jetzt zwei: SLO-1 Verlust → `004` mit
Median ≤ 3 s, p95 ≤ 5 s, harte Schranke 25 s je Versuch; SLO-2 Verlust → letzte **Bestätigung**
≤ 6 s + 0,6 s × (N − 1) und K = 0 (13 Kanäle: 13,2 s; 20 Kanäle: 17,4 s). Das Abbruchkriterium
trägt SLO-2. Im Flapping-Fall kostet die neue Fassung 5 s ab dem dritten Ereignis statt bis zu 36 s.

**Was unverändert blieb, und warum.** Das Modell selbst (E1–E5), der 30-s-Deckel, der erste
Versuch nach 0 s, die Trägerin der Schleife, die Conduit-Einordnung (Abschnitt 9). Kein Befund der
Gegenrede berührte sie; sie stehen weiter unter den Vorbehalten aus Abschnitt 8.

**Neu offen (7.10):** Die Prod-Log-Ebene von `TwitchLib.Client.TwitchClient` muss auf
`Information`, sonst ist Fall B auch auf Prod unsichtbar — eine kleine `appsettings.json`-Änderung
mit eigener Abwägung (Log-Volumen: eine Zeile je JOIN/PART/Connect, vernachlässigbar).

---

## Anhang A — Was beim Gegenprüfen anders vorgefunden wurde als im Auftrag

1. **`_client_OnReconnected` ruft `QueueingJoinCheckAsync` einmal**, nicht in einer Schleife; der
   Rest der Queue wird über `Handle366` abgearbeitet. Ergebnis ist derselbe Schwall in
   Bestätigungs-Kadenz — die Beschreibung „reiht alle ein, joint ungedrosselt" bleibt richtig, der
   Mechanismus ist ein anderer (Abschnitt 7.1).
2. **`NoReconnectionPolicy` ist `ReconnectionPolicy(0, maxAttempts: 1)`**, und wegen
   `Reset(isReconnect: true)` ohne Zähler-Reset kann `ReconnectAsync` an einem solchen Client **nie**
   gelingen. Das macht `ReconnectAction.Reconnect` nicht nur überflüssig, sondern falsch
   (Abschnitt 3.1).
3. **TwitchLib liefert ohne Reconnect-Policy weiterhin ein Signal** binnen ~0,6 s — je Verlust bis zu
   zwei `OnDisconnected` und ein `OnConnectionError` („Fatal network error."), Letzteres wird zur
   Normalzeile (Abschnitt 3.2). Der Auftrag hatte den Signalweg als offene Frage gestellt.
4. **Unser 600-ms-Rejoin läuft heute inline in der Lese-Schleife** des frischen Clients
   (`Handle004` awaitet `OnConnected`). Nicht im Auftrag, entscheidend für E2 (Abschnitt 3.5).
5. **`RecreateClientAsync` awaitet `oldClient.DisconnectAsync()`**, das ≥ 1,9 s eingebaute Delays
   enthält, bevor es den neuen Client öffnet (Abschnitt 2.2, Punkt 3; E4).
6. **`TwitchWatchdogPolicy.cs:213-218`** im Auftrag sind in der Datei die Zeilen **63–68** (die
   Datei hat 95 Zeilen; die Nummern stammten aus einer zusammengesetzten Ausgabe). Alle übrigen
   Zeilenangaben des Auftrags (`TwitchChatManager.cs:102-136, 215-251, 303-338, 367-386, 423-440,
   442-456, 571-584`, `ReconnectPolicy.cs:122-127`, `EmotePurge.Worker.csproj:12,15`) stimmen.
7. **Netzname für `docker network disconnect`** ist `emote-purge-dev_emotepurge-network`; der
   Schnitt muss > 15 min dauern, nicht > 5 min (Abschnitt 6.2, S7).
8. Das Ticket #68 zitiert `TwitchChatManager.cs:250` für `Initialize(new ConnectionCredentials())`;
   die Zeile ist 255 (später im selben Ticket korrekt genannt). Kosmetik.

## Anhang B — Dokumente, die der Umbau mitziehen muss (im selben Commit, Regel 3)

- `docs/DECISIONS.md`: neuer Eintrag; er **ersetzt** die Topologie-Aussage des #114-Eintrags vom
  2026-09-08 („Reconnect heißt reconnect jetzt, recreate einen Tick später") und nennt die Herkunft
  der drei `ReconnectPolicy`-Regeln, damit sie nicht wiederkommen.
- `CLAUDE.md`, Architektur-Absatz zum Worker (der Satz ab „Einen heißt seit 2026-09-08 …").
- `docs/Architectur.md` A.1 (Verbindungsaufbau, Watchdog).
- `src/EmotePurge.Api/Health/WorkerCapacity.cs:18-26`: Begründung der 20 neu schreiben (7.2).
- `src/EmotePurge.Worker/TwitchChatManager.cs:22-39` (Klassenkommentar), `Worker.cs:23-25`.
- `src/EmotePurge.Worker/appsettings.json:8`: Log-Ebene `TwitchLib.Client.TwitchClient` (7.10) —
  falls mit dem Umbau, dann hier.
- `docker-compose.prod.yml` und `docker-compose.yml`: `stop_grace_period` für `worker` (#122) —
  derselbe Deploy, s. 4.6.
- `src/EmotePurge.Worker/TwitchChatManager.cs:400-408` (`OnFailureToReceiveJoinConfirmation`)
  und `:410-421` (`OnConnectionError`): Kommentare beschreiben nach dem Umbau ein anderes Verhalten.
- `.github/dependabot.yml`: unberührt — kein Versionswechsel von `TwitchLib.*`, der Umbau umgeht
  die Bibliothek weiterhin.
