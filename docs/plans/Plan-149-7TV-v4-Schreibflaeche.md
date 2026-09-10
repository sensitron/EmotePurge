# Plan #149 — Umlaut-Aliase: die 7TV-Schreibfläche von v3 auf v4 umstellen

Erstellt am 2026-09-10 gegen `main` = `c49a881`. Quellen: Issue #149, der Abnahmelauf zu #147
(gemergt als `84b39d2`), 7TVs Quellcode (`SevenTV/SevenTV` @ `e1733255`) und eigene Live-Proben
gegen `7tv.io` vom 2026-09-10.

Der Plan enthält keinen Code. Er beschreibt Absicht, Verträge, Grenzfälle und Reihenfolge.

---

## 0. Beweislage

Alles hier ist gemessen oder aus der Quelle zitiert, nichts angenommen.

### 0.1 Der Befund

`v3`s Feld `emoteSet(id).emotes(id, action, name)` legt auf das `name`-Argument den
`EmoteNameValidator` mit `^[a-zA-Z0-9_\-():!+|.'?><\p{Emoji_Presentation}*$#]{1,100}$` — ASCII plus
Emoji. Das Argument ist für `ADD` **und** `UPDATE` dasselbe, der Validator ebenso. Der Vorschlag aus
dem Issue (erst mit Basisnamen hinzufügen, dann umbenennen) kann deshalb nicht tragen.

`v4` hat genau das repariert (`SevenTV/SevenTV#228`, gemergt 2025-12-01): Set-Aliase laufen dort über
den unicode-fähigen `EmoteAliasValidator`, nur der kanonische Emote-Name bleibt streng. `v3` wurde nie
nachgezogen.

Live gemessen am 2026-09-10 gegen das Set `01M16ZBA3NHWWYNM7Q9416ZCH7`:

| Probe | Ergebnis |
|---|---|
| `v3` ADD, Alias `Sitzgemüse` | `Failed to parse "String": invalid emote name` |
| `v4` `addEmote`, derselbe Alias | Erfolg, ein Aufruf |
| `Löwe`, `Gänsehosen`, `UnterschätzDasMalNicht` | alle drei akzeptiert |
| `ß`, `café`, `Привет`, `絵文字`, `party😀time` | alle akzeptiert |
| `a b` (Leerzeichen) | abgelehnt, `invalid emote alias` — anderer Validator, anderer Text |

Die Sperre trifft also die gesamte Nicht-ASCII-Klasse, nicht nur Umlaute. Das Zurücklesen über `v3`
liefert den Umlaut-Alias korrekt — `v3` kann lesen, nur nicht schreiben.

### 0.2 Warum „Alias weglassen" keine Alternative ist

Ohne `name` fällt 7TV auf den Basisnamen zurück. In HandOfBloods Set haben **alle 27** Aliase mit
Umlaut oder ß einen abweichenden, rein asciifizierten Basisnamen (`Gänsehosen`→`Gaensehosen`,
`wutköder`→`rageBait`, `SchrödingersChat`→`CrossEyesBob`). Weglassen setzte still den falschen Namen —
schlechter als der heutige gemeldete Fehlschlag.

### 0.3 Was sich beim Umzug **nicht** ändert

