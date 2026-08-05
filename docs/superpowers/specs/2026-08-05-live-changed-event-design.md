# Design: `live.changed`-Event — Live-Badges aktualisieren sich ohne Browser-Refresh

**Datum:** 2026-08-05
**Status:** vom Nutzer freigegeben (Konversation vom 2026-08-05)

## Problem

Die LIVE-Badges auf der Übersichtsseite (und der Admin-Channel-Liste) werden nach dem
initialen Seitenladen nie aktualisiert — in keine Richtung. Der `TwitchLivePollWorker`
schreibt alle 300 s korrekt den Redis-Key `worker:live-status`, aber es existiert kein
Weg von dieser Änderung zurück in einen bereits geladenen Browser-Tab: kein Pub/Sub-Event
bei Zustandswechsel, kein SSE-Anschluss der Übersicht, kein Frontend-Polling. Beobachtet:
ein Kanal wurde ~30 min nach Stream-Ende noch als LIVE angezeigt; erst ein Refresh
korrigierte es. Die ursprüngliche B10-Entscheidung („Kein SSE-Anschluss: 5-Minuten-
Granularität rechtfertigt keine Push-Updates", DECISIONS.md) wird hiermit revidiert.

Nebenbefund, im selben Zug behoben: der Tooltip „Stand vor x min" auf der Übersicht
friert nach dem ersten Rendern ein, weil das `computed()` `Date.now()` liest statt ein
Signal (Verstoß gegen CLAUDE.md-Regel 14).

## Lösung im Überblick

Der Worker erkennt Zustandswechsel per Diff gegen den zuletzt publizierten Snapshot und
publiziert ein Thin-Event `live.changed` pro betroffenem Channel auf `live:events`. Die
Übersicht bekommt einen eigenen schlanken SSE-Endpoint und lädt bei jedem Event ihre
Channel-Liste neu; die Admin-Channel-Liste abonniert den Typ zusätzlich auf ihrem
bestehenden Admin-Stream.

## 1. Event-Vertrag (Core)

- Neuer Typ `live.changed` in `LiveEvents.cs`, Payload wie `channel.synced`:
  nur der (normalisierte) Channel-Name, **kein** Zustand — Clients reloaden, wie im
  bestehenden Thin-Event-Muster.
- Der Typ wird in **beide** Filterlisten aufgenommen:
  - `ChannelTypes` — fließt damit über den bestehenden per-Channel-Stream
    `GET /api/channels/{channelName}/live` (heute ungenutzt, kostenlos für später).
  - `AdminTypes` — fließt über den bestehenden Admin-Stream.
- Vertrags-Commit ⇒ DECISIONS.md-Eintrag im selben Commit (Regel 3), inklusive
  expliziter Revision der alten „Kein SSE-Anschluss"-Entscheidung.

## 2. Worker: Diff + Publikation

- `TwitchLivePollWorker` hält das zuletzt publizierte Set an Live-Logins in-memory.
  Nach jedem **erfolgreichen** Poll (inkl. „niemand live") wird die symmetrische
  Differenz zum vorherigen Set gebildet; pro gewechseltem Channel geht ein
  `live.changed` raus.
- Die Diff-Entscheidung liegt in einer **puren Klasse** `LiveStatusDiff`
  im Worker-Projekt — analog `ReconnectPolicy`/`TwitchWatchdogPolicy`, container-frei
  testbar in `tests/EmotePurge.Worker.Tests`.
- Randfälle:
  - **Erster Poll nach Worker-Start:** Baseline wird aus dem ggf. noch vorhandenen
    Redis-Key gelesen (TTL 600 s) — Zustandswechsel während eines kurzen
    Worker-Neustarts werden so nicht verschluckt. Fehlt der Key: keine Baseline,
    erster Poll publiziert **keine** Events (kein Event-Sturm für alle Live-Channels).
  - **Fehlgeschlagener Poll** (Helix-Fehler): wie bisher Tick überspringen, keine
    Events, In-Memory-Baseline bleibt unverändert stehen.

## 3. API: neuer SSE-Endpoint für die Übersicht

- `GET /api/channels/live-events`, registriert in
  `LiveEndpoints`, hinter dem normalen Session-Auth-Filter, **kein** Rollencheck.
- Typfilter: ausschließlich `live.changed`.
- **Keine Per-User-Filterung:** Events sind selten (nur echte Zustandswechsel
  getrackter Channels), der Reload holt ohnehin nur die eigenen Channels über
  `GET /api/channels/mine`.
- Der Admin-Stream braucht keine Endpoint-Änderung — nur die erweiterte
  `AdminTypes`-Liste (Abschnitt 1).

## 4. Frontend

- **Übersichtsseite (`overview-page.ts`):**
  - Umbau von „einmal im Konstruktor laden" auf `rxResource` + `reload()` — das
    Muster der Admin-Seiten.
  - `liveEvents(...)`-Abo auf `live.changed` → `reload()`.
  - Das lokale Patchen in `reactivate()` (`isTracked`/`isBotActive`) bleibt erhalten.
  - **Tooltip-Fix:** `liveAgeMinutes` liest ein tickendes Timer-Signal statt
    `Date.now()` direkt im `computed()`.
- **Admin-Channel-Liste (`admin-channels-page.ts`):** `live.changed` zusätzlich zu
  `channel.synced` in die abonnierten Event-Typen aufnehmen (eine Zeile).
- Neuer Event-Typ in der Frontend-Konstante `LIVE_EVENT_TYPES`.

## 5. Tests & Verifikation

- **`Worker.Tests`** (container-frei): `LiveStatusDiff` — keine Baseline ⇒ keine
  Events; live→offline; offline→live; unverändert ⇒ leer; Baseline-Übernahme aus
  Redis-Snapshot.
- **`Api.Tests`**: der neue SSE-Endpoint bekommt seinen Fall in der
  Filter-Matrix (401 ohne Session; Regel 11).
- **Frontend:** kein neuer Spec nötig, sofern in `core/` nichts Neues entsteht
  (der `liveEvents`-Service existiert und ist getestet); die Seiten-Umbauten sind
  Feature-Komponenten (Regel 12: Live-Testen im Browser).
- **Live-Verifikation vor dem Commit (Regel 16):** Dev-Stack; `live.changed` per
  `redis-cli PUBLISH` auf `live:events` simulieren → Übersicht lädt sichtbar neu;
  zusätzlich den echten Diff im Worker-Log beobachten (Poll-Intervall temporär
  niedrig konfigurieren).

## Explizit außerhalb des Scopes

- Keine DB-Migration, keine neuen `ApiErrorCodes`/i18n-Einträge.
- Keine neuen Dauer-Controls im UI (Frontend-Zurückhaltung).
- Kein Live-Badge auf Channel-Workspace-Seiten (rendert dort heute kein `liveState`;
  der Event-Typ fließt über `ChannelTypes` aber schon jetzt mit).
- Kein Per-User-Eventfilter im SSE-Endpoint.
