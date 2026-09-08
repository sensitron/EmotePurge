# Nachtlauf 2026-09-08 — Shared-Chat-Anzeige, Zug 2 (Issue #73)

**Branch:** `feat/shared-chat-anzeige-73-zug2` · **Modus:** `durchlauf` (unbeaufsichtigt) ·
**Plan:** [`docs/superpowers/plans/2026-09-08-shared-chat-anzeige-73-zug2.md`](../superpowers/plans/2026-09-08-shared-chat-anzeige-73-zug2.md)

## Was ist passiert

**Erledigt: der Plan vollständig, Tasks 1–6.** Drei Commits, alle Gates gefahren und grün, die
Live-Probe aus Task 5 belegt. Der Lesepfad rechnet ab jetzt über `UseCount` allein, und der
erklärende Satz steht im selben Commit auf der Seite. Der Marker-Test heißt jetzt
`SharedOnlyRow_ReadsAsUnused_LikeBotOnly` und behauptet mit demselben Seed das umgekehrte
Verhalten — er ist nicht „repariert", sondern umgeschrieben.

**Nicht erledigt (bewusst, weil im Nachtlauf gesperrt):** kein `git push`, kein PR, kein Deploy,
keine Codex-Zweitmeinung. Die drei Commits liegen lokal.

**Offen für den Nutzer:** die Sichtprüfung im Browser (de/en, hell/dunkel, ein Kanal mit und einer
ohne Shared-Chat-Historie) und die Coverage-Entscheidung unten.

### Gates: was gelaufen ist und mit welchem Ergebnis

