# ARCHITECTURE & SPECIFICATION: Emote Purge

> **Projektname:** Emote Purge  
> **Repository:** `emote-purge`  
> **Backend API:** `EmotePurge.Api` (.NET 10)  
> **Worker Bot:** `EmotePurge.Worker` (.NET 10)  
> **Message Broker / Cache:** Redis 7.2  
> **Datenbank:** PostgreSQL (EF Core)

---

## 1. Systemübersicht & Leitprinzipien

**Emote Purge** ist eine plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch.

### Architektur-Grundsätze:

1. **Single Source of Truth (PostgreSQL):** Die PostgreSQL-Datenbank speichert dauerhaft, welche Kanäle aktiv sind (`IsBotActive = true`), welche Emotes existieren und wie die Chat-Statistiken aussehen.
2. **Entkoppelte Echtzeit-Steuerung (Redis Pub/Sub):** Web API und Worker Service sind strikt getrennt. Betritt ein Streamer den Bot im Dashboard, schreibt die API dies in PostgreSQL und publisht ein Event via Redis (`channel:bot:commands`). Der Worker empfängt dieses Event in Echtzeit (< 5ms) und join den Chat.
3. **Automatisches Recovery bei Neustarts:** Beim Start liest der Worker-Service alle aktiven Kanäle aus PostgreSQL aus, stellt die Twitch-IRC-Chat-Verbindungen automatisch wieder her und synct 7TV für jeden Kanal einmalig voll. Danach übernimmt der hybride 7TV-Sync den laufenden Betrieb (s. A.3): EventAPI-WebSocket für Live-Deltas (hinter Feature-Flag) plus der periodische `SevenTvPeriodicResyncWorker` als Reconciliation.
4. **Zero-Knowledge für Schreib-Tokens:** 7TV-Access-Tokens mit Schreibrechten verbleiben _ausschließlich_ im Browser des Admins. Das Backend speichert oder verarbeitet zu keinem Zeitpunkt 7TV-Tokens.
5. **Dynamisches Rollen-Caching:** Rollen (Sub, VIP, Mod) werden nicht fest in der Datenbank abgelegt, sondern live über die Twitch API abgefragt und kurzzeitig in Redis / MemoryCache gecacht.
6. **High-Performance Analytics:** Der Chat-Bot verarbeitet hohe Chat-Volumen ressourcenschonend durch In-Memory-Pufferung (`ConcurrentDictionary`) und führt alle 30 Sekunden einen Batch-Flush in PostgreSQL aus.

---

## 2. Tech-Stack & Infrastructure

| Schicht            | Technologie            | Beschreibung & Zweck                                                                          |
| :----------------- | :--------------------- | :-------------------------------------------------------------------------------------------- |
| **Backend API**    | .NET 10 (ASP.NET Core) | REST API für Auth, Dashboard, Voting-Engine und Redis-Publisher.                              |
| **Worker Service** | .NET 10 Worker Service | Hintergrund-Bot für Twitch IRC Chat Listener & hybriden 7TV-Sync: EventAPI-WebSocket (Live-Deltas, Feature-Flag) + periodischer REST-Resync als Reconciliation (s. A.3). |
| **Message Broker** | Redis 7.2 (Alpine)     | Entkopplung von API & Worker via Pub/Sub; Caching für Twitch-Rollen. Pin auf 7.2, der letzten BSD-lizenzierten Redis-Version vor dem Lizenzwechsel auf RSALv2/SSPL ab 7.4. |
| **Datenbank**      | PostgreSQL 16+         | Relationale Persistenz für Channel, Emotes, Stats und VoteSessions via EF Core (Npgsql).      |
| **Frontend**       | Angular + Tailwind CSS | Single Page Application mit Virtual Scrolling (`CdkVirtualScrollViewport`) für 1.000+ Emotes. |
| **Deployment**     | Docker Compose         | Containerisierung von API, Worker Service und Redis mit persistenten Volumes.                 |

---

## 3. Inter-Service Kommunikation (Pub/Sub + Recovery)

[ Angular Dashboard ]
│
│ HTTP POST /api/channels/{name}/join
▼
[ ASP.NET Core API ] ───────── (1) Save Status ─────────► [ PostgreSQL ]
│ ▲
│ (2) PUBLISH "JOIN:montanablack" │
▼ │ (4) Recovery On Boot
[ Redis Pub/Sub Broker ] │
│ │
│ (3) Realtime Signal │
▼ │
[ .NET Worker Service ] ──────────────────────────────────────┘
│
├─► Twitch IRC: Join Channel
├─► 7TV EventAPI (WSS): Live-Dispatches (Feature-Flag, s. A.3)
└─► 7TV REST: Voll-Sync (initial + periodische Reconciliation, s. A.3)

