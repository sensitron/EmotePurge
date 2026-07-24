# ARCHITECTURE & SPECIFICATION: Emote Purge

> **Projektname:** Emote Purge  
> **Repository:** `emote-purge`  
> **Backend API:** `EmotePurge.Api` (.NET 10)  
> **Worker Bot:** `EmotePurge.Worker` (.NET 10)  
> **Message Broker / Cache:** Redis 7.2  
> **Datenbank:** PostgreSQL (EF Core)

---

## 1. Systemübersicht & Leitprinzipien

**Emote Purge** ist eine plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch.

### Architektur-Grundsätze:

1. **Single Source of Truth (PostgreSQL):** Die PostgreSQL-Datenbank speichert dauerhaft, welche Kanäle aktiv sind (`IsBotActive = true`), welche Emotes existieren und wie die Chat-Statistiken aussehen.
2. **Entkoppelte Echtzeit-Steuerung (Redis Pub/Sub):** Web API und Worker Service sind strikt getrennt. Betritt ein Streamer den Bot im Dashboard, schreibt die API dies in PostgreSQL und publisht ein Event via Redis (`channel:bot:commands`). Der Worker empfängt dieses Event in Echtzeit (< 5ms) und join den Chat.
3. **Automatisches Recovery bei Neustarts:** Beim Start liest der Worker-Service alle aktiven Kanäle aus PostgreSQL aus und stellt alle Chat- und WebSocket-Verbindungen automatisch wieder her.
4. **Zero-Knowledge für Schreib-Tokens:** 7TV-Access-Tokens mit Schreibrechten verbleiben _ausschließlich_ im Browser des Admins. Das Backend speichert oder verarbeitet zu keinem Zeitpunkt 7TV-Tokens.
5. **Dynamisches Rollen-Caching:** Rollen (Sub, VIP, Mod) werden nicht fest in der Datenbank abgelegt, sondern live über die Twitch API abgefragt und kurzzeitig in Redis / MemoryCache gecacht.
6. **High-Performance Analytics:** Der Chat-Bot verarbeitet hohe Chat-Volumen ressourcenschonend durch In-Memory-Pufferung (`ConcurrentDictionary`) und führt alle 30 Sekunden einen Batch-Flush in PostgreSQL aus.

---

## 2. Tech-Stack & Infrastructure

| Schicht            | Technologie            | Beschreibung & Zweck                                                                          |
| :----------------- | :--------------------- | :-------------------------------------------------------------------------------------------- |
| **Backend API**    | .NET 10 (ASP.NET Core) | REST API für Auth, Dashboard, Voting-Engine und Redis-Publisher.                              |
| **Worker Service** | .NET 10 Worker Service | Hintergrund-Bot für Twitch IRC Chat Listener & 7TV WebSocket EventAPI.                        |
| **Message Broker** | Redis 7.2 (Alpine)     | Entkopplung von API & Worker via Pub/Sub; Caching für Twitch-Rollen. Pin auf 7.2, der letzten BSD-lizenzierten Redis-Version vor dem Lizenzwechsel auf RSALv2/SSPL ab 7.4. |
| **Datenbank**      | PostgreSQL 16+         | Relationale Persistenz für Channel, Emotes, Stats und VoteSessions via EF Core (Npgsql).      |
| **Frontend**       | Angular + Tailwind CSS | Single Page Application mit Virtual Scrolling (`CdkVirtualScrollViewport`) für 1.000+ Emotes. |
| **Deployment**     | Docker Compose         | Containerisierung von API, Worker Service und Redis mit persistenten Volumes.                 |

---

## 3. Inter-Service Kommunikation (Pub/Sub + Recovery)

[ Angular Dashboard ]
│
│ HTTP POST /api/channels/{name}/join
▼
[ ASP.NET Core API ] ───────── (1) Save Status ─────────► [ PostgreSQL ]
│ ▲
│ (2) PUBLISH "JOIN:montanablack" │
▼ │ (4) Recovery On Boot
[ Redis Pub/Sub Broker ] │
│ │
│ (3) Realtime Signal │
▼ │
[ .NET Worker Service ] ──────────────────────────────────────┘
│
├─► Twitch IRC: Join Channel
└─► 7TV WebSocket: Subscribe to Emote Set

