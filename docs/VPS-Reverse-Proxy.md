# VPS-Reverse-Proxy (nginx) für emotepurge.app

Stand: 2026-08-29 (nachgezogen nach dem Cloudflare-Umzug, s. Abschnitt „Cloudflare davor" — davor 2026-08-05, VPS-Härtung vom 2026-08-04). Der host-native nginx (1.24, Ubuntu) ist **nicht** Teil dieses Repos — seine Config liegt auf dem VPS (`/etc/nginx/`, Certbot-verwaltet) und bedient neben emotepurge.app weitere, projektfremde vHosts. Diese Datei dokumentiert den emotepurge-relevanten Ausschnitt und die Verträge, die Api-Code und Proxy miteinander eingehen, damit „was macht der Proxy?" ohne SSH beantwortbar ist. Der vollständige Host-Stand (alle vHosts, Firewall, Catchall) steht im privaten Repo `sensitron/infra-docs`, `VPS-und-Homelab-2026-08-04.md`.

## Der emotepurge-Block (sinngemäß, sanitisiert)

```nginx
# global (mit anderen vHosts geteilt):
# seit 2026-08-29, MUSS vor den Zonen stehen: sonst schlüsseln die auf CF-Edge-IPs
set_real_ip_from <15 IPv4- + 7 IPv6-Bereiche von cloudflare.com/ips-v4 bzw. /ips-v6>;
real_ip_header CF-Connecting-IP;
real_ip_recursive on;

limit_req_zone $binary_remote_addr zone=general:10m rate=10r/s;
limit_req_zone $binary_remote_addr zone=api:10m rate=5r/s;   # eigene /api/-Zone, seit 2026-08-04

server {
    server_name emotepurge.app www.emotepurge.app;

    # seit 2026-08-04: /etc/nginx/snippets/emotepurge-hardening.conf, enthält
    #   location /api/  →  limit_req zone=api burst=20,
    #                      proxy_buffering off, proxy_read_timeout 3600s (SSE),
    #                      Anti-Bot-444-Regexe (Scanner-Pfade)
    include snippets/emotepurge-hardening.conf;

    location / {
        limit_req zone=general burst=20 nodelay;
        proxy_pass http://127.0.0.1:4300/;    # Api-Container (liefert SPA + API zusammen)
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    listen [::]:443 ssl http2 ipv6only=on;    # managed by Certbot; http2 explizit seit 2026-08-01
    listen 443 ssl http2;                      # managed by Certbot; http2 explizit seit 2026-08-01
    # ssl_certificate …/emotepurge.app/… (Certbot), + HTTP→HTTPS-Redirect-Block auf :80
}
```

Exakte Werte (Rate der `api`-Zone, die Anti-Bot-Regexe) stehen im infra-docs-Bericht — diese Skizze hält nur den Vertrag fest.

## Verträge zwischen Api und Proxy

| Vertrag | Api-Seite | Proxy-Seite |
|---|---|---|
| **SSE-Buffering** | Antwort setzt `X-Accel-Buffering: no` (via `Response.OnStarting`, s. `LiveEndpoints.cs`) | Seit 2026-08-04 steht **zusätzlich** `proxy_buffering off` explizit im `/api/`-Snippet (Gürtel + Hosenträger); vorher trug der Header allein (live verifiziert 2026-08-01, Pings kamen einzeln durch). |
| **SSE-Timeout** | Broker-Heartbeat alle 15 s | Seit 2026-08-04: `proxy_read_timeout 3600s` für `/api/` (Snippet). Der nginx-Default **60 s** gilt nur noch für Nicht-API-Pfade; die alte Regel „Heartbeat-Intervall darf nie über 60 s wachsen" ist damit entschärft, bleibt aber als Untergrenze sinnvoll. |
| **Forwarded Headers** | `ForwardedHeadersMiddleware` mit leeren `KnownProxies` (vertraut jedem Absender) | `X-Forwarded-Proto`/`-For` werden gesetzt (Review-Punkt von 2026-07-29 damit geschlossen). Das blinde Vertrauen ist okay, weil `127.0.0.1:4300` nur lokal erreichbar ist — **den Port nie auf 0.0.0.0 binden.** |
| **Security-Header** | HSTS, `X-Content-Type-Options`, `Referrer-Policy`, CSP setzt die Api selbst (`Program.cs`) | Bewusst keine `add_header`-Zeilen im emotepurge-Block (anders als bei den anderen vHosts) — sonst gäbe es Doppelungen. |
| **Rate-Limit** | Eigene Policies pro User (`ExternalApi`, `Bookkeeping`); SSE bewusst ohne, stattdessen Verbindungs-Limits | Seit 2026-08-04 hat `/api/` eine **eigene** Zone `api` (burst 20, im Snippet) — nur die SPA-Auslieferung unter `/` läuft noch über die mit anderen vHosts geteilte `general`-Zone. SSE zählt nur beim Verbindungsaufbau (Reconnect alle 10 min) → unkritisch. Überschreitung liefert **503**, was `EventSource` als fatal wertet (Retry erst bei Tab-Refokus). |

## Bekannte Eigenheiten

- **Seit der VPS-Härtung 2026-08-04 gilt hostweit:** `server_tokens off` (`conf.d/00-hardening.conf`) und ein Catchall-`default_server` auf :80/:443, der Zugriffe per IP oder fremdem Host-Header mit `444` beendet (`00-default-catchall.conf`) — `emotepurge.app` ist davon nicht betroffen, nur nicht-passende Host-Header. SSH auf den VPS läuft seit dem 2026-08-04 nur noch als sudo-User (`PermitRootLogin no`).
- **HTTP/2 steht seit 2026-08-01 explizit in den `listen`-Zeilen** (vorher nur implizit vom Listen-Socket der anderen vHosts geerbt — nginx aktiviert HTTP/2 pro Socket, nicht pro Server-Block). Wichtig, weil ohne HTTP/2 das 6-Verbindungen-pro-Origin-Limit von HTTP/1.1 gilt und mehrere Tabs mit offenen SSE-Streams sich gegenseitig aushungern könnten. Prüfbar ohne Login: HTTP-Version eines `https://emotepurge.app`-Requests muss 2.0 sein.
- **WebSockets sind nicht konfiguriert** (kein `Upgrade`/`Connection`-Header-Paar im emotepurge-Block). Für SSE irrelevant; falls je SignalR/rohe WS dazukommen, braucht der entsprechende Pfad eine eigene `location` mit `proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";` und erhöhtem `proxy_read_timeout`.
- Der Config-Kommentar „Angenommener Port fuer die Angular-SPA" ist historisch — auf 4300 lauscht der Api-Container aus `docker-compose.prod.yml`, der die SPA aus `wwwroot/` mit ausliefert; es gibt keinen separaten Frontend-Prozess.

## Cloudflare davor (seit 2026-08-27 in den Logs sichtbar)

`emotepurge.app` steht seit kurzem hinter dem Cloudflare-Proxy — die Kette ist damit
**Client → Cloudflare → nginx → Kestrel**, nicht mehr Client → nginx → Kestrel. Die
Origin-IP steht in keinem DNS-Eintrag. Drei Dinge, die daraus folgen und die man bei jeder
Fehlersuche zuerst wissen muss:

- **Ein 429 im Browser kommt nie von nginx.** `limit_req` antwortet mit **503**. Ein 429
  stammt entweder vom Rate-Limiter der Api (nackt, ohne Body — `Program.cs`) oder von
  Cloudflare selbst. Das Frontend kann die beiden nicht unterscheiden:
  `apiErrorTranslationKey` mappt auf den Statuscode, beide landen auf
  `errors.status.rateLimited`. Unterscheidungsmerkmal ist der Response-Header `cf-ray`
  (Cloudflare) bzw. dessen Fehlen.
- **Was Cloudflare abweist, erreicht den Origin nie** und steht in keinem nginx-Log. Fehlt
  eine erwartete Zeile im access.log, ist das der erste Verdacht — dann in die Security
  Events im CF-Dashboard schauen, nicht weiter auf dem VPS suchen.
- **`real_ip` ist Voraussetzung dafür, dass die Zonen überhaupt sinnvoll metern.** Ohne
  `set_real_ip_from` + `real_ip_header CF-Connecting-IP` sieht nginx nur CF-Edge-IPs und
  wirft die halbe Besucherschaft in einen Eimer. Messbar: am 2026-08-27 sprangen die
  `limiting requests`-Einträge im error.log auf 2.001 (activitytracker.icu) + 1.003
  (emotepurge.app) an einem Tag, gegenüber 3–191 pro Tag davor — beide Sites gleichzeitig,
  weil sie sich die Zone `general` teilen. Behoben am 2026-08-29. Nebeneffekt derselben
  Zeilen: `X-Forwarded-For` trägt seither wieder die Client-IP, womit auch
  `Connection.RemoteIpAddress` in der Api stimmt (relevant nur für den IP-Fallback des
  Rate-Limiters bei anonymen Requests, also praktisch `/api/health`).

**SSH geht nicht über die Domain** — Cloudflare proxyt nur HTTP/HTTPS. Deploys der
nginx-Config laufen über die Origin-IP; Prozedur in `sensitron/infra-docs`,
`configs/nginx/README.md`.

**Noch offen:** Ob SSE (`/api/channels/live-events`, `/api/channels/{c}/live`) durch
Cloudflare unverändert durchkommt, ist nicht systematisch geprüft. `/api/` kommt als
`cf-cache-status: DYNAMIC` durch, das Snippet setzt `proxy_buffering off` und die Api
sendet `X-Accel-Buffering: no` — aber ein LIVE-Badge, das ohne Reload umspringt, hat seit
dem Umzug niemand bewusst beobachtet. Ebenfalls zu prüfen: ob Cloudflares Auto Minify und
Rocket Loader aus sind.
