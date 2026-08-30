# Review der Spec „Rate-Limits: Provider-Kosten statt HTTP-Requests begrenzen"

**Datum:** 2026-08-30
**Reviewt:** `docs/superpowers/specs/2026-08-30-rate-limit-architecture-design.md`
**Zweck:** Überarbeitungsauftrag. Die Spec wird korrigiert und neu zugeschnitten, **bevor** daraus ein Plan entsteht.
**Methode:** zwei unabhängige Durchgänge — ein adversariales Architektur-Review und eine Faktenprüfung jeder `Datei:Zeile`-Behauptung gegen den echten Code. Beide sind unabhängig voneinander auf dieselbe zentrale Rechenkorrektur gestoßen (Abschnitt A1).

---

## Gesamturteil

**Die Richtung stimmt, die Rechnung nicht, der Umfang ist zu groß.**

Tragfähig und beizubehalten: die Korrektur der falschen Grundannahme (Helix-User-Token-Budgets sind pro Client-ID *und Nutzer*, nicht app-weit), die Feststellung, dass `ExternalApi` an der falschen Grenze misst, die Cache-Regeln (nur vollständige Antworten cachen, `Unavailable` nie negativ cachen) und die Liste verworfener Varianten. Die Beleglage ist ungewöhnlich ehrlich und fast durchgehend zitierfähig — zehn von dreizehn geprüften Behauptungen sind exakt bestätigt.

Nicht tragfähig: die als Kernleistung verkaufte „korrigierte Request-Zählung" ist selbst um eins zu hoch und übersieht vier reale Verstärker, von denen einer größer ist als alles, was die Spec behandelt. Die dreiwertige Rollenauflösung ist an genau den Aufrufstellen nicht zu Ende gedacht, an denen sie Verhalten ändert, und würde dort das Produkt **schlechter** machen als heute. Und das Provider-Budget-System mit Redis-Reservierungen ist gleichzeitig unterspezifiziert und in seinem Bedarf unbelegt.

---

## A. Faktenkorrekturen — die Spec behauptet Falsches

Diese Punkte sind am Code verifiziert. Sie müssen im Spec-Text korrigiert werden, weil Zielarchitektur, Einsparungsversprechen und Abnahmekriterien darauf aufbauen.

### A1. Es gibt keinen dritten `active-set`-Abruf. Es sind zwei.

Die Spec behauptet in *Korrigierte Request-Zählung → Usage-Workspace* einen Zwischenlauf (Punkt 4) und begründet ihn damit, dass der Load-Effect vor dem Korrektur-Effect registriert ist.

Das ist nach Angulars Signal-Semantik nicht möglich. `rangeResolved` (`web/src/app/features/usage-stats/usage-stats-page.ts:484-500`) ist ein `computed<boolean>`, das im beschriebenen Zwischenzustand seinen **Wert** nicht ändert (`false` → `false`). Angular überspringt einen Effect, dessen Producer sich wertmäßig nicht geändert haben (`runEffect` → `consumerPollProducersForChange`). Der Load-Effect wird davon nicht dirty; er läuft erst wieder, wenn der Korrektur-Effect `from` setzt. Der von der Spec als Beleg zitierte Kommentar ist in Wahrheit die Verteidigung **gegen** diesen Zwischenlauf, nicht sein Nachweis.

Gegenprobe im Bestand: `web/e2e/usage-range-resolution.e2e.spec.ts:95-97` fixiert `totals` und `series` auf genau einen Aufruf.

**Tatsächlich, in allen drei Fällen** (frischer Channel, Channel älter als der 365-Tage-Floor, `trackedSince = null`): 2× `active-set`, 1× `totals`, 1× `series`.

Daraus folgt, was zu ändern ist:

- **6 Permits pro Workspace-Einstieg**, nicht sieben.
- **7 Permits pro Rundgang** (Einstieg + Rückkehr zur ungecacheten Übersicht), nicht acht. Sechs Channels = **42**, nicht 48. Die Diagnose hält (42 > 40), aber mit **zwei** Permits Reserve statt zehn — das ist die ehrlichere und für die Dringlichkeit wichtigere Zahl.
- Die Fallunterscheidung „bei fehlendem oder sehr altem Tracking-Start sind es sechs" entfällt ersatzlos; das Ergebnis ist immer gleich.
- Im Abschnitt *Request-Verstärker entfernen → `active-set` nur bei fachlichem Anlass laden* halbiert sich das Versprechen: es entfällt **ein** Request pro Workspace-Einstieg, nicht zwei.

### A2. Vier Request-Verstärker fehlen — der größte ist unbehandelt

- **`awaitSync`-Poll:** `usage-stats-page.ts:1099-1128`, `SYNC_POLL_INTERVAL_MS = 2000`, `SYNC_POLL_MAX_ATTEMPTS = 15`. Sobald ein Channel noch keine `activeEmoteSetId` und keinen Fehlergrund hat, sind das **bis zu 15 zusätzliche `active-set`-Requests in 30 Sekunden, alle auf `ExternalApi`**. Das ist der mit Abstand größte einzelne Verstärker im Client und kommt in der Spec nicht vor. Er trifft genau den Erstnutzungsfall nach einem Join.
- **Sync-Fehlergrund-Recheck:** `usage-stats-page.ts:682-712`, alle 30 Sekunden ein `active-set`, bei gesetzter Set-ID zusätzlich ein `totals` — dauerhaft, solange ein Fehlergrund angezeigt wird. Beide auf `ExternalApi`.
- **Pro Vote ein `GET /api/channels/{channelName}`:** `vote()` → `load()` → `loadActiveEmoteSetId()` → `getStatus()` (`vote-session-detail-page.ts:706-709`, `:734`). Dieser Endpoint trägt **heute gar keine Policy**, obwohl sein `ChannelManagementAuthorizationFilter` (`ChannelEndpoints.cs:19-29`) bei Cache-Miss Helix trifft — und für Nicht-Manager ist er ein garantierter 403 pro Vote. Die Zielrechnung „`n + 1`" im Abschnitt *Eine Vote-Reload-Pipeline* ignoriert ihn ebenfalls; die neue Pipeline muss ihn mit einsammeln.
- **Guard-Doppelabruf:** `vote-session-access.guard.ts:33` holt `GET /results` als Zugangsprobe, die Seite lädt es unmittelbar danach erneut (`vote-session-detail-page.ts:324`). Der Einstieg in eine Vote-Session kostet **4** `ExternalApi`-Permits, nicht 2.

### A3. „Rollenprüfungen sind bereits gecacht" ist als Entwarnung zu breit

Die 10 Minuten stimmen (`ModRoleCache.cs:95`, konfigurierbar über `Auth:ModCheckCacheTtlMinutes`) und gelten für alle vier Endpoint-Filter in `src/EmotePurge.Api/Auth/`. Aber:

- **`MyChannelsService.cs:35-56` hinter `/channels/mine` ist völlig ungecacht** und ruft Helix bei jedem Request — das ist der teuerste Endpunkt der App.
- **`ResolveUntrackedGrantsAsync` (`MyChannelsService.cs:156-183`) macht selbst bei Grant-Cache-*Treffer* noch einen App-Token-Helix-`GetUsers`.**
- `TwitchUserTokenService.cs:20-27` validiert stündlich pro Nutzer live.

Die Spec markiert den Kommentar an `ChannelEndpoints.cs:117-120` im Doku-Abschnitt als veraltet und zur Korrektur vorgesehen. **Dieser Kommentar ist korrekt** und darf nicht „korrigiert" werden. Die pauschale Aussage der Spec widerspricht außerdem ihrem eigenen Abschnitt *Login*, der die Helix-Kosten von `/mine` richtig beschreibt.

### A4. Kleinere Belegfehler