---

## 4. Modul-Spezifikationen

### Modul A: Twitch Chat Bot & Analytics Engine (Worker Service)

> **Umsetzungsstand:** Der Grundfluss aus A.1 (ohne Spam-Schutz/Emote-Matching im Chat) und A.3 (7TV-Sync) sind implementiert. `EmotePurge.Worker` verbindet sich anonym/read-only per `TwitchLib.Client` (kein Bot-Account, kein OAuth-Token), joint/verlässt Channels auf Zuruf per Redis (`channel:bot:commands`, Messages `JOIN:<name>`/`LEAVE:<name>`) und beim Start automatisch alle `IsBotActive=true`-Channels aus Postgres (Boot-Recovery, Grundsatz 3), und loggt jede empfangene Chat-Nachricht. Gesteuert über zwei Minimal-API-Endpoints in `EmotePurge.Api`: `POST /api/channels/{channelName}/join` upsertet den `Channel` in Postgres (Grundsatz 1) und published `JOIN:<name>`; `DELETE /api/channels/{channelName}` löscht die Zeile hart (kein reines Deaktivieren — siehe CLAUDE.md-Entscheidungslog) und published `LEAVE:<name>`. Bei jedem Join wird zusätzlich das aktive 7TV-Emote-Set aufgelöst und vollständig nach Postgres synchronisiert (`ISevenTvSyncService`), danach hält eine gemeinsame 7TV-WebSocket-Verbindung (`ISevenTvEventClient`, s. A.3) die Emote-Liste aller aktiven Channels live aktuell. A.2 (Spam-Schutz beim Matching, In-Memory-Aggregator, Batch-Flush) ist noch nicht implementiert.

#### A.1 IRC Chat Listener & Spam-Schutz

- Verbindet sich via `TwitchLib.Client` mit allen aktiven Twitch-Kanälen.
- Nachrichten werden am Leerzeichen gespalten (`string.Split(' ')`) und gegen ein `HashSet<string>` abgeglichen.
- **Spam-Schutz:** Jedes vorkommende Emote wird **maximal 1-mal pro Chat-Nachricht** gezählt (verhindert Verzerrung durch Spam-Copypastas).

#### A.2 In-Memory Aggregator & Batch Flush

- Counts werden in einem `ConcurrentDictionary<string, int>` (Key: `EmoteId`) hochgezählt.
- Ein Timer führt alle **30 Sekunden** einen Batch-Flush in die PostgreSQL-Datenbank aus.

#### A.3 7TV EventAPI WebSocket Engine (Realtime Tracking)

> **Abweichung von der urspr. Spezifikation:** **Eine gemeinsame** WebSocket-Verbindung für alle aktiven Kanäle (nicht eine pro Kanal) — 7TV erlaubt mehrere `emote_set.update`-Subscriptions auf derselben Connection (begrenzt durch `subscription_limit` aus dem `Hello`-Frame). Spart Ressourcen und Reconnect-Logik; gleiches Prinzip wie der eine gemeinsame `TwitchClient` für alle IRC-Kanäle in A.1. Implementiert als `ISevenTvEventClient`/`SevenTvEventClient` in `EmotePurge.Worker`, hält intern ein `channelName → emoteSetId`-Mapping (nötig, weil `DELETE /api/channels/{channelName}` die `Channel`-Zeile bereits vor der Leave-Verarbeitung im Worker löscht, ein DB-Lookup zum Unsubscribe-Zeitpunkt also nicht mehr möglich ist).
>
> **API-Version:** 7TV v3 (REST + GQL + EventAPI), nicht v4. v4 existiert bereits als GraphQL-API, hat aber (Stand Sync-Implementierung) noch keine EventAPI/WebSocket unter `events.7tv.io/v4` — für Echtzeit-Updates bleibt v3 daher die einzig nutzbare Version.
>
> **Auflösung Channel → Emote-Set:** 7TVs REST-Endpoint (`/v3/users/twitch/{twitchUserId}`) akzeptiert nur die numerische Twitch-User-ID, nicht den Usernamen. Da bewusst keine Twitch-Helix-API/App-Registrierung genutzt wird, löst `ISevenTvApiClient` den Twitch-Usernamen stattdessen über 7TVs eigene GraphQL-Nutzersuche (`/v3/gql`, `users(query: ...)`, gefiltert auf exakten Treffer in `connections[]` mit `platform=="TWITCH"`) auf. Das befüllt `Channel.TwitchChannelId` damit bereits jetzt (nicht erst durch das künftige Modul B) — semantisch dieselbe numerische ID, nur ein anderer Befüllungsweg.

