# Plan: Issue #71 (K2) — Zwei Backend-Handgriffe für den Emote-Import

Branch: `feat/emote-import-38` · Worktree: `/home/dev/projects/EmotePurge-import` · Stand: 2026-09-05

Quellen: Issue #71, [`docs/designs/Emote-Import-38-2026-09-05.md`](../designs/Emote-Import-38-2026-09-05.md)
(Abschnitte „Zieldaten" und „Backend (zwei Handgriffe)"), `CLAUDE.md`, der Code selbst.

**Der Plan enthält bewusst keinen fertigen Code.** Signaturen und Routen stehen als Vertrag da,
Verhalten in Prosa. Jeder Task läuft als eigener Subagent mit frischem Kontext und ist allein
verständlich.

---

## Verifikation des Ist-Zustands

Jede Datei-/Zeilenangabe des Issues gegen den Code auf `feat/emote-import-38` geprüft.

| Behauptung des Issues | stimmt? | tatsächlicher Befund |
|---|---|---|
| Gruppe `/api/channels/{channelName}/emotes`, `InteractiveRead`, Filter `ChannelNameValidationFilter` + `UsageStatsAccessAuthorizationFilter` | ja | `EmoteEndpoints.cs:23-28`, genau in dieser Reihenfolge (Validierung vor Autorisierung) |
| `sync-deleted` `:30`, `Bookkeeping` `:62` | ja | exakt |
| `sync-restored` `:68`, `Bookkeeping` `:98` | ja | exakt |
| `set-warning` `:100`, `active-set` `:117`, `duplicate-names` `:130` | ja | exakt |
| `SyncRestoredRequest(IReadOnlyList<string> EmoteIds)` bei `:177` | ja | exakt; `internal sealed record`, Datei-Ende |
| `MarkRestoredAsync` un-archiviert und schreibt `emotes.syncRestored` mit `details: new { emoteCount }` (`EmoteService.cs:94`) | ja | `:92-97`, Konstante steht auf `:94`. Ergänzung: der Audit-Eintrag hängt an `emotes.Count > 0` (Ziel-Zustand), **nicht** an `newlyRestored` |
| Keine Liste mit `sevenTvEmoteId` ohne Pflicht-Zeitraum (`UsageStatsEndpoints.cs:26-33`, `:35-50`) | ja | `GET …/usage-stats` (`:26-33`) liefert `EmoteUsageDto(EmoteName, Date, UseCount)` — kein `sevenTvEmoteId`; `/totals` (`:35-50`) hat `SevenTvEmoteId`, verlangt aber `from`/`to` und trägt die volle Payload |
| `AuditLogEntry.DetailsJson` frei, `AuditActions` in `AuditLogEntry.cs:9-28` | fast | Klasse `AuditActions` steht auf `:9-29` (Konstanten `:11-28`, schließende Klammer `:29`). Kosmetisch |
| Frontend `AuditAction`-Union `audit.model.ts:15-28` | ja | exakt |
| `ACTION_KEYS` `audit-actions.ts:13-26` | fast | `:13-27` (Einträge `:14-26`, schließende Klammer `:27`). Kosmetisch |
| `CHANNEL_SCOPED_ACTIONS` ist abgeleitet | ja | `audit-actions.ts:40-42`: `Object.keys(ACTION_KEYS)` minus `CHANNELLESS_ACTIONS`. Die neue Aktion landet dort **automatisch**, kein Handgriff nötig |
| Spec-Zählungen `audit-actions.spec.ts:29` (13) und `:33` (11) | **nein (halb)** | 13 steht auf `:29` ✓; die 11 steht auf **`:34`**, nicht `:33` (`:33` ist der Kommentar unter dem `it(...)`). Zusätzlich prüft `:37` `CHANNELLESS_ACTIONS.size === 2` — das bleibt bei 2 und darf **nicht** mitgezogen werden |
| Keys `audit.actions.*` in `de.json:520-536` und `en.json` | ja | `"audit"` auf `:520`, `"actions"` `:522`, Einträge `:523-535`, `}` `:536`; `en.json` deckungsgleich (beide `emotesSyncRestored` auf `:533`) |
| Filter-Matrix `AuthFilterMatrixTests.cs:51-75` als `InlineData`-Inventar | ja, aber unvollständig zitiert | `:51-75` ist das Inventar der **401-anonym**-Theorie. Daneben gibt es ein zweites, kleineres Inventar `:88-94` (401 bei unvollständigen Claims) und Einzelfälle je Filter |
| `emote-admin.service.ts:49-58` = `getSetWarning`/`getSetStatus` | ja | `getSetWarning` `:49-51`, `getSetStatus` `:56-58`; darunter noch `getDuplicateNames` `:63-67` |
| „`400` mit sprachneutralem Code `ApiErrorCodes.ValidationFailed` (bestehender Code)" | **nein** | `ApiErrorCodes` kennt **kein** `ValidationFailed`. Vorhanden und passend: `EmoteIdsEmpty = "emote_ids_empty"`. Für unbekanntes `SourceKind` gibt es nichts — ein neuer Code ist Pflicht (Regel 7, s. Risiko R4) |
| „Test wie für `sync-restored`, falls vorhanden" (Rate-Limit) | **nein, es gibt keinen** | `Bookkeeping` kommt in `tests/` nirgends vor. Kein Test deckt heute ab, dass `sync-deleted`/`sync-restored` die Gruppenpolicy überschreiben. Neuer Test nötig, s. R7 |
| „Detailanzeige zeigt `sourceChannelName` bzw. ‚Datei' und `emoteCount` (gleiches Muster wie `emotesSyncRestored`)" | **nein — nicht baubar wie beschrieben** | Der Server reduziert `DetailsJson` auf **genau ein** `AuditLogDetail(Kind, Count, Text)` mit fester Präzedenz (`AuditLogQueryService.ProjectDetail`, `:105-149`; Whitelist `AuditLogDetail.Kinds` = `emoteCount`/`removedEntries`/`title`). `sourceChannelName` und `sourceKind` fallen dort still weg. `AuditRow` (`audit-row.ts:11-22`) verwirft außerdem `targetType`/`targetId` komplett. Siehe R1 |

---

## Risiken und Grenzfälle

### R1 — Audit-Detailanzeige: Herkunft **und** Anzahl. **Entschieden: wird gebaut.**