`v4` benutzt dasselbe `ApiError`-Modul wie `v3` (`apps/api/src/http/error.rs`). Rate-Limiting bleibt
damit bitgleich: Code `RATE_LIMIT_EXCEEDED`, `extensions.status: 429`, Eimer `emote_set_change`,
1 Ticket pro Aufruf, und die Reset-Werte reisen weiterhin in `extensions.headers` unter
`x-ratelimit-emote_set_change-*` (Sekunden, aus Redis' `TTL`). `isRateLimitError`
(`seven-tv-run-engine.ts:160-162`), `readRateLimitInfo` (`:164-172`) und `parseHeaderNumber`
(`:151-158`) bleiben unangetastet. Live bestätigt: der Header-Satz erschien unverändert auf der
`v4`-Mutation.

Der Auth-Interceptor ist ebenfalls nicht betroffen: `api-auth.interceptor.ts:35` filtert über die
Positivliste `req.url.startsWith('/api/')`, nicht über die 7TV-URL. Nur der Kommentar bei `:25` nennt
`v3` und wird mitgezogen.

### 0.4 Was beim Umzug **bricht** — die Abbruchlogik

`v4` meldet fehlende Schreibrechte als **HTTP 200** mit
`extensions.code = "LACKING_PRIVILEGES"`, `extensions.status = 403` und dem Text
`"LACKING_PRIVILEGES you are not an editor for this user"` (bzw. `"… you do not have permission to
manage this user's emote sets"`).

`abortsForMissingPrivileges` (`seven-tv-import.service.ts:42-51`) prüft heute auf `httpStatus`
401/403 und auf die Substrings `insufficient privileges` / `missing permission`
(`PRIVILEGE_ERROR_FRAGMENTS`, `:36`). Nach dem Umzug greift **keine** der beiden Bedingungen:
`httpStatus` ist `null`, weil HTTP 200, und keiner der Substrings kommt vor. Ein Import ohne
Schreibrecht liefe dann durch alle Zeilen statt abzubrechen und verbrennte dabei das
Rate-Limit-Budget. Das ist der eigentliche Risikopunkt dieses Umbaus.

Die Reparatur ist zugleich eine Verbesserung: `extensions.code` ist ein strukturiertes Feld. Die
geratene Substring-Liste — im Kommentar bei `:32` selbst als „as far as it is known from their
frontend" markiert — entfällt ersatzlos.

---

## 1. Vertragsänderungen

### V1 — Endpunkt

`SEVEN_TV_GQL_ENDPOINT` (`seven-tv-run-engine.ts:24`) zeigt auf `https://7tv.io/v4/gql`. Ein
Endpunkt für die ganze Schreibfläche; gemischter Betrieb ist ausdrücklich nicht das Ziel.

### V2 — Die drei Mutationen

`v4` hat das `action`-Enum abgeschafft und je Operation ein eigenes Feld. Alle drei liegen unter
`emoteSets { emoteSet(id:) { … } }`, alle drei nehmen den Emote über das Input-Objekt
`EmoteSetEmoteId { emoteId: Id!, alias: String }`:

- **Hinzufügen** — `addEmote(id: { emoteId, alias })`. Der Alias reist **im Input-Objekt** mit, nicht
  als Geschwister-Argument. Das ist der ganze Fix: ein Aufruf, kein Zwischenschritt, kein Teilzustand.
- **Entfernen** — `removeEmote(id: { emoteId })`. Kein Alias.
- Die Variablentypen heißen `Id!` statt `ObjectID!`. Live bestätigt, dass unsere vorhandenen IDs
  unverändert auflösen — sowohl Set- als auch Emote-ID.

Betroffen sind drei Stellen mit heute **wortgleicher** ADD-Mutation:
`seven-tv-import.service.ts:21-28`, `seven-tv-restore.service.ts:20-27` und — für REMOVE —
`seven-tv-delete.service.ts:27-34`.

### V3 — Der Fehlercode wandert bis zur Abbruchentscheidung durch

Heute trägt ein Zeilenfehlschlag nur `{ message, httpStatus }` (`seven-tv-run-engine.ts:118-119`,
Hook bei `:559`). Er bekommt zusätzlich den Fehlercode aus `extensions.code` — `null`, wenn keiner da
ist (Transportfehler, leerer Body).

`abortsForMissingPrivileges` entscheidet danach auf `LACKING_PRIVILEGES`; die HTTP-Prüfung auf
401/403 bleibt, weil ein ungültiger Token weiterhin einen echten `HTTP 401` liefert — mit einem Body
**außerhalb** des GraphQL-Schemas (`{"status":"Unauthorized","error_code":1000,"error":"invalid
session"}`, live gemessen), aus dem sich nichts extrahieren lässt. `PRIVILEGE_ERROR_FRAGMENTS`
entfällt.

### V4 — Verhaltensparität, ausdrücklich

Der Umbau ändert außer dem Bugfix **nichts** am beobachtbaren Verhalten. Insbesondere bekommt der
Löschlauf **keine** neue Abbruchbedingung, obwohl er heute bei fehlenden Rechten alle Zeilen
durchläuft. Das ist Bestandsverhalten, kein Migrationsschaden — s. Abschnitt 3.

---

## 2. Tasks

Jeder Task ist ein eigener Subagent mit frischem Kontext. Reihenfolge ist bindend: T1 legt den
Vertrag fest, auf den T2–T4 sich stützen.

### T1 — Engine: Endpunkt und Fehlercode-Durchreichung

`seven-tv-run-engine.ts`. Endpunkt auf `v4`. Der Fehlschlag-Datensatz und der `abortOn`-Hook tragen
zusätzlich den Fehlercode aus `extensions.code`. Rate-Limit-Pfad unangetastet — `isRateLimitError`
prüft bereits `code === 'RATE_LIMIT_EXCEEDED' || status === 429` und deckt `v4` damit unverändert ab.

Der Kommentarblock bei `:25-28` behauptet, 7TVs Quote für `emote_set_change` stehe „not in their
open-source tree". Das stimmt nicht mehr: sie ist inzwischen belegt (100 pro 60-Sekunden-Fenster,
live aus dem Header abgelesen). Kommentar berichtigen.