- Verwaltet **eine gemeinsame** WebSocket-Verbindung zu `wss://events.7tv.io/v3` für alle aktiven Kanäle.
- Abonniert das Event `emote_set.update` je Kanal:

```json
{
  "op": 35,
  "d": {
    "type": "emote_set.update",
    "condition": {
      "object_id": "<EMOTE_SET_ID>"
    }
  }
}
```

Event-Handling (`ApplyEmoteSetUpdateAsync` in `ISevenTvSyncService`):

- ADD: Fügt das neue Emote in PostgreSQL an.
- UPDATE: Aktualisiert Name/Bild-URL des bestehenden Emotes.
- REMOVE: Setzt in PostgreSQL `IsArchived = true`.

Reconnect-Strategie: kein Session-Resume — bei jedem Reconnect werden alle getrackten Emote-Sets neu subscribed und zusätzlich per REST voll resynct (heilt auch einen währenddessen gewechselten aktiven Set).

### Modul B: Auth & Dynamisches Rollen-System

#### B.1 Authentication

- Twitch OAuth2 Flow via Web API: `/api/auth/twitch/login` und `/api/auth/twitch/callback`.
- Fragt nur die Grund-Identität ab (`user:read:email` oder Basis-Profil).

#### B.2 Live-Rollenprüfung

- Twitch-Rollen werden nicht persistent in PostgreSQL gespeichert.
- Beim Vote-Request prüft das Backend die Rollen des Users live via Twitch Helix API.
- Ergebnisse werden für 5–15 Minuten in Redis gecacht, um Rate-Limits zu schonen.

### Modul C: Voting Engine & Beliebtheits-Score

- Voting-Ort: Das Voting findet ausschließlich im Web-Dashboard statt (nicht im Chat).
- Parallele Sessions: Erlaubt flexible Votings (z. B. "Monats-Aufräumaktion Juli").
- Zielgruppen-Einschränkung (`AllowedRoles`): Festlegbar, wer abstimmen darf (Alle, Subs, VIPs, Mods).
- Der Beliebtheits-Score: Das System berechnet automatisch einen Gesamtwert pro Emote:

$$\text{Score} = f(\text{Chat-Nutzung}) + (\text{Keep-Votes} - \text{Delete-Votes})$$

### Modul D: Client-Side Mass Delete Engine (Angular Frontend)

- Virtual Scrolling: Nutzung von Angular CDK `CdkVirtualScrollViewport` für flüssiges Rendering.
- Direct GraphQL Execution: Schreib-Tokens verbleiben lokal im Browser (`sessionStorage`).
- Batch Delete Queue: Das Frontend schickt beim Löschbefehl die Mutation direkt vom Browser an `https://7tv.io/v3/gql`:

```graphql
mutation RemoveEmote($setId: ObjectID!, $emoteId: ObjectID!) {
  emoteSet(id: $setId) {
    emotes(id: $emoteId, action: REMOVE) {
      id
    }
  }
}
```

- Rate-Limiting: Sequenzielle Ausführung mit 250–300 ms Verzögerung zwischen Requests.
- Backend-Sync: Das Frontend meldet gelöschte IDs an die C#-API (`POST /api/emotes/sync-deleted`), um den Datenbank-Status zu aktualisieren.

## 5. Datenbankmodell (Entity Framework Core Schema)

