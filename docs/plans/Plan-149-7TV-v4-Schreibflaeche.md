# Plan #149 — Umlaut-Aliase: die 7TV-Schreibfläche von v3 auf v4 umstellen

Erstellt am 2026-09-10 gegen `main` = `c49a881`. Quellen: Issue #149, der Abnahmelauf zu #147
(gemergt als `84b39d2`), 7TVs Quellcode (`SevenTV/SevenTV` @ `e1733255`), eigene Live-Proben gegen
`7tv.io` vom 2026-09-10 und die adversariale Zweitmeinung von Codex Sol vom selben Tag.

Der Plan enthält keinen Code. Er beschreibt Absicht, Verträge, Grenzfälle und Reihenfolge.

**Fassung 2.** Die erste Fassung ist an fünf Stellen nachgebessert worden — vier davon aus dem
Codex-Review, eine als Korrektur einer eigenen Fehlbehauptung (0.3). Was sich geändert hat, steht in
Abschnitt 6.

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

Live gemessen am 2026-09-10 gegen ein Wegwerf-Set:

| Probe | Ergebnis |
|---|---|
| `v3` ADD, Alias `Sitzgemüse` | `Failed to parse "String": invalid emote name` |
| `v4` `addEmote`, derselbe Alias | Erfolg, ein Aufruf |
| `Löwe`, `Gänsehosen`, `UnterschätzDasMalNicht` | alle drei akzeptiert |
| `ß`, `café`, `Привет`, `絵文字`, `party😀time` | alle akzeptiert |
| `a b` (Leerzeichen) | abgelehnt, `invalid emote alias` — anderer Validator, anderer Text |
| `v4` `removeEmote` | Erfolg; Set danach exakt im Ausgangszustand (77 Einträge, Vergleich per Diff) |

Die Sperre trifft die gesamte Nicht-ASCII-Klasse, nicht nur Umlaute. Das Zurücklesen über `v3` liefert
den Umlaut-Alias korrekt — `v3` kann lesen, nur nicht schreiben.

### 0.2 Warum „Alias weglassen" keine Alternative ist

Ohne `name` fällt 7TV auf den Basisnamen zurück. In HandOfBloods Set haben **alle 27** Aliase mit
Umlaut oder ß einen abweichenden, rein asciifizierten Basisnamen (`Gänsehosen`→`Gaensehosen`,
`wutköder`→`rageBait`, `SchrödingersChat`→`CrossEyesBob`). Weglassen setzte still den falschen Namen —
schlechter als der heutige gemeldete Fehlschlag.

### 0.3 Rate-Limit: die Form ist identisch, die Quote bleibt gelernt

`v4` benutzt dasselbe `ApiError`-Modul wie `v3` (`apps/api/src/http/error.rs`). Quellbelegt ist:
derselbe Guard an allen vier Set-Mutationen
(`PermissionGuard::one(EmoteSetPermission::Manage).and(RateLimitGuard::new(RateLimitResource::EmoteSetChange, 1))`),
derselbe Eimer `emote_set_change`, **ein** Ticket pro Aufruf, derselbe Serialisierer. Live bestätigt:
auf einer echten `v4`-Mutation erschien der vollständige Header-Satz `x-ratelimit-emote_set_change-*`,
und die abgelehnte Zeile (`invalid emote alias`) kostete **kein** Ticket.

**Was daraus ausdrücklich nicht folgt:** dass die Quote fest ist. Der Entscheidungseintrag vom
2026-08-01 hält fest, dass `RateLimits` pro Rolle in 7TVs MongoDB liegt, nutzerabhängig sein kann und
sich ohne Ankündigung ändert. Der von mir gemessene Wert (100/60 s) belegt eine Rolle zu einem
Zeitpunkt, sonst nichts. **Die Lernmechanik bleibt unangetastet, und der Kommentar bei
`seven-tv-run-engine.ts:25-28` bleibt stehen wie er ist** — die erste Planfassung wollte ihn
„berichtigen", das war falsch.

`isRateLimitError` (`:160-162`), `readRateLimitInfo` (`:164-172`) und `parseHeaderNumber` (`:151-158`)
bleiben damit inhaltlich unverändert. Dass sie es tun, ist kein Freibrief, sondern eine Behauptung,
die T1 mit einem Testfall gegen eine **entartete** Antwort absichern muss (s. dort).