`ProjectDetail` gibt pro Zeile höchstens **ein** `AuditLogDetail` zurück, in fester Präzedenz
(`emoteCount` → `removedEntries` → `title`). Ein Payload `{ emoteCount, sourceChannelName, sourceKind }`
rendert mit dem heutigen Stand also ausschließlich „N Emotes".

Die erste Fassung dieses Plans schloss daraus, die Herkunft müsse unsichtbar bleiben, weil ein neuer
`Kind` den `emoteCount` *verdrängen* würde. **Das ist falsch, und die Korrektur ist der Grund für
diese Entscheidung:** `AuditLogDetail` ist `(string Kind, long? Count, string? Text)` — der Record
trägt Anzahl und Text **gleichzeitig**, es ist nur der `Kind`, der einmalig ist. Ein Import-Detail
kann also `Count = emoteCount` **und** `Text = sourceChannelName` setzen, ohne den Vertrag von
`AuditLogEntryDto` zu berühren (weiterhin genau ein Detail je Zeile). Der einzige echte Blocker liegt
im Frontend, `audit-row.ts:55`: `params: detail.count !== null ? { count } : { title: … }` wählt
entweder-oder. Das ist eine Zeile.

**Entscheidung (2026-09-05): Herkunft wird angezeigt**, wie #71 Punkt 3 und die Akzeptanzkriterien 3
und 6 es verlangen. Umsetzung:

- **Zwei neue Kinds** statt eines, damit `DETAIL_KEYS` eine flache `kind → key`-Abbildung bleibt:
  `importedFromChannel` (`Count` = Anzahl, `Text` = Quellkanal) und `importedFromFile`
  (`Count` = Anzahl, `Text` = `null`). Welcher der beiden, entscheidet der Server anhand von
  `sourceKind`; das Frontend bekommt keine Fallunterscheidung.
- **Präzedenz:** Die Import-Prüfung steht in `ProjectDetail` **vor** der `emoteCount`-Prüfung,
  erkannt am Vorhandensein von `sourceKind`. Sonst gewinnt `emoteCount` und die Herkunft fällt still
  weg. Das ist die einzige verhaltensrelevante Reihenfolge in dieser Methode und gehört als
  Kommentar dorthin.
- **`renderDetail`** reicht künftig beide Parameter durch, wenn beide gesetzt sind; die bestehenden
  drei Kinds setzen weiterhin genau einen und verhalten sich unverändert (Bestandstests pinnen das).