> **Abweichung von der urspr. Spezifikation (`Emote`):** Die 7TV-ObjectID ist **nicht** mehr der Primary Key, sondern liegt in `SevenTvEmoteId`. Grund: Ein 7TV-Emote kann gleichzeitig in mehreren Channels aktiv sein; da `Emote` aber pro Channel eine eigene Zeile ist (`ChannelId`-Spalte), hätte die 7TV-ID als globaler PK bei geteilten Emotes zu einer Primary-Key-Kollision geführt. Stattdessen ist `Id` ein interner Guid-PK, und ein Unique-Index auf `(ChannelId, SevenTvEmoteId)` stellt die Eindeutigkeit pro Channel sicher. `UsageStat.EmoteId` referenziert diesen internen PK.
>
> Zusätzlich hat `UsageStat` einen Unique-Index auf `(EmoteId, Date)`, damit der 30-Sekunden-Batch-Flush pro Emote und Tag genau eine aggregierte Zeile pflegt statt vieler Einzelzeilen.
>
> `User`, `VoteSession`, `Vote` und `AllowedRoles`/`VoteType` (Modul B/C) sind noch nicht implementiert — nur `Channel`, `Emote`, `UsageStat` existieren bisher (Modul 1: Chat-Analytics & Live-Synchronisation).
>
> **Zweite Abweichung (`Channel.TwitchChannelId`):** ist `string?` (nullable) statt non-nullable — da die Spalte einen Unique-Index hat, hätte ein non-nullable Default (`""`) beim zweiten angelegten Channel einen Unique-Constraint-Verstoß ausgelöst (leerer String zählt für Unique-Indizes, NULL nicht). Bleibt `null`, bis sie aufgelöst werden kann. Wird inzwischen (Modul A.3) beim Channel-Join über 7TVs GraphQL-Nutzersuche befüllt, nicht erst durch das künftige Modul B (Twitch-OAuth) — siehe Modul-A.3-Abschnitt.

```csharp
namespace EmotePurge.Core.Entities;

public class Channel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TwitchChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ActiveEmoteSetId { get; set; } = string.Empty;
    public bool IsBotActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Emote> Emotes { get; set; } = new List<Emote>();
}

public class Emote
{
    public string Id { get; set; } = Guid.NewGuid().ToString(); // interner PK

    // 7TV ObjectID (24-hex string). Nicht der PK: dasselbe 7TV-Emote kann in
    // mehreren Channels gleichzeitig aktiv sein, daher gilt Eindeutigkeit nur
    // pro Channel via Unique-Index auf (ChannelId, SevenTvEmoteId).
    public string SevenTvEmoteId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    public Channel Channel { get; set; } = null!;
    public ICollection<UsageStat> UsageStats { get; set; } = new List<UsageStat>();
}

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty; // FK auf Emote.Id (interner PK)
    public DateTime Date { get; set; } // UTC-Kalendertag, Unique-Index mit EmoteId
    public int UseCount { get; set; }

    public Emote Emote { get; set; } = null!;
}

// Noch nicht implementiert (Modul B/C):
public class User
{
    public string Id { get; set; } = string.Empty; // Twitch User ID
    public string TwitchUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
}

public class VoteSession
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public AllowedRoles AllowedVoterRoles { get; set; } = AllowedRoles.Everyone;
    public bool IsActive { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public ICollection<Vote> Votes { get; set; } = new List<Vote>();
}

[Flags]
public enum AllowedRoles
{
    Everyone = 1,
    Subs = 2,
    VIPs = 4,
    Mods = 8,
    Broadcaster = 16
}

public class Vote
{
    public long Id { get; set; }
    public long VoteSessionId { get; set; }
    public string EmoteId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public VoteType Type { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum VoteType
{
    Keep = 1,
    Delete = 2
}
```

## 6. Docker Compose Topologie

