# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projekt-Überblick

Emote Purge: plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch. .NET 10, Clean/Layered Architecture, PostgreSQL (EF Core) als Single Source of Truth, Redis Pub/Sub zur Entkopplung von API und Worker.

**Vor Architektur-Fragen immer zuerst [Architectur.md](Architectur.md) lesen** — dort steht die vollständige Spezifikation (Module A–D, DB-Schema, Docker-Topologie, Kommunikationsfluss). Dieses Dokument hier ist der schnelle Einstieg für Claude Code, kein Ersatz dafür.

**Umsetzungsstand:** **Modul 1 (Chat-Analytics & Live-Synchronisation)** — Entitäten `Channel`/`Emote`/`UsageStat`, EF-Core-Persistenz (erste Migration `InitialCreate` angewendet), Redis-Pub/Sub-Grundgerüst, Docker-/Devcontainer-Infrastruktur — sowie **Modul A.1 (Grundfluss) und A.3 (7TV-Sync)**: `POST /api/channels/{channelName}/join` und `DELETE /api/channels/{channelName}` (Minimal API) verwalten den Channel in Postgres und publishen an Redis, `EmotePurge.Worker` joint/verlässt Channels per anonymem `TwitchLib.Client`-IRC und loggt Chat-Nachrichten, inkl. Boot-Recovery beim Start. Bei jedem Join wird zusätzlich das aktive 7TV-Emote-Set aufgelöst und voll nach Postgres synchronisiert, danach hält eine gemeinsame 7TV-WebSocket-Verbindung die Emote-Liste aller aktiven Channels live aktuell. Noch nicht implementiert: Spam-Schutz/Emote-Matching im Chat + Batch-Flush (Modul A.2), Auth/Rollen (Modul B), Voting-Engine (Modul C), Angular-Frontend (Modul D).

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

Erwartet Postgres/Redis erreichbar über die in `appsettings.json` hinterlegten `localhost`-Connection-Strings (Default-Credentials matchen `.env.example`: `emotepurge`/`change-me`), z. B. via `docker compose up postgres redis`. Api lauscht lokal per `launchSettings.json` auf `http://localhost:5151` (nicht `8080` — das gilt nur im Container).

Test-Requests:
```
curl -X POST http://localhost:5151/api/channels/<twitchChannelName>/join
curl -X DELETE http://localhost:5151/api/channels/<twitchChannelName>
```

### Tests

Es existiert noch kein Testprojekt.

### EF Core Migrationen

`dotnet-ef` ist als globales Tool installiert. Migrationen liegen in `EmotePurge.Infrastructure/Migrations/`, Startprojekt für die Connection-String-Auflösung ist `EmotePurge.Api`:

