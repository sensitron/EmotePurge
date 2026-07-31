# VPS-Reverse-Proxy (nginx) für emotepurge.app

Stand: 2026-08-01. Der host-native nginx (1.24, Ubuntu) vor dem Loopback-Port ist **nicht** Teil dieses Repos — seine Config liegt auf dem VPS (`/etc/nginx/`, Certbot-verwaltet) und bedient neben emotepurge.app weitere, projektfremde vHosts. Diese Datei dokumentiert den emotepurge-relevanten Ausschnitt und die Verträge, die Api-Code und Proxy miteinander eingehen, damit „was macht der Proxy?" ohne SSH beantwortbar ist.

## Der emotepurge-Block (sinngemäß, sanitisiert)

```nginx
# global (mit anderen vHosts geteilt):
limit_req_zone $binary_remote_addr zone=general:10m rate=10r/s;

server {
    server_name emotepurge.app www.emotepurge.app;

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

## Verträge zwischen Api und Proxy

| Vertrag | Api-Seite | Proxy-Seite |
|---|---|---|
| **SSE-Buffering** | Antwort setzt `X-Accel-Buffering: no` (via `Response.OnStarting`, s. `LiveEndpoints.cs`) | Kein `proxy_buffering off` nötig — der Header schaltet es pro Response ab. **Live verifiziert 2026-08-01** (Pings kommen einzeln durch). |
| **SSE-Timeout** | Broker-Heartbeat alle 15 s | `proxy_read_timeout` steht nicht in der Config → nginx-Default **60 s** > 15 s. Heartbeat-Intervall darf nie über dieses Timeout wachsen. |
| **Forwarded Headers** | `ForwardedHeadersMiddleware` mit leeren `KnownProxies` (vertraut jedem Absender) | `X-Forwarded-Proto`/`-For` werden gesetzt (Review-Punkt von 2026-07-29 damit geschlossen). Das blinde Vertrauen ist okay, weil `127.0.0.1:4300` nur lokal erreichbar ist — **den Port nie auf 0.0.0.0 binden.** |
| **Security-Header** | HSTS, `X-Content-Type-Options`, `Referrer-Policy`, CSP setzt die Api selbst (`Program.cs`) | Bewusst keine `add_header`-Zeilen im emotepurge-Block (anders als bei den anderen vHosts) — sonst gäbe es Doppelungen. |
| **Rate-Limit** | Eigene Policies pro User (`ExternalApi`, `Bookkeeping`); SSE bewusst ohne, stattdessen Verbindungs-Limits | Zusätzlich nginx-seitig 10 r/s pro IP (burst 20, `nodelay`) auf **alles** — Zone mit den anderen vHosts geteilt. SSE zählt nur beim Verbindungsaufbau (Reconnect alle 10 min) → unkritisch. Überschreitung liefert **503**, was `EventSource` als fatal wertet (Retry erst bei Tab-Refokus). |

## Bekannte Eigenheiten

- **HTTP/2 steht seit 2026-08-01 explizit in den `listen`-Zeilen** (vorher nur implizit vom Listen-Socket der anderen vHosts geerbt — nginx aktiviert HTTP/2 pro Socket, nicht pro Server-Block). Wichtig, weil ohne HTTP/2 das 6-Verbindungen-pro-Origin-Limit von HTTP/1.1 gilt und mehrere Tabs mit offenen SSE-Streams sich gegenseitig aushungern könnten. Prüfbar ohne Login: HTTP-Version eines `https://emotepurge.app`-Requests muss 2.0 sein.
- **WebSockets sind nicht konfiguriert** (kein `Upgrade`/`Connection`-Header-Paar im emotepurge-Block). Für SSE irrelevant; falls je SignalR/rohe WS dazukommen, braucht der entsprechende Pfad eine eigene `location` mit `proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";` und erhöhtem `proxy_read_timeout`.
- Der Config-Kommentar „Angenommener Port fuer die Angular-SPA" ist historisch — auf 4300 lauscht der Api-Container aus `docker-compose.prod.yml`, der die SPA aus `wwwroot/` mit ausliefert; es gibt keinen separaten Frontend-Prozess.
