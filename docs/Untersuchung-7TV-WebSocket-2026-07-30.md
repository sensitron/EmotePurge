# Untersuchung: 7TV-Emote-Set-Sync via EventAPI-WebSocket — Re-Evaluation (2026-07-30)

> **Status-Nachtrag:** Option C (WS zusätzlich zum REST-Resync) wurde noch am 2026-07-30 umgesetzt —
> s. [DECISIONS.md](DECISIONS.md), Eintrag „7TV-EventAPI-WebSocket wieder eingeführt". Der
> Untersuchungstext darunter bleibt unverändert als Beleg-Dokument stehen.

Auftrag: Rekonstruktion und Bewertung der Entscheidung vom 2026-07-25, den 7TV-EventAPI-WebSocket
zu entfernen und durch periodisches REST-Polling zu ersetzen ([docs/DECISIONS.md](DECISIONS.md),
Eintrag „2026-07-25 — 7TV-EventAPI-WebSocket (Live-Dispatch) komplett entfernt").
Methodik: Git-Archäologie (Commits `65f47cd`/`d8869ce`), Doku-Abgleich (CLAUDE.md, Architectur.md,
DECISIONS.md, Review-2026-07-29 + Umsetzung), externe Recherche (7TV-Monorepo-Quellcode, Issues,
Extension/DankChat/Chatterino), plus eigene Live-Probe gegen `wss://events.7tv.io/v3` am 2026-07-30.
Durchgehende Kennzeichnung: **belegt** / **plausibel** / **widerlegt** / **nicht überprüfbar**.

---

## 1. Fazit vorab

**Die Entscheidung war im Ergebnis richtig, in der Begründung falsch.** Die beobachtete
„unzuverlässige Zustellung seitens 7TV" war mit hoher Wahrscheinlichkeit keine — unsere damalige
Implementierung enthielt **zwei eigenständig fatale Bugs** (Resubscribe lief vor dem
Verbindungsaufbau ins Leere; der Dispatch-Parser las die falschen Feldnamen `added`/`removed`
statt `pushed`/`pulled`), und die nachgerüstete channel-scoped Subscription konnte die gesuchten
Channel-Set-Updates **strukturell nie** liefern (serverseitig ein Presence-Scope für
Personal-Emotes, kein Set-Scope). Da die 7TV-EventAPI zusätzlich jede Verbindung nach ~1 h hart
trennt und **kein Resume/Replay implementiert**, waren unsere Subscriptions spätestens eine Stunde
nach Prozessstart dauerhaft tot — REITEN konnte nie ankommen, DankChat (korrekte Implementierung)
bekam es prompt. Der Gegenbeweis wurde am 2026-07-30 live geführt: Zwei Emote-Adds und ein Remove
auf einem eigenen Set kamen über eine korrekte `{object_id}`-Subscription **unmittelbar** als
Dispatch an (Abschnitt 5). Richtig war die Entscheidung trotzdem: Ein REST-Vollsync ist bei 7TV **kein
optionales Sicherheitsnetz, sondern Pflicht** (kein Replay, belegte Publish-Lücken), und die
Entfernung hat echte Komplexität gespart.

**Empfehlung:** Status quo jetzt behalten. Vor dem größeren Ausbau (Zielfall HandOfBlood,
> ~30–50 aktive Channels oder sobald Sekunden-Latenz gewünscht ist) den WebSocket **zusätzlich**
wieder einführen — `{object_id}`-Subscription, Resubscribe nach jedem Hello, Heartbeat-Watchdog,
Dispatch-Deltas nach dem Anforderungskatalog in Abschnitt 6/7 — und den REST-Resync als
Reconciliation auf ~5-Minuten-Takt strecken. Kein Wechsel auf v4 (es gibt keinen v4-Event-Kanal).

---

## 2. Was wir damals gebaut haben

Quelle: eigener Read des historischen Codes (`git show d8869ce^:src/EmotePurge.Worker/SevenTv/…`)
plus Git-Archäologie (Scratchpad-Artefakt Agent 1). **Belegt**, sofern nicht anders markiert.

**Commits:**

- `65f47cd` (2026-07-24 17:33) — „feat: add 7TV WebSocket EventAPI client and wire into join/leave
  flow": `ISevenTvEventClient`/`SevenTvEventClient`/`SevenTvDispatchParser` (Worker),
  `SevenTvEmoteSetDelta` (Core), `ApplyEmoteSetUpdateAsync` (SevenTvSyncService).
- `d8869ce` (2026-07-25 16:30) — „refactor(worker): remove unreliable 7TV WebSocket for periodic
  REST resync": alles gelöscht, `SevenTvPeriodicResyncWorker` (1-Minuten-Vollsync) eingeführt.
- **Zwischen den beiden Commits wurden die WebSocket-Dateien kein einziges Mal committet verändert**
  (`git diff 65f47cd d8869ce^ -- <WS-Dateien>` ist leer). Die im Entfernungs-Commit und in
  DECISIONS.md beschriebenen Test-Iterationen (Warning-Logging + Raw-JSON, Hello/Ack/Error-Sichtbarkeit,
  Wildcard `emote_set.*`, channel-scoped Subscription) existierten **nur uncommittet** im
  Arbeitsverzeichnis und sind nicht mehr rekonstruierbar. Der einzige je committete Stand ist der
  von `65f47cd`.

**Die committete Implementierung** (rohes `System.Net.WebSockets.ClientWebSocket`, Singleton, eine
Verbindung für die Worker-Lebensdauer):

- **Subscribe:** genau ein Frame-Typ, hart `emote_set.update` mit
  `{"op":35,"d":{"type":"emote_set.update","condition":{"object_id":"<setId>"}}}` — pro Channel
  eine Subscription (`_activeSubscriptions`: channelName → setId), gesendet bei Boot-Recovery,
  Redis-`JOIN:` und (kaputt, s. u.) beim Reconnect. Unsubscribe analog mit op 36.
- **Hello (op 1):** nur `heartbeat_interval` gelesen (Timeout = 3×, als Receive-Idle-Timeout via
  `CancellationTokenSource.CancelAfter` — strukturell korrekt). `session_id`, `subscription_limit`
  wurden nie gelesen; **kein Resume (op 34) im Code**.
- **Ignorierte Op-Codes:** Ack (5), Error (6), **Reconnect (4)**, End of Stream (7) fielen alle in
  einen `default`-Zweig mit `LogDebug` — der Server konnte einen bevorstehenden Disconnect ankündigen,
  ohne dass wir es je gesehen hätten.
- **Reconnect-Schleife:** fester 5-s-Backoff, unbegrenzt; Verbindung als komplett neue Session.
- **Dispatch-Pfad:** `HandleDispatchAsync` filtert auf `type == "emote_set.update"`, Parser liest
  `body.added[]`/`body.updated[]`/`body.removed[]` (Einträge mit `key=="emotes"`),
  `ApplyEmoteSetUpdateAsync` upsertet/archiviert inkrementell und ruft danach
  `RefreshMatchCacheAsync` (voller Reload).
- **Fire-and-forget:** `ConnectAsync` startet den Verbindungs-Loop per `_ = RunConnectionLoopAsync(…)`
  und gibt sofort `Task.CompletedTask` zurück; kein Watchdog beobachtete den WS-Client (der
  `TwitchConnectionWatchdog` entstand erst später und nur für IRC).

---

## 3. Was wir beobachtet haben — Einordnung der dokumentierten Befunde

Quelle der Beobachtungen: DECISIONS.md:174-178, Architectur.md A.3, Commit-Message `d8869ce`.

| # | Beobachtung (damals) | Einordnung heute |
|---|---|---|
| 1 | WS verbunden, Hello empfangen, Subscribe-Frames „korrekt" (`op:35`, `{object_id}`) | **Belegt.** Das committete Frame-Format ist auch gegen den heutigen Server korrekt (Live-Probe 2026-07-30: identisches Frame wird sofort per Ack bestätigt). |
| 2 | „Subscriptions serverseitig korrekt registriert (per Ack bestätigt, subscription_limit weit unausgeschöpft)" | **Plausibel, aber nicht überprüfbar.** Der committete Code konnte Acks gar nicht sichtbar machen (op 5 → `LogDebug`); die Aussage stammt aus den uncommitteten Test-Iterationen. Sie belegt zudem nur den *Moment des Subscribens* — nicht, dass die Subscription eine Stunde später noch lebte (s. Abschnitt 4/5: TTL + Resubscribe-Bug). |
| 3 | Test-Emote „REITEN" (vassilly) kam **nie** per Dispatch an, auch nach 18+ h | **Beobachtung belegt** (dokumentiert). Die damalige *Attribution* („Zustellung seitens 7TV unzuverlässig") ist als notwendige Erklärung **widerlegt**: Nach spätestens ~1 h (Server-TTL) + erstem Reconnect war unsere Subscription serverseitig dauerhaft weg (Bug 1, Abschnitt 4), und selbst ein zugestellter Add-Dispatch hätte im committeten Code **nichts bewirkt** (Bug 2: falsche Feldnamen). Ob 7TV die Dispatches tatsächlich gesendet hat, ist nicht mehr feststellbar — es *musste* aber nichts ankommen, damit wir genau dieses Symptom sehen. |
| 4 | Test-Emote „COPIUM" kam erst nach ~10 min an | **Beobachtung belegt; Erklärung plausibel:** entweder der REST-Sync innerhalb von `ResubscribeAndResyncAsync` bei einem zufälligen Reconnect, oder der 7TV-REST-Cache-Lag (SevenTV/SevenTV Issue #81: v3-REST liefert 10–30 min veraltete Daten, offen seit 2024). Eine echte, verspätete WS-Zustellung ist die unwahrscheinlichste der drei Erklärungen. |
| 5 | DankChat bekam dasselbe Update korrekt | **Belegt** (dokumentiert) und heute **konsistent erklärbar**: DankChat subscribt ausschließlich `{object_id}`, sendet nach *jedem* Hello alle Subscriptions neu und parst `pushed`/`pulled`/`updated` — exakt die drei Punkte, an denen unser Client scheiterte. |
| 6 | Wildcard `emote_set.*` + channel-scoped Subscription nachgerüstet, „ohne messbare Verbesserung" | **Nicht überprüfbar** (nie committet). Für die channel-scoped Variante heute **erklärt**: `{ctx:"channel",platform,id}` mappt serverseitig auf einen **Presence-Scope** (Personal-Emote-Sets/Cosmetics/Entitlements der anwesenden User), nicht auf das Channel-Set — sie *konnte* die gesuchten Updates strukturell nicht liefern (7TV-Monorepo, `event_topic.rs`; von der eigenen Live-Probe bestätigt: die channel-scoped Subscription lieferte binnen 120 s 30+ Dispatches, ausnahmslos Personal-Set-/Cosmetic-Events Dritter). Dass auch die Wildcard-`object_id`-Variante nichts brachte, passt zu Bug 1: Alle Varianten liefen über denselben kaputten Resubscribe-Pfad. |
| 7 | Ein scheinbarer „Live-Fang" beim Set-Wechsel-Test war ein Zufallstreffer des Resync-Ticks | **Belegt** (DECISIONS.md) — und rückblickend das stärkste damalige Indiz dafür, dass der WS-Pfad überhaupt nichts lieferte und alle „Erfolge" aus dem REST-Pfad kamen. |
| 8 | „REST-Vollsync war in jedem Test zuverlässig" | **Belegt** — mit der heutigen Einschränkung, dass Issue #81 dem REST-Pfad im Worst Case 10–30 min Datenverzug bescheinigt; „zuverlässig" hieß damals „kam an", nicht „kam frisch an". |

