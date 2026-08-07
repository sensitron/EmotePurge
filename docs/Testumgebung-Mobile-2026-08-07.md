# Mobile-Testumgebung (`dev.home.sensitron.me`)

Die App auf einem echten Telefon prüfen, ohne nach Produktion zu deployen und ohne USB-Kabel. Das Handy ruft `https://dev.home.sensitron.me` im heimischen WLAN auf und landet auf dem lokalen Angular-Dev-Server — mit echtem Zertifikat, echtem Twitch-Login, denselben Testdaten wie am Rechner und Hot Reload.

Es gibt bewusst **keine** eigene Staging-Stage: die Umgebung *ist* die lokale Entwicklungsumgebung, nur unter einem anderen Namen erreichbar. Begründung im Entscheidungslog, Eintrag vom 2026-08-07.

## Starten

Drei Prozesse, drei Terminals:

```
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api --launch-profile lan
npm --prefix web run start:lan
```

Dann am Handy `https://dev.home.sensitron.me` aufrufen. Änderungen an `web/` erscheinen ohne Neuladen.

Der Unterschied zum Alltagsstart (`dotnet run --project src/EmotePurge.Api` + `npm --prefix web start`) sind genau zwei Dinge: das Launch-Profil `lan` setzt die Twitch-Redirect-URI auf den neuen Hostnamen um, und die Serve-Configuration `lan` lässt den Dev-Server auf allen Netzwerkschnittstellen lauschen statt nur auf `localhost`. Alles andere — Datenbank, Redis, Worker, Seed-Daten — ist identisch, weil es dieselben Container sind.

Der Worker läuft nicht mit; wer Chat-Zählung oder 7TV-Sync braucht, startet ihn wie sonst per `dotnet run --project src/EmotePurge.Worker` oder lässt `docker compose up -d worker` mitlaufen.

## Topologie

```
Handy (WLAN)
  │  https://dev.home.sensitron.me
  ▼
AdGuard 192.168.178.4   →  Wildcard-Rewrite *.home.sensitron.me
  ▼
Nginx Proxy Manager 192.168.178.5   →  TLS-Terminierung (Let's-Encrypt-Wildcard)
  │  http, X-Forwarded-Proto: https
  ▼
Dev-PC :4200   ng serve
  │  /api  →  proxy.conf.json
  ▼
Dev-PC :5151   dotnet run   →  Postgres/Redis aus docker-compose.yml
```

Zwei Proxy-Hops, aber nur ein NPM-Eintrag: `/api` bleibt beim bestehenden Angular-Dev-Proxy, damit die Topologie dieselbe ist wie beim Arbeiten am Rechner. Same-origin bleibt sie dadurch auch — kein CORS, kein `withCredentials`.

Nichts davon ist von außen erreichbar. `*.home.sensitron.me` existiert öffentlich nicht (nur als AdGuard-Rewrite im LAN), am Router wird kein Port geöffnet. Unterwegs, im Mobilfunknetz, funktioniert die Adresse nicht — das ist Absicht und keine Lücke.

## Einmalige Einrichtung

Erledigt am 2026-08-07. Hier festgehalten, damit es nach einem NPM-Umzug, einem neuen Rechner oder einer neuen Twitch-App nachvollziehbar bleibt.

**1. Feste IP für den Dev-PC** — Fritz!Box, *Heimnetz → Netzwerk → Gerät → Bearbeiten*, „Diesem Netzwerkgerät immer die gleiche IPv4-Adresse zuweisen". Ohne das zeigt der Proxy-Host irgendwann ins Leere.

**2. Windows-Firewall, eingehend TCP 4200, nur Profil „Privat"**:

```powershell
New-NetFirewallRule -DisplayName "Angular dev server (LAN)" -Direction Inbound -Protocol TCP -LocalPort 4200 -Profile Private -Action Allow
```

**3. Proxy-Host in NPM** — *Hosts → Proxy Hosts → Add Proxy Host*:

| Feld | Wert |
|---|---|
| Domain Names | `dev.home.sensitron.me` |
| Scheme / Forward Hostname / Port | `http` / IP des Dev-PCs / `4200` |
| Websockets Support | **an** (sonst kein Live-Reload) |
| SSL Certificate | `*.home.sensitron.me` |
| Force SSL | **an** |

Ein DNS-Eintrag ist nicht nötig: AdGuards Wildcard-Rewrite deckt jeden neuen Namen unter `home.sensitron.me` bereits ab.

