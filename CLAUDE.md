# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projekt-Überblick

Emote Purge: plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch. .NET 10, Clean/Layered Architecture, PostgreSQL (EF Core) als Single Source of Truth, Redis Pub/Sub zur Entkopplung von API und Worker.

**Vor Architektur-Fragen immer zuerst [Architectur.md](Architectur.md) lesen** — dort steht die vollständige Spezifikation (Module A–D, DB-Schema, Docker-Topologie, Kommunikationsfluss). Dieses Dokument hier ist der schnelle Einstieg für Claude Code, kein Ersatz dafür.

**Umsetzungsstand:** Bisher ist nur **Modul 1 (Chat-Analytics & Live-Synchronisation)** implementiert — Entitäten `Channel`/`Emote`/`UsageStat`, EF-Core-Persistenz, Redis-Pub/Sub-Grundgerüst, Docker-/Devcontainer-Infrastruktur. Twitch-IRC-Bot, 7TV-WebSocket-Engine (Modul A), Auth/Rollen (Modul B), Voting-Engine (Modul C) und das Angular-Frontend (Modul D) existieren noch nicht.

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

Erwartet Postgres/Redis erreichbar über die in `appsettings.json` hinterlegten `localhost`-Connection-Strings, z. B. via `docker compose up postgres redis`.

### Tests

Es existiert noch kein Testprojekt.

### EF Core Migrationen

`dotnet-ef` ist nicht global installiert (`dotnet tool install --global dotnet-ef` vor erster Nutzung). Migrationen liegen konzeptionell in `EmotePurge.Infrastructure`, Startprojekt für die Connection-String-Auflösung ist `EmotePurge.Api`:

```
dotnet ef migrations add <Name> --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

Es existieren noch keine Migrationen.

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

- **`EmotePurge.Core`** — Entitäten (`Channel`, `Emote`, `UsageStat`) und die Messaging-Abstraktion (`IRedisPublisher`/`IRedisSubscriber`). Hat bewusst **keine** Abhängigkeit auf EF Core oder StackExchange.Redis: Die Redis-Interfaces arbeiten mit reinen `string`-Signaturen statt `RedisChannel`/`RedisValue`, damit die Domänenschicht frei von Infrastruktur-Paketen bleibt.
- **`EmotePurge.Infrastructure`** — `AppDbContext` (Npgsql), `RedisPublisher`/`RedisSubscriber` (StackExchange.Redis-Implementierung der Core-Interfaces), sowie `ServiceCollectionExtensions.AddEmotePurgeInfrastructure(configuration)` als zentraler DI-Registrierungspunkt. Sowohl `Api` als auch `Worker` rufen in ihrer `Program.cs` nur diese eine Extension-Methode auf, um DbContext + Redis-`ConnectionMultiplexer` + Publisher/Subscriber zu bekommen.
- **`EmotePurge.Api`** — ASP.NET Core Web API (Controllers), hört im Container/Dev-Container auf `http://0.0.0.0:8080`.
- **`EmotePurge.Worker`** — .NET Worker Service, Zielort für den zukünftigen Twitch-IRC-Bot und die 7TV-WebSocket-Engine.

Connection-Strings/Redis-Config kommen aus `appsettings.json` (Keys: `ConnectionStrings:DefaultConnection`, `Redis:ConnectionString`), in Docker/Dev-Container per Environment-Variablen (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) überschrieben.

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
