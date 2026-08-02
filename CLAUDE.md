# CLAUDE.md

Schneller Einstieg für Claude Code. Zwei Dokumente daneben, beide verbindlich:

- **[docs/Architectur.md](docs/Architectur.md)** — die vollständige Spezifikation (Module A–D, DB-Schema, Docker-Topologie, Kommunikationsfluss). **Bei Architektur-Fragen zuerst dort lesen.**
- **[docs/DECISIONS.md](docs/DECISIONS.md)** — die vollständige Historie aller Architektur-/Infrastruktur-Entscheidungen, absteigend nach Datum, jeder Eintrag mit `**Betrifft:**`-Zeile. Durchsuchbar per `grep <dateiname> docs/DECISIONS.md`. **„Warum ist X so gebaut?"** steht dort, nicht hier.

## Projekt-Überblick

Emote Purge: plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch. .NET 10, Clean/Layered Architecture, PostgreSQL (EF Core) als Single Source of Truth, Redis Pub/Sub zur Entkopplung von API und Worker, Angular 22 als Frontend. Produktion läuft auf `emotepurge.app` (VPS, GHCR-Images + Portainer-Stack).

## Umsetzungsstand

| Modul | Inhalt | Stand |
|---|---|---|
| **1 / A** | Chat-Analytics & Live-Synchronisation: Channel-Join/Leave über Redis, anonymes Twitch-IRC, hybrider 7TV-Sync (EventAPI-WebSocket-Live-Deltas hinter Feature-Flag + periodischer REST-Vollsync als Reconciliation), Chat-Matching + 30-Sekunden-Batch-Flush, Worker-Health | ✅ vollständig |
| **B** | Twitch-OAuth-Login (Cookie-Session, kein JWT), Live-Rollenprüfung (Admin-Allowlist / Broadcaster / Live-Moderator / 7TV-Editor), serverseitig wirksames Logout | ✅ vollständig |
| **C** | Voting Engine: `VoteSession`/`Vote`, rollengesteuerte Abstimmung, Beliebtheits-Score aus normalisierter Chat-Nutzung + Keep/Delete-Votes | ✅ vollständig |
| **D** | Angular-Frontend: Login, Übersicht, Usage-Stats-Grid, Voting-UI, 7TV-Mass-Delete-Engine, i18n (de/en), Pagination | ✅ vollständig |
| **Admin** | Globaler Admin-Bereich (`/admin/*`, Allowlist `Auth:AdminTwitchLogins`): Monitoring, Channel-Liste inkl. Resync/Purge, Nutzerliste inkl. Session-Revoke und Rollen-Cache-Invalidierung, Audit-Log (`AuditLogEntry`), eigener SSE-Stream | ✅ vollständig |
| **E** | Launch-Vorbereitung: Ressourcenlimits, Container-Healthchecks (S3-35), Log-Aggregation/Alerting (S3-36), Rechtstexte | ⬜ offen — **Monitoring selbst ist gebaut** (Admin-Bereich + `GET /api/admin/health` + `WorkerHealthPublisher`); offen ist die Infrastruktur drumherum |
| **Review 2026-07-29** | 81 Befunde; Wellen A–D umgesetzt (D am 2026-08-02: Autorisierungs-Tests, Api-Filter-Matrix, Struktur-Test), nur E (Infra & Launch) offen — s. [Review-2026-07-29-Umsetzung.md](docs/Review-2026-07-29-Umsetzung.md) | 🟡 laufend |
| **Review 2026-08-01** | Struktur, Formatter-Tooling, Fremd-Wartbarkeit — s. [Review-2026-08-01-Struktur-und-Wartbarkeit.md](docs/Review-2026-08-01-Struktur-und-Wartbarkeit.md) | 🟡 laufend |

## Commands

### Build

```
dotnet build EmotePurge.slnx                              # ganze Solution
dotnet build src/EmotePurge.Api/EmotePurge.Api.csproj      # einzelnes Projekt
```

Solution-Datei ist `EmotePurge.slnx` (neues .NET-10-SDK-Format), keine `.sln`. `dotnet build`/`dotnet sln` funktionieren identisch damit.

### Lokal ausführen (ohne Docker)