**Rückkanal (Live-Updates, seit 2026-07-31):** Worker und Api publizieren dünne Benachrichtigungs-Events (`{type, channel, sessionId?}` — nie Daten) auf den Redis-Kanal `live:events` (Vertrag: `Core/Messaging/LiveEvents.cs`). Die Api ist dafür erstmals selbst Redis-Subscriber: `RedisLiveEventStream` (Infrastructure, Singleton, Lazy-Subscribe beim ersten Client) fächert die Events an offene **Server-Sent-Events**-Verbindungen auf (`GET /api/channels/{name}/live`, `GET /api/admin/live` — natives `TypedResults.ServerSentEvents`, kein SignalR). Der Browser refetcht daraufhin über die normalen REST-Endpoints (Notify-and-Refetch). Da jede Api-Replica selbst subscribed, funktioniert der Mechanismus ohne Backplane und ohne Sticky Sessions auch mit mehreren Replicas. Begründungen und Betriebsvertrag (Heartbeat 15 s, 10-min-Verbindungscap, Verbindungs-Limits statt Rate-Limit, Proxy-Anforderungen) im DECISIONS-Eintrag vom 2026-07-31.

Publizierende Stellen je Event-Typ (Stand 2026-08-01):

| Event | Publisher |
|---|---|
| `usage.flushed` | Worker: `UsageFlushWorker` nach erfolgreichem Flush |
| `vote.changed` | Api: `VoteSessionEndpoints` (Success-Arme von Vote-POST/DELETE) |
| `channel.synced` | Worker: `Worker` (JOIN-/RESYNC-Kommando **unconditional**, Boot-Recovery nur bei Änderung), `SevenTvPeriodicResyncWorker` und `SevenTvEventClient` (Delta + Follow-up-/Gap-Fill-Resyncs) **nur bei Änderung** · Api: `EmoteEndpoints` `POST .../emotes/sync-deleted`, wenn ≥1 Emote neu archiviert wurde |
| `worker.health` | Worker: `WorkerHealthPublisher` |

„Nur bei Änderung" heißt: `SevenTvSyncResult.HasChanges` bzw. `SevenTvDeltaOutcome.Applied` — s. DECISIONS-Eintrag vom 2026-08-01.

---

## 4. Modul-Spezifikationen

### Modul A: Twitch Chat Bot & Analytics Engine (Worker Service)

> **Umsetzungsstand:** A.1 (Grundfluss + Spam-Schutz/Emote-Matching), A.2 (In-Memory-Aggregator + Batch-Flush) und A.3 (7TV-Sync, hybrid: EventAPI-WebSocket + REST-Reconciliation — s. u.) sind vollständig implementiert. `EmotePurge.Worker` verbindet sich anonym/read-only per `TwitchLib.Client` (kein Bot-Account, kein OAuth-Token), joint/verlässt Channels auf Zuruf per Redis (`channel:bot:commands`, Messages `JOIN:<name>`/`LEAVE:<name>`) und beim Start automatisch alle `IsBotActive=true`-Channels aus Postgres (Boot-Recovery, Grundsatz 3). Jede empfangene Chat-Nachricht wird gegen die aktiven 7TV-Emotes des jeweiligen Channels gematcht (`IEmoteMatchCache`, `channelName → {EmoteName → Emote.Id}`) und Treffer max. 1x pro Nachricht in `IEmoteUsageCounter` gezählt (Spam-Schutz gegen Copypasta); ein separater `UsageFlushWorker`-Hosted-Service draint diesen Zähler alle 30 Sekunden und upserted die Counts über `IUsageStatFlushService` in `UsageStat`. Gesteuert über Minimal-API-Endpoints in `EmotePurge.Api`: `POST /api/channels/{channelName}/join` upsertet den `Channel` in Postgres (Grundsatz 1) und published `JOIN:<name>`; `DELETE /api/channels/{channelName}` löscht die Zeile hart (kein reines Deaktivieren — siehe CLAUDE.md-Entscheidungslog) und published `LEAVE:<name>`; `GET /api/channels/{channelName}/usage-stats` liefert die aktuellen `UsageStat`-Zeilen zum Debuggen; `GET /api/channels/{channelName}/usage-stats/totals?from=&to=` liefert pro Emote die über einen frei wählbaren Zeitraum aufsummierte `UseCount` (Basis für das Usage-Stats-Dashboard sowie die Manager-Kontextspalte in den Voting-Ergebnissen von Modul C — seit 2026-08-01 nicht mehr Bestandteil des Scores, siehe docs/DECISIONS.md). Bei jedem Join wird das aktive 7TV-Emote-Set aufgelöst und vollständig nach Postgres synchronisiert (`ISevenTvSyncService`, refresht dabei auch `IEmoteMatchCache`); danach hält der hybride 7TV-Sync den Bestand aktuell — Live-Deltas über die EventAPI-WebSocket (`SevenTvEventWorker`/`SevenTvEventClient`, Feature-Flag `SevenTv:EventApi:Enabled`) plus ein `SevenTvPeriodicResyncWorker`, der denselben Voll-Sync für alle aktiven Channels periodisch als Reconciliation wiederholt (Takt `SevenTv:ResyncIntervalSeconds`, Default 60 s — s. A.3).