- **Locales:** `audit.details.importedFromChannel` („{{count}} Emotes aus {{title}}") und
  `.importedFromFile` („{{count}} Emotes aus einer Datei") in `de` und `en`.

`sourceKind` selbst bleibt unsichtbar — er ist der Diskriminator, nicht die Botschaft.

### R2 — Registrierung der Gruppen-Wurzel

`app.MapGroup("/api/channels/{channelName}/emotes")` ist selbst der gewünschte Pfad. Die im Repo
belegte Form ist **`group.MapGet("")`** mit leerem String — nicht `"/"`. Beleg: `UsageStatsEndpoints.cs:26`
registriert so, und `AuthFilterMatrixTests.cs:59` trifft damit `/api/channels/testchannel/usage-stats`
ohne Schrägstrich. `"/"` erzeugt stattdessen einen Pfad mit angehängtem Trennzeichen.

Kollision mit bestehenden Routen: keine. `/api/channels/{channelName}` (`ChannelEndpoints.cs:20`) hat
drei Segmente, die neue Route vier; die Untterrouten der Gruppe haben fünf. Auch
`/api/channels/live-events` (`LiveRouteStructureTests`) ist nicht betroffen.

**Entscheidung:** `group.MapGet("")`, Reihenfolge im Quelltext **vor** `sync-deleted` (die Wurzel
zuerst, dann die Unterrouten) — reine Lesbarkeit, Routing hängt nicht daran.

### R3 — `UsageStatsAccessAuthorizationFilter` ist für die Liste die richtige Prüfung, das 404 aber nicht seine Sache

Der Filter (`UsageStatsAccessAuthorizationFilter.cs:18-41`) prüft: Namensformat → 400, fehlender
`TwitchPrincipal` → 401, `IChannelAccessService.CanViewUsageStatsAsync` → 403. Er admittiert
Admin/Broadcaster/Live-Mod **und** 7TV-Editoren. Genau die Zielgruppe: wer importieren darf, ist
7TV-Editor oder Broadcaster im Zielkanal, und `active-set`/`duplicate-names` — die beiden Nachbarn,
die der Import-Dialog ohnehin lädt — hängen an demselben Filter. Eine schlanke Emote-Liste ist
schwächer als beides. **Kein neuer Filter, keine neue Rolle** (Design, Prämisse 5).

Wichtig: der Filter erzeugt **nie** ein 404. `CanViewUsageStatsAsync` kann für einen nicht getrackten
Kanal true liefern (Admin, oder Broadcaster des eigenen Namens). Das 404 muss deshalb aus dem
Handler kommen, wie bei `active-set` (`:123`) und `duplicate-names` (`:136`): Service liefert `null`
für „Kanal unbekannt", Handler antwortet `Results.NotFound()`.

**Folge für die Tests:** Akzeptanzkriterium 2 des Issues („404 bei unbekanntem Kanal … (Filter-Matrix)")
ist in `Api.Tests` **nicht** prüfbar — dort ist keine Datenbank, und `IEmoteListQueryService` wird
in `ApiFactory` nicht substituiert. Der 404-Fall gehört nach `Infrastructure.Tests` (Service liefert
null). In `Api.Tests` bleiben 401/403/400.

### R4 — Sortierung „nach `name` ordinal" ist eine echte Falle

PostgreSQL sortiert `ORDER BY name` nach der Collation der Spalte, nicht ordinal. Im Repo ist auf
`Emote.Name` **keine** Collation gesetzt (kein `UseCollation`/`HasColumnType` im `AppDbContext`), es
gilt also die Datenbank-Default-Collation — und die unterscheidet sich zwischen Umgebungen
(`postgres:16-alpine` in Dev und Testcontainers, potenziell etwas anderes in Prod). Unter einer
locale-bewussten Collation steht `apple` vor `Zebra`, ordinal steht `Zebra` vor `apple`.

Zweitens: EF Core kann `OrderBy(e => e.Name, StringComparer.Ordinal)` gar nicht übersetzen — der
`IComparer`-Überladung fehlt eine SQL-Entsprechung, die Query scheitert statt still falsch zu sein.

Das Gegenstück ist im Bestand vorhanden: `DuplicateEmoteNameQueryService.cs:24-27` materialisiert
erst und gruppiert/sortiert **danach** mit `StringComparer.Ordinal`, ausdrücklich mit der Begründung
„muss ordinal case-sensitive sein, um dem Chat-Matching zu entsprechen". Der Import vergleicht Namen
gegen dieselbe Semantik (Design, Constraint „Namenskollisionen … Der Vergleich ist ordinal").

**Entscheidung:** Die Query filtert und projiziert in SQL (`ChannelId` + `!IsArchived`), materialisiert,
und sortiert **im Speicher** mit `StringComparer.Ordinal`. Der Test muss ein Namenspaar verwenden,
das ordinal und locale-bewusst **verschieden** sortiert (Großbuchstabe gegen Kleinbuchstabe).

Nebenbefund: `(ChannelId, SevenTvEmoteId)` ist unique (`AppDbContext:30`), `Name` ist es **nicht** —
die Antwort darf doppelte Namen enthalten (das ist der ganze Sinn von `duplicate-names`). Der
Vertrag sagt das ausdrücklich; der Client baut daraus ein Set.

### R5 — Antwortgröße und Pagination

~900 aktive Emotes (HandOfBlood) × (24-Zeichen-ObjectID + Name) ≈ **40–50 KB JSON**. Bestandsmuster:
`duplicate-names` und `usage-stats/totals` sind beide unpaginiert, und `totals` trägt für denselben
Kanal ein Vielfaches dieser Payload. Pagination gibt es im Repo nur für Listen mit Nutzer-Navigation
(`PagingQuery`, Audit-Log). Der Import-Dialog braucht die **vollständige** Liste, um „schon im
Zielset" und Namenskollisionen zu beantworten — eine Seite davon ist wertlos.

Zu wissen, aber nicht zu lösen: JSON wird auf dem VPS nicht komprimiert (`gzip_types application/json`
fehlt in der nginx-Config — bekannter offener Handgriff, s. Entscheidungslog zum Series-Endpunkt).
Das kodierte Wire-Format des Series-Endpunkts ist genau daraus entstanden, aber bei 50 KB lohnt der
Aufwand nicht.

**Entscheidung:** Keine Pagination, kein kodiertes Format. Antwort als Objekt-Wrapper
`{ emotes: [...] }` (wie im Issue), nicht als nacktes Array — konsistent mit dem, was die Admin-Kanalliste
seit B10 tut, und erweiterbar ohne Vertragsbruch.

### R6 — `sourceChannelName` ist angreiferkontrollierter Freitext in `jsonb`

`MarkImportedAsync` schreibt einen Wert aus dem Request-Body in `DetailsJson`. Der Lesepfad ist durch
`ProjectDetail` whitelistet, der **Rohwert** steht aber dauerhaft in der Datenbank (Retention
unbegrenzt, s. `AuditLogEntry`-Doku) und wird von Menschen mit `psql` gelesen. Ein unbegrenzter String
dort ist eine unnötige Fläche.

**Entscheidung:** `SourceChannelName` wird gegen `ChannelNameValidation.IsValid` geprüft, sobald er
gesetzt ist; ungültig → 400 mit `ApiErrorCodes.InvalidChannelName` (bestehender Code, in beiden
Locales vorhanden). Gespeichert wird die **normalisierte** Form (`ChannelName.Normalize`, Regel 9).
Bei `SourceKind = "file"` ist `SourceChannelName` erlaubt, aber optional — eine `emote-list`-Datei
trägt laut Design den Ursprungskanal mit, und der ist die nützlichste Information am Eintrag.

### R7 — `Bookkeeping` ist heute von keinem Test berührt

`RequireRateLimiting(RateLimitPolicyNames.Bookkeeping)` hängt an `sync-deleted` (`:62`) und
`sync-restored` (`:98`) als **Override** der Gruppenpolicy `InteractiveRead` — die letzte Registrierung
gewinnt. Kein Test im Repo prüft das; ein versehentlich weggelassener Override wäre unsichtbar, bis
in Produktion eine Bookkeeping-Meldung an einem verbrauchten Lesebudget scheitert (genau der Vorfall,
den die Kommentare `:58-62` beschreiben).

Zwei gangbare Wege:

1. **Strukturtest** über `EndpointDataSource`, nach dem Muster von
   `LiveRouteStructureTests.cs:38-63`: die Route heraussuchen und prüfen, dass ihre
   `EnableRateLimitingAttribute`-Metadaten auf `Bookkeeping` und nicht auf `InteractiveRead`
   enden. `EnableRateLimitingAttribute` samt `PolicyName` ist öffentliche Fläche des
   `Microsoft.AspNetCore.RateLimiting`-Ref-Assemblys (geprüft gegen
   `Microsoft.AspNetCore.App.Ref/10.0.10`) — in der Rot-Phase trotzdem zuerst gegen
   `sync-restored` verifizieren, bevor der neue Fall dazukommt.
2. **Verhaltenstest** mit Konfigurations-Override nach dem Muster von `RateLimitRejectionTests`
   (eigene Factory, `RateLimiting:InteractiveRead:TokenLimit` klein): Lesebudget leerlaufen lassen,
   dann muss `GET …/emotes` 429 antworten und `POST …/sync-imported` **nicht**.

**Entscheidung:** Weg 1 als Pflicht (billig, deterministisch, deckt beide Bestandsrouten gleich mit
ab). Weg 2 nur, falls sich in der Rot-Phase zeigt, dass die Metadaten nicht eindeutig lesbar sind.

### R8 — Was der Endpunkt bei unbekanntem Kanal tut

`MarkRestoredAsync` gibt für einen unbekannten Kanal still Nullwerte zurück, der Endpunkt antwortet
`200` — das ist dort vertretbar, weil `notFoundIds` die Auskunft trägt. `sync-imported` antwortet
`204` und hat kein solches Feld: ein unbekannter Zielkanal wäre schlicht unsichtbar, obwohl der
Nachlauf des Import-Laufs davon abhängt, dass der Eintrag geschrieben wurde.

**Entscheidung:** `MarkImportedAsync` meldet zurück, ob geschrieben wurde; der Handler antwortet
`404` (ohne Body, wie `active-set`) für einen unbekannten Kanal und `204` sonst. Damit weicht er
bewusst von `sync-restored` ab — das Design sagt ohnehin ausdrücklich, `sync-restored` sei **kein**
Vorbild.

### R9 — `emoteCount` zählt gemeldete IDs, nicht Zeilen

`MarkImportedAsync` legt keine Zeilen an und un-archiviert nichts (Design, ausdrücklich). Es gibt
also nichts, wogegen zu zählen wäre: `emoteCount` ist die Länge der übergebenen Liste. Sinnvoll
**nach Deduplizierung** (ordinal, per `SevenTvEmoteId`) — ein Client, der dieselbe ID doppelt meldet,
hat sie nicht doppelt geschrieben. Der Frontend-Parser dedupliziert bereits (Design, `parseImportSource`);
die Server-Seite verlässt sich nicht darauf.

### R10 — Kein `channel.synced`-Live-Event

`sync-deleted`/`sync-restored` publizieren `channel.synced`, wenn sich Zeilen geändert haben
(`PublishChannelSyncedAsync`, `:153-172`). `sync-imported` ändert **nie** Zeilen, also darf es
**nie** publizieren — sonst lassen alle offenen Seiten grundlos neu laden. Der Zielkanal bekommt
seine Zeilen erst durch den Resync, den der Import-Service danach selbst anstößt (Design,
„Nachlauf-Gate" und „Resync-Cooldown"). Der Handler braucht daher weder `IRedisPublisher` noch
`ILogger`.

### R11 — Parallele Sitzung auf denselben Dateien

`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` und `docs/DECISIONS.md` werden
gleichzeitig von der Sitzung zu Issue #69 geändert.

- In `ServiceCollectionExtensions.cs` **genau eine Zeile** hinzufügen, direkt unter
  `services.AddScoped<IDuplicateEmoteNameQueryService, …>()` (heute `:60`). Nichts umsortieren,
  keine Kommentare umformulieren, keinen `using`-Block anfassen.
- In `docs/DECISIONS.md` **einen** Eintrag anlegen, oben, unter der Trennlinie, in absteigender
  Datumsordnung — heutiges Datum, also über dem bestehenden `2026-09-05`-Eintrag oder direkt darunter,
  je nachdem, was #69 dort schon abgelegt hat. **Vor dem Schreiben `head -30 docs/DECISIONS.md`
  lesen**, nie blind einfügen. Mit `**Betrifft:**`-Zeile (Regel 3 und die Konvention im Kopf der Datei).

### R12 — Keine Migration, kein Schemawechsel

`AuditActions` sind Konstanten, keine Enum, und `DetailsJson` ist freies `jsonb`. Der neue
Aktionsstring braucht keine Migration. Rollback = Revert; geschriebene Einträge bleiben stehen, was
gewollt ist (Issue, Abschnitt Rollback).

---

## Tasks

Zwei Spuren. **Spur A (Backend, T1 → T2 sequenziell)** und **Spur B (Frontend, T3 ∥ T4)** sind
voneinander unabhängig und dürfen gleichzeitig laufen. T5 und T6 kommen zum Schluss.

T1 und T2 fassen beide `EmoteEndpoints.cs` an und dürfen deshalb **nicht** gleichzeitig laufen.

---

### T1 — `GET /api/channels/{channelName}/emotes` (Spur A, zuerst)

**Ziel:** Eine schlanke Liste der nicht-archivierten Emotes eines Kanals mit 7TV-ID und Name,
ordinal nach Name sortiert. Sie beantwortet im Import-Dialog „schon im Zielset?" und
„Namenskollision?".

**Betroffene Dateien**
- `src/EmotePurge.Core/Services/IEmoteListQueryService.cs` (neu)
- `src/EmotePurge.Infrastructure/Services/EmoteListQueryService.cs` (neu)
- `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (eine Zeile, s. R11)
- `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs`
- `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteListQueryServiceTests.cs` (neu)
- `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`

**Vertrag**
- Interface in `Core/Services/` (Regel 4/5), Implementierung in `Infrastructure/Services/`.
  Signatur: `Task<IReadOnlyList<EmoteListItemDto>?> ListActiveAsync(string channelName, CancellationToken cancellationToken = default)`.
- DTO als reiner `record` neben dem Interface, Felder `SevenTvEmoteId` und `Name` — sonst nichts.
  Kein `Emote.Id`: der Guid ist kanal-scoped und gehört laut Regel 8 und dem Design nicht in eine
  kanalübergreifende Liste.
- `null` bedeutet „Kanal unbekannt" (Muster von `DuplicateEmoteNameQueryService`), leere Liste
  bedeutet „Kanal bekannt, keine aktiven Emotes". Der Handler übersetzt `null` → `404`, sonst
  `200 { emotes: [...] }`.
- Kanal-Lookup über `db.LoadChannelReadOnlyAsync` (normalisiert selbst, Regel 9).
- Filter in SQL: `ChannelId` und `!IsArchived`. Projektion auf die zwei Felder **vor** dem
  Materialisieren. Sortierung **nach** dem Materialisieren mit `StringComparer.Ordinal` (R4).
- Registrierung als `group.MapGet("")` (R2), Reihenfolge im Quelltext vor `sync-deleted`.
  Keine eigene Rate-Limit-Policy — die Gruppen-Policy `InteractiveRead` gilt (Design: „`GET …/emotes`
  bleibt auf der Gruppen-Policy").
- Kommentar am Handler auf Englisch (Sprachregel), der sagt, warum die Liste ohne Zeitraum und ohne
  Nutzungszahlen existiert und warum sie nicht paginiert ist.

**Grenzfälle**
- Kanal existiert, hat keine Emotes → `200` mit leerer Liste, nicht 404.
- Kanal existiert nur archiviert → `200` mit leerer Liste.
- Kanal unbekannt → `404` ohne Body.
- Doppelte Namen sind zulässig und müssen erhalten bleiben (R4, Nebenbefund).
- Groß-/Kleinschreibung im Routenwert: `HandOfBlood` muss denselben Kanal treffen wie `handofblood`.

**Tests — zuerst schreiben, rot sehen, dann implementieren**

`tests/EmotePurge.Infrastructure.Tests/Integration/EmoteListQueryServiceTests.cs`
(`[Collection("Postgres")]`, Muster: `DuplicateEmoteNameQueryServiceTests`, eigene Kanalnamen je Fall,
damit die geteilte Fixture nicht kollidiert):
1. Kanal mit 3 aktiven und 1 archivierten Zeile → genau 3 Einträge, jeweils mit gesetztem
   `SevenTvEmoteId` und `Name`; die archivierte 7TV-ID kommt nicht vor.
2. Sortierung ist ordinal: Namen so wählen, dass ordinal und locale-bewusst **verschieden**
   sortieren (Großbuchstabe vs. Kleinbuchstabe am Wortanfang). Der Fall ist die eigentliche
   Absicherung von R4 — im Kommentar festhalten, warum genau diese Namen.
3. Unbekannter Kanal → `null` (nicht leere Liste).
4. Zusatz: gemischt geschriebener Kanalname trifft dieselbe Zeile (Regel 9).

`tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`
5. Neue `InlineData("GET", "/api/channels/testchannel/emotes")` im 401-anonym-Inventar (`:51-75`).
6. Eigener `[Fact]`: `CanViewUsageStatsAsync` → false ergibt `403` auf der neuen Route (Muster
   `UsageStatsFilter_AlsoGuardsTheEmoteGroup_NotJustTheUsageStatsGroup`).
7. Eigener `[Fact]`: malformierter Kanalname auf der neuen Route ergibt `400` mit
   `invalid_channel_name` — belegt, dass die Wurzel-Route die Gruppenfilter in der richtigen
   Reihenfolge erbt.

Kein 404-Fall in `Api.Tests` (R3).

**Definition of Done**
- `dotnet build EmotePurge.slnx` ohne neue Warnungen (bei Bedarf `--no-incremental`).
- `dotnet test EmotePurge.slnx` grün (braucht laufendes Docker).
- Member-Reihenfolge nach Regel 19, `dotnet format EmotePurge.slnx` sauber.
- Kein `AppDbContext` im Handler (Regel 4).

---

### T2 — `POST /api/channels/{channelName}/emotes/sync-imported` (Spur A, nach T1)

**Ziel:** Der Import hinterlässt genau eine Zeile im Audit-Log des **Zielkanals**. Er ändert keine
Emote-Zeilen — die legt allein der Resync an.

**Betroffene Dateien**
- `src/EmotePurge.Core/Entities/AuditLogEntry.cs` (Konstante `EmotesSyncImported = "emotes.syncImported"`)
- `src/EmotePurge.Core/Services/IEmoteService.cs` (neue Methode)
- `src/EmotePurge.Infrastructure/Services/EmoteService.cs` (Implementierung)
- `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (ein neuer Code)
- `src/EmotePurge.Core/Services/IAuditLogQueryService.cs` (zwei neue `AuditLogDetail.Kinds`)
- `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs` (`ProjectDetail`, R1)
- `tests/EmotePurge.Infrastructure.Tests/Integration/AuditLogQueryServiceTests.cs`
- `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs` (Route + Request-Record)
- `web/src/app/core/i18n/api-error.ts` und beide Locale-Dateien (Regel 7, im selben Commit)
- `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs`
- `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`
- `tests/EmotePurge.Api.Tests/` — neuer Strukturtest für die Rate-Limit-Policy (R7)

**Vertrag**
- Konstante: `EmotesSyncImported = "emotes.syncImported"`, in `AuditActions` **unter**
  `EmotesSyncRestored` einsortiert (die beiden Identitätsaktionen bleiben mit ihrem Kommentar am Ende).
- Service: `Task<bool> MarkImportedAsync(string channelName, IReadOnlyList<string> sevenTvEmoteIds, string? sourceChannelName, string sourceKind, AuditActor actor, CancellationToken cancellationToken = default)`
  — `false` heißt „Kanal unbekannt, nichts geschrieben" (R8). Wenn ein `bool` beim Implementieren zu
  dünn wirkt, ein kleines `record`/`enum` daneben; die Aussage muss dieselbe bleiben.
- Implementierung folgt exakt `MarkRestoredAsync`: `ChannelName.Normalize` in eine lokale
  `normalized`-Variable, `db.LoadChannelReadOnlyAsync`, dann `db.AddAuditEntry(actor, …, channelName: normalized, details: …)`,
  dann `db.SaveChangesAsync`. **Keine** Abfrage auf `db.Emotes`, **kein** Schreiben an Emote-Zeilen.
- `details`: anonymes Objekt mit camelCase-Membern `emoteCount`, `sourceChannelName`, `sourceKind`.
  `emoteCount` ist die Anzahl **nach ordinaler Deduplizierung** der übergebenen 7TV-IDs (R9).
  `sourceChannelName` normalisiert oder `null`.
- Endpoint: Request-Record `internal sealed record SyncImportedRequest(IReadOnlyList<string> SevenTvEmoteIds, string? SourceChannelName, string SourceKind);`
  am Dateiende neben den beiden bestehenden. Antwort `204` bei Erfolg, `404` bei unbekanntem Kanal.
- Actor: `httpContext.User.TryBuildAuditActor()`, `null` → `Results.Unauthorized()`. **Exakt** wie
  `sync-restored` `:82-86` — die Prüfung steht **nach** der Body-Validierung, damit ein 400 nicht von
  einem 401 verdeckt wird und die 400-Fälle ohne Datenbank testbar bleiben.
- `RequireRateLimiting(RateLimitPolicyNames.Bookkeeping)` als Override, mit einem Kommentar in der
  Art der beiden Nachbarn: das 7TV-Schreiben ist schon passiert, ein 429 kostet nur den Papierweg.
- Kein `IRedisPublisher`, kein `channel.synced` (R10).
- **Detail-Projektion (R1).** `AuditLogDetail.Kinds` bekommt `ImportedFromChannel = "importedFromChannel"`
  und `ImportedFromFile = "importedFromFile"`. `ProjectDetail` prüft **vor** der `emoteCount`-Prüfung
  auf das Vorhandensein von `sourceKind` und liefert dann eines der beiden Kinds mit
  `Count = emoteCount` **und** `Text = sourceChannelName` (bei `"file"`: `Text = null`, Kind
  `ImportedFromFile`). Die Reihenfolge ist verhaltensrelevant — steht die Prüfung hinter
  `emoteCount`, gewinnt jene und die Herkunft fällt still weg; das gehört als Kommentar an die
  Stelle. Fehlt `emoteCount` im Payload oder ist `sourceKind` unbekannt, degradiert die Methode wie
  überall sonst auf den nächsten Zweig statt zu werfen. `Text` unterliegt derselben
  `MaxDetailTextLength`-Kürzung wie `title`.

**Validierung im Handler** (es gibt kein Body-Validierungsmuster in `Validation/` — dort liegen nur
Routen-/Query-Sachen; alle Body-Prüfungen im Repo stehen im Handler, s. `:39-42`, `:77-80`):
- `SevenTvEmoteIds` null oder leer → `400` mit `ApiErrorCodes.EmoteIdsEmpty` (bestehender Code, in
  beiden Locales vorhanden — der im Issue genannte `ValidationFailed` existiert nicht).
- `SourceKind` nicht in `{ "channel", "file" }` (ordinaler Vergleich) → `400` mit einem **neuen**
  Code, Vorschlag `InvalidSourceKind = "invalid_source_kind"`. Regel 7 verlangt dann im selben Commit:
  Eintrag in `KNOWN_API_ERROR_CODES` (`api-error.ts`) und `errors.api.invalid_source_kind` in
  `de.json` **und** `en.json`.
- `SourceChannelName` gesetzt, aber nicht `ChannelNameValidation.IsValid` → `400` mit
  `ApiErrorCodes.InvalidChannelName` (R6).
- **Keine** Obergrenze für die Listenlänge: `sync-restored` hat keine, die Payload wird nur gezählt,
  und Kestrels Body-Limit begrenzt sie ohnehin. Bewusst entschieden, nicht vergessen.

**Grenzfälle**
- Zwei identische 7TV-IDs im Body → `emoteCount = 1`.
- `SourceKind = "file"` ohne `SourceChannelName` → gültig, `sourceChannelName: null`.
- `SourceKind = "Channel"` (Großbuchstabe) → `400`. Der Vergleich ist ordinal, der Vertrag ist
  kleingeschrieben; das ist strenger als nötig und deshalb im Kommentar zu begründen.
- Unbekannter Zielkanal → `404`, **kein** Audit-Eintrag.
- Ein zweiter, identischer Aufruf schreibt eine zweite Zeile. Das ist beabsichtigt (das Log
  protokolliert Meldungen, nicht Zustände — dieselbe Begründung wie bei `sync-deleted` `:39-43`).

**Tests — zuerst schreiben, rot sehen, dann implementieren**

`tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs` (an die bestehende Klasse anhängen):
1. `MarkImportedAsync` mit 2 IDs → genau ein Eintrag mit Aktion `emotes.syncImported` am Zielkanal;
   `DetailsJson` enthält `emoteCount` 2, den normalisierten `sourceChannelName` und `sourceKind`.
2. Derselbe Aufruf ändert keine `Emote`-Zeile: Anzahl, `IsArchived` und `ArchivedAt` der vorher
   angelegten Zeilen sind unverändert; auch keine neue Zeile entstanden.
3. Doppelte ID im Aufruf → `emoteCount` 1.
4. Unbekannter Kanal → `false`, und `AuditLogEntries` hat für diesen Namen keine Zeile.

`tests/EmotePurge.Infrastructure.Tests/Integration/AuditLogQueryServiceTests.cs` (R1):
4b. Ein `emotes.syncImported`-Eintrag mit `{ emoteCount, sourceChannelName, sourceKind: "channel" }`
    projiziert auf Kind `importedFromChannel` mit **beiden** Feldern (`Count` und `Text`) — nicht auf
    `emoteCount`. Das ist der Test, der die Präzedenz pinnt.
4c. Derselbe Eintrag mit `sourceKind: "file"` und ohne `sourceChannelName` → Kind
    `importedFromFile`, `Count` gesetzt, `Text` null.
4d. Ein Bestandseintrag mit nacktem `emoteCount` (etwa `emotes.syncRestored`) projiziert unverändert
    auf `emoteCount` — die Gegenprobe, dass die neue Vorabprüfung nichts kaputt macht.

`tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`:
5. Neue `InlineData("POST", "/api/channels/testchannel/emotes/sync-imported")` im 401-Inventar.
6. `403` ohne Kanalrecht (Muster `SyncRestored_Answers403_ForACallerWithoutUsageStatsAccess`).
7. `400` + `emote_ids_empty` bei leerem Body (Muster `SyncRestored_Answers400_WhenTheBodyCarriesNoEmoteIds`;
   der leere Body `{}` bindet `SevenTvEmoteIds` auf null).
8. `400` + `invalid_source_kind` bei `SourceKind = "x"` mit nichtleerer ID-Liste.

Neue Datei in `tests/EmotePurge.Api.Tests/` (Vorschlag `EmoteRoutePolicyTests.cs`), Muster
`LiveRouteStructureTests`:
9. `sync-imported` trägt `Bookkeeping`, nicht `InteractiveRead`.
10. Derselbe Assert für `sync-restored` — damit deckt der Test auch die Bestandsrouten ab und ist
    seinerseits gegen einen kaputten Assert abgesichert (er muss dort grün werden, bevor Fall 9
    aussagekräftig ist).
11. Gegenprobe: `GET …/emotes` trägt `InteractiveRead`.

Der neue Fehlercode zieht `api-error-locales.spec.ts` mit — dieser Test wird ohne die
Locale-Einträge rot und ist damit das Gate für Regel 7.

**Definition of Done**
- `dotnet test EmotePurge.slnx` grün, `npm --prefix web test -- --watch=false` grün
  (wegen `api-error-locales.spec.ts`).
- `dotnet format EmotePurge.slnx` und `npm --prefix web run format` sauber.
- `sync-imported` fasst nachweislich keine Emote-Zeile an (Test 2).

---

### T3 — Audit-Aktion im Frontend (Spur B, parallel zu Spur A)

**Ziel:** Die neue Aktion hat in beiden Sprachen ein Label und taucht im Filter eines Kanal-Logs auf.

**Betroffene Dateien**
- `web/src/app/core/audit/audit.model.ts` (Union, `:15-28`)
- `web/src/app/shared/audit/audit-actions.ts` (`ACTION_KEYS`, `:13-27`)
- `web/src/app/shared/audit/audit-actions.spec.ts` (Zählungen `:29` und `:34`)
- `web/public/i18n/de.json`, `web/public/i18n/en.json` (`audit.actions.emotesSyncImported`)

**Vertrag**
- `'emotes.syncImported'` in die `AuditAction`-Union, direkt hinter `'emotes.syncRestored'`.
- `'emotes.syncImported': 'audit.actions.emotesSyncImported'` in `ACTION_KEYS`, an derselben Stelle.
- `CHANNEL_SCOPED_ACTIONS` **nicht** anfassen: es ist abgeleitet (`:40-42`) und nimmt die Aktion
  automatisch auf, weil sie nicht in `CHANNELLESS_ACTIONS` steht. `CHANNELLESS_ACTIONS` bleibt bei
  zwei Einträgen.
- Locale-Text: kurz, im Stil der Nachbarn (`de`: „Emotes wiederhergestellt" → sinngemäß „Emotes
  importiert"; `en` analog). Beide Dateien halten dieselbe Schlüsselreihenfolge.
- **Detailanzeige (R1, entschieden: wird gebaut).** Zusätzlich zu den Aktions-Schlüsseln:
  `AuditDetailKind` um `'importedFromChannel' | 'importedFromFile'` erweitern; beide in
  `DETAIL_KEYS` auf `audit.details.importedFromChannel` / `.importedFromFile` abbilden;
  `renderDetail` (`audit-row.ts:47-57`) so ändern, dass es **beide** Parameter durchreicht, wenn
  `count` und `text` gesetzt sind — die drei bestehenden Kinds setzen weiterhin genau einen und
  müssen sich unverändert verhalten. Locale-Texte in `de` und `en`:
  „{{count}} Emotes aus {{title}}" bzw. „{{count}} Emotes aus einer Datei" (sinngemäß, Stil der
  Nachbarn). Der Server liefert diese Kinds — das ist Task T2, nicht dieser Task; hier entsteht nur
  die Anzeigeseite. **Reihenfolge:** T3 darf vor T2 fertig sein, ein unbekannter `kind` rendert
  heute schon nichts.

**Grenzfälle**
- Der Assert `audit-actions.spec.ts:52` listet die bekannten Detail-Kinds
  (`['emoteCount','removedEntries','title']`) und **muss** um die zwei neuen erweitert werden —
  das ist der Test, der die Erweiterung überhaupt erzwingt. (Die erste Planfassung sagte hier das
  Gegenteil; sie ging von einer verworfenen Entscheidung aus.)
- `renderDetail` darf für die drei Bestandskinds keinen zusätzlichen Parameter erfinden: ein
  `title: ''` neben einem `count` würde bei `emoteCount` eine leere Interpolation einschleusen.
  Bedingt beide Parameter setzen, nicht unbedingt.
- Die Zählung 11→12 steht auf Zeile **34**, nicht 33 — das Issue nennt die falsche Zeile.

**Tests — zuerst anpassen, rot sehen**
1. `audit-actions.spec.ts`: 13 → 14 (`:29`).
2. `audit-actions.spec.ts`: 11 → 12 (`:34`).
3. Die bestehende `it.each`-Schleife über `ACTION_KEYS` prüft die Übersetzung in beiden Sprachen
   automatisch — kein neuer Fall nötig, aber der neue Eintrag muss dort grün werden.

**Definition of Done**
- `npm --prefix web test -- --watch=false` grün, `npm --prefix web run lint` grün,
  `npm --prefix web run format` sauber.

---

### T4 — `EmoteAdminService`: `listEmotes` und `syncImported` (Spur B, parallel zu T3)

**Ziel:** Der Import-Dialog kann die Zielset-Liste laden und den Nachlauf melden.

**Betroffene Dateien**
- `web/src/app/core/emotes/emote-admin.service.ts`
- `web/src/app/core/emotes/emote-admin.service.spec.ts`
- ggf. `web/src/app/core/emotes/emote-list.model.ts` (neu, falls die Modelle nicht in die
  Service-Datei sollen — die Nachbarn `duplicate-emote-name.model.ts` und `emote-set-status.model.ts`
  liegen als eigene Dateien, die Request-/Response-Formen der `sync-*`-Aufrufe stehen dagegen oben in
  der Service-Datei; beides ist Bestandsmuster, entscheiden und begründen)

**Vertrag**
- `listEmotes(channelName: string): Observable<EmoteListItem[]>` — `GET /api/channels/${channelName}/emotes`.
  Der Server antwortet `{ emotes: [...] }`; die Methode packt aus (`map`), damit die Aufrufer nicht
  jedes Mal den Wrapper kennen. `EmoteListItem` = `{ sevenTvEmoteId: string; name: string }`.
- `syncImported(channelName: string, body: SyncImportedBody): Observable<void>` —
  `POST /api/channels/${channelName}/emotes/sync-imported`,
  `SyncImportedBody` = `{ sevenTvEmoteIds: string[]; sourceChannelName: string | null; sourceKind: 'channel' | 'file' }`.
  Antwort `204`, also `Observable<void>`.
- Beide Methoden neben `getSetStatus`/`getSetWarning` einsortieren, mit JSDoc im Stil der Nachbarn
  (englisch, sagt **warum** die Methode existiert, nicht was sie tut).
- Kein eigenes Caching, kein `shareReplay`: der Import-Dialog holt die Liste genau einmal pro Öffnen.

**Grenzfälle**
- Leere Liste vom Server → leeres Array, kein Fehler.
- `sourceChannelName: null` muss als `null` im Body landen, nicht weggelassen werden — der Server
  liest ein `string?`, und ein fehlendes Feld ist dasselbe, aber der Test soll die explizite Form
  festnageln.

**Tests — zuerst schreiben, rot sehen**
`emote-admin.service.spec.ts`, Muster der vier bestehenden Fälle (`HttpTestingController`,
`httpMock.verify()` im `afterEach`):
1. `listEmotes` sendet `GET` an die richtige URL und liefert das ausgepackte Array.
2. `syncImported` sendet `POST` an die richtige URL mit exakt dem erwarteten Body (inkl. `null`).
3. `syncImported` mit `sourceKind: 'file'` und `sourceChannelName: null` — belegt, dass die Form
   beide Herkünfte trägt.

**Definition of Done**
- `npm --prefix web test -- --watch=false` grün, Lint und Format sauber.

---

### T5 — Doku: `DECISIONS.md` (nach T1–T4)

**Ziel:** Der Vertragswechsel ist im Entscheidungslog festgehalten (Regel 3).

**Betroffene Dateien:** `docs/DECISIONS.md`

**Vertrag**
- **Erst `head -30 docs/DECISIONS.md` lesen** (die Sitzung zu #69 schreibt parallel dorthin, R11),
  dann den eigenen Eintrag in absteigender Datumsordnung einfügen.
- Überschrift `### <Datum> — <Titel>`, danach die `**Betrifft:**`-Zeile mit allen berührten Pfaden
  (`EmoteEndpoints.cs`, `IEmoteListQueryService.cs`, `EmoteListQueryService.cs`, `IEmoteService.cs`,
  `EmoteService.cs`, `AuditLogEntry.cs`, `ApiErrorCodes.cs`, `api-error.ts`, beide Locales,
  `audit.model.ts`, `audit-actions.ts`).
- Inhalt, in Prosa: die zwei neuen Endpunkte; dass `sync-imported` **nur** Audit schreibt und
  ausdrücklich kein Vorbild an `sync-restored` nimmt; die ordinale Sortierung im Speicher statt in
  SQL samt Begründung; der neue Fehlercode; und — als der Befund, der am ehesten wieder aufschlägt —
  dass `ProjectDetail` genau **ein** Detail je Zeile liefert und die Import-Prüfung deshalb
  **vor** der `emoteCount`-Prüfung stehen muss, damit die Herkunft nicht still wegfällt — samt der
  zwei neuen Detail-Kinds, die Anzahl und Herkunft gemeinsam tragen (R1).
- **Nicht** in `CLAUDE.md` schreiben; das Log ist der Ort (s. Kopf von `DECISIONS.md`).
- A16 in `docs/Feature-Ideen-2026-08-01.md` bleibt diesem Task **fern** — die Statuszeile gehört zu
  dem Kind, das das Feature sichtbar macht, nicht zu den Backend-Handgriffen.

**Definition of Done:** Eintrag steht in der richtigen Datumsordnung, `grep EmoteEndpoints.cs docs/DECISIONS.md`
findet ihn.

---

### T6 — Live-Verifikation (Regel 16, zuletzt, nicht delegierbar an eine Suite)

**Ziel:** Beide Endpunkte einmal gegen die echte lokale Api mit echtem Postgres/Redis gesehen haben.

**Ablauf**
1. `docker compose up -d postgres redis`
2. `dotnet run --project src/EmotePurge.Api` (Port `5151`; braucht die User-Secrets im
   `EmotePurge.Api`-Projekt — fehlende `ClientId` äußert sich als `unexpected_error` beim Login)
3. Im Browser `http://localhost:5151/api/auth/twitch/login`, danach mit der Session-Cookie:
   - `GET /api/channels/<eigener Kanal>/emotes` → 200, Liste plausibel gegen das Usage-Grid,
     Sortierung ordinal.
   - `GET /api/channels/<nicht getrackter Name>/emotes` → 404 (nicht 403, nicht 500).
   - `POST /api/channels/<eigener Kanal>/emotes/sync-imported` mit zwei echten 7TV-IDs,
     `sourceKind: "channel"` → 204.
   - Danach im Admin-Audit-Log: genau eine neue Zeile, Label in de und en vorhanden, Detail „2 Emotes".
   - In der Datenbank gegenprüfen: `select count(*) from "Emotes" where …` unverändert.
   - `POST` mit leerer Liste → 400 `emote_ids_empty`; mit `sourceKind: "x"` → 400 `invalid_source_kind`.
4. Api beenden, **bevor** E2E läuft (die Suite scheitert irreführend, wenn auf `:5151` eine Api lauscht).

**Definition of Done:** Alle sechs Aufrufe wie beschrieben beobachtet, mit Notiz im PR/Commit.

---

## Gates

Am Ende, in dieser Reihenfolge:

1. `dotnet build EmotePurge.slnx` — bei Warnungsfragen `--no-incremental`, sonst meldet ein
   inkrementeller Lauf nichts mehr.
2. `dotnet test EmotePurge.slnx` — braucht laufendes Docker (Testcontainers).
3. `npm --prefix web test -- --watch=false`
4. `npm --prefix web run lint`
5. `dotnet format EmotePurge.slnx` und `npm --prefix web run format` — beides prüft die CI.
6. `npm --prefix web run e2e` — **nur** wenn auf `:5151` keine Api lauscht. Formal nicht gefordert
   (dieser Branch ändert keine UI), aber der Lauf ist billig; rote Fälle bei auffälliger Laufzeit
   (> 2 min) sind Speicherdruck, keine Regression.
7. Live-Verifikation aus T6 (Regel 16 — keine Suite ersetzt sie bei Backend-Features).
8. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` vor dem Merge.
   Ohne `--scope` reviewt Codex den Working Tree und gibt bei sauberem Baum eine falsche Entwarnung
   mit Exit 0.

---

## Entscheidungen des Orchestrators (2026-09-05)

Alle drei vormals offenen Fragen sind entschieden; die Abschnitte oben sind bereits danach
geschrieben.

**F1 — Herkunft im Audit-Log: sichtbar.** Umgesetzt wie in R1 beschrieben (zwei neue Detail-Kinds,
Präzedenz vor `emoteCount`, eine Zeile in `renderDetail`, zwei Locale-Paare). Begründung: #71
Punkt 3 und die Akzeptanzkriterien 3 und 6 verlangen es, das Design-Dokument widerspricht nicht, und
die ursprüngliche Kostenschätzung beruhte auf der falschen Annahme, ein neuer `Kind` koste die
Anzahl. Für einen Import ist die Herkunft nicht schmückendes Beiwerk, sondern die eine Information,
die aus der Zeile sonst nicht rekonstruierbar ist — anders als bei `emotesSyncRestored`, wo die
Quelle immer der Kanal selbst ist.

**F2 — Fehlercode heißt `invalid_source_kind`** (`ApiErrorCodes.InvalidSourceKind`). Folgt der
Wortbildung von `invalid_vote_type` und `invalid_channel_name`. Für die leere Liste wird der
**bestehende** `EmoteIdsEmpty` verwendet — der im Issue genannte `ValidationFailed` existiert nicht
(s. Verifikation des Ist-Zustands).

**F3 — `SourceKind` streng kleingeschrieben**, `"Channel"` ist ein 400. Bewusst strenger als die
Kanalnamen-Normalisierung: der einzige Aufrufer ist unser eigenes Frontend, und ein stiller
Groß-/Kleinschreibungs-Fallback würde einen Frontend-Fehler verdecken statt ihn zu zeigen.