```
dotnet run --project src/EmotePurge.Api
dotnet run --project src/EmotePurge.Worker
```

Erwartet Postgres/Redis erreichbar über die in `appsettings.json` hinterlegten `localhost`-Connection-Strings (Default-Credentials matchen `.env.example`: `emotepurge`/`change-me`), z. B. via `docker compose up postgres redis`. Api lauscht lokal per `launchSettings.json` auf `http://localhost:5151` (nicht `8080` — das gilt nur im Container). Login zuerst im Browser: `http://localhost:5151/api/auth/twitch/login` — `join`/`leave` erfordern eine authentifizierte Session.

### Frontend (Angular) lokal ausführen

```
npm --prefix web install     # einmalig, oder via .devcontainer postCreateCommand
npm --prefix web start       # ng serve mit Dev-Proxy (web/proxy.conf.json, /api -> :5151)
```

Erwartet die Api parallel laufend per `dotnet run --project src/EmotePurge.Api` (Port `5151`) — **nicht** die VS-Code-`Api`-Launch-Config, die hart auf `:8080` bindet und damit den lokal registrierten Twitch-OAuth-Redirect (`http://localhost:5151/api/auth/twitch/callback`) bricht. `ng serve` läuft dann auf `http://localhost:4200`.

### Tests

```
dotnet test EmotePurge.slnx                # Backend, braucht laufendes Docker (Testcontainers)
npm --prefix web test -- --watch=false     # Frontend Unit (Vitest)
npm --prefix web run e2e                   # Frontend E2E (Playwright, /api/** gemockt)
```

Drei Backend-Testprojekte (xUnit):

- **`tests/EmotePurge.Infrastructure.Tests`** deckt `EmotePurge.Infrastructure` ab — Integrationstests laufen per Testcontainers gegen echte, ephemere Postgres-/Redis-Container (kein Mocking von `AppDbContext`/`IConnectionMultiplexer`), reine Logik-Tests (z. B. `EmoteMatchCache`, `SevenTvDispatchParser`, `ChannelAccessService`) ohne Container. Ausschlaggebend für `Unit/` vs. `Integration/` ist allein, ob die Klasse unter Test echte Infrastruktur berührt — `VoteEligibilityService` und `MyChannelsService` nehmen `AppDbContext` und liegen deshalb trotz reiner Entscheidungslogik in `Integration/`.
- **`tests/EmotePurge.Worker.Tests`** ist bewusst container-frei und testet die puren Entscheidungs-/Zustandsklassen der Worker-Transporte (`ReconnectPolicy`, `SevenTvSubscriptionRegistry`, `SevenTvBackoffPolicy`, `EmoteUsageCounter`).
- **`tests/EmotePurge.Api.Tests`** (seit 2026-08-02) fährt per `WebApplicationFactory` die echte `Program.cs`-Pipeline und prüft die Endpoint-Filter-Matrix (401/403/404/409 plus den 400-Vertrag der Namensvalidierung) an den echten Routen. Ebenfalls container-frei: substituiert werden nur `IChannelAccessService`, `IVoteEligibilityService`, `IChannelService` und `IConnectionMultiplexer` — Letzteres, weil `RequestDelegateFactory` die Handler-Services **vor** der Filter-Pipeline auflöst, ein abgelehnter Request also trotzdem den ganzen Service-Graph konstruiert.

### EF Core Migrationen

`dotnet-ef` ist als globales Tool installiert. Migrationen liegen in `EmotePurge.Infrastructure/Migrations/`, Startprojekt für die Connection-String-Auflösung ist `EmotePurge.Api`:

