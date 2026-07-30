# Untersuchung: 7TV-Schreib-Token per Login-Redirect statt manuellem Kopieren? (2026-07-30)

**Anlass:** Die neue offizielle API-Doku unter `https://7tv.app/api/docs` dokumentiert einen
Login-Endpoint. Frage: Können wir das 7TV-Schreib-Token für die Mass-Delete-Engine automatisiert
über einen Login-Redirect-Flow beschaffen, statt Nutzer es manuell aus den DevTools kopieren zu
lassen?

**Methodik:** Repo-Rekonstruktion (Sub-Agent), externe Recherche gegen OpenAPI-Spec,
Server-Quellcode `SevenTV/SevenTV` und web.archive.org (Sub-Agent), plus eigene empirische Probe
der Redirect-Kette bis zur Twitch-Maske (ohne Anmeldung, Wegwerf-Skript, keine Tokens
involviert). Alle urteilstragenden Behauptungen wurden an den Rohquellen nachgeprüft.
Kennzeichnung durchgehend: **[belegt]** / **[plausibel]** / **[widerlegt]** /
**[nicht überprüfbar]**.

---

## 1. Fazit vorab

**Es geht nicht, und zwar aus drei unabhängigen, im Server-Code verankerten Gründen — der
manuelle Weg bleibt.** Der dokumentierte Login-Endpoint (`GET /v4/auth/login`) ist der interne
Login der 7TV-Website, kein Dritt-App-OAuth: (1) `Referer`, `Origin` und `return_to` werden
gegen eine feste Allowlist aus 7TV-eigenen Origins geprüft — `return_to=https://emotepurge.app/`
liefert empirisch **403 Forbidden** [belegt]; (2) die `redirect_uri` gegenüber Twitch wird
serverseitig aus dem Referer gebaut und landet damit immer auf `7tv.app/login/callback`, nie bei
uns [belegt]; (3) der PKCE-Verifier liegt in einem `HttpOnly`/`SameSite=Strict`-Cookie auf
`api.7tv.app`, unerreichbar für fremde Origins [belegt]. Eine Client-Registrierung für
Dritt-Apps existiert nicht; der einzige je angedachte manuelle Token-Bezug (`/v3/auth/manual`)
trägt im Code den Kommentar „won't be implemented" [belegt].

Die gute Nachricht nebenbei: Die damals „blind" getroffene Entscheidung war korrekt — die Doku
existierte zum Entscheidungszeitpunkt nachweislich noch nicht (sie ging zwischen 2026-07-26 und
2026-07-29 live [belegt]), und auch heute öffnet sie nur die Sicht auf den internen Flow, nicht
den Flow selbst. Empfehlung: Status quo behalten, gezielt die UX härten (Abschnitt 7).

---

## 2. Was wir damals entschieden haben

### 2.1 Fundstellen im Repo