```
dotnet ef migrations add <Name> --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

Erste Migration (`InitialCreate`) existiert bereits und ist gegen die lokale Postgres-Instanz angewendet.

### Docker Compose (voller Stack)

```
docker compose up -d --build      # redis, postgres, api, worker
docker compose logs -f api
docker compose down                # -v zum Löschen des Postgres-Volumes
```

Konfiguration über `.env` am Repo-Root (Vorlage: `.env.example`).

### Dev Container Debugging (VS Code)

"Reopen in Container" startet einen SDK-Container plus `postgres`/`redis` im selben Compose-Netzwerk (`api`/`worker` aus `docker-compose.yml` werden dabei **nicht** gestartet). Danach F5 mit Launch-Config `Api`, `Worker` oder Compound `Api + Worker` — läuft direkt über den .NET-Debugger, nicht als vorgebautes Image.

## Architektur

Vier Projekte, referenziert wie folgt: `Api`/`Worker` → `Infrastructure` → `Core`.

- **`EmotePurge.Core`** — Entitäten (`Channel`, `Emote`, `UsageStat`), die Messaging-Abstraktion (`IRedisPublisher`/`IRedisSubscriber`), Service-Interfaces (`Services/IChannelService`, `Services/ISevenTvSyncService`) und 7TV-DTOs (`SevenTv/SevenTvModels.cs`, `SevenTv/ISevenTvApiClient`). Hat bewusst **keine** Abhängigkeit auf EF Core, StackExchange.Redis oder HTTP-Client-Typen: Die Redis-Interfaces arbeiten mit reinen `string`-Signaturen, die 7TV-DTOs sind reine `record`s, damit die Domänenschicht frei von Infrastruktur-Paketen bleibt.
- **`EmotePurge.Infrastructure`** — `AppDbContext` (Npgsql), `RedisPublisher`/`RedisSubscriber` (StackExchange.Redis-Implementierung der Core-Interfaces), `Services/ChannelService` (Join/Leave-Business-Logik inkl. Upsert/Delete + Redis-Publish), `SevenTv/SevenTvApiClient` (typisierter `HttpClient`, REST+GQL gegen 7TV v3) und `Services/SevenTvSyncService` (löst Channel→Emote-Set auf, reconciled `Emote`-Zeilen), sowie `ServiceCollectionExtensions.AddEmotePurgeInfrastructure(configuration)` als zentraler DI-Registrierungspunkt. Sowohl `Api` als auch `Worker` rufen in ihrer `Program.cs` nur diese eine Extension-Methode auf.
- **`EmotePurge.Api`** — ASP.NET Core **Minimal API** (keine Controllers — bewusst entfernt, s. Entscheidungslog), Endpoints direkt in `Program.cs`. Endpoints injizieren `IChannelService`, **nicht** `AppDbContext` direkt (s. Entscheidungslog) — HTTP-Boundary-Validierung (Regex-Formatcheck) bleibt im Endpoint, Normalisierung/Persistenz/Redis-Publish liegt im Service. Hört im Container/Dev-Container auf `http://0.0.0.0:8080`, lokal per `launchSettings.json` auf `:5151`.
- **`EmotePurge.Worker`** — .NET Worker Service. `TwitchChatManager` implementiert `ITwitchChatManager` (Singleton) und kapselt einen einzigen langlebigen `TwitchLib.Client` für die gesamte Worker-Lebensdauer — **nie** pro Channel/Join neu instanziieren. `SevenTv/SevenTvEventClient` implementiert `ISevenTvEventClient` (ebenfalls Singleton) analog dazu: **eine einzige** `ClientWebSocket`-Verbindung zu `wss://events.7tv.io/v3` für alle aktiven Channels (mehrere Subscriptions auf einer Connection, nicht eine pro Channel), hält intern ein `channelName → emoteSetId`-Mapping (`ConcurrentDictionary`), `SevenTv/SevenTvDispatchParser` parst eingehende Dispatch-Nachrichten defensiv (unbekannte Formen werden geloggt+übersprungen, nie geworfen). `Worker` hängt in beiden Fällen vom Interface ab, nicht der konkreten Klasse. `Worker.ExecuteAsync` verbindet beide Clients, joint beim Start alle `IsBotActive=true`-Channels aus Postgres (Boot-Recovery) inkl. 7TV-Sync+Subscribe, und abonniert danach `channel:bot:commands` für Echtzeit-Join-/Leave-Kommandos (Join → Twitch-Join + 7TV-Sync+Subscribe, Leave → Twitch-Leave + 7TV-Unsubscribe).

Connection-Strings/Redis-Config kommen aus `appsettings.json` (Keys: `ConnectionStrings:DefaultConnection`, `Redis:ConnectionString`), in Docker/Dev-Container per Environment-Variablen (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) überschrieben. 7TV-Endpunkte (`https://7tv.io/v3/`, `wss://events.7tv.io/v3`) sind fest im Code verdrahtet, nicht konfigurierbar — externe, feste Drittanbieter-URLs, kein Umgebungsunterschied wie bei DB/Redis.

### Datenmodell-Besonderheit: `Emote.Id` ist kein 7TV-PK

`Emote.Id` ist ein interner Guid, **nicht** die 7TV-ObjectID. Die 7TV-ID steht in `SevenTvEmoteId`, eindeutig nur pro Channel via Unique-Index `(ChannelId, SevenTvEmoteId)` in `AppDbContext.OnModelCreating`. Grund: Dasselbe 7TV-Emote kann in mehreren Channels gleichzeitig aktiv sein; ein globaler PK auf der 7TV-ID würde bei geteilten Emotes kollidieren. `UsageStat.EmoteId` referenziert den internen `Emote.Id`-PK und hat einen eigenen Unique-Index `(EmoteId, Date)`, damit der geplante 30-Sekunden-Batch-Flush pro Emote und UTC-Tag genau eine aggregierte Zeile pflegt statt vieler Einzelzeilen.

## Wichtige Architektur-Entscheidungen (Änderungslog)

> Bei künftigen wesentlichen Architektur-/Infrastruktur-Entscheidungen diese Liste ergänzen, nicht nur lesen.