- `Program.cs:93-120` sollte `Program.cs:95-140` heißen. Alle übrigen Zeilenverweise stimmen auf ±2 Zeilen.
- „Beide SSE-Endpunkte" — es sind **drei**; der Admin-Stream aus `AdminEndpoints` ist ebenfalls policy-frei.

---

## B. Blocker — vor dem Plan zu klären

### B1. Die dreiwertige Auflösung bricht Stellen, an denen die Rolle kein Gate ist

Die Spec definiert nur die Abbildung in den Autorisierungsfiltern. `CanManageChannelAsync` wird aber auch **innerhalb** von Handlern zur Anreicherung benutzt:

- `VoteSessionEndpoints.cs:178` — `viewerIsManager` auf der Ergebnisseite. Heute degradiert ein Helix-Ausfall zur Nicht-Manager-Sicht (Tallies ohne Rohdaten): die Seite funktioniert. `Unavailable` → 503 würde sie bei jedem Helix-Schluckauf **für alle** brechen — schlechter als der Ist-Zustand.
- `VoteSessionEndpoints.cs:127` — der Manager-Branch der Sessionliste, gleiche Frage.
- `ChannelEndpoints.cs:85-97` + `ChannelPermissionsDto` — **`/permissions` ist bool-only und kann `Unavailable` gar nicht ausdrücken.** Die Spec hängt den Endpoint an `InteractiveRead`, definiert aber nirgends seine Antwort bei `Unavailable`. Er speist drei Frontend-Guards, die Flags lesen; `false` und „unbekannt" blieben ununterscheidbar.
- Am Ende der Kette behandeln alle Guards jeden Fehler als „keine Rechte" (`catchError(() => of(fallback()))` in `usage-stats-access.guard.ts`, `vote-session-access.guard.ts`). **Ein 503 statt 403 ändert am Rauswurf nichts**, solange die Frontend-Seite nicht mitgeplant wird — und die Frontend-Sektion der Spec enthält keine 503-Behandlung.

**Auflage:** Jede Aufrufstelle von `IChannelAccessService` und `IVoteEligibilityService` klassifizieren — **Gate** (→ 503) oder **Anreicherung** (→ definiert degradieren, nie 503). Den `/permissions`-Vertrag für `Unavailable` ausformulieren. Die Frontend-Behandlung festlegen: Guards, Fehlermeldung, Wiederholbarkeit.

**Zusatz:** Diese Arbeit hat mit den gemeldeten 429ern nichts zu tun. Sie ist echte Korrektheitsarbeit (Ausfall ≠ fehlende Rechte) und gehört in einen **eigenen Slice mit eigener Spec** — nicht in die Rate-Limit-Runde. Siehe Abschnitt C.

### B2. Die Redis-Koordination des TwitchApp-Budgets ist unspezifiziert und im Bedarf unbelegt

„Optimistische Reservierungen … atomar in Redis" ist keine Spezifikation. Es fehlt vollständig:

- Welche Primitive? Lua-Skript, `INCR` + `EXPIRE`, `WATCH`/`MULTI`?
- Was passiert mit einer Reservierung, wenn der Prozess zwischen Reservierung und Call stirbt — TTL, expliziter Release-Pfad?
- **Out-of-Order-Antworten:** Die `Ratelimit-Remaining`-Header zweier paralleler Antworten treffen ungeordnet ein. Ohne Monotonie über die `Ratelimit-Reset`-Epoche überschreibt der ältere Wert den neueren, und der beobachtete Zustand ist dauerhaft falsch.
- Schreibt der Worker denselben Zustand? Mit welcher Konfliktregel?

Dem gegenüber steht der reale Verbrauch: `ResolveUntrackedGrantsAsync` nur bei untracked Grants, `TwitchLivePollWorker` alle 300 s, `TwitchConnectionWatchdog` — einstellig pro Minute gegen ein Providerbudget der Größenordnung 800/min. **In der gesamten Beleglage der Spec steht kein einziger dokumentierter Provider-429.** Alle untersuchten 429er waren lokal.