#### A.1 IRC Chat Listener & Spam-Schutz

- Verbindet sich via `TwitchLib.Client` mit allen aktiven Twitch-Kanälen.
- Nachrichten werden am Leerzeichen gespalten (`string.Split(' ')`) und gegen ein `HashSet<string>` abgeglichen.
- **Spam-Schutz:** Jedes vorkommende Emote wird **maximal 1-mal pro Chat-Nachricht** gezählt (verhindert Verzerrung durch Spam-Copypastas).

#### A.2 In-Memory Aggregator & Batch Flush

- Counts werden in einem `ConcurrentDictionary<string, int>` (Key: `EmoteId`) hochgezählt.
- Ein Timer führt alle **30 Sekunden** einen Batch-Flush in die PostgreSQL-Datenbank aus.

#### A.3 7TV Sync Engine (hybrid: EventAPI-WebSocket-Live-Deltas + periodische REST-Reconciliation)

> **Abweichung von der urspr. Spezifikation — Historie:** Ursprünglich implementiert als **eine gemeinsame** WebSocket-Verbindung zu `wss://events.7tv.io/v3` (`ISevenTvEventClient`/`SevenTvEventClient`) mit `emote_set.update`-Subscriptions je Kanal auf einer Connection. Am 2026-07-24/25 über mehrere Live-Tests (Channels `vassilly`, `sensitron`, u. a.) systematisch untersucht: Dispatches kamen nachweislich **nicht zuverlässig** an — teils mehrminütige Verzögerung, teils gar nicht (z. B. ein live hinzugefügtes Emote "REITEN", das nie per Dispatch ankam, obwohl ein anderer 7TV-Client (DankChat) das Update korrekt erhielt). Die Subscriptions selbst waren serverseitig korrekt registriert (per `Ack`-Frame bestätigt, `subscription_limit` weit unausgeschöpft). Eine Analyse des offiziellen 7TV-Browser-Extension-Quellcodes (github.com/SevenTV/Extension, `src/worker/worker.http.ts`) ergab zwei Abweichungen — Wildcard-Subscription-Typ `emote_set.*` statt `emote_set.update`, plus eine zusätzliche channel-scoped Subscription (`condition: {ctx: "channel", platform: "TWITCH", id: <TwitchChannelId>}`, überlebt Set-Wechsel) — beide testweise nachgerüstet, ohne die Zuverlässigkeit messbar zu verbessern. Da der REST-Vollsync (`ISevenTvSyncService.SyncChannelAsync`) in jedem Test zuverlässig war, wurde die komplette WebSocket-Logik entfernt und durch einen periodischen REST-Resync ersetzt (**Entscheidung**, s. docs/DECISIONS.md für den vollständigen Verlauf).
>
> **Nachtrag 2026-07-30:** Die Re-Untersuchung [docs/Untersuchung-7TV-WebSocket-2026-07-30.md](docs/Untersuchung-7TV-WebSocket-2026-07-30.md) hat die Attribution „nachweislich nicht zuverlässig seitens 7TV" **widerlegt**: Ursache waren zwei eigene Implementierungsfehler (Resubscribe vor dem Verbindungsaufbau; Parser las `added`/`removed` statt des echten Wire-Formats `pushed`/`pulled`), und die channel-scoped Subscription ist serverseitig ein Presence-Scope, der Channel-Set-Updates strukturell nicht liefert. Der WebSocket wurde daraufhin als **Ergänzung** wieder eingeführt (Eintrag „7TV-EventAPI-WebSocket wieder eingeführt" in docs/DECISIONS.md): `SevenTvEventWorker`/`SevenTvEventClient` liefern Live-Deltas (`emote_set.*` + `user.*`, jeweils `{object_id}`), der periodische REST-Resync bleibt als zwingende Reconciliation bestehen — die EventAPI hat kein Resume/Replay und trennt jede Verbindung nach ~1 h TTL. Feature-Flag `SevenTv:EventApi:Enabled` (Default aus), Resync-Takt `SevenTv:ResyncIntervalSeconds` (Default 60 s, bei bewährtem WS-Betrieb manuell streckbar).
>
> **API-Version:** 7TV v3 (REST + GQL + EventAPI), nicht v4. v4 existiert als GraphQL-API, hat aber keinen Event-Kanal (kein `events.7tv.io/v4`, GQL-Schema ohne Subscriptions) — die v3-EventAPI ist der einzige Live-Weg und nicht deprecated (Stand 2026-07-30).
>
> **Auflösung Channel → Emote-Set:** 7TVs REST-Endpoint (`/v3/users/twitch/{twitchUserId}`) akzeptiert nur die numerische Twitch-User-ID, nicht den Usernamen. Da bewusst keine Twitch-Helix-API/App-Registrierung genutzt wird, löst `ISevenTvApiClient` den Twitch-Usernamen stattdessen über 7TVs eigene GraphQL-Nutzersuche (`/v3/gql`, `users(query: ...)`, gefiltert auf exakten Treffer in `connections[]` mit `platform=="TWITCH"`) auf. Das befüllt `Channel.TwitchChannelId` damit bereits jetzt (nicht erst durch das künftige Modul B) — semantisch dieselbe numerische ID, nur ein anderer Befüllungsweg.