```
dotnet ef migrations add <Name> --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

#### Prod-Migration (manuell, über SSH-Tunnel)

Migrationen laufen in Produktion **nicht** automatisch beim App-Start — sie werden von Hand nachgezogen, **bevor** die neuen Images deployt werden (additive Migrationen ignoriert das noch laufende alte Image; umgekehrt liefe die neue Api gegen fehlende Spalten). Prod-Postgres ist auf dem VPS nur an `127.0.0.1:5433` gebunden (`docker-compose.prod.yml`), also erst tunneln:

```
ssh -N -L 15432:127.0.0.1:5433 <VPS-USER>@<VPS-HOST>
```

Dann lokal, in einer zweiten Shell. `--connection` statt einer Umgebungsvariable, damit nichts für spätere lokale Läufe hängen bleibt; in PowerShell **einfache** Anführungszeichen, sonst interpoliert die Shell ein `$` im Passwort:

```
dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
```

Erst `list` (zeigt, was `(Pending)` ist — mehr als erwartet heißt: Prod hängt mehrere Feature-Runden zurück, dann vorher die Migrationen durchsehen), dann `update`, dann `list` zur Gegenprobe. Das Passwort steht in der `.env` auf dem VPS und gehört nirgends ins Repo (Regel 17).

### Docker Compose (voller Stack)

```
docker compose up -d --build      # redis, postgres, api, worker
docker compose logs -f api
docker compose down                # -v zum Löschen des Postgres-Volumes
```

Konfiguration über `.env` am Repo-Root (Vorlage: `.env.example`). Der `api`-Build enthält eine `web-build`-Stage (Node), die `web/` baut und das Ergebnis nach `wwwroot/` kopiert — die Angular-App wird direkt von der Api unter `http://localhost:8080/` mitausgeliefert, kein eigener Frontend-Service/Port nötig. Produktion läuft über `docker-compose.prod.yml` (GHCR-Images, s. Architectur.md Abschnitt 6b).

### Dev Container Debugging (VS Code)

"Reopen in Container" startet einen SDK-Container plus `postgres`/`redis` im selben Compose-Netzwerk (`api`/`worker` aus `docker-compose.yml` werden dabei **nicht** gestartet). Danach F5 mit Launch-Config `Api`, `Worker` oder Compound `Api + Worker` — läuft direkt über den .NET-Debugger, nicht als vorgebautes Image.

### Backup

`scripts/backup-postgres.sh` plus [docs/Backup-und-Restore.md](docs/Backup-und-Restore.md) — dort steht auch, was auf dem VPS einmalig einzurichten ist.

## Architektur

Vier .NET-Projekte plus ein Angular-Workspace. Verweise: `Api`/`Worker` → `Infrastructure` → `Core`.

- **`EmotePurge.Core`** — Entitäten, Service-**Interfaces**, Messaging-Abstraktion (`IRedisPublisher`/`IRedisSubscriber`), 7TV-/Twitch-DTOs. Hat bewusst **keine** Abhängigkeit auf EF Core, StackExchange.Redis, HTTP-Clients oder ASP.NET Core: die Redis-Interfaces arbeiten mit reinen `string`-Signaturen, die DTOs sind reine `record`s.
- **`EmotePurge.Infrastructure`** — `AppDbContext` (Npgsql), Redis-Implementierungen, alle Service-Implementierungen, die typisierten 7TV-/Twitch-`HttpClient`s, und `ServiceCollectionExtensions.AddEmotePurgeInfrastructure(configuration)` als **einziger** DI-Registrierungspunkt (Api und Worker rufen nur diese Methode auf).
- **`EmotePurge.Api`** — ASP.NET Core **Minimal API**, keine Controllers. `Program.cs` ist reines Bootstrapping; die Endpoints liegen nach Domäne getrennt in `Endpoints/*.cs` (je eine `Map<X>Endpoints(this WebApplication app)`-Extension-Method, registriert per `MapGroup`). Autorisierung läuft über `IEndpointFilter`-Klassen in `Auth/`, Validierung über `Validation/`. Hört im Container auf `:8080`, lokal auf `:5151`.
- **`EmotePurge.Worker`** — .NET Worker Service mit sieben Hosted Services: `Worker` (Boot-Recovery + Redis-Kommandos), `UsageFlushWorker`, `SevenTvPeriodicResyncWorker`, `SevenTvEventWorker` (7TV-EventAPI-WebSocket, Feature-Flag `SevenTv:EventApi:Enabled`), `TwitchConnectionWatchdog`, `WorkerHealthPublisher`, `WorkerRosterPublisher` (Per-Channel-Roster nach `worker:roster`, 60-s-Takt). `TwitchChatManager` kapselt genau **einen** langlebigen `TwitchLib.Client` für die gesamte Worker-Lebensdauer — **nie** pro Channel/Join neu instanziieren; die Reconnect-Entscheidung liegt TwitchLib-frei in `ReconnectPolicy`. Analog kapselt `SevenTvEventClient` genau **eine** EventAPI-Verbindung; dessen Subscription-Zustand liegt in `SevenTvSubscriptionRegistry` (Desired-State-first), das Reconnect-Pacing in `SevenTvBackoffPolicy` — beide pur und getestet.
- **`web/`** — Angular 22, Standalone Components + Signals (kein NgModule), Tailwind CSS, `@angular/cdk` fürs Virtual Scrolling, Transloco für i18n. `core/` = injectable Services, Guards, Models · `shared/` = wiederverwendbare Bausteine · `features/` = geroutete Seiten. Auth ist cookie-basiert wie das Backend, kein Token im Frontend. Details in [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md).