**4. Twitch Developer Console** — in der App **`EmotePurgeDev`** unter *OAuth Redirect URLs* `https://dev.home.sensitron.me/api/auth/twitch/callback` eintragen und speichern. Zeichengenau, `https`, kein abschließender Schrägstrich.

**Es gibt zwei Twitch-Apps, und das ist die Falle bei diesem Schritt.** `EmotePurgeDev` ist die Entwicklungs-App, eine zweite gehört zu `emotepurge.app`. Seit dem 2026-08-07 benutzt **jeder lokale Startweg** die Entwicklungs-App: `docker compose` über `TWITCH_CLIENT_ID` aus der `.env`, `dotnet run` über die User-Secrets des Api-Projekts. Vorher lief `dotnet run` gegen die Produktions-App — genau diese Verwechslung hat den ersten Live-Test gekostet. `EmotePurgeDev` braucht deshalb **alle drei** Redirect-URLs:

```
http://localhost:5151/api/auth/twitch/callback          (dotnet run)
http://localhost:8080/api/auth/twitch/callback          (docker compose)
https://dev.home.sensitron.me/api/auth/twitch/callback  (Handy)
```

Wer die Zugangsdaten neu setzen muss, holt sie aus der `.env` (Werte werden dabei nicht ausgegeben):

```powershell
$vals = @{}; Get-Content .env | ForEach-Object { if ($_ -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)$') { $vals[$matches[1]] = $matches[2].Trim() } }
dotnet user-secrets set "Auth:Twitch:ClientId" $vals['TWITCH_CLIENT_ID'] --project src/EmotePurge.Api
dotnet user-secrets set "Auth:Twitch:ClientSecret" $vals['TWITCH_CLIENT_SECRET'] --project src/EmotePurge.Api
```

## Was im Repo dafür geändert wurde

Alles additiv — `npm start` und `dotnet run` ohne Profil verhalten sich unverändert, `appsettings.Development.json` ist nicht angefasst.

- `web/angular.json` — Serve-Configuration `lan`: `host: "0.0.0.0"` plus `allowedHosts: ["dev.home.sensitron.me"]`. Der Vite-basierte Dev-Server lehnt fremde `Host`-Header sonst ab, und NPM reicht genau diesen Namen durch (`proxy_set_header Host $host`). `buildTarget` muss die Configuration selbst mitbringen, weil `--configuration lan` das `defaultConfiguration: "development"` ersetzt.
- `web/package.json` — Script `start:lan`.
- `src/EmotePurge.Api/Properties/launchSettings.json` — Profil `lan` mit `Auth__Twitch__RedirectUri` und `Auth__Twitch__PostLoginRedirectUrl`. Umgebungsvariablen schlagen `appsettings.Development.json`, dessen `PostLoginRedirectUrl` auf `http://localhost:4200/` zeigt.

## Wenn etwas klemmt

**Login endet mit `InvalidOAuthState` — und in der Adresszeile steht `emotepurge.app`.** Dann ist die Redirect-URI in der Twitch-App nicht hinterlegt, und die Meldung kommt gar nicht aus dieser Umgebung: Twitch lehnt die unbekannte URI ab und schickt `?error=redirect_mismatch&state=…` an eine *registrierte* Adresse — also an Produktion. Deren Callback sieht einen `state` ohne `code` und antwortet mit genau diesem Fehlercode. Das Symptom zeigt damit auf die falsche Maschine. Gegenprobe, die in zwei Sekunden Klarheit schafft — sie liest aus, was die Api wirklich an Twitch schickt:

```powershell
$req = [System.Net.HttpWebRequest]::Create("https://dev.home.sensitron.me/api/auth/twitch/login")
$req.AllowAutoRedirect = $false
try { $res = $req.GetResponse() } catch [System.Net.WebException] { $res = $_.Exception.Response }
$loc = $res.Headers['Location']; $res.Close()
foreach ($p in ([uri]$loc).Query.TrimStart('?').Split('&')) {
  $kv = $p.Split('=',2)
  if ($kv[0] -in @('redirect_uri','client_id')) { "$($kv[0]): >$([uri]::UnescapeDataString($kv[1]))<" }
}
```

Stimmt die ausgegebene `redirect_uri`, liegt es an der Registrierung — in der Twitch-Console die App mit der ausgegebenen Client-ID öffnen und prüfen, ob der Eintrag dort steht **und gespeichert wurde**.