- Bei jedem Join: einmaliger Voll-Sync (`SyncChannelAsync`) — löst Twitch-Username → 7TV-Emote-Set auf, reconciled alle `Emote`-Zeilen (Add/Update/Archive) gegen Postgres, refresht `IEmoteMatchCache` und meldet Set + 7TV-User-ID als gewünschte EventAPI-Subscriptions an (`SevenTvSubscriptionRegistry`, Desired-State-first).
- **Live-Pfad (Feature-Flag):** `SevenTvEventWorker` hält genau eine EventAPI-Verbindung (`SevenTvEventClient`); Subscriptions werden nach **jedem** Hello aus der Registry neu aufgebaut (dedupliziert je `(type, object_id)` — geteilte Sets ergeben eine Subscription), Dispatches streng sequenziell verarbeitet und als Deltas per `ApplyEmoteSetUpdateAsync` unter dem `ChannelSyncGate` angewendet (danach voller `IEmoteMatchCache`-Reload, kein inkrementelles Cache-Patchen). `user.update` erkennt Set-Wechsel; Heartbeat-Watchdog (3× Intervall), op-4/7-Reconnects und die ~1-h-Server-TTL sind Normalfälle mit Gap-Filling-Vollsync nach jedem Reconnect.
- `SevenTvPeriodicResyncWorker` (eigener `BackgroundService`) wiederholt denselben Voll-Sync für **alle** `IsBotActive=true`-Channels periodisch (Default 60 s) — als Reconciliation zwingend neben dem WebSocket (kein Resume/Replay bei 7TV), fängt Set-/Account-Wechsel und verpasste Dispatches ab und konvergiert die Subscriptions (`EnsureSubscribed` je Tick). Ein fehlschlagender Sync für einen Channel wird geloggt und übersprungen, ohne die anderen Channels oder den Worker-Host zu beeinträchtigen.
- Kosten: ein 7TV-REST-Request pro aktivem Channel und Resync-Tick plus eine stehende WebSocket-Verbindung — bei der aktuellen/absehbaren Channel-Zahl vernachlässigbar, kein beobachtetes Rate-Limiting. Health: `GET /api/worker/health` liefert den EventAPI-Zustand als `sevenTv`-Unterobjekt (`disabled/disconnected/stale/connected`, Staleness an Heartbeat-Frames gemessen).

### Modul B: Auth & Dynamisches Rollen-System

#### B.1 Authentication

- Twitch OAuth2 Flow via Web API: `/api/auth/twitch/login` und `/api/auth/twitch/callback`.
- Fragt nur die Grund-Identität ab (`user:read:email` oder Basis-Profil).

#### B.2 Live-Rollenprüfung

- Twitch-Rollen werden nicht persistent in PostgreSQL gespeichert.
- Beim Vote-Request prüft das Backend die Rollen des Users live via Twitch Helix API.
- Ergebnisse werden für 5–15 Minuten in Redis gecacht, um Rate-Limits zu schonen.

### Modul C: Voting Engine & Netto-Vote-Score

- Voting-Ort: Das Voting findet ausschließlich im Web-Dashboard statt (nicht im Chat).
- Parallele Sessions: Erlaubt flexible Votings (z. B. "Monats-Aufräumaktion Juli").
- Zielgruppen-Einschränkung (`AllowedRoles`): Festlegbar, wer abstimmen darf (Alle, Subs, VIPs, Mods).
- **Emote-Subset pro Session (seit 2026-08-01):** Der Ersteller kann der Session einen expliziten Wahlzettel mitgeben (`VoteSessionEmote`-Join-Tabelle, `emoteIds` beim Erstellen). Ohne Auswahl deckt die Session dynamisch alle nicht-archivierten Channel-Emotes ab (Bestandsverhalten, keine Join-Rows). Ein kuratierter Wahlzettel ist ab Erstellung fix; fliegt ein Mitglied mid-session aus dem 7TV-Set, bleibt es mit Badge sichtbar (Votes erhalten), weitere Votes darauf sind gesperrt.
- Der Score (seit 2026-08-01, vorher `f(Chat-Nutzung) + (Keep − Delete)`):

