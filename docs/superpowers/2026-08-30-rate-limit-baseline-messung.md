# Baseline-Messung: `/api/`-Requests je Ablauf (vor der Umsetzung von #33)

**Datum:** 2026-08-30

**Zweck:** empirischer Vergleichswert für die Spec [`specs/2026-08-30-rate-limit-architecture-design.md`](specs/2026-08-30-rate-limit-architecture-design.md). Die dortigen Permit-Zahlen stammten aus Codeanalyse; dieses Dokument ist die Gegenprobe im laufenden System. Es ist **Task 0** des Plans und wurde **vor jeder Codeänderung** aufgenommen.

**Betrifft:** die Abnahmekriterien des Umsetzungsplans zu Issue #33.

## Messaufbau

| Teil | Wert |
|---|---|
| Api | `dotnet run --project src/EmotePurge.Api`, `http://localhost:5151`, Environment `Development` |
| Frontend | `npm --prefix web start`, `http://localhost:4200`, Dev-Proxy auf `:5151` |
| Infrastruktur | `docker compose up -d postgres redis` (Dev-Container) |
| Worker | **nicht** gestartet — deshalb bleibt `/api/health` auf `503` und ein 7TV-Sync läuft nicht an. Für die Requestzählung im Client ist das ohne Belang und für Ablauf (e) sogar die Voraussetzung (der Poll läuft in seine Obergrenze). |
| Browser | Playwright 1.62.1, Chromium headless, je Ablauf ein **frischer** Browser-Context (kalter Permissions-Cache), dieselbe Session-Cookie |
| Gegenprobe | `Microsoft.AspNetCore` in `appsettings.Development.json` vorübergehend auf `Information`; die Änderung wurde nach der Messung zurückgenommen und gehört **nicht** in einen Commit |
| Konto | Twitch-User-ID `62843286` (Partitionsschlüssel der Policy `ExternalApi`) |
| Testdaten | `brudivoeller_tv` (649 Emotes, aktive Vote-Session `2` mit 21 Emotes), `reved` (kein aktives Emote-Set) |

Gezählt werden alle Requests auf `/api/`. Die Spalte „Policy“ folgt dem Ist-Stand aus `grep -rn RequireRateLimiting src/EmotePurge.Api/`; `—` heißt policy-frei.

## Ergebnis je Ablauf

### a) Workspace-Einstieg, Zeitraum „all“ — **6 Permits**, 3 policy-freie Requests

Direktaufruf `/channels/brudivoeller_tv/usage-stats`.

| # | +ms | Request | Policy |
|---:|---:|---|---|
| 1 | 629 | `GET /api/auth/me` | — |
| 2 | 678 | `GET /api/channels/{c}/permissions` | ExternalApi |
| 3 | 1006 | `GET /api/worker/health` | — |
| 4 | 1144 | `GET /api/channels/{c}/emotes/duplicate-names` | ExternalApi |
| 5 | 1144 | `GET /api/channels/{c}/emotes/active-set` | ExternalApi |
| 6 | 1173 | `GET /api/channels/{c}/live` (SSE) | — |
| 7 | 1230 | `GET /api/channels/{c}/emotes/active-set` | ExternalApi |
| 8 | 1231 | `GET /api/channels/{c}/usage-stats/totals?from=2026-08-29&to=2026-08-30` | ExternalApi |
| 9 | 1231 | `GET /api/channels/{c}/usage-stats/series?from=…` | ExternalApi |

Der aufgelöste Zeitraum `from=2026-08-29` ist das `CreatedAt` des Channels — das **ist** „all“ für diesen Testdatenbestand. Die zwei `active-set`-Abrufe sind reproduziert: der zweite folgt der Korrektur von `from` auf den Tracking-Start.

**Deckt sich mit der Spec (6).**

### b) Rundgang: Channel öffnen + zurück zur Übersicht — **7 Permits**, 2 policy-freie Requests

In-App-Navigation von der Übersicht in den Channel und zurück (kein Full Reload).

Hinweg identisch zu (a) ohne `auth/me`/`worker/health`: 6 Permits. Rückweg zur Übersicht: `GET /api/channels/mine` (ExternalApi) und `GET /api/channels/live-events` (SSE, policy-frei).