**Login bricht mit `InvalidOAuthState` ab, ohne dass die Adresszeile auf `emotepurge.app` zeigt.** Dann ist `X-Forwarded-Proto: https` auf dem Weg NPM → `ng serve` → Api verloren gegangen. Das State-Cookie wird in `AuthEndpoints.cs` mit `Secure = Request.IsHttps` gesetzt; ohne den Header fehlt das Flag, und der Browser liefert das Cookie im Callback nicht zurück. Ausweg: NPM auf zwei Locations umstellen (`/` → `:4200`, `/api` → `:5151`) und Kestrel per `--urls http://0.0.0.0:5151` auf alle Schnittstellen binden — dann kommt der Header direkt vom Proxy. Kostet eine zweite Firewall-Regel.

**Seite lädt, aber Login lässt einen sofort wieder als anonym dastehen.** Kein Session-Cookie: das Auth-Cookie ist `SecurePolicy.Always` (bewusst, s. Kommentar in `Program.cs`). Prüfen, ob „Force SSL" im Proxy-Host wirklich an ist — über `http://` funktioniert diese Umgebung grundsätzlich nicht.

**Twitch antwortet mit `redirect_uri mismatch`.** Die URI im `lan`-Profil und die in der Twitch-Console weichen ab — Schema, Host und Pfad müssen zeichengleich sein.

**Der Dev-Server antwortet mit „Blocked request".** `allowedHosts` in `web/angular.json` passt nicht zum aufgerufenen Namen.

**Der Login klappt, danach ist nur der Hintergrund zu sehen — in jedem Browser, auch nach hartem Neuladen.** In der Konsole steht dann `Failed to fetch dynamically imported module: …/chunk-XXXXXXXX.js`, im Netzwerk-Tab ein `504 Gateway Time-out` auf genau diesen Chunk. Nicht der Dev-Server ist schuld: **NPM hat den Fehler zwischengespeichert.** Die Option „Cache Assets" im Proxy-Host legt eine eigene Location für `.js`/`.css`/`.ico` an, und wenn der Dev-Server einmal unten war, landen die 504er dieser Runde mit Ablaufdatum im Cache. Der betroffene Chunk bleibt tot, bis er abläuft — die anderen, die im Fehlerfenster niemand angefragt hat, funktionieren weiter. Das erklärt auch, warum die Landing-Seite noch rendert und erst der Login ins Leere führt: sie ziehen verschiedene Lazy-Chunks.

Nachweis in einem Befehl — derselbe Pfad, einmal mit Query-String:

```
curl -sk -o /dev/null -w "%{http_code}\n" https://dev.home.sensitron.me/chunk-XXXXXXXX.js
curl -sk -o /dev/null -w "%{http_code}\n" https://dev.home.sensitron.me/chunk-XXXXXXXX.js?cachebust=1
```

`504` und `200` heißt Cache. **Abhilfe: „Cache Assets" im Proxy-Host ausschalten** — bei einem Dev-Server ist die Option ohnehin falsch, weil die Chunk-Hashes sich mit jedem Rebuild ändern und ein Cache darüber verlässlich Leichen ausliefert.

**Das Handy zeigt weiter eine alte Version — auch nach hartem Neuladen, und obwohl der Rechner das Neue sieht.** Dieselbe Ursache, eine Ebene weiter: „Cache Assets" hat den `.js`-Dateien ein `Cache-Control: max-age=11808` mitgegeben, gut drei Stunden. Diese Einträge liegen im **Browser**, nicht im Proxy — das Abschalten der Option nimmt sie nicht zurück, und ein Reload holt sie nicht neu, weil der Browser sie für frisch hält. Einmal den Seiten-Cache löschen (Chrome Android: Schloss-Symbol in der Adresszeile → Berechtigungen/Website-Einstellungen → Daten löschen), danach ist Ruhe. Wer das nicht merkt, testet stundenlang gegen ein altes Bundle und schiebt jede Beobachtung auf den Code — es lohnt sich, bei einer Verhaltensänderung, die nicht ankommt, zuerst eine sichtbare Probe einzubauen (eine Farbe, ein Rahmen) statt am Verhalten weiterzumessen.

**Handy lädt nicht neu nach einer Änderung.** Vites HMR-WebSocket kommt nicht durch — „Websockets Support" im Proxy-Host prüfen. Bis dahin: manuell neu laden, die Umgebung ist ansonsten voll benutzbar.

**Testdaten fehlen.** Dann zeigt `dotnet run` auf eine andere Datenbank als der Compose-Stack: `appsettings.json` hält `Password=change-me`, das muss zu `POSTGRES_PASSWORD` in der `.env` passen.