**Widersprüche zwischen den Dokumenten** (aus dem Doku-Abgleich, Details im Review-Kontext):
Die drei Quellen (CLAUDE.md → Architectur.md A.3 → DECISIONS.md) sind in der Kernaussage konsistent;
DECISIONS.md ist die vollständigste (nur dort: der Zufallstreffer-Befund und „18+ Stunden").
Zwei Punkte verdienen Korrektur-Bewusstsein: (a) Architectur.md beschrieb die Fehlerisolierung des
Resync-Workers schon vor dem S2-6-Fix (2026-07-30) so, als wäre sie vollständig gewesen — die
Formulierung war optimistischer als der Code. (b) Das Wort „**nachweislich** nicht zuverlässig"
in DECISIONS.md/Architectur.md ist nach dieser Untersuchung nicht mehr haltbar — nachgewiesen war
nur das Symptom, nicht die Ursache beim Anbieter. DECISIONS.md wird vereinbarungsgemäß nicht
rückwirkend umgeschrieben; dieses Dokument ist die Korrektur.

---

## 4. Mögliche Fehlerquellen auf unserer Seite — konkret am damaligen Code

Beide Kernbugs habe ich selbst am historischen Code (`git show d8869ce^:…`) verifiziert. **Belegt.**

**Bug 1 — Resubscribe lief vor dem Verbindungsaufbau, gegen `_socket == null`:**

```csharp
// SevenTvEventClient.RunConnectionLoopAsync (Stand d8869ce^)
if (!firstConnect)
{
    await ResubscribeAndResyncAsync(ct);   // sendet op-35-Frames über _socket …
}
firstConnect = false;
await ConnectOnceAndPumpAsync(ct);          // … aber _socket wird ERST HIER gesetzt
```

`ConnectOnceAndPumpAsync` setzt `_socket = null` im `finally`, sobald eine Verbindung endet.
Beim nächsten Schleifendurchlauf läuft `ResubscribeAndResyncAsync` also garantiert gegen `null`,
und `SendFrameAsync` verwirft jedes Frame still:

```csharp
if (socket is null || socket.State != WebSocketState.Open)
{
    logger.LogDebug("7TV-Socket nicht offen, Frame (op {Op}) wird beim nächsten Reconnect nachgeholt.", op);
    return;   // ← die Log-Aussage ist falsch: es gibt keinen Nachhol-Mechanismus
}
```

**Konsequenz: Ab dem ersten In-Process-Reconnect war die serverseitige Subscription dauerhaft
weg** — der Client hielt `_activeSubscriptions` weiter für gültig, 7TV kannte sie nicht mehr.
Da der Server jede Verbindung nach ~1 h TTL trennt (Abschnitt 5), trat dieser Zustand spätestens
eine Stunde nach Prozessstart ein — und blieb bis zum Neustart. Der REST-Sync innerhalb von
`ResubscribeAndResyncAsync` lief dagegen normal weiter (kein Socket-Bezug), weshalb der REST-Pfad
in jedem Test „zuverlässig" wirkte.

**Bug 2 — Der Dispatch-Parser las die falschen Feldnamen:**

```csharp
// SevenTvDispatchParser.ParseEmoteSetUpdate (Stand d8869ce^)
if (body.TryGetProperty("added",   out var addedArray))   { … }
if (body.TryGetProperty("updated", out var updatedArray)) { … }
if (body.TryGetProperty("removed", out var removedArray)) { … }
```

Echte `emote_set.update`-Dispatches transportieren Emote-Änderungen aber in **`pushed`/`pulled`**
(plus `updated` für Renames) — belegt durch die eigene Live-Probe
(`"pushed":[{"key":"emotes","index":0,"type":"object","value":{…}}]`), die EventAPI-README
(ChangeMap) und die Parser aller drei Vergleichs-Clients. `TryGetProperty("added")` schlägt bei
einem echten Add-Dispatch einfach fehl — **kein Fehler, kein Log, leeres Delta, keine Wirkung**.
Selbst ein perfekt zugestellter REITEN-Dispatch wäre im committeten Code spurlos versandet.
(Ob die uncommitteten Test-Iterationen mit Raw-JSON-Logging diesen Parser korrigiert hatten, ist
**nicht überprüfbar**; die Commit-Message erwähnt eine Parser-Neufassung, aber deren Inhalt ist weg.)

**Verstärkende Faktoren (belegt am Code):**

- **Op 4 (Reconnect) ignoriert:** Der Server kündigt den TTL-Disconnect per op 4 an — wir haben es
  als „unbehandelter Opcode" auf Debug-Level verworfen und den Disconnect passiv erlitten.
- **Initial-Connect-Race:** `ConnectAsync` kehrte sofort zurück (`_ = RunConnectionLoopAsync(…)`);
  die ersten Boot-Recovery-Subscribes konnten den Handshake überholen und still verloren gehen
  (dieselbe „wird nachgeholt"-Illusion wie Bug 1).
- **Fire-and-forget ohne Beobachter** (Review-Muster S2-8): Der Verbindungs-Loop lief unbeobachtet;
  eine Exception, die den `when (!ct.IsCancellationRequested)`-Filter verfehlte, hätte den Client
  lautlos für immer beendet. Zusätzlich lief jeder `JOIN:`-Subscribe durch den damals
  fire-and-forget-Redis-Handler.
- **Ack/Error unsichtbar** (op 5/6 → Debug): Ein serverseitiges „Invalid Payload"/Fehler-Frame wäre
  von einem unbekannten Opcode nicht unterscheidbar gewesen.

Sauber waren: die Heartbeat-Timeout-Mechanik (3× Intervall als Receive-Timeout), das äußere
try/catch um die Dispatch-Verarbeitung, und die Interface-/Singleton-Struktur.

---

## 5. Stand der 7TV-EventAPI heute (2026-07-30)

Quellen: 7TV-Monorepo `SevenTV/SevenTV` (Rust, produktiv; die Alt-Repos `EventAPI`/`API` sind
archiviert, das v3-*Protokoll* lebt als `apps/event-api` weiter), EventAPI-README, Issues,
Client-Quellcode; plus eigene Live-Probe. Vollständige Quellenliste mit URLs im
Recherche-Artefakt; die zentralen Fakten:

- **v3-WebSocket ist der einzige Live-Kanal und nicht deprecated.** v4 hat GraphQL/REST
  (`7tv.io/v4/gql`, live verifiziert), aber **keinen Event-Kanal**: `wss://events.7tv.io/v4`
  existiert nicht (Probe: sofortiger Close 1006), das v4-GQL-Schema ist explizit
  `EmptySubscription` (Monorepo `apps/api/src/http/v4/gql/mod.rs`), der event-api-Service hat nur
  ein `v3`-Modul. Deckt sich mit DECISIONS.md („v4 … hat keine EventAPI").
- **Kein Resume, kein Replay:** Der aktuelle Server beantwortet op 34 RESUME hart mit
  `success:false, dispatches_replayed:0` („Subscription resume is not supported",
  `apps/event-api/src/http/v3/mod.rs`). Die README-Zusage „missed events will replay" ist
  veraltet. **Verpasste Dispatches sind unwiederbringlich verloren.**
- **Harte Verbindungs-TTL ~1 h** (Code-Default `ttl: 3600 s` mit Jitter; vorher wird op 4
  Reconnect gesendet). Prod-Konfiguration nicht eingesehen — Default-Vorbehalt. Zusammen mit
  „kein Resume": **mindestens stündlich eine garantierte Lücke** → ein REST-Abgleich ist
  protokollbedingt Pflicht, für jeden Client.
- **Condition-Semantik:** `{object_id:<setId>}` → Set-Scope (der richtige Weg);
  `{ctx:"channel",platform,id}` → **Presence-Scope** (Personal-Sets/Cosmetics/Entitlements
  anwesender User, benötigt zudem `writePresence`-Aufrufe). Die Extension subscribt `emote_set.*`
  deshalb bewusst **zweimal** — unsere damalige Lesart „channel-scoped ist die bessere Variante
  fürs Channel-Set" war ein Missverständnis.
- **Belegte serverseitige Schwächen:** REST-Cache wird nicht invalidiert (Issue #81, offen:
  10–30 min Lag — betrifft **unseren heutigen REST-Sync** direkt); mindestens ein belegter Fall
  fehlender Dispatches bei Mehrfach-Entfernung desselben Emotes (Issue #137, behoben);
  inkonsistente Payload-Formen (Issue #256, offen); Presence-Fanout über verdrängende LRU
  (Issue #139 — ob der `object_id`-Scope derselben Grenze unterliegt: **nicht geklärt**).
- **Vergleichs-Clients:** DankChat (nur `object_id`, Resubscribe nach jedem Hello,
  Heartbeat-Watchdog 3×, exponentieller Backoff), Chatterino (beide Conditions modelliert,
  Watchdog, op 4 → Reconnect), Extension (4 Subscriptions/Channel, Resume-Versuch mit
  Re-Subscribe-Fallback, **kein** Heartbeat-Watchdog). **Keiner der drei betreibt periodisches
  REST-Polling** — deren Sicherheitsnetz ist der Channel-/Seitenwechsel; für einen dauerhaft
  mitlesenden Server wie unseren ist das kein Vorbild, unser Resync-Ansatz bleibt nötig.

**Eigene Live-Probe** (Wegwerf-Skripte im Scratchpad, 2026-07-30, Channel `handofblood`,
Set `01GV88A38G0006FW5TVZVMG507`, 903 Emotes):

- Hello: `heartbeat_interval≈47–49 s` (gejittertes 45-s-Default), `subscription_limit=500`,
  Instanz `event-api-4` mit ~78 000 Verbindungen.
- Alle drei Subscribe-Varianten (auch unser damaliges committetes Frame, byte-gleich) wurden
  binnen <100 ms per Ack bestätigt; Heartbeats kamen exakt im Intervall.
- Über die channel-scoped Subscription trafen in 120 s **30+ Dispatches** ein — ausnahmslos
  Personal-Set-/Cosmetic-/Entitlement-Events von Zuschauern, bestätigt die Presence-Semantik.

**Kontrollierter Dispatch-Beweis (2026-07-30, ~12:44 Uhr) — erbracht.** Zweite Probe:
`emote_set.*`-Subscription mit `{object_id}` auf das gemeinsame aktive Set von
`sensitron`/`olaf_olaf_son` (`01KH8GBXCN401GKGN7T1XQKZCP`), danach hat der Set-Owner live zwei
Emotes hinzugefügt und eines entfernt. Alle drei Änderungen kamen **unmittelbar** als
`emote_set.update`-Dispatch an:

| Zeit | Aktion | Payload |
|---|---|---|
| 12:44:05 | catJAM hinzugefügt | `pushed: [{key:"emotes", index:12, value:{…}}]` |
| 12:44:18 | OMEGALUL hinzugefügt | `pushed: [{key:"emotes", index:13, value:{…}}]` |
| 12:44:44 | DIESOFCRINGE entfernt | `pulled: [{key:"emotes", index:10, old_value:{…}}]` |

Damit ist **belegt**: (a) Die `object_id`-Zustellung funktioniert prompt und zuverlässig (bei
dieser Set-Größe); (b) das Wire-Format ist `pushed`/`pulled` mit `value`/`old_value` — die
Feldnamen, die der damalige Parser (`added`/`removed`) nie gelesen hätte (Bug 2, Abschnitt 4).
Nebenbefund: Eine versehentlich doppelt gesendete identische Subscription führte **nicht** zum
laut README erwartbaren `4009 Already Subscribed`-Disconnect, sondern wurde still ignoriert
(nur ein Ack).

**Dritte Probe (2026-07-30, ~12:52 Uhr): Set-Wechsel + große Sets — ebenfalls erbracht.**
Subscriptions: `emote_set.*` (`object_id`) + `user.*` (`object_id` der 7TV-User-ID,
Chatterino-Muster) auf drei Channels, darunter zwei mit großen Sets (`schakaraltd2`: 409 Emotes,
`aatrociity`: 442 Emotes). Der Set-Owner führte live einen Set-Wechsel auf `sensitron`
(hin und zurück) sowie Emote-Remove/-Add auf den großen Sets aus. Ergebnis:

- **Set-Wechsel kommt als `user.update` an, nicht als `emote_set`-Event** — prompt, mit alter
  *und* neuer Set-ID explizit im Payload: `updated[0]` ist `{key:"connections", index:0,
  nested:true, value:[{key:"emote_set", old_value:{id,name,…}, value:{id,name,…}},
  {key:"emote_set_id", old_value:"<alt>", value:"<neu>"}]}`. Beide Wechsel (12:52:04 hin,
  12:52:16 zurück) wurden sofort zugestellt. Die alte `object_id`-Subscription bleibt bestehen,
  liefert danach aber naturgemäß nur noch Änderungen am alten Set — ein Worker muss auf
  `user.update` reagieren und das neue Set nachabonnieren (die nötigen IDs stehen komplett im
  Event; ein Add auf das *neu gewechselte* Set wurde in diesem Test nicht mehr ausgeführt, der
  Nachsubscribe-Pfad blieb daher unexerziert).
- **Große Sets liefern genauso prompt:** Remove auf dem 409er- und dem 442er-Set (12:52:50,
  160 ms auseinander), Adds auf drei Sets (12:53:07–08, Spanne <500 ms) — alle Dispatches
  unmittelbar, mit korrekten `pushed`/`pulled`-Deltas (`index` 379/420/441 zeigt: auch am
  Set-Ende großer Arrays). Damit gibt es einen belastbaren Datenpunkt bis ~440 Emotes; nur die
  HandOfBlood-Größenordnung (~900) und Channels mit sehr vielen Zuschauern bleiben ungemessen.
- Nebenbefund: Wird dasselbe Emote in mehreren abonnierten Sets geändert, kommt **pro Set ein
  eigener Dispatch** — sauber pro `object_id` getrennt.

---

## 6. Einbettung in den heutigen Sync-Pfad

Der Sync-Pfad von heute ist nicht mehr der vom Juli: Seit Review-Welle A existieren
`ChannelSyncGate` (serialisiert `SyncChannelAsync` pro Channelname), `BootRecoveryGate`
(Resync-Worker startet erst nach Boot-Recovery), der komplett fehlerisolierte
`ResyncOnceAsync`-Tick (S2-6), der S3-12-Schutz (implausibel leeres 7TV-Set wird übersprungen
statt alles zu archivieren) und der sequenzielle Redis-Handler (S2-8). Eine
WS-Wiedereinführung fügt sich so ein:

- **Dispatch-Verarbeitung läuft durch `SyncChannelAsync`-Bausteine, nicht daran vorbei.** Ein
  Dispatch-Handler, der inkrementell in Postgres schreibt, ist ein zweiter nebenläufiger
  Schreiber auf denselben `(ChannelId, SevenTvEmoteId)`-Zeilen — exakt die Kollisionsklasse, die
  `ChannelSyncGate` beseitigt hat. Jede Delta-Anwendung muss deshalb dasselbe Gate pro Channel
  erwerben wie der Resync und der JOIN-Pfad.
- **`RefreshMatchCacheAsync` bleibt Voll-Reload.** Das atomare `ReplaceChannel`-Muster (im Review
  als rennfrei bestätigt) funktioniert für Deltas unverändert: Delta in Postgres anwenden, dann
  vollen Cache-Reload — kein inkrementelles Cache-Patchen einführen.
- **S3-12 sinngemäß übertragen:** Der REST-Schutz („leeres Set trotz bekanntem Bestand →
  überspringen") braucht ein Delta-Pendant — z. B. ein `pulled`-Dispatch-Schwall, der den
  bekannten Bestand eines Channels auf 0 brächte, sollte dieselbe Plausibilitätsgrenze treffen
  und stattdessen einen Voll-Resync auslösen.
- **Set-Wechsel während laufender Subscription:** Die `object_id`-Subscription klebt am alten
  Set. Erkennung über eine zusätzliche `user.*`-Subscription (Chatterino-Muster) ist **live
  verifiziert** (Probe 3, Abschnitt 5): `user.update` liefert alte und neue Set-ID explizit
  (`connections[0]` → `emote_set`/`emote_set_id`), prompt bei beiden Wechselrichtungen.
  Alternativ — noch einfacher — erkennt der periodische Resync den Wechsel beim nächsten Tick
  (Worst-Case-Latenz = Resync-Takt). Empfehlung: `user.*` mitabonnieren (kostet 1 Subscription
  pro Channel) und beim Event neu subscriben + Voll-Resync des Channels auslösen.
- **Boot-Reihenfolge:** WS-Subscriptions erst nach `BootRecoveryGate.Completed` aufbauen (der
  Boot-Sync liefert ohnehin den frischen Vollstand; frühere Dispatches wären redundant bzw.
  kollidierten mit dem Boot-Sync).
- **Lehren aus S2-8/S2-1 direkt anwendbar:** kein fire-and-forget-Verbindungs-Loop (beobachteter
  Task + Anbindung an `WorkerHealthPublisher`), Reconnect-Verhalten gegen den echten
  Server-Vertrag verifizieren statt gegen Annahmen (die TwitchLib-„10-Reconnects"-Episode ist die
  Blaupause dafür, was sonst passiert).

---

## 7. Optionen mit Aufwand/Risiko

| Option | Aufwand | Nutzen | Risiko / Gegenargument |
|---|---|---|---|
| **A. Status quo behalten** (1-min-REST-Vollsync) | 0 | bewährt, einfach | Latenz nominell ≤60 s, real bis 10–30 min bei REST-Cache-Lag (Issue #81); Kosten skalieren linear mit Channels (N Requests/min) |
| **B. REST-Polling verbessern** (adaptiver Takt: aktive/offline Channels unterschiedlich; Jitter; ggf. GQL-Batch mehrerer User-Lookups) | ~0,5–1 Tag | weniger Requests bei vielen Channels | Latenz und Cache-Lag bleiben; ETag/If-Modified-Since-Support der 7TV-API ist unverifiziert (vor Umsetzung prüfen) |
| **C. WS zusätzlich zum REST-Resync** (empfohlen beim Ausbau) | ~1,5–3 Tage inkl. Tests + Live-Verifikation | Sekunden-Latenz für In-Set-Änderungen; REST-Takt auf ~5 min streckbar → weniger Requests trotz mehr Channels; umgeht den REST-Cache-Lag für Adds/Removes (Dispatch trägt die Emote-Daten im Payload) | Komplexität kehrt zurück (Reconnect, Subscription-Tracking, Delta-Parsing); Anforderungskatalog s. u.; Zustellqualität des `object_id`-Scopes in großen Channels nicht abschließend belegt (Lücke: LRU-Frage) |
| **D. WS statt REST** | — | — | **Ausgeschlossen.** Kein Resume/Replay, ~1-h-TTL, belegte Publish-Lücken (Issue #137) — ohne periodischen Vollabgleich driftet der Bestand garantiert |
| **E. Wechsel auf v4** | — | — | **Für Events nicht möglich** — v4 hat keinen Event-Kanal (`EmptySubscription`, kein v4-Endpoint am Event-Service). v4-GQL/REST-Migration ist ein separates Thema ohne Live-Update-Nutzen |

**Anforderungskatalog für Option C** (jeder Punkt adressiert einen konkreten damaligen Fehler
oder einen heutigen Server-Fakt): (1) Subscription nur `{object_id:<setId>}` (`emote_set.update`
oder `emote_set.*`); (2) Resubscribe **nach jedem empfangenen Hello** (DankChat-Muster) statt vor
dem Connect; (3) Heartbeat-Watchdog: Reconnect nach 3 ausbleibenden Intervallen; (4) op 4/7 →
proaktiver Reconnect, op 5/6 auf Info-/Warning-Level sichtbar, Close-Code 4009 (Already
Subscribed) nicht in eine Reconnect-Schleife laufen lassen; (5) TTL-Disconnect ~stündlich als
Normalfall behandeln: Resubscribe + Voll-Resync der betroffenen Channels als Gap-Filling;
(6) Parser auf `pushed`/`pulled`/`updated` mit `key=="emotes"`, gegen echte Live-Frames
verifiziert; (7) kein fire-and-forget: beobachteter Loop, Health-Anbindung; (8) Delta-Anwendung
unter `ChannelSyncGate`, Plausibilitätsgrenze nach S3-12-Vorbild; (9) `subscription_limit=500`
pro Verbindung → ab ~500 Sets Connection-Sharding (bei absehbarer Channel-Zahl irrelevant).

---

## 8. Empfehlung + offene Fragen

**Empfehlung:** Jetzt nichts ändern — der 1-Minuten-REST-Sync ist für die aktuelle Channel-Zahl
richtig dimensioniert und robust (S2-6/S3-12/ChannelSyncGate). Die WS-Wiedereinführung (Option C,
mit dem Anforderungskatalog aus Abschnitt 7) als eigenes Arbeitspaket **vor** dem größeren Ausbau
einplanen; sie ist technisch klar machbar — die damalige „Unzuverlässigkeit" war mit hoher
Wahrscheinlichkeit hausgemacht, und alle relevanten Drittclients fahren heute erfolgreich genau
diesen Weg. Als Vorstufe mit minimalem Aufwand: den kontrollierten Dispatch-Beweis führen (s. u.).

**Offene Fragen, die nur der Projekteigner entscheiden kann:**

1. **Wann lohnt Option C?** Vorschlag als Trigger: sobald mehr als ~30–50 Channels aktiv sind
   *oder* die Voting-/Purge-UX Sekunden-Frische braucht. Vorher ist der Zusatzaufwand schwer zu
   rechtfertigen.
2. ~~**Kontrollierter Dispatch-Beweis**~~ — **erledigt am 2026-07-30** (s. Abschnitt 5, Proben
   2 + 3): Adds/Removes auf Sets bis 442 Emotes sowie zwei Set-Wechsel kamen unmittelbar und im
   erwarteten Format an (`pushed`/`pulled` bzw. `user.update` mit alter+neuer Set-ID). Offen
   bleibt nur noch die HandOfBlood-Größenordnung (~900 Emotes, hohe Zuschauerzahl).
3. **Umgang mit dem REST-Cache-Lag (Issue #81):** Betrifft den Status quo *heute* — akzeptieren
   wir bis zu ~10–30 min Verzug im Worst Case, oder ist das allein schon ein Argument, Option C
   vorzuziehen?
4. **DECISIONS.md-Nachtrag:** Soll ein neuer, datierter Eintrag die „nachweislich
   unzuverlässig"-Begründung von 2026-07-25 auf diese Untersuchung verweisen lassen? (Konvention:
   kein rückwirkendes Umschreiben — ein Nachtrag wäre der saubere Weg, gehört aber in einen
   eigenen Commit mit Freigabe.)

**Verbleibende Erkenntnislücken** (ehrlich ausgewiesen, Details im Recherche-Artefakt):
7TV-Prod-Konfiguration (TTL/Limits) nur als Code-Defaults bekannt; ob der `object_id`-Fanout
derselben LRU-Verdrängung unterliegt wie der Presence-Fanout (bis 442 Emotes empirisch
unauffällig, ~900er-Sets ungemessen); Inhalt der uncommitteten
Test-Iterationen vom 2026-07-24/25 unwiederbringlich verloren; etwaige 7TV-Incidents im
Juli 2026 nicht prüfbar (kein Statuspage-/Discord-Archivzugriff).
