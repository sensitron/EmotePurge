# Genauigkeits-Harness für den Chat-Log-Backfill (Issue #69): Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code**
> (globale Regel, s. `~/.claude/CLAUDE.md`): jeder Task beschreibt Absicht, Verträge, Grenzfälle
> und Prüfbedingungen. Methodenrümpfe, Testmethoden, Compose-Blöcke und Fixture-Inhalte entstehen
> im Task selbst. Signaturen stehen einzeilig, wo sie einen Vertrag zwischen Tasks festlegen.

**Goal:** Der Betreiber bekommt eine Zahl, die entscheidet, ob importierte Chat-Log-Nutzung
gekennzeichnet ins Raster, in die Kurve und in den Manager-Kontext darf (Feature B) oder nur als
getrennte Historie: ein Einmal-Container zählt für einen Kanal die Emote-Nutzung aus dem
Log-Archiv mit exakt der Regel des Live-Pfads nach und vergleicht sie gegen die eigenen
`UsageStat`-Zeilen. Nichts wird persistiert, kein Schema, kein UI, kein Endpunkt. Was dabei
entsteht (Matching-Klasse, Log-Client, Query-Methoden), verwendet B weiter.

**Architecture:** Sieben Bausteine entlang des Datenflusses aus dem Design-Doc. **(A1)** Eine
geteilte, TwitchLib- und EF-freie Matching-Klasse in `Core` mit zwei Funktionen: Token-Matching
(Split an Leerzeichen, ordinaler Lookup, Dedup je Nachricht) und Namens-Koaleszenz (erster
gewinnt, mehrdeutige Namen werden gemeldet). Live-Pfad, Match-Cache-Aufbau und Harness rufen sie
gleichermaßen. **(A2)** Fensterstart (`TrackingResumedAt ?? CreatedAt`) als reine Funktion in
`Core` und der Bot-Split-Stichtag (frühestes `Date` mit `BotUseCount > 0`) als Methode am
Query-Interface; `EmoteSetStatusService` und Harness rufen beide. **(A3)** Ein typisierter
Log-Archiv-Client (Interface in `Core`, Implementierung in `Infrastructure`), der `?raw` je
Kanal-Tag über `ResponseHeadersRead` zeilenweise liest, IRC-Zeilen zu Nachrichten parst, mit
festem Abstand strikt sequenziell anfragt, je Abruf einen Body-Timeout-CTS hält, bei 429 stoppt
und eine Byte-Decke respektiert. **(A4)** Zwei Query-Methoden an `IUsageStatQueryService`
(Emotes eines Kanals inklusive archivierter mit `FirstSeenAt`/`ArchivedAt`/`LastSyncedAt`;
`UsageStat`-Zeilen des Fensters über eine ID-Liste), einmal je Lauf geladen. **(A5)** Eine reine
Rechenlogik im Worker: Tagesmap mit Grenzen, Gründe „nicht zuordenbar", Gesamtabweichung über die
volle Population, Top-20-Recall, Quartil-Precision, Tagesverhältnis mit Abdeckungs-Markierung,
stabile Teilmenge als Diagnostik, k=1-Kennzahl. **(A6)** Ein zweiter Einstiegspunkt des
Worker-Images (`harness <kanal>`) mit strikter Argumentprüfung vor dem Host-Aufbau, ohne Hosted
Service, mit JSONL-Bericht (Kopf mit Lauf-Identität und Input-Hash, Tageszeilen mit Body-Digest,
atomarer Abschluss, Resume nur bei identischem Kopf). **(A7)** Ein Compose-Service `harness` in
beiden Compose-Dateien mit `harness` im `entrypoint`, eigenem Profil, Bind-Mount und
Speicherlimit; der DECISIONS-Eintrag dazu im selben Commit.

**Tech Stack:** .NET 10 (Worker Service mit TwitchLib.Client 4.0.1, EF Core/Npgsql, typisierte
`HttpClient`s, `System.Text.Json`, `SHA256`), xUnit + NSubstitute (container-frei) und
Testcontainers (Postgres), Docker Compose (Profile, `entrypoint`). Kein Angular-Anteil.

**Spec:** [`docs/designs/Chat-Log-Backfill-69-2026-09-05.md`](../../designs/Chat-Log-Backfill-69-2026-09-05.md),
Commit `cc3ca90`, plus der **ungecommittete Codex-Nachtrag** im Working Tree (Abschnitt
„Codex-Adversarial 2026-09-05: Ergebnisse" mit Präregistrierung und T8-Live-Proben; `git diff`
zeigt 102 neue Zeilen). Verbindlich sind „Recommended Approach A", „Eng-Review 2026-09-05:
Ergebnisse" (Datenfluss, Failure Modes, Lanes A–D, Tasks T1–T10) und der Codex-Nachtrag; bei
Widerspruch gilt der jüngere Abschnitt. Die Entscheidungen dort werden hier nicht neu aufgerollt.
Der Nachtrag gehört vor dem ersten Code-Commit in einen eigenen `docs:`-Commit (s. Reihenfolge).

## Ist-Zustand, am Code verifiziert (2026-09-05, HEAD `cc3ca90`)