**Auflage:** streichen (siehe Abschnitt C) und durch strukturiertes Header-Logging ersetzen. Falls entgegen dieser Empfehlung behalten: die Primitive ausformulieren, inklusive Crash-Pfad und Monotonieregel.

**Nebenbefund, vorher zu klären:** Der Kommentar in `TwitchAppTokenProvider.cs` („Twitch invalidates the previous app token on every new grant") widerspricht dem Spec-Modell zweier unabhängiger Prozesse mit je eigenem App-Token. Einer von beiden irrt. Das ist zu klären, **bevor** App-Token-Zustand als „gemeinsam" geführt wird.

### B3. Zählung und Verstärker-Inventar müssen stimmen, bevor Tasks daraus werden

Siehe A1 und A2. Ein Plan auf der 7-Permit-Rechnung schreibt die falsche Zahl in Tasks und Abnahmekriterien fest. Die überarbeitete Spec braucht eine **am Netzwerk-Tab nachgemessene** Tabelle für: Workspace-Einstieg, Rundgang mit Rückkehr, Vote-Session-Einstieg, `n` schnelle Votes, Erstnutzung nach Join (mit `awaitSync`).

---

## C. Zuschnitt — was jetzt, was später, was separat

Für ein konkretes Fehlerbild bei **zwei gleichzeitigen Nutzern auf einer Api-Replica** baut die Spec ein verteiltes Budget-Beobachtungssystem mit Observe/Enforce-Moduswechseln und zeitgebuckelten Metrikzählern. Die Providerbudgets sind heute strukturell geschützt (10-Minuten-Rollencaches, Resync-Cooldown, Worker-Timer, künftig der Listencache), und es gibt keinen belegten Provider-429.

### Jetzt bauen — der Kern

1. **Client-Verstärker entfernen.** `active-set` aus dem range-abhängigen `load()` lösen; die Vote-Reload-Pipeline vereinheitlichen **inklusive** des Kanalstatus-Abrufs aus A2; den Guard-Doppelabruf der Vote-Session beseitigen; `awaitSync`- und Fehlergrund-Poll gegen die neue Policy-Rechnung prüfen und, wo nötig, drosseln.
2. **`ExternalApi` durch `InteractiveRead` und `Voting` ersetzen.** Dabei `GET /api/channels/{channelName}` **erstmals** einer Policy zuordnen und `DELETE .../votes/{emoteId}` (Retract) explizit in `Voting` aufnehmen. Die Startwerte 300/+5 s und 120/+2 s sind gegen die korrigierte Rechnung geprüft und tragen mit großer Reserve (42–48 Requests/min gegen Kapazität 300; 100 Votes gegen 120).
3. **Gemeinsamer Moderated-Channels-Listencache mit Single-Flight**, Ablösung des Bool-Caches, Admin-Invalidate erweitert. Das ist die echte Helix-Ersparnis im `/mine`-Fall aus #33.
4. **Beobachtbarkeit** — siehe unten, ausdrücklich Teil des Kerns.

### Beobachtbarkeit: bleibt drin, aber ohne die Budget-Maschinerie

Die Admin-Ansicht ist eine **ausdrückliche Anforderung des Projektinhabers** und wird nicht vertagt. Sie hängt aber nicht am Observe/Enforce-Apparat. Was ohne ihn schon geht und den Zweck erfüllt:

- Effektive Konfiguration je lokaler Policy: Kapazität, Nachfüllrate, Partition, Queue.
- Akzeptierte und abgelehnte Requests je Policy, letzte Minute und letzte 24 Stunden.
- Letzte lokale Ablehnung: Zeitpunkt, Route-Template, Policy, Partition. **Die vorhandene Struktur nutzen** — Policy- und Partitionsname liegen bereits in `HttpContext.Items` (`RateLimitRejection.PartitionPerUser`), das strukturierte Rejection-Log existiert seit `dfabd81`.
- Cache-Trefferquote der Rollencaches (Mod-Liste, 7TV-Grants, Sub-Check).
- Gezählte **echte** Provider-429er samt `Retry-After` und letztem Vorkommen, plus die zuletzt gesehenen `Ratelimit-*`-Header — als beobachteter Zustand, ohne Reservierungslogik.
- Keine Prozentanzeige ohne bekannten Nenner (der Punkt der Spec ist richtig und bleibt).

Was **nicht** in diese Runde gehört: der Observe-/Enforce-Modusumschalter als eigene Maschinerie, die Redis-Reservierungen, die konfigurierbare 7TV-Grenze.

### Vertagen

TwitchApp-Reservierungssystem, Observe/Enforce-Modi, konfigurierbare 7TV-Grenze. Voraussetzung für eine Wiedervorlage ist ein **gemessener** Provider-429 in den neuen Zählern — dann liegt auch die Kalibrierung vor, die die Spec zu Recht einfordert.

### Separat designen

Die dreiwertige Rollenauflösung (B1), als eigener Slice mit eigener Spec.

### Ehrlich ausweisen

Die Mass-Delete-/Restore-Engine läuft **per Design browserseitig direkt gegen `7tv.io`** (CSP `connect-src`, eigene `SevenTvRunEngine`). Die serverseitig gemessene 7TV-Rate im Monitoring ist damit strukturell unvollständig. Das ist kein Fehler, muss aber in der Spec und später in der UI stehen, sonst wird die Anzeige falsch gelesen.

---

## D. Rollout — der Fix steht am Ende und gehört an den Anfang

Die Schritte 5 und 6 beheben #33 und hängen technisch an nichts aus 1–4. Die Begründung „verhindert eine zweite Runde blind gewählter Grenzwerte" trägt nicht: 300/+5 s und 120/+2 s **sind** blind gewählt — und dürfen es sein, weil sie großzügige lokale Missbrauchsgrenzen sind, keine Providerkalibrierung. In der Spec-Reihenfolge wartet der betroffene Nutzer eine vollständige Observe-Testphase auf einen Fix, der heute deploybar wäre.

**Vorgeschlagene Reihenfolge:**

1. Client-Verstärker entfernen (bisher Schritt 6). Wirkt sofort, ohne Backend-Änderung.
2. `InteractiveRead` + `Voting` einführen, Endpoints umhängen, `ExternalApi` entfernen (bisher Schritt 5). Ein Schritt, eine Einheit — ein Zwischenzustand mit zwei konkurrierenden Policies auf denselben Routen ist schlechter als beide Endzustände.
3. Gemeinsamer Moderated-Channels-Cache mit Single-Flight (bisher Schritt 2).
4. Zähler, Header-Erfassung und Admin-Ansicht.

Jeder Schritt muss einzeln deploybar und einzeln rückrollbar sein; die überarbeitete Spec sagt das je Schritt ausdrücklich.

---

## E. Projektregeln, die die Spec noch nicht berücksichtigt

`CLAUDE.md` im Repo-Root ist beim Verfassen der Spec nicht gelesen worden. Die folgenden Regeln sind **verbindlich** und wirken auf diese Arbeit. Die überarbeitete Spec muss sie erkennbar einarbeiten; die vollständige Fassung steht in `CLAUDE.md`, die Begründungen in `docs/DECISIONS.md`.

**Regel 3 — Konventions-/Vertrags-/Topologieänderungen tragen ihren `docs/DECISIONS.md`-Eintrag im selben Commit.** Diese Arbeit ändert Policy-Grenzen, Fehlerverträge und Cache-Topologie. Die Spec erwähnt das bereits richtig; es bleibt so.

**Regel 4 — kein `AppDbContext` und kein `IConnectionMultiplexer` direkt aus Minimal-API-Handlern.** Neue Fähigkeiten bekommen ein Interface in `Core/Services/` und eine Implementierung in `Infrastructure/Services/`. Kein generisches Repository-Pattern. Das betrifft die Zähler und den Admin-Snapshot unmittelbar: Der Rate-Limit-Endpoint darf Redis nicht selbst anfassen.

**Regel 5 — Klassen mit nicht-trivialer Logik oder externer Abhängigkeit bekommen ein Interface**, reine DTOs nicht. Ausnahme: ein `BackgroundService`, der nur per `AddHostedService<T>()` läuft.

**Regel 6 — Minimal API, keine Controllers.** Neue Endpoints in `Endpoints/*.cs`, nicht in `Program.cs`. Autorisierung über `IEndpointFilter`, **nicht** über ASP.NET-Core-Policies.

**Regel 7 — die API liefert bei Fehlern nur sprachneutrale Codes (`ApiErrorCodes`), nie fertigen Text.** Ein neuer Code braucht denselben Eintrag in `web/src/app/core/i18n/api-error.ts` **und** in **beiden** Locale-Dateien (de/en). `api-error-locales.spec.ts` erzwingt die hinteren beiden Schritte; der Schritt von `ApiErrorCodes.cs` nach `api-error.ts` ist Disziplin — und genau dort ist die Liste schon zweimal auseinandergelaufen. **Die Spec fordert neue Codes für Provider-429 und 503, benennt sie aber nicht und nennt die Kette nicht.** Nachzutragen: die konkreten Codenamen und alle vier Fundstellen je Code.

**Regel 11 — Testpflicht.** Neue Services/Logik in `Infrastructure` bekommen einen Test in `tests/EmotePurge.Infrastructure.Tests` (`Unit/` vs. `Integration/` danach, ob echte Infrastruktur berührt wird). Neue **reine** Worker-Logik in das container-freie `tests/EmotePurge.Worker.Tests`. **Ein neuer `IEndpointFilter` oder eine geänderte Filter-Reihenfolge einer `MapGroup` bekommt seinen Fall in `tests/EmotePurge.Api.Tests`.** Endpoint-*Handler* dagegen nicht — die bleiben dünn.

**Regel 12 — Frontend:** neue Services/Guards/reine Utilities in `web/src/app/core/` und `shared/` bekommen einen co-located `*.spec.ts` (Vitest). Größere Flows zusätzlich als Playwright-E2E mit gemockten `/api/**`. Isolierte Komponententests sind bewusst **nicht** Teil der Konvention.

**Regel 16 — Backend-Features vor dem Commit live gegen echte Postgres-/Redis-/Twitch-/7TV-Zugänge verifizieren**, nicht nur `dotnet build`.

**Regel 19 — Member-Reihenfolge in C#-Klassen:** `const`/`static readonly` → `readonly` Felder → veränderliche Felder → öffentliche Properties → öffentliche Methoden → private Methoden → `private static` Helper; verschachtelte Typen ans Klassenende.

**Schichtentreue** (verbindlich, per Test erzwungen):

| Schicht | Erlaubt | Verboten |
|---|---|---|
| `EmotePurge.Core` | **nur BCL** | EF Core, StackExchange.Redis, `System.Net.Http`, ASP.NET Core — auch transitiv. **0 `PackageReference`, 0 `ProjectReference`**, erzwungen durch `CoreAssemblyReferenceTests` |
| `EmotePurge.Infrastructure` | → Core; EF/Redis/HTTP | ASP.NET-Core-Typen, Rückverweis auf Api/Worker |
| `EmotePurge.Api` | → Infrastructure, → Core | direkter `AppDbContext`-/`IConnectionMultiplexer`-Zugriff aus Handlern |
| `EmotePurge.Worker` | → Infrastructure, → Core | direkter `AppDbContext`-Zugriff |
| `web/core/` | — | nichts aus `features/` oder `shared/` |
| `web/shared/` | → `core/` | nichts aus `features/` |

**Konkrete Folge für diese Arbeit:** Der Provider-Kontext (Providerklasse, Akteur, Aufrufquelle) darf in `Core` nur als reiner `record`/`enum` ohne jede Abhängigkeit liegen. Die Redis-Anbindung gehört nach `Infrastructure`; die Redis-Interfaces in `Core` arbeiten mit reinen `string`-Signaturen. `AddEmotePurgeInfrastructure(configuration)` ist der **einzige** DI-Registrierungspunkt.

**Sprache:** Bezeichner, Typen, öffentliche APIs und Kommentare in neuem Code **englisch**. Log- und `throw`-Messages **deutsch**. Projektdokumentation deutsch, Commit-Messages englisch. Conventional Commits, mehrere logisch getrennte Commits statt eines Sammel-Commits.

**Fertig-Definition:** `dotnet test EmotePurge.slnx` (braucht laufendes Docker, Testcontainers), `npm --prefix web test -- --watch=false`, bei UI-Änderungen zusätzlich `npm --prefix web run e2e`. **Die E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht** — sonst fällt rund die halbe Suite mit irreführenden „element not found"-Fehlern durch.

---

## F. Tests und Abnahmekriterien

### Auswirkung auf bestehende Tests fehlt vollständig

Die Teststrategie der Spec listet nur neue Tests. Betroffen sind aber:

- **`tests/EmotePurge.Api.Tests`, `ApiFactory`** substituiert `IChannelAccessService` und `IVoteEligibilityService` bool-basiert. Eine dreiwertige Auflösung ändert Core-Interfaces und damit **alle** Substitute plus die 403-Zeilen der `AuthFilterMatrixTests` — nach Regel 11 kommen `Unavailable`-Zeilen in die Matrix.
- **`RateLimitRejectionTests`** hängt an Fixed-Window-Semantik und an den Policy-Namen. Der Wechsel auf Token-Buckets und neue Namen bricht sie.
- Frontend: bestehende Specs zu Usage-Range-Auflösung und Vote-Reload sind anzupassen, nicht zu ergänzen.

Die überarbeitete Spec listet die betroffenen Bestandstests namentlich.

### Abnahmekriterien brauchen einen zugeordneten Harness

„Sechs Rundgänge in einer Minute erzeugen keine lokale 429" kann die Playwright-Suite **prinzipiell nicht** prüfen: sie mockt `/api/**` und sieht den Limiter nie. Die Zuordnung lautet:

- **429-/Policy-Kriterien** → `WebApplicationFactory`-Harness in `tests/EmotePurge.Api.Tests`; `RateLimitRejectionTests` ist das vorhandene Muster.
- **Request-*Zahl*-Kriterien** (vier Votes → vier Mutationen + höchstens ein Reload; All-Time-Auflösung lädt `active-set` nicht erneut) → Playwright, durch Zählen der abgefangenen Requests. Dafür ist die Suite geeignet.
- **Cache-Kriterien** (Hit ohne Helix, Single-Flight bei Parallelität, kein negativer Eintrag bei Teilfehler) → `tests/EmotePurge.Infrastructure.Tests`, Testcontainers.
- **Live-Verifikation** nach Regel 16 für den Helix-Pfad; keine Suite ersetzt sie.

Jedes Abnahmekriterium der überarbeiteten Spec nennt seinen Harness. Die Kriterien werden außerdem auf die **korrigierten** Zahlen aus A1 umgestellt.

---

## G. Kleinere Punkte

- **Bool-Cache:** „kann während der Migration gelesen werden" — wer schreibt ihn dann noch? Ein gelesener Cache ohne Schreiber ist toter Code. Sauberer: im Cache-Schritt vollständig ablösen.
- **Options-Validierung:** „zueinander konsistente Werte" — welche Konsistenzrelation ist gemeint? Ausformulieren oder streichen.
- **7TV-Backoff-Scope:** „pro Providerzustand gemeinsam" — pro Prozess oder Redis-geteilt? Der dominante 7TV-Konsument ist der periodische Resync-Worker; ob ein API-seitig gesehener 429 den Worker pausiert, ist eine Produktentscheidung, die die Spec treffen muss.
- **Fail-open ist keine Autorisierungslücke — das darf die Spec explizit begründen.** Am Code belegt: `Allowed` setzt immer einen Provider- oder Cachebeleg voraus (`ModeratorCheckService.cs:25-43` antwortet bei Token-/Helix-Fehler `false` ohne zu cachen). Ein fail-open Budgetkoordinator öffnet nur die Drossel; die lokalen Policies sind in-memory und bleiben bei Redis-Ausfall wirksam.
- **SSE-Reconnects:** `MaxConnectionLifetime = 10 min` (`LiveEndpoints.cs:24`) erzwingt alle zehn Minuten einen Reconnect pro offener Seite — policy-frei, aber durch die volle Auth-Pipeline. Für das Monitoring relevant, für die Permit-Rechnung nicht.
- **`WorkerHealthService`** pollt `/api/worker/health` alle 30 Sekunden über die gesamte App-Lebensdauer, ohne Policy. Kein Permit, aber ein Dauerstrom, den die Monitoring-Anzeige einordnen können sollte.

---

## H. Was unverändert bleibt

Damit die Überarbeitung nicht am Falschen ansetzt — diese Teile sind geprüft und tragen:

- Die Trennung der Beleglage in „am Code belegt / Betriebsbeobachtung / extern verifiziert". Beibehalten, auch im überarbeiteten Dokument.
- Die Korrektur der Grundannahme: Helix-User-Token-Budgets sind pro Client-ID und Nutzer, nicht app-weit. Der Kommentar in `Program.cs` („those quotas are per application, not per user") ist für User-Token-Aufrufe tatsächlich falsch, und die Schlussfolgerung, dass `ExternalApi` an der falschen Grenze misst, folgt daraus korrekt.
- Die Cache-Regeln: nur vollständige, erfolgreiche Antworten cachen; Timeout, 429, 5xx, Tokenfehler und unvollständige Pagination erzeugen **keinen** negativen Eintrag; nach dem Single-Flight-Gate Redis erneut prüfen. Das entspricht der besten bestehenden Praxis im Repo (`SevenTvEditorService`).
- Die Entscheidung, `duplicate-names` und `active-set` **nicht** zusammenzulegen, mit der gegebenen Begründung.
- Die Entscheidung gegen einen pauschalen `/mine`-Clientcache.
- Die Liste verworfener Varianten insgesamt — sie nimmt die naheliegenden Abkürzungen sauber vom Tisch. Nur der Eintrag „`ExternalApi` nur erhöhen" sollte die korrigierte Zahl aus A1 verwenden.
- Startwerte `InteractiveRead` 300/+5 s und `Voting` 120/+2 s inklusive der Partition `TwitchUserId + SessionId`. Gegen die korrigierte Rechnung geprüft, mit großer Reserve.

---

## Erwartetes Ergebnis dieser Runde

Eine **überarbeitete Spec**, kein Plan. Sie enthält:

1. Die korrigierte, am Netzwerk-Tab nachgemessene Permit-Rechnung inklusive der vier fehlenden Verstärker (A1, A2) und ohne die falsche Entwarnung zum Rollencache (A3).
2. Einen auf den Kern plus die zugeschnittene Beobachtbarkeit reduzierten Umfang (C), mit ausdrücklicher Vertagungsliste und Begründung.
3. Die umgedrehte Rollout-Reihenfolge (D), je Schritt einzeln deploybar und rückrollbar.
4. Die eingearbeiteten Projektregeln (E), insbesondere die vollständige Fehlercode-Kette nach Regel 7 mit benannten Codes und die Schichtenzuordnung des Provider-Kontexts.
5. Betroffene Bestandstests namentlich und je Abnahmekriterium den zuständigen Harness (F).
6. Die dreiwertige Rollenauflösung **ausgegliedert** in einen eigenen Slice, mit einem kurzen Absatz, der festhält, warum sie hier nicht mitläuft und welche Klärung (B1) sie zuerst braucht.