> **Abweichungen von der urspr. Spezifikation:** `redis:7-alpine` → `redis:7.2-alpine` (Lizenz-Grund, siehe Abschnitt 2). `depends_on` nutzt `condition: service_healthy` statt einer einfachen Liste, dazu Healthchecks für `postgres`/`redis` — ohne das starten `api`/`worker` sonst, bevor die Datenbank überhaupt Verbindungen annimmt, und crashen beim ersten Zugriff. `postgres` ist zusätzlich lokal auf `127.0.0.1:5432` exponiert (DB-Tools wie DataGrip/pgAdmin während der Entwicklung). Die konkreten Dockerfiles liegen unter `src/EmotePurge.Api/Dockerfile` und `src/EmotePurge.Worker/Dockerfile` (Multi-Stage-Build: SDK-Image für Build/Publish, schlankes Runtime-Image für `final`).

```yaml
name: emote-purge

services:
  redis:
    image: redis:7.2-alpine
    container_name: emotepurge-redis
    restart: unless-stopped
    command: redis-server --requirepass ${REDIS_PASSWORD}
    ports:
      - "127.0.0.1:6379:6379"
    healthcheck:
      test: ["CMD", "redis-cli", "-a", "${REDIS_PASSWORD}", "--no-auth-warning", "ping"]
      interval: 5s
      timeout: 3s
      retries: 10
    networks:
      - emotepurge-network

  postgres:
    image: postgres:16-alpine
    container_name: emotepurge-postgres
    restart: unless-stopped
    environment:
      POSTGRES_DB: emotepurge
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    ports:
      - "127.0.0.1:5432:5432"
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER} -d emotepurge"]
      interval: 5s
      timeout: 3s
      retries: 10
    networks:
      - emotepurge-network

  api:
    build:
      context: .
      dockerfile: src/EmotePurge.Api/Dockerfile
    container_name: emotepurge-api
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=emotepurge;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Redis__ConnectionString=redis:6379,password=${REDIS_PASSWORD}
    networks:
      - emotepurge-network
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy

  worker:
    build:
      context: .
      dockerfile: src/EmotePurge.Worker/Dockerfile
    container_name: emotepurge-worker
    restart: unless-stopped
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=emotepurge;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      - Redis__ConnectionString=redis:6379,password=${REDIS_PASSWORD}
    networks:
      - emotepurge-network
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy

volumes:
  postgres-data:
    driver: local

networks:
  emotepurge-network:
    driver: bridge
```

Konfiguration erfolgt über eine `.env`-Datei am Repo-Root (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`) — Vorlage in `.env.example`, `.env` selbst ist git-ignored.

## 7. Lokale Entwicklung & Debugging (Dev Containers)

Für das Debuggen von `EmotePurge.Api`/`EmotePurge.Worker` direkt in VS Code (Breakpoints, F5) wird das offizielle **Dev Containers**-Modell verwendet, nicht das Attachen an ein produktionsnahes, vorgebautes Image:

- `.devcontainer/devcontainer.json` + `.devcontainer/docker-compose.yml` definieren einen eigenen `devcontainer`-Service (SDK-Image, Repo als Volume gemountet) im selben Compose-Netzwerk wie `postgres`/`redis`. Die `api`/`worker`-Services aus dem Root-`docker-compose.yml` werden dabei bewusst **nicht** gestartet (`runServices: ["postgres", "redis"]`) — im Dev Container läuft die App direkt über den .NET-Debugger, nicht als vorgebautes Docker-Image.
- Verbindungsstrings (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) zeigen im Dev Container automatisch auf die Compose-Hostnamen `postgres`/`redis`; außerhalb des Containers (normaler Host-Debug) greifen dieselben `.vscode/launch.json`-Konfigurationen stattdessen auf `localhost` aus `appsettings.json` zurück (sofern `docker compose up postgres redis` lokal läuft).
- `.vscode/launch.json` enthält `coreclr`-Launch-Configs `Api` und `Worker` sowie eine Compound-Config `Api + Worker` zum gemeinsamen Debuggen beider Prozesse; `.vscode/tasks.json` baut jeweils vorher (`build-api`/`build-worker`).
- Die Api bindet dabei explizit auf `http://0.0.0.0:8080` (`ASPNETCORE_URLS`), passend zum Port, den auch der produktive `api`-Container exponiert — HTTPS-Dev-Zertifikate werden im Linux-Container bewusst nicht eingerichtet.