Tests: `seven-tv-run-engine.spec.ts` — Endpunkt-Konstante bei `:31`, die Body-Zusicherung bei
`:124-129`. Neuer Fall: ein GraphQL-Fehler mit `extensions.code` reicht den Code an `abortOn` durch.

### T2 — Import- und Restore-Pfad auf `addEmote`

`seven-tv-import.service.ts` und `seven-tv-restore.service.ts`. Beide auf die `v4`-ADD-Mutation aus
V2. In `seven-tv-import.service.ts` zusätzlich `abortsForMissingPrivileges` auf den Fehlercode
umstellen und `PRIVILEGE_ERROR_FRAGMENTS` entfernen.

Tests: `seven-tv-import.service.spec.ts` (`:26`, `:98-105`, Privilegien-Fall `:172-191` — der fütterte
bisher `insufficient privileges` als rohen Text und muss auf die `v4`-Form umgestellt werden) und
`seven-tv-restore.service.spec.ts` (`:26`, `:82-89`). Die Zusicherungen auf `action: ADD` fallen weg;
an ihre Stelle gehört eine, die den Alias im Input-Objekt festnagelt.

**Ein Fall muss neu dazu**, sonst ist der Bug nicht abgedeckt: ein Alias mit Umlaut geht unverfälscht
über die Leitung.

### T3 — Löschpfad auf `removeEmote`

`seven-tv-delete.service.ts:27-44`. Reine Übersetzung der Mutation, keine Verhaltensänderung.

Tests: `seven-tv-delete.service.spec.ts` — `:47`, `:197-209`. Die Rate-Limit-Hilfsfunktion bei
`:28-44` bleibt gültig und **soll** unverändert bleiben; dass sie es tut, ist der Beleg für 0.3.

### T4 — Randstellen

- `web/e2e/support/mocks.ts:1049` routet `https://7tv.io/v3/gql` hart; Kommentar bei `:1029` ebenso.
- `web/e2e/emote-import.e2e.spec.ts:952` und `:1037` liefern `{ data: { emoteSet: { emotes: […] } } }`
  — die `v3`-Antwortform. Die `v4`-Antwort ist anders geschachtelt.
- `api-auth.interceptor.ts:25` und `api-auth.interceptor.spec.ts:64,66` nennen die `v3`-URL. Nur
  Kosmetik, aber sonst zeigt der einzige Ort, der die Ausnahme dokumentiert, ins Leere.

### T5 — Entscheidungseintrag

`docs/DECISIONS.md` bekommt im selben Commit wie T1 einen Eintrag (Regel 3): eine Endpunkt-Umstellung
ist eine Topologieänderung. Inhalt: warum `v3` den Alias nicht mehr schreiben kann, dass der
Rate-Limit-Vertrag identisch bleibt, und dass die Substring-Rateschleife durch `extensions.code`
ersetzt wurde.

### T6 — Live-Nachweis

Ein echter Kopierlauf über die UI, der mindestens eines der vier Emotes aus #147 enthält und es unter
seinem Umlaut-Alias im Zielset landen lässt. Ohne den ist die Sache nicht erledigt, egal wie grün die
Suiten sind (Regel 16). Zielset: das Wegwerf-Set des Zweitkontos.

---

## 3. Nicht Teil dieses Plans

Zwei Befunde sind beim Untersuchen aufgefallen und gehören **nicht** hier hinein:

1. **`addEmote` dedupliziert nicht über die Emote-ID.** Der Resolver prüft nur, ob der *Alias*
   kollidiert: bei Kollision `BAD_REQUEST` / `status 409` („this emote has a conflicting name"), sonst
   ein blindes `$push`. Ein Emote, das schon unter anderem Alias im Zielset liegt, wird dadurch
   **doppelt** eingetragen. Live beobachtet (Eintragszahl 77 → 79 bei einem einzigen zusätzlichen
   ADD). `v3` benutzt denselben Resolver-Zweig, das ist also Bestandsverhalten. Zu prüfen ist, ob der
   Import-Flow schon gegen den Zielset-Bestand filtert; falls nicht, eigenes Issue.
2. **Der Löschlauf bricht bei fehlenden Rechten nicht ab.** Nur der Import hat einen `abortOn`-Hook.
   Nach T1 wäre die Nachrüstung billig, aber sie ändert Verhalten und gehört in ein eigenes Ticket.

---

## 4. „Fertig" heißt

- `dotnet test EmotePurge.slnx` grün (braucht laufendes Docker). Backend ist nicht berührt, der Lauf
  ist die Regression-Absicherung.
- `npm --prefix web test -- --watch=false` grün.
- `npm --prefix web run e2e` grün — **nur wenn auf `:5151` keine Api lauscht**.
- `node scripts/coverage-local.mjs` vor dem PR.
- Der Live-Nachweis aus T6.
- `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` vor dem Merge.

Der Merge gehört dem Nutzer.