### 0.4 Was beim Umzug bricht — die Abbruchlogik

`v4` meldet fehlende Schreibrechte als **HTTP 200** mit `extensions.code = "LACKING_PRIVILEGES"`,
`extensions.status = 403` und dem Text `"LACKING_PRIVILEGES you are not an editor for this user"`
(bzw. `"… you do not have permission to manage this user's emote sets"`).

`abortsForMissingPrivileges` (`seven-tv-import.service.ts:42-51`) prüft heute auf `httpStatus`
401/403 und auf die Substrings `insufficient privileges` / `missing permission`
(`PRIVILEGE_ERROR_FRAGMENTS`, `:36`). Nach dem Umzug greift **keine** der beiden Bedingungen:
`httpStatus` ist `null`, weil HTTP 200, und keiner der Substrings kommt vor. Ein Import ohne
Schreibrecht liefe dann durch alle Zeilen statt abzubrechen und verbrennte dabei das
Rate-Limit-Budget. Das ist der Risikopunkt des Umbaus.

Die Reparatur ist zugleich eine Verbesserung: `extensions.code` ist ein strukturiertes Feld. Die
geratene Substring-Liste — im Kommentar bei `:32` selbst als „as far as it is known from their
frontend" markiert — entfällt ersatzlos.

### 0.5 Die UI warnt vor genau dem, was danach funktioniert

`isNameRejectedBySevenTv` (`web/src/app/shared/seven-tv/import-preview.ts:78-80`) stuft **jeden**
Alias mit einem Codepoint `> 0x7f` als von 7TV abzulehnen ein; der Bestätigungsdialog zeigt die
betroffenen Zeilen unter „7TV wird diese ablehnen" (`import-confirm-dialog.ts:193-196`,
Schlüssel `import.confirm.*`). Ohne Anpassung gehen Umlaut-Aliase nach der Migration durch, werden dem
Nutzer aber weiter als sichere Fehlschläge angekündigt — #149 wäre nur halb repariert.

Der Doc-Kommentar darüber (`:64-77`) behauptet zudem das Gegenteil unserer Messung: „the alias is not
affected — 7TV does allow umlauts there". Auf `v3` ist das nachweislich falsch; die zwei dort
zitierten Live-Ablehnungen (`Hänno`, `HörMalZuBrudi`) waren genau Alias-Ablehnungen. Der Kommentar
wird berichtigt, nicht bloß der Code.

Derselbe Kommentar warnt richtig davor, eine Erlaubnisliste zu **raten** (#33, #37: Code und Test-Mock
teilten dieselbe falsche Annahme, die Tests blieben grün). Diese Warnung gilt weiter — deshalb T0.

### 0.6 Duplikate: durch die Migration erst erreichbar