Das Design-Doc wurde am Code geschrieben; seitdem sind `06b474d` (Design #38), `3c79547`
(Angular-Cache ignoriert), `973d64d` (T10, Modul-C-Zeile) und `cc3ca90` (Design #69) dazugekommen,
alle ohne Produktivcode. Die Annahmen des Docs halten; die Abweichungen stehen fett.

| Ort | Befund |
|---|---|
| `src/EmotePurge.Worker/Program.cs` | Top-Level-Statements: `Host.CreateApplicationBuilder(args)` (`:6`, **die Argumente landen damit auch in der Konfiguration**), `AddEmotePurgeInfrastructure` (`:7`), sieben Singletons (`:8-15`, darunter `IBotChatterDetector`), neun `AddHostedService` (`:23-31`), `Build()` (`:33`), `IPendingMigrationGuard` in eigenem Scope (`:37-41`), `host.Run()` (`:43`). Kein Argument wird ausgewertet, `BackgroundServiceExceptionBehavior` ist nicht konfiguriert (Default `StopHost`). |
| `src/EmotePurge.Worker/Dockerfile` | `ENTRYPOINT ["dotnet", "EmotePurge.Worker.dll"]` in Exec-Form (`:40`), kein `CMD`. `ENV Worker__HeartbeatFilePath=/tmp/alive` (`:37`) und ein `HEALTHCHECK` auf diese Datei (`:38-39`); im Harness-Zweig gäbe es keinen Publisher, der sie berührt. |
| `docker-compose.yml` | Projektname `emote-purge-dev` (`:4`); `worker` (`:83-113`) mit `build:`, `container_name: emotepurge-dev-worker`, `deploy.resources.limits` 1 CPU / 512M (`:89-93`), Env `:94-107`, **keine Volumes, keine Profile, kein `entrypoint`** in der Datei. |
| `docker-compose.prod.yml` | `worker` (`:85-114`) aus `ghcr.io/sensitron/emotepurge-worker:latest`, dieselben Limits als **`deploy.resources.limits`** (`:89-93`), **nicht `mem_limit`** wie das Design schreibt; `volumes:`-Abschnitt `:116-120` (nur benannte Volumes). |
| `src/EmotePurge.Worker/TwitchChatManager.cs` | Primärkonstruktor `:12-17`. `OnMessageReceived` `:488-527`: Watchdog-Schreibvorgänge `:496-500`, Debug-Log `:502-503`, `emoteMatchCache.GetChannelEmotes` mit frühem Return `:505-509`, genau ein `IsBot(e.ChatMessage.UserId, e.ChatMessage.Badges)` `:515`, `HashSet<string> matchedThisMessage` `:517`, `Message.Split(' ')` mit `TryGetValue` und `matchedThisMessage.Add` `:518-524`, `usageCounter.Increment(emoteId, isBot)` `:522`. **`source-room-id` wird nirgends im Worker gelesen** (Shared-Chat-Spiegelungen zählen mit, s. Memory). |
| `src/EmotePurge.Worker/EmoteUsageCounter.cs` | `Increment(string emoteId, bool isBot)` `:14-19` mit closure-freier `AddOrUpdate`-Überladung; bleibt unangetastet. |
| `src/EmotePurge.Worker/BotChatterDetector.cs` | Konstruktor nimmt `IConfiguration` (`:38-47`), vereinigt sechs statische IDs (`:26-34`) mit `Twitch:AdditionalBotAccountIds` (`:70-85`, Skalar gewinnt); `IsBot` `:49-63` prüft `bot-badge` und dann die ID-Menge. **Die vereinigte ID-Menge ist privat (`_botAccountIds`, `:36`); das Design will sie im Berichtskopf.** `IBotChatterDetector` hat genau eine Methode. |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs` | `RefreshMatchCacheAsync` `:333-372`: lädt nur `!IsArchived` (`:335-338`), koalesziert per `Dictionary.TryAdd` in Ladereihenfolge (`:344-352`, Default-Comparer, also ordinal), meldet die Kollisionen an `IDuplicateEmoteNameTracker` (`:354-370`), `ReplaceChannel` (`:372`). `ReconcileAsync` `:376`: **die REST-Archivierung stempelt `IsArchived`/`ArchivedAt`, nicht `LastSyncedAt`** (`:406-407`); der Dispatch-Pull stempelt alle drei (`:247-249`); Umbenennung/Restore stempeln `LastSyncedAt` (`:451-460`); `FirstSeenAt` wird nur im REST-Pfad und nur für aktive Emotes nachgezogen (`:439-441`), archivierte bleiben `null`. Das bestätigt die Teilmengen-Definition des Designs wörtlich. `EmoteService.cs:32-36` und `:82-85` (In-App-Archivierung/Restore) stempeln `LastSyncedAt` ebenfalls. |
| `src/EmotePurge.Infrastructure/Services/EmoteMatchCache.cs`, `src/EmotePurge.Core/Services/IEmoteMatchCache.cs` | Cache liegt in `Infrastructure`, Interface in `Core`; hält je Kanal ein `IReadOnlyDictionary<string, string>` (Name zu Emote-Id). Test: `Unit/EmoteMatchCacheTests.cs`. |
| `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs` | Ein Gate `ActiveEmoteSetId.Length == 0` (`:24`) für `occupiedSlots` und `botsExcludedSince`; Regel-10-Zuschnitt: erst Emote-ID-Liste inklusive archivierter (`:41-44`), dann `MinAsync` über `UsageStats` mit `BotUseCount > 0`, projiziert auf `DateOnly?` (`:46-49`); `TrackedSince = channel.TrackingResumedAt ?? channel.CreatedAt` (`:56`). Genau die zwei Regeln, die T2 herauszieht. |
| `src/EmotePurge.Core/Services/IUsageStatQueryService.cs`, `.../Services/UsageStatQueryService.cs` | Fünf Methoden (`:116-150`), alle kanalnamenbasiert außer `GetTotalsByEmoteIdsAsync` (`:149`, ID-Liste). Regel-10-Kommentar `UsageStatQueryService.cs:31-37`; `GetTotalsByEmoteIdsAsync` `:227-245` ist das Muster „`ids.Contains(u.EmoteId)` über die eine Tabelle". DTOs liegen neben dem Interface. |
| `src/EmotePurge.Core/Services/IEmoteService.cs` | Nur `MarkDeletedAsync`/`MarkRestoredAsync` (`:24`, `:29`), also ein Kommando-Service. **Es gibt im ganzen Repo keine Abfrage, die Emotes inklusive archivierter mit `FirstSeenAt`/`ArchivedAt`/`LastSyncedAt` liefert**; sie ist neu (T4). |
| `src/EmotePurge.Core/Entities/Emote.cs` | `IsArchived` (`:15`), `ArchivedAt` (`:21`, Kommentar: `null` auf archivierter Zeile heißt „Datum unbekannt", kein Backfill), `FirstSeenAt` (`:26`, `null` = unbekannt), `LastSyncedAt` (`:27`, nicht nullbar). |
| `src/EmotePurge.Core/Entities/UsageStat.cs`, `Channel.cs` | `Date` (`DateOnly`, UTC-Kalendertag, `:40`), `UseCount` (`:41`), `BotUseCount` (`:46`). `Channel.TwitchChannelId` ist `string?` (`:55`), `CreatedAt` (`:65`), `TrackingResumedAt` (`:72`). |
| `src/EmotePurge.Core/Services/IChannelService.cs` | `GetByNameAsync(string channelName, ct)` (`:104`) liefert die Entität; reicht für die Vorbedingungen des Harness. |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | Drei `AddHttpClient<TInterface, TImpl>` mit `BaseAddress`, `Timeout` 10 s und `ProviderTelemetry`-Handler (`:68-74`, `:78-83`, `:86-95`). **Registriert keinen Hosted Service** (geprüft per grep). **Der Ordner `src/EmotePurge.Infrastructure/Http/` aus dem Design existiert nicht**: Clients liegen je Anbieter in `Infrastructure/SevenTv/` und `Infrastructure/Twitch/`, ihre Interfaces und DTO-Records in `Core/SevenTv/` und `Core/Twitch/`. `RateLimitProviders` kennt nur `twitch` und `seventv` (`Core/Services/IRateLimitTelemetry.cs:77-82`); ein dritter Anbieter würde die Admin-Monitoring-Seite berühren. |
| Tests | `tests/EmotePurge.Worker.Tests` ist container-frei, ohne Fixture-Ordner; Klassen werden direkt konstruiert, `WorkerBootSequenceTests` baut `Worker` mit NSubstitute (`:52-60`). **Kein Test startet `Program`.** `tests/EmotePurge.Infrastructure.Tests`: `Fakes/RecordingLogger.cs`, `Fixtures/PostgresFixture.cs`, `RedisFixture.cs`; **kein geteilter Fake-`HttpMessageHandler`**: `Unit/TwitchHelixClientTests.cs` hält `StubHandler` (`:121`) und `SequencedStubHandler` (`:135`) als private Nested Classes; `CreateClient` (`:114`) baut `new HttpClient(handler) { BaseAddress }`. `Integration/EmoteSetStatusServiceTests.cs` ist `[Collection("Postgres")]` mit `SeedChannelAsync(db, name, capacity, activeEmoteSetId)` und `SeedEmoteAsync(db, channelId, name, isArchived)`. `Unit/CoreAssemblyReferenceTests` wacht über die BCL-Reinheit von `Core`. Das Infrastructure-Testprojekt referenziert `Core` direkt und trägt die `Core`-Unit-Tests. |
| TwitchLib.Client.Models 4.0.1 | Enthält die ACTION-Entpackung (`"ACTION "`, `IsMe`); `ChatMessage.Message` ist bei `/me`-Nachrichten der Innentext. Der Zeilen-Parser des Harness muss das spiegeln. |
| `docs/DECISIONS.md` | Format: `### YYYY-MM-DD — Titel`, dann `**Betrifft:**` mit Dateipfaden, dann fett gesetzte Absatz-Anker; neuester Eintrag zuoberst (`:13`). |
| `CLAUDE.md:138`, `docs/Architectur.md:289-301` | „neun Hosted Services" und die Compose-Topologie 6a/6b. Beide brauchen mit T7 je einen Satz zum zweiten Einstiegspunkt und zum `harness`-Service. |
| Worktrees | `/home/dev/projects/EmotePurge-import` auf `feat/emote-import-38` läuft parallel (Ressourcen-Aufteilung vom 2026-09-05). |

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1:** vor jedem `git commit` erst den Nutzer fragen. Die Commit-Zeilen unten sind
  Vorschläge für die Rückfrage.
- **Regel 2:** Conventional Commits, englisch, ein Commit je Task (Tabelle unten).
- **Regel 3:** zwei DECISIONS-Einträge, je im Commit der Änderung: Task 1 (geteilte
  Matching-Regel als Vertrag zwischen Live-Pfad, Match-Cache und Harness) und Task 7
  (Einstiegspunkt des Worker-Images, Compose-Service, geteilte Fensterstart-/Stichtagsregel,
  Query-Methoden). Task 2 und 4 sind verhaltensneutrale Refactorings bzw. reine Ergänzungen und
  bekommen keinen eigenen Eintrag; Task 7 nennt ihre Dateien in der Betrifft-Zeile.
- **Regel 4 / Schichtentreue:** die Matching-Klasse und die Fensterstart-Funktion liegen in `Core`
  und bleiben BCL-only (`CoreAssemblyReferenceTests` wacht). Der Harness liest die DB nur über
  `IChannelService` und `IUsageStatQueryService`, nie über `AppDbContext`; er berührt Redis nie.
- **Regel 5:** Log-Client mit Interface (externe Abhängigkeit); Matching-Klasse, Fensterstart,
  Zeilen-Parser, Rechenlogik und Argument-Parser sind statische bzw. reine Klassen ohne Interface
  (Design D1: keine eigene Abstraktion je Funktion; Präzedenz `ChannelName`, `ReconnectPolicy`).
- **Regel 7:** kein neuer Fehlercode, keine Frontend-Änderung.
- **Regel 10:** die neuen Abfragen filtern über eine skalare Emote-ID-Liste, kein `GroupBy` über
  Navigations-Joins; die Übersetzung wird per Integrationstest geprüft, nicht angenommen.
- **Regel 11:** `Core`-Logik testet `tests/EmotePurge.Infrastructure.Tests/Unit` (dort liegen
  schon `EmoteMatchCacheTests` und `CoreAssemblyReferenceTests`); Infrastruktur-Abfragen
  `Integration/` (Testcontainers); Log-Client `Unit/` mit Fake-Handler; Worker-Logik
  (Rechenlogik, Argument-Parser, Registrierung, JSONL) `tests/EmotePurge.Worker.Tests`.
  Kein `Api.Tests`-Fall (kein Filter, keine Route). `TwitchChatManager` wird live verifiziert.
- **Regel 16:** Live-Verifikation vor dem Merge steht in Task 8, „läuft durch" ist keine.
- **Regel 18:** `dotnet format EmotePurge.slnx` vor jedem Commit.
- **Regel 19:** C#-Memberreihenfolge wie in CLAUDE.md.
- **Sprache:** Bezeichner und Kommentare englisch, Log-/`throw`-/stderr-Meldungen deutsch,
  DECISIONS und Berichtstexte deutsch, Commit-Messages englisch.
- **Fixtures:** ausschließlich synthetische Chat-Zeilen mit erfundenen Nutzern und IDs. **Keine
  aufgezeichnete IRC-Zeile, keine echte `user-id`, kein echter Nachrichtentext** wandert ins Repo
  (Design, Eng-Review 5A). Die lokale Vorher/Nachher-Probe in Task 1 bleibt außerhalb des Repos.
- **„Fertig" heißt:** `dotnet test EmotePurge.slnx` grün (Docker läuft) und
  `dotnet format EmotePurge.slnx --verify-no-changes` sauber. Kein `npm test`, kein E2E: keine
  Datei unter `web/` ändert sich (s. Definition of Done).
- **Befehle:**
  - ein Testprojekt: `dotnet test tests/EmotePurge.Worker.Tests/EmotePurge.Worker.Tests.csproj`
  - ein Test: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EmoteNameMatchingTests"`
  - Compose-Prüfung: `docker compose config --profile harness` bzw. `docker compose -f docker-compose.prod.yml config --profile harness`
  - lokaler Harness-Lauf: `dotnet run --project src/EmotePurge.Worker -- harness brudivoeller_tv --days 3`

## Ressourcen-Regeln für die Ausführung

Gelten, solange die #38-Sitzung im Worktree `EmotePurge-import` läuft (Memory „Ressourcen-Aufteilung
#38/#69"), und werden in jedem Subagent-Auftrag mitgegeben:

- **Der Worker gehört #69, es läuft immer genau einer.** Vor jedem
  `dotnet run --project src/EmotePurge.Worker` (auch für den Harness-Zweig, die Regel kennt keine
  Ausnahme) erst `docker compose stop worker`, danach `docker compose up -d worker` (bzw. `--build`,
  wenn sich Worker-Code geändert hat, Regel 15).
- **Port 5151 und die E2E-Suite gehören #38.** #69 startet nie `dotnet run` der Api. Braucht ein
  Task die Api (Login, Kanal anlegen), dann als Container auf `:8080` per
  `docker compose up -d --build api`.
- **`docker compose up -d --build` nie ohne Service-Namen.** Ein `up` ohne Namen ersetzt das
  Api-Image der anderen Sitzung und startet einen zweiten Worker.
- **Testcontainers dürfen parallel laufen**, auch aus mehreren Worktrees; sie tragen eigene
  Container und Ports.
- **Worktrees starten keinen Compose-Stack.** Der Projektname `emote-purge-dev` ist in der Datei
  fixiert; ein `up` aus einem Worktree träfe denselben Stack.

## Reihenfolge, Lanes und Commits

| Task | Design | Lane | Inhalt | Commit | Modell | human / CC |
|---|---|---|---|---|---|---|
| 0 | keiner | keine | Codex-Nachtrag am Design-Doc committen | `docs: record the codex adversarial review for the backfill harness (#69)` | Hauptsession | 5 min / 1 min |
| 1 | T1 | A | Matching-Klasse (Match + Koaleszenz) in `Core`, Live-Pfad und Match-Cache umgestellt, Fixtures, DECISIONS | `refactor(matching): share the emote name matching rule between chat and match cache` | sonnet | 1 Tag / 30 min |
| 2 | T2 | C | Fensterstart als reine Funktion, Stichtag am Query-Interface, `EmoteSetStatusService` ruft beide | `refactor(usage): extract the tracking window and bot cutover rules` | sonnet | 2 h / 10 min |
| 3 | T3 | B | Log-Archiv-Client + Zeilen-Parser, Fake-Handler-Tests, Registrierung | `feat(infra): add the streaming chat log archive client` | sonnet | 1 Tag / 30 min |
| 4 | T4 | C | Zwei Query-Methoden (Emote-Lebenszeiten, Fensterzeilen) + Integrationstests | `feat(usage): expose emote lifetimes and window rows for the replay harness` | sonnet | 3 h / 15 min |
| 5 | T5 | A | Reine Rechenlogik des Harness + Tests | `feat(harness): compute replay fidelity from day counts` | opus | 1 Tag / 30 min |
| 6 | T6 | nach Merge | Einstiegspunkt, Argumentprüfung, Registrierung ohne Hosted Service, JSONL, Resume, Abschluss | `feat(worker): add the harness entry point with a resumable jsonl report` | opus | 1 Tag / 30 min |
| 7 | T7 | nach 6 | Compose-Service in beiden Dateien, `.env.example`, Doku-Sätze, DECISIONS | `feat(compose): run the backfill harness as a one-off worker container` | sonnet | 1 h / 10 min |
| 8 | Regel 16 | nach 7 | Gates, Vorher/Nachher-Probe, lokaler Harness-Lauf, Prod-Übergabe | kein Code-Commit | opus | 3 h / 30 min |
| 9 | Regel 22 | vor Merge | Codex-Sol-Zweitmeinung, ggf. Fable als Schiedsrichter | kein Commit | (Codex) | 30 min / 10 min |
| 10 | T9 | nach Merge | T8-Bericht und Präregistrierung als Kommentare in #69 | kein Commit | Nutzer | 30 min / 5 min |

**Erledigt, nur Vermerk:** T8 (Live-Proben, 2026-09-05, Ergebnisse im Design-Doc) und T10
(Modul-C-Zeile in `CLAUDE.md`, Commit `973d64d`).

**Lanes (aus dem Design):** A = Task 1 → Task 5 (sequenziell, `Core/Matching`, `Worker/`,
Tests), B = Task 3, C = Task 2 → Task 4 (sequenziell, `Core/Services`, `Infrastructure/Services`,
`Integration/`). A, B und C starten parallel in je einem eigenen Worktree auf einem gemeinsamen
Integrationsbranch `feat/chat-log-harness-69`; Lane-Branches `feat/harness-69-lane-a|b|c`,
Worktrees unter `/home/dev/projects/EmotePurge-harness-a|b|c`. Nach dem Merge der drei Lanes
folgen 6, 7, 8, 9 sequenziell auf dem Integrationsbranch.

**Erwartbare Konflikte:**
- Innerhalb von #69: keine Datei wird von zwei Lanes geschrieben. A schreibt
  `SevenTvSyncService.cs`, C schreibt `EmoteSetStatusService.cs`, `UsageStatQueryService.cs`,
  `IUsageStatQueryService.cs`; beide unter `Infrastructure/Services/`, verschiedene Dateien. B ist
  die einzige Lane an `ServiceCollectionExtensions.cs`. Nur A schreibt `docs/DECISIONS.md` (Task 1).
- Mit #38 (eigener Worktree): `docs/DECISIONS.md` (beide fügen oben Einträge ein, beide Einträge
  bleiben, Sortierung absteigend nach Datum) und `ServiceCollectionExtensions.cs` (beide
  registrieren, additive Blöcke). Beim Merge auf `main` von Hand zusammenführen, nichts verwerfen.
- Task 6 hängt an allen fünf Lanes-Tasks: `IsBot`-Signatur und Matching-Signatur (Task 1),
  Fensterstart/Stichtag (Task 2), Client-Vertrag (Task 3), DTOs der Query-Methoden (Task 4),
  Rechenlogik-Typen (Task 5). Die Namen stehen hier unter „Interfaces"; der Task-6-Subagent liest
  zusätzlich die gemergten Dateien.

## File Structure

```
src/EmotePurge.Core/
  Matching/EmoteNameMatching.cs                     (C, Task 1: statisch, Match + Coalesce)
  Services/TrackingCoverage.cs                      (C, Task 2: TrackedSince als reine Funktion)
  Services/IUsageStatQueryService.cs                (M, Task 2: Stichtag; Task 4: zwei Methoden + zwei DTOs)
  ChatLogArchive/IChatLogArchiveClient.cs           (C, Task 3)
  ChatLogArchive/ChatLogArchiveModels.cs            (C, Task 3: ChatLogMessage, ChatLogDayResult, ChatLogDayStatus)
src/EmotePurge.Infrastructure/
  Services/SevenTvSyncService.cs                    (M, Task 1: Koaleszenz über die geteilte Funktion)
  Services/EmoteSetStatusService.cs                 (M, Task 2)
  Services/UsageStatQueryService.cs                 (M, Task 2 + Task 4)
  ChatLogArchive/ChatLogArchiveClient.cs            (C, Task 3)
  ChatLogArchive/JustlogRawLineParser.cs            (C, Task 3: IRC-Zeile zu ChatLogMessage)
  ChatLogArchive/ChatLogArchiveOptions.cs           (C, Task 3: Konfigurationswerte)
  ServiceCollectionExtensions.cs                    (M, Task 3: AddHttpClient ohne Telemetrie-Handler)
src/EmotePurge.Worker/
  TwitchChatManager.cs                              (M, Task 1: Token-Schleife durch Aufruf ersetzt)
  IBotChatterDetector.cs, BotChatterDetector.cs     (M, Task 6: KnownBotAccountIds)
  Program.cs                                        (M, Task 6: Argument-Zweig vor dem Host-Aufbau)
  WorkerServiceRegistration.cs                      (C, Task 6: Registrierungen als testbare Funktionen)
  Harness/HarnessCommandLine.cs                     (C, Task 6: reine Argumentprüfung)
  Harness/HarnessOptions.cs                         (C, Task 6: Harness:*-Konfiguration)
  Harness/HarnessRunner.cs                          (C, Task 6: Ablauf, Vorbedingungen, Resume)
  Harness/HarnessReportFile.cs                      (C, Task 6: JSONL lesen/schreiben, atomarer Abschluss)
  Harness/HarnessInputHash.cs                       (C, Task 6: kanonischer Input-Hash)
  Harness/ReplayDayCounter.cs                       (C, Task 5: zählt einen Tag)
  Harness/ReplayFidelityCalculator.cs               (C, Task 5: Endbericht aus Tageszeilen)
  Harness/ReplayModels.cs                           (C, Task 5: HarnessHeader, DayLine, FinalReport, Gründe)
tests/EmotePurge.Infrastructure.Tests/
  Unit/EmoteNameMatchingTests.cs                    (C, Task 1)
  Unit/TrackingCoverageTests.cs                     (C, Task 2)
  Unit/JustlogRawLineParserTests.cs                 (C, Task 3)
  Unit/ChatLogArchiveClientTests.cs                 (C, Task 3, mit eigenem Streaming-Fake-Handler)
  Unit/TestData/chatlog-raw-day.txt                 (C, Task 3, synthetisch)
  Integration/EmoteSetStatusServiceTests.cs         (M, Task 2: unverändert grün; Task 4: Stichtag-Fälle)
  Integration/UsageStatQueryServiceTests.cs         (M, Task 2 + Task 4)
tests/EmotePurge.Worker.Tests/
  ReplayDayCounterTests.cs, ReplayFidelityCalculatorTests.cs   (C, Task 5)
  HarnessCommandLineTests.cs, WorkerServiceRegistrationTests.cs,
  HarnessReportFileTests.cs, HarnessInputHashTests.cs           (C, Task 6)
  BotChatterDetectorTests.cs                        (M, Task 6)
docker-compose.yml, docker-compose.prod.yml, .env.example        (M, Task 7)
docs/DECISIONS.md (M, Task 1 und Task 7), CLAUDE.md:138 (M, Task 7), docs/Architectur.md 6a/6b (M, Task 7)
docs/designs/Chat-Log-Backfill-69-2026-09-05.md    (Task 0: Nachtrag committen)
```

## Entscheidungen dieses Plans (wo das Design Spielraum ließ)

1. **Die Matching-Klasse liegt in `Core` (`EmotePurge.Core.Matching.EmoteNameMatching`, statisch).**
   Das Design lässt `Core` oder `Infrastructure` offen. `Infrastructure` braucht die Koaleszenz
   (`SevenTvSyncService`), der Worker das Matching; `Core` ist der gemeinsame Nenner ohne EF und
   ohne TwitchLib, und `CoreAssemblyReferenceTests` erzwingt die Reinheit. **Folge für den Test:**
   das Design nennt `tests/EmotePurge.Worker.Tests`; nach Regel 11 und der Projektbeschreibung
   dieses Testprojekts („pure decision and state classes of the worker's transports") gehört ein
   `Core`-Typ nach `Infrastructure.Tests/Unit`, neben `EmoteMatchCacheTests`. Der Worker-seitige
   Konsument (Task 5) testet weiter in `Worker.Tests`.
2. **Der Log-Client liegt in `Infrastructure/ChatLogArchive/`, sein Interface in
   `Core/ChatLogArchive/`.** Den Ordner `Infrastructure/Http/` aus dem Design gibt es nicht; die
   bestehenden Clients sind je Anbieter abgelegt, und das ist die Konvention, der der neue folgt.
3. **Kein `ProviderTelemetry`-Handler am Log-Client.** Ein dritter Eintrag in `RateLimitProviders`
   erschiene auf der Admin-Monitoring-Seite und zöge eine Frontend-Änderung nach sich, die dieser
   Plan ausdrücklich nicht hat. Anfragen, Bytes und 429 zählt der Harness selbst in seinen Bericht.
4. **Beide Query-Methoden aus T4 landen an `IUsageStatQueryService`.** `IEmoteService` ist ein
   Kommando-Service (nur `MarkDeleted`/`MarkRestored`); die Emote-Lebenszeiten werden nur gebraucht,
   um Nutzungszeilen zu deuten, und stehen so neben den Zeilen, die sie deuten. Der Stichtag aus
   T2 kommt ebenfalls dorthin. Alle drei nehmen die interne `Channel.Id`, nicht den Namen: beide
   Aufrufer halten die Kanalzeile bereits und dürfen sie nicht ein zweites Mal laden.
5. **Der Parser der IRC-Zeilen gehört zum Client, nicht zum Harness.** Der Client liefert fertige
   `ChatLogMessage`-Records (Zeitstempel, `user-id`, Badges als
   `IReadOnlyList<KeyValuePair<string, string>>`, `room-id`, `source-room-id`, Text mit entpacktem
   ACTION); das Wire-Format bleibt in `Infrastructure`, und die Badge-Form ist exakt die, die
   `IBotChatterDetector.IsBot` heute nimmt, also keine Projektion im Harness.
6. **Der Client liefert je Tag ein Ergebnis-Record und ruft je Nachricht einen Callback**, statt
   ein `IAsyncEnumerable` zurückzugeben: Status (ok, kein Log-Tag, 429, Body-Timeout, Byte-Decke,
   Transportfehler, Parser-Fehler), Bytes, SHA-256 des Bodys und Zählstände der verworfenen Zeilen
   müssen nach dem Stream zurückkommen, und ein `IAsyncEnumerable` hat dafür keinen Kanal.
7. **Argument-Grammatik:** `harness <kanal>` und optional `--days <n>` (ganze Zahl 1 bis 90, Nutzerentscheidung D3 vom 2026-09-05: Deckel 90 statt 30),
   sonst nichts. Ohne Argumente startet der Worker; jede andere Form (auch `harness` ohne Kanal,
   ein zweiter Kanal, ein unbekannter Schalter, `--days 0`) endet mit Exit-Code 2 und einer
   deutschen Zeile auf stderr, **vor** jedem `Host.CreateApplicationBuilder`. `--days` existiert,
   weil der lokale Lauf gegen `brudivoeller_tv` ein kleines Fenster braucht (Design: Fenster ist
   Parameter). Alles andere (Ausgabeverzeichnis, Byte-Decke, Abstand, Body-Timeout, Basis-URL)
   kommt aus der Konfiguration (`Harness:*`, `ChatLogArchive:*`), nie aus der Kommandozeile.
   Der Harness-Zweig gibt dem Builder ein leeres Argument-Array, damit `--days` nicht als
   Konfigurationsschlüssel durchsickert.
8. **Lauf-Identität und Dateiname sind dasselbe.** Der Berichtskopf trägt ein Objekt `identity`
   (Kanal-Id, Twitch-Id, Kanalname, Fenster von/bis, Bot-Split-Stichtag, sortierte Bot-ID-Menge,
   Algorithmus-Version, Input-Hash) und daneben `loadedAtUtc`, das in die Identität **nicht**
   eingeht. Der Dateiname ist `<kanalname>-<von>-<bis>-<erste 12 Hex des Identitäts-Hashs>.jsonl`.
   Damit fällt „gleicher Kopf ⇒ Resume, anderer Kopf ⇒ neue Datei" aus dem Dateisystem heraus,
   und ein zweiter Lauf mit veränderter Bot-ID-Menge oder verschobenem Fenster kann die alte Datei
   gar nicht erst öffnen. Beim Öffnen wird die Identität trotzdem verglichen (Schutz gegen eine
   umbenannte Datei).
9. **Der Abschluss ist kein eigenes Kommando.** Ein erneuter Lauf mit gleicher Identität findet
   alle Tageszeilen vor, ruft nichts ab und schreibt den Endbericht neu; das ist der
   Reproduktionspfad („Abschluss zweimal ⇒ identisch"). Der Endbericht liegt neben der JSONL-Datei
   als `<gleicher Stamm>.report.json` (Maschine) und `.report.md` (lesbare Tabelle), beide per
   Temp-Datei plus Rename atomar geschrieben.
10. **`IPendingMigrationGuard` läuft auch im Harness-Zweig.** Er liest nur, und ein Harness gegen
    ein veraltetes Schema würde sonst erst beim ersten Query scheitern.
11. **Zwei DECISIONS-Einträge statt einem** (s. Global Constraints, Regel 3): die Matching-Regel
    ist ein Vertrag, der mit Task 1 in Kraft tritt und dessen Commit auch ohne den Rest des Plans
    Bestand hätte; Topologie und Einstiegspunkt entstehen erst in Task 6/7. Ein Eintrag im
    Task-7-Commit für alles hätte Task 1 gegen Regel 3 verstoßen lassen.
12. **`--days` bestimmt auch die Vorbedingung.** Das Design verlangt „≥ 30 Tage `UsageStat` ab
    Fensterstart"; mit einem kleineren Fenster gilt entsprechend „≥ `days` Tage". Der bindende
    Lauf nutzt den Default 30, und der Bericht nennt die Fensterlänge, damit ein 3-Tage-Lauf nie
    als Gate-Lauf gelesen werden kann (Feld `gateEligible` ist dann `false` mit Grund).
13. **Distinkte Chatter über das Fenster** werden nur in einem ununterbrochenen Prozess gezählt
    (transiente Menge, nie geschrieben). Nach einem Resume steht im Bericht „nicht verfügbar
    (wiederaufgenommen)"; die k-Verteilung je `(Emote, Tag)` ist davon unberührt, weil sie je Tag
    abgeschlossen in die Tageszeile geht.

---

### Task 0: Codex-Nachtrag committen

**Files:** `docs/designs/Chat-Log-Backfill-69-2026-09-05.md` (bereits geändert im Working Tree)

- [x] **Step 1:** `git diff docs/designs/Chat-Log-Backfill-69-2026-09-05.md` lesen; erwartet sind
  nur die Abschnitte „Codex-Adversarial 2026-09-05: Ergebnisse", „Präregistrierung",
  „T8-Live-Proben" und die angepassten T3/T5/T6/T7/T8/T9-Zeilen.
- [x] **Step 2:** Nutzer fragen; Commit
  `docs: record the codex adversarial review for the backfill harness (#69)`. Danach die
  Integrations- und Lane-Branches anlegen.

---

### Task 1: Eine Matching-Regel für Live-Pfad, Match-Cache und Harness (T1, Lane A)

**Files:**
- Create: `src/EmotePurge.Core/Matching/EmoteNameMatching.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/EmoteNameMatchingTests.cs`
- Modify: `src/EmotePurge.Worker/TwitchChatManager.cs:517-524`,
  `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:340-352`, `docs/DECISIONS.md`

**Vorab lesen:** Design „Constraints: Zählung und Zuordnung" (Dedup, doppelte Namen) und
Approach Schritt 1; `TwitchChatManager.OnMessageReceived` vollständig (Hot-Path-Kommentare, die
Watchdog-Reihenfolge, die genau eine `IsBot`-Klassifikation); `RefreshMatchCacheAsync` samt
Tracker-Logik; `ChannelName.cs` als Vorbild für eine statische Regel in `Core`;
`docs/DECISIONS.md` Kopf und die drei jüngsten Einträge.

**Interfaces (verbindlich, Konsumenten: Task 5 und Task 6):**
- `static class EmoteNameMatching` in `EmotePurge.Core.Matching`.
- `static IReadOnlySet<string> MatchEmoteIds(string message, IReadOnlyDictionary<string, string> nameToId)`:
  liefert die Menge der getroffenen Emote-Ids **je Nachricht dedupliziert**; bei leerem Text oder
  leerer Map eine geteilte leere Instanz (keine Allokation).
- `static EmoteNameMap Coalesce(IEnumerable<KeyValuePair<string, string>> emotesInLoadOrder)`
  mit `readonly record struct EmoteNameMap(Dictionary<string, string> NameToId, IReadOnlySet<string> AmbiguousNames)`:
  erster Eintrag je Name gewinnt, jeder weitere Name landet in `AmbiguousNames`. Das Dictionary
  wird mit dem Default-Comparer gebaut (ordinal), wie heute.

**Absicht und Verträge:**
- Die Regel ist **Split an einzelnen Leerzeichen** (`Split(' ')`, keine `RemoveEmptyEntries`, kein
  Trim, kein Unicode-Whitespace), **ordinaler Lookup** des Tokens, **Dedup je Nachricht**. Das
  ist keine Verbesserungsgelegenheit: der Harness misst, ob ein Import die Live-Zählung
  reproduziert, und jede „Korrektur" hier verfälscht beide Seiten gleichzeitig. Wer die Regel je
  ändert, ändert sie für alle drei Aufrufer, und der DECISIONS-Eintrag sagt das.
- Der Hot Path in `OnMessageReceived` darf nicht schlechter werden als heute: heute entstehen je
  Nachricht ein `string[]` aus `Split` und ein `HashSet<string>`; die neue Funktion darf genau das
  allokieren und nicht mehr (kein LINQ, keine Closure, kein Enumerator-Boxing). Die Reihenfolge
  Watchdog → Log → frühes Return → eine Klassifikation → Schleife bleibt unverändert; der Aufruf
  ersetzt nur die Schleife `:518-524`, und `usageCounter.Increment(emoteId, isBot)` läuft dann über
  die zurückgegebene Menge.
- `RefreshMatchCacheAsync` baut die Map über `Coalesce` aus der Ladereihenfolge der Query
  (`:335-338` unverändert) und reicht `AmbiguousNames` wie heute an `duplicateNameTracker.Update`.
  Log-Texte und Cache-Aufruf bleiben wörtlich.
- **DECISIONS-Eintrag** (deutsch, Datum des Commits, Titel sinngemäß „Eine Matching-Regel für
  Live-Pfad, Match-Cache und Harness"): warum geteilt statt kopiert (Dedup je Nachricht ist eine
  Metrik-Definition, nicht ein Implementierungsdetail; ein Harness mit anderer Zählung vergliche
  zwei Metriken); warum `Core`; dass die Regel absichtlich naiv ist und nur für alle drei Aufrufer
  zugleich geändert wird; dass der Winner der Koaleszenz die Ladereihenfolge ist und damit für den
  Live-Cache die unspezifizierte Reihenfolge einer Query ohne `OrderBy`, was der Harness
  ausweist statt zu reparieren. `**Betrifft:**` nennt die vier Dateien dieses Tasks.

- [ ] **Step 1 (Tests zuerst, `EmoteNameMatchingTests`, container-frei):** `MatchEmoteIds`: leerer
  Text ⇒ leer; nur Whitespace ⇒ leer; Emote am Anfang, in der Mitte, am Ende; dasselbe Emote
  dreimal ⇒ genau eine Id; zwei Namen auf dieselbe Id ⇒ eine Id; Doppel-Leerzeichen zwischen
  Tokens (leeres Token trifft nichts); Tab oder geschütztes Leerzeichen trennt **nicht** (Token
  bleibt ungetrennt, wie live); Groß-/Kleinschreibung unterscheidet (ordinal); Unicode-Name mit
  Emoji trifft; ACTION-Innentext (der Aufrufer hat entpackt, die Funktion sieht nur Text); leere
  Map ⇒ leer ohne Allokation (Referenzgleichheit der leeren Instanz). `Coalesce`: eindeutige
  Namen ⇒ keine Mehrdeutigkeit; doppelter Name ⇒ erster gewinnt, Name in `AmbiguousNames`;
  dreifacher Name ⇒ einmal in der Menge; leere Eingabe ⇒ leere Map.
- [ ] **Step 2: rot laufen lassen.** Filter `EmoteNameMatchingTests`; Expected: Compilerfehler.
- [ ] **Step 3: implementieren**, beide Aufrufer umstellen, `CoreAssemblyReferenceTests` im Blick.
- [ ] **Step 4: grün laufen lassen.** Filter `EmoteNameMatchingTests`, `EmoteMatchCacheTests`,
  `CoreAssemblyReferenceTests`; dann `dotnet build EmotePurge.slnx` und
  `dotnet test EmotePurge.slnx` (die `SevenTvSyncServiceTests` decken die Koaleszenz über den
  Tracker mit).
- [ ] **Step 5: lokale Vorher/Nachher-Probe (Regel 16, Ergebnis in den PR, Eingabe nicht ins
  Repo).** Ressourcen-Regel beachten (`docker compose stop worker` vorher, `up -d worker`
  nachher). Den Worker vor der Änderung (Stand `main`) und nach der Änderung je einige Minuten mit
  `Logging__LogLevel__EmotePurge.Worker.TwitchChatManager=Debug` gegen dieselben gejointen
  Kanäle laufen lassen und die Debug-Zeilen in eine Datei **außerhalb des Repos** schreiben
  (Scratchpad). Anschließend beide Mitschnitte mit einem kleinen Wegwerf-Skript durch
  `MatchEmoteIds` gegen die aktuelle Map treiben (die Map per `docker compose exec postgres psql`
  aus `Emotes` ziehen) und die Trefferzahlen je Nachricht vergleichen: Erwartung null Differenz.
  Da die Live-Nachrichten in beiden Läufen unterschiedlich sind, ist der eigentliche Beleg der
  **Replay** desselben Mitschnitts durch alten und neuen Code, nicht der Zählerstand zweier
  Zeitfenster; der PR nennt Kanäle, Minuten, Nachrichtenzahl und „0 Differenzen".
- [ ] **Step 6:** DECISIONS-Eintrag schreiben, `dotnet format`, Nutzer fragen, Commit
  `refactor(matching): share the emote name matching rule between chat and match cache`.

**Fertig-Bedingung:** Backend-Suite grün; `git diff` von `TwitchChatManager.cs` zeigt nur die
ersetzte Schleife; `RefreshMatchCacheAsync` produziert dieselben Log-Zeilen; Probe ohne Differenz.

**Ausdrücklich nicht:** keine Änderung an `IEmoteMatchCache`, kein Interface für die statische
Klasse, kein Trimmen, kein Case-Folding, keine Twitch-Emote-Tags.

**Modell: sonnet.** Klar umrissene Extraktion mit vollständig aufgezählten Randfällen; die
Probe in Step 5 braucht Sorgfalt, keine Architekturentscheidung.

---

### Task 2: Fensterstart und Bot-Split-Stichtag werden geteilt (T2, Lane C)

**Files:**
- Create: `src/EmotePurge.Core/Services/TrackingCoverage.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/TrackingCoverageTests.cs`
- Modify: `src/EmotePurge.Core/Services/IUsageStatQueryService.cs`,
  `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs`,
  `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs:41-56`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs`

**Vorab lesen:** Design „Schichten und Auslöser" (Eng-Review 6A) und „Was `UsageStat` wirklich
enthält" (Stichtag ist kein Kalenderdatum); `EmoteSetStatusService.cs` vollständig mit dem
Gate-Kommentar; `EmoteSetStatusServiceTests` (die Fälle zu `BotsExcludedSince` und zum Sprung bei
leerem Set sind die Regressionsgarantie); `UsageStatQueryServiceTests` (Seed-Muster).

**Interfaces (verbindlich, Konsumenten: Task 6):**
- `static class TrackingCoverage` in `EmotePurge.Core.Services` mit
  `static DateTime TrackedSince(DateTime? trackingResumedAt, DateTime createdAt)`.
- `IUsageStatQueryService.GetEarliestBotUsageDateAsync(string channelId, CancellationToken ct) → Task<DateOnly?>`:
  frühestes `Date` mit `BotUseCount > 0` über alle Emotes des Kanals **inklusive archivierter**,
  `null` wenn nie ein Bot gesehen wurde. Der `<summary>` übernimmt die Sichtungs-Semantik aus dem
  `<param>` von `EmoteSetStatusDto.BotsExcludedSince` (erste Sichtung, nicht Deploy-Tag) per
  Verweis, nicht als Kopie.

**Absicht und Verträge:**
- **Verhaltensneutral.** `EmoteSetStatusService.GetAsync` ruft beide Funktionen und liefert
  byte-gleich dasselbe DTO wie heute. Das Gate `ActiveEmoteSetId.Length == 0` bleibt **im
  Status-Service** (es schützt die Poll-Schleife der Usage-Seite), nicht in der Query-Methode:
  der Harness will den Stichtag auch für einen Kanal ohne aktives Set wissen.
- Regel 10: die neue Methode übernimmt den Zuschnitt aus `EmoteSetStatusService.cs:41-49` (erst
  ID-Liste, dann `MinAsync` auf `DateOnly?`) unverändert; der Kommentar dazu wandert mit.
- `TrackedSince` ist die eine Zeile `trackingResumedAt ?? createdAt`; ihr Wert liegt darin, dass es
  sie genau einmal gibt und der Harness sie nicht nachbaut. Die `<summary>` nennt beide Aufrufer.

- [ ] **Step 1 (Tests zuerst):** `TrackingCoverageTests`: `null` ⇒ `createdAt`; gesetzt ⇒
  `trackingResumedAt`, auch wenn er vor `createdAt` läge (die Funktion urteilt nicht).
  `UsageStatQueryServiceTests`: Stichtag = frühester Bot-Tag, nicht früheste Zeile; nur
  `BotUseCount = 0` ⇒ `null`; Bot-Zeile eines archivierten Emotes zählt; fremder Kanal zählt nicht.
- [ ] **Step 2: rot laufen lassen.** Filter `TrackingCoverageTests|UsageStatQueryServiceTests`;
  Expected: Compilerfehler.
- [ ] **Step 3: implementieren und `EmoteSetStatusService` umstellen** (Query-Service wird dort
  injiziert; Konstruktor bekommt `IUsageStatQueryService` dazu).
- [ ] **Step 4: grün laufen lassen.** Zusätzlich Filter `EmoteSetStatusServiceTests`: **unverändert
  grün ohne Anpassung einer Assertion** (das ist der Beleg der Verhaltensneutralität), dann
  `dotnet test EmotePurge.slnx` (die Api-Tests fahren `Program.cs` hoch und lösen den
  Status-Service auf).
- [ ] **Step 5:** `dotnet format`, Nutzer fragen, Commit
  `refactor(usage): extract the tracking window and bot cutover rules`.

**Fertig-Bedingung:** Backend-Suite grün; `EmoteSetStatusServiceTests.cs` ohne Diff;
`EmoteSetStatusService.cs` enthält weder `?? channel.CreatedAt` noch `MinAsync` mehr.

**Ausdrücklich nicht:** kein DECISIONS-Eintrag (Refactor); keine Änderung am DTO oder am Endpunkt.

**Modell: sonnet.**

---

### Task 3: Der Log-Archiv-Client liest einen Tag zeilenweise (T3, Lane B)

**Files:**
- Create: `src/EmotePurge.Core/ChatLogArchive/IChatLogArchiveClient.cs`,
  `src/EmotePurge.Core/ChatLogArchive/ChatLogArchiveModels.cs`,
  `src/EmotePurge.Infrastructure/ChatLogArchive/ChatLogArchiveClient.cs`,
  `src/EmotePurge.Infrastructure/ChatLogArchive/JustlogRawLineParser.cs`,
  `src/EmotePurge.Infrastructure/ChatLogArchive/ChatLogArchiveOptions.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/JustlogRawLineParserTests.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/ChatLogArchiveClientTests.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/TestData/chatlog-raw-day.txt`
- Modify: `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (nach dem Helix-Block)

**Vorab lesen:** Design Approach Schritt 2, „Log-Dienst, gemessen 2026-09-05", T8-Live-Proben
(`?raw`-Format, 404-Semantik, kein `Content-Length`, keine Rate-Limit-Header, `type`-Werte);
Failure Modes „Log-Client Body/429/Format"; `TwitchHelixClient.cs` und `SevenTvApiClient.cs`
(Fehlerbehandlung, deutsche Log-Meldungen, `ReadFromJsonAsync`-Catch-Muster);
`TwitchHelixClientTests.cs` (Stub-Handler, `CreateClient`); Kommentar zu `IBotChatterDetector.IsBot`
(Badge-Form); Memory „Fremd-API-Annahmen live prüfen".

**Interfaces (verbindlich, Konsumenten: Task 6):**
- `interface IChatLogArchiveClient` mit genau einer Methode
  `Task<ChatLogDayResult> ReadDayAsync(string twitchChannelId, DateOnly day, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)`.
- `record ChatLogMessage(DateTime SentAtUtc, string? UserId, IReadOnlyList<KeyValuePair<string, string>> Badges, string? RoomId, string? SourceRoomId, string Text)`.
  `Text` ist der Nachrichtentext nach ACTION-Entpackung, so wie TwitchLib ihn dem Live-Pfad gibt.
- `enum ChatLogDayStatus { Complete, NoLogDay, RateLimited, BodyTimeout, ByteCapExceeded, TransportFailure, MalformedResponse }`.
- `record ChatLogDayResult(ChatLogDayStatus Status, long BytesReceived, string? BodySha256Hex, int MessageCount, int NonPrivmsgLines, int MalformedLines, int? HttpStatusCode)`;
  `BodySha256Hex` nur bei `Complete`.
- `static class JustlogRawLineParser` mit `static bool TryParse(string line, out ChatLogMessage message, out string? ircCommand)`:
  `false` für alles, was keine vollständige PRIVMSG-Zeile ist; `ircCommand` nennt den
  Befehl (für die Zählung `NonPrivmsgLines` gegenüber `MalformedLines`).
- `ChatLogArchiveOptions` mit `BaseUrl` (Default `https://logs.zonian.dev/`), `RequestDelay`
  (Default 1500 ms), `BodyTimeout` (Default 120 s), gebunden an `ChatLogArchive:*`.

**Absicht und Verträge:**
- **Eine Anfrage je Kanal-Tag**: `GET channelid/{twitchChannelId}/{yyyy}/{M}/{d}?raw`. Kein `HEAD`,
  kein Retry, kein `RateLimiter`. Vor jeder Anfrage außer der ersten wartet der Client per
  `Task.Delay` mit dem Abbruch-Token, bis seit dem Beginn der letzten Anfrage `RequestDelay`
  vergangen ist; der Client ist damit strikt sequenziell, und das steht im Klassenkommentar als
  Vertrag (ein zweiter gleichzeitiger Aufrufer wäre ein Fehler des Aufrufers).
- **Streaming**: `SendAsync` mit `HttpCompletionOption.ResponseHeadersRead`; der Body wird über
  `StreamReader.ReadLineAsync` gelesen, nie als Ganzes gepuffert. Bytes werden gezählt und in
  einen inkrementellen SHA-256 gegeben, **bevor** die Zeile geparst wird (der Digest belegt den
  empfangenen Body, nicht das Parser-Ergebnis).
- **Body-Timeout** (Eng-Review 2A): `HttpClient.Timeout` deckt bei `ResponseHeadersRead` nur die
  Header. Je Abruf ein `CancellationTokenSource` mit `BodyTimeout`, verkettet mit dem
  Aufrufer-Token; ein Timeout ist `BodyTimeout`, ein Aufrufer-Abbruch propagiert als
  `OperationCanceledException` (der Harness unterscheidet beides, deshalb muss der Client sie
  unterscheiden können: `ct.IsCancellationRequested` entscheidet).
- **Statusabbildung**: 404 ⇒ `NoLogDay` (Normalzustand laut T8, kein Log); 429 ⇒ `RateLimited`
  ohne Body-Lesen; andere Nicht-2xx ⇒ `TransportFailure` mit Statuscode; `HttpRequestException`,
  `IOException` mitten im Body ⇒ `TransportFailure`; `maxBytes` überschritten ⇒
  `ByteCapExceeded`, der Stream wird verworfen (Response `Dispose`), keine Fortsetzung; ein
  Body, dessen Zeilen zu mehr als 50 % nicht als IRC-Zeilen lesbar sind (Zähler nach dem Stream),
  ⇒ `MalformedResponse` (Failure Mode „Wurzelform anders als angenommen").
- **Parser**: IRCv3-Zeile `@tags :prefix COMMAND #channel :trailing`. Tags werden am `;` getrennt,
  Werte nach IRCv3 entschlüsselt (`\s`, `\:`, `\\`, `\r`, `\n`); `badges` wird an `,` und `/` zu
  Paaren (Set-Id, Version) zerlegt, leere `badges` ⇒ leere Liste; `tmi-sent-ts` (Epoch-ms) ⇒
  `SentAtUtc`; `user-id`, `room-id`, `source-room-id` als Strings; der Trailing-Text wird **nicht**
  entschlüsselt (er ist kein Tag). `ACTION …` wird entpackt wie in TwitchLib (Präfix
  und Suffix entfernt). Alles außer `PRIVMSG` ⇒ `false` mit gesetztem `ircCommand`; fehlendes
  `PRIVMSG`, fehlender Trailing-Teil, unparsbarer Zeitstempel ⇒ `false` ohne Befehl (malformed).
- **Rückfall** aus dem Design: liefern die Zeilen weder `badges` noch `user-id`, ist das kein
  Client-Fehler (beides ist nullable); die Entscheidung „nicht entscheidungsfähig" trifft der
  Harness (Task 6), der Client zählt nur.
- Log-Meldungen deutsch, eine je Statuswechsel (kein Log je Zeile). Registrierung per
  `AddHttpClient<IChatLogArchiveClient, ChatLogArchiveClient>` mit `BaseAddress` aus den Options,
  `Timeout` 30 s (nur Header), `User-Agent` wie die anderen Clients, **ohne** Telemetrie-Handler
  (Plan-Entscheidung 3, als Kommentar an der Registrierung).

- [ ] **Step 1 (Tests zuerst).** `JustlogRawLineParserTests` (synthetische Zeilen, erfundene IDs):
  gewöhnliche PRIVMSG mit drei Badges; ohne Badges; mit `bot-badge`; ACTION entpackt; Text mit
  Doppelpunkt und Leerzeichen bleibt vollständig; Tag-Wert mit `\s` entschlüsselt (z. B.
  `display-name`); `source-room-id` ungleich `room-id` gesetzt; CLEARCHAT ⇒ `false`, Befehl
  gesetzt; USERNOTICE mit Trailing-Text ⇒ `false` (Live zählt keine USERNOTICE); Zeile ohne
  Trailing ⇒ malformed; leere Zeile ⇒ malformed; Zeitstempel als `DateTimeKind.Utc`.
  `ChatLogArchiveClientTests` mit einem **eigenen** Stub-Handler, der einen `Stream` statt eines
  Strings liefert (der vorhandene `StubHandler` in `TwitchHelixClientTests` ist privat und puffert;
  ein geteilter Fake nach `Fakes/` ist erlaubt, wenn der Task ihn dorthin verschiebt, aber nicht
  Pflicht): 200 mit `TestData/chatlog-raw-day.txt` ⇒ `Complete`, `MessageCount` gleich der
  PRIVMSG-Zeilen, `NonPrivmsgLines` gleich der übrigen, Digest gleich dem SHA-256 der Datei,
  Callback je Nachricht in Dateireihenfolge; 404 ⇒ `NoLogDay`, Callback nie; 429 ⇒ `RateLimited`,
  Callback nie, `HttpStatusCode` 429; Stream, der nach 1 KB **stehen bleibt** (ein Stream, dessen
  `ReadAsync` nach n Bytes auf ein nie gesetztes Signal wartet) ⇒ `BodyTimeout` innerhalb eines
  kurzen konfigurierten `BodyTimeout` (Sekundenbereich), kein Hänger; `maxBytes` kleiner als die
  Datei ⇒ `ByteCapExceeded`, `BytesReceived` ≤ `maxBytes` plus eine Zeile; Aufrufer-Token
  abgebrochen mitten im Body ⇒ `OperationCanceledException`, nicht `BodyTimeout`; zwei Aufrufe
  hintereinander ⇒ zweiter startet frühestens `RequestDelay` nach dem ersten (mit kleinem Delay
  im Test messbar, oder über einen injizierten `TimeProvider`, wenn der Implementer die Uhr
  abstrahiert: `RateLimitTelemetryStore` nutzt `TimeProvider` bereits als Muster); Body aus
  lauter unlesbaren Zeilen ⇒ `MalformedResponse`. Die Testdatei ist synthetisch: sechs bis zehn
  Zeilen, erfundene Logins wie `alice_test`, IDs wie `100000001`, ein ACTION, ein CLEARCHAT, ein
  USERNOTICE, ein Shared-Chat-Fall.
- [ ] **Step 2: rot laufen lassen.** Filter `JustlogRawLineParserTests|ChatLogArchiveClientTests`;
  Expected: Compilerfehler.
- [ ] **Step 3: implementieren** (Options, Parser, Client, Registrierung).
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter; dann `CoreAssemblyReferenceTests` (die
  Records in `Core` dürfen nichts referenzieren) und `dotnet build EmotePurge.slnx`.
- [ ] **Step 5: Live-Probe ein Tag (Regel 16).** Ein Wegwerf-Aufruf (z. B. ein `dotnet script`
  oder ein temporärer xUnit-Fall mit `[Fact(Skip)]`, der **nicht** committet wird) gegen
  `channelid/489111423/<gestriges Datum>?raw` mit `maxBytes` 50 MB: erwartet `Complete` mit
  Bytes im einstelligen MB-Bereich oder `NoLogDay`, Zeilenzähler plausibel, kein Hänger. Ein
  zweiter Aufruf direkt danach zeigt den Abstand im Log. Ergebnis (Status, Bytes, Nachrichten,
  Dauer) in die Task-Rückmeldung. Mehr als zwei Anfragen macht diese Probe nicht.
- [ ] **Step 6:** `dotnet format`, Nutzer fragen, Commit
  `feat(infra): add the streaming chat log archive client`.

**Fertig-Bedingung:** beide Testklassen grün; Live-Probe mit genannten Zahlen; kein neuer
`RateLimitProviders`-Eintrag; `git diff web/` leer.

**Ausdrücklich nicht:** kein `?json`-Pfad, kein `HEAD`, kein Retry, kein `RateLimiter`, keine
Persistenz, kein zweiter Anbieter, keine Parallelität.

**Modell: sonnet.** Vollständig spezifizierte Zustände; die eine Unsicherheit (Wire-Format)
ist durch T8 geklärt und durch die Live-Probe abgesichert.

---

### Task 4: Zwei Query-Methoden liefern Lebenszeiten und Fensterzeilen (T4, Lane C)

**Files:**
- Modify: `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` (zwei Methoden, zwei DTOs),
  `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs`

**Vorab lesen:** Design „Schichten und Auslöser" (DB-Lesezugriff über bestehende Interfaces,
einmal je Lauf geladen) und „Constraints: Zählung und Zuordnung" (welche Felder der Harness
braucht); `UsageStatQueryService.GetTotalsByEmoteIdsAsync` (`:227-245`) als Zuschnitt-Vorbild;
`Emote.cs` Kommentare zu `ArchivedAt`/`FirstSeenAt`.

**Interfaces (verbindlich, Konsumenten: Task 5 und Task 6):**
- `record EmoteLifetimeDto(string Id, string Name, bool IsArchived, DateTime? FirstSeenAt, DateTime? ArchivedAt, DateTime LastSyncedAt)`.
- `record UsageStatRowDto(string EmoteId, DateOnly Date, int UseCount, int BotUseCount)`.
- `IUsageStatQueryService.GetEmoteLifetimesAsync(string channelId, CancellationToken ct) → Task<IReadOnlyList<EmoteLifetimeDto>>`:
  **alle** Emotes des Kanals, archivierte eingeschlossen, sortiert nach `Id` (ordinal), damit
  der Input-Hash aus Task 6 deterministisch ist.
- `IUsageStatQueryService.GetRowsAsync(IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken ct) → Task<IReadOnlyList<UsageStatRowDto>>`:
  Zeilen im inklusiven Bereich, **ohne** `UseCount > 0`-Filter (der Harness braucht Bot-only-Zeilen
  für die Gesamtsumme), sortiert nach `(EmoteId, Date)`.

**Absicht und Verträge:**
- Regel 10: `GetRowsAsync` ist ein `Where` über `UsageStats` mit `Contains` auf die ID-Liste und
  dem Datumsbereich, kein Join, kein `GroupBy`; `GetEmoteLifetimesAsync` ist eine Projektion über
  `Emotes` mit `ChannelId`-Filter. Beide `AsNoTracking`.
- Die `<summary>`-Kommentare sagen, wofür die Methoden da sind (Replay-Harness, Feature B) und
  warum sie **nicht** filtern, was die anderen Methoden filtern (archivierte Emotes,
  Bot-only-Zeilen): der Harness deutet selbst, der Leser darf nicht denken, jemand habe die
  Filter vergessen.
- `from > to` wirft `ArgumentException` wie `GetUsageContextAsync`.

- [ ] **Step 1 (Tests zuerst, `UsageStatQueryServiceTests`):** Lebenszeiten: aktives und
  archiviertes Emote beide enthalten, Felder durchgereicht, `FirstSeenAt` null bleibt null,
  fremder Kanal fehlt, Reihenfolge nach `Id`; Zeilen: Bereich inklusiv an beiden Enden, Zeile
  außerhalb fehlt, Bot-only-Zeile (`UseCount 0, BotUseCount 3`) **enthalten**, fremde Emote-Id
  fehlt, leere ID-Liste ⇒ leer ohne Query-Fehler, Reihenfolge `(EmoteId, Date)`.
- [ ] **Step 2: rot laufen lassen.** Filter `UsageStatQueryServiceTests`; Expected: Compilerfehler.
- [ ] **Step 3: implementieren.**
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter, dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 5:** `dotnet format`, Nutzer fragen, Commit
  `feat(usage): expose emote lifetimes and window rows for the replay harness`.

**Fertig-Bedingung:** Backend-Suite grün; beide Methoden ohne Navigations-Join; kein Konsument
im Produktivcode (kommt in Task 6).

**Modell: sonnet.**

---

### Task 5: Die Rechenlogik des Harness, pur (T5, Lane A, nach Task 1)

**Files:**
- Create: `src/EmotePurge.Worker/Harness/ReplayModels.cs`,
  `src/EmotePurge.Worker/Harness/ReplayDayCounter.cs`,
  `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs`,
  `tests/EmotePurge.Worker.Tests/ReplayDayCounterTests.cs`,
  `tests/EmotePurge.Worker.Tests/ReplayFidelityCalculatorTests.cs`

**Vorab lesen:** Design vollständig zu Metriken: „Constraints" (Grenzen, stabile Teilmenge,
Bot-Split, Live-Lücken), Approach Schritt 3 (Diagnostik-Liste), **„Codex-Adversarial:
Metrik-Population (D1)" und „Log-Abdeckung (D2)"**, „Präregistrierung" (die bindenden Zahlen),
Test-Coverage-Diagramm „Harness-Rechenlogik". Aus Task 1 `EmoteNameMatching`, aus Task 4 die
DTOs (Lane C ist zu diesem Zeitpunkt eventuell noch nicht gemergt: die zwei Records sind
einzeilig oben definiert; der Task legt sie **nicht** selbst an, sondern arbeitet gegen eigene
Eingabetypen in `ReplayModels.cs`, die Task 6 aus den DTOs befüllt).

**Interfaces (verbindlich, Konsumenten: Task 6):**
- `ReplayModels.cs`: `record ReplayEmote(string Id, string Name, bool IsArchived, DateTime? FirstSeenAt, DateTime? ArchivedAt, DateTime LastSyncedAt)`;
  `record ReplayUsageRow(string EmoteId, DateOnly Date, int UseCount, int BotUseCount)`;
  `record ReplayWindow(DateOnly From, DateOnly To, DateOnly? BotSplitCutover)`;
  `enum UnmatchedReason { UnknownName, AmbiguousName, BeforeFirstSeen, AfterArchived }`;
  `record ReplayDayLine(DateOnly Day, string Status, long Bytes, string? BodySha256Hex, int MessageCount, int BotMessageCount, int SharedChatMessageCount, int NonPrivmsgLines, int MalformedLines, int OutsideDayCount, IReadOnlyDictionary<string, int> HumanCounts, IReadOnlyDictionary<string, int> BotCounts, IReadOnlyDictionary<string, int> UnmatchedByReason, int FirstSeenUnknownHits, IReadOnlyList<int> KHistogram, int CellCount, int DistinctChatters)`;
  `record ReplayFinalReport(...)` mit den unten aufgezählten Kennzahlen als benannte Felder.
- `sealed class ReplayDayCounter` (eine Instanz je Tag): Konstruktor
  `ReplayDayCounter(DateOnly day, IReadOnlyList<ReplayEmote> emotes, Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> isBot)`;
  `void Count(ChatLogMessage message)` (Typ aus Task 3, Lane B; bis zum Merge über ein
  gleichnamiges lokales Record in `ReplayModels.cs`, das Task 6 entfernt, **oder** der Counter
  nimmt die vier Werte einzeln: `Count(DateTime sentAtUtc, string? userId, IReadOnlyList<KeyValuePair<string,string>> badges, string? roomId, string? sourceRoomId, string text)`; **Letzteres ist die Vorgabe**, damit Lane A nicht auf Lane B wartet);
  `ReplayDayLine Finish(string status, long bytes, string? bodySha256Hex, int nonPrivmsg, int malformed)`.
- `static class ReplayFidelityCalculator` mit
  `static ReplayFinalReport Compute(ReplayWindow window, IReadOnlyList<ReplayEmote> emotes, IReadOnlyList<ReplayUsageRow> liveRows, IReadOnlyList<ReplayDayLine> days, int windowDays, bool runComplete)`.

**Absicht und Verträge (Tageszähler):**
- **Tagesmap:** aus allen Emotes, deren Lebenszeit den Tag deckt (`FirstSeenAt` null oder
  `FirstSeenAt.Date <= day`, und `ArchivedAt` null oder `ArchivedAt.Date >= day`), per
  `EmoteNameMatching.Coalesce` in der Reihenfolge der Emote-Liste (Task 4 sortiert nach `Id`;
  das ist bewusst **nicht** die Live-Ladereihenfolge, weil die niemand kennt; der Bericht weist
  mehrdeutige Namen deshalb aus, s. u.). Emotes mit `IsArchived` und `ArchivedAt == null`
  gehören **nicht** in die Tagesmap (Design: ausgeschlossen, separat gezählt) und stehen im
  Bericht als `archivedWithoutDate`.
- **Zweitmap für Gründe:** dieselbe Koaleszenz über **alle** Emotes des Kanals. Trifft ein Token
  die Tagesmap nicht, aber die Zweitmap, ist der Grund `BeforeFirstSeen` bzw. `AfterArchived`
  (je nachdem, auf welcher Seite der Tag liegt); trifft es keine, `UnknownName`. Trifft ein Token
  einen Namen aus `AmbiguousNames` der Tagesmap, zählt der Treffer auf die koaleszierte Id
  (wie live) **und** zusätzlich in `UnmatchedByReason[AmbiguousName]` als Markierung; der
  Endbericht nimmt diese Emotes aus der stabilen Teilmenge, nicht aus der vollen Population.
- **Je Nachricht:** `sourceRoomId` gesetzt und ungleich `roomId` ⇒ `SharedChatMessageCount++`,
  Nachricht wird trotzdem gezählt (wie live). `isBot(userId, badges)` genau einmal; Treffer per
  `EmoteNameMatching.MatchEmoteIds(text, dayMap)` (die Dedup je Nachricht steckt darin); je
  Treffer `HumanCounts` oder `BotCounts`. Fällt `sentAtUtc.Date` nicht auf `day`, zählt
  `OutsideDayCount` und die Nachricht wird **trotzdem** gezählt (die Tagesdatei ist die Wahrheit
  des Archivs; der Zähler ist Diagnostik). Emotes mit `FirstSeenAt == null` zählen normal und
  erhöhen je Treffer `FirstSeenUnknownHits`.
- **k-Verteilung:** je `(EmoteId, Tag)` eine Menge distinkter `userId` (nur menschliche Treffer,
  `userId` null zählt als ein Pseudo-Chatter „unbekannt"); `Finish` verdichtet zu `KHistogram`
  (Index k, Wert Anzahl Zellen mit genau k Chattern, Index 0 ungenutzt, Kappung bei k = 10 mit
  „10+"), `CellCount`, `DistinctChatters` des Tages, und **verwirft die Mengen**. Keine
  `userId` verlässt den Zähler.
- Kein Zustand über den Tag hinaus; keine I/O; keine Logger-Abhängigkeit.

**Absicht und Verträge (Endbericht, aus Tageszeilen reproduzierbar):**
- **Tagesklassen:** je Tag `hasLog` (Status `Complete`), `humanOnlyDay` (Tag ≥ Stichtag),
  `liveTotal` = Σ `UseCount + BotUseCount` der Live-Zeilen des Tages, `logTotal` = Σ Human + Bot
  der Tageszeile. `ratio = logTotal / liveTotal` (undefiniert bei `liveTotal == 0`).
  **Live-Lücke vermutet:** `liveTotal == 0` bei `logTotal > 0`. **Abdeckung fraglich (D2):**
  `ratio` außerhalb `[median/2, 2·median]` über alle Tage mit definiertem Verhältnis. **Gewertet**
  = `hasLog` und `humanOnlyDay` und weder Lücke noch fraglich.
- **Gate-Metriken (Präregistrierung), nur über gewertete Tage, human-only, volle Population**
  (jedes Emote mit Live > 0 oder Log > 0 über die gewerteten Tage, inklusive archivierter und
  umbenannter): `totalDeviation = Σ|Log − Live| / ΣLive` (bei ΣLive = 0 undefiniert ⇒
  `gateEligible false`); `top20Recall` = |Live-Top-20 ∩ Log-Top-20| / 20 (bei weniger als 20
  Emotes: /n); `bottomQuartilePrecision` = |Log-Q ∩ Live-Q| / |Log-Q| mit Q = unterstes Viertel
  (⌊n/4⌋ Emotes, mindestens 1) der jeweiligen Rangliste. Ranglisten absteigend nach Summe,
  Gleichstand ordinal nach `EmoteId` (deterministisch). `ratedDays` und `gateEligible` (= Lauf
  vollständig, `windowDays == 30`, `ratedDays ≥ 20`) mit `gateIneligibleReasons`.
- **Plausibilität (a):** Σ Log (Human + Bot) gegen Σ `UseCount + BotUseCount` über alle Tage mit
  Log, als Verhältnis und Differenz.
- **Diagnostik:** stabile Teilmenge = `LastSyncedAt < windowFrom` und (`ArchivedAt` null oder
  `< windowFrom`) und nicht mehrdeutig; darauf Median und p90 der relativen Einzelabweichung
  `|Log − Live| / Live` über Emotes mit Live ≥ N, N = ⌈20 · ratedDays / 30⌉, mindestens 5; M = 30
  qualifizierte Emotes, sonst „nicht entscheidungsfähig" (Feld `stableSubsetDecisive false`);
  Nullzählung auf einer Seite = 100 % und getrennt gezählt; Spearman über die qualifizierten
  Emotes mit mittlerem Rang bei Gleichstand; dieselben Zahlen über alle Emotes daneben; Anteil
  log-only und live-only an ΣLog bzw. ΣLive; `totalDeviation` zusätzlich **mit** den
  fraglichen/lückenhaften Tagen (Open Question 10); Summen der `UnmatchedByReason` und
  `FirstSeenUnknownHits`; `sharedChatMessages`, `outsideDayCount`, `botMessages`,
  `nonPrivmsgLines`, `malformedLines`; k=1-Anteil = Σ `KHistogram[1]` / Σ `CellCount`,
  aggregiertes Histogramm; Liste der Lücken- und der fraglichen Tage mit Verhältnis;
  `humanOnlyDays`, `logDays`, `noLogDays`, Bytes gesamt, Anzahl 429 (Status `RateLimited` in den
  Tageszeilen), `resumePoint` (letzter Tag mit Zeile), `runComplete`.
- **Determinismus:** `Compute` ist eine reine Funktion seiner Argumente; Gleitkommawerte werden
  auf vier Nachkommastellen gerundet, Listen sortiert. Dass zwei Aufrufe dasselbe Objekt liefern,
  ist ein Testfall.

- [ ] **Step 1 (Tests zuerst, container-frei, synthetische Emotes und Zeilen).**
  `ReplayDayCounterTests`: Emote vor `FirstSeenAt` ⇒ `BeforeFirstSeen`, nicht gezählt; nach
  `ArchivedAt` ⇒ `AfterArchived`; `FirstSeenAt` null ⇒ gezählt und `FirstSeenUnknownHits`;
  `IsArchived` ohne `ArchivedAt` ⇒ nicht in der Map, Treffer als `UnknownName`? **Nein:** als
  `AfterArchived` über die Zweitmap, wenn der Name dort steht (das Emote ist in der Zweitmap; der
  Test legt fest, dass „archiviert ohne Datum" wie „nach Archivierung" gilt, und der Bericht
  zählt `archivedWithoutDate` separat); unbekannter Name ⇒ `UnknownName`; mehrdeutiger Name ⇒
  Treffer auf erste Id **und** Markierung; Bot-Nachricht ⇒ `BotCounts`; Shared Chat ⇒ gezählt und
  markiert; Zeitstempel am Vortag ⇒ `OutsideDayCount`; zwei Nachrichten desselben Chatters auf
  dasselbe Emote ⇒ Zelle k = 1; zwei Chatter ⇒ k = 2; Histogramm-Kappung; `Finish` liefert keine
  Nutzer-IDs (Reflexion oder Typ-Check: kein `string`-Set im Record).
  `ReplayFidelityCalculatorTests`: identische Zahlen beidseitig ⇒ Abweichung 0, Recall 1,
  Precision 1, Spearman 1; log-only-Emote erhöht `totalDeviation` (D1: nicht ausgeblendet);
  live-only ebenso; Tag mit `liveTotal 0` ⇒ Lücke, nicht gewertet, Abweichung mit/ohne
  unterscheidet sich; Tag mit dreifachem Verhältnis ⇒ fraglich; Tage vor dem Stichtag zählen für
  (a), nicht fürs Gate; `ratedDays < 20` ⇒ `gateEligible false` mit Grund; `windowDays 3` ⇒
  `gateEligible false`; `runComplete false` ⇒ `gateEligible false`; stabile Teilmenge schließt
  `LastSyncedAt` im Fenster, `ArchivedAt` im Fenster und mehrdeutige Namen aus; N skaliert mit
  `ratedDays` und hat den Boden 5; M < 30 ⇒ `stableSubsetDecisive false`; Nullzählung = 100 %;
  Spearman mit Gleichstand (Handrechnung im Test dokumentiert); Rang-Gleichstand ordinal nach Id;
  `Compute` zweimal ⇒ `Equals` wahr.
- [ ] **Step 2: rot laufen lassen.** Filter `ReplayDayCounterTests|ReplayFidelityCalculatorTests`;
  Expected: Compilerfehler.
- [ ] **Step 3: implementieren.** Median/Perzentil per nächstem Rang (kein Interpolieren, im
  Kommentar genannt); Spearman als Pearson über mittlere Ränge.
- [ ] **Step 4: grün laufen lassen.** Gleicher Filter, dann das ganze Worker-Testprojekt.
- [ ] **Step 5:** `dotnet format`, Nutzer fragen, Commit
  `feat(harness): compute replay fidelity from day counts`.

**Fertig-Bedingung:** Worker-Tests grün; die zwei Klassen haben keine Abhängigkeit außer `Core`
und BCL; jede Kennzahl der Präregistrierung und der Diagnostik-Liste ist ein benanntes Feld.

**Ausdrücklich nicht:** kein Schreiben, kein Lesen, kein Logging, keine Nutzer-ID im Ergebnis,
keine Schwellenauswertung (der Bericht liefert Zahlen, das Gate liest sie; die Schwellen stehen in
#69, nicht im Code).

**Modell: opus.** Genau dieser Code kann still eine falsche Zahl liefern und damit über B
entscheiden; die Testfälle verlangen Handrechnungen. Das Review dieses Tasks ist ein
Beurteilungs-Checkpoint.

---

### Task 6: Der zweite Einstiegspunkt des Worker-Images (T6, nach dem Lane-Merge)

**Files:**
- Create: `src/EmotePurge.Worker/WorkerServiceRegistration.cs`,
  `src/EmotePurge.Worker/Harness/HarnessCommandLine.cs`, `HarnessOptions.cs`, `HarnessRunner.cs`,
  `HarnessReportFile.cs`, `HarnessInputHash.cs`,
  `tests/EmotePurge.Worker.Tests/HarnessCommandLineTests.cs`, `WorkerServiceRegistrationTests.cs`,
  `HarnessReportFileTests.cs`, `HarnessInputHashTests.cs`
- Modify: `src/EmotePurge.Worker/Program.cs`, `src/EmotePurge.Worker/IBotChatterDetector.cs`,
  `BotChatterDetector.cs`, `tests/EmotePurge.Worker.Tests/BotChatterDetectorTests.cs`,
  `src/EmotePurge.Worker/Harness/ReplayModels.cs` (Umstellung des Counters auf `ChatLogMessage`,
  falls Task 5 ein lokales Record gebraucht hat)

**Vorab lesen:** Design „Schichten und Auslöser" (Einstiegspunkt, Vorbedingungen, JSONL), Datenfluss-
Diagramm, Failure Modes „Resume", „Abschluss", „Query 2", **Codex „Fail-open CLI"** und
**„JSONL-Kopf identifiziert den Datensnapshot nicht"**; `Program.cs` vollständig (Kommentar zur
Registrierungsreihenfolge und `BootRecoveryGate`); `WorkerBootSequenceTests` (wie Worker-Klassen
container-frei gebaut werden); die Verträge aus Task 1 bis 5 in den gemergten Dateien.

**Interfaces (verbindlich):**
- `IBotChatterDetector.KnownBotAccountIds → IReadOnlySet<string>` (statische plus konfigurierte
  IDs); `IsBot` unverändert.
- `static class HarnessCommandLine` mit `static HarnessCommandLineResult Parse(string[] args)`;
  Ergebnis ist einer von drei Fällen: `RunWorker`, `RunHarness(string channelName, int days)`,
  `Invalid(string message)`. Grammatik aus Plan-Entscheidung 7.
- `static class WorkerServiceRegistration` mit `AddWorkerCore(IServiceCollection, IConfiguration)`
  (Infrastructure + die sieben Singletons aus `Program.cs:7-15`),
  `AddWorkerHostedServices(IServiceCollection)` (die neun `AddHostedService`, samt Kommentar),
  `AddHarness(IServiceCollection, IConfiguration)` (`AddWorkerCore` plus `HarnessOptions` und
  `HarnessRunner`; **kein** `AddHostedService`).
- `HarnessOptions` (`Harness:*`): `OutputDirectory` (Default `harness-reports`), `MaxMegabytesPerRun`
  (Default 200), `WindowDays` Default 30 (nur Default für `--days`).
- `sealed class HarnessRunner` mit `Task<int> RunAsync(string channelName, int days, CancellationToken ct)`;
  Rückgabe ist der Exit-Code: 0 vollständig mit Endbericht, 3 Vorbedingung verletzt, 4
  abgebrochen mit Wiederaufnahmepunkt (429, Body-Timeout, Byte-Decke, Transportfehler), 5
  „nicht entscheidungsfähig" (Rückfall ohne Badges/`user-id`).
- `HarnessReportFile`: Kopf schreiben/lesen, Tageszeile anhängen (Flush je Zeile), `ReadDays`
  (verwirft eine letzte unvollständige Zeile), `WriteFinalReportAtomically`.
- `static class HarnessInputHash` mit `static string Compute(IReadOnlyList<EmoteLifetimeDto>, IReadOnlyList<UsageStatRowDto>, IReadOnlySet<string> botIds)`
  (SHA-256 über eine kanonische Textform: Emotes nach `Id`, Felder mit `|`, Daten als ISO-8601
  `o`; Zeilen nach `(EmoteId, Date)`; Bot-IDs sortiert ordinal; Zeilenende `\n`).

**Absicht und Verträge:**
- **`Program.cs`:** erste Anweisung ist `HarnessCommandLine.Parse(args)`. `Invalid` ⇒ Meldung nach
  stderr, `return 2`, **kein** Builder. `RunWorker` ⇒ der heutige Ablauf über
  `AddWorkerCore` + `AddWorkerHostedServices`, unverändert im Verhalten. `RunHarness` ⇒
  `Host.CreateApplicationBuilder(Array.Empty<string>())`, `AddHarness`, `Build()`, Migration-Guard
  wie heute, dann in einem Scope `HarnessRunner.RunAsync`, dessen Exit-Code zurückgegeben wird;
  **nie `host.Run()`** in diesem Zweig. `Console.CancelKeyPress`/SIGTERM über
  `IHostApplicationLifetime` oder einen eigenen CTS an den Runner reichen, damit ein
  `docker stop` sauber abbricht (Status „abgebrochen", Wiederaufnahmepunkt).
- **Vorbedingungen** (Reihenfolge, jede mit deutscher Meldung und Exit 3, nichts geschrieben):
  Kanal per `IChannelService.GetByNameAsync` gefunden; `TwitchChannelId` gesetzt (kein
  Login-Fallback, Kommentar verweist auf #34/#44); Fenster: `to` = gestern UTC bezogen auf den
  Prozessstart, `from` = max(`TrackingCoverage.TrackedSince(...)` als UTC-Datum **plus ein Tag**
  (der Join-Tag ist unvollständig), `to − days + 1`); `to − from + 1 == days`, sonst Abbruch mit
  „nur n Tage Messung". Dann laden: Lebenszeiten, Zeilen des Fensters, Stichtag, Bot-IDs;
  `loadedAtUtc` stempeln; Input-Hash; Kopf bilden. Log-Abdeckung ist keine Vorbedingung vor dem
  ersten Abruf (kein `HEAD`), sondern ein Abbruchgrund **nach** dem Lauf: alle Tage 404 ⇒ Exit 3
  mit Meldung, Datei bleibt (sie belegt die Anfragen).
- **Resume:** Dateiname aus der Identität (Plan-Entscheidung 8). Existiert die Datei, Kopf lesen,
  Identität byte-gleich vergleichen (sonst Exit 3 „Datei trägt fremde Identität"), vorhandene
  Tageszeilen laden, eine abgeschnittene Schlusszeile verwerfen; fehlende Tage in Datumsreihenfolge
  abrufen; vorhandene nie erneut. Existiert sie nicht, Kopf schreiben.
- **Je Tag:** `maxBytes` = Decke minus bisher empfangene Bytes (aus Tageszeilen und diesem Lauf);
  `ReadDayAsync` mit einem `ReplayDayCounter`; Ergebnis in eine Tageszeile mit Status, Bytes,
  Digest. `NoLogDay` ⇒ Zeile mit Status `no-log`. `RateLimited`, `BodyTimeout`,
  `ByteCapExceeded`, `TransportFailure`, `MalformedResponse` ⇒ **keine** Tageszeile für diesen
  Tag, Log-Meldung mit Wiederaufnahmepunkt (letzter Tag mit Zeile), Exit 4; die 429-Zahl des
  Laufs wird in den Endbericht des nächsten Laufs getragen, indem die Datei eine Ereigniszeile
  (`"kind":"event"`) mit Statuscode und Zeitstempel bekommt, die der Reader neben den Tageszeilen
  zählt. Aufrufer-Abbruch ⇒ dasselbe mit Meldung „abgebrochen".
- **Rückfall (Design):** haben nach dem ersten `Complete`-Tag null Nachrichten eine `user-id`
  **und** null Nachrichten Badges, Abbruch mit Exit 5 „Logs ohne Badges und user-id, Bot-Split
  nicht möglich, Ansatz neu bewerten"; keine weiteren Abrufe.
- **Abschluss:** nach dem letzten Tag `ReplayFidelityCalculator.Compute` mit `runComplete = true`
  (alle Tage haben eine Zeile), Endbericht atomar als `.report.json` und `.report.md` (Tabelle
  mit den Präregistrierungs-Kennzahlen zuoberst, dann Diagnostik, dann Tagesliste; Text sagt
  ausdrücklich „Replay-Treue, nicht Zuordnungsgenauigkeit", Design T2A). Zusätzlich im Kopf des
  `.md`: Kanal, Fenster, Stichtag, human-only-Tage, Bot-IDs, Ladezeitpunkt, Laufzeit, Bytes, 429,
  `gateEligible` mit Gründen, Hinweis „Live-Lücken: unabhängiger Beleg sind Worker-Log und Uptime
  Kuma, nicht dieser Bericht" (Open Question 10). Distinkte Chatter im Fenster nur ohne Resume
  (Plan-Entscheidung 13).
- **JSON:** `System.Text.Json`, camelCase, `DateOnly` als `yyyy-MM-dd`, eine Zeile je Objekt,
  `"kind"` als Diskriminator (`header`, `day`, `event`). Keine Nutzer-ID, kein Nachrichtentext,
  kein Login eines Chatters in der Datei.
- Konfiguration der Byte-Decke in MB; der Runner rechnet in Bytes.

- [ ] **Step 1 (Tests zuerst, container-frei).** `HarnessCommandLineTests`: leer ⇒ `RunWorker`;
  `harness foo` ⇒ Harness mit Default-Tagen; `harness foo --days 3` ⇒ 3; `harness` ⇒ Invalid;
  `harness foo bar` ⇒ Invalid; `foo` ⇒ Invalid; `harness foo --days 0`, `--days 31`, `--days x`,
  `--days` ohne Wert ⇒ Invalid; `--help` ⇒ Invalid (bewusst); Kanalname mit Großbuchstaben wird
  durchgereicht (Normalisierung macht der Service, Regel 9). `WorkerServiceRegistrationTests`:
  `AddHarness` über eine In-Memory-`IConfiguration` (mit `ConnectionStrings:DefaultConnection` und
  `Redis:ConnectionString`, weil `AddEmotePurgeInfrastructure` sonst wirft) ⇒ **kein**
  `ServiceDescriptor` mit `ServiceType == typeof(IHostedService)`; `AddWorkerCore` +
  `AddWorkerHostedServices` ⇒ genau neun; `AddHarness` löst `HarnessRunner` auf (mit
  `IChatLogArchiveClient`, `IUsageStatQueryService` etc. als registrierte Typen; kein Aufruf, keine
  Verbindung). `HarnessInputHashTests`: gleiche Eingabe in anderer Reihenfolge ⇒ gleicher Hash;
  ein geändertes `LastSyncedAt` ⇒ anderer Hash; zusätzliche Bot-ID ⇒ anderer Hash; ein
  geändertes `BotUseCount` ⇒ anderer Hash. `HarnessReportFileTests` (temporäres Verzeichnis im
  Test): Kopf schreiben und lesen ⇒ Identität gleich; Datei mit abgeschnittener letzter Zeile ⇒
  `ReadDays` liefert nur die vollständigen; fremde Identität ⇒ Fehler; Endbericht atomar (nach
  dem Schreiben existiert keine Temp-Datei, Inhalt vollständig); Ereigniszeile wird gezählt.
  `BotChatterDetectorTests`: `KnownBotAccountIds` enthält die sechs statischen und die
  konfigurierten IDs. Der Resume-Ablauf selbst (Tag n+1) wird als Test des Runners mit
  substituiertem `IChatLogArchiveClient` (NSubstitute) und substituierten Query-Services
  geschrieben: Lauf 1 liefert Tag 1 und 2, dann 429 ⇒ Exit 4, Datei hat zwei Tageszeilen und eine
  Ereigniszeile; Lauf 2 mit gleichen Substituten ⇒ Client wird nur für Tag 3 ff. gerufen, Exit 0,
  Endbericht vorhanden, 429-Zähler 1; Lauf 3 ⇒ kein Client-Aufruf, Endbericht byte-gleich zu
  Lauf 2 (bis auf `generatedAtUtc`, das deshalb **nicht** im `.json`, sondern nur im `.md` steht).
  Vorbedingungen: fehlende `TwitchChannelId` ⇒ Exit 3, kein Client-Aufruf, keine Datei; zu kurze
  Messung ⇒ Exit 3; alle Tage 404 ⇒ Exit 3 mit Datei; Rückfall ohne Badges/IDs ⇒ Exit 5 nach dem
  ersten Tag.
- [ ] **Step 2: rot laufen lassen.** Worker-Testprojekt; Expected: Compilerfehler.
- [ ] **Step 3: implementieren**, `Program.cs` umbauen, Detektor erweitern.
- [ ] **Step 4: grün laufen lassen.** Worker-Testprojekt, dann `dotnet test EmotePurge.slnx`.
- [ ] **Step 5: Argumentprüfung von außen belegen.** `dotnet run --project src/EmotePurge.Worker -- harness`
  und `-- harness foo bar` ⇒ Exit 2 mit stderr-Zeile, ohne dass ein Host startet (kein
  „Application started"-Log). Ressourcen-Regel: vorher `docker compose stop worker`, nachher
  `up -d worker`. Ergebnis in die Rückmeldung.
- [ ] **Step 6:** `dotnet format`, Nutzer fragen, Commit
  `feat(worker): add the harness entry point with a resumable jsonl report`.

**Fertig-Bedingung:** Backend-Suite grün; `Program.cs` hat keinen `AddHostedService`-Aufruf mehr
direkt (alle in `WorkerServiceRegistration`); der Harness-Zweig enthält kein `Run()`; die
Registrierungstests belegen null Hosted Services; Exit-Codes von außen belegt.

**Ausdrücklich nicht:** kein Redis-Zugriff, kein Kommando-Kanal, keine Einzellauf-Sperre, kein
Feature-Flag, kein `HEAD`, kein Schreiben in Postgres, kein Chatter-Login in irgendeiner Datei.

**Modell: opus.** Der Fail-open-Befund von Codex liegt genau hier; Resume-Semantik und
Exit-Codes brauchen ein Modell, das die Failure Modes gegeneinander abwägt.

---

### Task 7: Compose-Service, Konfiguration, Doku, DECISIONS (T7)

**Files:**
- Modify: `docker-compose.yml`, `docker-compose.prod.yml`, `.env.example`, `docs/DECISIONS.md`,
  `CLAUDE.md:138` (ein Satz zum zweiten Einstiegspunkt), `docs/Architectur.md` 6a/6b (je ein
  Satz zum `harness`-Service), `.gitignore` (`harness-reports/`)

**Vorab lesen:** Design „Laufumgebung: Einmal-Container auf dem VPS", Codex „Fail-open CLI"
(`entrypoint`, nicht `command`); beide Compose-Dateien vollständig; `.env.example`
(Kommentarstil); Memory „Prod-Redeploy fährt still das alte Image weiter"; `docs/DECISIONS.md`
Kopf und die Einträge aus Task 1 und vom 2026-09-05.

**Absicht und Verträge:**
- **Service `harness` in beiden Dateien**, gleich aufgebaut: dev mit `build:` wie `worker`, prod
  mit `image: ghcr.io/sensitron/emotepurge-worker:latest` (dasselbe Image, kein zweites);
  `profiles: ["harness"]`, damit `up -d` ihn nie startet; **`entrypoint: ["dotnet",
  "EmotePurge.Worker.dll", "harness"]`** und **kein `command`** (die Argumente von `compose run`
  werden angehängt; ein `run` ohne Argumente landet in `Invalid`, Exit 2); `restart: "no"`;
  **kein `container_name`** (compose `run` vergibt eigene); `healthcheck: disable: true` (der
  Heartbeat-Check des Images misst einen Publisher, den es hier nicht gibt); dieselben Env-Zeilen
  wie `worker` (der Harness braucht `Redis__ConnectionString` nur, weil die Registrierung ihn
  verlangt, öffnet aber keine Verbindung: Kommentar) plus `Harness__OutputDirectory=/data/harness`
  und `Harness__MaxMegabytesPerRun=${HARNESS_MAX_MEGABYTES_PER_RUN:-200}`; `volumes:` Bind-Mount
  `./harness-reports:/data/harness`; `deploy.resources.limits` 1 CPU / 512M **in der Form der
  Nachbarn** (nicht `mem_limit`); `depends_on: postgres: condition: service_healthy` (kein Redis).
- `.env.example`: `HARNESS_MAX_MEGABYTES_PER_RUN=200` mit Kommentar (Fremdlast, 30 Tage eines
  großen Kanals rund 490 MB, kleiner Kanal zuerst, ein am Limit abgebrochener Lauf liefert keine
  Replay-Treue).
- **VPS-Befehle für den Nutzer** (Handgriffe, nie ausführen; SSH-Regel): im Stack-Verzeichnis auf
  dem VPS (Platzhalter `<STACK-DIR>`, Portainer legt die Compose-Datei dort ab, wo der Nutzer den
  Stack pflegt; `.env` liegt daneben): `docker compose -f docker-compose.prod.yml pull harness`
  (sonst fährt `:latest` still alt, s. Memory), dann
  `docker compose -f docker-compose.prod.yml run --rm harness <kanal>` (optional `--days <n>`),
  danach `scp vps:<STACK-DIR>/harness-reports/<datei>.report.md .` und dieselbe `.jsonl`. Ein
  zweiter Lauf mit derselben Zeile nimmt wieder auf. Diese Befehle stehen im `.md`-Bericht-Kopf?
  **Nein**, sie stehen in der Task-Rückmeldung an den Nutzer und im DECISIONS-Eintrag.
- **DECISIONS-Eintrag** (Titel sinngemäß „Der Harness ist ein zweiter Einstiegspunkt des
  Worker-Images, kein Hosted Service"): warum Einmal-Container statt Hosted Service
  (Prozessgrenze macht Redis-Kommando, Einzellauf-Sperre, Konfigurationsschalter, Exception-
  Isolation überflüssig; OOM trifft nicht den Worker, der IRC und 7TV-Socket für alle Kanäle
  hält); warum `harness` im `entrypoint` (Codex: `compose run` ersetzt `command`; sonst startet
  ein Argument-loser Lauf den vollen Worker neben dem Prod-Worker und zählt doppelt über den
  additiven UPSERT); die Argument-Grammatik und die Exit-Codes als Vertrag; die geteilten Regeln
  (Fensterstart, Stichtag, Query-Methoden) mit Verweis auf den Task-1-Eintrag zur Matching-Regel;
  JSONL-Identität und Resume-Regel; Byte-Decke als Fremdlast-Bremse; was ausdrücklich nicht
  gebaut wurde (Design „Bewusst weggelassen"). `**Betrifft:**` nennt beide Compose-Dateien,
  `.env.example`, `Program.cs`, `WorkerServiceRegistration.cs`, `Harness/`, die Dateien aus
  Task 2 und 4.
- `CLAUDE.md:138`: ein Halbsatz „… und einen zweiten Einstiegspunkt `harness <kanal>` (kein Hosted
  Service, s. DECISIONS)". `Architectur.md` 6a/6b: `harness` als Profil-Service, nur per `run`.

- [ ] **Step 1: Compose-Blöcke, `.env.example`, `.gitignore`.**
- [ ] **Step 2: `docker compose --profile harness config`** (dev) und
  `docker compose -f docker-compose.prod.yml --profile harness config` (prod, mit einer Kopie von
  `.env`): Expected: `entrypoint` enthält `harness`, kein `command`, kein `container_name`, Profil
  gesetzt; `docker compose config` **ohne** Profil zeigt den Service nicht.
- [ ] **Step 3: lokaler Container-Lauf.** `docker compose build harness`, dann
  `docker compose run --rm harness` ⇒ Exit 2 (Invalid, kein Host); dann
  `docker compose run --rm harness brudivoeller_tv --days 2` ⇒ je nach Dev-DB Exit 3 (Vorbedingung,
  mit Meldung) oder ein echter Lauf mit zwei Anfragen; `./harness-reports/` enthält danach die
  Dateien mit Besitzer `appuser` (UID aus dem Image): **prüfen, dass der Bind-Mount beschreibbar
  ist**; wenn nicht, gehört ein `user:`-Eintrag oder ein vorab angelegtes Verzeichnis mit
  passenden Rechten in den Compose-Block, und der DECISIONS-Eintrag nennt es. Währenddessen läuft
  `emotepurge-dev-worker` weiter; sein Log zeigt weiter IRC-Frames (Live-Probe „Prozessgrenze").
- [ ] **Step 4: DECISIONS, CLAUDE.md, Architectur.md.**
- [ ] **Step 5:** Nutzer fragen, Commit
  `feat(compose): run the backfill harness as a one-off worker container` (inklusive DECISIONS,
  Regel 3).

**Fertig-Bedingung:** beide `config`-Ausgaben wie erwartet; lokaler `run` mit Exit 2 und mit
Exit 0/3 belegt; Worker-Log ungestört; DECISIONS-Eintrag im selben Commit.

**Modell: sonnet.**

---

### Task 8: Gates, Live-Verifikation, Prod-Übergabe (Regel 16)

**Files:** keine Code-Änderungen erwartet. Ein Defekt geht als eigener Fix-Task an einen
Subagenten zurück.

**Vorab lesen:** `~/.claude/CLAUDE.md` „Fernzugriff (SSH)"; Memory
`project_parallel_sessions_38_69_resources`, `project_prod_redeploy_stale_latest_image`,
`feedback_abort_failing_probe_after_two_tries`; Design „Success Criteria".

- [ ] **Step 1: Gates komplett.** `dotnet test EmotePurge.slnx` (Docker läuft),
  `dotnet format EmotePurge.slnx --verify-no-changes`, `git diff --stat main -- web/` ist leer.
- [ ] **Step 2: Matcher-Refactor vorher/nachher.** Falls in Task 1 nur der Replay-Vergleich lief:
  jetzt zusätzlich den gemergten Stand einige Minuten live (`docker compose up -d --build worker`,
  Debug-Level am `TwitchChatManager`) gegen die gejointen Kanäle laufen lassen und stichprobenhaft
  Nachrichten mit Emotes gegen `UsageStats` prüfen (Zeile für heute steigt um die Zahl
  **verschiedener** Emotes je Nachricht, nicht um Vorkommen). Watchdog-Zeitstempel im
  Admin-Monitoring laufen weiter.
- [ ] **Step 3: Harness-Lauf lokal gegen `brudivoeller_tv` (Twitch-ID 489111423, einziger
  Dev-Kanal im Archiv laut T8).** Vorher per
  `docker compose exec postgres psql -U emotepurge -d emotepurge` prüfen, ob die Dev-DB den Kanal
  mit `TwitchChannelId` und wie vielen Messtagen führt; `--days` entsprechend klein wählen (2 bis
  5). Lauf über den Container aus Task 7 (nicht `dotnet run`, dann bleibt der Dev-Worker
  unberührt). Anfragebudget: fester Abstand aus der Konfiguration, ein 429 beendet den Lauf;
  **nach zwei Fehlschlägen abbrechen und den Nutzer fragen**, nicht dreimal wiederholen. Erwartung:
  `.jsonl` mit Kopf und n Tageszeilen, `.report.md` mit `gateEligible false` (Fenster < 30) und
  plausiblen Zahlen (Plausibilität (a) in der Größenordnung 1, sofern der Worker die Tage
  gemessen hat), Bytes und Dauer genannt. Zweiter identischer Lauf ⇒ keine Anfrage, gleicher
  Bericht. Fehlt dem Kanal die Messung: der Exit-3-Pfad mit korrekter Meldung ist das Ergebnis,
  und ein Lauf mit `--days 1` gegen einen Kanal mit einem Messtag ersetzt ihn, wenn es einen gibt.
- [ ] **Step 4: Prod-Übergabe an den Nutzer** (Befehle vorbereiten, nicht ausführen): Merge und
  Push nach Task 9; CI baut das Worker-Image; keine Migration (kein Schema); Portainer-Redeploy
  mit erzwungenem Pull, damit der Worker den Refactor fährt; für den Harness die drei Befehle
  aus Task 7 (`pull harness`, `run --rm harness <kanal>`, `scp`). Kanalwahl: **ein kleiner Kanal**
  aus den 12 erfassten (Open Question 6, per read-only SELECT über `Channels` und `UsageStats`,
  den der Nutzer ausführt); ein großer nur nach Open Question 1. **Hinweis, kein Task:** der
  bindende Lauf braucht 30 human-only-Tage ab dem kanalspezifischen Bot-Split-Stichtag, also
  frühestens Anfang Oktober 2026; ein früherer Lauf liefert Plausibilität (a) und den Screenshot
  für den Mod, keine Gate-Entscheidung.
- [ ] **Step 5: Rückmeldung** mit Zahlen der Probe, Berichts-Auszug, offenen Punkten.

**Fertig-Bedingung:** Gates grün; Live-Probe des Matchers ohne Auffälligkeit; ein lokaler
Harness-Lauf (oder sein sauberer Exit-3-Pfad) mit Zahlen belegt; Prod-Befehle übergeben.

**Modell: opus.**

---

### Task 9: Zweitmeinung vor dem Merge (Regel 22)

- [ ] **Step 1:** `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` in
  **einem** Argument-String; ohne `--scope branch` reviewt Codex den Working Tree und meldet bei
  sauberem Tree eine falsche Entwarnung. „Reviewer failed to output a response" mit Exit 1 ist
  das Kontingent, Job-Log lesen, nicht neu starten. Einmal je Branch, nie zweimal für dasselbe Diff.
- [ ] **Step 2:** Findings unverändert an den Nutzer. Widerspricht Codex einem Opus-Review
  (P1/P2, das die andere Seite nicht sieht; gegensätzliche Bewertung; unvereinbare Fixes),
  entscheidet Fable mit nur den strittigen Stellen. Reine Ergänzungen sind kein Widerspruch.
- [ ] **Step 3:** Merge auf `main` und Push macht der Nutzer; dabei `docs/DECISIONS.md` und
  `ServiceCollectionExtensions.cs` gegen den #38-Stand von Hand zusammenführen.

---

### Task 10: Präregistrierung und T8-Bericht in #69 (T9, Prozess)

Kein Code. Nach dem Merge, vor dem ersten Lauf auf Prod.

- [ ] **Step 1:** Den T8-Bericht (Abschnitt „T8-Live-Proben 2026-09-05" des Design-Docs) als
  Kommentar nach #69 stellen, falls noch nicht geschehen (das Design-Doc verweist darauf, der
  Kommentar existiert laut Auftrag noch nicht).
- [ ] **Step 2:** Die Präregistrierung als Kommentar nach #69: Gesamtabweichung ≤ 10 % über die
  volle Population und gewertete Tage, Top-20-Recall ≥ 0,9, unteres-Quartil-Precision ≥ 0,8,
  mindestens zwei Kanäle mit je mindestens 20 gewerteten Tagen, Fensterlänge 30; Diagnostik nicht
  bindend (Median, p90, Spearman der stabilen Teilmenge mit N, M; log-only/live-only-Anteile;
  Tagesverhältnisse). Nach dem Kommentar werden die Zahlen nicht mehr verhandelt.
- [ ] **Step 3:** Die Feldnamen des `.report.json` (aus Task 5/6) im selben Kommentar den Schwellen
  zuordnen, damit der spätere Bericht ohne Deutung gegen die Schwellen gelesen werden kann.

---

## Tests des Designs → Task-Zuordnung

| Design-Zeile (Coverage-Diagramm / Success Criteria) | Task | Datei |
|---|---|---|
| Split/ordinal/Dedup: leer, Whitespace, Unicode, ACTION, Anfang/Ende, Wiederholung; Koaleszenz erster gewinnt, Mehrdeutige gelistet | 1 | `Infrastructure.Tests/Unit/EmoteNameMatchingTests.cs` |
| Regression Live-Pfad + Match-Cache gleiche Treffer | 1, 8 | Vorher/Nachher-Probe (nicht committet), `SevenTvSyncServiceTests` |
| `EmoteSetStatusServiceTests` unverändert grün | 2 | `Integration/EmoteSetStatusServiceTests.cs` (ohne Diff) |
| Tag gestreamt, 404, 429, Stream stockt nach 1 KB, MB-Decke, Abbruch-Token, Wurzelform | 3 | `Unit/ChatLogArchiveClientTests.cs`, `Unit/JustlogRawLineParserTests.cs` |
| Emotes inkl. archivierter mit drei Zeitstempeln; UsageStat-Fenster über ID-Liste; Stichtag | 2, 4 | `Integration/UsageStatQueryServiceTests.cs` |
| Grenzen, stabile Teilmenge, Replay-Treue, N/M, Spearman, Rangwechsel, k=1, Lücken mit/ohne, D1-Population, D2-Abdeckung | 5 | `Worker.Tests/ReplayDayCounterTests.cs`, `ReplayFidelityCalculatorTests.cs` |
| Argument-Zweig startet keinen Hosted Service; unbekannte Argumente Exit ≠ 0 | 6 | `Worker.Tests/WorkerServiceRegistrationTests.cs`, `HarnessCommandLineTests.cs` |
| Kopf-Gleichheit ⇒ Resume, Ungleichheit ⇒ neue Datei; Abschluss zweimal identisch; Prozesstod nach Tag k; Datei korrupt | 6 | `Worker.Tests/HarnessReportFileTests.cs`, Runner-Tests mit NSubstitute |
| Vorbedingung verletzt ⇒ Meldung, Exit ≠ 0; Logs ohne Badges/user-id ⇒ nicht entscheidungsfähig | 6 | Runner-Tests |
| Lauf im Einmal-Container berührt den Prod-Worker nicht (IRC-Frames laufen) | 7, 8 | lokale Probe, Prod-Handgriff des Nutzers |
| Keine Api-Testfälle | keiner | kein Filter, keine Route |

## Definition of Done

- `dotnet test EmotePurge.slnx` grün (drei Backend-Testprojekte, Testcontainers).
- `dotnet format EmotePurge.slnx --verify-no-changes` ohne Befund.
- Live-Verifikation aus Task 8: Matcher-Probe ohne Differenz, ein lokaler Harness-Lauf mit
  Zahlen (oder sein sauberer Vorbedingungs-Abbruch), Dev-Worker während des Container-Laufs
  ungestört.
- DECISIONS-Einträge: Task 1 (Matching-Regel) und Task 7 (Einstiegspunkt, Compose, geteilte
  Regeln), je im selben Commit wie die Änderung (Regel 3).
- **Keine Frontend-Änderung, deshalb weder `npm --prefix web test` noch die E2E-Suite:** kein
  Endpunkt, kein DTO auf der Leitung, kein Fehlercode, kein i18n-Schlüssel ändert sich; der
  Bericht ist eine Datei im Container, nicht eine Antwort der Api. `git diff main -- web/` leer ist
  der Beleg, und die E2E-Suite gehört ohnehin #38 (Port 5151).
- Codex-Zweitmeinung (Task 9) liegt vor; Widersprüche durch Fable entschieden.
- Prod-Befehle übergeben, nicht ausgeführt; Präregistrierung in #69 vor dem ersten Prod-Lauf.

## Selbstprüfung (beim Schreiben dieses Plans)

- **Design-Deckung:** T1 → Task 1, T2 → Task 2, T3 → Task 3, T4 → Task 4, T5 → Task 5, T6 →
  Task 6, T7 → Task 7, T9 → Task 10; T8 und T10 als Vermerk. Datenfluss-Diagramm: jede Box hat
  einen Task (Vorbedingungen, Query 1/2, geteilte Regeln, Kopf, Tagesschleife mit 404/429/Timeout/
  Decke, Abschluss). Failure Modes: Body (3), 429 (3, 6), Format (3), Query 2 bei DB-Ausfall (6:
  Abbruch vor dem ersten Abruf, nichts geschrieben), Matching-Regression (1), Resume korrupt (6),
  Abschluss nach Prozesstod (6), OOM (7: Limit), Prod-Worker (7/8). Codex-Nachtrag: Fail-open
  (6/7), Snapshot-Identität (6), D1-Population (5), D2-Abdeckung (5). Präregistrierung (10).
- **Namenskonsistenz:** `EmoteNameMatching.MatchEmoteIds`/`Coalesce`/`EmoteNameMap` (1 → 5),
  `TrackingCoverage.TrackedSince` (2 → 6), `GetEarliestBotUsageDateAsync` (2 → 6),
  `IChatLogArchiveClient.ReadDayAsync`/`ChatLogMessage`/`ChatLogDayResult`/`ChatLogDayStatus`
  (3 → 6), `EmoteLifetimeDto`/`UsageStatRowDto`/`GetEmoteLifetimesAsync`/`GetRowsAsync` (4 → 6),
  `ReplayDayCounter`/`ReplayFidelityCalculator`/`ReplayDayLine`/`ReplayFinalReport` (5 → 6),
  `KnownBotAccountIds`, `HarnessCommandLine.Parse`, `WorkerServiceRegistration.AddHarness`,
  `HarnessRunner.RunAsync`, `HarnessReportFile`, `HarnessInputHash.Compute` (6 → 7/8),
  `Harness:*` und `ChatLogArchive:*` (3/6 → 7), `HARNESS_MAX_MEGABYTES_PER_RUN` (7 → 8).
- **Kein fertiger Code:** Signaturen einzeilig, Verhalten in Sätzen, Tests als Fallnamen,
  Compose als Eigenschaftsliste, DECISIONS als Pflichtinhalte.
- **Keine Gedankenstriche im neuen Text**; die zitierten Überschriften des DECISIONS-Formats
  tragen den vorhandenen.

## Offene Punkte für den Nutzer

Nur echte Widersprüche zwischen Code und Design-Doc plus die zwei Prozesshinweise. Keiner
blockiert den Start; die Plan-Entscheidungen oben sind Vorschläge, die der Nutzer kippen kann.

- **O1: Testort der Matching-Klasse.** Das Design nennt `tests/EmotePurge.Worker.Tests` (T1);
  weil die Klasse nach `Core` muss (Infrastructure braucht die Koaleszenz), liegt ihr Test nach
  Regel 11 in `Infrastructure.Tests/Unit`. Plan-Entscheidung 1; wer die Design-Zeile wörtlich will,
  müsste die Klasse in `Infrastructure` legen und `Worker.Tests` den Testauftrag geben, was der
  csproj-Beschreibung von `Worker.Tests` widerspricht.
- **O2: `mem_limit` gegen `deploy.resources.limits`.** Das Design schreibt `mem_limit`; beide
  Compose-Dateien setzen Limits als `deploy.resources.limits`, und Compose v2 honoriert die für
  `run` ebenso. Der Plan folgt der Datei. Nur relevant, falls der Nutzer weiß, dass Portainer auf
  dem VPS `deploy`-Limits bei `run` ignoriert; dann `mem_limit` zusätzlich.
- **O3: `IBotChatterDetector` bekommt ein zweites Member.** Der Plan zu #31 legte „genau eine
  Methode" fest; das Design will die Bot-ID-Menge im Berichtskopf. Plan-Entscheidung: read-only
  Property `KnownBotAccountIds` (Task 6). Alternative wäre, die Konfiguration im Harness ein
  zweites Mal zu parsen, was die Duplikation ist, die der #31-Plan gerade vermied.
- **O4: Koaleszenz-Reihenfolge ist im Live-Pfad unspezifiziert.** `RefreshMatchCacheAsync` lädt
  ohne `OrderBy`; „erster gewinnt" hängt damit an der physischen Reihenfolge in Postgres. Der
  Harness koalesziert nach `Id` und kann bei mehrdeutigen Namen eine andere Id treffen als der
  Live-Cache. Das Design weist Mehrdeutigkeit aus und nimmt sie aus der stabilen Teilmenge; die
  volle Population (D1) enthält sie. Kein Task ändert den Live-Pfad (ein `OrderBy` dort wäre eine
  Verhaltensänderung des Hot Path); der DECISIONS-Eintrag aus Task 1 benennt es. Falls der Nutzer
  die Live-Reihenfolge lieber festnageln will: eigener kleiner Task vor Task 1.
- **O5: Wo liegt die Compose-Datei auf dem VPS?** Die `run`-Befehle brauchen `docker-compose.prod.yml`
  und `.env` in einem Verzeichnis, das der Nutzer kennt (Portainer-Stack-Pfad oder eine Kopie).
  Der Plan verwendet den Platzhalter `<STACK-DIR>`; der Nutzer ersetzt ihn bei der Übergabe.
- **Prozess:** Der T8-Bericht soll laut Auftrag noch als Kommentar nach #69 (Task 10, Step 1), und
  die neuen Schwellen der Präregistrierung werden dort vor dem ersten Prod-Lauf festgeschrieben
  (Task 10, Step 2). Der bindende Lauf ist frühestens Anfang Oktober 2026 möglich (30 human-only-
  Tage ab dem kanalspezifischen Stichtag); ein früherer Lauf liefert Plausibilität und Screenshot.