| Gate | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx --no-incremental` | **0 Warnungen, 0 Fehler** |
| `dotnet test EmotePurge.slnx` (Docker/Testcontainers) | **1025 grün, 0 rot** — Worker 253, Api 96, Infrastructure 676 (Laufzeit 1 s / 4 s / 12 s) |
| `npm --prefix web test -- --watch=false` | **854 grün** in 89 Dateien, 2,7 s |
| `npm --prefix web run e2e` | **116 grün**, 58,2 s — im gewohnten Rahmen, `:5151` war vorher und nachher frei |
| `npm --prefix web run lint` | sauber (inkl. `check-color-tokens`) |
| `npm --prefix web run format:check` | „All matched files use Prettier code style!" |
| `dotnet format EmotePurge.slnx --verify-no-changes` | Exit 0 |
| `node scripts/coverage-local.mjs` (nach den Commits) | **echte Messung, 24 geänderte Dateien, 9 relevant — 59,8 % Gesamtquote.** Siehe „Coverage" unten. |
| Live-Probe (Task 5, Regel 16) | **13/13 Prüfungen grün** gegen echtes Postgres, Prüfstand rückstandslos abgeräumt |

Die im Auftrag genannten `verify`-Kommandos (`pwsh server/scripts/verify.ps1`, `npm run verify` in
`spa/`) existieren in diesem Repository **nicht** — sie stammen aus der Homeport-Vorlage des
Nachtlauf-Rahmens. Verbindlich ist hier die Gate-Liste aus `CLAUDE.md`, und die ist die Tabelle
oben. Das ist die einzige Abweichung vom Auftragstext und keine Auslassung.

## Annahmen und Entscheidungen

Alles, was der Plan vorentschieden hat, ist unverändert übernommen worden. Neu entschieden wurde
nur, was der Plan nicht wissen konnte:

1. **Fünf Frontend-Spec-Fixtures mussten mitgezogen werden**, weil `sharedChatSeparatedSince` ohne
   Default in `EmoteSetStatus` steht (Plan-Entscheidung Nr. 7: der Compiler ist die Suchhilfe). Der
   Typprüfer hat drei davon erzwungen — `file-import-trigger.spec.ts`, `import-flow.spec.ts`,
   `restore-flow.spec.ts` (Spread über `Partial<EmoteSetStatus>` macht ein fehlendes Feld
   `undefined`). Die vierte, `emote-admin.service.spec.ts`, hat der Compiler **nicht** erzwungen
   (`flush()` ist untypisiert), aber ihr `toEqual`-Paar ist der Test, der die Durchreiche des
   ganzen DTO behauptet — ohne das neue Feld hätte er stillschweigend aufgehört, sie ganz zu
   prüfen. Ich habe es dort in Flush **und** Erwartung ergänzt. Die fünfte,
   `import-target-loader.spec.ts:13` (`READY_STATUS`), habe ich **nicht** angefasst: sie ist ein
   untypisiertes Flush-Payload, der Loader liest die neuen Felder nicht, und der Plan verbietet
   Aufräumen am Rand. Vertretbare Gegenmeinung: man könnte sie der Vollständigkeit halber
   nachziehen.
2. **Reihenfolge der Sätze im Caption-Absatz** ist als Vertrag in §2.5 der Designsprache
   festgeschrieben (Zählbeginn → Live-Tage → Bots → geteilter Chat), wie im Plan unter 3.5
   vorgesehen — nur dann darf der E2E-Fall sie prüfen (Regel 12). Der neue E2E-Fall prüft, dass
   alle vier Sätze nebeneinander stehen, nicht ihren Wortlaut als Selbstzweck.
3. **Die `usageFlushed`-Bedingung** fragt jetzt nach, wenn **eines der beiden** Daten `null` ist
   (`!bots || !shared`). Das ist die im Plan vorgesehene Fassung. Es bleibt bei höchstens einer
   Statusanfrage je Flush-Ereignis; die Anfrage hört nur später auf, nämlich erst wenn beide Daten
   stehen. Für einen Kanal, der nie geteilten Chat sieht, heißt das: eine Statusanfrage pro
   Flush-Ereignis, dauerhaft. Das war vorher für Kanäle ohne Bots genauso, ist also keine neue
   Klasse — aber es ist der eine Punkt, an dem der Rückbau etwas Laufendes teurer macht, und
   deshalb steht er hier.
4. **Restfundstellen zu „D5"** gibt es noch fünf (zwei Kommentare in `UsageStatQueryService.cs`,
   zwei im Marker-Test, eine im Docblock der Caption-Funktion). Alle sind **rückblickend**
   formuliert („through the D5 transition", „never carried", „the inverted D5 marker") und
   behaupten keine noch bestehende Übergangskonstruktion — die Abnahmebedingung 6 des Plans ist
   damit erfüllt. Sie stehen bewusst da: sie erklären, warum der Index nicht angefasst wurde und
   wofür der Test der Beleg ist.
5. **Der Weg der Live-Probe war der über die Services**, wie vom Auftrag ausdrücklich freigegeben
   — Twitch-OAuth im Browser ist nachts nicht herstellbar. Details unten.

## Coverage: der eine Befund, der eine Entscheidung braucht

`node scripts/coverage-local.mjs` meldet **59,8 %** über acht gemessene Dateien und liegt damit
unter der 80-%-Schwelle. Der Plan hat genau diesen Fall unter Task 4 vorentschieden: **kein
improvisierter Komponenten-Spec**, sondern der Befund mit Zahl an den Nutzer. Das ist hier passiert.

Die Zahl kommt fast vollständig aus **einer** Zeile der Tabelle:

```
web/src/app/features/usage-stats/usage-stats-page.ts    0/399 Zeilen, 0/185 Zweige    0.0 %
```

Die Datei hat keinen Vitest-Spec (Plan-Entscheidung Nr. 11), geht aber mit **allen** 399 Zeilen in
den Nenner — obwohl mein Diff dort genau **acht** ausführbare Zeilen anfasst: ein `import`, ein
dreizeiliges `computed()` und eine vierzeilige `else if`-Bedingung. Sonar misst zeilengenau auf
neuem Code; realistisch stehen dort also ~8 ungedeckte gegen ~15 gedeckte neue Zeilen im Rest des
Branches (Query-Methode, DTO-Feld, Service-Feld, Caption-Funktion — die vier sind zu 91–100 %
gedeckt). Ob das Sonars `new_coverage` über oder unter 80 % trägt, kann ich lokal nicht sagen; die
Schätzung ist in beide Richtungen unscharf, und im Repository ist schon einmal gemessen worden,
dass Sonar bei 77,8 % Zeilendeckung 90 % `new_coverage` meldete.

**Die Entscheidung gehört dem Nutzer.** Zwei Wege, falls das Gate am PR reißt:
- Die acht Zeilen decken hieße, `usage-stats-page.ts` seinen ersten Spec zu geben (SSE-Stub,
  Transloco-Boot, `rxResource`-Aufbau) — genau das, wovon der Plan einen unbeaufsichtigten Lauf
  ausdrücklich abhält.
- Oder das Gate am PR bewusst überstimmen, mit dem Argument, dass die Entscheidungslogik der
  Caption vollständig in der puren, zu 100 % gedeckten Funktion liegt und die Seite nur verdrahtet.

## Live-Probe (Task 5, Regel 16)

**Zwei Abweichungen aus dem Auftrag befolgt:** Schritt 1 des Plans (`docker compose up postgres
redis`) ist entfallen — der geteilte Dev-Stack lief bereits und ist unberührt geblieben
(`emotepurge-dev-worker/-api/-postgres/-redis`, alle vier durchgehend `healthy`). Aus dem Worktree
wurde **kein** `docker compose`-Kommando abgesetzt. Der Prüfkanal wurde mit `IsBotActive = false`
angelegt, damit kein Boot-Recovery oder Resync des laufenden Worker-Prozesses ihn joint.

**Migrationsstand geprüft, nicht angewandt:** `dotnet ef migrations list --connection …` zeigt
`20260907080507_AddUsageStatSharedChatUseCount` ohne `(Pending)` — die Zug-1-Migration liegt an.

**Gewählter Weg: über die Services, nicht über die Api.** Die Endpunkte hängen hinter Auth und dem
Zugriffsfilter, und eine echte Twitch-Session ist nachts nicht herstellbar. Statt dessen ein
Wegwerf-Konsolenprogramm im Scratchpad, das die echten `UsageStatQueryService`- und
`EmoteSetStatusService`-Instanzen gegen das lokale Dev-Postgres fährt. `EmoteSetStatusService` ist
exakt das, was `GET /api/channels/{name}/emotes/active-set` zurückgibt — die Aussage über den
Endpunkt ist damit belegt, der HTTP-Rahmen darum herum nicht. Das ist der im Plan vorgesehene Weg.

**Prüfstand** (Kanal `zug2probe`, ein Emote, drei Zeilen):

| Tag | `UseCount` | `SharedChatUseCount` |
|---|---|---|
| 2026-09-01 | 10 | 0 |
| 2026-09-03 | 4 | 6 |
| 2026-09-05 | 0 | 7 |

**Ergebnis, 13 von 13 Prüfungen grün:**

- `GetEarliestSharedChatUsageDateAsync` = **2026-09-03** — der gemischte Tag, nicht der rein fremde.
  Genau die Verwechslung, die eine falsch gebaute Query machen würde.
- `EmoteSetStatusDto.SharedChatSeparatedSince` = **2026-09-03**; `BotsExcludedSince` bleibt `null`
  (die eingefrorene Bot-Methode ist nachweislich unberührt).
- `GetUsageContextAsync.TotalUseCount` = **14** (10 + 4), nie 27 (10 + 4 + 6 + 7).
  `LastUsedDate` = **2026-09-03**, nicht der jüngere rein fremde Tag.
- `GetTotalsByEmoteIdsAsync` = **14** — die Nutzungsspalte der Voting-Ergebnisse fällt still mit,
  wie in Nr. 10 des Plans entschieden.
- `GetDailySeriesAsync` und `GetChannelSeriesAsync`: **2 Tage** statt 3, Summe **14**, der gemischte
  Tag trägt **4** statt 10 — der rein fremde Tag ist aus beiden Reihen verschwunden.
- `GetRowsAsync` (Harness): **3 Zeilen**, rohe `SharedChatUseCount`-Summe **13** — unverändert
  ungefiltert, wie #69 es braucht.

**Aufräumen belegt:** Der Prüfkanal ist restlos gelöscht (`Cleanup: probe channel rows remaining =
0`). Auf `:5151` lief zu keinem Zeitpunkt eine Api; die E2E-Suite ist vorher gelaufen.

## Offene Punkte und Nebenfunde

- **Sichtprüfung im Browser steht aus** — kein unbeaufsichtigter Lauf kann sie leisten. Zu prüfen:
  der Satz in de und en, hell und dunkel, auf einem Kanal mit und einem ohne Shared-Chat-Historie,
  und dass er unter dem Bogen umbricht statt zu überlaufen.
- **Der `impeccable`-Design-Hook meldet in `usage-stats-page.html` einen Befund an Zeile 512**
  (`design-system-font-size`, ein literales `9px` außerhalb der Typo-Rampe). Das ist **Bestand**
  und liegt nicht in meinem Diff; ich habe ihn stehen gelassen und nichts unterdrückt — der Plan
  verbietet Aufräumen am Rand. Ein eigenes Issue wert, wenn der Nutzer ihn bestätigt.
- **`import-target-loader.spec.ts:13` (`READY_STATUS`)** ist jetzt ein unvollständiges
  Api-Antwort-Fixture (s. Annahme 1). Harmlos, aber jemand wird beim nächsten Feld darüber
  stolpern.
- **Keine Codex-Zweitmeinung gefahren** — der Auftrag reserviert das Kontingent für den wachen
  Merge. Regel 22 ist damit vor dem Merge noch offen.
- **Keine Migration nötig**, Zug 2 ändert kein Schema (Plan Nr. 8). Der Prod-Deploy braucht nur die
  Images.
- **Die #69-Uhr ist unberührt.** Kein `AlgorithmVersion`-Bump, kein neuer Stichtag, kein Eingriff
  in den Harness-Rechenkern — die einzige Berührung dort ist ein gelöschter, veralteter
  Docstring-Absatz in `ReplayFidelityCalculator.BuildPopulation`. Die Freeze-Liste läuft nicht mit
  diesem Merge aus.

## Commits

```
f0981f2 feat(usage-stats): count only own usage and say since when shared chat is excluded
b6aac6d feat(usage-stats): add the shared-chat separation caption and its translations
cfda060 feat(usage-stats): expose the earliest shared-chat usage date on the set status
```

`docs/DECISIONS.md`, `docs/Architectur.md` und `docs/UI-Designsprache.md` liegen im selben Commit
wie die Vertragsänderung (`f0981f2`, Regel 3).
