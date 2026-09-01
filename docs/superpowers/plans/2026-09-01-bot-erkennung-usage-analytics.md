# Bot-Erkennung in der Usage-Analytics — Umsetzungsplan (Issue #31)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code**
> (globale Regel, s. `~/.claude/CLAUDE.md`): jeder Task beschreibt Absicht, Verträge, Grenzfälle
> und Prüfbedingungen — Methodenrümpfe, Migrations-Bodies, Templates und Testmethoden entstehen
> im Task selbst.

**Goal:** Chat-Nachrichten bekannter Bots werden ab dem Deploy in `UsageStats` in einer eigenen
Spalte `BotUseCount` verbucht statt in `UseCount`; die Nutzungsseite zeigt ab wann, und sonst
ändert sich für Nutzer nichts. Jeder Tag ohne diese Trennung erzeugt weitere Zeilen, die für
immer gemischt bleiben — das ist der einzige Grund, warum dieses Issue vor anderen dran ist.

**Architecture:** Vier Bausteine entlang des bestehenden Datenflusses. **(A1)** Ein
TwitchLib-freier `IBotChatterDetector` im Worker entscheidet einmal pro Nachricht aus
Chatter-ID und Badges, ob ein Bot spricht (`bot-badge` → sechs verifizierte Konten-IDs →
Konfigschlüssel `Twitch:AdditionalBotAccountIds`). **(A2)** `EmoteUsageCounter` zählt pro Emote
ein Paar `EmoteUsageCounts(Human, Bot)` statt eines `int`; der Typ liegt in `EmotePurge.Core`,
weil `IUsageStatFlushService.FlushAsync` ihn in der Signatur trägt. **(A3)** `UsageStat` bekommt
`BotUseCount` (`NOT NULL DEFAULT 0`, additive Migration), der `UNNEST`-Upsert ein drittes Array;
der Covering-Index `(EmoteId, Date) INCLUDE (UseCount)` bleibt **unverändert**. **(A3b, Nutzer-
entscheidung vom 2026-09-01)** Die vier Lesestellen in `UsageStatQueryService`, die heute die
Existenz einer Zeile als Nutzung deuten, bekommen ein `UseCount > 0`-Prädikat — der Defekt
entstünde erst durch die Trennung, und das Prädikat ist auf Bestandsdaten ein No-op. **(A4)**
`EmoteSetStatusDto` trägt ein nullbares Trenndatum, abgeleitet als `MIN(Date)` über Zeilen mit
`BotUseCount > 0` — kein neuer Request, kein Toggle. **(A5)** Die Nutzungsseite zeigt einen
Hinweissatz nach dem Muster der bestehenden Coverage-Unterschriften, oder nichts.

**Tech Stack:** .NET 10 (Worker Service mit TwitchLib.Client 4.0.1, Minimal API, EF Core/Npgsql,
xUnit + NSubstitute + Testcontainers), Angular 22 (Standalone, Signals, zoneless), Transloco,
Vitest, Playwright.

**Spec:** [`docs/superpowers/specs/2026-09-01-bot-erkennung-usage-analytics-design.md`](../specs/2026-09-01-bot-erkennung-usage-analytics-design.md)
(Commit `eb54665`) — **freigegeben und verbindlich**. Die Entscheidungen E1–E4 werden hier nicht
neu aufgerollt; wo der Code beim Lesen einen echten Widerspruch zur Spec zeigt, steht er unter
„Offene Punkte für den Nutzer" am Ende, nicht als stille Umentscheidung in einem Task.

## Ist-Zustand, am Code verifiziert (2026-09-01)

Die Spec-Tabelle stimmt; hier die Stellen mit den Namen, die der Implementer tatsächlich vorfindet:

