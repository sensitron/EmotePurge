# Channel-Identität: Twitch-ID statt Name — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code** (Repo-Regel seit 2026-08-29): Er beschreibt Verträge, Grenzfälle und Messlatten; Signaturen und Feldnamen sind verbindlich, Methodenrümpfe schreibt der Implementer.

**Goal:** Ein Twitch-Kanal, den es unter seinem alten Login nicht mehr gibt, erscheint nicht länger
als Geisterkanal in der Übersicht (GitHub-Issue #34) — und ein Kanal, der nach einem Rename unter
neuem Login weiterläuft, bleibt in Emote Purge **derselbe** Channel: gleiche Zeile, gleiche
Emotes, gleiche Nutzungshistorie, nur der Name zieht nach. Verwaiste Zeilen mit totem Login
werden erkennbar statt still endlos re-joint; ein bereits entstandenes Duplikat wird ohne
Historienverlust zusammengeführt.

**Architecture:** Zwei getrennte Wirkorte desselben Grundfehlers „Login als Identität". **(A) Der
Anzeigepfad** — der real gemeldete Fall: `MyChannelsService` baut `/mine`-Einträge aus den
7TV-Editor-Grants und nutzt davon nur die Logins; 7TVs eigene Login-Kopie überlebt Twitch-Renames
und -Löschungen, also erscheinen tote Kanäle ganz ohne DB-Beteiligung. Fix: die Grants behalten
ihre Login↔ID-**Paare**, und `/mine` prüft ungetrackte Grant-IDs gegen Helix — tot wird gar nicht
angezeigt, umbenannt unter dem neuen Namen. **(B) Der Datenpfad** — der schwerere, präventiv
abzudichtende Fall: die unveränderliche numerische Twitch-ID (`Channel.TwitchChannelId`) wird zur
Identität, wo sie bekannt ist; `ChannelName` bleibt die veränderliche **Adresse** (IRC-Join,
Routen, Cache-Keys). Dazu ein Helix-Users-Lookup im `ITwitchHelixClient` (App-Token, kein Scope),
ein `IChannelIdentityService` (Abgleich, Rename-Nachführung, ID-Backfill, bewachter Merge),
periodisch getrieben von einem neuen Worker-Hosted-Service, und ein Join-Pfad, der Identität
**vorab** auflöst. **Keine Schema-Migration**: Spalte und Unique-Index existieren seit
`InitialCreate`; die „Migration über Bestandsdaten" ist ein selbstheilender Backfill zur Laufzeit.

**Tech Stack:** .NET 10 (Minimal API, EF Core/Npgsql, xUnit + NSubstitute + Testcontainers),
Angular 22 (nur i18n-/Fehlercode-Berührung), Redis Pub/Sub (`BotCommands`), Redis-Cache
(`ModRoleCache`).

**Spec:** GitHub-Issue #34 („An orphaned 7tv channel is shown after the twitch channel was
renamed") plus der folgende, am 2026-08-29 am Code verifizierte Befund. Die
Architektur-Begründung wandert per Regel 3 in `docs/DECISIONS.md` (Task 5).

## Antworten des Betreibers (2026-08-29, eingearbeitet)

1. **Join toter Logins ablehnen: ja.** 404 mit `channel_not_on_twitch` („der Channel existiert ja
   nicht mehr, ist nur noch auf 7TV angezeigt") → Task 7.
2. **Reconcile-Intervall 60 Minuten reicht** („kommt sowieso nicht oft vor") → Task 6, Default fix.
3. **Der einzige aktuell bekannte betroffene Channel ist nicht gejoint.** Das verschiebt das
   Zentrum des Plans: Das gemeldete Symptom entsteht mit hoher Wahrscheinlichkeit im
   **Anzeigepfad (A)**, nicht im Datenpfad (B) — Beleg und Restunsicherheit im Befund, Klärung
   in Task 1.
4. **Nachtrag am selben Tag: der Fall ist eine Umbenennung, kein toter Kanal.** 7TV-Nutzer
   `01HB16F5BR000CK4GBCA21FBAR` zeigt weiterhin den alten Login `affeoderwatt`; auf Twitch heißt
   der Kanal jetzt `affeaufbike`. Der Grant trägt die Twitch-ID mit
   (`SevenTvEditorGrant(TwitchChannelLogin, TwitchChannelId)`), wir verwerfen sie. Für Antwort 1
   heißt das: der 404-Zweig bleibt richtig, greift für **diesen** Kanal aber nicht — er lebt.
   Details und Erwartung in Task 1.

## Befund (am 2026-08-29 am Code verifiziert)

**Belegt durch Lesen des Bestands:**

| # | Aussage | Ort |
|---|---|---|
| 1 | `TwitchChannelId` ist `string?`, `ChannelName` NOT NULL; beide unique indiziert (Postgres: beliebig viele NULLs im Unique-Index) | `src/EmotePurge.Core/Entities/Channel.cs:6-7`, `AppDbContext.cs:22-23`, Index seit `20260724135718_InitialCreate.cs:82-84` |
| 2 | Der **einzige** DB-Lookup-Pfad filtert auf `ChannelName`; es gibt keine Suche per `TwitchChannelId` | `src/EmotePurge.Infrastructure/Persistence/ChannelQueries.cs:26-40` |
| 3 | **Anzeigepfad:** `MyChannelsService` erzeugt aus jedem Editor-Grant-Login einen eigenen `/mine`-Eintrag (`GetOrAdd`, auch für völlig unbekannte Namen); die mitgelieferte `TwitchChannelId` wird ignoriert | `MyChannelsService.cs:58-64`, `SevenTvModels.cs:63` |
| 4 | Die Login↔ID-**Paarung** geht genau in `SevenTvEditorService` verloren: aus `IReadOnlyList<SevenTvEditorGrant>` (Paare) werden zwei ungepaarte Sets; der Redis-Cache speichert dieselben zwei Listen | `SevenTvEditorService.cs:40-43`, `ISevenTvEditorService.cs:13`, `ModRoleCache.cs:27-36,97` |
| 5 | Die Übersicht **rendert** ungetrackte Einträge (ohne Link/Hover, aber sichtbar mit Badges) — ein Grant mit totem Login wird also angezeigt, ganz ohne `Channel`-Zeile. Das deckt sich wörtlich mit „ist nur noch auf 7TV angezeigt" | `web/src/app/features/overview/overview-page.html:64-70` |
| 6 | `JoinAsync` legt bei unbekanntem Namen eine neue Zeile mit `TwitchChannelId = null` an; `LeaveAsync` **behält** die Zeile (Soft-Deactivate) — „nicht gejoint" schließt eine existierende, inaktive Zeile nicht aus | `ChannelService.cs:11-44,46-73` |
| 7 | Der 7TV-Sync schreibt die ID write-once (`??=`), nie ein Update bei Abweichung; die ID stammt aus einer 7TV-GQL-Nutzersuche per Name — Drittanbieter-Daten, potenziell veraltet | `SevenTvSyncService.cs:34-36,74`, `SevenTvApiClient.cs:41-71` |
| 8 | `ChannelAccessService` prüft Broadcaster/Editor per ID, wo vorhanden, mit Login-Fallback; der Rename-Fall ist dort im Kommentar bereits benannt | `ChannelAccessService.cs:80-108` |
| 9 | Ein IRC-Join auf einen toten Namen scheitert still: nur `OnFailureToReceiveJoinConfirmation` + Log-Warnung, `EnsureJoinedAsync` wiederholt minütlich; der Roster publiziert `JoinConfirmed` aber bereits pro Channel | `TwitchChatManager.cs:394-402`, `SevenTvPeriodicResyncWorker.cs:55-63`, `ITwitchChatManager.cs`, `WorkerRosterPublisher.cs` |
| 10 | `TwitchLivePollWorker` fragt Helix-Streams per **Login** ab — ein umbenannter Kanal ist dort schlicht abwesend, ununterscheidbar von „offline" | `TwitchLivePollWorker.cs:57-77` |
| 11 | Nirgends wird ein gespeicherter `ChannelName` aktualisiert (repo-weiter Grep über alle Schreibstellen) | — |

**Die Duplikat-Kette im Datenpfad, vollständig belegt:** Joint jemand den neuen Namen eines
umbenannten, bereits getrackten Kanals, entsteht eine zweite Zeile (Befund 6). Deren erster Sync
löst per 7TV dieselbe Twitch-ID auf, `channel.TwitchChannelId ??= twitchUserId` (Zeile 74)
greift, und `SaveChangesAsync` (`SevenTvSyncService.cs:94`) läuft in die `DbUpdateException` des
Unique-Index `IX_Channels_TwitchChannelId`, den die alte Zeile hält. `SyncChannelAsync` fängt
nichts; die Aufrufer fangen und loggen nur (`SevenTvPeriodicResyncWorker.cs:82-86`,
`Worker.cs:88-93` — dessen Kommentar nennt exakt diese Exception-Klasse). **Folge, und für den
Merge zentral:** weil derselbe fehlgeschlagene `SaveChangesAsync` auch alle von `ReconcileAsync`
eingefügten Emotes zurückrollt, persistiert die Duplikat-Zeile **nie** Emotes — und damit keine
`UsageStat`s und keine sinnvollen Votes. Der EventAPI-Delta-Pfad kann ebenfalls nichts schreiben
(`ActiveEmoteSetId` bleibt leer → `SetNotActive`). Was sie sehr wohl ansammelt:
`ChannelLiveDay`-Zeilen (der Live-Poll läuft per Login und kennt den neuen Namen).

**Am echten Fall gemessen (2026-08-29, Helix `GET /users` mit App-Token):**

| Abfrage | Antwort |
|---|---|
| `id=955448938` (die ID aus dem 7TV-Grant) | `login=affeaufbike`, Partner, `created_at=2023-09-11` |
| `login=affeoderwatt` (was 7TV uns liefert) | **leeres `data`-Array** |
| `login=affeaufbike` | `id=955448938` — dasselbe Konto |

Das belegt die beiden Vertragsseiten aus Task 2 an Produktionsdaten: **fehlender Eintrag in einer
erfolgreichen Antwort = dieser Login existiert nicht mehr**, und die ID überlebt den Rename
unverändert. Zugleich ist es die Messlatte für Task 3: der Grant dieses Nutzers muss nach dem Fix
als **`affeaufbike`** in der Übersicht stehen — nicht verschwinden. 7TVs Kopie (`7tv.io/v3/users/
01HB16F5BR000CK4GBCA21FBAR` → `connection.username = affeoderwatt`) ist die veraltete Seite; die
ID im selben Objekt ist korrekt.

**Keine offene Annahme mehr:** Die Prod-Messung in Task 1 hat am selben Tag ergeben, dass für
*keinen* der beiden Logins und für die ID `955448938` eine `Channel`-Zeile existiert, und dass
alle 13 Zeilen des Bestands eine Twitch-ID tragen. Der gemeldete Fall liegt damit vollständig im
Anzeigepfad; der Datenpfad ist Prävention ohne Bestandsschaden.

## Entscheidungen

**1. Wo das gemeldete Symptom entsteht — und wo deshalb das Zentrum liegt.** Der Anzeigepfad (A)
erzeugt den Issue-Effekt nachweislich ohne jede DB-Zeile (Befunde 3–5); der einzige bekannte
reale Fall („nicht gejoint") passt exakt darauf. Der Fix im Anzeigepfad (Task 3) ist deshalb die
zentrale, früheste Code-Task. Der Datenpfad (B) bleibt vollständig im Plan — nicht als Reparatur
eines belegten Bestandsschadens, sondern **präventiv**: die Duplikat-Kette ist am Code
bewiesen und tritt in dem Moment ein, in dem ein *getrackter* Kanal umbenannt wird; ob Prod
heute schon betroffene Zeilen hat, klärt Task 1.

**2. Anzeigepfad: Grants behalten ihre Paare, `/mine` prüft gegen Helix — nicht der
Auth-Pfad.** `SevenTvEditorGrants` bekommt die Login↔ID-Paare zurück (Befund 4), und
`MyChannelsService` entscheidet pro Grant: kennt die **DB** die ID, gewinnt deren aktueller
`ChannelName` (deckt getrackte, bereits umbenannte Kanäle); sonst fragt **Helix** (App-Token,
gebatcht): Login existiert unter neuem Namen → Eintrag unter dem neuen Namen; ID existiert nicht
mehr → Eintrag entfällt (genau das Issue); Helix nicht erreichbar → heutiges Verhalten
(7TV-Login), stille Degradation. Bewusst **nicht** in `SevenTvEditorService` kanonisiert: die
Grants sind ein Autorisierungs-Input (`ChannelAccessService`), und der Auth-Pfad darf nicht von
der Verfügbarkeit eines zweiten Fremdsystems abhängen — die Autorisierung matcht ohnehin schon
per ID, wo die DB eine hat (Befund 8). Kosten: ein gebatchter Helix-Request pro `/mine`-Aufruf,
nur für ungetrackte Grant-IDs (typisch 0–5); der Endpoint ist bereits `ExternalApi`-rate-limited.

**3. Datenpfad-Auflösungsregel.** Neu: `ChannelQueries.LoadChannelByTwitchIdAsync`. Regel ab
jetzt: **Wo eine Operation die Identität eines Kanals meint (Join, Identitätsabgleich,
Berechtigung), wird per ID gesucht, sobald eine bekannt ist; der Name bleibt die Adresse** für
IRC, HTTP-Routen, `EmoteMatchCache`, Redis-Kommandos und Anzeige. Bestehende namensbasierte
Endpunkt-Lookups bleiben unangetastet — die Route trägt nun mal den Namen, und nach der
Nachführung ist der gespeicherte Name wieder korrekt.

**4. Signal für die Nachführung: Helix `GET /helix/users`, periodisch aus dem Worker.**
Begründung gegen die Alternativen: Der **7TV-Sync** kennt nur 7TVs Sicht auf den Twitch-Login —
Dritthand, nachweislich cache-verzögert (docs/Untersuchung-7TV-WebSocket-2026-07-30.md), und der
Sync hat mit Emote-Wahrheit genau eine Verantwortung, in die Identitäts-Mutation nicht gehört.
Der **Live-Poll** fragt per Login und kann einen Rename prinzipiell nicht sehen (Befund 10).
**`/mine`** feuert nur, wenn der betroffene Nutzer die Seite besucht, und sieht nur dessen
Kanäle. Helix `/users` ist Twitchs eigene, autoritative Antwort, ID→Login direkt, App-Token ohne
Scope, 100 pro Request. Intervall: **60 Minuten, vom Betreiber bestätigt** (Antwort 2).
Zusätzlich löst `JoinAsync` die Identität **vorab** über denselben Pfad auf (Task 7), damit
Duplikate gar nicht erst entstehen; die Periodik bleibt nötig, weil ein Rename auch ohne jeden
Join passiert. Die Reconciliation läuft über **aktive** Channels; inaktive Zeilen holt der
Join-Pfad nach (wer einen per Leave geparkten, inzwischen umbenannten Kanal re-joint, trifft per
ID die alte Zeile und führt sie nach).

**5. Merge-Fall: bewachter automatischer Merge.** Die Duplikat-Kette belegt: Solange die alte
Zeile die ID hält, kann die Duplikat-Zeile keine Emotes und damit keine Nutzungshistorie
persistieren. Der Merge läuft deshalb automatisch **nur** unter der zur Laufzeit geprüften
Invariante „die Verlierer-Zeile hat null `Emote`-Zeilen": Überlebende ist die Zeile mit der von
Helix bestätigten ID (sie trägt die Historie), sie übernimmt den neuen Namen; die
Verlierer-Zeile gibt ihre `ChannelLiveDay`s und etwaige `VoteSession`s ab und wird gelöscht.
Damit ist Historienverlust strukturell ausgeschlossen — es gibt nichts zu verlieren — und die
Unique-Indizes `(ChannelId, SevenTvEmoteId)` und `(EmoteId, Date)` werden nie berührt, weil
keine Emote-Zeile wandert. Hält die Invariante nicht (nach heutigem Code nicht erreichbar, aber
Bestandsdaten sind Bestandsdaten), wird **nicht** gemerged: Warnung mit beiden Zeilen-IDs, Skip,
manuelle Entscheidung. Ein vollgenerischer Merge mit UsageStat-Summierung über kollidierende
`(EmoteId, Date)`-Paare wäre Code für einen Fall, den der Bestand nicht erzeugen kann —
ungetestet im Ernstfall und selbst das Risiko. `ChannelLiveDay`-Kollisionen pro Datum:
**MAX(LiveMinutes)**, nicht Summe — beide Zeilen haben denselben physischen Kanal gemessen, eine
Summe könnte 1440 Minuten/Tag übersteigen; real kollidiert ohnehin fast nichts, weil der alte
Login für den Streams-Poll ab dem Rename tot ist.

**6. Bestandsdaten.** Prod wird **vor allem anderen** nur gemessen (read-only SQL über den
SSH-Tunnel, jetzt **Task 1**, weil das Ergebnis den realen Fall einordnet): existiert eine Zeile
für den bekannten toten Channel, wie viele Zeilen haben `TwitchChannelId IS NULL`, wie alt ist
deren `LastSyncedAtUtc`. Der Backfill selbst ist **kein** einmaliges Skript, sondern der erste
Lauf der periodischen Reconciliation nach dem Deploy — selbstheilend, idempotent, identisch
getestet wie der Dauerbetrieb. Logins, die Helix nicht mehr kennt, werden geloggt (einmal pro
Zustandswechsel, nicht pro Tick) und bleiben ansonsten unangetastet: automatisches Leave/Purge
wäre eine destruktive Entscheidung auf Basis eines möglicherweise transienten Signals (Bann kann
aufgehoben werden). Sichtbar sind sie heute schon über Admin-Channel-Liste
(`TwitchChannelId`-Spalte, stehengebliebenes `LastSyncedAtUtc`) und Worker-Roster
(`JoinConfirmed=false`).

**7. Stiller IRC-Join: gehört in Grundzügen hierher, der Ausbau nicht.** Die Erkennbarkeit
existiert zur Hälfte schon (Roster, Befund 9) — was fehlt, ist die **Interpretation** „dieser
Login existiert laut Twitch nicht mehr", und genau die liefert die Reconciliation als
Log-Warnung nebenbei mit (Task 5). Ein Admin-UI-Badge („Login tot seit …") bräuchte dagegen eine
persistierte Spalte, Migration, Frontend und i18n — das ist ein eigener, kleinerer Folgeschritt
und wird hier bewusst nicht gebaut (s. Nicht-Ziele).

## Nicht-Ziele (bewusst so entschieden)

1. **Kein `NOT NULL` auf `TwitchChannelId`** und keine Schema-Migration: Es wird immer
   Legacy-Zeilen geben können, deren Login tot ist, bevor je eine ID aufgelöst wurde.
2. **Kein automatisches Leave/Purge** toter Channels (Entscheidung 6); für die per Task 1
   identifizierte tote Zeile — falls es sie gibt — entscheidet der Betreiber manuell.
3. **Kein Admin-Badge/keine persistierte Orphan-Markierung** (Entscheidung 7) — Folgeschritt.
4. **Keine URL-Weiterleitung** alter Channel-Routen nach einem Rename: alte Bookmarks laufen auf
   404, wie überall im Web nach einem Twitch-Rename.
5. **Kein genereller UsageStat-Merge** über kollidierende Emote-Zeilen (Entscheidung 5) — der
   bewachte Merge weigert sich stattdessen laut.
6. **Kein eigener Cache und kein neues Degradations-Banner für die `/mine`-Helix-Prüfung**
   (Entscheidung 2): ein Request pro Seitenaufruf für eine Handvoll IDs, stille Degradation auf
   heutiges Verhalten; ein Banner für kosmetische 7TV-Staleness widerspräche der
   Frontend-Zurückhaltung.
7. **Keine Änderung an TwitchLib/IRC selbst** — der Join braucht weiterhin den Login, und der
   stimmt nach der Nachführung wieder.

## Koordination

- **Paralleler Plan `2026-08-29-sync-fehlergrund-sichtbar-machen.md`** (Issue #32) ändert
  `Channel.cs`, `SevenTvSyncService.cs` und `docs/DECISIONS.md`. Vor Task 4 und vor jedem
  DECISIONS-Schreiben die betroffenen Dateien **neu einlesen**; ist jener Plan schon gelandet,
  ändern sich hier Zeilennummern, aber keine Aussagen.
- Der Git-Status trägt unkommittete Änderungen fremder Arbeit (`SevenTvApiDtos.cs`,
  Usage-Stats-Frontend u. a.) — **nur die eigenen Dateien stagen**, atomar committen.

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1:** vor jedem `git commit` erst den Nutzer fragen — auch unter freigegebenem Plan.
- **Regel 2:** Conventional Commits, ein Commit je Task.
- **Regel 3:** Der DECISIONS-Eintrag zur Identitätsregel gehört in **denselben Commit wie Task 5**
  (dort ändert sich der Vertrag); die CLAUDE.md-Worker-Liste in den Commit von Task 6.
- **Regel 4:** kein `AppDbContext`/`IConnectionMultiplexer` aus Handlern; neue Fähigkeit =
  Interface in `Core/Services/` + Implementierung in `Infrastructure/Services/`. Kein Repository.
- **Regel 5/19:** Interfaces für Logik mit externer Abhängigkeit; `BackgroundService` ohne
  Injektion als konkrete Klasse; Member-Reihenfolge wie vorgegeben.
- **Regel 7:** neue Fehlercodes sprachneutral; Eintrag in `ApiErrorCodes.cs`, `api-error.ts`
  **und beiden** Locale-Dateien (`api-error-locales.spec.ts` erzwingt die hinteren zwei).
- **Regel 9:** jeder Namens-Schreib- und -Vergleichspfad läuft durch `ChannelName.Normalize`;
  Twitch-IDs werden **nicht** normalisiert (opake Ziffernstrings, `StringComparison.Ordinal`).
- **Regel 10:** Aggregat-/Umzugs-Queries im Merge erst auf skalare Listen reduzieren.
- **Regel 11:** Merge-, Reconcile- und `/mine`-Logik zwingend in `Integration/` mit
  Testcontainers — die Unique-Index-Fälle sieht ein Unit-Test nie. Kein neuer purer Policy-Typ im
  Worker → kein neuer `Worker.Tests`-Fall (der neue Hosted Service ist eine dünne Timer-Hülle um
  getestete Infrastructure-Logik, wie `TwitchLivePollWorker`).
- **Regel 16:** vor dem Commit live gegen echte Postgres/Redis/Twitch/7TV verifizieren (Task 8);
  Regel 15 (`--build`) beim Compose-Test beachten.
- **Sprache:** Bezeichner/Kommentare englisch, Log-/`throw`-Messages deutsch.
- **„Fertig" heißt:** `dotnet test EmotePurge.slnx` grün (Docker läuft),
  `npm --prefix web test -- --watch=false` grün; E2E nur nötig, falls doch UI berührt wird — dann
  ohne lauschende Api auf `:5151`.

## File Structure

```
src/EmotePurge.Core/
  Entities/AuditLogEntry.cs                    (M: AuditActions.ChannelRename/ChannelMerge)
  Services/AuditActor.cs                       (M: statischer System-Actor)
  Services/IChannelIdentityService.cs          (C: Interface + Ergebnistypen)
  Services/ISevenTvEditorService.cs            (M: Grants tragen Login↔ID-Paare)
  Twitch/ITwitchHelixClient.cs                 (M: GetUsersAsync)
  Twitch/TwitchModels.cs                       (M: TwitchUserIdentity)
src/EmotePurge.Infrastructure/
  Persistence/ChannelQueries.cs                (M: LoadChannelByTwitchIdAsync)
  Redis/ModRoleCache.cs                        (M: Cache-Format mit Paaren, abwärtskompatibel)
  Services/ChannelIdentityService.cs           (C)
  Services/ChannelService.cs                   (M: Join löst Identität vorab auf)
  Services/MyChannelsService.cs                (M: Grants per ID/Helix statt 7TV-Login)
  Services/SevenTvEditorService.cs             (M: Paare durchreichen)
  Services/SevenTvSyncService.cs               (M: konfliktbewusster Backfill)
  Twitch/TwitchHelixClient.cs                  (M: GetUsersAsync)
  ServiceCollectionExtensions.cs               (M: Registrierung)
src/EmotePurge.Worker/
  TwitchIdentityReconcileWorker.cs             (C)
  Program.cs                                   (M: AddHostedService)
src/EmotePurge.Api/
  Validation/ApiErrorCodes.cs                  (M: channel_not_on_twitch)
  Endpoints/ChannelEndpoints.cs                (M: Join-Handler reicht neues Ergebnis durch)
web/src/app/core/i18n/api-error.ts             (M) + web/public/i18n/de.json, en.json (M)
tests/EmotePurge.Infrastructure.Tests/
  Integration/ChannelIdentityServiceTests.cs   (C)
  Integration/ChannelServiceTests.cs           (M)
  Integration/ModRoleCacheTests.cs             (M)
  Integration/MyChannelsServiceTests.cs        (M)
  Integration/SevenTvSyncServiceTests.cs       (M)
docs/DECISIONS.md (M, im Task-5-Commit) · CLAUDE.md (M, im Task-6-Commit)
```

---

### Task 1: Bestandsmessung in Prod — ERLEDIGT am 2026-08-29 (read-only)

Kein Code. Die Messung ist gelaufen; dieser Abschnitt hält das Ergebnis, damit die
Umsetzungs-Reihenfolge begründet bleibt.

**Der reale Fall ist eine Umbenennung, kein gelöschter Kanal.** 7TV-Nutzer
`01HB16F5BR000CK4GBCA21FBAR` hält einen Editor-Grant, dessen Login noch `affeoderwatt` lautet;
`7tv.io/v3/users/…` liefert dazu `connection: TWITCH id=955448938 username=affeoderwatt`. Die ID
ist korrekt, der Name veraltet — und wir werfen laut Befund 3/4 genau die ID weg.

**Helix `GET /users` (App-Token), am 2026-08-29:**

| Abfrage | Antwort |
|---|---|
| `id=955448938` | `login=affeaufbike`, Partner, `created_at=2023-09-11` |
| `login=affeoderwatt` | leeres `data`-Array |
| `login=affeaufbike` | `id=955448938` — dasselbe Konto |

**Prod-DB (`docker exec emotepurge-postgres psql`, nur `SELECT`), am 2026-08-29:**

| Abfrage | Ergebnis |
|---|---|
| `Channels` mit `ChannelName ∈ {affeoderwatt, affeaufbike}` **oder** `TwitchChannelId = '955448938'` | **0 Zeilen** |
| `count(*)` über `Channels` | **13** |
| davon `TwitchChannelId IS NULL` | **0** (aktiv wie inaktiv) |

**Was das für den Plan festlegt:**

1. **Der gemeldete Fall lebt ausschließlich im Anzeigepfad.** Es gibt keine `Channel`-Zeile —
   weder aktiv noch per Leave geparkt. Annahme (a) aus dem Befund ist damit beantwortet: **Task 3
   behebt Issue #34 vollständig**, ohne dass eine der Datenpfad-Tasks nötig wäre.
2. **Der Datenpfad (Tasks 4–7) ist reine Prävention, ohne Bestandsschaden.** Keine Duplikat-Zeile,
   keine ID-lose Zeile — die Duplikat-Kette aus dem Befund ist am Code bewiesen, in Prod aber
   nicht eingetreten. Die Merge-Invariante aus Entscheidung 5 hat heute nichts zu bewachen.
3. **Der Backfill in Task 4 hat im Bestand nichts zu tun.** 0 von 13 Zeilen ohne ID heißt: der
   7TV-Sync hat die ID bisher lückenlos aufgelöst. Der Backfill bleibt trotzdem im Plan — er ist
   die Voraussetzung dafür, dass die ID-basierte Auflösung überhaupt greifen *kann*, wenn künftig
   doch eine Zeile ohne ID entsteht (Join eines Kanals, dessen 7TV-Auflösung scheitert).
4. **Die Messlatte für Task 8 steht fest:** der Grant muss nach dem Fix als **`affeaufbike`** in
   der Übersicht erscheinen, nicht verschwinden — tot ist der Login, nicht das Konto.

### Task 2: Helix-Users-Lookup

**Files:** `ITwitchHelixClient.cs`, `TwitchModels.cs`, `TwitchHelixClient.cs`.

**Interfaces (verbindlich):**
- `public record TwitchUserIdentity(string Id, string Login);`
- `Task<IReadOnlyList<TwitchUserIdentity>?> GetUsersAsync(IReadOnlyCollection<string> ids, IReadOnlyCollection<string> logins, string accessToken, CancellationToken ct = default)`
  — `GET /helix/users`, `id`- und `login`-Parameter gemischt, gebatcht zu 100 Parametern pro
  Request (Helix-Cap, gleiche Mechanik wie `GetLiveStreamsByLoginsAsync`). Vertrag im
  Interface-Kommentar festhalten: **`null` = irgendein Batch transient fehlgeschlagen** (Aufrufer
  darf daraus nichts ableiten und nichts cachen); **fehlender Eintrag in einer erfolgreichen
  Antwort = diese ID/dieser Login existiert nicht (mehr)** — genau diese Unterscheidung tragen
  Anzeigepfad und Reconciliation. Login-Werte der Antwort sind bereits lowercase; trotzdem beim
  Konsumenten normalisieren (Regel 9).

- [ ] **Step 1:** Record + Interface-Methode + Implementierung; Member-Reihenfolge und
  vorhandene DTO-/Fehlerbehandlungs-Muster der Klasse übernehmen (Batch-Fehler ⇒ Gesamt-`null`,
  wie beim Streams-Poll).
- [ ] **Step 2:** Kein eigener Testfile — der Helix-Client hat projektweit keinen (Transport wird
  per Regel 16 live verifiziert, Task 8); die Konsumentenlogik testen Task 3 und 5. Build grün.
- [ ] **Step 3:** Nutzer fragen, Commit `feat(twitch): resolve user identities via helix users
  endpoint`.

### Task 3: Anzeigepfad — Editor-Grants nach Identität statt nach 7TV-Login (der Issue-Fix)

**Files:** `Core/Services/ISevenTvEditorService.cs`, `Infrastructure/Services/SevenTvEditorService.cs`,
`Infrastructure/Redis/ModRoleCache.cs`, `Infrastructure/Services/MyChannelsService.cs`;
Tests: `Integration/MyChannelsServiceTests.cs`, `Integration/ModRoleCacheTests.cs`.

**Interfaces (verbindlich):**
- `public record SevenTvEditorGrantEntry(string ChannelLogin, string TwitchChannelId);` — Login
  normalisiert; lebt neben `SevenTvEditorGrants` in `ISevenTvEditorService.cs`.
- `SevenTvEditorGrants` bekommt eine dritte Komponente
  `IReadOnlyList<SevenTvEditorGrantEntry> Entries`; die beiden Sets bleiben (aus den Entries
  abgeleitet), damit `ChannelAccessService` **unverändert** bleibt — Auth-Pfad unberührt
  (Entscheidung 2). XML-Doc des Records erklärt: Sets für Autorisierung, Entries für die
  Übersicht, die pro Grant entscheiden muss.
- `ModRoleCache`-Format: `StoredEditorGrants` trägt zusätzlich die Paare.
  **Abwärtskompatibilität ist Vertrag:** ein alter Cache-Eintrag ohne Paare darf weder crashen
  noch als „keine Grants" lesen — leere `Entries` bei nicht-leeren `ChannelLogins` heißt
  „Legacy-Payload", und `MyChannelsService` fällt dann auf das heutige Login-Verhalten zurück,
  bis der Eintrag per TTL ausläuft.

**`/mine`-Vertrag (ersetzt die Login-Schleife `MyChannelsService.cs:61-64`):** Die
Tracked-Channels-Query wird um `TwitchChannelId` erweitert und filtert auf
`ChannelName ∈ bekannte Namen ∪ TwitchChannelId ∈ Grant-IDs` (eine Query, skalare Projektion).
Pro Grant-Entry dann genau ein Ausgang:

1. **DB kennt die ID** → Editor-Flag unter dem **aktuellen `ChannelName` der DB-Zeile** (deckt
   getrackte, bereits umbenannte Kanäle; kein Helix-Aufruf nötig).
2. **DB kennt die ID nicht** → die IDs aller solcher Entries in **einem**
   `GetUsersAsync`-Aufruf (App-Token via `ITwitchAppTokenProvider`, neue Konstruktor-Abhängigkeit;
   `ITwitchHelixClient` ist schon injiziert) auflösen:
   Helix kennt die ID → Flag unter dem kanonischen (normalisierten) Login — ein umbenannter,
   ungetrackter Kanal erscheint unter seinem **neuen** Namen;
   Helix kennt die ID nicht → **kein Eintrag** (der tote Kanal aus Issue #34 verschwindet);
   `GetUsersAsync` liefert `null` oder kein App-Token → Flag unter dem 7TV-Login (heutiges
   Verhalten, stille Degradation mit `LogInformation`, kein neues DTO-Feld — Nicht-Ziel 6).
3. Legacy-Cache ohne Entries (s. o.) → alle Grants wie heute per Login.

- [ ] **Step 1 (Tests zuerst, `MyChannelsServiceTests` mit `PostgresFixture` + Substitutes):**
  Messlatten je Ausgang: toter Grant (Helix antwortet, ID unbekannt) → **kein** Eintrag im
  Ergebnis; getrackter umbenannter Kanal (DB-Zeile per ID, anderer aktueller Name) → genau ein
  Eintrag unter dem aktuellen Namen mit `IsSevenTvEditor = true`, kein Orphan unter dem alten
  Login; ungetrackter umbenannter Kanal (kein DB-Treffer, Helix kennt die ID unter neuem Login)
  → Eintrag unter dem neuen Namen; Helix-`null` → Eintrag unter dem 7TV-Login (Regressionsfall);
  Legacy-Grants ohne Entries → heutiges Verhalten. Gemischt getippte Logins füttern (Regel 9).
- [ ] **Step 2:** `ModRoleCacheTests` erweitern: Roundtrip des neuen Formats erhält die Paare;
  ein von Hand in Redis gelegter Legacy-JSON (nur zwei Listen) liest als Grants mit leeren
  Entries, nicht als Miss und nicht als Exception.
- [ ] **Step 3:** Umsetzung Record/Service/Cache/`MyChannelsService`; `SevenTvEditorService`
  reicht die Paare aus `GetEditorOfChannelsAsync` durch (die Normalisierungsstelle
  `SevenTvEditorService.cs:40-43` bleibt die einzige).
- [ ] **Step 4:** Infrastructure-Suite grün; bestehende `ChannelAccessService*`-Tests unverändert
  grün (Beleg, dass der Auth-Pfad unberührt ist).
- [ ] **Step 5:** Nutzer fragen, Commit `fix(mine): resolve seventv editor grants by twitch id
  instead of stale logins` — **das ist der Commit, der Issue #34s gemeldetes Symptom behebt.**

### Task 4: Lookup per Twitch-ID + konfliktbewusster Backfill im 7TV-Sync

**Files:** `ChannelQueries.cs`, `SevenTvSyncService.cs`; Test:
`Integration/SevenTvSyncServiceTests.cs`. Vorher Koordination beachten (Issue-#32-Plan berührt
dieselbe Datei).

**Interfaces (verbindlich):**
- `internal static Task<Channel?> LoadChannelByTwitchIdAsync(this AppDbContext db, string twitchChannelId, CancellationToken ct)`
  — tracked, `SingleOrDefaultAsync` auf `TwitchChannelId`, keine Normalisierung. XML-Doc erklärt
  die Identitäts-/Adress-Regel aus Entscheidung 3 (analog dem bestehenden Klassenkommentar).

- [ ] **Step 1 (Test zuerst):** In `SevenTvSyncServiceTests` den Duplikat-Fall gegen echtes
  Postgres nachstellen: Zeile A mit `TwitchChannelId = "111"`, Zeile B ohne ID unter anderem
  Namen; der 7TV-Client-Substitute löst für B dieselbe ID `"111"` auf. Messlatte: der Sync von B
  wirft **keine** Exception mehr, gibt `null` zurück, persistiert **keine** Emotes für B und
  lässt `B.TwitchChannelId` null. Dieser Test ist zugleich der Beleg der Befund-Kette — vor dem
  Fix muss er mit `DbUpdateException` rot sein (kurz nachweisen, dann Fix).
- [ ] **Step 2:** `LoadChannelByTwitchIdAsync` ergänzen; ein zweiter Testfall deckt Treffer und
  Nicht-Treffer ab (kein eigener ReadOnly-Zwilling, solange es keinen reinen Lese-Konsumenten
  gibt — im Zweifel dort nachziehen, wo er gebraucht wird).
- [ ] **Step 3:** In `SyncChannelAsync` das `??=` (Zeile 74) durch einen bewachten Backfill
  ersetzen: Ist die eigene ID null und hält **eine andere** Zeile die aufgelöste ID
  (`LoadChannelByTwitchIdAsync`), bricht der Sync mit deutscher `LogWarning` (beide Channel-IDs
  und Namen benennen) und `return null` ab — die Duplikat-Zeile darf keinerlei Historie
  ansammeln; Auflösung übernehmen Task 5/7. Sonst Backfill wie bisher. Bestehende Tests bleiben
  grün.
- [ ] **Step 4:** `dotnet test EmotePurge.slnx` (mindestens das Infrastructure-Projekt) grün.
- [ ] **Step 5:** Nutzer fragen, Commit `fix(sync): stop duplicate channel rows from racing the
  TwitchChannelId unique index`.

### Task 5: `IChannelIdentityService` — Nachführung, Backfill, bewachter Merge

**Files:** `Core/Services/IChannelIdentityService.cs`, `Core/Services/AuditActor.cs`,
`Core/Entities/AuditLogEntry.cs`, `Infrastructure/Services/ChannelIdentityService.cs`,
`ServiceCollectionExtensions.cs`, `docs/DECISIONS.md`; Test:
`Integration/ChannelIdentityServiceTests.cs`.

**Interfaces (verbindlich):**
- `public enum TwitchUserLookupStatus { Found, NotFound, Unavailable }`
- `public record TwitchUserLookup(TwitchUserLookupStatus Status, TwitchUserIdentity? User);`
- `public record ChannelIdentityReconcileSummary(int Checked, int IdsBackfilled, int Renamed, int Merged, int MergesRefused, int LoginsMissing);`
- `public interface IChannelIdentityService` mit
  `Task<ChannelIdentityReconcileSummary?> ReconcileActiveChannelsAsync(CancellationToken ct = default)`
  (`null` = kein App-Token/Helix nicht erreichbar → Tick übersprungen, nichts geschrieben) und
  `Task<TwitchUserLookup> LookupByLoginAsync(string login, CancellationToken ct = default)`
  (kapselt App-Token + `GetUsersAsync` für den Join-Pfad, Task 7).
- `AuditActions.ChannelRename = "channel.rename"`, `AuditActions.ChannelMerge = "channel.merge"`.
- Statischer System-Actor auf `AuditActor` (z. B. `AuditActor.System` mit `("system", "system")`),
  XML-Doc: für Worker-getriebene, nutzerlose Aktionen; das Admin-Audit-Log zeigt ihn als Login
  `system` an, ohne weitere Anpassung.

**Reconcile-Algorithmus (Vertrag, pro Tick):** aktive Channels als skalare Projektion
`(Id, TwitchChannelId, ChannelName)` laden; **ein** `GetUsersAsync`-Aufruf mit allen bekannten IDs
plus den Logins der ID-losen Zeilen; dann pro Zeile genau einer dieser Fälle:

1. **ID bekannt, Login unverändert** → nichts.
2. **ID bekannt, Helix meldet anderen Login** → *Rename*: existiert keine Zeile unter dem neuen
   (normalisierten) Namen, wird `ChannelName` aktualisiert, `TrackingResumedAt = now` gesetzt
   (zwischen Rename und Nachführung war der IRC-Join tot — das ist exakt die Tracking-Lücke, die
   dieses Feld ehrlich macht; `CreatedAt` bleibt), Audit `channel.rename` mit `AuditActor.System`
   und Details `{ twitchChannelId, oldLogin, newLogin }` in derselben Transaktion. **Nach** dem
   Commit: `LEAVE:<altername>` publizieren, dann `JOIN:<neuername>` (Reihenfolge zwingend — der
   Worker lädt beim JOIN-Sync die Zeile per Name, die es erst nach dem Commit gibt; das LEAVE
   räumt `EmoteMatchCache` und EventAPI-Subscription des alten Namens ab, Worker.cs:42-47).
3. **ID bekannt, Zielname von anderer Zeile belegt** → *Merge* (unten) oder — weicht deren
   eigene ID von der autoritativen ab — Skip + `LogWarning` (konvergiert im nächsten Tick,
   sobald die blockierende Zeile selbst nachgeführt ist).
4. **ID-lose Zeile, Helix kennt den Login** → Backfill der ID; hält bereits eine andere Zeile
   diese ID, ist das der Duplikat-Fall mit vertauschten Rollen → *Merge* mit der ID-Zeile als
   Überlebender.
5. **ID-lose Zeile, Helix kennt den Login nicht** → `LoginsMissing++`, `LogWarning` nur beim
   Zustandswechsel (in-memory-Set der bereits gemeldeten Namen; entscheidend: keine
   Warnungsflut im 60-Minuten-Takt, aber mindestens eine Warnung pro Prozesslauf).
6. **ID bekannt, Helix kennt die ID nicht** (Konto gelöscht/gebannt) → wie 5, eigener Logtext.

**Merge-Vertrag (bewacht, Entscheidung 5):** Parameter `(survivor, loser, newLogin)`. Guard:
`loser` hat null `Emote`-Zeilen, sonst `MergesRefused++`, `LogWarning` mit beiden IDs, kein
Schreibzugriff. Sonst in **einer** Transaktion (ein `SaveChangesAsync`; die
`ChannelLiveDay`-Umzüge vorher als getrackte Änderungen stagen): `loser`-`ChannelLiveDay`s auf
`survivor.Id` umhängen — Kollisionsdaten zuerst als skalare Datumslisten beider Seiten laden
(Regel 10), kollidierende Tage per `MAX(LiveMinutes)` in die Survivor-Zeile schreiben und die
Loser-Zeile löschen, nicht-kollidierende per `ChannelId`-Update umziehen; `VoteSession`s per
`ChannelId`-Update umhängen; `survivor.IsBotActive |= loser.IsBotActive`;
`survivor.ChannelName = newLogin` (normalisiert); `survivor.TrackingResumedAt = now`; Audit
`channel.merge` (Details: beide Zeilen-IDs, `twitchChannelId`, `oldLogin`, `newLogin`, Anzahl
umgezogener LiveDays/Sessions); `loser` löschen (Kaskade trifft nichts — er ist emote-los).
Danach dieselben zwei Publishes wie beim Rename.

- [ ] **Step 1 (Tests zuerst,** `ChannelIdentityServiceTests` **mit `PostgresFixture` +
  substituiertem `ITwitchHelixClient`/`ITwitchAppTokenProvider` + Fake-`IRedisPublisher`):**
  je ein Fall pro Algorithmus-Zweig. Messlatten: Backfill schreibt die ID; Rename ändert Name +
  `TrackingResumedAt` + Audit-Eintrag und publiziert LEAVE/JOIN in dieser Reihenfolge; Merge
  erhält **alle** LiveDays (inkl. MAX-Regel an einem konstruierten Kollisionstag), hängt Sessions
  um, löscht die Loser-Zeile und übersteht die echten Unique-Indizes; Merge-Verweigerung bei
  Loser-Emotes lässt beide Zeilen unverändert stehen; Helix-`null` schreibt nichts und liefert
  Summary `null`; Fall 5/6 warnen genau einmal. Die Namens-Fälle mit gemischt getippten Logins
  füttern (Regel 9).
- [ ] **Step 2:** Typen in Core (BCL-only — `CoreAssemblyReferenceTests` wacht), Implementierung,
  DI-Registrierung (scoped, wie die übrigen Services).
- [ ] **Step 3:** Infrastructure-Suite grün.
- [ ] **Step 4:** `docs/DECISIONS.md` **neu einlesen** (Parallel-Session!), Eintrag verfassen.
  Inhaltliche Pflichtpunkte: die Identitäts-/Adress-Regel (Entscheidung 3) samt „wann per ID,
  wann per Name"; die Zwei-Pfade-Diagnose zu Issue #34 (Anzeigepfad real, Datenpfad präventiv)
  samt Task-1-Messergebnis; Helix als Signalquelle mit den verworfenen Alternativen
  (7TV-Staleness, Live-Poll-Blindheit, `/mine`-Reichweite); die Merge-Invariante samt Beleg,
  warum die Verlierer-Zeile leer sein muss, und der Verweigerungspfad; „kein Schema-Change,
  Backfill zur Laufzeit"; die bewusste Nicht-Kanonisierung im Auth-Pfad (Entscheidung 2);
  `Betrifft:`-Zeile mit den Kerndateien. Nicht ausformulieren, was der Plan schon sagt — der
  Eintrag begründet, der Plan beschreibt.
- [ ] **Step 5:** Nutzer fragen, **ein** Commit inkl. DECISIONS (Regel 3):
  `feat(identity): reconcile channel identity against helix and merge duplicates`.

### Task 6: `TwitchIdentityReconcileWorker`

**Files:** `src/EmotePurge.Worker/TwitchIdentityReconcileWorker.cs`, `Worker/Program.cs`,
`CLAUDE.md`.

**Vertrag:** konkrete Klasse (Regel-5-Ausnahme), `PeriodicTimer`, Konfigschlüssel
`Twitch:IdentityReconcileIntervalMinutes`, Default **60** (Antwort 2 des Betreibers; ein Tick
kostet einen Helix-Request je 100 Channels). Ablauf je Tick: Credential-Guard wie im
`TwitchLivePollWorker` (fehlen ClientId/Secret → einmal warnen, Service beendet sich);
`BootRecoveryGate.Completed` abwarten, **dann sofort** ein erster Lauf (das ist der
Prod-Backfill aus Entscheidung 6) und danach der Timer; Scope pro Tick,
`IChannelIdentityService.ReconcileActiveChannelsAsync`, Summary als eine `LogInformation` nur
wenn sie von der Null-Summary abweicht (ruhige Nächte bleiben ruhig); Catch-all pro Tick nach dem
Muster der Nachbar-Worker (ein Fehler kostet einen Tick, nie den Host).

- [ ] **Step 1:** Worker + Registrierung (`AddHostedService`). Keine neue pure Policy → kein
  `Worker.Tests`-Fall (Regel-11-Randnotiz in Global Constraints).
- [ ] **Step 2:** `CLAUDE.md`-Architekturabschnitt: „acht Hosted Services" → neun, mit
  Einzeiler zum neuen Worker samt Konfigschlüssel.
- [ ] **Step 3:** Solution-Build + Backend-Tests grün.
- [ ] **Step 4:** Nutzer fragen, Commit `feat(worker): periodic channel identity reconciliation`.

### Task 7: `JoinAsync` löst die Identität vorab auf

**Files:** `Core/Services/IChannelService.cs`, `ChannelService.cs`,
`Api/Validation/ApiErrorCodes.cs`, `Api/Endpoints/ChannelEndpoints.cs`,
`web/src/app/core/i18n/api-error.ts`, `web/public/i18n/de.json`, `web/public/i18n/en.json`;
Test: `Integration/ChannelServiceTests.cs`.

**Vertrag:** `JoinAsync` ruft zuerst `IChannelIdentityService.LookupByLoginAsync` (neue
Konstruktor-Abhängigkeit):

- **`Found`:** erst `LoadChannelByTwitchIdAsync`. Trifft eine Zeile mit **anderem** Namen, ist der
  Join zugleich die Rename-Nachführung — auch für **inaktive** Zeilen, die die periodische
  Reconciliation bewusst nicht anfasst (Entscheidung 4): Zeile umbenennen auf den kanonischen
  Helix-Login (normalisiert), reaktivieren (bestehende `TrackingResumedAt`-Logik greift;
  zusätzlich setzt der Namenswechsel sie wie in Task 5), Audit `channel.rename` mit dem
  **echten** Actor plus das bestehende `channel.join`; Publish-Reihenfolge `LEAVE:<altername>`
  vor dem bestehenden `JOIN:<neuername>`. Trifft keine Zeile per ID, läuft der heutige
  Namenspfad — nur bekommt eine neu angelegte Zeile die ID sofort mit.
- **`NotFound`** (Helix erreichbar, Login existiert definitiv nicht): Join ablehnen —
  **vom Betreiber bestätigt (Antwort 1)**; sonst entsteht per Tippfehler die nächste tote Zeile.
  Neuer Rückgabeweg nötig, weil `JoinAsync` heute `Channel` liefert: kleines Ergebnis-Record in
  `IChannelService` (`ChannelJoinResult` mit `Channel?` und einem Status
  `Joined | ChannelNotOnTwitch`), Endpoint mappt auf `404` mit
  `ApiErrorCodes.ChannelNotOnTwitch = "channel_not_on_twitch"`.
- **`Unavailable`:** exakt heutiges Verhalten (Namenspfad, ID bleibt ggf. null) — Verfügbarkeit
  schlägt Strenge, die Reconciliation heilt später. Vertrag: `NotFound` und `Unavailable` niemals
  verwechseln.

Frontend: nur der neue Code in `api-error.ts` + beide Locale-Dateien (Regel 7; der bestehende
Spec erzwingt Code↔Übersetzung). Die Join-Response trägt weiterhin `channelName` — im
Rename-Fall den kanonischen; kein UI-Umbau.

- [ ] **Step 1 (Tests zuerst, `ChannelServiceTests`):** Found/neue Zeile → ID gesetzt;
  Found/Rename-Fall (einmal aktive, einmal inaktive Alt-Zeile) → keine zweite Zeile, Umbenennung
  + beide Audit-Einträge + LEAVE-vor-JOIN; NotFound → keine Zeile, Ergebnisstatus
  `ChannelNotOnTwitch`; Unavailable → heutiges Verhalten (Regressionsfall). Substitutes für
  `IChannelIdentityService`.
- [ ] **Step 2:** Umsetzung Service + Endpoint (`ChannelManagementAuthorizationFilter` bleibt
  unverändert davor; kein neuer Filter, also kein `Api.Tests`-Pflichtfall — der 404 kommt aus
  dem Handler-Ergebnis, nicht aus der Filter-Matrix).
- [ ] **Step 3:** Fehlercode-Dreischritt (C#-Konstante, `api-error.ts`, beide Locales);
  `npm --prefix web test -- --watch=false` grün — der Locale-Spec ist die Wache.
- [ ] **Step 4:** Backend-Tests grün. Nutzer fragen, Commit `feat(channels): resolve twitch
  identity at join time and reject dead logins`.

### Task 8: Live-Verifikation und Rollout

**Files:** keine Code-Änderungen; ggf. Statuszeile in `docs/Feature-Ideen-…` falls dort ein
passender Eintrag existiert (prüfen, sonst nichts).

- [ ] **Step 1 — Live-Verifikation lokal (Regel 16, Regel 15):** Stack mit
  `docker compose up -d --build` neu bauen. Vier Proben gegen echte Dienste:
  (a) *Anzeigepfad — mit harter Messlatte:* mit dem Betreiber-Konto `/mine` laden. Der Grant für
  Twitch-ID `955448938` muss dort als **`affeaufbike`** stehen (vorher: `affeoderwatt`, ein Login,
  den Helix nicht mehr kennt — Messtabelle im Befund). Verschwindet er stattdessen, greift
  fälschlich der „ID weg"-Zweig, und der Helix-Aufruf ist per ID statt per Login zu prüfen.
  Übrige reale Grants erscheinen unverändert korrekt;
  (b) *Backfill:* in der Dev-DB bei einem echten getrackten Channel `TwitchChannelId` auf NULL
  setzen → nächster Reconcile-Tick (Intervall temporär klein konfigurieren) füllt die korrekte
  ID, Log-Summary stimmt;
  (c) *Rename+Merge:* die Zeile eines echten Channels auf einen erfundenen Namen umbenennen und
  eine zweite, emote-lose Zeile unter dem echten Namen anlegen → ein Tick benennt zurück, merged,
  Audit-Einträge sichtbar im Admin-Log, Worker joint/synct den echten Namen, Roster sauber;
  (d) *Join:* Join eines nicht existierenden Logins → 404 `channel_not_on_twitch` samt
  Übersetzung; Join eines echten, neuen Channels → Zeile hat sofort eine ID.
  Einen echten Twitch-Rename kann die Verifikation nicht *erzwingen*, aber sie muss ihn auch
  nicht simulieren: Probe (a) **ist** ein echter Rename mit Produktionsdaten. Hat Task 1 eine **aktive** tote Zeile
  ergeben, nach dem Prod-Deploy gezielt prüfen, dass die Reconciliation sie als
  `LoginsMissing` meldet.
- [ ] **Step 2 — Gesamtabnahme:** `dotnet test EmotePurge.slnx` und
  `npm --prefix web test -- --watch=false` grün; E2E nur falls doch UI-Dateien angefasst wurden
  (dann ohne Api auf `:5151`). Es gibt **keine** EF-Migration in diesem Plan —
  Deploy-Reihenfolge ist frei.
- [ ] **Step 3:** Ergebnisse dem Nutzer melden; Deploy und Kuma bleiben wie gehabt Handgriffe des
  Nutzers.

---

## Selbstprüfung

**Abdeckung gegen den Auftrag und die Betreiber-Antworten.** Das real gemeldete Symptom (toter,
nicht gejointer Channel „nur noch auf 7TV") → Anzeigepfad-Diagnose in Befund 3–5, Fix als
früheste Code-Task (Task 3), Restunsicherheit über die DB-Zeile per read-only SQL als Task 1
entschieden. Umbenannter statt toter Grant → erscheint unter dem **neuen** Namen (Task-3-Vertrag
Ausgang 2, eigener Testfall). Auflösungspfad per ID + Regel → Task 4 (+ Entscheidung 3).
Nachführungs-Signal begründet gewählt, Intervall vom Betreiber bestätigt → Entscheidung 4,
Tasks 2/5/6. Merge-Fall mit Constraint-Analyse (`(ChannelId, SevenTvEmoteId)` und
`(EmoteId, Date)` werden nie berührt, weil die bewachte Invariante Emote-Umzüge ausschließt;
`ChannelLiveDay (ChannelId, Date)` per MAX-Regel; `CreatedAt` bleibt, `TrackingResumedAt`
markiert die Rename-Lücke) → Entscheidung 5, Task 5 — bewusst als Prävention eingeordnet, nicht
als Fix des gemeldeten Falls, und deshalb **nach** dem Anzeigepfad. Join-Ablehnung toter Logins →
Antwort 1, Task 7. Bestandsdaten: Messung vor allem anderen + Backfill-Strategie + tote Logins →
Entscheidung 6, Tasks 1/5. Stiller IRC-Join: Interpretation hier, Badge als eigener Schritt →
Entscheidung 7, Nicht-Ziel 3.

**Regeln.** Regel 1/2: jeder Code-Task endet mit Rückfrage + eigenem Commit. Regel 3: DECISIONS
im Task-5-Commit, CLAUDE.md im Task-6-Commit. Regel 4: neue Fähigkeit als
`IChannelIdentityService` (Core-Interface + Infrastructure-Implementierung), Handler bleiben
dünn. Regel 7: `channel_not_on_twitch` dreifach, Spec-erzwungen (Task 7). Regel 8: kein Merge
fasst `Emote.Id`/`SevenTvEmoteId` an. Regel 9: alle Namenspfade normalisiert, IDs ordinal.
Regel 10: Merge lädt Kollisionsdaten als skalare Listen. Regel 11: `/mine`-, Merge- und
Reconcile-Logik in `Integration/` mit Testcontainers; kein neuer purer Worker-Typ. Regel 16:
Task 8 Step 1. Regel 17: Prod-Passwort nur als Platzhalter (Task 1).

**Typkonsistenz.** `SevenTvEditorGrantEntry`/`Entries` (Task 3) sind die einzige Grant-Quelle
von `MyChannelsService`; die Sets bleiben für `ChannelAccessService` unverändert.
`TwitchUserIdentity`/`GetUsersAsync` (Task 2) sind die einzige Helix-Identitätsquelle von
Task 3, 5 und 7. `LoadChannelByTwitchIdAsync` (Task 4) wird in Task 5 und 7 unter genau diesem
Namen benutzt. `TwitchUserLookup{,Status}`, `ChannelIdentityReconcileSummary`,
`LookupByLoginAsync`, `ReconcileActiveChannelsAsync` heißen in Task 5, 6 und 7 gleich.
`AuditActions.ChannelRename`/`ChannelMerge` und `AuditActor.System` werden in Task 5 definiert;
Task 7 nutzt `ChannelRename` mit echtem Actor. Der Wire-Code `channel_not_on_twitch` steht
wortgleich in `ApiErrorCodes`, `api-error.ts` und beiden Locales. Konfigschlüssel
`Twitch:IdentityReconcileIntervalMinutes` identisch in Task 6 und der Verifikation.
Publish-Reihenfolge LEAVE-vor-JOIN ist in Task 5 und 7 identisch spezifiziert und getestet.

**Vom Betreiber beantwortet (2026-08-29), nichts mehr offen:** Join-Ablehnung ja (Task 7);
60-Minuten-Intervall bestätigt (Task 6); der bekannte tote Channel ist nicht gejoint —
eingearbeitet als Zwei-Pfade-Diagnose, Task 1 klärt die letzte Restfrage (existiert eine
DB-Zeile?) read-only, bevor Code entsteht.