$$\text{Score} = \text{Keep-Votes} - \text{Delete-Votes}$$

  Chat-Nutzung fließt **nicht** mehr ein — die Mods verbrauchen die Usage-Daten bereits beim Kuratieren des Wahlzettels, und normalisierte 0–100-Usage-Punkte dominierten rohe ±N-Votes. Usage bleibt Managern als Kontextspalte erhalten (`TotalUseCount`, für Nicht-Manager `null`). Ergebnisse sortieren aufsteigend (Delete-Kandidaten zuerst), Tiebreaker: mehr Gesamtstimmen. `VoterCount` (distinct Voter) qualifiziert dünne Beteiligung in der UI.

### Modul D: Angular Dashboard (Übersicht, Usage-Stats, Voting-UI, Mass Delete Engine)

> **Umsetzungsstand:** Vollständig implementiert (2026-07-26) — vollständige Details/Gotchas im CLAUDE.md-Entscheidungslog, hier nur die konkretisierte Spezifikation.

- **Seiten/Routen (`web/src/app/app.routes.ts`):** `/welcome` (öffentliche, guard-lose Landing-Page — Einstieg für anonyme Besucher, z. B. über einen geteilten Link), `/login`, `/` (Übersicht, `homeGuard` — anonyme Besucher landen auf `/welcome` statt dem Login-Formular; eingeloggt: die eigenen getrackten **und** ungetrackten moderierten Channels, `GET /api/channels/mine` — die frühere Admin-Sektion „alle getrackten Channels + Join-Formular" auf derselben Seite wurde am 2026-07-31 zugunsten des `/admin`-Bereichs entfernt), `/admin/*` (globaler Admin-Bereich, `adminGuard`: Monitoring, Channel-Liste, Audit-Log), `/my-votings` (kanalübergreifende eigene Stimmhistorie, `authGuard` — bewusst Geschwister-Route der Channel-Workspace-Routen, nicht darunter verschachtelt, da sie an keinen einzelnen `channelName`-Routenwert gebunden ist), `/channels/:channelName/usage-stats` (`usageStatsAccessGuard` — echte Berechtigung fürs Channel, nicht nur Login: Admin/Broadcaster/Live-Moderator oder 7TV-Editor), `/channels/:channelName/vote-sessions` (Liste, `authGuard` — nur Login-Pflicht, die Liste selbst hat keine sitzungsspezifische Rolleneinschränkung), `/channels/:channelName/vote-sessions/:sessionId` (Detail, `voteSessionAccessGuard` — Login **und** echte Zugehörigkeit zur Zielgruppe dieser konkreten Session). **Anonyme Share-Links wurden am 2026-07-27 entfernt** (explizite Nutzerentscheidung, reversiert eine frühere Design-Entscheidung): Voting-Seiten waren ursprünglich bewusst ohne Login-Zwang erreichbar, verlangen jetzt aber durchgängig Login — s. CLAUDE.md-Entscheidungslog für den vollständigen Verlauf.
- **Grid statt Liste:** Bei bis zu ~1.000 Emoten pro Channel wäre eine Ein-Spalten-Liste unpraktikabel lang zum Scrollen. Usage-Stats und Voting-Ergebnisse rendern die Emotes daher als responsives Grid (2–8 Spalten je Fensterbreite) — `CdkVirtualScrollViewport` virtualisiert dabei **Zeilen** von je mehreren Karten (Row-Chunking), nicht einzelne Emotes; Spaltenzahl reagiert live auf Resize.
- Mehrfachauswahl (Checkbox + Shift-Klick-Bereichsauswahl) auf beiden Grid-Seiten identisch.
- Virtual Scrolling: Nutzung von Angular CDK `CdkVirtualScrollViewport` für flüssiges Rendering.
- Direct GraphQL Execution: Schreib-Tokens verbleiben lokal im Browser (`sessionStorage`).
- Batch Delete Queue: Das Frontend schickt beim Löschbefehl die Mutation direkt vom Browser an `https://7tv.io/v3/gql`:

```graphql
mutation RemoveEmote($setId: ObjectID!, $emoteId: ObjectID!) {
  emoteSet(id: $setId) {
    emotes(id: $emoteId, action: REMOVE) {
      id
    }
  }
}
```

- Rate-Limiting: Sequenzielle Ausführung mit ~275 ms Verzögerung zwischen Requests.
- **Backend-Sync — Abweichung von der urspr. Spezifikation:** Das Frontend meldet gelöschte IDs an die C#-API über `POST /api/channels/{channelName}/emotes/sync-deleted` (channel-scoped), nicht den ursprünglich skizzierten globalen Pfad `POST /api/emotes/sync-deleted` — konsistent mit jedem anderen channel-bezogenen Endpoint. Route-Gruppe hängt hinter `UsageStatsAccessAuthorizationFilter` (nicht `ChannelManagementAuthorizationFilter` — 7TV-Editoren des Channels dürfen ebenfalls archivieren, s. dessen Klassenkommentar für die aktuelle Endpoint-Liste). Markiert die betroffenen `Emote`-Zeilen als `IsArchived = true` (Soft-Archive, kein Hard-Delete — s. CLAUDE.md-Entscheidungslog), der 1-Minuten-`SevenTvPeriodicResyncWorker` bleibt das eigentliche Sicherheitsnetz.
- **Voting-UI:** Daumen-hoch/-runter pro Emote (Keep/Delete), eigener Vote wird hervorgehoben (`MyVote`, in den Ergebnissen mitgeliefert — seit der Login-Pflicht vom 2026-07-27 immer ein echter Nutzer, kein anonymer `null`-Fall mehr); Session-Erstellung/-Beendigung/-Löschung nur für Manager sichtbar (`ChannelManagementAuthorizationFilter`), Ergebnis-Ansicht hinter `VoteAudienceFilter` (Login + Zugehörigkeit zur Zielgruppe der Session, auch nach Session-Ende weiterhin sichtbar für das ursprüngliche Zielpublikum). **Zwei Erstell-Einstiege (seit 2026-08-01):** das Inline-Formular auf der Voting-Liste (erzeugt „ganzes Set"-Sessions) und „Zur Abstimmung stellen" aus der Mehrfachauswahl des Usage-Stats-Grids (`CreateVoteSessionDialog`, übergibt die Auswahl als festen Wahlzettel).
- **Internationalisierung (i18n):** Transloco (`@jsverse/transloco`), zwei Sprachen (`de`/`en`), Locale-Dateien unter `web/public/i18n/{de,en}.json`. `web/src/app/core/i18n/language.service.ts` (`LanguageService`) hält die aktive Sprache als Signal, persistiert die Wahl in `localStorage` und fällt ohne gespeicherte Präferenz auf die Browsersprache bzw. Deutsch zurück (primäre Zielgruppe). Umschaltung zur Laufzeit ohne Reload.
- **Stabiler Fehlercode-Vertrag:** Die Api liefert bei Fehlern ausschließlich sprachneutrale Codes (`{ errorCode = "..." }`), nie fertigen Text — übersetzt wird genau einmal im Frontend. Kette: `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (Konstanten) → `web/src/app/core/i18n/api-error.ts` (`apiErrorTranslationKey`, mappt `errorCode` auf einen `errors.api.<code>`-Übersetzungsschlüssel, mit Status-Code-Fallback für Antworten ohne Body, z. B. ein blankes `Forbid()`) → `errors.api.*`-Einträge in beiden Locale-Dateien. Der Vertrag lebt bewusst manuell synchron in diesen drei Stellen (kein Generator); `web/src/app/core/i18n/api-error.spec.ts` gleicht die bekannten Codes gegen beide Locale-Dateien ab.
- **Pagination:** `PagedResult<T>` (`src/EmotePurge.Core/Services/PagedResult.cs`, `record` mit `Items`/`Page`/`PageSize`/`TotalCount`/berechnetem `TotalPages`) als generisches Paging-Envelope für Listen-Endpoints (u. a. Vote-Session-Listen). `web/src/app/shared/pagination/pager.ts` (`Pager`-Komponente) rendert Vor/Zurück + „Seite X von Y" darüber, wiederverwendet auf allen paginierten Listenseiten.

## 5. Datenbankmodell (Entity Framework Core Schema)

> **Abweichung von der urspr. Spezifikation (`Emote`):** Die 7TV-ObjectID ist **nicht** mehr der Primary Key, sondern liegt in `SevenTvEmoteId`. Grund: Ein 7TV-Emote kann gleichzeitig in mehreren Channels aktiv sein; da `Emote` aber pro Channel eine eigene Zeile ist (`ChannelId`-Spalte), hätte die 7TV-ID als globaler PK bei geteilten Emotes zu einer Primary-Key-Kollision geführt. Stattdessen ist `Id` ein interner Guid-PK, und ein Unique-Index auf `(ChannelId, SevenTvEmoteId)` stellt die Eindeutigkeit pro Channel sicher. `UsageStat.EmoteId` referenziert diesen internen PK.
>
> Zusätzlich hat `UsageStat` einen Unique-Index auf `(EmoteId, Date)`, damit der 30-Sekunden-Batch-Flush pro Emote und Tag genau eine aggregierte Zeile pflegt statt vieler Einzelzeilen. `Date` ist als `DateOnly`/Postgres `date` typisiert (nicht `DateTime`/`timestamptz`) — macht den "UTC-Kalendertag"-Charakter der Spalte typsicher statt nur per Kommentar, und der Index trägt `UseCount` als Include-Spalte, damit Zeitraum-Summenabfragen (`SUM(UseCount) WHERE Date BETWEEN from AND to`) als Index-Only-Scan laufen. Diese Tages-Granularität ist bewusst die Grundlage für flexible Dashboard-Zeiträume (Tag/Woche/Monat/Custom) — ein Zeitraum ist einfach eine Summe über die passenden Tages-Zeilen, keine feinere Granularität oder Rollup-Tabelle nötig (siehe CLAUDE.md-Entscheidungslog).
>
> **Alle sechs Entitäten sind implementiert:** `Channel`, `Emote`, `UsageStat`, `User`, `VoteSession`, `Vote` (plus `AllowedRoles`/`VoteType` als Enums) liegen vollständig unter `src/EmotePurge.Core/Entities/` und sind über Migrationen angewendet — der bis 2026-07-25 gültige Stand ("nur Modul 1 implementiert") ist überholt. Zusätzlich liegt dort `ChannelName.cs` — keine Entität, sondern eine statische Normalisierungs-Hilfsklasse (`Normalize(string) => value.Trim().ToLowerInvariant()`), die die stille Invariante "`Channel.ChannelName` ist in der DB immer lowercase/getrimmt" an einer Stelle festhält.
>
> **Zweite Abweichung (`Channel.TwitchChannelId`):** ist `string?` (nullable) statt non-nullable — da die Spalte einen Unique-Index hat, hätte ein non-nullable Default (`""`) beim zweiten angelegten Channel einen Unique-Constraint-Verstoß ausgelöst (leerer String zählt für Unique-Indizes, NULL nicht). Bleibt `null`, bis sie aufgelöst werden kann. Wird inzwischen (Modul A.3) beim Channel-Join über 7TVs GraphQL-Nutzersuche befüllt, nicht erst durch das künftige Modul B (Twitch-OAuth) — siehe Modul-A.3-Abschnitt.

Die vollständigen Feldlisten für alle sechs Entitäten stehen direkt in `src/EmotePurge.Core/Entities/` (`Channel.cs`, `Emote.cs`, `UsageStat.cs`, `User.cs`, `VoteSession.cs`, `Vote.cs`) — bewusst nicht hier gespiegelt, das war genau der Grund, warum dieser Abschnitt zuletzt veraltete. Was man beim Lesen der Datenbank kennen muss, sind die beiden oben beschriebenen Invarianten:

```csharp
public class Emote
{
    public string Id { get; set; } = Guid.NewGuid().ToString(); // interner PK, NICHT die 7TV-ObjectID

    // 7TV ObjectID (24-hex string). Eindeutig nur pro Channel via Unique-Index
    // auf (ChannelId, SevenTvEmoteId) — dasselbe 7TV-Emote kann in mehreren
    // Channels gleichzeitig aktiv sein.
    public string SevenTvEmoteId { get; set; } = string.Empty;
    // ... ChannelId, Name, ImageUrl, IsArchived, LastSyncedAt, Channel, UsageStats
}

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty; // FK auf Emote.Id (interner PK)

    // UTC-Kalendertag, Postgres `date` (nicht `timestamptz`) — Unique-Index (Include UseCount)
    // zusammen mit EmoteId.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }
    // ... Emote
}
```

## 6. Docker-Topologie

> **Abweichungen von der urspr. Spezifikation:** `redis:7-alpine` → `redis:7.2-alpine` (Lizenz-Grund, siehe Abschnitt 2). `depends_on` nutzt `condition: service_healthy` statt einer einfachen Liste, dazu Healthchecks für `postgres`/`redis` — ohne das starten `api`/`worker` sonst, bevor die Datenbank überhaupt Verbindungen annimmt, und crashen beim ersten Zugriff. Die konkreten Dockerfiles liegen unter `src/EmotePurge.Api/Dockerfile` und `src/EmotePurge.Worker/Dockerfile` (Multi-Stage-Build: SDK-Image für Build/Publish, schlankes Runtime-Image für `final`, bei der Api zusätzlich eine `web-build`-Node-Stage für den Angular-Build, s. Modul D).

Es gibt zwei Compose-Dateien, kein YAML mehr hier gespiegelt — die eingebettete Kopie war genau deshalb veraltet, weil sie eine Kopie war. Für den vollständigen, aktuellen Inhalt gilt jeweils die Datei selbst als Quelle der Wahrheit.

### 6a. Lokal (`docker-compose.yml`)

Für lokale Entwicklung/Tests: `docker compose up -d --build` baut `api`/`worker` aus dem Repo-Stand (`build:`-Sektion, kein vorgebautes Image). Gestartet mit `redis`, `postgres`, `api`, `worker` im gemeinsamen `emotepurge-network`-Bridge-Netz.

### 6b. Produktion (`docker-compose.prod.yml` + `.github/workflows/publish.yml`)

Läuft auf einem VPS neben einer bestehenden, unabhängigen App, als Portainer-Stack importiert (`docker-compose.prod.yml` ist die Datei, die dafür auf GitHub liegt). `.github/workflows/publish.yml` baut bei jedem Push auf `main` (nach grünem `test`- und `test-web`-Job) beide Images und pusht sie nach `ghcr.io/sensitron/emotepurge-{api,worker}:latest` (zusätzlich mit dem Commit-SHA getaggt); ein Redeploy des Portainer-Stacks zieht `:latest` neu.

**Unterschiede lokal vs. Produktion:**

| Aspekt | Lokal (`docker-compose.yml`) | Produktion (`docker-compose.prod.yml`) |
| :--- | :--- | :--- |
| `api`/`worker`-Images | `build:` aus dem lokalen Repo-Stand | `image: ghcr.io/sensitron/emotepurge-{api,worker}:latest`, per CI gebaut |
| Host-Port `api` | `127.0.0.1:8080:8080` | `127.0.0.1:4300:8080` — Port 8080 ist auf dem VPS bereits von der anderen App belegt |
| Host-Port `postgres` | `127.0.0.1:5432:5432` | `127.0.0.1:5433:5432` — analog, eigene isolierte Postgres-Instanz statt Mitnutzung der anderen App |
| Host-Port `redis` | `127.0.0.1:6379:6379` | `127.0.0.1:6380:6379` |
| TLS/Reverse Proxy | keiner, direkter HTTP-Zugriff auf `localhost:8080` | Ein host-nativer (nicht containerisierter) Reverse Proxy vor dem Loopback-Port terminiert TLS für `emotepurge.app` und setzt `X-Forwarded-Proto`/`-For`; `ForwardedHeadersMiddleware` in `Program.cs` vertraut dem mit leerem `KnownIPNetworks`/`KnownProxies`, da der Container ausschließlich über den lokal gebundenen Port erreichbar ist |
| `Auth:Twitch:RedirectUri` | `http://localhost:8080/api/auth/twitch/callback` | `https://emotepurge.app/api/auth/twitch/callback` |
| `dataprotection-keys`-Volume | vorhanden — bewusste Parität zu Prod, damit dieser Pfad lokal überhaupt getestet wird | vorhanden — ohne persistierten Schlüsselring würde jeder Container-Neustart alle eingeloggten Nutzer aus der Cookie-Session werfen |

Bewusst **nicht** unterschiedlich: `redis` läuft in beiden Dateien mit `--maxmemory 256mb --maxmemory-policy allkeys-lru`. Redis trägt hier neben dem Rollen-/Health-Cache auch `channel:bot:commands`; ein unkontrolliert wachsender Redis würde also die Bot-Steuerung mitreißen, und ein lokal unlimitierter Redis hätte genau den Pfad ungetestet gelassen, auf den es ankommt.

Konfiguration erfolgt in beiden Fällen über eine `.env`-Datei am Repo-Root (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `TWITCH_CLIENT_ID`, `TWITCH_CLIENT_SECRET`) — Vorlage in `.env.example`, `.env` selbst ist git-ignored.

## 7. Lokale Entwicklung & Debugging (Dev Containers)

Für das Debuggen von `EmotePurge.Api`/`EmotePurge.Worker` direkt in VS Code (Breakpoints, F5) wird das offizielle **Dev Containers**-Modell verwendet, nicht das Attachen an ein produktionsnahes, vorgebautes Image:

- `.devcontainer/devcontainer.json` + `.devcontainer/docker-compose.yml` definieren einen eigenen `devcontainer`-Service (SDK-Image, Repo als Volume gemountet) im selben Compose-Netzwerk wie `postgres`/`redis`. Die `api`/`worker`-Services aus dem Root-`docker-compose.yml` werden dabei bewusst **nicht** gestartet (`runServices: ["postgres", "redis"]`) — im Dev Container läuft die App direkt über den .NET-Debugger, nicht als vorgebautes Docker-Image.
- Verbindungsstrings (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) zeigen im Dev Container automatisch auf die Compose-Hostnamen `postgres`/`redis`; außerhalb des Containers (normaler Host-Debug) greifen dieselben `.vscode/launch.json`-Konfigurationen stattdessen auf `localhost` aus `appsettings.json` zurück (sofern `docker compose up postgres redis` lokal läuft).
- `.vscode/launch.json` enthält `coreclr`-Launch-Configs `Api` und `Worker` sowie eine Compound-Config `Api + Worker` zum gemeinsamen Debuggen beider Prozesse; `.vscode/tasks.json` baut jeweils vorher (`build-api`/`build-worker`).
- Die Api bindet dabei explizit auf `http://0.0.0.0:8080` (`ASPNETCORE_URLS`), passend zum Port, den auch der produktive `api`-Container exponiert — HTTPS-Dev-Zertifikate werden im Linux-Container bewusst nicht eingerichtet.