- **Emote-PK statt 7TV-ID** — s. Abschnitt oben.
- **`redis:7.2-alpine` statt `redis:7-alpine`** in `docker-compose.yml`: letzte BSD-lizenzierte Redis-Version vor dem Lizenzwechsel auf RSALv2/SSPL ab Redis 7.4.
- **`depends_on` mit `condition: service_healthy`** + Healthchecks für `postgres`/`redis`: verhindert, dass `api`/`worker` starten und crashen, bevor die Datenbank überhaupt Verbindungen annimmt.
- **`Microsoft.OpenApi` explizit auf `2.7.5` gepinnt** in `EmotePurge.Api.csproj`: patcht GHSA-v5pm-xwqc-g5wc (Stack-Overflow-DoS bei zirkulären OpenAPI-`$ref`s), das transitiv über `Microsoft.AspNetCore.OpenApi 10.0.10` reinkommt.
- **Dev Containers statt Debugger-Attach an einen laufenden Produktions-Container**: `.devcontainer/` startet einen eigenen SDK-Container im selben Compose-Netzwerk wie `postgres`/`redis`; `api`/`worker` laufen dort nicht als vorgebautes Docker-Image, sondern direkt über den VS-Code-Debugger.
- **`.vscode/launch.json` und `tasks.json` sind von `.gitignore` ausgenommen** (`.vscode/*` + gezielte `!`-Ausnahmen), damit die Team-Debug-Config versioniert wird, während sonstige lokale VS-Code-Dateien ignoriert bleiben.
- **Commit-Konvention: Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), in mehreren logisch getrennten Commits statt einem Sammel-Commit pro Feature.
- **Minimal API statt Controllers in `EmotePurge.Api`**: explizite Nutzerentscheidung. `AddControllers()`/`MapControllers()` (Template-Boilerplate ohne je genutzte Controller) entfernt, Endpoints direkt in `Program.cs`. `app.UseAuthorization()` ebenfalls entfernt — ohne `AddAuthorization()`/`[Authorize]`-Endpoints wirft es beim Start eine `InvalidOperationException`.
- **`Channel.TwitchChannelId` ist `string?` (nullable)**: ohne Twitch-Auth (Modul B, noch nicht implementiert) kann die echte numerische Twitch-ID nicht aufgelöst werden; bei non-nullable Default (`""`) hätte der Unique-Index beim zweiten angelegten Channel einen Constraint-Verstoß ausgelöst (leerer String ≠ NULL für Unique-Indizes). Bleibt `null`, bis Modul B sie nachträgt.
- **TwitchLib.Client 4.0.1, anonyme/read-only Verbindung** (`new ConnectionCredentials()` ohne Parameter): kein Bot-Account/OAuth-Token nötig für Join+Chat-Lesen. Kann keine Nachrichten senden — für die aktuelle Bot-Funktionalität irrelevant. `net10.0` wird von TwitchLib.Client 4.0.1 explizit als Dependency-Group unterstützt.
- **Redis-Protokoll für Join-Kommandos**: Channel `channel:bot:commands`, Message-Format `JOIN:<channelName>` (reiner String-Präfix, kein JSON) — matcht `IRedisPublisher`/`IRedisSubscriber`s ohnehin string-basierte Signaturen.
- **`appsettings.json`-Default-Connection-Strings korrigiert**: zeigten seit Schritt 1 auf `Username=postgres;Password=postgres` (Postgres) bzw. kein Passwort (Redis) — passte nie zu den tatsächlichen `.env.example`-Credentials (`emotepurge`/`change-me`) bzw. zu `docker-compose.yml`s `--requirepass`. Erst bei der ersten echten DB-Migration aufgefallen. Jetzt auf `emotepurge`/`change-me` (Postgres) und `localhost:6379,password=change-me` (Redis) korrigiert, damit lokales `dotnet run` gegen `docker compose up postgres redis` ohne manuelles Override funktioniert.
- **Zeilenenden auf LF normalisiert**: `.gitattributes` (`* text=auto eol=lf`), `.editorconfig` und ein geteiltes `.vscode/settings.json` (`files.eol`) sorgen dafür, dass Repo-Inhalt und Checkout konsistent LF sind — vorher schrieb Windows-Checkout CRLF auf die Platte, obwohl im Repo bereits LF gespeichert war, was bei jedem `git add` Warnungen auslöste. `.vscode/settings.json` ist wie `launch.json`/`tasks.json` von `.gitignore` ausgenommen, da EOL-Konvention Team-weit gilt, nicht persönliche Editor-Einstellung ist.
- **`DELETE /api/channels/{channelName}` löscht die Zeile hart** (nicht nur `IsBotActive = false`) und published `LEAVE:<channelName>`. Ursprünglich damit begründet, dass `Emote`/`UsageStat` noch leer waren — das stimmt seit dem 7TV-Sync (Modul A.3) nicht mehr: ein Leave löscht jetzt kaskadierend alle synchronisierten `Emote`-Zeilen des Channels (`OnDelete: Cascade` in `AppDbContext`). Noch nicht revidiert; falls Emote-Historie über einen Leave hinweg erhalten bleiben soll, hier auf Soft-Deactivate (`IsBotActive = false`, Zeile bleibt) umstellen.
- **Service-Layer + Interfaces für DI/Testbarkeit** (explizite Nutzeranforderung): `AppDbContext` darf nicht direkt aus Minimal-API-Handlern aufgerufen werden — dafür `IChannelService`/`ChannelService` (Interface in Core, Implementierung in Infrastructure, wie beim Redis-Pub/Sub-Muster) eingeführt, der die Join/Leave-Business-Logik kapselt. Ausdrücklich **kein** generisches Repository-Pattern über EF Core. Aus demselben Grund haben `TwitchChatManager`/`SevenTvEventClient` Interfaces (`ITwitchChatManager`/`ISevenTvEventClient`), gegen die `Worker` sich verdrahtet — nicht die konkreten Klassen. Faustregel für neuen Code: Klassen mit nicht-trivialer Logik oder externen Abhängigkeiten (DB, Redis, TwitchLib, 7TV) bekommen ein Interface, reine Daten-/DTO-Typen nicht.
- **Vor jedem `git commit` erst den Nutzer fragen** — gilt auch, wenn Code-Änderungen selbst ohne Rückfrage umgesetzt werden dürfen (z. B. nach freigegebenem Plan-Mode-Plan). Die Freigabe für Code-Edits deckt nicht automatisch den Commit-Schritt ab.
- **7TV API v3, nicht v4**: v4 existiert bereits als GraphQL-API (`https://7tv.io/v4/gql`, live verifiziert per Introspection), hat aber keine EventAPI/WebSocket unter `events.7tv.io/v4` (404) — nur v3 bietet Echtzeit-Updates. Da der Sync-Feature-Kern genau darauf beruht, bleibt v3 (REST+GQL+EventAPI) die einzige sinnvolle Wahl; v4 kann nachgezogen werden, sobald 7TV dort eine EventAPI anbietet.
- **Channel→7TV-Auflösung über 7TVs eigene GraphQL-Nutzersuche, nicht Twitch Helix API**: 7TVs REST-Lookup (`/v3/users/twitch/{id}`) akzeptiert nur die numerische Twitch-ID, keinen Usernamen; eine Twitch-App-Registrierung (Helix, Client-Credentials-Flow) wäre robuster (kein Risiko einer falschen 7TV-Fuzzy-Suche), wurde aber bewusst verschoben — `ISevenTvApiClient.ResolveTwitchUserIdAsync` ist als eigene, isolierte Interface-Methode geschnitten, ein späterer Wechsel auf Helix betrifft nur ihre Implementierung, nicht `SevenTvSyncService`/`Worker`.
- **Eine gemeinsame 7TV-WebSocket-Verbindung für alle Channels** (nicht eine pro Channel, wie ursprünglich in Architectur.md beschrieben): 7TV erlaubt mehrere `emote_set.update`-Subscriptions auf einer Connection; spart Ressourcen/Reconnect-Logik, gleiches Prinzip wie der eine gemeinsame `TwitchClient`.
- **`SevenTvEventClient` hält ein eigenes `channelName → emoteSetId`-Mapping**, unabhängig von Postgres: `ChannelService.LeaveAsync` löscht die `Channel`-Zeile bereits, bevor der Worker `LEAVE:` verarbeitet — ein DB-Lookup zum Unsubscribe-Zeitpunkt fände die Zeile nicht mehr. Beim Reconnect wird dieses In-Memory-Mapping genutzt, um alle Subscriptions neu zu senden und einen vollen REST-Resync pro Channel zu fahren (kein Session-Resume/op 34) — heilt auch einen währenddessen vom Streamer gewechselten aktiven Set.