7TVs `addEmote` dedupliziert **nicht** über die Emote-ID. Der Resolver prüft nur, ob der *Alias*
kollidiert: bei Kollision `BAD_REQUEST` / `status 409` („this emote has a conflicting name"), sonst ein
blindes `$push`. Ein Emote, das bereits unter **anderem** Alias im Zielset liegt, wird dadurch doppelt
eingetragen. Live beobachtet: Eintragszahl 77 → 79 bei einem einzigen zusätzlichen ADD.

Das ist nicht einfach Bestandsverhalten. Auf `v3` scheitert eine Zeile mit Umlaut-Alias **vor** dem
Push an der Validierung; auf `v4` erreicht sie ihn. Die Migration macht den Pfad für genau die Aliase
erreichbar, um die es in #149 geht.

Zwei Wege führen dorthin:

- **Import** filtert gegen den Zielset-Bestand (`import-preview.ts:34-47`, Schlüssel ist die
  `sevenTvEmoteId`), aber gegen einen **Schnappschuss**, den der Bestätigungsdialog vorher geholt hat.
  Zwischen Dialog und Lauf kann sich das Set ändern.
- **Restore** filtert **gar nicht**: `restore-flow.ts:82` und `mass-delete-panel.ts:382` gehen direkt
  von den Protokollzeilen in `startRestore` (`seven-tv-restore.service.ts:95`).

Ein Zurückdrehen des Endpunkts repariert entstandene Doppeleinträge nicht — die stehen dann bei 7TV.
Deshalb ist der Schutz Teil dieser Migration (T5), nicht Folge-Issue.

---

## 1. Vertragsänderungen

### V1 — Endpunkt

`SEVEN_TV_GQL_ENDPOINT` (`seven-tv-run-engine.ts:24`) zeigt auf `https://7tv.io/v4/gql`. Ein Endpunkt
für die ganze Schreibfläche; gemischter Betrieb ist ausdrücklich nicht das Ziel. Der Löschpfad zieht
mit — Begründung und Auflagen in Abschnitt 5.

### V2 — Die drei Mutationen

`v4` hat das `action`-Enum abgeschafft und je Operation ein eigenes Feld. Alle drei liegen unter
`emoteSets { emoteSet(id:) { … } }` und nehmen den Emote über das Input-Objekt
`EmoteSetEmoteId { emoteId: Id!, alias: String }`:

- **Hinzufügen** — `addEmote(id: { emoteId, alias })`. Der Alias reist **im Input-Objekt** mit, nicht
  als Geschwister-Argument. Das ist der ganze Fix: ein Aufruf, kein Zwischenschritt, kein Teilzustand.
- **Entfernen** — `removeEmote(id: { emoteId })`. Kein Alias.
- Variablentypen heißen `Id!` statt `ObjectID!`. Live bestätigt, dass unsere vorhandenen IDs
  unverändert auflösen — Set- wie Emote-ID.

Betroffen sind drei Stellen mit heute **wortgleicher** ADD-Mutation:
`seven-tv-import.service.ts:21-28`, `seven-tv-restore.service.ts:20-27`, und für REMOVE
`seven-tv-delete.service.ts:27-34`.

### V3 — Der Fehlercode wandert bis zur Abbruchentscheidung durch

Heute trägt ein Zeilenfehlschlag nur `{ message, httpStatus }` (`seven-tv-run-engine.ts:118-119`, Hook
bei `:559`). Er bekommt zusätzlich den Fehlercode aus `extensions.code` — `null`, wenn keiner da ist.

`abortsForMissingPrivileges` entscheidet danach auf `LACKING_PRIVILEGES`; die HTTP-Prüfung auf 401/403
bleibt, weil ein ungültiger Token weiterhin einen echten `HTTP 401` liefert — mit einem Body
**außerhalb** des GraphQL-Schemas (`{"status":"Unauthorized","error_code":1000,"error":"invalid
session"}`, live gemessen), aus dem sich nichts extrahieren lässt. `PRIVILEGE_ERROR_FRAGMENTS`
entfällt.

### V4 — Verhaltensparität, ausdrücklich

Außer dem Bugfix und dem Duplikatschutz ändert der Umbau **nichts** am beobachtbaren Verhalten.
Insbesondere bekommt der Löschlauf **keine** neue Abbruchbedingung — s. Abschnitt 4.

---

## 2. Tasks

Jeder Task ist ein eigener Subagent mit frischem Kontext. Die Reihenfolge ist bindend: T0 liefert die
Messwerte für T3, T1 legt den Vertrag fest, auf den T2–T4 sich stützen.

### T0 — Den erlaubten Alias-Zeichensatz messen, nicht raten

Live gegen das Wegwerf-Set, über `updateEmoteAlias` auf **einem** Eintrag (ein Ticket je Probe,
Budget 100/60 s, abgelehnte Proben kosten nichts). Zu klären ist, was `v4` außer Whitespace noch
ablehnt: führende/schließende Leerzeichen, Tab, Zero-Width-Zeichen, Doppelpunkt, Schrägstrich,
Anführungszeichen, Kombinationen mit Emoji, Länge > 100.

Ergebnis ist eine **Liste beobachteter Ablehnungen**, keine erratene Erlaubnisliste. Sie geht als
Tabelle in den Entscheidungseintrag (T7) und begründet T3.

Kein absichtlich provoziertes Rate-Limit. Nach zwei Fehlschlägen derselben Sonde: abbrechen, Methode
wechseln oder fragen.

### T1 — Engine: Endpunkt und Fehlercode-Durchreichung

`seven-tv-run-engine.ts`. Endpunkt auf `v4`. Der Fehlschlag-Datensatz und der `abortOn`-Hook tragen
zusätzlich `extensions.code`. Der Rate-Limit-Pfad bleibt **inhaltlich unverändert** — `isRateLimitError`
prüft bereits `code === 'RATE_LIMIT_EXCEEDED' || status === 429` und deckt `v4` damit ab; der
Kommentarblock bei `:25-28` bleibt stehen (s. 0.3).

Tests: `seven-tv-run-engine.spec.ts` — Endpunkt bei `:31`, Body-Zusicherung bei `:124-129`. Neu:
(a) ein GraphQL-Fehler mit `extensions.code` reicht den Code an `abortOn` durch; (b) eine **entartete**
Rate-Limit-Antwort — `code` gesetzt, `extensions.headers` fehlt — führt zum blinden 60-s-Rückzug statt
zu einem Zeilenfehlschlag. (b) ist die Absicherung der Behauptung aus 0.3.

### T2 — Import- und Restore-Pfad auf `addEmote`

`seven-tv-import.service.ts` und `seven-tv-restore.service.ts` auf die `v4`-ADD-Mutation. In
`seven-tv-import.service.ts` zusätzlich `abortsForMissingPrivileges` auf den Fehlercode umstellen,
`PRIVILEGE_ERROR_FRAGMENTS` entfernen.

Tests: `seven-tv-import.service.spec.ts` (`:26`, `:98-105`, Privilegienfall `:172-191` — der fütterte
bisher `insufficient privileges` als rohen Text und muss auf die `v4`-Form) und
`seven-tv-restore.service.spec.ts` (`:26`, `:82-89`). Die Zusicherungen auf `action: ADD` fallen weg;
an ihre Stelle gehört eine, die den Alias im Input-Objekt festnagelt.

**Ein Fall muss neu dazu**, sonst ist der Bug nicht abgedeckt: ein Alias mit Umlaut geht unverfälscht
über die Leitung.

### T3 — Die Vorschau hört auf, vor Umlauten zu warnen

`web/src/app/shared/seven-tv/import-preview.ts`. `isNameRejectedBySevenTv` (`:78-80`) prüft nicht mehr
auf Nicht-ASCII, sondern auf die in T0 **beobachteten** Ablehnungen. Der Doc-Kommentar (`:64-77`) wird
berichtigt: die Aussage „the alias is not affected" war falsch, die Warnung vor geratenen
Erlaubnislisten bleibt.

Mitzuziehen: `import-preview.spec.ts`, die Fälle des Bestätigungsdialogs zu `invalidNames`, und die
i18n-Texte in beiden Locales, falls sich die Aussage der Meldung ändert (Regel 7 betrifft nur unsere
eigenen `ApiErrorCodes`, hier geht es um freie UI-Texte — trotzdem beide Sprachen).

Testfälle: ein Umlaut-Alias wird **nicht** mehr gewarnt; ein in T0 beobachtet abgelehnter Alias
**wird** gewarnt.

### T4 — Löschpfad auf `removeEmote`

`seven-tv-delete.service.ts:27-44`. Reine Übersetzung der Mutation, keine Verhaltensänderung.

Tests: `seven-tv-delete.service.spec.ts` — `:47`, `:197-209`. Die Rate-Limit-Hilfsfunktion bei `:28-44`
bleibt gültig und **soll** unverändert bleiben.

### T5 — Duplikatschutz

Zwei Eingriffe, beide klein:

- **Restore filtert wie der Import.** Der Wiederherstellungs-Pfad gleicht die Protokollzeilen gegen
  den aktuellen Zielset-Bestand ab (Schlüssel `sevenTvEmoteId`) und überspringt, was schon drin ist.
  **Nachtrag nach dem Codex-Review:** der Abgleich fragt **7TV selbst**, nicht unsere Datenbank —
  gegen unsere Postgres gemessen hätte er nach einem fehlgeschlagenen `sync-deleted` genau die
  Zeilen weggefiltert, die der Nutzer wiederherstellen will.
  Der Nutzer erfährt die Zahl der übersprungenen Zeilen, wie beim Import.
- **Der Import zieht frisch.** Der Bestandsabgleich passiert unmittelbar vor dem Lauf, nicht nur gegen
  den Dialog-Schnappschuss.

Tests: ein zweiter Lauf über dieselben Zeilen reiht nichts ein; ein zwischen Dialog und Lauf
hinzugekommenes Emote wird nicht doppelt geschickt.

Bleibt ein Restrisiko: zwischen letztem Abgleich und dem einzelnen `addEmote` liegt weiter ein Fenster,
in dem ein anderer Editor schreiben kann. Das ist ohne atomare Operation aufseiten 7TVs nicht zu
schließen und gehört als solches in den Entscheidungseintrag.

### T6 — Randstellen

- `web/e2e/support/mocks.ts:1049` routet `https://7tv.io/v3/gql` hart; Kommentar bei `:1029` ebenso.
  **Der Handler-Typ bei `:1041-1044` (`errors?: { message: string }[]`) kann `extensions` nicht
  ausdrücken und muss erweitert werden** — sonst lässt sich kein `v4`-Fehler nachstellen.
- `web/e2e/emote-import.e2e.spec.ts`: die Erfolgs-Fixtures bei `:952` und `:1037` liefern die
  `v3`-Antwortform. **Und der Privilegienfall bei `:1145-1150` sendet `insufficient privileges` als
  reine Textmeldung** — nach T2 löst das keinen Abbruch mehr aus. Das ist der einzige Nachweis auf
  Browser-Ebene, dass ein Rechtefehler nach einem Request stoppt; er muss auf die `v4`-Form.
- `api-auth.interceptor.ts:25` und `api-auth.interceptor.spec.ts:64,66` nennen die `v3`-URL. Nur
  Kosmetik — der Interceptor filtert über `req.url.startsWith('/api/')` (`:35`) und ist funktional
  nicht betroffen —, aber sonst zeigt der einzige Ort, der die Ausnahme dokumentiert, ins Leere.
- `docs/Architectur.md:187-191` dokumentiert die Löschmutation samt `v3`-URL, und `:100` legt
  „7TV v3 …, nicht v4" fest. Beides mitziehen; `:100` bleibt für REST und EventAPI richtig und wird um
  die Schreibfläche eingeschränkt.

### T7 — Entscheidungseintrag

`docs/DECISIONS.md` bekommt im selben Commit wie T1 einen Eintrag (Regel 3): eine Endpunkt-Umstellung
ist eine Topologieänderung. Inhalt: warum `v3` den Alias nicht mehr schreiben kann, dass der
Rate-Limit-**Vertrag** identisch bleibt und die Quote weiterhin gelernt wird, dass die
Substring-Rateschleife durch `extensions.code` ersetzt wurde, die T0-Tabelle, und das Restrisiko aus
T5.

### T8 — Live-Nachweis

Nicht ein Lauf, sondern fünf Nachweise gegen echte 7TV-Zugänge (Regel 16). Zielset ist das
Wegwerf-Set des Zweitkontos:

1. **Import** mit mindestens einem der vier Emotes aus #147, das unter seinem Umlaut-Alias landet.
   Das ist das Abnahmekriterium des Issues.
2. **Restore** eines Purge-Protokolls, ebenfalls mit Umlaut-Alias.
3. **Delete** eines Laufs — die Auflage aus dem Codex-Review für den mitmigrierten Pfad.
4. **Rechtefehler**: ein Lauf gegen ein Set ohne Schreibrecht bricht nach **einer** Zeile ab.
5. **Duplikatschutz**: ein zweiter Restore-Lauf über dieselben Zeilen reiht nichts ein.

Nachweis 4 braucht ein Set, das dem Zweitkonto nicht gehört. Vor der Ausführung mit dem Nutzer klären,
welches — ein fehlschlagender Schreibversuch gegen ein fremdes Set wird nicht auf Verdacht gefahren.

---

## 3. Nicht Teil dieses Plans

**Der Löschlauf bricht bei fehlenden Rechten nicht ab.** Nur der Import hat einen `abortOn`-Hook. Nach
T1 wäre die Nachrüstung billig, aber sie ändert Verhalten und gehört in ein eigenes Ticket. Anders als
der Duplikatschutz wird sie durch die Migration **nicht** gefährlicher: der Fehler war vorher wie
nachher ein Zeilenfehlschlag.

---

## 4. Rückweg

Der Endpunkt ist eine Konstante — das Zurückdrehen ist ein Einzeiler, nimmt aber den Fix mit, weil
`v3` den Alias nicht schreiben kann. Ein Teil-Rückweg (nur DELETE zurück auf `v3`) ist möglich, sobald
`buildRequest` den Endpunkt je Operation trägt; das ist der Umbau, den Codex vorgeschlagen hat und den
wir bewusst nicht machen — er wäre die Antwort auf einen DELETE-Ausfall, nicht dessen Verhinderung.

Auslöser für den Rückweg: Nachweis 3 oder 5 aus T8 scheitert, oder nach dem Merge häufen sich
Zeilenfehlschläge mit einem `extensions.code`, den die Engine nicht kennt.

**Was der Rückweg nicht repariert:** bereits entstandene Doppeleinträge im Zielset (0.6). Deshalb
steht T5 vor dem Merge, nicht danach.

---

## 5. Warum der Löschpfad trotzdem mitzieht

Codex Sol hat widersprochen: DELETE funktioniert, die Migration riskiert ihn ohne Not, und ein
Rollback nähme den Fix mit. Der Einwand ist erfasst und wurde vom Nutzer entschieden — mit zwei
Gründen und einer Auflage.

Gründe: `v3` ist bei 7TV erkennbar Altbestand (die Alias-Reparatur wurde dort nie nachgezogen), ein
Umzug steht also ohnehin an; und zwei Endpunkte mit zwei Fehlerverträgen in einer Engine kosten mehr
Code als einer, nicht weniger. Dazu: `removeEmote` ist auf `v4` bereits live gefahren worden (0.1) —
die Behauptung, der Pfad sei unerprobt, trifft nicht mehr zu.

Auflage: die erweiterte Nachweisliste in T8, insbesondere Nachweis 3 und 4.

---

## 6. Was sich gegenüber Fassung 1 geändert hat

1. **0.3 korrigiert eine eigene Fehlbehauptung.** Fassung 1 wollte den Quoten-Kommentar in der Engine
   „berichtigen", weil ich 100/60 s live gemessen hatte. Der Entscheidungseintrag vom 2026-08-01 hält
   fest, dass die Quote rollenabhängig und veränderlich ist — die Messung belegt eine Rolle, nicht die
   Quote. Kommentar bleibt, Lernmechanik bleibt.
2. **T3 ist neu** (Codex, mittel): die Vorschau warnt sonst weiter vor genau den Aliasen, die nach der
   Migration funktionieren.
3. **T0 ist neu**: der Zeichensatz für T3 wird gemessen, nicht geraten — die Datei warnt selbst davor,
   mit #33/#37 als Belegen.
4. **T5 ist neu** (Codex, hoch): der Duplikat-Push wird durch die Migration für Umlaut-Aliase erst
   erreichbar, und ein Rückweg repariert entstandene Doppeleinträge nicht.
5. **T6 und T8 sind erweitert** (Codex, mittel/hoch): der E2E-Privilegienfall bei `:1145-1150` und der
   Mock-Handler-Typ waren übersehen; `Architectur.md` ebenso. T8 verlangt jetzt fünf Nachweise statt
   einem.

Nicht übernommen wurde Codex' Kernempfehlung, den Löschpfad auf `v3` zu lassen — begründet in
Abschnitt 5.

---

## 7. „Fertig" heißt

- `dotnet test EmotePurge.slnx` grün (braucht laufendes Docker). Backend ist nicht berührt; der Lauf
  ist Regressionsabsicherung.
- `npm --prefix web test -- --watch=false` grün. Vergleichspunkt: 958 Tests in 98 Dateien, grün auf
  `c49a881`.
- `npm --prefix web run e2e` grün — **nur wenn auf `:5151` keine Api lauscht**.
- `node scripts/coverage-local.mjs` vor dem PR.
- Die fünf Nachweise aus T8.
- `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` vor dem Merge.

Der Merge gehört dem Nutzer.
