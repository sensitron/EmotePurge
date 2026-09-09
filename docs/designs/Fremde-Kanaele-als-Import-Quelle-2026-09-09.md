# Design: Fremde Kanäle als Import-Quelle

Erzeugt von `/office-hours` am 2026-09-09
Branch: `main`
Repo: sensitron/EmotePurge
Status: APPROVED (2026-09-09, durch den Betreiber)
Modus: Builder
Review: 3 adversariale Runden, 22 Befunde gefunden und behoben, Endnote 9/10

## Problem

Die Einlese-Seite akzeptiert seit #72/#91 eine Datei mit einer Emote-Liste. Die
Erzeuger-Seite kann diese Datei aber nur für Kanäle schreiben, in denen der Nutzer eine
Rolle hat **und** die unser Worker trackt. Wer Emotes aus einem fremden Kanal übernehmen
will, hat keinen Weg an die Liste — obwohl die Daten bei 7TV öffentlich sind.

Heute gibt es genau einen Weg, und nur für Admins: einen beliebigen Kanal joinen und
danach dessen Usage-Stats-Seite exportieren. Das kostet einen von 100 Twitch-Chatroom-
Plätzen, erzeugt dauerhaftes Tracking und schiebt dem laufenden Messfenster (Epic #118,
bis 2026-10-07) einen Kanal unter, der nicht hineingehört.

**Abgrenzung:** Fremd ist die **Quelle**. Das **Ziel** bleibt ein Kanal, in dem der Nutzer
7TV-Schreibrechte hat und der von uns getrackt wird — die Zielauswahl speist sich aus
`listMine()`, und Diff sowie Kapazität kommen aus `/api/channels/{ziel}/emotes*`, einer
Gruppe, die für ungetrackte Kanäle 404 liefert. Ein Import in ein völlig fremdes Set ist
ausdrücklich nicht Gegenstand dieses Dokuments.

## Was das Ding besonders macht

Drei Werkzeuge kopieren heute 7TV-Sets, und alle drei sind blinde Rohre:

- **merge7tv.lesh.me** — nimmt zwei Set-IDs und einen 7TV-Token. Der verlinkte Quellcode
  `github.com/derlesh/merge7tv` gibt 404, die GitHub-Suche nach `Merge7TV` findet null
  Repos, der Account hat 43 öffentliche und keins davon. Im gesamten Markup kein Treffer
  für `trend`, `popular`, `rank` oder `usage`.
- **Voltikors Python-Gist** — Kommandozeile, Alles-oder-nichts, `time.sleep(30)` gegen
  Rate-Limits.