Connection-Strings/Redis-Config kommen aus `appsettings.json` (`ConnectionStrings:DefaultConnection`, `Redis:ConnectionString`), in Docker per Environment-Variablen (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) überschrieben. Der 7TV-Endpunkt (`https://7tv.io/v3/`) ist fest im Code verdrahtet — externer Drittanbieter-URL, kein Umgebungsunterschied wie bei DB/Redis.

### Schichtentreue

Verbindlich, und beim Review vom 2026-07-29 über alle `using`-Direktiven geprüft:

| Schicht | Erlaubt | Verboten |
|---|---|---|
| **`EmotePurge.Core`** | nur BCL | EF Core, StackExchange.Redis, `System.Net.Http`, ASP.NET Core — auch transitiv. 0 `PackageReference`, 0 `ProjectReference`. **Seit 2026-08-02 von `CoreAssemblyReferenceTests` erzwungen**, nicht mehr nur Review-Disziplin |
| **`EmotePurge.Infrastructure`** | → Core; EF/Redis/HTTP | ASP.NET-Core-Typen, Rückverweis auf Api/Worker |
| **`EmotePurge.Api`** | → Infrastructure, → Core | direkter `AppDbContext`- **oder** `IConnectionMultiplexer`-Zugriff aus Handlern; alles über Service-Interfaces |
| **`EmotePurge.Worker`** | → Infrastructure, → Core | direkter `AppDbContext`-Zugriff — seit 2026-08-02 verstoßfrei (die zwei Stellen laufen über `IChannelService.ListActiveChannelNamesAsync`) |
| **`web/core/`** | nichts aus `features/` oder `shared/` | Verweise „nach oben" |
| **`web/shared/`** | → `core/` | nichts aus `features/` |
| **`web/features/`** | → `core/` + `shared/` | — |

## Geltende Regeln

Die Begründung zu jeder Regel steht in [docs/DECISIONS.md](docs/DECISIONS.md).

