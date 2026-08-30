# Rate-Limits: Verstärker, Policies, Cache, Beobachtbarkeit — Umsetzungsplan (Issue #33)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. **Dieser Plan enthält bewusst keinen fertigen Code**
> (Projektregel, s. `~/.claude/CLAUDE.md`): jeder Task beschreibt Absicht, Verträge, Grenzfälle
> und Prüfbedingungen — die Implementierung entsteht im Task selbst.

**Goal:** Lokale 429er bei normaler Bedienung (Issue #33) verschwinden: Client-Verstärker werden
entfernt, die falsch messende Policy `ExternalApi` wird durch `InteractiveRead` und `Voting`
ersetzt, die Moderated-Channels-Liste wird serverseitig gemeinsam gecacht, und lokale Ablehnungen,
Cachewirkung und echte Provider-429er werden read-only beobachtbar.

**Architecture:** Vier Rollout-Schritte, jeder einzeln deploy- und rückrollbar, in der Reihenfolge
der Spec: (1) rein frontendseitige Request-Reduktion, (2) neue Token-Bucket-Policies als *eine*
Einheit mit der Entfernung von `ExternalApi`, (3) ein Redis-Listencache mit In-Process-Single-Flight
als einzige Quelle der moderierten Twitch-Channels, (4) fail-open-Telemetrie in Redis plus ein
read-only Admin-Endpoint mit UI-Abschnitt.

**Tech Stack:** .NET 10 (Minimal API, `System.Threading.RateLimiting`, EF Core/Npgsql, Redis via
StackExchange.Redis, xUnit + NSubstitute + Testcontainers), Angular 22 (Standalone, Signals,
zoneless), Transloco, Vitest, Playwright.

**Spec:** [`docs/superpowers/specs/2026-08-30-rate-limit-architecture-design.md`](../specs/2026-08-30-rate-limit-architecture-design.md)
— fertig reviewt und **verbindlich**; dieser Plan setzt sie um und entscheidet nur dort, wo sie
ausdrücklich Entscheidungen an die Implementierung delegiert. **Baseline (Task 0, erledigt):**
[`docs/superpowers/2026-08-30-rate-limit-baseline-messung.md`](../2026-08-30-rate-limit-baseline-messung.md)
— die empirischen Vergleichswerte je Ablauf; jede „vorher/nachher"-Zahl in diesem Plan verweist
dorthin.

## Abgrenzung: was dieser Plan ausdrücklich NICHT enthält

Wer eines der folgenden Themen sucht, sucht am falschen Ort — sie sind bewusst ausgegliedert:

- **Die dreiwertige Rollenauflösung** (`provider_rate_limited`, `authorization_provider_unavailable`,
  Guard-Verhalten bei `503`): eigener Slice mit eigener Spec (s. Spec-Abschnitt „Dreiwertige
  Rollenauflösung hier mitbauen"). In diesem Plan bleiben alle Rollenverträge bool.
- **Issue #39** (App-Token-Topologie, Multi-Prozess-Koordination).
- **Alles unter „Bewusst vertagt" in der Spec**: verteilter Provider-Budgetzustand,
  Observe-/Enforce-Modi, konfigurierbare 7TV-Grenze, providerbedingte Fehlerverträge,
  Laufzeit-editierbare Limits/Admin-Write-Endpoint, Zusammenlegen von `duplicate-names` und
  `active-set`, SSE-Änderungen, Cloudflare-/nginx-Regeln.

## Korrektur eines Abnahmekriteriums: zwölf statt sechs Rundgänge

Die Spec formuliert als Kriterium „sechs vollständige Rundgänge mit Rückkehr in einer Minute
erzeugen keine lokale 429". Die Baseline-Messung hat dieses Kriterium **widerlegt als Test**: sechs
dichte Rundgänge kosten dank des 30-s-Permissions-Caches im Client nur **38** Permits und bleiben
schon heute unter der 40er-Grenze — erst der **siebte** Rundgang löst die 429 aus
(Baseline, Abschnitt „Abweichung von der Spec"). Ein Abnahmetest, der ohne jede Codeänderung grün
ist, prüft nichts.

Der Plan setzt die Schwelle deshalb auf **zwölf Rundgänge in einer Minute** (~74 Permits nach
heutiger Zählung): das fällt heute sicher durch (Bruchpunkt bei 40) und liegt zugleich klar
innerhalb der geplanten `InteractiveRead`-Kapazität von 300 bei 5 Tokens/s Nachfüllung. Der
zugehörige Test (Task 7) ist damit vor Schritt 2 rot und nach Schritt 2 grün — ein echtes Gate.
Alle übrigen Kriterien und ihre Harness-Zuordnung übernimmt der Plan unverändert aus der Spec
(Tabelle am Ende).

## Global Constraints

Jede Task-Anforderung schließt diesen Abschnitt implizit ein.

- **Regel 1: vor jedem `git commit` erst den Nutzer fragen.** Jeder Rollout-Schritt endet in einem
  Commit-**Checkpoint** — einer ausdrücklichen Freigabefrage an den Nutzer, nie einem Automatismus.
- **Commitgrenzen (Spec-Vorgabe):** Jeder der vier Rollout-Schritte ist **ein eigener logischer
  Commit** und separat rückrollbar. Diese Spec-Vorgabe geht der üblichen feineren Aufteilung aus
  Regel 2 hier vor; die Tasks innerhalb eines Schritts bauen den Working Tree auf und committen
  selbst **nicht**. Conventional-Commit-Präfixe gelten weiter (`fix(web):`, `feat(api):`, …).
- **Regel 3:** Der Commit eines Schritts, der Konvention/Vertrag/Topologie ändert, enthält seinen
  `docs/DECISIONS.md`-Eintrag **im selben Commit**. Das betrifft Schritt 2 (Policy-Zuordnung),
  Schritt 3 (Cache-Topologie) und Schritt 4 (Monitoring-Topologie). Schritt 1 ändert nur
  Client-Verhalten innerhalb bestehender Verträge und braucht keinen Eintrag.
- **Regel 4 / Schichtentreue:** Kein `AppDbContext` und kein `IConnectionMultiplexer` aus
  Minimal-API-Handlern; neue Backend-Fähigkeiten als Interface in `EmotePurge.Core/Services/` +
  Implementierung in `EmotePurge.Infrastructure/Services/` (bzw. `Redis/` für Redis-Stores),
  registriert **ausschließlich** in `AddEmotePurgeInfrastructure`. `EmotePurge.Core` bleibt
  BCL-only (von `CoreAssemblyReferenceTests` erzwungen) — kein `HttpContext`, kein Redis-Typ, kein
  ASP.NET-Typ in Core. ASP.NET-spezifische Policy-Options, Partitionierung und die
  Accepted/Rejected-Erfassung bleiben in `src/EmotePurge.Api/RateLimiting/`.
- **Regel 6:** Endpoints in `Endpoints/*.cs`, Autorisierung über `IEndpointFilter` (der neue
  Admin-Endpoint erbt den `GlobalAdminAuthorizationFilter` der bestehenden `/api/admin`-MapGroup).
- **Regel 7:** **Kein neuer API-Fehlercode in dieser Runde.** Lokale Ablehnungen behalten
  `ApiErrorCodes.RateLimitExceeded` samt bestehender Übersetzungen. Neue Admin-UI-Texte brauchen
  Schlüssel in **beiden** Locale-Dateien (`web/public/i18n/de.json` und `en.json`).
- **Regel 11/12:** Neue Infrastructure-Logik → Test in `tests/EmotePurge.Infrastructure.Tests`
  (`Unit/` vs. `Integration/` je nach echter Infrastruktur-Berührung); geänderte Filter-/Policy-
  Zuordnung → Fälle in `tests/EmotePurge.Api.Tests`; neue Services/Guards/Utilities in
  `web/src/app/core/` → co-located `*.spec.ts`; Seiten-Flows → Playwright. **Keine isolierten
  Angular-Komponententests** — die Seiten werden per Playwright und live geprüft.
- **Regel 16:** Backend-Schritte vor dem Commit **live** gegen echte Postgres-/Redis-/Twitch-/
  7TV-Zugänge verifizieren; der konkrete Verifikationsschritt steht im Abschluss-Task jedes
  Schritts. `dotnet build` ist keine Fertigmeldung.
- **Regel 18:** vor jedem Commit `dotnet format EmotePurge.slnx` und
  `npm --prefix web run format`; `npm --prefix web run lint` muss grün sein.
- **Regel 19 / Angular-Memberreihenfolge:** C# `const` → `readonly` → Felder → Properties →
  öffentliche → private Methoden; Angular-Reihenfolge nach `web/.claude/CLAUDE.md`.
- **Sprache:** neue Bezeichner und Kommentare englisch, Log-/`throw`-Messages deutsch,
  Plandokumentation und DECISIONS-Einträge deutsch.
- **„Fertig" je Schritt:** `dotnet test EmotePurge.slnx` (braucht laufendes Docker, Testcontainers)
  und `npm --prefix web test -- --watch=false` grün; bei UI-Änderungen (Schritte 1 und 4)
  zusätzlich `npm --prefix web run e2e`. **Die E2E-Suite läuft nur, wenn auf `:5151` keine Api
  lauscht** — eine laufende Api beantwortet ungemockte Requests mit 401, der `apiAuthInterceptor`
  leitet auf die Login-Seite um und rund die halbe Suite fällt mit irreführendem „element not
  found" durch. Vor jedem Playwright-Lauf ein laufendes `dotnet run` beenden.
- **Regel 15:** vor jedem Docker-Test nach Api-/Worker-Änderungen `--build` mitgeben.
- **UI-Änderungen** (Schritt 4, Admin-Abschnitt) folgen `docs/UI-Designsprache.md` (verbindlich)
  und `DESIGN.md`; bei Widerspruch gilt die Designsprache.
- **Befehle:**
  - ein xUnit-Test: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter "FullyQualifiedName~<Klasse>.<Methode>"`
  - eine Vitest-Datei: `npm --prefix web test -- --watch=false --include="src/app/core/voting/vote-session.service.spec.ts"`
  - ein Playwright-Test: `npm --prefix web run e2e -- vote-ballot.e2e.spec.ts -g "<Testname>"`

## Reihenfolge und Abhängigkeiten

Die vier Schritte werden **strikt in Spec-Reihenfolge** umgesetzt und deployt (1 → 2 → 3 → 4);
jeder endet mit einem eigenen Commit-Checkpoint. Innerhalb der Schritte gilt:

| Schritt | Tasks | Parallelisierbar |
|---|---|---|
| 1 — Client-Verstärker | 1–5, Abschluss 6 | **Strang A** (Tasks 1→2→3, alle in `usage-stats-page.ts`) und **Strang B** (Tasks 4→5, Voting) dürfen parallel laufen; innerhalb eines Strangs sequenziell (gleiche Dateien). Task 6 wartet auf beide. |
| 2 — Policies | 7–9, Abschluss 10 | sequenziell: 7 (rote Tests) → 8 (Policy-Bausteine) → 9 (Umhängen) → 10 |
| 3 — Listencache | 11–12, Abschluss 13 | sequenziell: 11 → 12 → 13 |
| 4 — Beobachtbarkeit | 14–17, Abschluss 18 | 14 → 15 → 16 → 17 sequenziell (jeder konsumiert Verträge des Vorgängers); 17 (UI) darf parallel zu 16 **starten**, sobald der Response-Vertrag aus 16 festgezurrt ist. Task 18 wartet auf alle. |

Schritt 2 hängt fachlich nicht an Schritt 1 (die roten Tests aus Task 7 messen die heutige
Serverseite), Schritt 3 nicht an Schritt 2 — die Reihenfolge ist Rollout-Disziplin aus der Spec,
kein technischer Zwang. Schritt 4 braucht Schritt 2 (Policy-Namen und Options als Snapshot-Quelle)
und Schritt 3 (der Listencache ist einer der drei zu zählenden Caches).

## File Structure (Gesamtbild)

| Datei | Schritt | Verantwortung |
|---|---|---|
| `web/src/app/features/usage-stats/usage-stats-page.ts` | 1 | Set-Status vom Range-Load lösen, `awaitSync` eventgetrieben mit ≤3 Fallback-Probes, Fehlergrund-Recheck 1/min |
| `web/src/app/core/voting/vote-session.service.ts` + `.spec.ts` | 1 | einmaliger Guard-zu-Page-Handoff des `/results`-Erfolgs |
| `web/src/app/core/voting/vote-session-access.guard.ts` + `.spec.ts` | 1 | Guard legt die erfolgreiche Antwort ab statt sie zu verwerfen |
| `web/src/app/features/voting/vote-session-detail-page.ts` | 1 | Handoff verbrauchen; Vote-Erfolge in die gemeinsame Reload-Pipeline; Channel-Status nur initial + `channel.synced` |
| `web/e2e/usage-range-resolution.e2e.spec.ts`, `usage-atlas.e2e.spec.ts`, `vote-ballot.e2e.spec.ts` | 1 | Requestzählung als Abnahme-Harness |
| `src/EmotePurge.Api/RateLimiting/RateLimitingOptions.cs` | 2 | **neu** — stark typisierte Options aller fünf Policies, Validierung |
| `src/EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs` | 2 | **neu** — die Policy-Namen als Konstanten (Endpoints + Tests teilen sie) |
| `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs` | 2, 4 | Token-Bucket-Partitionierer (per User; per User+Session), Retry-After-Fallback; ab Schritt 4 Ablehnungs-Markierung für die Telemetrie |
| `src/EmotePurge.Api/Program.cs` | 2, 4 | Policy-Registrierung aus Options, Kommentarkorrekturen; ab Schritt 4 Telemetrie-Middleware um `UseRateLimiter` |
| `src/EmotePurge.Api/Endpoints/{Channel,VoteSession,Emote,UsageStats}Endpoints.cs` | 2 | Routen umhängen nach Solltabelle (Task 9), `ExternalApi` restlos entfernen |
| `tests/EmotePurge.Api.Tests/RateLimitPolicyBudgetTests.cs` | 2 | **neu** — die zwei korrigierten Abnahmetests (12 Rundgänge; 100 Votes) |
| `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs`, `AuthFilterMatrixTests.cs`, `ApiFactory.cs` | 2 | Token-Bucket-Semantik, Policy-Namen, unveränderte 401/403-Verträge |
| `src/EmotePurge.Core/Services/IModeratedChannelsProvider.cs` | 3 | **neu** — Vertrag der einzigen Quelle moderierter Channels (`ModeratedChannelsLookup`) |
| `src/EmotePurge.Infrastructure/Services/ModeratedChannelsProvider.cs` | 3 | **neu** — Redis-Listencache + In-Process-Single-Flight + Helix-Miss-Pfad |
| `src/EmotePurge.Infrastructure/Services/{MyChannelsService,ModeratorCheckService,EmoteSetOwnershipService}.cs` | 3 | Konsumenten auf den Provider umstellen |
| `src/EmotePurge.Core/Services/IModRoleCache.cs`, `src/EmotePurge.Infrastructure/Redis/ModRoleCache.cs` | 3 | Bool-Moderatorcache entfernen; Invalidate löscht den Listen-Key mit |
| `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs` | 3 | abgeschnittene Pagination (>10 Seiten) als Fehler statt als stilles Teilergebnis |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ModeratedChannelsProviderTests.cs` | 3 | **neu** — Hit/Miss/Single-Flight/Teilfehler/Invalidierung |
| `src/EmotePurge.Core/Services/IRateLimitTelemetry.cs`, `IRateLimitTelemetryReader.cs` | 4 | **neu** — Schreib-/Lesevertrag der Telemetrie, DTOs BCL-only |
| `src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs` | 4 | **neu** — Zeit-Buckets mit TTL, Letzt-Vorfälle, fail-open |
| `src/EmotePurge.Infrastructure/Telemetry/ProviderRequestTelemetryHandler.cs` | 4 | **neu** — `DelegatingHandler` an den drei typisierten Provider-Clients |
| `src/EmotePurge.Api/RateLimiting/RateLimitTelemetryMiddleware.cs` | 4 | **neu** — akzeptiert/abgelehnt je Policy zählen, fachliche 429 unangetastet |
| `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs` | 4 | `GET /api/admin/rate-limits` (read-only, global-admin-only) |
| `web/src/app/core/admin/admin.{service,model}.ts` + `admin.service.spec.ts` | 4 | Snapshot-Typ + `getRateLimits()` |
| `web/src/app/features/admin/admin-monitoring-page.ts` | 4 | Abschnitt „Rate Limits" |
| `web/public/i18n/de.json`, `en.json` | 4 | `admin.rateLimits.*`-Schlüssel in beiden Sprachen |
| `docs/DECISIONS.md` | 2, 3, 4 | je ein Eintrag im Abschluss-Task des Schritts |

## Entscheidungen dieses Plans (wo die Spec Spielraum ließ)

1. **Die fünf bisher policy-freien Management-Mutationen** (`DELETE /{channelName}`,
   `DELETE /{channelName}/purge`, `POST` Vote-Session anlegen, `POST /{sessionId}/end`,
   `DELETE /{sessionId}`) **gehen auf `Bookkeeping`**: mutierende, DB-gebundene Aktionen ohne
   Provider-Kosten — dieselbe Charakteristik wie `join` und `sync-deleted`, und 120/min ist für
   Verwaltungsaktionen großzügig. Bewusst policy-frei bleiben: die `/api/admin`-Gruppe
   (Allowlist-gegated, interne Werkzeuge), `/api/auth` (ein Rate-Limit auf dem Login-Pfad kann
   Nutzer aussperren), `GET /api/worker/health` (der eigene 30-s-Dauerpoll der App) und die drei
   SSE-Endpunkte (Verbindungsgrenze liegt in `ILiveEventStream`). Jede Zeile wird im
   DECISIONS-Eintrag von Schritt 2 festgehalten.
2. **`EmoteSetOwnershipService` wird dritter Konsument des Listencaches.** Die Spec nennt nur
   `MyChannelsService` und `ModeratorCheckService`, verlangt aber die „einzige Quelle der
   vollständigen moderierten Twitch-Channels" — die Bestandsaufnahme fand mit
   `EmoteSetOwnershipService.CheckModeratedChannelsAsync` einen dritten Lader derselben Liste
   (`EmoteSetOwnershipService.cs:57-91`). Ihn auszulassen widerspräche der Spec-Absicht.
3. **Der Listencache übernimmt den TTL-Schlüssel `Auth:ModCheckCacheTtlMinutes`** (Default 10 min)
   statt eines neuen Schlüssels: er ist der Nachfolger des `modcheck`-Bool-Caches, und die
   Betriebs-Konfiguration soll beim Deploy nichts Neues lernen müssen.
4. **Alle fünf Policies lesen ihre Werte aus `RateLimitingOptions`** (Sektion `RateLimiting`),
   nicht nur die zwei neuen: der Admin-Snapshot (Schritt 4) soll die effektive Konfiguration je
   Policy aus einer Quelle zeigen, und das Abnahmekriterium „geänderte Environment-Konfiguration
   erscheint nach Neustart im Snapshot" gilt damit einheitlich. Defaults = heutige Werte
   (`Bookkeeping` 120/min, `ChannelResync` 5/min, `PublicHealth` 30/min als Fixed Window;
   `InteractiveRead` 300 Kapazität / 5 Tokens/s, `Voting` 120 / 2 Tokens/s als Token-Bucket).
5. **Ein Commit je Rollout-Schritt** (Spec-Vorgabe), Checkpoints statt Automatik — s. Global
   Constraints.

---

# Schritt 1 — Client-Verstärker entfernen (rein frontendseitig)

Kein Backend-Deploy, rückrollbar durch Rückkehr zum vorherigen Web-Bundle. Erwartete Wirkung
(Baseline → Ziel): Workspace-Einstieg 6 → 5 Permits, Erstnutzung nach Join −12 Requests im Worst
Case, Fehlergrund-Dauerstrom 4 → 2 Requests/min, Vote-Einstieg −1, vier schnelle Votes 13 → 5
API-Requests.

### Task 1: Set-Status vom Range-Load lösen

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts` (Load-Effect im Konstruktor
  `:599-602`, `load()` `:1040-1080`)
- Test: `web/e2e/usage-range-resolution.e2e.spec.ts` (Request-Recorder `:40-64`, Fälle `:80-110`)

**Vorab lesen:** Spec-Abschnitte „Design 1: Client-Verstärker zuerst entfernen → Usage-Workspace"
und „Warum es genau zwei `active-set`-Abrufe sind"; Baseline-Ablauf (a);
`web/.claude/CLAUDE.md` (Signals, Memberreihenfolge).

**Interfaces:**
- Consumes: bestehendes `load(channelName, from, to, rangeResolved)` und den Load-Effect.
- Produces: Verhalten, kein neuer API-Vertrag — spätere Tasks hängen nicht an Namen aus diesem Task.

**Absicht:** Heute holt `load()` bei jedem Effect-Lauf den Set-Status; die „all"-Auflösung
(`from`-Korrektur auf den Tracking-Start) läuft den Effect ein zweites Mal und kostet den zweiten
`active-set`-Request. Ziel: der Set-Status wird **einmal pro Channel-Wechsel** geladen, eine reine
Range-Änderung lädt nur `totals` und `series`.

**Grenzfälle, die erhalten bleiben müssen:**
- Der erste Lauf mit `rangeResolved === false` muss den Status **trotzdem** laden — die
  Statusantwort ist es, die die Range überhaupt auflöst (`trackedSince`).
- Ein Status-**Fehler** muss die Range weiterhin als „resolved" markieren (Fallback-Verhalten des
  bestehenden `error`-Zweigs), sonst hängt die Seite im Skeleton — der bestehende E2E-Fall „loads
  anyway when the set status fails" fixiert das.
- Manueller Refresh und `channel.synced` aktualisieren Status **und** Daten weiterhin (die
  bestehenden Pfade `refresh()`/`refreshSetStatus()` bleiben).
- Das `awaitSync`-Startkriterium (kein Set-Id **und** kein `syncFailureReason`) bleibt an den
  Status-Load des Channel-Wechsels gebunden.

- [ ] **Step 1: E2E-Harness erweitern und den fehlschlagenden Fall schreiben.** In
  `usage-range-resolution.e2e.spec.ts` den Request-Recorder um `active-set`-Zählung ergänzen und im
  bestehenden „asks once"-Fall zusätzlich behaupten: genau **ein** `active-set`-Request beim
  Einstieg mit „all"-Range. Die bestehende Einmal-Semantik für `totals`/`series` bleibt behauptet.
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-range-resolution.e2e.spec.ts`
  Expected: FAIL — heute sind es zwei `active-set`-Requests (Baseline (a), Zeilen 5 und 7).
- [ ] **Step 3: implementieren.** Statusabruf im Load-Pfad an den Channel binden (die Seite hält
  mit `setStatusChannel` bereits ein Signal, das den Channel des letzten Status kennt — geeigneter
  Anker). Range-Läufe überspringen den Statusabruf.
- [ ] **Step 4: grün laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-range-resolution.e2e.spec.ts`
  Expected: PASS, alle Fälle der Datei.

**Fertig-Bedingung:** `npm --prefix web run e2e -- usage-range-resolution.e2e.spec.ts` grün,
inklusive des neuen Ein-Request-Assertions; `npm --prefix web test -- --watch=false` unverändert
grün.

**Ausdrücklich nicht:** kein Zusammenlegen von `duplicate-names` und `active-set` (Spec: vertagt);
keine Änderung an `awaitSync` oder am Fehlergrund-Recheck (Tasks 2 und 3).

### Task 2: `awaitSync` eventgetrieben, höchstens drei Fallback-Probes

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts` (`awaitSync` `:1099-1128`,
  Konstanten `SYNC_POLL_INTERVAL_MS`/`SYNC_POLL_MAX_ATTEMPTS` `:122-123`)
- Test: `web/e2e/usage-atlas.e2e.spec.ts` (neuer Fall; bestehende Poll-Fälle anpassen)

**Vorab lesen:** Spec „Design 1 → Usage-Workspace" (zweiter Absatz); Baseline-Ablauf (e);
`web/src/app/core/live/live-reload.ts` (die `liveReload`/`liveEvents`-Bausteine und den
`installLiveStub`-Mechanismus in `web/e2e/support/mocks.ts`).

**Interfaces:**
- Consumes: den bestehenden `channel.synced`-Stream, den die Seite im Konstruktor bereits über
  `liveReload` abonniert (`usage-stats-page.ts:661-674`), und `refreshSetStatus()`.
- Produces: Verhalten; die Konstanten bekommen neue Namen/Werte nach Wahl des Implementers, dokumentiert im Konstantenkommentar.

**Absicht:** Der 2-s-Poll mit 15 Versuchen wird ersetzt: primärer Abschlussimpuls ist das
`channel.synced`-Event (die bestehende Konstruktor-Subscription lädt dann Status + Totals bereits
nach). `awaitSync` behält nur eine Absicherung gegen verlorene Events: **höchstens drei** zeitlich
auseinanderliegende Fallback-Probes innerhalb des bisherigen 30-Sekunden-Fensters (z. B. gestaffelt
früh/mittig/spät — genaue Staffelung ist Implementierungsspielraum, die Obergrenzen 3 und ~30 s
nicht). Ein erfolgreicher Probe (Set-Id da) lädt einmal die Totals; ein Probe mit
`syncFailureReason` beendet die Wartezeit sofort (bestehende Semantik). Der `isAwaitingSync`-Banner
endet spätestens mit der letzten Probe.

**Grenzfälle:**
- Channel-/Range-Wechsel bricht die Wartezeit ab (bestehendes `syncPoll?.unsubscribe()`-Muster —
  auch für die neue Staffelung sicherstellen, inkl. `destroyRef.onDestroy`).
- Fehlgeschlagene einzelne Probes zählen als „noch leer", beenden die Staffel nicht (bestehendes
  inneres `catchError`).
- Trifft das `channel.synced`-Event ein, dürfen **keine** weiteren Probes mehr laufen.

- [ ] **Step 1: E2E-Fall schreiben.** Neuer Fall in `usage-atlas.e2e.spec.ts`: `active-set` liefert
  dauerhaft weder Set-Id noch Grund, der Live-Stub bleibt stumm → über die 30-Sekunden-Spanne
  werden **höchstens 3** `active-set`-Requests nach dem initialen Load gezählt. Zweiter Fall:
  ein `channel.synced` über den Live-Stub beendet die Wartezeit, danach kommen keine Probes mehr.
  Bevorzugt mit Playwrights Clock-API (`page.clock`) statt echter Wartezeit; falls die sich mit
  der zoneless-App nicht verträgt, echte Wartezeit akzeptieren — die Suite trägt bereits zwei
  bewusste ~31-s-Fälle (s. CLAUDE.md, Tests-Abschnitt), das hier ergänzte Muster im Testkommentar
  begründen.
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts`
  Expected: FAIL — heute 15 Probes im 2-s-Takt (Baseline (e)).
- [ ] **Step 3: implementieren** (Absicht oben). Bestehende Poll-Fälle der Datei auf die neuen
  Intervalle/Obergrenzen anpassen.
- [ ] **Step 4: grün laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts`
  Expected: PASS.

**Fertig-Bedingung:** `usage-atlas.e2e.spec.ts` grün mit dem Höchstens-drei-Probes-Assert;
Baseline-Ablauf (e) sinkt rechnerisch von 21 auf ≤ 9 Permits.

**Ausdrücklich nicht:** die Wartezeit nicht unbegrenzt machen; das Startkriterium von `awaitSync`
nicht ändern; keine neuen Dauer-Controls in der UI (Memory: Frontend-Zurückhaltung).

### Task 3: Fehlergrund-Recheck auf höchstens einmal pro Minute

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts` (Recheck-Effect `:675-712`,
  Konstante `SYNC_FAILURE_RECHECK_INTERVAL_MS` `:135`)
- Test: `web/e2e/usage-atlas.e2e.spec.ts` (bestehende Recheck-Fälle ab ca. `:416-513`)

**Vorab lesen:** Spec „Design 1 → Usage-Workspace" (dritter Absatz) — insbesondere warum der
Fallback **nicht** ganz entfallen darf (eine erfolgreiche periodische Synchronisation kann den
Fehlergrund auch ohne Inventory-Änderung löschen, `SevenTvSyncService.cs:108-121`, und sendet dann
kein `channel.synced`); Baseline-Nebenbefund „der Fehlergrund-Recheck ist ein anderer Ablauf als (e)".

**Interfaces:** Consumes/Produces: nur die Konstante und der bestehende Effect; kein neuer Vertrag.

**Absicht:** Intervall von 30 s auf 60 s anheben — Dauerstrom bei sichtbarem Fehlergrund sinkt von
bis zu 4 auf bis zu 2 Requests/min (Status + ggf. Totals). `channel.synced` aktualisiert weiterhin
sofort; die Totals-Nachladung bei aufgelöster Set-Id bleibt.

- [ ] **Step 1: bestehende E2E-Fälle anpassen** (die Fälle, die den 30-s-Recheck abwarten — Suche
  nach dem Kommentar „The 30 s recheck"): auf das 60-s-Intervall umstellen und behaupten, dass im
  ersten 59-s-Fenster **kein** Recheck-Request läuft. Auch hier bevorzugt `page.clock`, sonst
  reale Wartezeit (Testlaufzeit wächst dann um ~30 s je Fall — im Testkommentar ausweisen, damit
  es nicht als Hänger gelesen wird).
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts`
  Expected: FAIL — der Recheck feuert heute bei +31 s (Baseline-Nebenbefund).
- [ ] **Step 3: Konstante umstellen**, Konstantenkommentar aktualisieren (er begründet das Polling
  — die Begründung bleibt gültig, nur der Takt ändert sich).
- [ ] **Step 4: grün laufen lassen.**
  Run: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts`
  Expected: PASS.

**Fertig-Bedingung:** `usage-atlas.e2e.spec.ts` grün; höchstens ein Status-Recheck pro Minute
belegt.

**Ausdrücklich nicht:** den Fallback entfernen (s. Spec-Begründung oben).

### Task 4: Guard-zu-Page-Handoff des Vote-Results

**Files:**
- Modify: `web/src/app/core/voting/vote-session.service.ts`,
  `web/src/app/core/voting/vote-session-access.guard.ts`,
  `web/src/app/features/voting/vote-session-detail-page.ts` (`loadResults` `:711-729`)
- Test: `web/src/app/core/voting/vote-session.service.spec.ts`,
  `web/src/app/core/voting/vote-session-access.guard.spec.ts`,
  `web/e2e/vote-ballot.e2e.spec.ts` (Requestzählung beim Einstieg)

**Vorab lesen:** Spec „Design 1 → Voting" (erster Absatz) und die Alternative „Mehr clientseitiges
Caching" (warum ausdrücklich **kein** dauerhafter Result-Cache); Baseline-Ablauf (c) — der doppelte
`results`-Abruf im Abstand von 582 ms.

**Interfaces:**
- Produces (für Task 5 und die Specs): zwei neue Methoden auf `VoteSessionService` — eine, mit der
  der Guard die erfolgreiche `VoteSessionResults`-Antwort samt `channelName`/`sessionId` ablegt,
  und eine, mit der die Page sie **genau einmal** abholt (Rückgabe `VoteSessionResults | null`;
  `null` bei Schlüssel-Mismatch oder bereits verbraucht). Namensvorschlag:
  `stashGuardResults(channelName, sessionId, results)` / `takeGuardResults(channelName, sessionId)`
  — verbindlich ist die Semantik, nicht der Name; die Specs und Task 5 verwenden, was hier entsteht.

**Absicht:** Der Guard verwirft seine erfolgreiche `/results`-Antwort heute (`map(() => true)`).
Künftig legt er sie im Service ab; der **erste** `loadResults`-Lauf der Page konsumiert sie statt
neu zu laden. Kein dauerhafter Cache: der abgelegte Wert wird bei Fehler des Guards nie
geschrieben, bei Abholung verbraucht, bei nicht passendem Schlüssel (andere Session/Channel)
ignoriert und überschrieben, und ein liegen gebliebener Wert wird beim nächsten `stash` ersetzt.
Alle späteren Reloads (Votes, SSE, Refresh) laden normal über HTTP.

**Grenzfälle:**
- Direktnavigation zwischen zwei Sessions desselben Channels: der Stash der ersten darf die zweite
  nicht bedienen (Schlüssel = Channel **und** SessionId).
- Guard-Fehler (403/404): nichts abgelegt, Page lädt normal — bestehendes Redirect-Verhalten des
  Guards unverändert.
- `freeze`-Semantik von `loadResults` (Reihenfolge einfrieren) muss für den konsumierten Wert
  genauso gelten wie für einen geladenen.

- [ ] **Step 1: Vitest-Fälle schreiben.** `vote-session.service.spec.ts`: Ablegen + einmaliges
  Abholen, zweites Abholen liefert `null`, Schlüssel-Mismatch liefert `null` **und verwirft** den
  liegenden Stash (definierte Semantik oben — er darf keine spätere fremde Abholung bedienen).
  `vote-session-access.guard.spec.ts`: erfolgreicher Guard legt ab; fehlschlagender legt nichts ab.
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web test -- --watch=false --include="src/app/core/voting/vote-session.service.spec.ts" --include="src/app/core/voting/vote-session-access.guard.spec.ts"`
  Expected: FAIL (Methoden existieren nicht).
- [ ] **Step 3: implementieren** (Service-Stash, Guard-Ablage, Page-Konsum im ersten
  `loadResults`).
- [ ] **Step 4: Vitest grün**, dann in `vote-ballot.e2e.spec.ts` (oder der dort passenden
  Einstiegs-Beschreibung) die Zählung ergänzen: der Seiteneinstieg erzeugt **genau einen**
  `/results`-Request, und mit ihr die volle Datei grün laufen lassen.
  Run: `npm --prefix web run e2e -- vote-ballot.e2e.spec.ts`
  Expected: PASS.

**Fertig-Bedingung:** die zwei Vitest-Dateien und `vote-ballot.e2e.spec.ts` grün; Einstieg =
1 `results`-Request (Baseline (c): heute 2).

**Ausdrücklich nicht:** kein Result-Cache über den einmaligen Handoff hinaus; der Guard bleibt
in seiner Autorisierungs-Semantik unverändert.

### Task 5: Vote-Erfolge in die gemeinsame Reload-Pipeline

**Files:**
- Modify: `web/src/app/features/voting/vote-session-detail-page.ts` (`vote()` `:602-634`,
  `load()`/`loadResults()`/`loadActiveEmoteSetId()` `:706-737`, Live-Pipeline `:355-380`)
- Test: `web/e2e/vote-ballot.e2e.spec.ts`

**Vorab lesen:** Spec „Design 1 → Voting" (zweiter Absatz); Baseline-Ablauf (d); den bestehenden
Kommentar über der `liveReload`-Subscription der Page (er beschreibt den heutigen Doppel-Reload als
„deliberate for now … tracked separately" — genau das wird hier behoben).

**Interfaces:**
- Consumes: die Handoff-Semantik aus Task 4 (der erste `loadResults`-Lauf konsumiert den Stash).
- Produces: Verhalten, kein neuer Vertrag.

**Absicht:** Lokale Vote-Erfolge und SSE-Echos speisen **dieselbe** 500-ms-Reload-Pipeline
(`VOTE_RELOAD_DEBOUNCE_MS`). Der lokale Erfolg triggert die Pipeline selbst — die Aktualisierung
darf nicht vom Redis-Publish/SSE-Echo abhängen (Spec-Vorgabe). Ein Vote lädt danach **nur**
Ergebnisse; der Channel-Status (`loadActiveEmoteSetId`) wird ausschließlich initial und bei
`channel.synced` geladen. Ergebnis für `n` schnelle Votes: `n` Mutationen + höchstens ein
Result-Reload (das SSE-Echo fällt in dieselbe Debounce-Spanne).

**Grenzfälle:**
- `refresh()` (manuell) und `onReloadRequested()` (nach Mass-Delete) laden weiterhin voll, sofort
  und inklusive Status — sie sind Nutzeraktionen, keine Echos.
- Der Fehlerpfad von `vote()` (`handleVoteError`) bleibt unangetastet; ein fehlgeschlagener Vote
  triggert keinen Reload.
- Retract (`DELETE …/votes/{emoteId}`) läuft durch denselben Pfad wie Cast.
- Die `freeze`-Semantik: Reloads aus der Pipeline laufen wie bisher mit `freeze: false`.

- [ ] **Step 1: E2E-Fall schreiben/erweitern.** In `vote-ballot.e2e.spec.ts`: vier schnelle Votes
  erzeugen **vier** Mutations-Requests, **höchstens einen** `/results`-Reload und **keinen**
  `GET /api/channels/{c}`-Status-Request pro Vote.
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web run e2e -- vote-ballot.e2e.spec.ts`
  Expected: FAIL — heute 4 direkte Reloads + 4 Status-Reads + 1 Echo-Reload (Baseline (d)).
- [ ] **Step 3: implementieren.** Den lokalen Erfolgs-Trigger in die bestehende
  Debounce-Pipeline einspeisen (z. B. indem der Erfolgspfad denselben Subject/Stream bedient, den
  `liveReload` speist — Mechanik ist Implementierungsspielraum; die Unabhängigkeit vom SSE-Stub im
  E2E-Test beweist die Spec-Vorgabe „nicht vom Redis-Publish abhängig"). Den überholten
  Doppel-Reload-Kommentar an der Pipeline korrigieren.
- [ ] **Step 4: grün laufen lassen.**
  Run: `npm --prefix web run e2e -- vote-ballot.e2e.spec.ts`
  Expected: PASS.

**Fertig-Bedingung:** `vote-ballot.e2e.spec.ts` grün mit den drei Zähl-Asserts; vier schnelle
Votes = 5 API-Requests statt 13.

**Ausdrücklich nicht:** kein Echo-Suppression-Umbau am SSE-Vertrag; `liveReload` selbst
(`core/live/live-reload.ts`) bleibt unverändert.

### Task 6: Schritt-1-Abschluss — Suiten, Live-Blick, Commit-Checkpoint

**Files:** keine neuen Änderungen; nur Verifikation und Formatierung.

- [ ] **Step 1: volle Frontend-Gates.**
  Run: `npm --prefix web test -- --watch=false` und `npm --prefix web run e2e` (vorher
  sicherstellen, dass auf `:5151` nichts lauscht) und `npm --prefix web run lint`.
  Expected: alles grün (Stand 2026-08-29: 91 E2E-Tests, ~2 min; nach Task 2/3 ggf. länger, wenn
  ohne `page.clock` gearbeitet wurde).
- [ ] **Step 2: Live-Gegenprobe im Browser** (Regel 16 sinngemäß, hier UI): Api + `npm start`
  nach CLAUDE.md starten, im Network-Tab die Baseline-Abläufe (a), (c), (d) nachfahren und die
  Zielzahlen aus der Schritt-1-Präambel bestätigen (5 Permits Einstieg, 1 Result-Read Einstieg,
  5 Requests für vier Votes). Abweichungen sind Befunde, keine Rundungsfehler.
- [ ] **Step 3: formatieren.** `npm --prefix web run format`.
- [ ] **Step 4: COMMIT-CHECKPOINT (Regel 1).** Dem Nutzer den Diff-Umfang und die gemessenen
  Vorher/Nachher-Zahlen nennen und **fragen**, ob committet werden darf. Vorgeschlagene Message:
  `fix(web): stop client-side request amplification behind local 429s (#33)` — ein Commit für den
  ganzen Schritt (Spec-Vorgabe). Kein DECISIONS-Eintrag nötig (kein Vertrag geändert).

---

# Schritt 2 — `InteractiveRead` + `Voting` einführen, `ExternalApi` entfernen

**Eine Einheit, ein Deploy, ein Commit** — kein Zwischenstand, in dem zwei Policies um dieselben
Routen konkurrieren. Rückrollbar durch Wiederherstellung der alten Policy-Zuordnung.

### Task 7: Die zwei Abnahmetests zuerst — heute rot

**Files:**
- Create: `tests/EmotePurge.Api.Tests/RateLimitPolicyBudgetTests.cs`

**Vorab lesen:** `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs` (Partition über einen
Test-eigenen Principal, `ApiFactory`-Substitutionen) und `ApiFactory.cs`;
Baseline-Abschnitte (b) und „Abweichung von der Spec"; Spec-Tabelle „Abnahmekriterien und Harness".

**Interfaces:**
- Consumes: `ApiFactory` (echte `Program.cs`-Pipeline, Test-Authentifizierung mit setzbarer
  Twitch-User-ID als Partitionsschlüssel).
- Produces: zwei rote Tests, die Task 9 grün macht — **ohne** Bezug auf noch nicht existierende
  Policy-Namen (sie messen nur Antwort-Statuscodes).

**Absicht:** Beide korrigierten Abnahmekriterien als `WebApplicationFactory`-Tests, nach dem Muster
von `RateLimitRejectionTests`:

1. **Zwölf Rundgänge in einer Minute, keine lokale 429** (Schwellen-Begründung s. oben, mit
   Verweis auf das Baseline-Dokument im Testkommentar): die Requestfolge eines Rundgangs exakt aus
   Baseline (b) übernehmen (`permissions`, `duplicate-names`, 2× `active-set`, `totals`, `series`,
   Rückweg `mine`), zwölfmal hintereinander unter **einer** Partition senden (~74 zählende
   Requests) und behaupten, dass **keine** Antwort den Status 429 trägt. Handler-Fehler (500er aus
   nicht substituierten Services) sind ausdrücklich toleriert und werden nicht behauptet — gezählt
   wird das Permit, das die Middleware **vor** dem Handler verbraucht; genau deshalb ist der Test
   gegen die echten Routen aussagekräftig, obwohl `ApiFactory` nur vier Services substituiert.
2. **100 Vote-Mutationen in einer Session ohne lokale 429; eine andere Session bleibt
   unbeeinflusst**: 100× `POST /api/channels/{c}/vote-sessions/1/votes` unter einer Partition —
   keine 429; unmittelbar danach ein Request desselben Nutzers gegen Session 2 und gegen eine
   Navigations-Route — ebenfalls keine 429 (das prüft die Partition `TwitchUserId + SessionId`
   und dass Voting die Navigation nicht leert).

- [ ] **Step 1: beide Tests schreiben** (Testnamen z. B.
  `TwelveWorkspaceRoundTripsInOneMinute_ProduceNoLocal429` und
  `HundredVoteMutationsInOneSession_ProduceNoLocal429_AndLeaveOtherTrafficUntouched`).
- [ ] **Step 2: rot laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter "FullyQualifiedName~RateLimitPolicyBudgetTests"`
  Expected: FAIL — heute liefert `ExternalApi` ab dem 41. Request der Minute 429
  (Baseline: fünf 429er ab Rundgang sieben).

**Fertig-Bedingung:** beide Tests existieren, kompilieren und schlagen mit beobachteten 429ern
fehl (nicht mit Setup-Fehlern).

**Ausdrücklich nicht:** noch keine Produktivcode-Änderung; keine Policy-Namen raten.

### Task 8: Policy-Bausteine — Options, Token-Bucket-Partitionierung, Validierung

**Files:**
- Create: `src/EmotePurge.Api/RateLimiting/RateLimitingOptions.cs`,
  `src/EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs`
- Modify: `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs`,
  `src/EmotePurge.Api/Program.cs` (Rate-Limiter-Block `:95-140`),
  `src/EmotePurge.Api/appsettings.json` (neue `RateLimiting`-Sektion mit den Defaults)
- Test: `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs` (anpassen)

**Vorab lesen:** Spec „Design 2" vollständig; `RateLimitRejection.cs` (Items-Keys,
Retry-After-Herleitung, Log-Vertrag); `RateLimitRejectionTests.cs`.

**Interfaces:**
- Produces (Task 9 und Schritt 4 hängen daran):
  - `RateLimitPolicyNames` mit den Konstanten `InteractiveRead`, `Voting`, `Bookkeeping`,
    `ChannelResync`, `PublicHealth` (die Endpoints referenzieren künftig die Konstanten statt
    String-Literale — das ist es, was Tests und Snapshot teilen).
  - `RateLimitingOptions` (Config-Sektion `RateLimiting`): je Policy ein Unterobjekt; für
    `InteractiveRead`/`Voting` Token-Bucket-Form (`TokenLimit`, `TokensPerPeriod`,
    `ReplenishmentPeriodSeconds`), für die drei bestehenden Fixed-Window-Form (`PermitLimit`;
    Fenster bleibt die gemeinsame 1-min-Konstante). Defaults: `InteractiveRead` 300 / 5 / 1;
    `Voting` 120 / 2 / 1; `Bookkeeping` 120; `ChannelResync` 5; `PublicHealth` 30. Environment
    überschreibt per `RateLimiting__InteractiveRead__TokenLimit` usw.; Änderungen wirken nur per
    Neustart, es gibt keinen Write-Endpoint.
  - Zwei neue Partitionierer in `RateLimitRejection` neben `PartitionPerUser`:
    ein Token-Bucket pro Twitch-User-ID (IP-Fallback wie bisher) und einer mit Partition
    `TwitchUserId + SessionId` für `Voting` (SessionId aus `HttpContext.Request.RouteValues`
    lesen; fehlt der Route-Wert wider Erwarten, auf den User-only-Schlüssel zurückfallen statt zu
    werfen). Beide schreiben dieselben `HttpContext.Items`-Schlüssel wie bisher, damit
    `OnRejectedAsync` Policy und Partition weiterhin benennen kann. `QueueLimit` fest 0,
    `AutoReplenishment` an.
- Consumes: nichts aus anderen Tasks.

**Absicht und Grenzfälle:**
- **Validierung beim Start, fail fast:** Kapazität, Tokens je Periode und Replenishment-Periode
  müssen > 0 sein (`PermitLimit` ebenso); Verstoß wirft beim Boot mit deutscher Message —
  dasselbe Fail-Fast-Muster wie der Migration-Guard, kein stiller Fallback.
- **Retry-After beim Token-Bucket:** die `RetryAfter`-Metadata einer abgelehnten Lease kann fehlen
  oder null Sekunden melden; Fallback ist die aufgerundete Replenishment-Periode, Minimum 1 s —
  der bestehende Vertrag „nie 0, Header immer gesetzt" bleibt. Der bisherige Fallback
  `RateLimitRejection.Window` gilt weiter für die Fixed-Window-Policies.
- Die fünf **Registrierungen in `Program.cs`** lesen alle Werte aus den Options; der große
  Kommentarblock dort wird neu geschrieben (Task 9 Schritt „Kommentare", s. dort — die inhaltliche
  Korrektur der falschen User-Token-Behauptung gehört zu diesem Schritt-2-Commit).
- In diesem Task werden **noch keine Routen umgehängt** — `ExternalApi` existiert nach Task 8
  vorübergehend weiter (Working-Tree-Zwischenstand, der nie deployt wird; der Commit kommt erst
  nach Task 9/10).
- `RateLimitRejectionTests` umbauen: statt des gespiegelten `ExternalApiPermitLimit = 40` setzt der
  Test-Host per Konfigurations-Override (z. B. `RateLimiting:InteractiveRead:TokenLimit` klein,
  etwa 3, ohne Nachfüllspielraum im Testfenster) ein billig erschöpfbares Budget und prüft den
  unveränderten 429-Vertrag: Retry-After ≥ 1, `errorCode = rate_limit_exceeded`,
  `retryAfterSeconds` konsistent, Warn-Log mit Policy/Route/Partition. Damit ist zugleich früh
  belegt, dass Environment-Konfiguration die Policies wirklich steuert.

- [ ] **Step 1: `RateLimitRejectionTests` auf das Options-Override-Muster umschreiben** (rot, weil
  Options/Namen noch fehlen).
- [ ] **Step 2: Options, Namen, Partitionierer, Validierung, Registrierung implementieren.**
- [ ] **Step 3: grün laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter "FullyQualifiedName~RateLimitRejectionTests"`
  Expected: PASS. `RateLimitPolicyBudgetTests` bleibt rot (Routen hängen noch auf `ExternalApi`).

**Fertig-Bedingung:** `RateLimitRejectionTests` grün über die neuen Bausteine; `dotnet build
EmotePurge.slnx` sauber.

**Ausdrücklich nicht:** keine Route anfassen; keine Telemetrie (Schritt 4); kein neuer Fehlercode.

### Task 9: Alle Routen umhängen, `ExternalApi` restlos entfernen

**Files:**
- Modify: `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs`, `VoteSessionEndpoints.cs`,
  `EmoteEndpoints.cs`, `UsageStatsEndpoints.cs`; `src/EmotePurge.Api/Program.cs`
  (Kommentarblock der Policies, Entfernen der `ExternalApi`-Registrierung)
- Test: `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `ApiFactory.cs` (nur falls nötig),
  `RateLimitPolicyBudgetTests.cs` (wird grün)

**Vorab lesen:** Spec „Design 2 → Vollständiges Inventar vor dem Umhängen" — die Ist-Tabelle und
die Liste der heute policy-freien Routen; Abschnitt „Entscheidungen dieses Plans" (Nr. 1) oben.

**Interfaces:**
- Consumes: `RateLimitPolicyNames` und die Partitionierer aus Task 8.
- Produces: die endgültige Policy-Zuordnung (Solltabelle unten) — Schritt 4 zeigt sie im Snapshot.

**Solltabelle (vollständig — jede Zeile wird umgesetzt und keine weitere Route angefasst):**

| Route | heute | neu |
|---|---|---|
| `GET /{channelName}/permissions` (`ChannelEndpoints.cs:101`) | ExternalApi | InteractiveRead |
| `GET /mine` (`ChannelEndpoints.cs:121`) | ExternalApi | InteractiveRead |
| `POST /{channelName}/join` (`ChannelEndpoints.cs:139`) | ExternalApi | **Bookkeeping** (Spec: bereits auf 7TV/Twitch ausgeführte Aktionen dürfen lokal nicht verloren gehen) |
| Vote-Session-Liste `GET` (`VoteSessionEndpoints.cs:158`) | ExternalApi | InteractiveRead |
| `GET /{sessionId}/results` (`VoteSessionEndpoints.cs:183`) | ExternalApi | InteractiveRead |
| `POST /{sessionId}/votes` (`VoteSessionEndpoints.cs:225`) | ExternalApi | **Voting** |
| `DELETE /{sessionId}/votes/{emoteId}` (`VoteSessionEndpoints.cs:256`) | ExternalApi | **Voting** (Retract darf nicht unter InteractiveRead fallen — Spec) |
| Emote-Gruppe, Gruppenebene (`EmoteEndpoints.cs:24`) | ExternalApi | InteractiveRead (die Endpoint-eigenen `Bookkeeping`-Zuordnungen von `sync-deleted`/`sync-restored` `:58/:94` bleiben und überstimmen die Gruppe weiterhin) |
| Usage-Stats-Gruppe, Gruppenebene (`UsageStatsEndpoints.cs:20`) | ExternalApi | InteractiveRead |
| `GET /{channelName}` (`ChannelEndpoints.cs:19`) | — | **InteractiveRead** (erstmals eine Policy — Spec; der Rollenfilter kann bei Cache-Miss Helix erreichen) |
| `GET /api/vote-sessions/mine` (`VoteSessionEndpoints.cs:260`) | — | **InteractiveRead** (ein Read wie die übrigen Vote-Listen — Spec) |
| `DELETE /{channelName}`, `DELETE /{channelName}/purge` (`ChannelEndpoints.cs:197/222`) | — | **Bookkeeping** (Planentscheidung Nr. 1) |
| `POST` Vote-Session anlegen, `POST /{sessionId}/end`, `DELETE /{sessionId}` (`VoteSessionEndpoints.cs:21/57/88`) | — | **Bookkeeping** (Planentscheidung Nr. 1) |
| `/{channelName}/audit-log` (`ChannelEndpoints.cs:64`) | Bookkeeping | bleibt |
| `/{channelName}/resync` (`ChannelEndpoints.cs:195`) | ChannelResync | bleibt (inkl. per-Channel-Cooldown) |
| `GET /api/health` (`WorkerHealthEndpoints.cs:63`) | PublicHealth | bleibt |
| `/api/admin`-Gruppe, `/api/auth`, `GET /api/worker/health`, drei SSE-Endpunkte | — | **bewusst policy-frei** (Begründungen: Planentscheidung Nr. 1; im Code an der jeweiligen MapGroup mit einem Satz begründen) |

**Kommentarkorrekturen in demselben Task** (Spec „Dokumentation und Commitgrenzen"):
- Der `Program.cs`-Kommentarblock über den Policies: die falsche Behauptung eines für alle
  Moderatoren gemeinsamen Twitch-User-Token-Budgets streichen (Spec „Extern verifiziert":
  User-Access-Buckets sind pro Client-ID **und Nutzer**); neue Begründung: lokale
  Missbrauchsgrenzen, keine Providerbudgets.
- Die Gruppen-Kommentare an `EmoteEndpoints.cs:19-24` und `UsageStatsEndpoints.cs:15-20`
  (behaupten ungecachte 7TV-/Helix-Kosten für DB-Reads) sachlich richtigstellen.
- **Nicht anfassen:** der Kommentar an `/channels/mine` (`ChannelEndpoints.cs:117-120`, „ungecacht
  und teuer") — er stimmt bis Schritt 3 und wird **dort** aktualisiert.

- [ ] **Step 1: Routen nach Solltabelle umhängen**, `ExternalApi`-Registrierung und -Konstante
  entfernen, Kommentare korrigieren.
- [ ] **Step 2: das Entfernen belegen.**
  Run: `grep -rn "ExternalApi" src/EmotePurge.Api/`
  Expected: **kein Treffer.** Zusätzlich `grep -rn "RequireRateLimiting" src/EmotePurge.Api/`
  gegen die Solltabelle abgleichen — jede Ist-Zeile der Spec-Tabelle hat einen Nachfolger.
- [ ] **Step 3: Abnahmetests grün.**
  Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj`
  Expected: PASS — inklusive `RateLimitPolicyBudgetTests` (vorher rot, Task 7) und
  `AuthFilterMatrixTests` (die bool-401/403-Verträge sind von der Policy-Zuordnung unberührt;
  schlägt hier etwas fehl, ist das ein Befund am Umhängen, nicht am Test).

**Fertig-Bedingung:** der `grep` ist leer, die volle Api-Testsuite grün.

**Ausdrücklich nicht:** keine Tri-State-Zeilen in der Filter-Matrix (eigener Slice); keine
SSE-/Auth-Policies; `ILiveEventStream`-Verbindungsgrenzen unangetastet.

### Task 10: Schritt-2-Abschluss — DECISIONS, Live-Verifikation, Commit-Checkpoint

**Files:**
- Modify: `docs/DECISIONS.md` (neuer Eintrag, oben, mit `**Betrifft:**`-Zeile)

- [ ] **Step 1: DECISIONS-Eintrag schreiben** (deutsch): Policy-Topologie neu — `InteractiveRead`
  (300, 5/s, per User) und `Voting` (120, 2/s, per User+Session) als Token-Buckets ersetzen
  `ExternalApi`; vollständige Zuordnungstabelle inkl. der bewusst policy-freien Routen und der
  fünf neu auf `Bookkeeping` gehängten Management-Mutationen; Begründung „lokale
  Missbrauchsgrenze statt Provider-Surrogat" mit Verweis auf Spec und Baseline.
  `**Betrifft:**` nennt mindestens `Program.cs`, die vier Endpoint-Dateien und
  `RateLimiting/RateLimitingOptions.cs`.
- [ ] **Step 2: volle Gates.**
  Run: `dotnet test EmotePurge.slnx` (Docker läuft) und `npm --prefix web test -- --watch=false`.
  Expected: grün. Kein E2E-Lauf nötig (keine UI-Änderung in diesem Schritt).
- [ ] **Step 3: Live-Verifikation (Regel 16).** Lokal `docker compose up -d postgres redis`,
  `dotnet run --project src/EmotePurge.Api`, `npm --prefix web start`, echter Twitch-Login. Dann:
  (a) mit Env-Override `RateLimiting__InteractiveRead__TokenLimit=3` (Neustart) einen echten
  429 provozieren und Warn-Log + Retry-After + Frontend-Fehlertext prüfen — das belegt den
  Ablehnungspfad und die Env-Wirkung live; (b) Override entfernen, Neustart, und die
  12-Rundgänge-Bedienung aus der Baseline im Browser dicht nachfahren: **keine** 429 im
  Network-Tab und keine `Rate-Limit erreicht`-Zeile im Log.
- [ ] **Step 4: formatieren** (`dotnet format EmotePurge.slnx`, ggf. `npm --prefix web run format`).
- [ ] **Step 5: COMMIT-CHECKPOINT (Regel 1).** Nutzerfreigabe einholen. Vorgeschlagene Message:
  `feat(api): replace ExternalApi with InteractiveRead and Voting token buckets (#33)` — ein
  Commit, DECISIONS-Eintrag enthalten (Regel 3).

---

# Schritt 3 — Gemeinsamer Moderated-Channels-Cache

Rückrollbar, weil Redis nur abgeleitete TTL-Daten hält; die Live-Paginierung bleibt der Miss-Pfad.

### Task 11: `ModeratedChannelsProvider` — Listencache mit Single-Flight

**Files:**
- Create: `src/EmotePurge.Core/Services/IModeratedChannelsProvider.cs`,
  `src/EmotePurge.Infrastructure/Services/ModeratedChannelsProvider.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/ModeratedChannelsProviderTests.cs`
- Modify: `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs`
  (`FetchModeratedChannelsAsync` `:52-99`),
  `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (Registrierung)

**Vorab lesen:** Spec „Design 3" vollständig; `MyChannelsService.cs:26-62`,
`ModeratorCheckService.cs`, `ModRoleCache.cs` (Key-Muster, TTL-Herleitung aus
`Auth:ModCheckCacheTtlMinutes`, `InvalidateUserAsync`); als Single-Flight-Vorbilder
`TwitchTokenRefreshGate.cs` und `ChannelSyncGate.cs`; `Fixtures/RedisFixture.cs` und ein
bestehender Integrationstest mit Redis (z. B. `ModRoleCacheTests.cs`).

**Interfaces:**
- Produces (Task 12 hängt daran):
  - Core: `IModeratedChannelsProvider` mit genau einer Methode
    `GetModeratedChannelsAsync(TwitchPrincipalInfo principal, CancellationToken ct)`, Rückgabe
    ein Ergebnis-Record `ModeratedChannelsLookup` (im selben Core-File) mit
    `IReadOnlyList<TwitchModeratedChannelInfo>? Channels` (null = nicht ermittelbar: kein Token,
    Helix-Fehler, unvollständige Pagination) und `bool ReauthRequired` (durchgereicht vom
    Token-Service; bei Cache-Hit `false`, s. Grenzfälle). BCL-only, `TwitchModeratedChannelInfo`
    existiert bereits in Core.
  - Infrastructure: `ModeratedChannelsProvider` (nimmt `ITwitchUserTokenService`,
    `ITwitchHelixClient`, `IConnectionMultiplexer`, `IConfiguration`, Logger), registriert in
    `AddEmotePurgeInfrastructure`.
- Consumes: bestehende `ITwitchHelixClient.GetModeratedChannelsAsync` (Login **und**
  Broadcaster-ID — die ID-tragende Variante, nicht die Login-only).

**Absicht und Verträge:**
- **Redis-Key** `modlist:{twitchUserId}`, Inhalt: JSON-Liste der Einträge (normalisierter Login +
  immutable Twitch-Channel-ID), **TTL aus `Auth:ModCheckCacheTtlMinutes`** (Default 10 min —
  Planentscheidung Nr. 3).
- **Nur eine vollständige erfolgreiche Pagination wird gecacht.** Timeout, 429, 5xx, Tokenfehler
  und unvollständige Pagination schreiben keinen Eintrag; der Aufrufer bekommt dann
  `Channels = null` und entscheidet selbst (heutige Semantik der Konsumenten bleibt: transient
  verweigern, nicht cachen). Dafür muss `TwitchHelixClient.FetchModeratedChannelsAsync` einen
  nach `MaxModeratedChannelPages` (10) **noch vorhandenen Cursor** als Fehler (null) melden statt
  still ein Teilergebnis zu liefern — heute unerreichbar unterhalb von 1000 moderierten Channels,
  aber die Cache-Korrektheit darf nicht an einer stillen Abschneidung hängen.
- **In-Process-Single-Flight pro Twitch-User:** gleichzeitige Misses desselben Nutzers führen zu
  genau einer Pagination; nach Eintritt ins Gate wird Redis **erneut** geprüft (Double-Check).
  Muster: `TwitchTokenRefreshGate`. Kein verteilter Lock (eine Api-Replica — bekannte Grenze,
  analog Token-Refresh).
- **Ein leeres Ergebnis ist gültig** (Nutzer moderiert nichts) und wird gecacht — leere Liste und
  „nicht ermittelbar" dürfen im Cache-Payload nicht zusammenfallen.
- **Redis-Ausfall beim Lesen/Schreiben** ist ein Miss bzw. ein verlorener Schreib — strukturiert
  deutsch loggen, live weiterarbeiten (der Miss-Pfad ist die Wahrheit).
- **Cache-Hit prüft kein Token** und liefert `ReauthRequired = false`: ein kaputtes Refresh-Token
  fällt damit erst beim nächsten Miss (≤ TTL) oder über die stündliche Live-Validierung benutzter
  Tokens auf — akzeptierte Staleness, im Code-Kommentar festhalten.

- [ ] **Step 1: Integrationstests schreiben** (`Integration/`, echte Redis via `RedisFixture`,
  Helix + Token-Service per NSubstitute an der Grenze — dasselbe Muster wie bestehende
  Integrationstests, die nur Redis echt brauchen): (a) Miss → eine Pagination, Eintrag liegt in
  Redis, Folge-Call innerhalb TTL → **kein** Helix-Aufruf; (b) zwei parallele Misses → genau eine
  Pagination (Zählung am Substitute); (c) Helix-null/Tokenfehler → kein Redis-Eintrag, nächster
  Call versucht live erneut; (d) leere Liste wird gecacht und als leer (nicht null) gelesen;
  (e) Löschen des Keys → nächster Call paginiert wieder.
- [ ] **Step 2: rot laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ModeratedChannelsProviderTests"`
  Expected: FAIL (Typ existiert nicht).
- [ ] **Step 3: implementieren** (inkl. Helix-Client-Korrektur und Registrierung).
- [ ] **Step 4: grün laufen lassen.** Gleicher Befehl, Expected: PASS.

**Fertig-Bedingung:** `ModeratedChannelsProviderTests` grün; `CoreAssemblyReferenceTests`
weiterhin grün (Core blieb BCL-only).

**Ausdrücklich nicht:** kein Browser-/HTTP-Cache für `/mine` (Spec lehnt das ab — Live-Events
sollen frische Zustände zeigen); keine Konsumenten-Umstellung (Task 12); die separate
App-Token-Auflösung ungetrackter 7TV-Grants bleibt unberührt.

### Task 12: Konsumenten umstellen, Bool-Moderatorcache abschaffen

**Files:**
- Modify: `src/EmotePurge.Infrastructure/Services/MyChannelsService.cs`,
  `ModeratorCheckService.cs`, `EmoteSetOwnershipService.cs`;
  `src/EmotePurge.Core/Services/IModRoleCache.cs` und
  `src/EmotePurge.Infrastructure/Redis/ModRoleCache.cs` (Moderator-Bool-Methoden entfernen,
  `InvalidateUserAsync` erweitern); `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs:117-120`
  (den „ungecacht und teuer"-Kommentar an `/mine` jetzt aktualisieren)
- Test: `tests/EmotePurge.Infrastructure.Tests/Unit/ModeratorCheckServiceTests.cs`,
  `Integration/{MyChannelsServiceTests,ModRoleCacheTests,EmoteSetOwnershipServiceTests}.cs`

**Vorab lesen:** Spec „Design 3" (insbesondere: „Der alte Bool-Moderatorcache wird in diesem
Schritt vollständig abgelöst; es gibt keinen read-only Übergang ohne Schreiber"); Planentscheidung
Nr. 2 (dritter Konsument); die vier genannten Testdateien.

**Interfaces:**
- Consumes: `IModeratedChannelsProvider`/`ModeratedChannelsLookup` aus Task 11.
- Produces: keine neuen — Verträge der Konsumenten nach außen bleiben identisch (`/mine`-Antwort,
  `IsModeratorAsync`-bool, Ownership-DTO).

**Absicht:**
- `MyChannelsService`: eigener Token-Abruf + `GetModeratedChannelLoginsAsync` entfallen; ein
  Provider-Call liefert Logins (+ IDs) und `ReauthRequired`; `helixUnavailable` ⇔
  `Channels == null`. Verhalten der Antwort ansonsten unverändert.
- `ModeratorCheckService`: Cache-Lookup + Token + Helix + Bool-Cache-Write entfallen; ein
  Provider-Call, Mitgliedschaft per normalisiertem Login; `Channels == null` → verweigern ohne
  Cache (heutige Transient-Semantik).
- `EmoteSetOwnershipService.CheckModeratedChannelsAsync`: Provider statt eigenem
  Token+`GetModeratedChannelsAsync`-Paar.
- `IModRoleCache`/`ModRoleCache`: `TryGetIsModeratorAsync`/`SetIsModeratorAsync` ersatzlos
  entfernen (7TV-Grants und Subscriber-Bool bleiben). `InvalidateUserAsync` löscht zusätzlich
  `modlist:{twitchUserId}`; der `modcheck:*`-SCAN entfällt (es schreibt niemand mehr solche Keys;
  Alt-Keys laufen per TTL ≤ 10 min aus — im Kommentar festhalten). Der Admin-Invalidate-Pfad
  (`AdminEndpoints.cs:341`, „invalidate-role-cache") deckt damit die Spec-Anforderung „Listencache
  zusammen mit 7TV-Grant- und Subscriber-Caches" ohne API-Änderung ab.

- [ ] **Step 1: Tests anpassen/ergänzen.** `ModeratorCheckServiceTests` (Unit): Substitute des
  Providers statt Helix/Token/Cache; Fälle: Mitglied/Nichtmitglied/`null`→deny-ohne-Cache.
  `MyChannelsServiceTests`: Provider-Substitute; `helixUnavailable`- und `reauthRequired`-Pfade.
  `ModRoleCacheTests`: Invalidate löscht `modlist`-Key mit; Moderator-Bool-Fälle entfernen.
  `EmoteSetOwnershipServiceTests`: Provider-Substitute.
- [ ] **Step 2: rot laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj`
  Expected: FAIL in den vier angepassten Dateien.
- [ ] **Step 3: implementieren.**
- [ ] **Step 4: grün laufen lassen.** Gleicher Befehl, Expected: PASS — die ganze
  Infrastructure-Suite, nicht nur die vier Dateien (die Umstellung darf keine Nachbarn brechen).

**Fertig-Bedingung:** volle Infrastructure-Suite grün; `grep -rn "TryGetIsModeratorAsync"
src/` liefert keinen Treffer.

**Ausdrücklich nicht:** kein neuer Admin-Endpoint; keine Zähler (Schritt 4); Subscriber- und
7TV-Grant-Caches unverändert.

### Task 13: Schritt-3-Abschluss — DECISIONS, Live-Verifikation, Commit-Checkpoint

- [ ] **Step 1: DECISIONS-Eintrag** (deutsch): Cache-Topologie — `modlist:{twitchUserId}` als
  gemeinsamer Redis-Listencache (TTL `Auth:ModCheckCacheTtlMinutes`), In-Process-Single-Flight,
  Bool-Moderatorcache abgeschafft, drei Konsumenten (inkl. Begründung, warum
  `EmoteSetOwnershipService` dazukam), Invalidate-Erweiterung. `**Betrifft:**`-Zeile mit den
  geänderten Dateien.
- [ ] **Step 2: volle Gates.** `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`.
  Kein E2E nötig (keine UI-Änderung).
- [ ] **Step 3: Live-Verifikation (Regel 16, Spec-Kriterium „mit echten Zugängen"):** Api lokal
  mit echtem Twitch-Login; `Microsoft.AspNetCore`/HttpClient-Logging vorübergehend auf
  `Information` (nach der Probe zurücknehmen, nicht committen — Muster aus der Baseline). Ablauf:
  (a) erste Overview lädt `/mine` → im Log genau **eine** Helix-Paginierung; (b) sofortige zweite
  Overview + ein `permissions`-Check → **kein** Helix-Aufruf; (c) Admin-UI „Rollen-Cache
  invalidieren" für den eigenen Nutzer → nächste Overview paginiert wieder. Zusätzlich einmal die
  Nutzung mit dem realen Mod-Konto (HandOfBlood-Anwendungsfall) gegenprüfen, wenn verfügbar.
- [ ] **Step 4: formatieren.** `dotnet format EmotePurge.slnx`.
- [ ] **Step 5: COMMIT-CHECKPOINT (Regel 1).** Nutzerfreigabe einholen. Vorgeschlagene Message:
  `feat(infrastructure): share one cached moderated-channels list across role checks (#33)`.

---

# Schritt 4 — Beobachtbarkeit + Admin-Ansicht (read-only)

Telemetrie ist fail-open und kann ohne Änderung des Produktpfads rückgerollt werden. Kein
Write-Endpoint, keine Reservierung, kein Observe-/Enforce-Schalter.

### Task 14: Telemetrie-Verträge und Redis-Store

**Files:**
- Create: `src/EmotePurge.Core/Services/IRateLimitTelemetry.cs` (Schreibseite + die
  Record-DTOs), `src/EmotePurge.Core/Services/IRateLimitTelemetryReader.cs` (Leseseite +
  Snapshot-DTOs), `src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Integration/RateLimitTelemetryStoreTests.cs`
- Modify: `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs`

**Vorab lesen:** Spec „Design 4 → Erfassung und Ausfallverhalten" und „Angezeigte Daten";
`Redis/WorkerHealthReader.cs` und `Redis/TwitchLiveStatusStore.cs` als Muster für kleine
Redis-Stores; `CoreAssemblyReferenceTests` (Core bleibt BCL-only).

**Interfaces:**
- Produces (Tasks 15/16 hängen daran):
  - `IRateLimitTelemetry` (Schreibseite, fire-and-forget-tauglich): drei Methoden —
    lokale Policy-Entscheidung erfassen (Policy-Name, akzeptiert/abgelehnt, HTTP-Methode,
    Route-Template, Partition, Retry-After bei Ablehnung), Provider-Antwort erfassen
    (Provider-Name, Call-Source, Statuscode, Retry-After, optionale
    `Ratelimit-Limit/-Remaining/-Reset`-Stichprobe als Strings), Cache-Lookup erfassen
    (Cache-Name aus einer festen Namensliste, Hit/Miss). Alle Dimensionen sind **stabile Namen**,
    nie rohe URLs (Spec).
  - `IRateLimitTelemetryReader`: eine Methode, die den vollständigen Zähler-Snapshot liefert —
    je Policy akzeptiert/abgelehnt für letzte Minute und letzte 24 h, letzte lokale Ablehnung
    (Zeitpunkt, Methode, Route-Template, Policy, Partition, Retry-After), Cache-Hits/-Misses je
    Cache, je Provider Requests/echte 429/letzter Retry-After/letzter Zeitpunkt/Header-Stichprobe,
    plus ein `TelemetryAvailable`-Flag: bei Redis-Ausfall kommt ein Snapshot mit
    `TelemetryAvailable = false` und leeren Zählern zurück, **keine Exception**.
- **Speicherform:** kleine Redis-Zeit-Buckets (z. B. Minuten-Hashes mit >24-h-TTL, deren Summen
  der Reader über die Fenster aggregiert) plus je ein Letzt-Vorfall-Eintrag; genaue Key-Form ist
  Implementierungsspielraum, die Dimensionen und TTL-Pflicht nicht. Schreibfehler werden deutsch
  strukturiert geloggt und schlucken nie eine Exception in den Produktpfad (fail-open — Spec).

- [ ] **Step 1: Integrationstests schreiben** (Redis-Fixture): Zähler inkrementieren und über den
  Reader korrekt aggregiert zurücklesen (Minute vs. 24 h); Letzt-Ablehnung überschreibt sich;
  Provider-429 und Header-Stichprobe landen; Cache-Zähler je Name getrennt; **Ausfallpfad**: ein
  Store gegen einen nicht erreichbaren Multiplexer (eigene Verbindung mit `abortConnect=false`
  gegen einen geschlossenen Port) schreibt ohne Exception und liest
  `TelemetryAvailable = false`.
- [ ] **Step 2: rot laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Infrastructure.Tests/EmotePurge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~RateLimitTelemetryStoreTests"`
  Expected: FAIL.
- [ ] **Step 3: implementieren + registrieren.**
- [ ] **Step 4: grün laufen lassen.** Gleicher Befehl, Expected: PASS; `CoreAssemblyReferenceTests`
  weiter grün.

**Fertig-Bedingung:** `RateLimitTelemetryStoreTests` grün inkl. Ausfallpfad.

**Ausdrücklich nicht:** keine Reservierung, keine Vorab-Ablehnung, kein Budget-Zustand — reine
Zählung (Spec „Bewusst vertagt").

### Task 15: Erfassung anschließen — Middleware, Ablehnungs-Markierung, Provider-Handler, Cache-Zähler

**Files:**
- Create: `src/EmotePurge.Api/RateLimiting/RateLimitTelemetryMiddleware.cs`,
  `src/EmotePurge.Infrastructure/Telemetry/ProviderRequestTelemetryHandler.cs`
- Modify: `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs` (Ablehnungs-Markierung),
  `src/EmotePurge.Api/Program.cs` (Middleware-Registrierung um `UseRateLimiter`, `:243`),
  `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (Handler an die drei
  `AddHttpClient`-Registrierungen `:62-84`),
  `src/EmotePurge.Infrastructure/Services/ModeratedChannelsProvider.cs`,
  `SevenTvEditorService.cs` und die Subscriber-Cache-Lesestelle (per
  `grep -rn "TryGetIsSubscriberAsync" src/` lokalisieren) — je ein Hit/Miss-Zähleraufruf
- Test: `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs` (erweitern) bzw. ein neuer
  gezielter Fall in `RateLimitPolicyBudgetTests.cs`; Unit-Test für den Handler in
  `tests/EmotePurge.Infrastructure.Tests/Unit/ProviderRequestTelemetryHandlerTests.cs`

**Vorab lesen:** Spec „Design 4 → Erfassung und Ausfallverhalten" — insbesondere: „Fachliche 429er
wie der Resync-Cooldown werden nicht als Policy-Verstoß gezählt" und „ein um `UseRateLimiter`
liegender Telemetriepfad unterscheidet akzeptierte Requests von der explizit markierten lokalen
Ablehnung"; die `HttpContext.Items`-Keys in `RateLimitRejection.cs`.

**Interfaces:**
- Consumes: `IRateLimitTelemetry` (Task 14), `RateLimitPolicyNames` (Task 8).
- Produces: die Call-Source-Namen als Konstanten (z. B. `twitch-helix`, `twitch-auth`,
  `seventv-rest`) — Task 16/17 zeigen sie an; Ort: bei den Telemetrie-Verträgen in Core, damit
  Reader und Anzeige dieselben Namen teilen.

**Absicht und Grenzfälle:**
- **Middleware:** registriert **vor** `app.UseRateLimiter()`, misst nach Rücklauf der inneren
  Pipeline. Nur Requests, deren `HttpContext.Items` einen Policy-Namen tragen (der Partitionierer
  schreibt ihn), werden gezählt — policy-freie Routen (SSE, Worker-Health, Admin, Auth) erzeugen
  keine Zählerbewegung (Spec kennzeichnet sie in der UI als Lücke). Eine Ablehnung zählt nur,
  wenn `OnRejectedAsync` sie explizit markiert hat (neuer Items-Schlüssel) — ein 429 aus dem
  Resync-Cooldown-Handler trägt die Markierung nicht und zählt als akzeptierter Request der
  Policy. `OnRejectedAsync` meldet zusätzlich die Letzt-Ablehnungs-Details (Route-**Template**
  aus dem Endpoint, nicht der rohe Pfad — keine rohen URLs als Dimension).
- **Provider-Handler:** ein `DelegatingHandler`, je Client mit seiner Call-Source registriert;
  erfasst jede Antwort (Zählung), echte 429er samt `Retry-After`, und bei Twitch die
  `Ratelimit-*`-Header als Stichprobe („zuletzt beobachtet", ausdrücklich nicht autoritativ —
  Spec). Wirft nie selbst; Telemetriefehler fallen in das fail-open des Stores.
- **Cache-Zähler:** die drei Lesestellen melden Hit/Miss unter den Namen
  `moderated-channels`, `seventv-grants`, `subscriber-check`.

- [ ] **Step 1: Tests schreiben.** Api-Test: unter kleinem Override-Budget einen akzeptierten und
  einen abgelehnten Request fahren und am (per DI substituierten) `IRateLimitTelemetry`
  verifizieren: 1× akzeptiert, 1× abgelehnt, Resync-Cooldown-429 (falls ohne echten Kanal
  simulierbar: mindestens per Unit-Semantik der Markierung) **nicht** als Ablehnung.
  Handler-Unit-Test: gefakter innerer Handler liefert 200/429 mit Headern → korrekte Meldungen am
  Telemetrie-Substitute, keine Exception bei kaputtem Telemetrie-Sink.
- [ ] **Step 2: rot laufen lassen** (`dotnet test` mit passenden Filtern). Expected: FAIL.
- [ ] **Step 3: implementieren und verdrahten.**
- [ ] **Step 4: grün laufen lassen.**
  Run: `dotnet test EmotePurge.slnx`
  Expected: PASS — volle Backend-Suite (die Handler hängen jetzt in jedem HttpClient-Aufbau).

**Fertig-Bedingung:** Suite grün; ein manueller `curl`-Doppelblick lokal zeigt nach ein paar
Requests wachsende Zähler in Redis (`redis-cli --scan --pattern '*ratelimit*'` o. ä. — Key-Muster
je nach Implementierung).

**Ausdrücklich nicht:** die browserseitigen 7TV-Calls der Mass-Delete-Engine
(`seven-tv-run-engine.ts`) werden **nicht** erfasst — sie laufen am Server vorbei; ihre
Kennzeichnung als Lücke ist Aufgabe der UI (Task 17), nicht der Erfassung.

### Task 16: `GET /api/admin/rate-limits` — der read-only Snapshot

**Files:**
- Modify: `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs` (neuer `MapGet` in der bestehenden
  `/api/admin`-Gruppe `:24-26`)
- Test: `tests/EmotePurge.Api.Tests/` — neue Datei `AdminRateLimitsEndpointTests.cs`; eine Zeile
  in `AuthFilterMatrixTests.cs` (der Endpoint erbt den `GlobalAdminAuthorizationFilter` — 401/403
  wie die übrigen Admin-Routen)

**Vorab lesen:** Spec „Design 4 → Angezeigte Daten"; `AdminEndpoints.cs:24-60` (Gruppenaufbau,
`/health` als Muster eines Reader-Endpoints); `ApiFactory.cs` (wie Settings/Services im Test-Host
überschrieben werden).

**Interfaces:**
- Consumes: `IRateLimitTelemetryReader` (Task 14), `IOptions<RateLimitingOptions>` +
  `RateLimitPolicyNames` (Task 8).
- Produces (Task 17 spiegelt es): der JSON-Vertrag des Snapshots — ein Objekt mit
  `telemetryAvailable` (bool), `policies` (je Policy: Name, Typ Token-Bucket/Fixed-Window,
  Kapazität bzw. Permit-Limit, Nachfüllrate/Fenster, Partitionsbeschreibung als stabiler String,
  `queueLimit`), `counters` (je Policy akzeptiert/abgelehnt, letzte Minute und 24 h),
  `lastLocalRejection` (nullable), `caches` (je Cache Hits/Misses), `providers` (je Provider und
  Call-Source: Requests, echte 429, letzter Retry-After, letzter Zeitpunkt, Twitch-Header-
  Stichprobe; **kein Prozentwert für 7TV** — es gibt keinen belastbaren Nenner, Spec). Die
  effektive Konfiguration kommt aus den Options und ist auch bei `telemetryAvailable = false`
  vollständig enthalten (partielle `200`, nie 5xx wegen Redis).
- **Handler-Regeln:** injiziert nur die zwei Verträge — kein `AppDbContext`, kein
  `IConnectionMultiplexer` (Regel 4); kein schreibender Gegenpart; getrennt von `/admin/health`.

- [ ] **Step 1: Endpoint-Tests schreiben:** (a) 200-Vertrag mit substituiertem Reader (Zähler und
  Konfiguration erscheinen); (b) Reader meldet Ausfall → `200` mit `telemetryAvailable: false`
  **und** weiterhin vollständiger `policies`-Konfiguration; (c) **Env-Kriterium:** Test-Host mit
  überschriebenem `RateLimiting:InteractiveRead:TokenLimit` → der Snapshot zeigt den
  überschriebenen Wert (deckt „geänderte Environment-Konfiguration erscheint nach Neustart im
  Snapshot"); (d) Matrix-Zeile: ohne Login 401, ohne Admin-Allowlist 403.
- [ ] **Step 2: rot laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj --filter "FullyQualifiedName~AdminRateLimitsEndpointTests"`
  Expected: FAIL (404 — Route existiert nicht).
- [ ] **Step 3: implementieren.**
- [ ] **Step 4: grün laufen lassen.**
  Run: `dotnet test tests/EmotePurge.Api.Tests/EmotePurge.Api.Tests.csproj`
  Expected: PASS inkl. Filter-Matrix.

**Fertig-Bedingung:** Api-Suite grün; `curl` gegen die lokal laufende Api liefert als Admin den
Snapshot, als Nicht-Admin 403.

**Ausdrücklich nicht:** kein Write-Endpoint, keine Konfigurations-Mutation, kein neuer
API-Fehlercode.

### Task 17: Admin-UI-Abschnitt „Rate Limits"

**Files:**
- Modify: `web/src/app/core/admin/admin.model.ts` (Snapshot-Typen),
  `web/src/app/core/admin/admin.service.ts` (`getRateLimits()` → `GET /api/admin/rate-limits`),
  `web/src/app/core/admin/admin.service.spec.ts`,
  `web/src/app/features/admin/admin-monitoring-page.ts` (neuer Abschnitt),
  `web/public/i18n/de.json` und `web/public/i18n/en.json` (`admin.rateLimits.*`)

**Vorab lesen:** Spec „Design 4 → Angezeigte Daten" (die vollständige Anzeigeliste ist der
Abnahmemaßstab dieses Tasks); `admin-monitoring-page.ts` (Sektionsmuster, `rxResource`,
Refresh-Knopf, Skeletons); `docs/UI-Designsprache.md` („Neue UI bauen"-Checkliste) und
`DESIGN.md`; `web/.claude/CLAUDE.md` (Memberreihenfolge, i18n-Pflichten).

**Interfaces:**
- Consumes: den JSON-Vertrag aus Task 16, 1:1 als TypeScript-Typen gespiegelt.
- Produces: nichts für spätere Tasks.

**Absicht:** Ein Abschnitt auf der bestehenden Monitoring-Seite, im Stil der vorhandenen
Sektionen (`<section>` mit `border-t`, `<dl>`-Raster). Er zeigt **alle** Punkte der
Spec-Anzeigeliste: effektive Konfiguration je Policy; akzeptiert/abgelehnt je Policy (Minute /
24 h); letzte lokale Ablehnung; Cache-Hits/-Misses der drei Caches; Provider-429er je Provider
und Call-Source mit Retry-After und letztem Zeitpunkt; die Twitch-Header-Stichprobe (ausdrücklich
als Stichprobe beschriftet); 7TV ohne Prozentwert. Dazu zwei statische Hinweistexte (übersetzt,
beide Sprachen): (1) Mass-Delete/Restore rufen 7TV direkt aus dem Browser auf und fehlen in den
serverseitigen 7TV-Zahlen; (2) policy-freie SSE-Reconnects und der Worker-Health-Poll erscheinen
nicht in den Policy-Zählern. Bei `telemetryAvailable: false` zeigt der Abschnitt die
Konfiguration und einen Degraded-Hinweis statt Zahlen. Aktualisierung: beim Öffnen, manuell über
den vorhandenen Refresh-Mechanismus der Seite und alle 30 s (dem bestehenden Takt der Seite
folgen — keine neue eigene Poll-Mechanik erfinden).

- [ ] **Step 1: Vitest für den Service schreiben** (`admin.service.spec.ts`,
  `HttpTestingController`): URL, Methode, Typ-Durchreichung — Muster der Nachbarmethoden.
- [ ] **Step 2: rot laufen lassen.**
  Run: `npm --prefix web test -- --watch=false --include="src/app/core/admin/admin.service.spec.ts"`
  Expected: FAIL.
- [ ] **Step 3: Service + Modelle + UI-Abschnitt + i18n implementieren.** Beide Locale-Dateien im
  selben Zug (Regel 7 sinngemäß für UI-Texte).
- [ ] **Step 4: grün + Sichtprüfung.**
  Run: `npm --prefix web test -- --watch=false` und `npm --prefix web run lint`
  Expected: PASS. Danach Playwright-/Browser-**Sichtprüfung** (Spec-Harness): Abschnitt rendert in
  beiden Sprachen und beiden Themes, die zwei Lücken-Hinweise sind sichtbar, keine
  AXE-/Kontrastverstöße nach Designsprache-Checkliste.

**Fertig-Bedingung:** Vitest + Lint grün; Sichtprüfung dokumentiert (Screenshot oder
Kurzbefund an den Orchestrator).

**Ausdrücklich nicht:** kein neuer Dauer-Control außerhalb des bestehenden Seiten-Refreshs
(Memory: Frontend-Zurückhaltung); keine eigene Admin-Unterseite; keine Schreib-Aktionen.

### Task 18: Schritt-4-Abschluss — DECISIONS, Live-Verifikation, Commit-Checkpoint

- [ ] **Step 1: DECISIONS-Eintrag** (deutsch): Monitoring-Topologie — Redis-Zeit-Buckets
  (fail-open, TTL), Erfassungspunkte (Middleware um `UseRateLimiter`, `DelegatingHandler` an den
  drei Provider-Clients, drei Cache-Lesestellen), read-only Endpoint `GET /api/admin/rate-limits`,
  ausdrücklich keine Reservierung/kein Enforce. `**Betrifft:**`-Zeile mit den neuen Dateien.
- [ ] **Step 2: volle Gates.** `dotnet test EmotePurge.slnx`,
  `npm --prefix web test -- --watch=false`, **und** `npm --prefix web run e2e` (UI wurde geändert;
  vorher `:5151` freimachen).
- [ ] **Step 3: Live-Verifikation (Regel 16):** lokal mit echtem Login: (a) einige Minuten normale
  Bedienung → Admin-Abschnitt zeigt wachsende akzeptierte Zähler und Cache-Hits; (b) mit dem
  kleinen Env-Override aus Task 10 eine Ablehnung provozieren → sie erscheint als „letzte lokale
  Ablehnung"; (c) `docker compose stop redis` → `GET /api/admin/rate-limits` liefert `200` mit
  `telemetryAvailable: false` und die Admin-Seite zeigt den Degraded-Zustand, danach
  `docker compose start redis`; (d) einmal `/mine` mit vielen Channels → Twitch-Header-Stichprobe
  gefüllt. Helix-Header und echte Zugänge decken das letzte Spec-Kriterium („Helix-Paginierung,
  Cacheinvalidierung und Headererfassung funktionieren mit echten Zugängen") zusammen mit
  Task 13 ab.
- [ ] **Step 4: formatieren.** `dotnet format EmotePurge.slnx`, `npm --prefix web run format`.
- [ ] **Step 5: COMMIT-CHECKPOINT (Regel 1).** Nutzerfreigabe einholen. Vorgeschlagene Message:
  `feat(admin): observe local rate limits, cache effect and real provider 429s (#33, #35)`.

---

## Abnahmekriterien → Task-Zuordnung

Die Harness-Zuordnung ist unverändert aus der Spec übernommen; einzig die Rundgang-Schwelle ist
korrigiert (s. Abschnitt „Korrektur eines Abnahmekriteriums").

| Kriterium (Spec, ggf. korrigiert) | Harness (Spec) | Task |
|---|---|---|
| **Zwölf** vollständige Rundgänge mit Rückkehr in einer Minute erzeugen keine lokale 429 *(korrigiert von sechs — Baseline: sechs sind heute schon grün)* | `WebApplicationFactory` in `tests/EmotePurge.Api.Tests`, Muster `RateLimitRejectionTests` | 7 (rot) → 9 (grün) |
| 100 Vote-Mutationen in einer Session ohne lokale 429; andere Session unbeeinflusst | `WebApplicationFactory`, echte Policy-Metadaten, Test-Auth | 7 (rot) → 9 (grün) |
| All-Time-Auflösung lädt `active-set` nur einmal; `totals`/`series` je einmal | Playwright-Requestzählung `usage-range-resolution.e2e.spec.ts` | 1 |
| Fehlender Set-Status: höchstens drei Fallback-Probes in 30 s | Playwright mit gemocktem `active-set` + Live-Stub | 2 |
| Sichtbarer Fehlergrund: höchstens ein Status-Recheck pro Minute | Playwright `usage-atlas.e2e.spec.ts` | 3 |
| Vier schnelle Votes: vier Mutationen, ≤ 1 Result-Reload, kein Status-Read pro Vote | Playwright `vote-ballot.e2e.spec.ts` | 5 |
| Guard und Vote-Page teilen beim Einstieg genau einen Result-Read | Guard-Vitest + Playwright-Requestzählung | 4 |
| Cache-Hit ohne Helix; parallele Misses → eine Pagination; Teilfehler nicht gecacht | `tests/EmotePurge.Infrastructure.Tests`, Testcontainers/Fakes an der HTTP-Grenze | 11 |
| Admin-Snapshot: Config, lokale Ablehnung, Cachezähler, Provider-429; Redis-Ausfall → partielles `200` | Infrastructure-Test Redis-Buckets + `EmotePurge.Api.Tests` Endpointvertrag | 14 + 16 |
| Geänderte Environment-Konfiguration erscheint nach Neustart im Snapshot | `WebApplicationFactory` mit überschriebenen Settings | 16 (früh belegt schon in 8) |
| Browserseitige 7TV-Calls als Monitoring-Lücke sichtbar | Admin-UI-Vitest + Playwright-Sichtprüfung | 17 |
| Helix-Paginierung, Cacheinvalidierung, Headererfassung mit echten Zugängen | Live-Verifikation nach Regel 16 | 13 + 18 |

## Betroffene Bestandstests → Task-Zuordnung

| Bestandstest (Spec-Liste + Bestandsaufnahme) | Task |
|---|---|
| `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs` | 8 |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `ApiFactory.cs` | 9 (16 ergänzt eine Zeile) |
| `web/e2e/usage-range-resolution.e2e.spec.ts` | 1 |
| `web/e2e/usage-atlas.e2e.spec.ts` | 2, 3 |
| `web/e2e/vote-ballot.e2e.spec.ts` | 4, 5 |
| `web/src/app/core/voting/vote-session-access.guard.spec.ts`, `vote-session.service.spec.ts` | 4 |
| `web/src/app/core/admin/admin.service.spec.ts` | 17 |
| *(Bestandsaufnahme, in der Spec-Liste nicht genannt)* `Unit/ModeratorCheckServiceTests.cs`, `Integration/{MyChannelsServiceTests,ModRoleCacheTests,EmoteSetOwnershipServiceTests}.cs` | 12 |

## Selbstprüfung (durchgeführt beim Schreiben dieses Plans)

- **Spec-Deckung:** Alle vier Designs haben Tasks (D1 → 1–5, D2 → 7–9, D3 → 11–12, D4 → 14–17);
  jedes Spec-Abnahmekriterium und jeder Bestandstest ist oben zugeordnet; die
  Kommentarkorrekturen aus „Dokumentation und Commitgrenzen" stecken in Task 9 (Program.cs,
  Gruppen-Kommentare) und Task 12 (`/mine`-Kommentar, erst mit dem Cache). Die Spec-Vorgaben
  „join → Bookkeeping ist ein Wechsel", „Retract nicht unter InteractiveRead", „kein Zwischenstand
  mit zwei Policies" und „kein neuer Fehlercode" sind je in Task 9/9/8–10/Global verankert.
- **Keine Platzhalter:** Jeder Task nennt Dateien, Verträge, Grenzfälle und eine prüfbare
  Fertig-Bedingung; wo Spielraum bleibt (Probe-Staffelung, Redis-Key-Form, Methodennamen des
  Handoffs), ist er als solcher benannt und begrenzt.
- **Namenskonsistenz:** `IModeratedChannelsProvider`/`ModeratedChannelsLookup` (11 → 12),
  `RateLimitPolicyNames`/`RateLimitingOptions` (8 → 9, 15, 16), `IRateLimitTelemetry`/
  `IRateLimitTelemetryReader` (14 → 15, 16), `stashGuardResults`/`takeGuardResults` (4 → 5,
  ausdrücklich als Vorschlag markiert, Semantik verbindlich).