- **7TVs eigenes Merge** — offener Bug [SevenTV/API#253](https://github.com/SevenTV/API/issues/253):
  Emotes verschwinden, andere tauchen doppelt auf.

Keins davon zeigt, was in dem Set eigentlich drin ist, und keins erlaubt Einzelauswahl.
Emote Purge ist das einzige Produkt, dessen These bereits „nicht jedes Emote verdient es
zu bleiben" lautet. Dieselbe These auf das **Hinzufügen** angewandt ist der unbesetzte
Platz: auswählen statt schlucken, und dabei sehen, welche Emotes netzwerkweit bekannt
sind und welche fremde Insider-Witze.

## Randbedingungen

- **Messfenster Epic #118 bis 2026-10-07:** kein Worker-Code, keine Ausweitung über ~20
  gejointe Kanäle. Ein Feature, das nur 7TV per REST/GQL liest, ist messungsneutral und
  sofort baubar.
- **Twitch-Decke:** 100 gleichzeitig gejointe Chatrooms pro Account, aktuell ~15 belegt.
- **Kein 7TV-Caching im Bestand** (`ServiceCollectionExtensions.cs:71-77`). Gecacht wird
  nur `modlist:` (Helix) und `7tveditor:` (Rollenstatus), nie eine Emote-Liste.
- **Bestehende Rate-Limit-Policies modellieren die Kosten beim Drittanbieter
  ausdrücklich nicht** (#33).

## Im Code verifizierte Ausgangslage

| Befund | Beleg |
|---|---|
| 7TV-Schreibzugriff läuft komplett im Browser: Endpunkt `https://7tv.io/v3/gql` (`:24`), POST (`:354`), `Authorization: Bearer` (`:358`). Der Token liegt im `sessionStorage`, von Hand aus den DevTools kopiert. Unser Backend sieht ihn nie. | `web/src/app/core/seven-tv/seven-tv-run-engine.ts:24,354,358`; `seven-tv-token.service.ts:6-19` |
| Zielset-Rechte setzt 7TV durch. Wir erkennen **zwei** Fragmente (`insufficient privileges`, `missing permission`, `:34`) **plus** HTTP 401/403 (`:46`). | `web/src/app/core/seven-tv/seven-tv-import.service.ts:31-51` |
| Ein 7TV-Login per Redirect ist unmöglich — 7TVs Allowlist ist fest verdrahtet. | `docs/DECISIONS.md:3280` |
| Emote-Liste kommt heute aus unserer DB, nicht von 7TV; ungetrackter Kanal ⇒ `null` ⇒ 404. | `EmoteEndpoints.cs:42-43` |
| `IsGlobalAdmin` (`:20`) durchbricht `CanManageChannelAsync` (`:17-32`) und darüber `CanViewUsageStatsAsync` (`:36`). Das ist der heutige Admin-Weg. | `ChannelAccessService.cs:17-39` |
| „7TV für einen ungetrackten Kanal lesen" existiert bereits — **aber rollengebunden**: Tier 3 fragt nur für Kanäle, die der Aufrufer auf Twitch moderiert. Kein Präzedenzfall für einen beliebigen Fremdkanal. | `EmoteSetOwnershipService.cs:54-55` |
| Der Auswahl-**Mechanismus** `ListSelection<T>` ist generisch. Das auswählbare **Raster** ist es nicht: es steht inline in `usage-stats-page.html` (~485-560), hält `EmoteUsageTotal`-Zeilen und wird mit `emote.emoteId` (DB-interne Guid) instanziiert. Bänder, Sparkline und Füllbalken leiten sich aus `totalUseCount` ab. | `web/src/app/shared/selection/list-selection.ts`; `usage-stats-page.ts:516`; `mass-delete-panel.ts:174` („the host page owns the ListSelection") |
| Der Import-Auslöser sitzt auf der Usage-Stats-Seite **des eigenen** Kanals. | `usage-stats-page.html:54` |
| Alle drei Export-Aufrufstellen sitzen in rollengeschützten Kanalseiten. | `usage-stats-page.ts:1261`, `vote-session-detail-page.ts:587`, `mass-delete-panel.ts:314` |
| `POST /sync-imported` nimmt `SourceKind` aus einer **geschlossenen** Vokabelmenge `("channel" \| "file")`, sonst `ApiErrorCodes.InvalidSourceKind`. Der Wert wird im Audit-Log gerendert. | `EmoteEndpoints.cs:136,138,153-155,165`; `AuditLogQueryService.cs:21,140` |
| Der Client kennt v4 bereits (`V4GqlPath = "/v4/gql"`), paginiert Set-Einträge mit `SetEntriesPerPage = 500` und `MaxSetEntryPages = 10`, Kommentar: *„capacity can exceed 1000"*. | `SevenTvApiClient.cs:32-37` |
| **Falle:** `ResolveTwitchUserIdAsync` löst über 7TVs **Suchquery** auf (`GqlUsersQuery`, `users(query: $q)`). Der sichere Weg `userByConnection(platform, id)` liegt direkt darunter. | `SevenTvApiClient.cs:12-13` (Suche) vs. `:20` (sicher) |
| Beide Schritte der Auflösung existieren bereits als fertige Dienste: `LookupByLoginAsync` macht Normalize + App-Token + Helix + Dreizustand „in one place", `ResolveSevenTvIdentityAsync` macht `userByConnection` → `SevenTvIdentity(userId, emoteSetId)`. | `Core/Services/IChannelIdentityService.cs:113-119`; `ChannelIdentityService.cs:141-149`; `SevenTvApiClient.cs:178-223` |
| Ein App-Access-Token genügt für Helix-Login→ID; `ITwitchAppTokenProvider` ist Singleton, die Api registriert dieselbe Methode. | `ServiceCollectionExtensions.cs:125` |
| **Bereits geklärt (2026-08-31):** 7TV liefert für eine Twitch-ID ohne Konto ein **Platzhalter-Objekt statt `null`** — Sentinel-ObjectID `"00000000000000000000000000"` und `connections: []`. „Kein Account" wird **semantisch an der fehlenden TWITCH-Connection** erkannt, nicht am Sentinel. | `SevenTvApiClient.cs:189-197,207-214,455-457`; `docs/DECISIONS.md:3381-3385` |

## Live gegen 7TV gemessen (2026-09-09, curl)

- **Kein Popularitätssignal pro Kanal.** `EmoteSetEmote` hat nur `id, emote, alias,
  addedAt, flags, addedById, originSetId`. Wie oft ein Emote in *diesem* Chat fällt,
  weiß nur, wer dort im Chat sitzt.
- **Globale Popularität dagegen frei verfügbar, ohne Login:** `Emote.scores
  {trendingDay trendingWeek trendingMonth topDaily topWeekly topMonthly topAllTime}`,
  `Emote.ranking(Ranking)`. Für `BOOBA`: `topAllTime: 227959`, Rang 4 aller 7TV-Emotes.
- **Set samt scores in einem Request pro Seite.** HandOfBloods Set (956 Emotes,
  `perPage: 1000`): **174.547 Bytes, 0,72 s, Query-Complexity 16 von 5000**. Der
  v3-REST-Abruf derselben Liste **ohne** scores ist 2,3 MB. *Diese Messung belegt die
  Kosten pro Seite, nicht die Abwesenheit von Paginierung* — der eigene Client sagt
  ausdrücklich, dass Kapazitäten über 1000 vorkommen.
- **Falle:** `Emote.channels.totalCount` im selben Batch mitzuziehen scheitert sofort. Es
  hängt an einem eigenen Suchlimit (`x-ratelimit-search-limit: 100`) und sperrt nach
  Überziehung für ~1 Stunde (`x-ratelimit-search-reset: 3583`) — getarnt als HTTP 200 mit
  `extensions.status: 429`. Für ein Set-Ranking unbrauchbar. **Dasselbe Limit gilt für
  7TVs `users(query:)`-Suche** und ist der Grund für die Auflösungsregel unten.
- **Alias-Batching** (`a0:`, `a1:` …) bricht ab Complexity 600 mit `Query is too complex.`
- **Trending-Katalog funktioniert:** `EmoteQuery.search(query, tags, sort, filters, page,
  perPage)` mit `SortBy = TRENDING_DAILY|WEEKLY|MONTHLY, TOP_DAILY|WEEKLY|MONTHLY|ALL_TIME,
  NAME_ALPHABETICAL, UPLOAD_DATE`. Live nach `TRENDING_DAILY`: `Fishinge` (1.120.689),
  `GoodTake` (1.066.225), `Ratge` (1.061.946).
- **`emote_set`-Nullung (#43):** nur im verschachtelten `connections[]`-Pfad, top-level
  noch befüllt.

## Prämissen

- **P1' — Es entsteht keine neue Schreibfähigkeit und keine neue Rechtedurchsetzung.**
  Der Schreibpfad existiert und ist für jeden Nutzer identisch. *Präzisiert gegenüber dem
  ersten Entwurf: Lesefähigkeit, Endpunktbereich, Härtung und Auswahl-UI sind sehr wohl
  neu — „nur die Quell-Liste beschaffen" war zu weit gefasst.*
- **P2 — Der heutige Admin-Weg ist im Messfenster schädlich**, nicht bloß unbequem.
- **P3 — Die Autorisierungsfrage ist eine Ruf-, keine Sicherheitsfrage.** 7TVs
  Set-Endpunkt ist ohne Login lesbar, drei Werkzeuge kopieren längst. *Offen: bewusst als
  Wertung des Betreibers stehengelassen, siehe Offene Fragen.*
- **P4' — Kopieren ist der Kern, Ranking ist eine Spalte, die ohnehin mitkommt.**
  Abgeschwächt, nachdem der Betreiber widersprach: „alleine das schnelle kopieren wär
  schon ein riesen mehrwert".
- **P5' — Die Zahlen kosten null Requests, ihre Bedeutung kostet.** Korrigiert nach
  Codex' Einwand. Sie werden ausdrücklich als **netzwerkweit** beschriftet, sind **nie**
  die Standardsortierung, und die Spalte heißt nie bloß „Beliebtheit".

## Zweitmeinung (Codex Sol, kalt gelesen)

- **Steelman:** authentifizierter „Aus beliebigem 7TV-Kanal importieren"-Flow; Quellset
  live laden, gegen das Zielset diffen, Auswahl an die vorhandene Browser-Import-Engine
  geben. Kein Join, kein Tracking, keine Speicherung, kein serverseitiger Token.
- **Angegriffene Prämisse: P5.** Angenommen, siehe P5'. Der Beleg lag im Gespräch selbst:
  der Betreiber schrieb „Beliebtheit im Quellkanal", bevor er je eine Oberfläche gesehen
  hatte.
- **Endpunkt in eigener Gruppe** — übernommen, siehe unten.
- **`alias` statt `defaultName`** — übernommen, mit Folgehinweis: die Übernahme des
  Quell-Alias erhöht die Namenskollisionsrate im Zielset gegenüber `defaultName`.
- **Nicht übernommen:** sein Widerlegungskriterium (ein A/B-Test) — bei ~15 Kanälen und
  einem Hobby-Entwickler unrealistisch. Billigere Widerlegung: Spalte als „7TV global"
  beschriften, ausliefern, schauen ob trotzdem jemand nach Kanal-Beliebtheit fragt.
- **Drei unbehandelte Risiken:** (1) Verhältnis zu 7TV — alle Abrufe kommen konzentriert
  von einer Server-IP; (2) Community-Konflikte — massenhaftes Klonen von Insider- oder
  Brand-Emotes; (3) Urheber-/Persönlichkeitsrechte. Siehe Offene Fragen.

## Erwogene Ansätze

- **A — Import-Quelle „7TV-Kanal", schlank.** Neuer Endpunkt, keine Pufferung.
  *Verworfen: unser bisher einziger uncachierter 7TV-Fan-out auf Nutzeranfrage
  (`EmoteSetOwnershipService` Tier 3) ist rollengegatet und selten; ein für jeden
  eingeloggten Nutzer offener Pfad ohne Puffer ist etwas anderes — ausgerechnet gegen den
  Dienst, von dem das ganze Produkt abhängt.*
- **B — Dasselbe, gehärtet.** Plus Cache, Koaleszierung, 429-Erkennung, Circuit Breaker.
  **Gewählt.**
- **C — Fremdkanal-Export als eigene Seite.** *Verworfen: nicht wegen der neuen Route — die
  führt B genauso ein — sondern wegen des Flusses und der Erzählung. Zwei Schritte
  (herunterladen, hochladen) statt einem, und es stellt „fremde Sets abgreifen" als
  eigenständige Funktion in den Vordergrund statt als Quelle beim Einlesen.*
- **D — Multi-Kanal-Palette** (Codex: N Quellkanäle, Vereinigungsmenge, Signal „in 6 von
  8 gewählten Sets"). *Zurückgestellt: das Signal ist ehrlicher als `topAllTime`, aber N
  Kanäle sind N Set-Abrufe pro Ansicht — es vervielfacht genau das Risiko, das B senken
  soll. Kandidat für ein späteres Issue.*

## Empfohlener Ansatz: B, Kanalquelle

Der Import-Dialog bekommt neben „Datei" eine zweite Quelle. Der Trending-Katalog ist
**nicht** Teil dieses Arbeitspakets, sondern ein eigenes Folge-Issue (siehe unten) — auf
Wunsch des Betreibers mehrstufig, und weil er das gestellte Problem nicht löst.

### Endpunktbereich

Eigene `MapGroup` `/api/seventv/…`, nur `RequireAuthorization()`, **kein**
`UsageStatsAccessAuthorizationFilter`. `ChannelNameValidationFilter` bleibt als erster
Filter (400-Vertrag der Namensvalidierung, wie in beiden Bestandsgruppen). Eigene
Rate-Limit-Policy, **Fixed-Window, partitioniert nach `twitch-user`** — Vorschlagswert,
in der Spec zu bestätigen.

```
GET /api/seventv/channels/{channelName}/emotes
```

### Auflösungskette (verbindlich)

Die **ersten beiden** Schritte existieren fertig und werden nicht neu gebaut; Schritt 3
ist neu — das vorhandene `GqlSetEntriesQuery` holt nur `added_at` und `emote { id }`,
nicht `alias` und `scores`. Regel 4 verlangt für den Handler ohnehin ein
Service-Interface:

1. **`IChannelIdentityService.LookupByLoginAsync`** — macht `ChannelName.Normalize`,
   App-Token-Beschaffung, Helix-Aufruf und den Dreizustand aus `TwitchUserLookupStatus`
   in einem Schritt. Die App-Token-Behandlung darf **nicht** ein zweites Mal geschrieben
   werden.
2. **`ISevenTvApiClient.ResolveSevenTvIdentityAsync`** — `userByConnection(platform:
   TWITCH, id:)` → `SevenTvIdentity(userId, emoteSetId)`.
3. v4-GQL-Set-Abfrage auf `emoteSetId`.

**7TVs `users(query:)`-Suche (`GqlUsersQuery`) darf auf diesem Pfad nicht verwendet
werden.** Sie hängt an demselben 100er-Suchlimit, das eine Überziehung für ~1 Stunde
sperrt. Das ist die naheliegende, aber falsche Umsetzung.

### Set-Abruf

v4-GQL mit `alias`, Emote-ID, `defaultName` und `scores`. **Paginiert** —
`SetEntriesPerPage`/`MaxSetEntryPages` wie im Bestand; `totalCount > items.length` ist
der Normalfall bei Sub-Sets, nicht die Ausnahme. `channels.totalCount` wird **nicht** im
Batch geholt.

### Zustände und Fehlercodes

Fünf Fehlerzustände plus ein Leerzustand sind zu unterscheiden. Jeder **Fehler**zustand
braucht einen Code aus `ApiErrorCodes`, mit Eintrag in
`web/src/app/core/i18n/api-error.ts` **und** beiden Locale-Dateien (Regel 7); der
Leerzustand braucht keinen:

| Zustand | Quelle auf dem vorgeschriebenen Pfad | Behandlung |
|---|---|---|
| Kanal existiert nicht auf Twitch | `TwitchUserLookupStatus.NotFound` | vorhandener `ChannelNotOnTwitch` |
| Helix nicht erreichbar / kein App-Token | `TwitchUserLookupStatus.Unavailable` | eigener Code + Retry-Hinweis. **Nicht** als Ablehnung behandeln — der Bestand verlangt hier „carry on as before" (`IChannelIdentityService.cs:113-118`) |
| Kein 7TV-Konto | `SevenTvLookupStatus.NoSevenTvAccount` — erkannt an der **fehlenden TWITCH-Connection**, nicht am Sentinel | eigener Code |
| Konto ohne aktives Set | **`SevenTvIdentity.EmoteSetId is null`** | eigener Code |
| Aktives Set mit 0 Emotes | Set-Abruf, leeres `items` | Leerzustand, kein Fehler |
| 7TV nicht erreichbar / 429 | `SevenTvLookupStatus.Unavailable`, `extensions.status: 429` | eigener Code + Retry-Hinweis |

**Achtung:** `ResolveSevenTvIdentityAsync` liefert für „Konto vorhanden, kein aktives
Set" **`Ok` mit `EmoteSetId == null`** und ausdrücklich **nicht**
`SevenTvLookupStatus.NoActiveEmoteSet` (Kommentar `SevenTvApiClient.cs:212-214`). Dieser
Statuswert entsteht nur auf dem v3-REST-Pfad `GetChannelStateForTwitchUserAsync`, der
hier nicht verwendet wird. Wer ihn abfragt, prüft eine Bedingung, die nie eintritt.

### Fluss und Auswahl

Reihenfolge bleibt wie heute: **Quelle → Auswahl → Ziel → Bestätigung.** Der Diff gegen
das Zielset und die Kapazitätsanzeige entstehen erst nach der Zielwahl, in
`buildImportPreview` und `ImportConfirmDialog` — dort, wo sie heute schon entstehen.

**Neu zu bauen: ein generisches Auswahlraster** mit Zeilentyp
`{ sevenTvEmoteId, name, imageUrl }`, das `ListSelection<T>` benutzt. Das bestehende
Raster ist nicht wiederverwendbar (siehe Ausgangslage). Es ist zugleich der Teil, auf dem
der Betreiber ausdrücklich bestanden hat: „muss die möglichkeit geben dass man NICHT alle
direkt übernimmt". Der heutige Datei-Pfad hat diese Auswahl nicht — `ImportConfirmDialog`
übergibt `preview.toAdd` als Ganzes.

### Wiederverwendungsbilanz

- **Backend fast fertig:** `IChannelIdentityService.LookupByLoginAsync` (Normalize +
  App-Token + Helix + Dreizustand), `ISevenTvApiClient.ResolveSevenTvIdentityAsync`
  (`userByConnection`), `V4GqlPath`, die Paginierungskonstanten. Nicht beteiligt und
  daher nicht anzufassen: `GetChannelStateForTwitchUserAsync` (v3-REST-Alternativpfad).
- **Import-Kette fertig:** `ImportRow`, `ImportSource`, `ImportOrigin`,
  `dedupeImportRows`, `buildImportPreview`, `ImportTargetDialog`, `ImportConfirmDialog`,
  Token-Prompt, `SevenTvRunEngine`. Alle brauchen nur `{ sevenTvEmoteId, name }` plus
  einen `ImportOrigin`-Tag. **Beim `ImportTargetDialog` gehört `forcedScope` auf
  `'selection'`** (`import-target-dialog.ts:20-25`) — die Bereichs-Radiogruppe „Auswahl
  vs. alle sichtbaren" ist sinnlos, wenn im Raster bereits einzeln ausgewählt wurde.
- **Neu:** Endpunktbereich samt Härtung, das Auswahlraster, die dritte `SourceKind`-Vokabel.

### Herkunft und Audit

`POST /sync-imported` akzeptiert heute nur `("channel" | "file")`. Ein Fremdkanal ist
keins von beidem; ihn als `"channel"` zu buchen ließe das Audit-Log dauerhaft eine
getrackte Herkunft behaupten. Es braucht eine **dritte Vokabel** (Vorschlag:
`"seventv-channel"`) samt Validator, Audit-Log-Rendering und Locale-Einträgen.

### Härtung

- **Redis-Cache** auf der Set-ID. Vorschlag **60 s TTL** plus „neu laden"-Knopf; in der
  Spec zu bestätigen.
- **Koaleszierung** paralleler identischer Abrufe.
- **429-Erkennung** aus `extensions.status` bei HTTP 200 — ohne sie sieht ein Fehlschlag
  aus wie ein leeres Set.
- **Circuit Breaker.** Vorschlag: nach 5 aufeinanderfolgenden Upstream-Fehlern für 60 s
  öffnen, ein Probe-Request zum Schließen. Ein geöffneter Breaker wird geloggt.
- **Eigene `RateLimitCallSources`-Quelle** für diesen Pfad, damit der Verbrauch in
  `/api/admin/rate-limits` sichtbar ist. Ein Feature, dessen Begründung „7TV nicht
  verärgern" lautet, muss seinen eigenen Verbrauch zeigen.

### Bedeutungsvertrag der Score-Spalte

`scores` erscheinen als **optionale** Sortierung, beschriftet „7TV global". Nie
Standardsortierung. Die Spalte heißt nie bloß „Beliebtheit".

### Bewusst nicht enthalten

Worker-Änderungen, zusätzliche Joins, kanalbezogene Beliebtheit für fremde Kanäle,
persistierte Quellsnapshots, Multi-Kanal-Palette, automatische Auswahl anhand von Scores,
serverseitige 7TV-Mutationen, Import in ein Set eines ungetrackten Zielkanals.

## Folge-Issue: 7TVs Bestenliste als weitere Quelle

Vom Betreiber gewollt, vom Reviewer als Scope-Wucherung markiert — beides trifft zu,
deshalb eigenes Issue statt Streichung. Es löst das hier gestellte Problem nicht, hat
aber einen eigenen Wert: beim Trending-Katalog gibt es kein Opfer, was Codex' Risiko 2
entschärft. `EmoteQuery.search(sort:)` ist bereits verifiziert. Stufen: (1) feste
Bestenliste ohne Suche, (2) Blättern und Sortierwechsel, (3) Suchfeld und Filter.

## Offene Fragen

1. **P3 ist unbeantwortet.** Wollen wir dafür bekannt sein, das Abgreifen fremder Sets
   bequem zu machen? Entscheidung des Betreibers, nicht der Analyse.
2. **Codex' Risiken 2 und 3 sind unbehandelt** und berühren die seit 2026-07-28 offene
   Impressum-/Datenschutz-Frage. Brauchen wir eine sichtbare Warnung vor dem Import und
   einen Melde-/Takedown-Weg?
3. **Cache-TTL, Breaker-Schwellen und Rate-Limit-Policy** stehen oben als Vorschlagswerte.
   Zu bestätigen oder zu ersetzen.
4. **Dritte `SourceKind`-Vokabel:** `"seventv-channel"` oder etwas anderes? Der Wert wird
   im Audit-Log dauerhaft sichtbar.
5. **Alias-Kollisionen.** Die Übernahme des Quell-Alias statt `defaultName` erhöht die
   Kollisionsrate im Zielset. Reicht die heutige Behandlung (Zeilen bleiben in `toAdd`,
   7TV entscheidet), oder soll die Vorschau warnen?

## Erfolgskriterien

- Ein eingeloggter Nutzer **ohne jede Rolle im Quellkanal** kann dessen Emote-Liste sehen,
  **einzelne** Emotes auswählen und sie in ein Set einlesen, in dem er 7TV-Rechte hat —
  das Ziel bleibt ein getrackter Kanal aus `listMine()`.
- Der Admin-Weg „Kanal joinen, um eine Liste zu lesen" wird nicht mehr gebraucht.
- Kein Worker-Code angefasst, kein zusätzlicher Channel-Join, Messfenster unberührt.
- Ein Set mit mehr als 500 Einträgen wird vollständig geladen (Paginierung).
- Ein zweites Öffnen desselben Quellsets innerhalb der TTL erzeugt **keine weiteren**
  7TV-Abrufe (Cache/Koaleszierung) — die erste Ladung darf paginierungsbedingt mehrere
  sein.
- Ein 7TV-429 erzeugt eine erklärende Meldung, nicht ein leeres Set.
- Die fünf **Fehler**zustände aus der Tabelle haben je einen Code in `ApiErrorCodes.cs`,
  in `api-error.ts` und in **beiden** Locales (Regel 7). Der Leerzustand („aktives Set
  mit 0 Emotes") ist kein Fehler und bekommt keinen.
- Die neue `MapGroup` und ihre Filterreihenfolge haben ihren Fall in
  `tests/EmotePurge.Api.Tests` (Regel 11).
- Der neue 7TV-Pfad erscheint als eigene Call-Source in `/api/admin/rate-limits`.
- Die Score-Spalte ist in beiden Locales als netzwerkweit erkennbar und nicht
  vorausgewählt.
- Gates grün: `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`,
  `npm --prefix web run e2e`, plus `node scripts/coverage-local.mjs` vor dem PR.

## Auslieferung

Bestehende Pipeline: GHCR-Images plus Portainer-Stack auf `emotepurge.app`. Keine
Migration — das Feature schreibt nichts in unsere DB.

## Nächste Schritte

1. Spec schreiben nach `docs/superpowers/specs/`, dem Betreiber vorlegen, Freigabe abwarten.
2. Issue anlegen und in Epic #118 unter den **messungsneutralen** Tickets verlinken;
   das Bestenlisten-Issue separat anlegen und ebenfalls dort verlinken.
3. Bauen in dieser Reihenfolge:
   1. Endpunktbereich, Auflösungskette, paginierter Set-Abruf, Zustands-Mapping
   2. Härtung: Cache, Koaleszierung, 429-Erkennung, Breaker, Telemetrie-Call-Source
   3. Generisches Auswahlraster (neu, nicht wiederverwendet)
   4. Verdrahtung in den Import-Flow inklusive dritter `SourceKind`-Vokabel
   5. Score-Spalte samt Beschriftung und Übersetzungen
4. Vor dem Merge: `/codex:review --model gpt-5.6-sol` über das fertige Arbeitspaket.

## Was mir an deinem Denken aufgefallen ist

- Du hast auf P4 nicht zugestimmt, obwohl es die schmeichelhaftere Prämisse war. „bei P4
  bin ich unsicher. Ja natürlich wäre die Beliebtheit im Quellkanal cool, aber alleine
  das schnelle kopieren wär schon ein riesen mehrwert" — das hat die Prämisse
  abgeschwächt und den Ansatz kleiner gemacht. Die meisten nicken bei der eleganteren
  Version.
- Du hast das Werkzeug genannt, das dein Feature überflüssig machen würde, bevor ich
  danach gefragt habe: „das würde z.b. den 7tv emote merge obsolete machen". Die
  Konkurrenz selbst auf den Tisch zu legen ist selten.
- Deine erste Ergänzung nach der Ansatzwahl war eine Einschränkung, keine Erweiterung:
  „bei Kanal muss es die möglichkeit geben dass man NICHT alle direkt übernimmt". Und im
  selben Atemzug hast du die andere Hälfte kleiner geschnitten: „Das mit dem Trending muss
  auch nicht von anfang an als Vollausbau existieren." Genau diese Einschränkung hat sich
  im Review als der einzige Teil erwiesen, den es noch nirgends gibt.
- Du hast zwischen dem, was jemand gefragt hat, und dem, was du selbst willst, sauber
  getrennt — „der User hatte nach einer import funktion gefragt, und die hat er
  mittlerweile" — statt fremden Bedarf für die eigene Idee zu leihen.
