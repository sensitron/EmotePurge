# Review vom 2026-08-01 — Struktur, Formatter-Tooling und Fremd-Wartbarkeit

Anlass: Ein externer Entwickler hat den Angular-Teil des Repos gesichtet (den C#-Teil nicht) und drei Punkte angemerkt — (1) der Code sei nicht so strukturiert, wie ein Mensch ihn strukturieren würde, (2) es fehle Formatter-Tooling, (3) offene Frage, ob das Projekt von Dritten wartbar sei oder nur vom ursprünglichen Entwickler.

Dieses Dokument hält die Messung fest, das Urteil dazu und die daraus abgeleiteten Befunde. Frontend **und** Backend wurden geprüft; letzteres, weil der Kritiker es nicht gesehen hat und die Annahme „dort ist es genauso" nicht ungeprüft bleiben sollte.

Schwesterdokument: [`Review-2026-07-29-Umsetzung.md`](Review-2026-07-29-Umsetzung.md). Überschneidungen sind in Abschnitt 6 aufgelöst und werden hier **nicht** doppelt geführt.

| Welle | Inhalt | Status |
|---|---|---|
| **0** | Befundaufnahme (dieses Dokument) | ✅ **abgeschlossen** (2026-08-01) |
| **1** | Formatter-Tooling und Reformat-Sweep | ✅ **abgeschlossen** (2026-08-01) — TL-1, TL-3, TL-4 |
| **2** | Member-Ordnung Frontend (ESLint) + Backend (Konvention) | ✅ **abgeschlossen** (2026-08-01) — ST-1, ST-2 |
| **3** | CI-Gate für Format und Lint | ✅ **abgeschlossen** (2026-08-01) — TL-2 |
| **4** | Onboarding-Blocker und Doku-Drift | ⬜ offen |
| **5** | Code-Duplikate und Kleinkram | ⬜ offen |

---

## 1. Urteil zu Kritikpunkt 1 — „Der Code ist nicht aufgeräumt"

**Trifft zu, aber deutlich schwächer als vermutet.** Es gibt eine faktisch gelebte Member-Reihenfolge; sie ist nur nirgends dokumentiert und nirgends erzwungen. Von „generierungsbedingter, wechselnder Reihenfolge" kann keine Rede sein — die Abweichungen sind zählbar, benennbar und konzentrieren sich auf wenige Dateien.

**Angular — dominierendes Muster** (14 Dateien geprüft, Components und Services, groß und klein):

```
Modul-consts/Typen → input()/output() → inject() → resource/computed/signal
  → constructor + effect() → public/protected Handler → private Helper
```

**8 von 14 folgen ihm vollständig**: `core/auth/auth.service.ts`, `features/shell/app-shell.ts`, `features/admin/admin-audit-log-page.ts`, `features/admin/admin-monitoring-page.ts`, `features/admin/admin-users-page.ts`, `features/voting/vote-session-list-page.ts`, `shared/seven-tv/mass-delete-panel.ts`, `shared/emotes/emote-usage-filter.ts` (ebenso `shared/selection/list-selection.ts`, `core/live/live-update.service.ts`).

**C# — dominierendes Muster** (12 Dateien geprüft):

```
const/static readonly → readonly fields → mutable fields → public properties
  → public methods → private methods → private static helpers
```

**9 von 12 folgen ihm.** `#region`: **0 Vorkommen im gesamten Backend** — das ist konsistenter als die meisten C#-Projekte. Nested Types stehen in 5 von 6 Fällen am Klassenende.

**Der eigentliche, unauffälligere Teil des Kritikpunkts** ist nicht die Member-Reihenfolge, sondern das **additive Wachstum des State-Blocks** in den großen Feature-Seiten: Jedes neue Feature hängt sein Signal ans Ende des Signal-Blocks und seine Methode ans Ende der Klasse. Ein Konzept liegt dadurch über vier Stellen verteilt:

- `features/voting/vote-session-detail-page.ts`: `activeEmoteSetId` (L102) ↔ Setter `loadActiveEmoteSetId()` (L377) ↔ Auslöser-Flag `syncSeenSinceReload` (L244) ↔ dessen Verbraucher im Effekt (L236–239) — 275 Zeilen Spannweite.
- `features/usage-stats/usage-stats-page.ts`: Sync-Warten als consts (L58–59) ↔ Signal `isAwaitingSync` (L143) ↔ Feld `syncPoll` (L147) ↔ Implementierung `awaitSync()` (L335).
- `features/admin/admin-channels-page.ts`: Resync-Feedback über consts (L33–36), Signals (L261–266), Live-Effekt (L288–305), Auslöser (L335) und Helper (L352).

Gegenprobe in denselben Dateien: Die **Methoden-Cluster** sind sehr wohl bewusst gruppiert — `seven-tv-delete.service.ts` hält den kompletten Pacing-Block (`adaptPacing`/`recordRoundTrip`/`averageRoundTripMs`/`resetPacing`/`peakRequestsPer60s`) geschlossen in L388–437, und `vote-session-detail-page.ts` stellt `keepButtonTitle`/`deleteButtonTitle` direkt über ihren gemeinsamen Helper `voteButtonTitle`.

---

## 2. Urteil zu Kritikpunkt 2 — Tooling

**Die Werkzeug-Lücke ist real und vollständig bestätigt. Die befürchtete Folge — ein uneinheitlicher Bestand — ist ausgeblieben.**

Bestandsaufnahme:

| Erwartet | Ist |
|---|---|
| Prettier | als devDependency vorhanden (`prettier@^3.8.1`), `.prettierrc` vorhanden — aber **kein `format`-Script**, **keine `.prettierignore`** |
| npm-Scripts | genau `ng`, `start`, `build`, `watch`, `test`, `e2e` |
| ESLint | **nirgends** — keine `eslint.config.*`, keine `.eslintrc.*` |
| CSharpier / `dotnet format` gepinnt | **kein `.config/dotnet-tools.json`**, keine `Directory.Build.props`, keine `Directory.Packages.props`, kein `.globalconfig` |
| Analyzer | **kein** StyleCop/Roslynator/Sonar in irgendeiner `.csproj`; kein `TreatWarningsAsErrors`, kein `EnforceCodeStyleInBuild` |
| CI-Gate | `publish.yml` hat Jobs `test`, `test-web`, `publish` — **keinen einzigen Format-/Lint-Schritt** |
| Pre-Commit | kein `.husky/`, keine aktiven `.git/hooks/`, kein lint-staged |
| `.git-blame-ignore-revs` | existiert nicht |

**Der Bestand ist trotzdem konsistent.** Das ist der entscheidende Messwert und der Grund, warum der Sweep klein bleibt:

| Messung | Ergebnis |
|---|---|
| `prettier --check` über `web/src` | **13 von 101 Dateien** abweichend — 87,1 % bereits konform |
| `dotnet format --verify-no-changes` (Solution) | **9 von 189** — ausnahmslos generierte EF-Migrations, **0 handgeschriebene `.cs`** |
| file-scoped Namespaces | 146/146 im handgeschriebenen Code (die 21 block-scoped sind generierte Migrations) |
| `var` vs. expliziter Typ | 1.091 zu **1** echter Ausnahme (`VoteSessionService.cs:52`) |
| Feldkonvention `_camelCase` / `PascalCase` | 97 private Felder, **0 echte Ausnahmen** |
| `this.`-Präfix in C# | genau 1 Vorkommen — und das ist ein Wort in einem Kommentar |
| Brace-Stil / Einrückung C# | Allman 1.253 : 0, durchgängig 4 Spaces, 0 Tabs |
| Einrückung TS/HTML | durchgängig 2 Spaces, 0 Tabs |
| Single Quotes (TS-Imports) | 506 zu 1 |
| Trailing Commas (mehrzeilige Literale) | 400 zu 4 |
| Fehlende Semikolons | 0 (alle 8 Heuristik-Treffer sind Method-Chain-Fortsetzungen) |

Die vorhandene `.prettierrc` (`printWidth: 100`, `singleQuote: true`, Angular-Parser für HTML) **passt bereits zum Bestand** und wird nicht angefasst: Nur 1,63 % der Zeilen sind länger als 100 Zeichen, und die Überlängen sind fast durchweg Tailwind-Klassenlisten, keine Logik (längste Zeile: 275 Zeichen, `features/login/login-page.ts:19`, eine `class="…"`-Zeile).

Damit ist der gefürchtete „Sweep zerschießt `git blame`" **13 Dateien groß**. Arbeitsverzeichnis war zum Messzeitpunkt sauber, kein Stash — es kollidiert nichts.

**Zwei Messartefakte, die nicht als Befund zählen:**
- Der `dotnet format`-Trockenlauf meldet 392× `ENDOFLINE`. Das ist ein reines Working-Tree-Artefakt: `git ls-files --eol` zeigt `i/lf w/crlf`, das EF-Tooling schreibt CRLF, und `core.autocrlf=input` normalisiert erst beim Commit. Im CI-Checkout tritt das nicht auf.
- Die 9× `CHARSET` sind dagegen echt (UTF-8-BOM im Commit) → Befund **TL-3**.

---

## 3. Urteil zu Kritikpunkt 3 — „Kann das jemand anderes maintainen?"

**Trifft härter zu als der Kritiker vermutet — aber an einer anderen Stelle.** Nicht der Code ist der Engpass, sondern die **erste Woche**.

### Was überdurchschnittlich gut ist

Diese Punkte sind hier festgehalten, damit die Bewertung kalibriert bleibt und niemand sie später „wegoptimiert":

1. **118 von 118 Log-Aufrufen sind strukturierte Message-Templates, 0 interpolierte Strings**, und praktisch jede Zeile trägt `{Channel}`-Kontext. Aus der reinen Template-Liste ließ sich eine vollständige Diagnose-Kaskade für „Channel X synct nicht" ableiten, ohne eine Zeile Implementierung zu lesen — von „ist der Channel gejoint?" über „läuft der REST-Resync?" bis „ist die Twitch-Verbindung eingefroren?".
2. **0 TODO/FIXME/HACK im gesamten Repo.** Offene Punkte stehen strukturiert in den Review-Dokumenten statt verstreut im Code.
3. **Praktisch keine magischen Zahlen.** ~25 Betriebsparameter, alle als benannte Konstanten, die meisten mit Rechnung dabei — `UsageFlushWorker.cs:18`: „Five attempts ≈ 2.5 minutes of tolerance".
4. **Kommentare erklären Vorfälle mit Datum, nicht Syntax.** `ReconnectPolicy.cs:18`: „Every rule in here comes from a production outage (2026-07-26 twice, 2026-07-27)". `Program.cs:209` nennt Symptom und Datum eines Prod-Vorfalls. Nach Rausch-Kommentaren musste aktiv *gesucht* werden; gefunden wurden im Wesentlichen drei ASP.NET-Template-Zeilen.
5. **60 `catch`-Blöcke, kein einziger leerer.** Die 9 ohne Logging sind alle typisiert und am Ort begründet.
6. **Schichtentreue hält:** 0 Importe von `core/` nach `features/`/`shared/`, 0 Cross-Feature-Importe, 0 `MapGet/MapPost` außerhalb von `Endpoints/`.
7. **Tests laufen gegen echte Infrastruktur und echte Wire-Formate** (Testcontainers; `SevenTvDispatchParserTests` gegen live aufgezeichnete 7TV-Frames). Gemessen am 2026-08-01 (tatsächlich ausgeführte Fälle, nicht Attribute): **233 Backend-Tests** (199 Infrastructure + 34 Worker, aus 210 `[Fact]`/`[Theory]` über 25 Dateien), **165 Vitest-Unit-Tests** (22 Dateien) und **24 Playwright-E2E-Tests** — zusammen 422.
8. **Die Doku widerruft sich selbst, wenn sie sich geirrt hat** — `Architectur.md:96` nimmt die frühere Behauptung „7TV ist nachweislich unzuverlässig" ausdrücklich zurück und benennt die zwei eigenen Implementierungsfehler.

### Wo es klemmt

Der Weg von `git clone` bis „läuft lokal" ist eine lange, ununterbrochene Kette ohne Abkürzung: Docker starten → Twitch-App registrieren → `.env` bauen → 32-Byte-Key erzeugen → Migration einspielen → Api starten → einloggen → echten Twitch-Channel joinen, damit überhaupt Daten existieren. Erst danach zeigt das Frontend Inhalte. Es gibt keine Seed- oder Mock-Daten für manuelle Entwicklung; die vorhandenen Mocks (`web/e2e/support/mocks.ts`) sind Playwright-intern.

Die Einzelbefunde dazu stehen in Abschnitt 4.

### Gesamturteil

Das ist **kein Vibe-Coding-Projekt**. Auf der Achse „nur der Autor kann es warten" ↔ „jeder kann es warten" steht das Repo klar auf der guten Hälfte: Der Code trägt sein Warum mit sich, die Logs sind betriebstauglich, das Entscheidungslog ist gepflegt. Was fehlt, ist der Einstieg — und der ist mit vertretbarem Aufwand nachrüstbar, weil das Wissen existiert, nur an der falschen Stelle steht (in `docs/DECISIONS.md` und im Kopf des Autors statt in einer README).

---

## 4. Befunde

`E` = automatisch erzwingbar · `K` = nur Konvention/Review-Disziplin · `O` = macht den Code objektiv wartbarer · `G` = Geschmack

### Hoch

**WB-1 — `Auth:AdminTwitchLogins` ist nicht per Environment überschreibbar** · 30 min · O
Der Wert steht hart als `[ "sensitron" ]` in `src/EmotePurge.Api/appsettings.json:16`. `docker-compose.yml:54–58` reicht fünf `Auth__*`-Werte durch (`ClientId`, `ClientSecret`, `TokenEncryptionKey`, `RedirectUri`, `PostLoginRedirectUrl`) — **`AdminTwitchLogins` ist nicht dabei**, ebensowenig `ModCheckCacheTtlMinutes`. Ein fremder Entwickler, der am Admin-Bereich arbeiten soll (8 Endpoints, 4 Frontend-Seiten, eigene Entität, eigener Live-Stream), bekommt vom `GlobalAdminAuthorizationFilter` ein blankes 403 und findet nirgends heraus, warum. Fix: als Env durchreichen (`docker-compose.yml`, `docker-compose.prod.yml`) und in `.env.example` aufnehmen.

**WB-2 — Keine Root-README; `web/README.md` ist aktiv irreführend** · 1,5 h · O
Ohne Root-README zeigt GitHub nur den Dateibaum; `CLAUDE.md` findet nur, wer weiß, dass es sie gibt. `web/README.md` ist die unveränderte Angular-CLI-Template-README — Zeile 55 behauptet, das Projekt habe kein E2E-Framework, während Playwright konfiguriert ist und das echte Kommando `npm --prefix web run e2e` heißt.
**Identisch mit S4-18** aus Welle E des Vorreviews — dort als erledigt markieren, nicht neu führen.

**WB-3 — Node- und SDK-Versionen an drei Orten verschieden** · 20 min · E
CI pinnt Node `"22"` (`.github/workflows/publish.yml:54`), der Dev-Container `"lts"`, das Api-Dockerfile `node:lts`. `lts` ist heute nicht 22. Es fehlen `global.json`, `.nvmrc` und `engines` in `web/package.json`. Ein lokaler Build kann grün sein und die CI rot.

**WB-4 — `Architectur.md` Abschnitt 5 ist gedriftet** · 45 min · O
Zeile 166 sagt „Alle sechs Entitäten sind implementiert" — es sind **acht**. Es fehlen:
- `VoteSessionEmote` (Migration `20260801005055`) — ausgerechnet das Herz des Voting-Subset-Redesigns, das ein Kapitel weiter oben beschrieben wird
- `AuditLogEntry` (Migration `20260731101655`)
- `VoteSession.HideResultsUntilEnd` (Migration `20260801120155`) — Secret-Ballot, auch im Modul-C-Kapitel nicht erwähnt
- `User.TwitchAccessToken`/`TwitchRefreshToken`/`TwitchAccessTokenExpiresAtUtc`/`TwitchTokenScopes` und `User.SessionsValidFromUtc` — die sicherheitsrelevantesten Spalten der Datenbank
- `AllowedRoles` hat fünf Werte inkl. `Broadcaster = 16`; Zeile 124 nennt nur vier

Gegenprobe: Die ausformulierten Snippets zu `Emote` und `UsageStat` (Zeilen 173–195) stimmen **exakt**. Der Drift ist punktuell, nicht flächig.

**WB-5 — Rollen×Endpoint-Matrix fehlt vollständig** · 45 min · O
Fünf Filterklassen (`ChannelManagementAuthorizationFilter`, `GlobalAdminAuthorizationFilter`, `UsageStatsAccessAuthorizationFilter`, `VoteAudienceFilter`, `VoteEligibilityFilter`), vier Rollen, ~30 Endpoints. Welcher Filter für welchen Endpoint gilt und warum, muss aus `MapGroup`-Ketten in sieben Dateien rekonstruiert werden. Für ein mittleres Feature mit neuem Endpoint ist das die **erste** Frage. Die Präzedenz selbst ist im Code ablesbar (`ChannelAccessService.cs:16–31` und `:33–56`), die Gesamtsicht existiert nirgends.

### Mittel

**TL-1 — Kein `format`/`format:check`-Script, keine `.prettierignore`** · 20 min · E
Prettier ist installiert, aber nur per `npx` mit handgetipptem Glob nutzbar. Der Glob sollte über ganz `web/` gehen statt nur `src/` — sonst bleiben `e2e/` (8 Dateien), `playwright.config.ts`, `angular.json` und die `tsconfig*.json` ungeprüft.

**TL-2 — Kein CI-Gate für Format und Lint** · 30 min · E
`publish.yml` hat bereits die Jobs `test` und `test-web`, die bei `pull_request` laufen und den `publish`-Job per `needs` gaten. Es braucht keinen neuen Workflow, nur je einen Step. **Teilmenge von S4-16** (Welle E) — dort den Format-/Lint-Teil als erledigt vermerken; Scan/Audit/Dependabot bleiben offen.

**TL-3 — 22 Dateien tragen ein UTF-8-BOM und verletzen `.editorconfig charset=utf-8`** · 20 min · E
9 EF-Migrations, 10 `.Designer.cs`, `AppDbContextModelSnapshot.cs` und 3 `.csproj`. `dotnet format` überspringt die `.Designer.cs` als generiert, meldet aber die 9 Migrations — das blockiert ein `--verify-no-changes`-Gate, solange es nicht behoben ist.

**ST-1 — Frontend: sechs Member-Reihenfolge-Abweichungen** · 1 h · E (teilweise)
| Datei:Zeile | Abweichung |
|---|---|
| `features/voting/vote-session-detail-page.ts:244` | `private syncSeenSinceReload` **nach** dem Konstruktor |
| `features/usage-stats/usage-stats-page.ts:229` | dito, mit **wortgleichem Kommentar** → copy-paste |
| `features/usage-stats/usage-stats-page.ts:146` | `inject(DestroyRef)` nach dem Signal-Block statt bei den übrigen `inject()` (L98–103) |
| `features/admin/admin-channels-page.ts:266` | mutables Feld zwischen protected Signals |
| `shared/datetime/datetime-picker.ts:155–161` | `inject()` **vor** `model()`/`input()` — einzige Datei mit dieser Reihenfolge |
| `features/channel-workspace/channel-workspace-layout.ts:141` | privater Helper vor allen öffentlichen Handlern |

Zusätzlich streuen private Helper zwischen öffentliche Methoden in 4 von 8 Dateien mit privaten Methoden (`vote-session-detail-page.ts` L259/342/350/355/377/428, `admin-channels-page.ts` L352/393, `usage-stats-page.ts` L234, `channel-workspace-layout.ts` L141).

**Grenze der Automatisierbarkeit:** ESLints `member-ordering` fängt die Feld-nach-Konstruktor-Fälle und die verstreuten `private`-Helper. Es kann die Reihenfolge *innerhalb* des Feldblocks **prinzipbedingt nicht** sehen, weil `input()`, `inject()`, `signal()` und `computed()` für den Parser alle identisch sind: Property-Initializer. Die Fälle `datetime-picker.ts` und `usage-stats-page.ts:146` bleiben Review-Disziplin.

**ST-2 — Backend: sechs Member-Reihenfolge-Abweichungen** · 45 min · K
| Datei:Zeile | Abweichung |
|---|---|
| `Worker/TwitchChatManager.cs:41` | mutables Feld zwischen readonly-Feldern |
| `Worker/TwitchChatManager.cs:60` | `private static CreateClient` zwischen Properties und public methods |
| `Worker/TwitchChatManager.cs:435` | `private static ReadTimestamp` zwischen zwei Event-Handlern |
| `Worker/SevenTv/SevenTvSubscriptionRegistry.cs:119–133` | public Properties **nach** den Methoden |
| `Worker/WorkerStats.cs:48–70` | dito |
| `Worker/SevenTv/SevenTvEventClient.cs:181` | `private readonly record struct MessageOutcome` mitten im Methodenblock |

`TwitchChatManager.cs` ist zugleich die **einzige** Datei mit echt gemischter Sichtbarkeit (Wechsel public→private an L75/118/258/281/318/342). Alle übrigen geprüften Dateien gruppieren sauber.

**WB-6 — Der Admin-Bereich hat kein Doku-Kapitel** · 45 min · O
Er existiert als voller vertikaler Schnitt: 8 Endpoints, 4 Frontend-Seiten, 3 Services, eigene Entität, eigener Live-Stream, 4 E2E-Specs. In der Doku steht dazu **eine Klammerbemerkung** (`Architectur.md:136`). Das Wort „Audit" kommt in `CLAUDE.md` nicht vor. Umfangsmäßig konkurriert der Bereich mit Modul C, das ein eigenes Kapitel hat.

**WB-7 — Die SSE-Live-Pipeline ist viermal kopiert** · 1,5 h · O
`usage-stats-page.ts:197–224`, `vote-session-detail-page.ts:218–240`, `admin-channels-page.ts:293–304`, `admin-monitoring-page.ts:246–252`. Identische Struktur (`toObservable(url) → switchMap(stream) → filter → tap → debounceTime → takeUntilDestroyed`), identischer `syncSeenSinceReload`-Flag-Trick — und **fast wortgleiche Kommentare, die denselben Sachverhalt zweimal erklären**. Gehört als `liveReload(url, types, opts)` nach `core/live/`.

**WB-8 — `ChannelQueries.LoadChannelAsync` wird nur zur Hälfte benutzt** · 1 h · O
Der Helper wurde in Welle C des Vorreviews genau gegen diese Duplikation gebaut; sein eigener Klassenkommentar (`ChannelQueries.cs:8–9`) sagt: „just the exact two shapes that were copied by hand six times across three services." Genutzt wird er nur von den Vote-Session-Services. **Neun Stellen sind weiterhin handkopiert**: `ChannelService.cs:15,41,69,92,99`, `SevenTvSyncService.cs:26,97`, `EmoteService.cs:14`, `EmoteSetOwnershipService.cs:30`.
Das ist die tückischste Sorte Duplikat: Die Abstraktion **existiert**, wird aber nur halb benutzt — wer `ChannelService.cs` liest, lernt das falsche Muster.

**WB-9 — `CLAUDE.md` beschreibt zwei Ist-Zustände falsch** · 15 min · O
- Zeile 159: „Der Bestand in `EmotePurge.Worker` ist überwiegend deutsch und bleibt es." Gemessen: **~292 englische zu ~38 deutschen** Kommentarzeilen. Der Satz stammt aus Welle A (S4-14) und ist durch die Umbauten seither überholt. Ein Neuer liest „Worker = deutsch", öffnet `ReconnectPolicy.cs` und findet lupenreines Englisch.
- Zeile 20: Welle E nennt „Monitoring" als offen. Monitoring **ist gebaut** (`admin-monitoring-page.ts`, `GET /api/admin/health`, `WorkerHealthPublisher`, Live-Push). Offen sind Container-Healthchecks (S3-35) und Log-Aggregation (S3-36) — etwas anderes als das, was die Zelle behauptet.
- Der Admin-Bereich fehlt als eigene Zeile in der Umsetzungsstand-Tabelle.

**WB-10 — `DELETE_DELAY_MS` ist doppelt deklariert statt importiert** · 10 min · O
`core/seven-tv/seven-tv-delete.service.ts:29` und `seven-tv-delete.service.spec.ts:48`. Wer den Service-Wert ändert, bekommt keinen roten Test, sondern einen still falschen.

### Niedrig

**WB-11 — Konstanten-Duplikate mit widersprüchlichen Werten** · 30 min · O
`LIVE_RELOAD_DEBOUNCE_MS` ist **1000** in `usage-stats-page.ts:64`, aber **500** in `vote-session-detail-page.ts:49` — gleicher Name, verschiedener Wert, keine gemeinsame Quelle. `ROW_HEIGHT_PX` ist 128 vs. 192 (sachlich korrekt, aber die Namensgleichheit suggeriert dieselbe Größe). `PAGE_SIZE = 25` steht in `admin-audit-log-page.ts:21` und `admin-users-page.ts:22`; `RESYNC_FEEDBACK_MS`/`ROLE_CACHE_FEEDBACK_MS` sind derselbe Wert unter zwei Namen.

**ST-3 — Regel 12 verletzt: vier Dateien in `core/` ohne Spec** · 1 h · E
`core/auth/home.guard.ts` (der einzige Guard ohne Spec — `auth`, `admin`, `vote-session-access`, `usage-stats-access` haben alle einen), `core/i18n/language.service.ts`, `core/i18n/locale.ts`, `core/i18n/plural.ts`. Die 19 fehlenden Specs unter `shared/ui|seven-tv|datetime` sind dagegen **regelkonform** — Komponententests sind laut Regel 12 bewusst ausgenommen.

**ST-4 — `WorkerHealthStatus.cs` liegt in `Endpoints/`, enthält aber keine Endpoints** · 15 min · K
70 Zeilen Ableitungslogik (`Derive`) plus `internal readonly record struct WorkerHealthDerived`. Dort sucht sie niemand.

**TL-4 — Beide `.editorconfig` haben `root = true`** · 5 min · E
`web/.editorconfig:2` setzt `root = true` und kappt damit die Vererbung aus der Root-Datei — `end_of_line = lf` gilt unterhalb von `web/` **nicht**.

**ST-6 — Drei ASP.NET-Template-Rauschkommentare** · 5 min · G
`src/EmotePurge.Api/Program.cs:19,21,135` („Add services to the container.", „Learn more about configuring OpenAPI…", „Configure the HTTP request pipeline."). Die einzigen Kommentare im Repo, die ersatzlos gelöscht gehören.

**ST-7 — Drei ungenutzte Type-Exports** · 5 min · E
`ButtonVariant`, `ButtonSize` (`shared/ui/button.ts`), `NoticeVariant` (`shared/ui/notice-banner.ts`) — außerhalb ihrer Deklarationsdatei nirgends importiert.

### Beobachtet, aber kein Befund

- **Startup-Reihenfolge der Hosted Services.** `Worker/Program.cs` hat null Kommentare, obwohl die Reihenfolge relevant ist (Boot-Recovery vor Resync, Kopplung über `BootRecoveryGate`). Der Gate selbst ist aber exzellent dokumentiert; ein Zweizeiler in `Program.cs` genügt und fällt in WB-6 mit ab.
- **`ReconnectPolicy.StuckOpenThreshold = 10 min`** sagt *was*, aber nicht *warum 10 und nicht 5 oder 30*. Einzige nennenswerte Lücke in einer sonst vorbildlich begründeten Policy.
- **`UsageFlushWorker.FlushInterval = 30 s`** ist unbegründet — die 30 s stehen als Leitprinzip in `Architectur.md:23`, aber ohne Zahlenbegründung.
- **Von ~25 Betriebsparametern sind nur 2 zur Laufzeit konfigurierbar** (`SevenTv:ResyncIntervalSeconds`, `SevenTv:EventApi:Enabled`). Alles andere ist compile-time. Das ist eine kohärente Entscheidung — nichts ist halb-konfigurierbar —, aber eine, die man kennen muss: Ein Prod-Incident „das Rate-Limit ist zu streng" bedeutet Code-Änderung, Build, Image-Push, Redeploy. Gehört in die README, nicht auf die Befundliste.
- **Vier undokumentierte Config-Keys** neben WB-1: `Auth:Twitch:PostLoginRedirectUrl`, `Auth:ModCheckCacheTtlMinutes`, `Auth:Twitch:AccessTokenLifetimeOverrideSeconds` (Test-Hook), `DataProtection:KeyPath` (nur als Code-Kommentar). Werden in der README mitdokumentiert.

---

## 5. Bewusst nicht umgesetzt

- **Kein CSharpier.** `dotnet format` ist im SDK enthalten, braucht kein Tool-Manifest, und der handgeschriebene Bestand ist bereits zu 100 % konform. Ein zweiter Formatter mit eigener Meinung würde einen Sweep *erzeugen*, den es sonst nicht gäbe.
- **Kein StyleCop für die C#-Member-Ordnung.** 9/12 Dateien folgen der Ordnung bereits; StyleCop bringt ~200 Regeln, von denen ~190 unterdrückt werden müssten, um sechs Fundstellen zu erwischen. Stattdessen dokumentierte Konvention plus manueller Fix (ST-2).
- **Kein `recommended`-ESLint-Satz.** `no-explicit-any`, `no-unused-vars` und die Template-Regeln über 94 Dateien wären ein zweiter, unvermessener Sweep. Kann später additiv kommen.
- **Kein husky/lint-staged.** Zwei zusätzliche Dependencies plus `prepare`-Hook plus Reibung bei jedem Commit — für einen Bestand, der zu 87 % ohnehin konform ist und dessen PRs bereits durch CI gehen.
- **Keine Umstellung Inline- ↔ externe Templates (ST-5).** Die Aufteilung ist uneinheitlich: fünf externe Templates (121–314 Zeilen), aber vier Inline-Templates von 126–164 Zeilen. Der de-facto-Schwellwert liegt irgendwo zwischen 121 und 164. Allerdings ist `features/admin/` in sich durchgängig inline und `features/voting|usage-stats|landing|overview` durchgängig extern — es ist also nicht zufällig, sondern feature-lokal konsistent. Ein Umbau wäre reines Zeilenverschieben mit Merge-Konflikt-Risiko. Geschmack, nicht Wartbarkeit.
- **Keine Auflösung der 22 Ein-Implementierungs-Interfaces** in `Core/Services/`. Sie werden nie gemockt (NSubstitute läuft nur gegen die Boundary-Ports `IRedisPublisher`, `ISevenTvApiClient`, `ITwitchHelixClient`), tragen aber die Schichtentreue nach Regel 4/5. Der Preis — zwei Dateien plus eine DI-Zeile pro neuer Fähigkeit — ist bekannt und akzeptiert; er gehört in die README als Erwartungsmanagement, nicht auf die Abrissliste.
- **Keine Neuaufteilung der großen Feature-Seiten.** Das additive Wachstum des State-Blocks (Abschnitt 1) ist der härteste Teil von Kritikpunkt 1, aber ein Umbau von `vote-session-detail-page.ts` und `usage-stats-page.ts` ist ein eigenes Vorhaben mit echtem Regressionsrisiko. Bleibt offen und benannt.
- **Keine Api-Tests.** Es gibt kein `tests/EmotePurge.Api.Tests`; Endpoints, alle fünf Auth-Filter, `ChannelAccessService`, `VoteEligibilityService` und die `Program.cs`-Pipeline sind ohne automatisiertes Netz. Das ist **Welle D des Vorreviews**, nicht dieses Reviews.

---

## 6. Abgrenzung zu `Review-2026-07-29-Umsetzung.md`

Drei Überschneidungen. Sie werden **nicht** neu geführt, sondern im Vorreview als erledigt markiert, sobald die jeweilige Welle durch ist:

| Vorreview | Deckt hier ab |
|---|---|
| **S4-18** Repo-Hygiene: kein README/LICENSE/CONTRIBUTING | WB-2 |
| **S4-16** Fehlende CI-Hygiene: kein Lint/Format/Scan/Audit | TL-2 — **nur** der Format-/Lint-Teil; Scan, `npm audit`, NuGet-Cache und Dependabot bleiben offen |
| **S3-36** Keine strukturierten Logs, keine Metriken, kein Alerting | Der Messbefund präzisiert ihn: Die **Logs selbst sind bereits strukturiert** (118/118 Templates). Offen ist die *Aggregation* — man kommt an Prod-Logs nur per `docker compose logs` bzw. SSH+Portainer, und das ist nirgends dokumentiert. |

**Nebenbefund zur Pflege des Vorreviews:** Die Welle-D-Liste ist selbst veraltet. `UsageStatFlushServiceTests` (8 Facts) und die Fehlercode-Paritätsprüfung (`api-error.spec.ts`, 3 Its) sind dort als offen gelistet, existieren aber. Wirklich offen sind nur `ChannelAccessServiceTests`, `VoteEligibilityServiceTests`, Tests für `VoteSessionService.CastVoteAsync` und der Core-Assembly-Struktur-Test. Ebenso steht dort auf Zeile 71 „verifiziert: fünf registrierte Hosted Services" — es sind heute sechs (historisch korrekt für ein datiertes Umsetzungsdokument, hier nur zur Klarstellung notiert).

---

## 7. Messmethodik

Damit die Zahlen oben nachvollziehbar und wiederholbar bleiben:

- **Prettier-Trockenlauf:** `npx prettier --check "src/**/*.{ts,html,css,scss,json}"` in `web/`, prettier 3.9.6. Schreibt nichts.
- **`dotnet format`-Trockenlauf:** `dotnet format EmotePurge.slnx --verify-no-changes --no-restore`, SDK 10.0.302, ~28 s. Schreibt nichts; `git status --porcelain` war davor und danach leer.
- **Zeilenlängen:** `awk length($0)`-Histogramm über alle `.ts`/`.html` bzw. `.cs` ohne `node_modules`, `bin`, `obj`.
- **Quotes:** Anteil über Import-Statements (`from '…'` vs. `from "…"`) statt über alle Zeichen — Apostrophe in Strings und Kommentaren verfälschen die Rohzählung.
- **Trailing Commas:** `awk`-Nachbarzeilenvergleich (Zeile endet auf `,`, Folgezeile beginnt mit `)`/`]`/`}`), gegengeprüft an 10 Stichprobendateien.
- **Kommentarsprache:** Zeilen mit `//`/`///` je Projekt, klassifiziert per Stoppwort-Regex (`der|die|das|und|nicht|wird|damit|…` vs. `the|is|that|this|because|…`). Grob, aber für Verhältnisse aussagekräftig.
- **Log-Messages:** alle Message-Templates aus `Log(Information|Warning|Error|Debug|Trace|Critical)`-Aufrufen extrahiert und einzeln gesichtet (118 Stück). Interpolierte Strings per `grep -rE 'Log\w+\(\s*\$"'`.
- **Member-Reihenfolgen:** 14 Angular- und 12 C#-Dateien manuell als Zeilensequenz erfasst, nicht gesampelt.
