# Emote Purge

Plattformübergreifende Webanwendung, mit der Twitch-Communities ihre 7TV-Emote-Sets analysieren, bewerten und aufräumen. Der Chat wird live mitgelesen und pro Emote gezählt, die Community stimmt in Vote-Sessions über Behalten/Löschen ab, und am Ende löscht eine Mass-Delete-Engine die Verlierer direkt über die 7TV-API.

Produktion: **[emotepurge.app](https://emotepurge.app)**

**Stack:** .NET 10 (Minimal API + Worker Service) · PostgreSQL via EF Core · Redis Pub/Sub · Angular 22 (Standalone + Signals, Tailwind) · Docker.

---

## Wo was steht

| Dokument | Wofür |
|---|---|
| **diese README** | Einmal-Setup und tägliche Kommandos |
| [`CLAUDE.md`](CLAUDE.md) | Die geltenden Regeln und Konventionen, kompakt. Lies das vor dem ersten PR. |
| [`docs/Architectur.md`](docs/Architectur.md) | Die Spezifikation: Module, Kommunikationsfluss, DB-Modell, Docker-Topologie |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | **„Warum ist X so gebaut?"** — chronologisches Entscheidungslog. Durchsuchbar per `grep <dateiname> docs/DECISIONS.md`. |
| [`docs/UI-Designsprache.md`](docs/UI-Designsprache.md) | Verbindlich für jede UI-Änderung unter `web/` |
| [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md) | Frontend-spezifische Konventionen |

Die Doku ist deutsch, Bezeichner und Commit-Messages sind englisch — die genaue Regel steht in `CLAUDE.md` unter „Sprache".

---

## Voraussetzungen

| | Version | Anmerkung |
|---|---|---|
| .NET SDK | 10.0.300+ | gepinnt in [`global.json`](global.json) |
| Node.js | 22+ | [`web/.nvmrc`](web/.nvmrc); CI und das Docker-Image bauen auf 22 |
| Docker | aktuell | Pflicht — auch für `dotnet test` (Testcontainers startet echte Postgres-/Redis-Container) |
| `dotnet-ef` | passend zu EF 10 | `dotnet tool install --global dotnet-ef` |

---

## Einmal-Setup

Die Schritte bauen aufeinander auf. **Ohne Twitch-Login funktioniert nichts** — `join`/`leave` und praktisch jede Seite verlangen eine authentifizierte Session.

### 1. Twitch-Anwendung registrieren

Auf [dev.twitch.tv/console/apps](https://dev.twitch.tv/console/apps) eine Anwendung anlegen. Als OAuth-Redirect-URL **beide** eintragen, sonst funktioniert jeweils nur eine Betriebsart:

```
http://localhost:5151/api/auth/twitch/callback     # lokal via dotnet run
http://localhost:8080/api/auth/twitch/callback     # via docker compose
```

Client-ID und Client-Secret merken. **Das Secret gehört nie ins Repo** (Regel 17) — es geht gleich in die `.env` bzw. in `dotnet user-secrets`.

### 2. `.env` anlegen

```bash
cp .env.example .env
```

Dann ausfüllen:

- `TWITCH_CLIENT_ID` / `TWITCH_CLIENT_SECRET` aus Schritt 1
- `AUTH_TWITCH_TOKEN_ENCRYPTION_KEY` — 32-Byte-Schlüssel, mit dem die Twitch-Tokens in Postgres verschlüsselt werden:
  ```bash
  openssl rand -base64 32
  ```
- **`ADMIN_TWITCH_LOGINS` — dein eigener Twitch-Login.** Ohne das liefert der komplette Admin-Bereich (`/admin/*`) ein blankes 403, ohne Hinweis worauf.
- Postgres-/Redis-Passwörter kannst du für lokal so lassen.

### 3. Datenbank migrieren

**Migrationen laufen nicht automatisch beim Start** — in keiner Umgebung. Vor dem ersten Start also:

```bash
docker compose up -d postgres redis
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

### 4. Starten

**Variante A — alles in Docker** (nächster an Produktion; Angular wird ins Api-Image gebaut und von dort ausgeliefert):

```bash
docker compose up -d --build
# → http://localhost:8080
```

**Variante B — lokal mit Hot Reload** (für Frontend-Arbeit):

```bash
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api        # Terminal 1, Port 5151
dotnet run --project src/EmotePurge.Worker     # Terminal 2
npm --prefix web install                       # einmalig
npm --prefix web start                         # Terminal 3 → http://localhost:4200
```

`ng serve` proxied `/api` auf `:5151` ([`web/proxy.conf.json`](web/proxy.conf.json)), damit alles same-origin bleibt und die Session-Cookies ohne CORS-Konfiguration fließen.

> **Nicht** die VS-Code-Launch-Config `Api` für Frontend-Arbeit benutzen: die bindet hart auf `:8080` und bricht damit den lokal registrierten Redirect auf `:5151`.

### 5. Einloggen und einen Channel tracken

Im Browser einloggen, dann einen Twitch-Channel joinen. **Erst danach gibt es überhaupt Daten** — Emotes kommen aus dem 7TV-Sync, Nutzungszahlen erst aus mitgelesenem Chat. Es gibt bewusst keine Seed-Daten: die Anwendung lebt von echtem Chat-Verkehr, und ein Fixture würde das nur vortäuschen.

Wer sich als Nicht-Admin ausprobieren will, kann `ADMIN_TWITCH_LOGINS` leeren und den Stack neu starten.

---

## Tägliche Kommandos

```bash
# Bauen
dotnet build EmotePurge.slnx

# Tests (Backend braucht laufendes Docker — Testcontainers)
dotnet test EmotePurge.slnx
npm --prefix web test -- --watch=false      # Vitest
npm --prefix web run e2e                    # Playwright, /api/** gemockt

# Formatierung und Lint — dieselben Prüfungen wie in der CI
dotnet format EmotePurge.slnx
npm --prefix web run format
npm --prefix web run lint

# Stack neu bauen (nach Backend-Änderungen zwingend mit --build, s. Regel 15)
docker compose up -d --build
docker compose logs -f api
```

Einmalig empfohlen, damit `git blame` die reinen Formatierungs-Commits überspringt:

```bash
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

---

## Was beim ersten Beitrag überrascht

Vier Dinge, die bewusst so sind und trotzdem stolpern lassen:

**Eine neue Backend-Fähigkeit kostet drei Stellen.** Interface in `EmotePurge.Core/Services/`, Implementierung in `EmotePurge.Infrastructure/Services/`, Registrierung in `AddEmotePurgeInfrastructure`. `AppDbContext` und `IConnectionMultiplexer` sind aus API-Handlern verboten (Regel 4). Die Interfaces werden nie gemockt — sie tragen die Schichtentrennung, nicht die Testbarkeit. Das ist Zeremonie mit Absicht, nicht aus Versehen.

**Endpoints leben in `src/EmotePurge.Api/Endpoints/*.cs`, nie in `Program.cs`** (Regel 6), und Autorisierung läuft über `IEndpointFilter`-Klassen in `Auth/`, nicht über ASP.NET-Policies. Welcher Filter für welchen Endpoint gilt, steht als Matrix in `docs/Architectur.md`.

**Die API gibt bei Fehlern nur sprachneutrale Codes zurück** (`ApiErrorCodes`), nie fertigen Text. Ein neuer Code braucht denselben Schlüssel in `web/src/app/core/i18n/api-error.ts` **und** in beiden Locale-Dateien — `api-error.spec.ts` schlägt sonst fehl.

**Fast nichts ist zur Laufzeit konfigurierbar.** Flush-Intervall, Join-Drosselung, Rate-Limits, Watchdog-Schwellen und das Delete-Pacing sind benannte Konstanten im Code, keine Settings. Das ist eine bewusste, durchgehaltene Entscheidung — aber sie bedeutet, dass ein Betriebsproblem eine Code-Änderung samt Deploy braucht.

---

## Beitragen

- **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), lieber mehrere logisch getrennte Commits als ein Sammel-Commit.
- Ein Commit, der eine Konvention, einen Vertrag oder eine Topologie ändert, **enthält seinen Eintrag in `docs/DECISIONS.md` im selben Commit**.
- Backend-Änderungen vor dem Commit **live** gegen echte Postgres-/Redis-/Twitch-/7TV-Zugänge verifizieren, nicht nur `dotnet build` (Regel 16).
- Die vollständige Regelliste steht in [`CLAUDE.md`](CLAUDE.md).