| Ort | Befund |
|---|---|
| `src/EmotePurge.Worker/TwitchChatManager.cs`, `OnMessageReceived` | Reihenfolge heute: `Interlocked.Exchange` auf `_lastMessageReceivedUtcTicks` → Indexer-Zuweisung `_lastMessageByChannelTicks[e.ChatMessage.Channel]` → Debug-Log → `emoteMatchCache.GetChannelEmotes` mit frühem Return bei leerem Set → `HashSet<string> matchedThisMessage` → Token-Schleife mit `usageCounter.Increment(emoteId)`. Der Kommentar dort verlangt ausdrücklich „Hot path: … no LINQ and no allocation". Primärkonstruktor nimmt `ILogger`, `ILoggerFactory`, `IEmoteMatchCache`, `IEmoteUsageCounter`. |
| TwitchLib 4.0.1 (`TwitchLib.Client.Models.dll`) | `ChatMessage` hat `UserId` (`string`), `Badges` (`List<KeyValuePair<string, string>>`: Badge-Set-ID → Version) und `BadgeInfo`. Alle drei ungenutzt. |
| `src/EmotePurge.Worker/EmoteUsageCounter.cs` + `IEmoteUsageCounter.cs` | `ConcurrentDictionary<string, int> _counts`; `Increment(string)`, `Merge(IReadOnlyDictionary<string, int>)`, `DrainAndReset()` per `Interlocked.Exchange`, `PendingEmoteCount` per `Volatile.Read(...).Count`. Die Update-Lambda in `Increment` ist heute statisch (keine Closure). |
| `src/EmotePurge.Worker/UsageFlushWorker.cs` | `FlushOnceAsync` reicht das gedrainte Dictionary an `IUsageStatFlushService.FlushAsync`, bucht `stats.RecordFlushSuccess(counts.Count, …)` (Zahl der Emotes, nicht Summe) und hängt bei Fehlschlag `usageCounter.Merge(counts)` bis `MaxConsecutiveFailuresToRequeue`. |
| `src/EmotePurge.Worker/WorkerHealthPublisher.cs` | liest nur `usageCounter.PendingEmoteCount`. |
| `src/EmotePurge.Worker/Program.cs` | registriert `IEmoteUsageCounter` als Singleton; `TwitchChatManager` als `ITwitchChatManager`-Singleton. Konfig kommt per `IConfiguration` (Vorbild: `TwitchLivePollWorker` liest `Auth:Twitch:ClientId`). |
| `src/EmotePurge.Core/Entities/UsageStat.cs` | `Id`, `EmoteId`, `Date` (`DateOnly`), `UseCount`, Navigation `Emote`. |
| `src/EmotePurge.Core/Services/IUsageStatFlushService.cs` | `FlushAsync(IReadOnlyDictionary<string, int> usageCounts, ct)` → `IReadOnlyCollection<string>` (betroffene Channel-Namen). DTOs liegen in diesem Repo neben ihrem Interface (`EmoteSetStatusDto` in `IEmoteSetStatusService.cs`). |
| `src/EmotePurge.Infrastructure/Services/UsageStatFlushService.cs` | Projektion `validEmotes` (Id + Channel-Name), `validIds`, `useCounts`; `const string sql` mit `UNNEST(@emoteIds, @useCounts)` und `ON CONFLICT ("EmoteId","Date") DO UPDATE SET "UseCount" = … + EXCLUDED."UseCount"`; drei `NpgsqlParameter` (`date`, `emoteIds`, `useCounts`). Der Atomaritäts-Kommentar davor bleibt gültig. |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` | `Entity<UsageStat>`: `HasIndex(u => new { u.EmoteId, u.Date }).IsUnique().IncludeProperties(u => u.UseCount)` — **bleibt wörtlich so**. |
| `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` | fünf Methoden, alle über `UseCount`; `GetUsageContextAsync` dokumentiert die Regel-10-Falle (erst skalare Emote-ID-Liste, dann `GroupBy`). Vier Stellen deuten die **Existenz** einer Zeile als Nutzung statt ihren `UseCount`: `:66` (`LastUsedDate = g.Max(…)`), `:119` (sparse Tagesserie), `:124-129` (`bounds` First/Last), `:203` (Roh-Zeilen der Channel-Serie). Nachgeprüft vom Nutzer, wird in Task 4 behoben. |
| `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs` | `db.LoadChannelReadOnlyAsync`; `occupiedSlots` wird übersprungen, solange `channel.ActiveEmoteSetId.Length == 0` (Kommentar: Poll-Schleife der Usage-Seite); `EmoteSetStatusDto` hat sechs positionelle Parameter, genau eine Konstruktionsstelle. |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:117` | `group.MapGet("/active-set", …)` reicht das DTO 1:1 durch; kein neuer Filter nötig. |
| `src/EmotePurge.Infrastructure/Migrations/` | jüngste: `20260829131659_AddChannelSyncFailureReason`; Snapshot führt für `UsageStats` den Index mit `IncludeProperties … "UseCount"`. |
| `web/src/app/core/emotes/emote-set-status.model.ts` | `EmoteSetStatus` mit sechs Feldern; `emote-admin.service.spec.ts` flusht zwei **vollständige** Objektliterale und prüft eines per `toEqual`. |
| `web/src/app/features/usage-stats/usage-stats-page.{ts,html}` | `setStatus`-Signal, `trackedSince`/`trackedSinceDate`-Computeds, `formatDate(iso)`; im Template ab `:194` der `<p class="text-xs text-fg-muted">` mit `usageStats.trackedSince` plus `liveDaysInRangeKey` — das ist die Bildunterschrift, an die der Hinweis anschließt. |
| `web/src/app/shared/emotes/usage-series.ts` + `.spec.ts` | `liveDayCoverage` und `liveDayCaptionKey` — reine Funktionen „Transloco-Key oder `null`", co-located getestet. Das ist das Muster für den neuen Hinweis. |
| `web/public/i18n/{de,en}.json` | `usageStats.trackedSince` („Wir zählen für diesen Channel seit dem {{ date }}."), `usageStats.chart.*`. |
| `web/e2e/support/mocks.ts` | `mockActiveEmoteSet(page, channelName, activeEmoteSetId, status)` mit optionalem Status-Objekt und Defaults. |
| Tests | `tests/EmotePurge.Worker.Tests/EmoteUsageCounterTests.cs` (vier Fälle zu `PendingEmoteCount`); `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatFlushServiceTests.cs` (`CreateService`, `ReadStatsAsync` über frischen Context, `SeedEmoteAsync`, `SeedTwoEmotesInOneChannelAsync`); `EmoteSetStatusServiceTests.cs` (`SeedChannelAsync(db, name, capacity, activeEmoteSetId)`, `SeedEmoteAsync`); beide `[Collection("Postgres")]` über `PostgresFixture` (echte Migrationen). `Unit/CoreAssemblyReferenceTests` wacht über Core. |
| Konfiguration | `docker-compose.yml` und `docker-compose.prod.yml` reichen dem Worker `Auth__Twitch__ClientId/ClientSecret` und `Twitch__LivePollIntervalSeconds` durch; `.env.example` dokumentiert jede Variable mit Kommentar. `ChannelAccessService.GetAdminLogins` zeigt, wie eine Liste aus **beiden** Konfig-Formen (JSON-Array = indizierte Schlüssel, Env/Secret = kommagetrennter Skalar) gelesen wird — der Skalar gewinnt. |
| `docs/Architectur.md:80,232` | beschreibt Zähler („Treffer max. 1x pro Nachricht") und den Index mit `UseCount`-Include — beide Sätze brauchen einen Halbsatz. |

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1:** vor jedem `git commit` erst den Nutzer fragen — auch unter freigegebenem Plan. Die
  Commit-Zeilen unten sind Vorschläge für die Rückfrage, keine Automatismen.
- **Regel 2 / Commit-Zuschnitt:** fünf Commits (Tabelle unten). Tasks 2 und 3 bilden **einen**
  Commit, weil sie eine Kompiliereinheit sind: die Signatur von `FlushAsync` und der Typ des
  Zählers hängen aneinander, ein Zwischenstand, in dem nur eine Seite umgestellt ist, baut nicht.
  Der Task-2-Subagent meldet deshalb „Infrastructure-Projekt baut, Infrastructure-Tests grün",
  nicht „Solution grün" — das ist erst nach Task 3 die Messlatte.
- **Regel 3:** Der Commit mit der Schemaänderung (Tasks 2+3) enthält den `docs/DECISIONS.md`-Eintrag
  **im selben Commit**. Es gibt **einen** Eintrag für dieses Issue; die Commits von Task 4, 5 und 6
  ergänzen dessen `**Betrifft:**`-Zeile um ihre Dateien (und höchstens einen Satz), statt einen
  zweiten Eintrag zu schreiben. Der Eintrag trägt auch die **bekannte Einschränkung** von E4
  (s. „Entscheidungen dieses Plans", Nr. 9).
- **Regel 4 / Schichtentreue:** kein `AppDbContext` aus Handlern — der Endpunkt bleibt ein
  Durchreicher; der neue Detektor ist ein **Worker**-Typ (wie `IEmoteUsageCounter`, registriert in
  `Worker/Program.cs`, nicht in `AddEmotePurgeInfrastructure`). `EmotePurge.Core` bleibt BCL-only:
  `EmoteUsageCounts` ist ein `readonly record struct` ohne jede Abhängigkeit
  (`CoreAssemblyReferenceTests` wacht).
- **Regel 5:** Detektor mit Interface (Logik + externe Konfigabhängigkeit); der Record-Struct nicht.
- **Regel 7:** kein neuer Fehlercode. Neue UI-Texte in **beiden** Locale-Dateien.
- **Regel 10:** die neue `MIN(Date)`-Abfrage in Task 5 folgt dem Zuschnitt von
  `GetUsageContextAsync` (erst skalare Emote-ID-Liste) — und die Übersetzung wird per Test gegen
  echtes Postgres **geprüft**, nicht angenommen. Die Prädikate aus Task 4 ändern an keinem
  Zuschnitt etwas: sie kommen in bestehende `Where`-Klauseln über der einen Tabelle.
- **Regel 11:** Detektor- und Zähler-Tests im container-freien `tests/EmotePurge.Worker.Tests`;
  Upsert- und Status-Service-Tests in `tests/EmotePurge.Infrastructure.Tests/Integration`
  (Testcontainers). Kein `Api.Tests`-Fall: kein neuer `IEndpointFilter`, keine
  Filter-Reihenfolge ändert sich. `TwitchChatManager` selbst wird bewusst **live** verifiziert
  (Regel 16), nicht gegen Fakes.
- **Regel 12:** die Sichtbarkeitsregel des Hinweises ist eine reine Funktion in `web/src/app/core/`
  mit co-located `*.spec.ts`. **Kein** isolierter Komponententest der Seite.
- **Regel 15/16:** vor jedem Compose-Test `--build`; Backend vor dem Commit live gegen echtes
  Twitch/Postgres verifizieren — der konkrete Nachweis steht in Task 7, und „läuft durch" ist keiner.
- **Regel 18:** vor jedem Commit `dotnet format EmotePurge.slnx` und `npm --prefix web run format`;
  `npm --prefix web run lint` grün.
- **Regel 19:** C#-Memberreihenfolge `const`/`static readonly` → `readonly` → Felder → Properties →
  öffentliche → private Methoden → `private static`.
- **Sprache:** Bezeichner und Kommentare englisch, Log-/`throw`-Messages deutsch, DECISIONS deutsch.
- **„Fertig" heißt:** `dotnet test EmotePurge.slnx` (Docker läuft, Testcontainers) und
  `npm --prefix web test -- --watch=false` grün; wegen der UI-Änderung in Task 6 zusätzlich
  `npm --prefix web run e2e` — **nur, wenn auf `:5151` keine Api lauscht** (sonst 401 → Login-Redirect
  → halbe Suite rot mit irreführendem „element not found"). Vor dem Playwright-Lauf ein laufendes
  `dotnet run` beenden. Rote E2E-Fälle bei Laufzeit deutlich über 1,7 min sind Speicherdruck,
  nicht Regression — Suite allein wiederholen.
- **Befehle:**
  - ein xUnit-Test: `dotnet test tests/EmotePurge.Worker.Tests/EmotePurge.Worker.Tests.csproj --filter "FullyQualifiedName~BotChatterDetectorTests"`
  - eine Vitest-Datei: `npm --prefix web test -- --watch=false --include="src/app/core/emotes/bots-excluded-caption.spec.ts"`
  - ein Playwright-Test: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts -g "<Testname>"`
  - Migration: `dotnet ef migrations add AddUsageStatBotUseCount --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`

## Reihenfolge und Commits

| Task | Inhalt | Commit | Modell |
|---|---|---|---|
| 1 | `IBotChatterDetector` + Tests + Konfig-Verdrahtung | `feat(worker): detect bot chatters by badge and account id` | sonnet |
| 2 | `EmoteUsageCounts`, `UsageStat.BotUseCount`, Migration, Upsert, Tests, DECISIONS, Architectur.md | — (Working Tree) | sonnet |
| 3 | Zähler trägt das Paar; `TwitchChatManager` klassifiziert; `UsageFlushWorker` | `feat(usage): count bot emote usage apart from human usage` (Tasks 2+3) | sonnet |
| 4 | Lesequeries: `UseCount > 0` an den vier Existenz-Stellen + Tests | `fix(usage): keep bot-only rows out of the usage read models` | sonnet |
| 5 | `EmoteSetStatusDto.BotsExcludedSince` + Service + Tests | `feat(api): report since when bot usage is counted apart` | sonnet |
| 6 | Frontend-Hinweis, i18n, Vitest, E2E-Mock | `feat(web): say since when bot messages are not counted` | sonnet |
| 7 | Gates, Live-Verifikation, Dev-Migration + Gegenprobe, Prod-Übergabe | kein Code-Commit | opus |
| 8 | Codex-Sol-Zweitmeinung, ggf. Fable als Schiedsrichter | — | (Codex) |

Strikt sequenziell: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8. Task 4 braucht die Spalte aus Task 2 (die
Nullzeile, gegen die es testet, entsteht erst mit dem neuen Upsert); Task 5 ebenfalls; Task 6 den
Vertrag aus Task 5. Task 1 ist unabhängig und hat bis Task 3 keinen Konsumenten — das ist gewollt.

## File Structure

```
src/EmotePurge.Core/
  Entities/UsageStat.cs                          (M: BotUseCount)
  Services/IUsageStatFlushService.cs             (M: EmoteUsageCounts + neue Signatur)
  Services/IEmoteSetStatusService.cs             (M: BotsExcludedSince)
src/EmotePurge.Infrastructure/
  Services/UsageStatFlushService.cs              (M: drittes UNNEST-Array, beide Spalten addieren)
  Services/UsageStatQueryService.cs              (M: UseCount > 0 an vier Stellen, Task 4)
  Services/EmoteSetStatusService.cs              (M: MIN(Date)-Abfrage, übersprungen bei leerem Set)
  Migrations/<Zeitstempel>_AddUsageStatBotUseCount.cs (C, generiert) + Snapshot (M, generiert)
src/EmotePurge.Worker/
  IBotChatterDetector.cs, BotChatterDetector.cs  (C)
  IEmoteUsageCounter.cs, EmoteUsageCounter.cs    (M: Paar statt int)
  TwitchChatManager.cs                           (M: Detektor injiziert, Klassifikation vor der Token-Schleife)
  UsageFlushWorker.cs                            (M: nur Typ des Dictionaries)
  Program.cs                                     (M: Registrierung)
tests/EmotePurge.Worker.Tests/
  BotChatterDetectorTests.cs                     (C)
  EmoteUsageCounterTests.cs                      (M)
tests/EmotePurge.Infrastructure.Tests/Integration/
  UsageStatFlushServiceTests.cs                  (M)
  UsageStatQueryServiceTests.cs                  (M: vier Fälle zur Bot-only-Zeile, Task 4)
  EmoteSetStatusServiceTests.cs                  (M)
web/src/app/core/emotes/
  emote-set-status.model.ts                      (M)
  emote-admin.service.spec.ts                    (M: Literale vervollständigen)
  bots-excluded-caption.ts + .spec.ts            (C)
web/src/app/features/usage-stats/usage-stats-page.{ts,html}  (M)
web/public/i18n/de.json, en.json                 (M)
web/e2e/support/mocks.ts, web/e2e/usage-atlas.e2e.spec.ts    (M)
docker-compose.yml, docker-compose.prod.yml, .env.example    (M: Task 1)
docs/DECISIONS.md (M, Commit der Tasks 2+3; Betrifft-Zeile in 4, 5 und 6 ergänzt)
docs/Architectur.md (M, Commit der Tasks 2+3)
```

## Entscheidungen dieses Plans (wo die Spec Spielraum ließ)

1. **Signatur des Detektors.** Die Spec verlangt TwitchLib-Freiheit und „reine `string`-Werte".
   Der Detektor nimmt die Chatter-ID als `string?` und die Badges als
   `IReadOnlyList<KeyValuePair<string, string>>?` — das ist exakt die BCL-Form, in der TwitchLib
   `ChatMessage.Badges` liefert (Badge-Set-ID → Version), also **keine** Projektion pro Nachricht
   im Hot Path, und weiterhin kein TwitchLib-Typ in der Signatur. Der Worker-Test baut die Liste
   direkt.
2. **Konfig-Lesen im Detektor, nicht in `Program.cs`.** Die Spec-Testfälle „Konfig leer / mit
   Leerzeichen / Duplikat" verlangen, dass das Parsen dort liegt, wo es getestet wird. Der Detektor
   nimmt deshalb `IConfiguration` im Konstruktor und liest den Schlüssel einmalig beim Bau (er ist
   Singleton). Beide Konfig-Formen nach dem Vorbild `ChannelAccessService.GetAdminLogins`: der
   kommagetrennte Skalar (Env/Secret) gewinnt, sonst das indizierte JSON-Array. Der Worker-Test
   baut eine In-Memory-`IConfiguration` (`Microsoft.Extensions.Configuration` kommt transitiv über
   die Projektreferenz auf den Worker — keine neue `PackageReference`; „container-frei" heißt
   ohne Testcontainers, nicht ohne BCL-nahe Pakete).
3. **`EmoteUsageCounts` liegt in `IUsageStatFlushService.cs`**, nach dem Muster
   `EmoteSetStatusDto` neben `IEmoteSetStatusService`: der Typ ist Teil dieses Vertrags.
4. **Name des DTO-Felds: `BotsExcludedSince` (`DateOnly?`)**, auf der Leitung `botsExcludedSince`
   als `yyyy-MM-dd` — `DateOnly` läuft dort bereits (`EmoteUsageDto.Date`). Positionell am Ende
   des Records, **ohne** Default: es gibt genau eine Konstruktionsstelle (Präzedenz: Plan zu #32).
5. **Ein DECISIONS-Eintrag, im Schema-Commit**, der E1–E4 gemeinsam begründet (sie sind eine
   Entscheidung in vier Konsequenzen); spätere Commits ergänzen nur die Betrifft-Zeile.
6. **Ein Playwright-Fall zusätzlich zur Vitest-Sichtbarkeitslogik.** Die Spec-Tabelle nennt nur
   Vitest; das Template-Binding selbst sieht aber keine Suite. Ein Fall in
   `usage-atlas.e2e.spec.ts` (Hinweis mit Datum sichtbar / ohne Datum abwesend) ist billig und
   der einzige Ort, an dem der Satz je gerendert geprüft wird.
7. **Zwischenstand während Task 1–3:** der Detektor existiert nach Task 1 ohne Konsumenten; das ist
   kein Fehler, sondern der Preis für einen eigenen, prüfbaren Commit.
8. **Die Lesequeries werden doch angefasst (Nutzerentscheidung vom 2026-09-01, ehemals O1).**
   Die Spec sagt „unangetastet"; der Nutzer hat den Befund am Code nachgeprüft und entschieden,
   dass die vier Existenz-Stellen ein `UseCount > 0`-Prädikat bekommen — als eigener Task 4 mit
   eigenem `fix:`-Commit zwischen dem Schema-Commit und dem Api-Commit. Begründung steht im Task.
9. **E4 bleibt wie spezifiziert; die Grenze wird benannt, nicht heuristisch umgangen
   (Nutzerentscheidung vom 2026-09-01, ehemals O3).** `MIN(Date)` über `BotUseCount > 0` ist die
   erste *Sichtung* eines Bots, nicht der Beginn der Trennung. **Der Bruch ist ein Deploy-Ereignis,
   kein Datenereignis:** alte und neue Zeilen sind bei `BotUseCount = 0` ununterscheidbar, ein aus
   den Daten abgeleiteter Diskriminator existiert nicht. Ein korrekter Fix bräuchte neuen Zustand
   (Markierung beim ersten Flush mit dem neuen Code), den E4 bewusst vermeidet; der vorgeschlagene
   Vergleich gegen `trackedSince` träfe den Fall nicht (Join am 10.09., erster Bot am 11.09. ⇒
   Hinweis erscheint weiterhin und ist weiterhin falsch). Die Fehlrichtung ist konservativ — der
   Hinweis rät zur Vorsicht bei Zahlen, die in Wahrheit sauber sind. Das steht als **bekannte
   Einschränkung** im Frontend-Task (6) und im DECISIONS-Text (Task 2); kein Task, keine Heuristik.

---

### Task 1: `IBotChatterDetector` — Badge, statische IDs, Konfig-Ergänzung

**Files:**
- Create: `src/EmotePurge.Worker/IBotChatterDetector.cs`, `src/EmotePurge.Worker/BotChatterDetector.cs`,
  `tests/EmotePurge.Worker.Tests/BotChatterDetectorTests.cs`
- Modify: `src/EmotePurge.Worker/Program.cs` (Singleton-Registrierung neben `IEmoteUsageCounter`),
  `docker-compose.yml` und `docker-compose.prod.yml` (Worker-Env, neben `Twitch__LivePollIntervalSeconds`),
  `.env.example` (Variable mit Kommentar, leer als Default)

**Vorab lesen:** Spec-Abschnitt A1 vollständig; `ReconnectPolicy.cs`/`TwitchWatchdogPolicy.cs`
als Muster für pure Worker-Klassen mit Test; `ChannelAccessService.GetAdminLogins` (zwei
Konfig-Formen); `.env.example` (Kommentarstil).

**Interfaces (verbindlich):**
- `IBotChatterDetector` mit genau einer Methode
  `bool IsBot(string? chatterId, IReadOnlyList<KeyValuePair<string, string>>? badges)`.
- `BotChatterDetector(IConfiguration configuration)`; Konfigschlüssel
  **`Twitch:AdditionalBotAccountIds`**; die sechs statischen IDs aus der Spec-Tabelle
  (nightbot `19264788`, streamelements `100135110`, fossabot `237719657`, moobot `1564983`,
  streamlabs `105166207`, sery_bot `402337290`) als unveränderliche Menge in der Klasse; der
  Badge-Set-Schlüssel `bot-badge` als Konstante. **Nichts Unverifiziertes** wandert in die
  statische Liste — der Klassenkommentar nennt das Verifikationsdatum 2026-09-01.

**Absicht und Verträge:**
- Prüfreihenfolge wie in der Spec: `bot-badge` → statische Menge → Konfig-Ergänzung. Da alle drei
  mit „ist Bot" enden, ist die Reihenfolge nur Lesbarkeit, kein Semantikunterschied.
- Statische und konfigurierte IDs werden **beim Bau** zu einer Menge vereinigt (ordinaler
  Vergleich; Twitch-IDs sind opake Ziffernstrings und werden **nicht** normalisiert, nur getrimmt).
  Ein Konfigwert ergänzt, ersetzt nie.
- Konfig-Parsing: Skalar per Komma trennen, Einträge trimmen, leere verwerfen; Duplikate einer
  statischen ID sind harmlos (Menge). Fehlender oder leerer Schlüssel ⇒ nur die statische Menge.
- **Nie eine Ausnahme aus `IsBot`**, das ist Hot Path in `OnMessageReceived`: `null`/leere
  Chatter-ID ⇒ keine ID-Prüfung; `null`-Badge-Liste ⇒ keine Badge-Prüfung; beides zusammen ⇒
  `false`. Kein Logging pro Aufruf.
- Die Badge-Prüfung vergleicht nur den **Schlüssel** (Set-ID) des Paars, nie die Version.
- Compose/`.env.example`: `Twitch__AdditionalBotAccountIds=${TWITCH_ADDITIONAL_BOT_ACCOUNT_IDS:-}`
  für den Worker in beiden Compose-Dateien; im `.env.example` als kommagetrennte Twitch-User-IDs
  mit Hinweis, wofür (channel-eigene Bots, die in keiner Liste stehen) und dass die statische Liste
  bestehen bleibt. Damit ist der Spec-Satz „ohne Release nachtragbar" auch für Prod wahr.

- [ ] **Step 1 (Tests zuerst, `BotChatterDetectorTests`, container-frei):** je ein Fall für:
  `bot-badge` allein (auch ohne ID) ⇒ Bot; jede der sechs statischen IDs (Theory) ⇒ Bot; eine ID
  aus der Konfig ⇒ Bot; unbekannte ID ohne Badge ⇒ kein Bot; `null`/leere ID und `null`-Badges ⇒
  kein Bot, keine Ausnahme; Konfig fehlt / leer / nur Kommas ⇒ statische Liste wirkt weiter;
  Konfigwert mit Leerzeichen um die Einträge ⇒ erkannt; Konfig enthält eine statische ID doppelt
  ⇒ kein Fehler; Badge mit anderem Schlüssel (z. B. `moderator`) ⇒ kein Bot. Konfig per
  In-Memory-`ConfigurationBuilder`; ein Fall mit der indizierten Array-Form (`…:0`) und einer mit
  dem Skalar, damit beide Formen belegt sind.
- [ ] **Step 2: rot laufen lassen.** Filter `BotChatterDetectorTests`, Expected: Compilerfehler.
- [ ] **Step 3: implementieren** (Interface, Klasse, Registrierung, Compose, `.env.example`).
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter, Expected: PASS. Dazu einmal
  `dotnet build EmotePurge.slnx`.
- [ ] **Step 5:** `dotnet format`, Nutzer fragen, Commit
  `feat(worker): detect bot chatters by badge and account id`.

**Fertig-Bedingung:** `BotChatterDetectorTests` grün; Solution baut; die drei Konfig-Dateien nennen
den neuen Schlüssel; kein Konsument im Produktivcode (kommt in Task 3).

**Ausdrücklich nicht:** kein Import von twitchbots.info, keine Pflegeoberfläche, kein Log pro
erkannter Nachricht.

**Modell: sonnet** — klar spezifizierte pure Klasse mit vollständig aufgezählten Randfällen.

---

### Task 2: Persistenz — `EmoteUsageCounts`, `BotUseCount`, Migration, Upsert, DECISIONS

**Files:**
- Modify: `src/EmotePurge.Core/Services/IUsageStatFlushService.cs` (neuer Typ + Signatur),
  `src/EmotePurge.Core/Entities/UsageStat.cs`,
  `src/EmotePurge.Infrastructure/Services/UsageStatFlushService.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatFlushServiceTests.cs`,
  `docs/DECISIONS.md`, `docs/Architectur.md` (`:80` und `:232`, je ein Halbsatz)
- Create (generiert): `src/EmotePurge.Infrastructure/Migrations/<Zeitstempel>_AddUsageStatBotUseCount.cs`
  + aktualisierter `AppDbContextModelSnapshot.cs`

**Vorab lesen:** Spec A2 (Typ) und A3 vollständig; `UsageStatFlushService.cs` mit dem
Atomaritäts-Kommentar; `AppDbContext.cs` (Index-Konfiguration — **nicht anfassen**); die jüngste
Migration `20260829131659_AddChannelSyncFailureReason.cs` als Form-Vorbild; die
`UsageStatFlushServiceTests`-Helfer; `docs/DECISIONS.md` **Kopf und jüngste drei Einträge**
(Form: Überschrift `### Datum — Titel`, `**Betrifft:**`-Zeile, fett gesetzte Absatz-Anker).

**Interfaces (verbindlich):**
- `public readonly record struct EmoteUsageCounts(int Human, int Bot);` in
  `EmotePurge.Core.Services`, mit XML-Doc: Human ist das, was `UseCount` seit jeher bedeutet hat
  und ab jetzt ausschließlich bedeutet; Bot ist die zweite Spalte.
- `IUsageStatFlushService.FlushAsync(IReadOnlyDictionary<string, EmoteUsageCounts> usageCounts, ct)`;
  Rückgabe unverändert. Der `<summary>` sagt jetzt „Emote.Id → (human, bot)".
- `UsageStat.BotUseCount` (`int`), Kommentar: Zeile ist weiterhin `(EmoteId, Date)`-eindeutig;
  eine Zeile kann `UseCount = 0` bei `BotUseCount > 0` tragen — die Lesequeries filtern darauf
  (Task 4).

**Absicht und Verträge:**
- **Migration:** genau ein `AddColumn<int>` auf `UsageStats`, `nullable: false`,
  `defaultValue: 0` — EF generiert das für ein nicht-nullbares `int` von allein; **prüfen**, dass
  es drinsteht, denn auf diesem DB-Default beruht die Prod-Reihenfolge: das noch laufende **alte**
  Image schreibt per `UNNEST` ohne die Spalte und braucht `DEFAULT 0` auf der Spalte selbst.
  Postgres ≥ 11 fügt eine `NOT NULL DEFAULT <konstant>`-Spalte als Katalogänderung hinzu (kein
  Rewrite der größten Tabelle) — das gehört als Satz in den Migrationskommentar. Enthält die
  generierte Datei **irgendetwas anderes** (fremder Modellstand), verwerfen per
  `dotnet ef migrations remove …` und die Ursache klären, nicht die Datei kürzen. Der Snapshot
  muss den Index **weiterhin** mit `IncludeProperties … "UseCount"` und **nur** damit führen.
- **Upsert:** drittes Array `@botUseCounts` im `UNNEST`, dritte Spalte im `INSERT`, und
  `DO UPDATE SET` addiert **beide** Spalten getrennt (`"UseCount"` aus `EXCLUDED."UseCount"`,
  `"BotUseCount"` aus `EXCLUDED."BotUseCount"`). Die beiden Zahlen-Arrays werden aus derselben
  `validIds`-Reihenfolge gebaut wie heute `useCounts` — der bestehende Test zur Array-Paarung wird
  auf drei Arrays erweitert. Der Atomaritäts-Kommentar bleibt stehen und bekommt einen Halbsatz,
  dass er für beide Spalten gilt.
- **Kein** Filter „nur Einträge mit Human > 0": ein Batch, in dem ein Emote nur von Bots kam,
  erzeugt eine Zeile mit `UseCount = 0, BotUseCount = n` — genau das ist E1 („Bot-Nutzung wird
  erhalten"). Dass die Lesequeries so eine Zeile nicht als „benutzt" lesen dürfen, ist Task 4 —
  im nächsten Commit, nicht hier.
- **DECISIONS-Eintrag** (deutsch, Datum 2026-09-01, Titel sinngemäß „Bot-Nutzung bekommt eine
  zweite Spalte, keine zweite Zeile"): Pflichtinhalte — warum jetzt (irreversibel gemischte Zeilen);
  E1 mit den zwei reparierbaren Fehlerrichtungen und den zwei verworfenen Varianten
  (`IsBot` im Unique-Index, Verwerfen); E2 mit der twitchbots.info-Messung inkl. der
  `?username=`-Falle und der 903-Multi-Channel-Zahl; E3 (kein Toggle, Hinweis statt Steuerelement);
  E4 (abgeleitetes Datum statt Konstante) **samt seiner bekannten Einschränkung**: das Datum ist
  die erste Sichtung eines Bots, nicht der Beginn der Trennung; der Bruch ist ein Deploy-Ereignis,
  kein Datenereignis, alte und neue Zeilen sind bei `BotUseCount = 0` ununterscheidbar, ein
  Diskriminator aus den Daten existiert nicht, ein korrekter Fix bräuchte neuen Zustand, den E4
  bewusst vermeidet, und die Fehlrichtung ist konservativ (Vorsicht bei Zahlen, die sauber sind) —
  Wortlaut sinngemäß aus „Entscheidungen dieses Plans", Nr. 9; der unverändert bleibende
  Covering-Index samt der Bedingung, unter der das neu zu bewerten wäre; die
  Watchdog-Reihenfolge als Vertrag in `OnMessageReceived`; Konfigschlüssel; „ausdrücklich nicht
  gebaut". Die Begründung für das `UseCount > 0`-Prädikat (Task 4) **darf** hier schon stehen,
  weil sie Teil derselben Entscheidung ist — Task 4 ergänzt dann nur die Betrifft-Zeile.
  `**Betrifft:**` nennt die Dateien der Tasks 1–3 (Tasks 4/5/6 ergänzen später). Der Eintrag
  **begründet**, der Plan beschreibt — nichts aus diesem Plan abschreiben.
- **Architectur.md:** `:80` „max. 1x pro Nachricht" + „getrennt nach Mensch und Bot (`UseCount`/
  `BotUseCount`, s. DECISIONS 2026-09-01)"; `:232` beim Index-Satz ein Halbsatz, dass `BotUseCount`
  **nicht** im Include steht.

- [ ] **Step 1 (Tests zuerst, `UsageStatFlushServiceTests`):** bestehende Fälle auf den neuen
  Eingabetyp umstellen (Human-Werte wie bisher, Bot 0) — sie bleiben inhaltlich gleich; neue
  Fälle: (a) neue Zeile mit beiden Werten; (b) zwei Flushes am selben Tag addieren in **beide**
  Spalten getrennt (Konflikt-Pfad); (c) gemischter Batch aus zwei Emotes mit unterschiedlichen
  Paaren — Guard gegen Vertauschung der drei Arrays; (d) Batch, in dem ein Emote **nur** Bot-Treffer
  hat ⇒ Zeile mit `UseCount = 0` und `BotUseCount = n` existiert. Lesen weiterhin über
  `ReadStatsAsync` (frischer Context — die Zeilen kommen aus Roh-SQL).
- [ ] **Step 2: rot laufen lassen.** Filter `UsageStatFlushServiceTests`; Expected: Compilerfehler.
- [ ] **Step 3: Typ, Entität, Signatur, Migration erzeugen und prüfen, Upsert umbauen.**
- [ ] **Step 4: grün laufen lassen.** Filter `UsageStatFlushServiceTests` **und**
  `CoreAssemblyReferenceTests`; Expected: PASS. Die `PostgresFixture` migriert mit den echten
  Migrationen — grün heißt, die neue Migration läuft durch.
- [ ] **Step 5: lokale Dev-DB nachziehen** — `docker compose up -d postgres`, dann
  `dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`.
  **Nicht** vorher `docker compose up -d --build api worker` — Task 7 braucht die noch gecachten
  alten Images für die Gegenprobe.
- [ ] **Step 6: DECISIONS-Eintrag und Architectur.md-Halbsätze schreiben.** `docs/DECISIONS.md`
  unmittelbar vorher neu einlesen (der Kopf trägt die Sortierregel: neuester Eintrag zuoberst).
- [ ] **Step 7:** `dotnet build src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj`
  grün; **kein Commit** — der Worker baut erst nach Task 3.

**Fertig-Bedingung:** Infrastructure-Projekt baut; `UsageStatFlushServiceTests` und
`CoreAssemblyReferenceTests` grün; Migration enthält genau die eine Spalte mit `defaultValue: 0`;
Snapshot-Index unverändert; DECISIONS-Eintrag steht; Dev-DB migriert.

**Ausdrücklich nicht:** kein Anfassen der `IncludeProperties`; keine Änderung an
`UsageStatQueryService` (das ist Task 4 mit eigenem Commit); kein `HasDefaultValue` im Modell
(der DB-Default kommt aus der Migration, EF-Inserts liefern den CLR-Wert selbst).

**Modell: sonnet** — mechanische Erweiterung mit klaren Prüfbedingungen; die
Entscheidungsarbeit steckt in der Spec, nur der DECISIONS-Text braucht Sorgfalt.

---

### Task 3: Der Zähler trägt das Paar, `TwitchChatManager` klassifiziert

**Files:**
- Modify: `src/EmotePurge.Worker/IEmoteUsageCounter.cs`, `src/EmotePurge.Worker/EmoteUsageCounter.cs`,
  `src/EmotePurge.Worker/TwitchChatManager.cs` (Primärkonstruktor + `OnMessageReceived`),
  `src/EmotePurge.Worker/UsageFlushWorker.cs` (nur Dictionary-Typ),
  `tests/EmotePurge.Worker.Tests/EmoteUsageCounterTests.cs`

**Vorab lesen:** Spec A2 inklusive der **Gefahrenstelle**; `TwitchChatManager.OnMessageReceived`
mit den Kommentaren zur Watchdog-Buchführung und zum Hot Path; `OnSendReceiveData` (warum
Handler allokations- und ausnahmefrei bleiben müssen); `UsageFlushWorker.FlushOnceAsync`;
`WorkerHealthPublisher.cs:77`.

**Interfaces (verbindlich):**
- `IEmoteUsageCounter.Increment(string emoteId, bool isBot)`;
  `Merge(IReadOnlyDictionary<string, EmoteUsageCounts> counts)`;
  `IReadOnlyDictionary<string, EmoteUsageCounts> DrainAndReset()`; `PendingEmoteCount` unverändert
  (Zahl **verschiedener** Emotes — der bestehende Interface-Kommentar bleibt).
- `TwitchChatManager` bekommt `IBotChatterDetector botChatterDetector` als weiteren
  Konstruktorparameter (Singleton aus Task 1).

**Absicht und Verträge:**
- **Gefahrenstelle 1 — Reihenfolge in `OnMessageReceived`:** die Bot-Prüfung steht **nach** den
  beiden Watchdog-Schreibvorgängen (`_lastMessageReceivedUtcTicks`, `_lastMessageByChannelTicks`)
  und nach dem Debug-Log. Eine Bot-Nachricht beweist, dass der Socket lebt; würde sie vorher
  aussortiert, erfände der Watchdog stille Verbindungen und erzwänge Reconnects — das Fehlerbild
  vom 2026-08-03. Ein Kommentar an der Stelle benennt das ausdrücklich, damit ein späterer
  „Optimierer" die Prüfung nicht nach oben zieht.
- **Gefahrenstelle 2 — einmal pro Nachricht:** `IsBot` wird genau einmal aufgerufen, nach dem
  frühen Return bei leerem `channelEmotes` (eine Nachricht in einem Channel ohne Emotes braucht
  keine Klassifikation) und **vor** der Token-Schleife; das Ergebnis geht als `isBot` in jeden
  `Increment`-Aufruf der Schleife. Nie pro Token, nie pro Treffer.
- Mapping aus TwitchLib ausschließlich hier: `e.ChatMessage.UserId` und `e.ChatMessage.Badges`
  werden als die BCL-Typen durchgereicht, die sie sind — keine Projektion, keine Kopie.
- **Zähler:** `ConcurrentDictionary<string, EmoteUsageCounts>`; `Increment` erhöht die passende
  Hälfte des unveränderlichen Structs (`with`). Hot-Path-Hinweis: die heutige Update-Lambda ist
  closure-frei; eine Lambda, die `isBot` einfängt, allokiert pro Aufruf. `AddOrUpdate` hat eine
  Überladung mit Factory-Argument (`TArg`), oder es gibt zwei statische Lambdas — der Implementer
  wählt, der Kommentar im Zähler nennt den Grund. `Merge` addiert komponentenweise; `DrainAndReset`
  tauscht wie heute die ganze Instanz per `Interlocked.Exchange`.
- `UsageFlushWorker`: nur der Typ des gedrainten Dictionaries ändert sich; `counts.Count` in Logs
  und `RecordFlushSuccess` bleibt „Zahl der Emotes". Der Requeue-Kommentar zur Doppelzählung
  (`ON CONFLICT … + EXCLUDED`) gilt jetzt für beide Spalten — ein Halbsatz.
- `WorkerHealthPublisher` bleibt unangetastet (liest nur `PendingEmoteCount`).

- [ ] **Step 1 (Tests zuerst, `EmoteUsageCounterTests`):** bestehende vier Fälle auf die neue
  Signatur umstellen (sie prüfen weiter nur `PendingEmoteCount`); neue Fälle: (a) Human- und
  Bot-Inkremente desselben Emotes landen getrennt im gedrainten Paar; (b) `Merge` erhält beide
  Komponenten und addiert auf bestehende Einträge; (c) Drain gibt beide Komponenten zurück und
  leert; (d) ein Emote, das nur von Bots kam, zählt in `PendingEmoteCount` als ein Emote (Semantik
  unverändert).
- [ ] **Step 2: rot laufen lassen.** Filter `EmoteUsageCounterTests`; Expected: Compilerfehler.
- [ ] **Step 3: Zähler, Interface, `UsageFlushWorker`, `TwitchChatManager` umstellen.**
- [ ] **Step 4: alles grün.** `dotnet build EmotePurge.slnx`, dann `dotnet test EmotePurge.slnx`
  (Docker läuft). Expected: PASS über alle drei Backend-Testprojekte — das ist die erste Stelle,
  an der die Solution seit Task 2 wieder als Ganzes baut.
- [ ] **Step 5:** `dotnet format EmotePurge.slnx`; Nutzer fragen; **ein** Commit für Tasks 2+3
  inkl. `docs/DECISIONS.md`, `docs/Architectur.md`, Migration und Snapshot:
  `feat(usage): count bot emote usage apart from human usage`.

**Fertig-Bedingung:** `dotnet test EmotePurge.slnx` grün; `OnMessageReceived` hat die Reihenfolge
Watchdog → Log → Early-Return → **eine** Klassifikation → Schleife; `git diff` zeigt in
`AppDbContext.cs` keine Änderung.

**Ausdrücklich nicht:** kein Log pro Bot-Nachricht; keine Änderung an Roster/Health-Verträgen;
keine Klassifikation in `OnSendReceiveData`.

**Modell: sonnet** — kleiner, klar begrenzter Umbau; die zwei Gefahrenstellen sind benannt und
prüfbar.

---

### Task 4: Die Lesequeries lesen eine Bot-only-Zeile nicht als „benutzt"

**Files:**
- Modify: `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` (vier Stellen),
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs`,
  `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` (nur `<param>`-Kommentare, s. u.),
  `docs/DECISIONS.md` (Betrifft-Zeile des Eintrags aus Task 2)

**Vorab lesen:** `UsageStatQueryService.cs` vollständig, mit den Kommentaren zu Index-Only-Scan
und Regel 10; die `<param>`-Kommentare zu `LastUsedDate`, `FirstUsedDate` und `Days` in
`IUsageStatQueryService.cs` („the flush only ever writes rows for days with actual usage");
`UsageStatQueryServiceTests` (wie dort `UsageStat`-Zeilen per EF geseedet werden).

**Warum dieser Task existiert (Nutzerentscheidung vom 2026-09-01):** Vier Stellen deuten die
**Existenz** einer `UsageStat`-Zeile als Nutzung: `:66` (`LastUsedDate = g.Max(Date)` in
`GetUsageContextAsync`), `:119` (die sparse Tagesserie in `GetDailySeriesAsync`), `:124-129`
(`bounds` First/Last ebendort) und `:203` (die Roh-Zeilen der Channel-Serie in
`GetChannelSeriesAsync`). Heute ist das korrekt, weil der Flush nie eine Zeile mit `UseCount = 0`
schreibt — eine bot-getriebene Zeile liegt bei `UseCount > 0`, und „zuletzt benutzt" stimmt.
**Der Defekt entsteht erst durch die Trennung aus Task 2/3:** dieselbe Zeile wird zu
`UseCount 0 / BotUseCount n`, und ein Emote, das nur ein Bot postet, bekäme ein `lastUsedDate` von
heute bei `totalUseCount 0` — die Seite sortiert danach und schreibt „zuletzt benutzt am …", der
Drilldown zeigt Nulltage als Punkte, die DTO-Kommentare („absent maximum is the honest answer")
werden falsch. Das Prädikat `UseCount > 0` an genau diesen vier Stellen ist auf **Bestandsdaten ein
No-op** (es gibt keine Nullzeilen) und bleibt im **Index-Only-Scan**, weil `UseCount` Include-Spalte
des Covering-Index ist. Deshalb ein eigener, kleiner `fix:`-Commit direkt nach dem Schema-Commit —
er repariert, was jener Commit sonst kaputt machte, und lässt sich isoliert lesen.

**Interfaces:** keine Signaturänderung. Die `<param>`-Kommentare, die „Zeilen = Nutzung"
voraussetzen, bekommen den Halbsatz, dass eine Zeile mit `UseCount = 0` (nur Bot-Nutzung) für
diese Aussagen nicht zählt.

**Absicht und Verträge:**
- `GetUsageContextAsync`: `LastUsedDate` ist das Maximum **nur** über Zeilen mit `UseCount > 0`.
  Die beiden Summen brauchen kein Prädikat — sie summieren `UseCount`, und Null trägt nichts bei.
  Der Zuschnitt (erst Emote-ID-Liste, dann einfache `GroupBy`) bleibt wörtlich; das Prädikat kommt
  in die Aggregat-Lambda bzw. die `Where`-Klausel, je nachdem, was Npgsql als eine Query mit drei
  Aggregaten übersetzt — **prüfen**, nicht annehmen (Regel 10 gilt weiter).
- `GetDailySeriesAsync`: die sparse `days`-Liste und die `bounds`-Query filtern beide auf
  `UseCount > 0`; `TotalUseCount` wird weiterhin aus `days` summiert und ändert sich dadurch nicht.
- `GetChannelSeriesAsync`: die Roh-Zeilen filtern auf `UseCount > 0`, damit ein Emote ohne
  menschliche Nutzung im Zeitraum gar nicht erst in `Emotes` auftaucht („only emotes with at least
  one day of usage" bleibt wahr).
- `GetUsageStatsAsync` (Roh-Liste zum Debuggen) und `GetTotalsByEmoteIdsAsync` (reine Summe)
  bleiben unangetastet — Ersteres zeigt bewusst alles, Letzteres kann durch Nullen nicht falsch
  werden.
- Die Query-Kommentare zum Index-Only-Scan bekommen den Halbsatz, dass das Prädikat auf der
  Include-Spalte ausgewertet wird und den Scan nicht verlässt.

- [ ] **Step 1 (Tests zuerst, `UsageStatQueryServiceTests`):** je ein Fall pro Stelle, alle mit
  einer geseedeten Zeile `UseCount = 0, BotUseCount = 3` an einem jüngeren Tag neben einer
  Human-Zeile an einem älteren: (a) `GetUsageContextAsync` liefert als `LastUsedDate` den älteren
  Tag, nicht den Bot-Tag; ein Emote mit **ausschließlich** Bot-Zeilen hat `LastUsedDate == null`;
  (b) `GetDailySeriesAsync` führt den Bot-Tag nicht in `Days`, und `FirstUsedDate`/`LastUsedDate`
  ignorieren ihn (bei nur Bot-Zeilen beide `null`); (c) `GetChannelSeriesAsync` listet ein Emote
  mit ausschließlich Bot-Zeilen im Zeitraum nicht in `Emotes`, ein gemischtes nur mit seinen
  Human-Tagen; (d) Regressionsfall: eine gewöhnliche Zeile mit `UseCount > 0` verhält sich in allen
  drei Methoden wie bisher (die bestehenden Fälle decken das größtenteils schon — prüfen, dass sie
  unverändert grün bleiben, und nur die Lücke schließen).
- [ ] **Step 2: rot laufen lassen.** Filter `UsageStatQueryServiceTests`; Expected: die neuen
  Fälle FAIL mit dem Bot-Tag als Datum bzw. dem Bot-Tag in der Serie — das ist der Beleg, dass der
  Defekt ohne diesen Task real wäre.
- [ ] **Step 3: die vier Prädikate und die Kommentare.**
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter, dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 5:** Betrifft-Zeile ergänzen; `dotnet format`; Nutzer fragen; Commit
  `fix(usage): keep bot-only rows out of the usage read models`.

**Fertig-Bedingung:** Backend-Suite grün; `git diff` von `UsageStatQueryService.cs` zeigt genau
vier neue Prädikate und Kommentare, keine Umstellung eines Zuschnitts; kein Index angefasst.

**Ausdrücklich nicht:** kein Filter in `GetUsageStatsAsync` oder `GetTotalsByEmoteIdsAsync`; keine
Bot-Zahl in irgendeinem DTO; kein Anfassen von `UsageStatFlushService` (die Nullzeile ist
gewollt, E1).

**Modell: sonnet** — vier Prädikate mit klarer Begründung und je einem Testfall; die einzige
Unsicherheit (Übersetzung in `GetUsageContextAsync`) entscheidet der Test.

---

### Task 5: `EmoteSetStatusDto.BotsExcludedSince` — das Trenndatum aus den Daten

**Files:**
- Modify: `src/EmotePurge.Core/Services/IEmoteSetStatusService.cs` (siebter Parameter + `<param>`),
  `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteSetStatusServiceTests.cs`,
  `docs/DECISIONS.md` (nur `**Betrifft:**`-Zeile des Eintrags aus Task 2)

**Vorab lesen:** Spec A4 vollständig (beide Auflagen); `EmoteSetStatusService.cs` samt dem
Kommentar zum Überspringen bei leerem `ActiveEmoteSetId`; `UsageStatQueryService.GetUsageContextAsync`
(Regel-10-Kommentar und Zuschnitt); `EmoteEndpoints.cs:105-124` (der Endpunkt-Kommentar, der
Slot-Budget und `TrackedSince` als „same audience" begründet — dasselbe Argument trägt das neue
Feld); `EmoteSetStatusServiceTests`-Helfer.

**Interfaces (verbindlich):**
- `EmoteSetStatusDto(…, DateTime? LastSyncAttemptAtUtc, DateOnly? BotsExcludedSince)`. `<param>`:
  erster UTC-Tag, an dem in diesem Channel Bot-Nutzung getrennt gezählt wurde — `MIN(Date)` über
  Zeilen mit `BotUseCount > 0`; `null` = noch nie ein Bot erkannt, dann gibt es auch keinen Bruch
  zu erklären, und der Konsument zeigt **nichts**. Ausdrücklich: das ist die erste **Sichtung**,
  nicht der Deploy-Tag — die bekannte Einschränkung von E4 („Entscheidungen dieses Plans", Nr. 9)
  gehört in den `<param>`-Kommentar, damit niemand später aus dem Feld einen Deploy-Tag liest.
- `IEmoteSetStatusService.GetAsync` unverändert; der Endpunkt bleibt ein Durchreicher.

**Absicht und Verträge:**
- **Gefahrenstelle — übersprungen unter derselben Bedingung wie `occupiedSlots`:** solange
  `channel.ActiveEmoteSetId.Length == 0`, wird die `MIN`-Abfrage **nicht** gestellt und das Feld ist
  `null`. In genau diesem Fenster pollt die Usage-Seite den Endpunkt in einer Schleife auf den
  ersten Sync; eine Query je Poll für ein garantiertes Nichts ist der Fehler, den der bestehende
  Kommentar dort bereits abwehrt. Beide Abfragen hängen an **einer** Bedingung, nicht an zwei
  Kopien davon.
- **Regel 10 — Zuschnitt:** erst die Emote-IDs des Channels als skalare Liste laden (alle Emotes
  des Channels, **auch archivierte** — eine Bot-Zeile eines inzwischen archivierten Emotes sagt
  über den Zeitpunkt der Trennung genauso viel), dann `MIN` über `UsageStats` mit `Contains` auf
  diese Liste und `BotUseCount > 0`, projiziert auf `DateOnly?` (sonst wirft `MinAsync` bei leerer
  Menge). Kein Navigations-Join in der aggregierenden Query. **Die Übersetzung wird geprüft, nicht
  angenommen:** der Testfall „kein Bot-Treffer ⇒ `null`" ist zugleich der Nachweis, dass Npgsql das
  `MIN` über eine leere Menge als `NULL` liefert statt zu werfen.
- **Kosten, gemessen statt vermutet:** `BotUseCount` steht **nicht** im Covering-Index (Spec A3),
  die Abfrage liest also Heap-Zeilen aller Nutzungstage des Channels. Nach der Implementierung
  einmal `EXPLAIN (ANALYZE, BUFFERS)` der von EF erzeugten SQL gegen die Dev-DB am größten
  lokalen Channel laufen lassen (SQL aus dem EF-Log mit `Microsoft.EntityFrameworkCore.Database.Command`
  auf `Information` oder aus `ToQueryString()` im Test) und Laufzeit plus Plan in der
  Task-Rückmeldung nennen. Liegt sie über **20 ms**, ist das kein Grund, den Index anzufassen,
  sondern ein Punkt für O2 — der Nutzer entscheidet.

- [ ] **Step 1 (Tests zuerst, `EmoteSetStatusServiceTests`):** (a) zwei Emotes mit
  `UsageStat`-Zeilen an verschiedenen Tagen, nur die spätere mit `BotUseCount > 0` und eine noch
  frühere Human-only-Zeile ⇒ Feld ist der Tag der **frühesten Bot-Zeile**, nicht der frühesten
  Zeile; (b) Zeilen mit `BotUseCount = 0` ausschließlich ⇒ `null`; (c) leerer `ActiveEmoteSetId`
  **und** vorhandene Bot-Zeile ⇒ `null` und — als Beleg des Sprungs — die Abfrage wurde nicht
  gestellt (per `db.ChangeTracker`/EF-Log ist das umständlich; der pragmatische Beleg ist: Feld
  `null` trotz Bot-Zeile, denn nur der Sprung kann das erzeugen); (d) Bot-Zeile eines
  **archivierten** Emotes zählt mit. Seeds über die vorhandenen Helfer, `UsageStat`-Zeilen per EF
  wie in `UsageStatQueryServiceTests`.
- [ ] **Step 2: rot laufen lassen.** Filter `EmoteSetStatusServiceTests`; Expected: Compilerfehler.
- [ ] **Step 3: DTO, Service, Betrifft-Zeile.**
- [ ] **Step 4: grün laufen lassen** — Filter `EmoteSetStatusServiceTests`, dann
  `dotnet test EmotePurge.slnx` (die Api-Tests fahren `Program.cs` hoch und substituieren
  `IEmoteSetStatusService` **nicht**; sie müssen unverändert grün bleiben).
- [ ] **Step 5: `EXPLAIN ANALYZE`-Messung** wie oben, Ergebnis in die Rückmeldung.
- [ ] **Step 6:** `dotnet format`; Nutzer fragen; Commit
  `feat(api): report since when bot usage is counted apart`.

**Fertig-Bedingung:** Backend-Suite grün; `curl` auf `/api/channels/<name>/emotes/active-set`
(angemeldet, lokal) zeigt `botsExcludedSince` als `null` oder `yyyy-MM-dd`; Messwert liegt vor.

**Ausdrücklich nicht:** kein neuer Endpunkt, kein Feld auf `AdminChannelDto`, keine Änderung an
`UsageStatQueryService`, kein Index.

**Modell: sonnet** — eine Abfrage mit klarem Zuschnitt; die eine Unsicherheit (Übersetzung)
entscheidet der Test, die andere (Kosten) die Messung.

---

### Task 6: Frontend — der Hinweissatz, oder nichts

**Files:**
- Create: `web/src/app/core/emotes/bots-excluded-caption.ts` + `bots-excluded-caption.spec.ts`
- Modify: `web/src/app/core/emotes/emote-set-status.model.ts`,
  `web/src/app/core/emotes/emote-admin.service.spec.ts` (beide Objektliterale + das `toEqual`),
  `web/src/app/features/usage-stats/usage-stats-page.ts` (ein Computed neben `trackedSince`),
  `web/src/app/features/usage-stats/usage-stats-page.html` (im `<p>` ab `:194`),
  `web/public/i18n/de.json`, `web/public/i18n/en.json`,
  `web/e2e/support/mocks.ts` (`mockActiveEmoteSet`), `web/e2e/usage-atlas.e2e.spec.ts`,
  `docs/DECISIONS.md` (Betrifft-Zeile)

**Vorab lesen:** Spec E3 und A5; `usage-stats-page.html:185-212` (die Bildunterschrift mit
`trackedSince` und `liveDaysInRangeKey` — der Hinweis ist ein weiterer Satz **in diesem** Absatz,
kein neues Element, kein Banner); `usage-series.ts` (`liveDayCaptionKey`) und
`seven-tv-sync-failure.ts` als Muster für „Key oder `null`"-Helfer mit Spec;
`web/.claude/CLAUDE.md` (Angular-Memberreihenfolge, Signals); `docs/UI-Designsprache.md` zu
Unterschriften; `feedback_frontend_restraint` (kein neues Dauer-Bedienelement).

**Interfaces (verbindlich):**
- `EmoteSetStatus.botsExcludedSince: string | null` (`yyyy-MM-dd`), Doc-Kommentar mit der
  Sichtungs-Semantik aus Task 5.
- `botsExcludedCaptionKey(botsExcludedSince: string | null): string | null` — liefert
  `'usageStats.botsExcludedSince'` oder `null`. Trivial, und trotzdem die **eine** Stelle, an der
  die Sichtbarkeitsregel lebt: `null` ⇒ nichts, sonst der Satz. **Keine weitere Bedingung** — s.
  bekannte Einschränkung unten.

**Bekannte Einschränkung (Nutzerentscheidung vom 2026-09-01, keine Heuristik einbauen):** Das
Datum ist die erste *Sichtung* eines Bots, nicht der Beginn der Trennung. Für einen Channel, der
nach dem Deploy gejoint wurde, sind alle Zeilen sauber — der Satz „Zahlen davor enthalten sie
noch" ist dort falsch, sobald irgendwann ein Bot auftaucht; für Channels von vor dem Deploy
überzeichnet er die Tage zwischen Deploy und erster Sichtung. Der Bruch ist ein Deploy-Ereignis,
kein Datenereignis: aus den Daten lässt sich kein Diskriminator ableiten, ein korrekter Fix
bräuchte neuen Zustand, den E4 bewusst vermeidet. Ein Vergleich gegen `trackedSince` träfe den
Fall nicht (Join am 10.09., erster Bot am 11.09. ⇒ Hinweis bleibt und bleibt falsch). Die
Fehlrichtung ist konservativ: der Satz rät zur Vorsicht bei Zahlen, die in Wahrheit sauber sind.
Das steht als Kommentar am Helfer, nicht als Bedingung darin.
- i18n-Schlüssel `usageStats.botsExcludedSince` in **beiden** Dateien, sinngemäß: de „Nachrichten
  bekannter Bots zählen seit dem {{ date }} nicht mit; Zahlen davor enthalten sie noch." / en
  „Messages from known bots have not been counted since {{ date }}; numbers before that still
  include them." Ton wie `usageStats.trackedSince` — eine Ehrlichkeitsaussage, keine Warnung.

**Absicht und Verträge:**
- Im Template: `@if` auf das Computed, das den Key liefert; Datum über das bestehende
  `formatDate` (dieselbe Verwendung wie für `peak.date` im Sidecar — ein Date-only-String).
  Ist das Feld `null`, existiert im DOM **nichts** davon — kein leerer Knoten, kein Platzhalter.
- Kein Toggle, kein Signal für einen neuen Dauerzustand, keine Route, kein Banner.
- E2E-Mock: `botsExcludedSince?: string | null`, Default `null` — damit bleiben alle
  bestehenden Fälle (die das Feld nicht setzen) ohne Hinweis, was heute ihr Verhalten ist.
- Robustheit gegen alte Antworten: das Computed liest `?? null`, ein fehlendes Feld (alte Api
  während eines Deploys) verhält sich wie `null`.

- [ ] **Step 1 (Vitest zuerst):** `bots-excluded-caption.spec.ts` — `null` ⇒ `null`; Datum ⇒ Key.
  `emote-admin.service.spec.ts`: die beiden geflushten Literale und das `toEqual` um das Feld
  ergänzen (einmal `null`, einmal ein Datum), sonst meldet der Typcheck das unvollständige Literal.
- [ ] **Step 2: rot laufen lassen.** Include auf die neue Spec-Datei; Expected: FAIL (Modul fehlt).
- [ ] **Step 3: Modell, Helfer, Computed, Template, beide Locales, Mock-Default.**
- [ ] **Step 4: Playwright-Fall** in `usage-atlas.e2e.spec.ts`: einmal `mockActiveEmoteSet` mit
  `botsExcludedSince` ⇒ der Satz mit dem formatierten Datum steht im Unterschrift-Absatz; im
  Gegenfall (Default `null`) ist der Text abwesend. Die beiden Sätze `trackedSince` und
  `liveDaysInRange` müssen unverändert weiterhin erscheinen.
- [ ] **Step 5: Gates.** `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`,
  `npm --prefix web run format`, dann `npm --prefix web run e2e` — **vorher prüfen, dass auf
  `:5151` nichts lauscht**. Expected: alle grün; die Suite liegt bei rund 1,5 min.
- [ ] **Step 6:** Betrifft-Zeile ergänzen; Nutzer fragen; Commit
  `feat(web): say since when bot messages are not counted`.

**Fertig-Bedingung:** Vitest und E2E grün; im Browser (Dev-Stack, echter Login) erscheint der
Satz genau dann, wenn `/active-set` ein Datum liefert — was vor Task 7 lokal noch nicht der Fall
ist; der positive Fall wird dort gesichtet.

**Ausdrücklich nicht:** kein „mit Bots"-Schalter; keine Bot-Zahl irgendwo im UI; keine Änderung an
Drilldown, Export oder Voting.

**Modell: sonnet** — ein Satz, ein Helfer, i18n, Mock; Design-Entscheidung ist gefallen.

---

### Task 7: Verifikation, Dev-Migration mit Gegenprobe, Prod-Übergabe

**Files:** keine Code-Änderungen erwartet. Findet die Live-Verifikation einen Defekt, geht der
als eigener Fix-Task an einen Subagenten zurück — nicht „schnell hier".

**Vorab lesen:** Spec „Live-Verifikation (Regel 16)"; CLAUDE.md „Prod-Migration (manuell, über
SSH-Tunnel)"; `~/.claude/CLAUDE.md` „Fernzugriff (SSH)" — **nie selbst auf `vps` verbinden**, die
Befehle werden dem Nutzer aufbereitet; `feedback_prod_migration_handover`;
`project_prod_redeploy_stale_latest_image` (Prod-Redeploy fährt still das alte Image weiter).

- [ ] **Step 1 — Gates komplett:** `dotnet test EmotePurge.slnx` (Docker läuft),
  `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf `:5151`),
  `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run lint`.
- [ ] **Step 2 — Gegenprobe „altes Image gegen migriertes Schema"** (die Annahme hinter der
  Prod-Reihenfolge, Spec A3). Die Dev-DB ist seit Task 2 migriert. Die lokal gecachten
  `emote-purge-dev-api`/`-worker`-Images sind der **alte** Stand, solange seit dem ersten
  Code-Commit dieses Branches kein `--build` gelaufen ist — prüfen mit
  `docker compose images api worker` und dem `Created`-Zeitstempel aus
  `docker image inspect --format '{{.Created}}' emote-purge-dev-worker` gegen das Datum von Task 1.
  Dann **bewusst ohne `--build`** (die Umkehrung von Regel 15, hier der Zweck):
  `docker compose up -d api worker`. Nachweise: `GET /api/health` liefert 200; der alte Worker
  bootet (sein `IPendingMigrationGuard` sieht keine *ausstehende* Migration — die DB ist weiter als
  sein Build, nicht zurück); nach ≥ 30 s Chat in einem gejointen Channel loggt er **keinen**
  `Usage-Stat-Flush fehlgeschlagen`, und in der DB tragen die neuen Zeilen `BotUseCount = 0` aus
  dem Spalten-Default. Sind die gecachten Images doch schon Branch-Stand, den alten Stand aus einem
  `git worktree add <scratch>/main main` heraus mit `docker compose -p emote-purge-dev build api worker`
  bauen (Kopie der `.env` in den Worktree, sie ist gitignored) und die Probe wiederholen.
- [ ] **Step 3 — neuer Stand hoch:** `docker compose up -d --build api worker`
  (Regel 15). Für die Sichtung der Bot-Nachrichten temporär
  `Logging__LogLevel__EmotePurge.Worker.TwitchChatManager=Debug` am Worker — die bestehende
  Debug-Zeile zeigt Channel, Username und Text jeder Nachricht.
- [ ] **Step 4 — Live-Verifikation in den drei Schritten der Spec:**
  1. Worker läuft gegen einen getrackten Channel, in dem StreamElements oder Nightbot
     automatisiert postet (Timer-Nachrichten mit Emotes; im Zweifel den Broadcaster bitten, den
     Bot eine Emote-haltige Nachricht senden zu lassen).
  2. In der Dev-DB per `docker compose exec postgres psql -U emotepurge -d emotepurge` nachweisen:
     Zeile mit `BotUseCount > 0` **bei unverändertem `UseCount`** derselben `(EmoteId, Date)`-Zeile
     — also `UseCount` vor und nach der Bot-Nachricht gleich, `BotUseCount` um die Zahl der
     verschiedenen Emotes in der Nachricht höher. Parallel ein menschlicher Chatter mit demselben
     Emote ⇒ nur `UseCount` steigt.
  3. Kommt binnen vertretbarer Zeit (Richtwert: eine Stunde) kein Bot zustande: die Twitch-ID
     eines tatsächlich aktiven Chatters in `TWITCH_ADDITIONAL_BOT_ACCOUNT_IDS` eintragen
     (Worker neu starten), die Trennung wie in 2 nachweisen, Eintrag **wieder entfernen**, Worker
     neu starten.
  Zusätzlich, weil es die Gefahrenstelle aus Task 3 ist: nach einer Bot-Nachricht muss im
  Admin-Monitoring (Roster, `LastMessageUtc` des Channels) der Zeitstempel weitergelaufen sein —
  der Watchdog sieht Bot-Nachrichten weiterhin als Lebenszeichen.
- [ ] **Step 5 — Frontend-Sichtung:** Nach Schritt 4 liefert `/active-set` für diesen Channel ein
  Datum; die Nutzungsseite zeigt den Satz mit dem heutigen Datum in **beiden** Sprachen; ein
  Channel ohne Bot-Zeile zeigt nichts. Bild gehört in die Rückmeldung an den Nutzer.
- [ ] **Step 6 — Prod-Übergabe an den Nutzer** (Befehle vorbereiten, **nicht** ausführen):
  1. Merge auf `main` erst nach Task 8; CI baut die Images.
  2. **Vor** dem Deploy migrieren — Tunnel und die drei `dotnet ef`-Aufrufe (`migrations list`,
     `database update`, `migrations list`) wörtlich aus CLAUDE.md „Prod-Migration", Passwort als
     `<PROD-PW>`-Platzhalter, in der Shell des Nutzers. Erwartung beim ersten `list`: **genau eine**
     `(Pending)`-Zeile, `AddUsageStatBotUseCount`. Mehr heißt: Prod hängt hinterher, erst
     durchsehen.
  3. Portainer-Redeploy mit erzwungenem Pull (das Stack-`:latest` zieht sonst nicht neu — s.
     Memory); danach im Worker-Log den ersten erfolgreichen Flush und in der Prod-DB nach einem Tag
     die erste `BotUseCount > 0`-Zeile prüfen (read-only SELECT, wieder vom Nutzer).
  4. Optional im Stack: `TWITCH_ADDITIONAL_BOT_ACCOUNT_IDS` für channel-eigene Bots.
  5. **Die Messung, die E2 verlangt:** nach ein bis zwei Wochen ein SELECT pro Channel über
     `SUM(BotUseCount)` gegen `SUM(UseCount)` seit dem Deploy-Tag — das Ergebnis entscheidet, ob
     die 903 Multi-Channel-IDs oder eine Pflegeoberfläche je lohnen. Dem Nutzer als fertiges
     SELECT mitgeben, Termin ist seine Sache.
- [ ] **Step 7 — Rückmeldung** an den Nutzer mit Messwerten (Task 5), Live-Nachweisen (SQL-Ausgabe
  vor/nach), Screenshots und der offenen Liste unten. Kein Eintrag im Feature-Backlog: #31 kam als
  GitHub-Issue, der Stand steht am Issue.

**Fertig-Bedingung:** alle Gates grün; Gegenprobe bestanden; `BotUseCount > 0` bei unverändertem
`UseCount` an einer echten Zeile belegt; Hinweis im Browser gesichtet; Prod-Befehle übergeben.

**Modell: opus** — Live-Debugging gegen drei Fremdsysteme mit Interpretationsbedarf, kein
Schreiben von Produktivcode.

---

### Task 8: Zweitmeinung vor dem Merge (Regel 22)

- [ ] **Step 1:** `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — alle Flags
  in **einem** String (das Plugin liest getrennte Argumente als Fokustext), `--scope branch`
  ausdrücklich (ohne ihn reviewt Codex den Working Tree und meldet bei sauberem Tree eine falsche
  Entwarnung). „Reviewer failed to output a response" mit Exit 1 ist das Kontingent, kein Absturz —
  Job-Log lesen, nicht neu starten. Einmal je Branch, nicht je Task, nie zweimal für dasselbe Diff.
- [ ] **Step 2:** Findings unverändert an den Nutzer. Widerspricht Codex einem Opus-Review
  (P1/P2, das die andere Seite nicht sieht; gegensätzliche Bewertung derselben Stelle; unvereinbare
  Fixes), entscheidet **Fable** als Schiedsrichter — nur die strittigen Findings plus die
  betroffenen Stellen, nicht das ganze Diff. Reine Ergänzungen sind kein Widerspruch.
- [ ] **Step 3:** Merge auf `main` und Push macht der Nutzer; Prod-Reihenfolge aus Task 7.

---

## Tests der Spec → Task-Zuordnung

| Spec-Zeile | Task | Datei |
|---|---|---|
| Detektor: Badge · statische ID · Konfig · Unbekannter · leere/fehlende ID · Konfig leer/Leerzeichen/Duplikat | 1 | `Worker.Tests/BotChatterDetectorTests.cs` |
| Zähler: beide Arten getrennt · `Merge` erhält beide · Drain gibt beide zurück und leert | 3 | `Worker.Tests/EmoteUsageCounterTests.cs` |
| Upsert: neue Zeile · Konflikt addiert in beide Spalten · gemischter Batch · nur-Bot-Batch | 2 | `Integration/UsageStatFlushServiceTests.cs` |
| (Nutzerentscheidung) Lesequeries: Bot-only-Zeile setzt kein `lastUsedDate`/`firstUsedDate`, erscheint nicht in der sparsen Serie, nicht in der Channel-Serie | 4 | `Integration/UsageStatQueryServiceTests.cs` |
| `EmoteSetStatusService`: Datum vorhanden · kein Bot-Treffer ⇒ `null` · Sprung bei leerem Set | 5 | `Integration/EmoteSetStatusServiceTests.cs` |
| Vitest: Sichtbarkeit (`null` ⇒ nichts) | 6 | `core/emotes/bots-excluded-caption.spec.ts` |
| (Plan-Zusatz) Rendering des Satzes | 6 | `e2e/usage-atlas.e2e.spec.ts` |
| Keine Api-Testfälle | — | kein neuer Filter, keine Reihenfolgeänderung |

## Selbstprüfung (beim Schreiben dieses Plans)

- **Spec-Deckung:** A1 → Task 1, A2 → Task 3 (Typ in Task 2), A3 → Task 2, A3b (Nutzerentscheidung
  zu den Lesequeries) → Task 4, A4 → Task 5, A5 → Task 6; Live-Verifikation und
  Migrations-Gegenprobe → Task 7; „Nicht vergessen" (Regel 3 im Schema-Commit, Regel 1, Regel 22,
  kein Backlog-Eintrag) → Global Constraints, Task 3 Step 5, Task 8, Task 7 Step 7. Alle vier
  Gefahrenstellen der Spec stehen an ihrer Stelle: Watchdog-Reihenfolge und Einmal-pro-Nachricht in
  Task 3, Sprung bei leerem Set und Regel 10 in Task 5, Covering-Index unverändert in Task 2 (und
  als Diff-Prüfung in Task 3 und 4).
- **Nutzerentscheidungen vom 2026-09-01 eingearbeitet:** ehemals O1 → Task 4 mit eigenem
  `fix:`-Commit, Begründung im Task und im DECISIONS-Text; ehemals O3 → bekannte Einschränkung in
  Task 6 und im DECISIONS-Text (Task 2), Plan-Entscheidung Nr. 9, keine Heuristik. O2 und O4 bleiben
  offen (unten).
- **Namen gegen den Code geprüft:** jede Datei, Methode und jedes Feld in der Ist-Tabelle stammt
  aus dem Lesen des Branch-Stands `eb54665`, nicht aus der Spec; die vier Zeilennummern in Task 4
  hat der Nutzer bestätigt.
- **Namenskonsistenz:** `IBotChatterDetector.IsBot` (1 → 3), `EmoteUsageCounts` (2 → 3),
  `BotUseCount` (2 → 4 → 5 → 7), `BotsExcludedSince`/`botsExcludedSince` (5 → 6 → 7),
  `botsExcludedCaptionKey` und `usageStats.botsExcludedSince` (6), `Twitch:AdditionalBotAccountIds`
  / `TWITCH_ADDITIONAL_BOT_ACCOUNT_IDS` (1 → 7), Migration `AddUsageStatBotUseCount` (2 → 7).
- **Kein fertiger Code:** Signaturen als Einzeiler, SQL- und Query-Absichten in Worten, Tests als
  Fallnamen; die Locale-Sätze sind Inhalt, kein Code.

## Offene Punkte für den Nutzer

O1 und O3 sind am 2026-09-01 entschieden und stehen dort, wo sie wirken (Task 4; Task 6 und
DECISIONS-Text; „Entscheidungen dieses Plans" Nr. 8 und 9). Offen bleibt nur, was tatsächlich
offen ist — beides braucht keine Entscheidung vor dem Start, sondern ein Messergebnis aus dem Lauf:

- **O2 — Kosten der `MIN(Date)`-Abfrage ohne Index-Stütze.** `BotUseCount` steht bewusst nicht im
  Covering-Index (A3); die Abfrage aus Task 5 liest deshalb die Heap-Zeilen aller Nutzungstage des
  Channels bei **jedem** `/active-set`-Aufruf (Usage-Seite, Voting-Seite, Poll-Schleife nach dem
  ersten Sync). Bei ~900 Emotes × Tage seit Juli sind das zehntausende Zeilen. Task 5 misst; liegt
  der Wert spürbar über 20 ms, wären die Optionen: (a) ein kleiner partieller Index
  `(EmoteId) WHERE "BotUseCount" > 0` (winzig, weil Bot-Zeilen selten sind — aber ein Index, den
  die Spec nicht vorsieht), (b) das Datum einmal berechnen und am `Channel` persistieren (zweite
  Spalte, mehr Zustand), (c) akzeptieren. Kein Widerspruch zur Spec, aber die Spec hat die Kosten
  nicht beziffert.
- **O4 — `bot-badge` ist in der Spec nicht als live verifiziert markiert.** Die sechs Konten-IDs
  sind es; der Badge-Set-Schlüssel steht ohne Messung da. Task 7 kann ihn nur bestätigen, wenn ein
  Bot mit diesem Badge postet. Bleibt er unsichtbar, ist das ein Befund für den Nutzer, kein Grund,
  die Konstante still zu ändern — die statische Liste trägt die Erkennung ohnehin allein.