**Deckt sich mit der Spec (7).** Der Rückweg kostet genau ein Permit; `ChannelService.listMine()` cacht bewusst nicht.

### c) Einstieg in eine Vote-Session — **4 Permits**, 4 policy-freie Requests

Direktaufruf `/channels/brudivoeller_tv/vote-sessions/2`.

| # | +ms | Request | Policy |
|---:|---:|---|---|
| 1 | 563 | `GET /api/auth/me` | — |
| 2 | 616 | `GET /api/channels/{c}/vote-sessions/2/results` (Guard) | ExternalApi |
| 3 | 1109 | `GET /api/worker/health` | — |
| 4 | 1193 | `GET /api/channels/{c}/permissions` | ExternalApi |
| 5 | 1198 | `GET /api/channels/{c}/vote-sessions/2/results` (Page, erneut) | ExternalApi |
| 6 | 1198 | `GET /api/channels/{c}` (Channel-Status) | — |
| 7 | 1219 | `GET /api/channels/{c}/live` (SSE) | — |
| 8 | 1230 | `GET /api/channels/{c}/emotes/duplicate-names` | ExternalApi |

**Deckt sich mit der Spec (4 Permits plus ein policy-freier Channel-Status).** Der doppelte `results`-Abruf durch Guard und Page ist im Abstand von 582 ms belegt.

### d) Vier schnelle Votes hintereinander — **9 Permits** (`2n+1`), 4 policy-freie Requests

Vier `Keep`-Buttons in einem Klickfenster von **14 ms** ausgelöst.

| # | +ms | Request | Policy |
|---:|---:|---|---|
| 1–4 | 10–14 | 4× `POST /api/channels/{c}/vote-sessions/2/votes` | ExternalApi |
| 5,7,9,11 | 158–192 | 4× `GET …/vote-sessions/2/results` (direkter Reload je Vote) | ExternalApi |
| 6,8,10,12 | 160–192 | 4× `GET /api/channels/{c}` (Channel-Status je Vote) | — |
| 13 | 685 | `GET …/vote-sessions/2/results` (SSE-Echo, 500-ms-Debounce) | ExternalApi |

**Deckt sich mit der Spec exakt:** `2n+1 = 9` Permits plus `n = 4` policy-freie Kanalstatus-Reads, 13 Requests gesamt.

### e) Erstnutzung direkt nach einem Join (`awaitSync`-Poll) — **21 Permits**

`awaitSync` startet nur, wenn der Set-Status **weder** eine Set-ID **noch** einen `syncFailureReason` liefert (`usage-stats-page.ts:1057`). Das ist der Zustand unmittelbar nach einem Join. Um ihn zu erzeugen, wurde in der Dev-Datenbank `Channels.LastSyncFailureReason` für `reved` einmalig auf `NULL` gesetzt und **nach der Messung auf `no_active_emote_set` zurückgeschrieben**; Anwendungscode wurde dafür nicht angefasst.

- Baseline wie (a): 6 Permits
- danach **15** zusätzliche `GET …/emotes/active-set` im 2-Sekunden-Takt, von `+3,3 s` bis `+31,3 s`
- der Poll läuft in seine Obergrenze `SYNC_POLL_MAX_ATTEMPTS = 15`, weil der Worker nicht läuft

**Deckt sich mit der Spec (bis zu 15 zusätzliche `active-set` in 30 Sekunden).**

### Nebenbefund: der Fehlergrund-Recheck ist ein anderer Ablauf als (e)

Mit gesetztem `LastSyncFailureReason` (Normalzustand von `reved`) startet **kein** `awaitSync`. Stattdessen läuft der 30-Sekunden-Recheck: genau **ein** `active-set` bei `+31 s`, kein `totals` (weil keine Set-ID vorliegt). Das entspricht der Spec-Zeile „+2 pro Minute“ — die vier Requests pro Minute entstehen erst mit vorhandener Set-ID. Wer (e) messen will, muss den Zustand ohne Fehlergrund herstellen; ein Channel mit Fehlergrund misst etwas anderes.

## Abweichung von der Spec: wann die lokale 429 wirklich auftritt

Die Spec schreibt: „Sechs Rundgänge ergeben 42 Requests und überschreiten das 40er-Fenster weiterhin.“ **Das ist so nicht reproduzierbar.**