1. **Vor jedem `git commit` erst den Nutzer fragen.** Gilt auch, wenn Code-Änderungen selbst ohne Rückfrage umgesetzt werden dürfen (z. B. nach freigegebenem Plan-Mode-Plan) — die Freigabe für Edits deckt den Commit-Schritt nicht ab.
2. **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), in mehreren logisch getrennten Commits statt einem Sammel-Commit pro Feature.
3. **Ein Commit, der eine Konvention, einen Vertrag oder eine Topologie ändert, enthält seinen Eintrag in `docs/DECISIONS.md` im selben Commit.**
4. **Kein `AppDbContext` und kein `IConnectionMultiplexer` direkt aus Minimal-API-Handlern.** Neue Backend-Fähigkeiten bekommen ein Interface in `Core/Services/` und eine Implementierung in `Infrastructure/Services/`. Ausdrücklich **kein** generisches Repository-Pattern über EF Core.
5. **Klassen mit nicht-trivialer Logik oder externer Abhängigkeit (DB, Redis, TwitchLib, 7TV) bekommen ein Interface**, reine Daten-/DTO-Typen nicht. Ausnahme: ein `BackgroundService`, der ausschließlich per `AddHostedService<T>()` läuft und nirgends injiziert wird, wird als konkrete Klasse registriert.
6. **Minimal API, keine Controllers.** Neue Endpoints kommen in `Endpoints/*.cs`, nicht in `Program.cs`. Autorisierung über `IEndpointFilter`, nicht über ASP.NET-Core-Policies.
7. **Die API liefert bei Fehlern nur sprachneutrale Codes** (`ApiErrorCodes`), nie fertigen Text. Ein neuer Code braucht denselben Eintrag in `web/src/app/core/i18n/api-error.ts` **und** in beiden Locale-Dateien — `api-error.spec.ts` prüft das.
8. **`Emote.Id` ist ein interner Guid, nicht die 7TV-ObjectID.** Die 7TV-ID steht in `SevenTvEmoteId` und ist nur pro Channel eindeutig (Unique-Index `(ChannelId, SevenTvEmoteId)`), weil dasselbe Emote in mehreren Channels aktiv sein kann.
9. **Channel-Namen laufen immer durch `ChannelName.Normalize`.** Die DB hält sie getrimmt und lowercase; jeder Lookup filtert auf die normalisierte Form. Client-Validatoren müssen die serverseitige *Normalisierung* mitdenken, nicht nur die Regex kopieren — Twitch-Namen werden mit Großbuchstaben getippt (`HandOfBlood`).
10. **Aggregat-Queries in `Infrastructure`: Navigations-Joins vor einem `GroupBy` erst auf eine skalare ID-Liste reduzieren**, nicht direkt darauf gruppieren — EF Core/Npgsql übersetzt das sonst nicht.
11. **Neue Services/Logik in `EmotePurge.Infrastructure` bekommen einen Test** in `tests/EmotePurge.Infrastructure.Tests` (Fixture pro externer Abhängigkeit unter `Fixtures/`, `Integration/` oder `Unit/` je nachdem, ob echte Infrastruktur gebraucht wird). **Neue reine Logik in `EmotePurge.Worker`** (Policies, Registries — alles ohne Postgres/Redis-Berührung) bekommt ihren Test im container-freien `tests/EmotePurge.Worker.Tests`; Transport-Klassen selbst (`TwitchChatManager`, `SevenTvEventClient`) werden bewusst live statt gegen Fakes verifiziert (Regel 16). **Ein neuer `IEndpointFilter` oder eine Änderung an der Filter-Reihenfolge einer `MapGroup` bekommt seinen Fall in `tests/EmotePurge.Api.Tests`** — Endpoint-*Handler* dagegen nicht: die bleiben dünn und delegieren an die getestete Infrastructure-Schicht.
12. **Neue Services/Guards/reine Utilities in `web/src/app/core/` + `shared/` bekommen einen co-located `*.spec.ts`** (Vitest; `HttpTestingController` für alles mit `HttpClient`, `TestBed.runInInjectionContext` für Guards). Größere User-Flows über mehrere Seiten zusätzlich als Playwright-E2E mit gemockten `/api/**`-Responses. Isolierte Komponententests sind bewusst **nicht** Teil der Konvention — dort bleibt Live-Testen im Browser das Mittel.
13. **Nie ein required Signal-Input im Konstruktor lesen** — immer über `effect()`, `rxResource({ params })` oder `ngOnInit()` deferren, sonst `NG0950`.
14. **Ein `computed()`, das über eine Klasseninstanz auf mutable State zugreift statt auf ein Signal, reagiert nie auf Änderungen** daran. State, den ein `computed()` liest, gehört in ein Signal.
15. **Nach Code-Änderungen an `Api`/`Worker` vor jedem `docker compose up -d <service>`-Test entweder `--build` mitgeben oder vorher `docker compose build <service>` laufen lassen** — `up` allein reused ein vorhandenes, potenziell uraltes Image klaglos.
16. **Backend-Features vor dem Commit live gegen echte Postgres-/Redis-/Twitch-/7TV-Zugänge verifizieren**, nicht nur `dotnet build`.
17. **Twitch-Client-Secrets nie ins Repo**: lokal `dotnet user-secrets` im `EmotePurge.Api`-Projekt, im Container `.env` (gitignored, Platzhalter in `.env.example`).
18. **Formatierung ist Werkzeugsache, nicht Geschmackssache**: `npm --prefix web run format` (Prettier) und `dotnet format EmotePurge.slnx`. Die CI prüft beides plus `npm --prefix web run lint`. Repoweite Reformatierungen kommen in einen eigenen `style:`-Commit, der **nichts anderes** enthält, und wandern in `.git-blame-ignore-revs`.
19. **Member-Reihenfolge in C#-Klassen**: `const`/`static readonly` → `readonly` Felder → veränderliche Felder → öffentliche Properties → öffentliche Methoden → private Methoden → `private static` Helper; verschachtelte Typen ans Klassenende. Kein StyleCop — das ist Review-Disziplin, nicht erzwungen (Begründung im Entscheidungslog). Die Angular-Entsprechung steht in [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md) und wird dort per ESLint teilweise erzwungen.