- **Die Entscheidung ist keine dokumentierte Alternativen-Abwägung, sondern Tag-1-Spezifikation**
  [belegt]. Grundsatz 4 („Zero-Knowledge für Schreib-Tokens: 7TV-Access-Tokens mit
  Schreibrechten verbleiben _ausschließlich_ im Browser des Admins…", `Architectur.md:21`) und
  die Modul-D-Spec inkl. `RemoveEmote`-Mutation gegen `https://7tv.io/v3/gql`
  (`Architectur.md:124–135`) stehen unverändert seit dem initialen Commit `3e4d013`
  (2026-07-24) — vor jeglicher Implementierung.
- **Nirgendwo im Repo ist eine Prüfung eines automatisierten 7TV-Token-Bezugs dokumentiert**
  [belegt, im Sinne von: Abwesenheit nachgewiesen]. Kein Commit, kein `docs/DECISIONS.md`-Eintrag,
  kein Review-Befund erwähnt, dass ein 7TV-Login-/OAuth-Weg untersucht und verworfen wurde — im
  Kontrast zum Twitch-Auth-Pfad, wo Alternativen dokumentiert abgewogen wurden
  (`docs/DECISIONS.md:210–220`). Die Prämisse „geht nicht anders" war eine Annahme, kein Befund.
- Der `DECISIONS.md`-Eintrag „7TV API v3, nicht v4" (2026-07-25, `docs/DECISIONS.md:430–434`)
  betrifft nur REST/Sync, nicht Auth [belegt].
- Implementiert wurde die Engine ab Commit `e29b73f` (2026-07-26); die DevTools-Anleitung kam
  mit Commit `c2036f1` (2026-07-26) in die i18n-Dateien (`web/public/i18n/de.json:247–259`)
  [belegt].
- Der Review 2026-07-29 stufte zwei Annahmen als offen ein: ob `7tv-token` (noch) der korrekte
  `localStorage`-Key auf 7tv.app ist (`Review-2026-07-29.md:1215`), und das Fehlerformat von
  v3-GQL bei abgelaufenem Token (`:1217`, Befund S3-9). Befund S3-24 (Token nicht vorab
  hinterlegbar) ist ebenfalls offen [belegt].

### 2.2 Archive-Befund: Die Doku existierte zum Entscheidungszeitpunkt nicht

- Die Wayback-CDX-API hat **keine einzige Capture** von `7tv.app/api/docs` (Sanity-Check gegen
  `7tv.app` selbst liefert Captures — die API funktioniert) [belegt].
- Beweis über archivierte Build-Artefakte: Das SvelteKit-Route-Dictionary in den archivierten
  `entry/app.*.js`-Dateien der Captures vom **2026-07-21** und **2026-07-26 22:51 UTC** enthält
  **keine** `/api/docs`-Route (Dictionary intakt: `"/admin/tickets"` vorhanden, Node-IDs bis
  `"/upload":[66]`); der Live-Build vom 2026-07-30 enthält `"/api/docs":[29,[6]]`. Die Capture
  vom 2026-07-26 wurde von mir selbst heruntergeladen, entpackt und gegrept [belegt].
- Alle Live-Assets der Doku (inkl. `v3docs.json`/`v4docs.json`) tragen
  `last-modified: 2026-07-29 17:41 UTC` — der Deploy-Zeitstempel. **Die Doku-Seite ging also
  zwischen 2026-07-26 22:51 UTC und 2026-07-29 17:41 UTC live** [belegt], wahrscheinlich mit dem
  Deploy vom 2026-07-29 [plausibel].
- Einschränkung: Eine ältere, servergenerierte v3-Spec existiert seit 2024-10 unter
  `https://7tv.io/v3/docs` (Commit `a6c9d45d` im Monorepo) — deren Auth-Einträge sind aber
  parameterlos-leer und enthalten das schon damals als „won't be implemented" markierte
  `/v3/auth/manual` [belegt]. Wir hätten dort also auch 2026-07 nichts Nutzbares gefunden.

**Antwort auf die Ausgangsfrage der Rekonstruktion:** Wir haben die Doku nicht übersehen — es
gab sie noch nicht. Geprüft haben wir damals allerdings auch nichts; die Entscheidung war
richtig, aber unbelegt.

### 2.3 Bonus-Befund: Die offene Review-Frage zum Storage-Key ist jetzt geklärt

Der Website-Quellcode (`apps/website/src/lib/auth.ts`) definiert
`const LOCALSTORAGE_KEY = "7tv-token"` — unsere Anleitung („Local Storage → `7tv-token`") ist
damit **quellcodeseitig bestätigt** [belegt]. Die offene Frage aus `Review-2026-07-29.md:1215`
kann als beantwortet markiert werden (Einschränkung: Stand `main` 2026-05-29 des öffentlichen
Spiegels; die Produktion läuft auf einem neueren, nicht öffentlichen Commit
[nicht überprüfbar]).

---

## 3. Der Login-Flow heute (Spec + Server-Code + Messung)

### 3.1 Die Spec

Die Doku-Seite ist eine SvelteKit-SPA; die rohen Specs liegen unter
`https://7tv.app/api/docs/v4docs.json` und `…/v3docs.json` (statische Build-Assets, kein
API-Endpoint). Server-Angabe in beiden: `https://api.7tv.app` (`7tv.io` ist Alias) [belegt].

v4-Auth-Pfade laut Spec [belegt]:

| Pfad | Inhalt |
|---|---|
| `GET /v4/auth/login` | `platform` (required: twitch/discord/google/kick), `return_to` (optional). 400-Beispiel: `"can only login from website"` |
| `GET /v4/auth/link` | Plattform-Linking für den **bestehenden** Account |
| `POST /v4/auth/login-finish` | `{platform, code}` → `{token}` (Session-Token im JSON-Body) |
| `POST /v4/auth/logout` | Session-Invalidierung |

Auffällig: Die Spec definiert **keinerlei** `securitySchemes`, und der dokumentierte Pfad
`/v4/auth/login-finish` ist falsch — der Code registriert `/v4/auth/login/finish` [belegt].
Beides Indizien, dass die Doku frisch und nicht als Dritt-Entwickler-Vertrag gedacht ist
[plausibel].

### 3.2 Der tatsächliche Ablauf (Server-Code `SevenTV/SevenTV`, von mir an `auth.rs` nachgeprüft)

1. `GET /v4/auth/login?platform=twitch&return_to=…` prüft `Referer`, `Origin` **und**
   `return_to` jeweils gegen die Allowlist `{api_origin, website_origin, old_website_origin}`
   (`apps/api/src/http/v4/rest/auth.rs`, Fehlermeldungen `"can only login from website"` /
   `"origin mismatch"` / `"return_to origin mismatch"`) [belegt].
2. Der Server generiert PKCE, legt den Verifier in das Cookie `seventv-verifier`
   (`HttpOnly; SameSite=Strict; Secure; Domain=api.7tv.app; Max-Age=300`) und antwortet 303 auf
   `id.twitch.tv/oauth2/authorize` — mit 7TVs eigener Twitch-`client_id` und einer
   **aus dem Referer abgeleiteten** `redirect_uri` (`create_redirect_uri` →
   `https://7tv.app/login/callback?platform=twitch`); `return_to` wird als OAuth-`state`
   durchgereicht [belegt].
3. Nach dem Twitch-Login landet der OAuth-`code` auf `7tv.app/login/callback`; **die
   7TV-Website selbst** POSTet ihn an `/v4/auth/login/finish` und erhält das Token als
   JSON-Body (`{"token": …}`) — kein Auth-Cookie, kein URL-Fragment (das Fragment-Verfahren ist
   der Legacy-v3-Flow mit fest konfigurierter `old_website_origin`) [belegt].
4. Die Website legt das Token in `localStorage["7tv-token"]` ab — exakt der Wert, den unsere
   Nutzer heute kopieren [belegt].

### 3.3 Messergebnisse der eigenen Probe (2026-07-30, ohne Anmeldung)

Wegwerf-Skript im Scratchpad (`probe-login.sh`), beobachtet bis zur Twitch-Maske [belegt]:

- `return_to=https://7tv.app/` → `303 See Other` auf `id.twitch.tv/oauth2/authorize?client_id=jzsoiyuc9hnb5av6ehdj8adn2bierj&redirect_uri=https%3A%2F%2F7tv.app%2Flogin%2Fcallback…&response_type=code&code_challenge_method=S256&…`, `Set-Cookie: seventv-verifier=…; HttpOnly; SameSite=Strict; Secure; Domain=api.7tv.app; Max-Age=300`.
- **`return_to=https://emotepurge.app/` → `403 Forbidden`** — unabhängig davon, welcher
  Referer mitgesendet wird. Der Code-Befund ist damit empirisch bestätigt.
- CORS: `api.7tv.app` spiegelt beliebige Origins (`access-control-allow-origin:
  https://emotepurge.app`), gewährt aber `access-control-allow-credentials: true` **nur** den
  Allowlist-Origins. `authorization` steht in den erlaubten Headern — deshalb funktioniert unser
  heutiger Bearer-Zugriff aus dem Browser, und deshalb würde auch ein `login/finish`-Aufruf von
  uns aus technisch durchgehen — nur kommen wir nie legitim an den `code`, weil die
  `redirect_uri` bei Twitch auf 7TV registriert ist [belegt].
- Rate-Limit am Login-Endpoint: `x-ratelimit-login-limit: 10` pro 60 s [belegt].

### 3.4 Token-Eigenschaften

- **JWT** (`iss=seventv-api`, `sub`=User-ULID), Session zusätzlich DB-gestützt → per Logout
  sofort widerrufbar [belegt, aus `jwt.rs`/`auth.rs`].
- **Lebensdauer 30 Tage** (`expires_at: now + Duration::days(30)`) [belegt]. Ein „mitten in der
  Mass-Delete-Session abgelaufenes" Token ist damit selten; der realistische Invalidierungsfall
  ist ein 7TV-Logout des Nutzers.
- **Ein Token, beide GQL-Versionen:** Die `SessionMiddleware` (Cookie **oder**
  `Authorization: Bearer`) liegt über allen Routern (`/v3` und `/v4`) [belegt im Code].
  Konsistent damit: Unsere Live-Tests der Mass-Delete-Engine (v3-GQL) liefen erfolgreich mit
  Tokens, die aus der v4-Ära von 7tv.app kopiert wurden [plausibel als Inferenz; nicht isoliert
  gegen beide Endpunkte mit demselben Token getestet]. **Ein späterer v4-GQL-Umstieg unserer
  Mutationen erzwingt also keinen neuen Beschaffungsweg.**
- Es ist **dasselbe Token**, das der Nutzer heute manuell kopiert — der Login-Flow würde uns
  also nichts „Besseres" liefern, nur denselben Wert auf anderem Weg [belegt].

---

## 4. Eignung für Dritt-Apps

- **Nicht gegeben.** Kein `client_id`-/`redirect_uri`-Parameter, keine App-Registrierung, kein
  Scope-/Consent-Modell; `client_id`/`client_secret` im Code sind ausschließlich 7TVs eigene
  Plattform-Credentials [belegt]. `/v3/auth/manual`: „won't be implemented" [belegt].
- Selbst mit gefälschtem Referer/Origin (serverseitig möglich, im Browser nicht) landet der
  OAuth-`code` immer auf einer 7TV-Origin, weil die `redirect_uri` aus dem Referer gebaut und
  bei Twitch auf 7TV registriert ist [belegt im Code; der Vollweg wurde bewusst nicht per
  Live-Login getestet]. Die Allowlist-Funktion `root_origin_match` nutzt zwar ein
  `ends_with`-Suffix-Match (formal würde `xyz7tv.app` matchen) — für uns irrelevant, und
  ausnutzen wollen wir so etwas ohnehin nicht [belegt/plausibel].
- **ToS:** Der ToS-Text auf `7tv.app/tos` enthält keine API-/Automatisierungs-/Dritt-App-Klausel
  (Keyword-Suche im Text-Chunk; ob der Chunk vollständig ist: [nicht überprüfbar]). Es gibt
  keine Developer-Policy-Seite und keine GitHub-Issues/Discussions zu Dritt-App-Zugang
  (Discussions sind im Monorepo deaktiviert) [belegt/nicht überprüfbar]. Eine Mitbenutzung des
  Website-Logins wäre damit nicht explizit verboten, aber klar außerhalb des vorgesehenen
  Rahmens und jederzeit brechbar [plausibel].
- **Vergleich:** Chatterino7 hat gar keinen 7TV-Schreibzugriff (nur anonyme v3-GETs — kein
  Vorbild) [belegt]. Das einzige gefundene Schreib-Tool, `UberKitten/7tv-cli-mcp` (v4-GQL,
  2026-04), beschafft das Token **identisch manuell** aus `localStorage['7tv-token']` — per
  DevTools-Anleitung, Konsolen-Einzeiler `copy(localStorage.getItem('7tv-token'))` oder
  Bookmarklet [belegt]. Unser Verfahren ist also der Stand der Technik unter
  7TV-Dritt-Tools, nicht eine Verlegenheitslösung.

---

## 5. Sicherheitsbewertung gegen den Zero-Knowledge-Grundsatz

- **Der Status quo erfüllt Grundsatz 4 sauber** — vom Review 2026-07-29 am Code bestätigt
  (`sessionStorage` statt `localStorage`, Löschung bei Logout/Session-Ablauf/401/403, Token nur
  als `Authorization`-Header an `7tv.io`, nie an unser Backend) [belegt].
- **Ein hypothetischer Redirect-Flow wäre mit Zero-Knowledge sogar vereinbar gewesen** (Token
  ginge direkt an den Browser, wie bei 7TV selbst via `login/finish`-Response-Body — kein
  Fragment-Leak-Risiko im v4-Design). Die Frage stellt sich aber nicht, weil 7TV den Flow für
  fremde Origins blockiert. **Es gibt keinen Grund, den Grundsatz anzufassen.**
- Angriffsflächen, die wir uns durch den Verzicht ersparen: eigene Rückkehr-Route
  (Open-Redirect-/CSRF-Pflege), Token-Transport über URL-Parameter, Abhängigkeit von einem
  undokumentierten internen Flow, der ohne Ankündigung ändern kann (die Doku-Seite ist 1 Tag
  alt und enthält bereits einen falschen Pfad).
- Restrisiken des Status quo bleiben wie gehabt: XSS (Mitigation: CSP), Nutzer könnte das Token
  in falsche Felder pasten, und die bekannte Lücke **S3-9** (HTTP 200 + `errors[]` invalidiert
  das Token nicht — der Lauf feuert mit ungültigem Token weiter) [belegt, weiterhin offen].

---

## 6. Optionen mit Aufwand/Risiko

| Option | Aufwand | Risiko | Bewertung |
|---|---|---|---|
| **A: Status quo unverändert** | 0 | Anleitung bricht, wenn 7TV den Storage-Key ändert (überwachbar; Key jetzt quellcodeseitig bestätigt) | Funktioniert, live-getestet; UX-Schwächen (S3-24, S3-9) bleiben |
| **B: Login-Redirect-Flow** | — | — | **Widerlegt/unmöglich:** 403 auf fremde `return_to`, Referer-basierte `redirect_uri`, PKCE-Cookie auf `api.7tv.app`, keine Client-Registrierung |
| **C: Status quo + UX-Härtung** (Empfehlung) | klein–mittel | gering | Einzelne Bausteine unten |
| **D: 7TV um offiziellen Dritt-App-Zugang bitten** (Issue/Kontakt) | klein (ein Issue), Erfolg ungewiss | keins | Legitimer Weg zu einer echten Lösung; Monorepo-Issues sind aktiv, Discussions deaktiviert |

Bausteine für Option C, unabhängig voneinander umsetzbar:

1. **Konsolen-Einzeiler in die Anleitung aufnehmen:** `copy(localStorage.getItem('7tv-token'))`
   ist schneller und weniger fehleranfällig als das Navigieren durch den Application-Tab.
   Sicherheitsabwägung: Nutzer ans Einfügen von Code in die Konsole zu gewöhnen ist ein
   bekanntes Self-XSS-Risiko-Muster; vertretbar, wenn die Anleitung dazusagt, *nie* fremden
   Code dort einzufügen. Der reine DevTools-Weg bleibt als Alternative dokumentiert.
2. **Bookmarklet** (wie `7tv-cli-mcp`): ein Lesezeichen, das auf 7tv.app geklickt das Token in
   die Zwischenablage kopiert. Gleiches Muster-Risiko wie (1) in abgeschwächter Form; ein von
   uns ausgeliefertes Bookmarklet ist auditierbar und ändert sich nicht. Vertretbar [plausibel].
3. **S3-24 schließen:** Token vorab auf einer Einstellungs-/Profilseite hinterlegbar machen
   (weiterhin `sessionStorage`), statt nur im Lösch-Modal.
4. **Token-Vorabvalidierung:** Vor dem Lauf eine harmlose authentifizierte Query gegen
   `7tv.io/v3/gql` (z. B. Actor/Me) schicken, statt die Gültigkeit erst am ersten `RemoveEmote`
   zu entdecken. Nebeneffekt: klärt empirisch das Fehlerformat (S3-9).
5. **S3-9 fixen:** Auth-Fehler in HTTP-200-`errors[]`-Antworten erkennen und das Token genauso
   verwerfen wie bei 401/403.
6. **Anleitung um Lebensdauer-Hinweis ergänzen:** Token gilt ~30 Tage, wird aber bei
   7TV-Logout sofort ungültig.

---

## 7. Empfehlung + offene Fragen

**Empfehlung:** Option C — beim manuellen Verfahren bleiben und es gezielt härten (Reihenfolge:
5 → 4 → 3 → 1; Bookmarklet optional). Option B ist keine Entscheidung, sondern eine
Unmöglichkeit; sie sollte in `docs/DECISIONS.md` als geprüft-und-verworfen festgehalten werden,
damit die Frage nicht erneut aufkommt — diesmal *mit* Beleg, im Unterschied zu 2026-07-24.
Option D (7TV direkt fragen) kostet ein Issue und ist der einzige Weg zu einer echten
Verbesserung; sie kann parallel laufen.

**Nur der Projekteigner kann entscheiden:**

1. Soll Option D (öffentliches Issue im `SevenTV/SevenTV`-Repo bzw. Discord-Anfrage) gestellt
   werden? Das macht EmotePurge gegenüber 7TV sichtbar — erwünscht oder lieber unauffällig
   bleiben?
2. Konsolen-Einzeiler und/oder Bookmarklet in die offizielle Anleitung aufnehmen — ist das
   Self-XSS-Gewöhnungsrisiko für die Zielgruppe (Streamer/Mods, keine Entwickler) akzeptabel?
3. Priorität der UX-Bausteine gegenüber Modul E (Launch-Vorbereitung).

**Verbleibende Unsicherheiten (ehrlich):** Produktions-Auth-Code kann vom öffentlichen
`main`-Stand (2026-05-29) abweichen — die empirische 403-Probe deckt das Kernverhalten aber
live ab [belegt]. Vollständigkeit des ToS-Texts im untersuchten Chunk [nicht überprüfbar].
Konkrete Login-Rate-Limits jenseits des gemessenen Headers, Zustand von `daugustin/7tv-mcp`
[nicht überprüfbar]. Die v3+v4-Token-Äquivalenz ist aus dem Code geschlossen und durch unsere
Live-Tests gestützt, aber nicht isoliert verifiziert [plausibel].

---

*Quellen: `Architectur.md`, `docs/DECISIONS.md`, `Review-2026-07-29.md`,
`Review-2026-07-29-Umsetzung.md`, `web/src/app/core/seven-tv/*`,
`web/src/app/shared/seven-tv/*`, Commits `3e4d013`/`e29b73f`/`c2036f1`/`bf561d7`;
`https://7tv.app/api/docs/v4docs.json` + `v3docs.json`, `https://7tv.io/v3/docs`,
`SevenTV/SevenTV` (`apps/api/src/http/v4/rest/auth.rs`, `apps/api/src/http/mod.rs`,
`apps/api/src/http/middleware/session.rs` + `cookies.rs`, `apps/api/src/jwt.rs`,
`apps/website/src/lib/auth.ts`, `apps/website/src/routes/login/callback/+page.ts`),
`SevenTV/chatterino7` (`src/providers/seventv/SeventvAPI.cpp`), `UberKitten/7tv-cli-mcp`,
web.archive.org (Captures `20260721113814`, `20260726225123`). Empirische Probe:
Scratchpad-Skript `probe-login.sh`, Ausgabe vom 2026-07-30.*