Gemessen wurden zehn Rundgänge in dichter Folge (~2 s je Rundgang), abgebrochen beim ersten `429`:

| Rundgang | Permits | Summe im 60-s-Fenster | Zeit | lokale 429 |
|---:|---:|---:|---:|---:|
| 1 | 7 | 8 | +2 s | 0 |
| 2 | 6 | 14 | +4 s | 0 |
| 3 | 6 | 20 | +7 s | 0 |
| 4 | 6 | 26 | +9 s | 0 |
| 5 | 6 | 32 | +11 s | 0 |
| 6 | 6 | 38 | +13 s | **0** |
| 7 | 7 | 45 | +15 s | **5** |

Ursache der Abweichung: der Permissions-Cache im Client hat 30 Sekunden TTL (`channel.service.ts:12`). Bei dicht aufeinanderfolgenden Rundgängen kostet nur der erste 7 Permits, die folgenden 6. Sechs Rundgänge summieren sich damit auf **38** Permits und bleiben unter der Grenze von 40. Erst der siebte Rundgang überschreitet sie.

Die fünf abgelehnten Requests, client- und serverseitig deckungsgleich:

```
429 GET /api/channels/{c}/usage-stats/series   Retry-After: 60
429 GET /api/channels/{c}/emotes/active-set    Retry-After: 60
429 GET /api/channels/{c}/usage-stats/totals   Retry-After: 60
429 GET /api/channels/{c}/emotes/active-set    Retry-After: 60
429 GET /api/channels/mine                     Retry-After: 60
```

Serverseitig als lokale Ablehnung protokolliert:

```
warn: EmotePurge.Api.RateLimiting[0]
      Rate-Limit erreicht: Policy ExternalApi, GET /api/channels/{c}/emotes/active-set,
      Partition 62843286, Retry-After 60s
```

Damit ist die Kernaussage von #33 belegt: **die 429er stammen aus der lokalen ASP.NET-Policy `ExternalApi`**, nicht von einem Provider oder von Cloudflare. Kein einziger Provider-429 trat auf.

### Folge für die Abnahmekriterien

Das Kriterium „**sechs** vollständige Rundgänge mit Rückkehr in einer Minute erzeugen keine lokale 429“ ist als Abnahmetest wertlos: es ist **heute schon grün**, ohne jede Codeänderung. Der Plan muss die Schwelle über den gemessenen Bruchpunkt legen. Empfohlen: **zwölf Rundgänge in einer Minute** — das sind nach heutiger Zählung rund 74 Permits, fällt also heute sicher durch und liegt zugleich klar innerhalb der geplanten `InteractiveRead`-Kapazität von 300 bei 5 Tokens/s Nachfüllung.

## Zusammenfassung gegen die Erwartungswerte

| Ablauf | Erwartung der Spec | Gemessen | Urteil |
|---|---:|---:|---|
| a) Workspace-Einstieg | 6 Permits | 6 | bestätigt |
| b) Rundgang mit Rückkehr | 7 Permits | 7 (kalter Permissions-Cache) | bestätigt |
| c) Vote-Session-Einstieg | 4 Permits + 1 policy-frei | 4 + 1 | bestätigt |
| d) vier schnelle Votes | `2n+1` = 9 Permits + `n` policy-frei | 9 + 4 | bestätigt |
| e) Erstnutzung nach Join | bis zu 15 zusätzliche `active-set` in 30 s | 15 in 28 s | bestätigt |
| Bruchpunkt der 429 | „sechs Rundgänge überschreiten 40“ | erst der **siebte** Rundgang; sechs ergeben 38 | **abweichend** |

Fünf von sechs Erwartungswerten sind empirisch bestätigt. Die einzige Abweichung betrifft nicht die Diagnose, sondern die Schwelle eines Abnahmekriteriums.

## Reproduktion

Die Messskripte liegen bewusst **nicht** im Repository — sie sind Wegwerf-Sonden mit eingebettetem Session-Cookie. Reproduktion: Api und Dev-Server nach `CLAUDE.md` starten, in einem Playwright-Context mit gültigem `.AspNetCore.Cookies` die oben genannten Routen aufrufen und `page.on('request')` auf `/api/` filtern. Für Ablauf (e) vorher den Zustand ohne `LastSyncFailureReason` herstellen und danach zurücksetzen.