## Sprache

Die Sprachmischung im Bestand ist historisch gewachsen und uneinheitlich. Ab sofort gilt für **neuen** Code:

- Bezeichner, Typen und öffentliche APIs: englisch.
- Kommentare in neuem Code: englisch. Auch der Worker ist inzwischen überwiegend englisch (Messung 2026-08-01: 291 zu 31 von 420 Kommentarzeilen) — die frühere Aussage „Bestand im Worker ist überwiegend deutsch" stammt aus Welle A und ist durch die Umbauten seither überholt. Verbliebene deutsche Kommentare bleiben stehen, werden aber nicht fortgeführt; gemischtsprachige Dateien wie `ITwitchChatManager.cs` sind Altlast, kein Muster.
- Log- und `throw`-Messages: deutsch.
- Projektdokumentation deutsch, Commit-Messages englisch.

Kein Bestandscode wird rückwirkend umgeschrieben.

## Bekannte offene Grenzen

- **Twitchs JOIN-Limits — zwei getrennte, das härtere ist ein Bestandslimit.** (1) *Rate*: eine nicht-verifizierte Verbindung darf 20 JOINs pro 10 Sekunden; TwitchLib drosselt JOINs überhaupt nicht. Unsere eigenen Join-Pfade sind auf 600 ms Abstand gedrosselt, TwitchLibs eigener Rejoin nach einem Reconnect nicht. (2) *Bestand*: **seit 2024-05-15 darf ein Account maximal 100 Chatrooms gleichzeitig gejoint haben** ([dev.twitch.tv/docs/chat](https://dev.twitch.tv/docs/chat/)) — ausgenommen sind nur Kanäle, in denen der Account Broadcaster oder Moderator ist. Die Messung vom 2026-07-30 (28 ungedrosselte JOINs in 5 s, 0 Fehler) hat die *Rate* geprüft und sagt über diese Decke nichts. Vor einem größeren Ausbau: verifizierter Bot-Account (2.000 JOINs/10 s, hebt beide Limits) oder Sharding — Details in [Review-2026-07-29-Umsetzung.md](docs/Review-2026-07-29-Umsetzung.md). **Ein Wechsel auf EventSub ist ausdrücklich kein Ausweg**: Twitch wendet beide Limits auf `channel.chat.message` mit User-Token wortgleich an — s. [Untersuchung-Twitch-EventSub-2026-08-01.md](docs/Untersuchung-Twitch-EventSub-2026-08-01.md).
- **Twitch-Token-Refresh ist in-process.** Seit 2026-07-30 werden Access Tokens serverseitig per Refresh-Token erneuert (lazy, Single-Flight — s. DECISIONS-Eintrag); der Lock ist aber ein In-Process-Semaphor: bei mehr als einer Api-Replica bräuchte es einen verteilten Lock. Idle gespeicherte Tokens werden zudem nicht stündlich validiert, nur benutzte.
- **7TV-EventAPI-Grenzen.** `subscription_limit` ist 500 pro Verbindung (2 Subscriptions je Channel → Connection-Sharding nötig ab ~250 Channels; bisher nur eine 90-%-Warnung im Log). Kein Resume/Replay und ~1-h-Verbindungs-TTL — der periodische REST-Resync ist deshalb Pflicht, nicht Optimierung. Zustellqualität bei ~900er-Sets (HandOfBlood) ungemessen; 7TVs REST-Cache kann 10–30 min veraltet sein (SevenTV/SevenTV#81), das Mess-Log „archiviert <15 min altes Emote" in `ReconcileAsync` quantifiziert die Folgen. Details: [docs/Untersuchung-7TV-WebSocket-2026-07-30.md](docs/Untersuchung-7TV-WebSocket-2026-07-30.md).
