# Plan #91 — Datei-Weg in den Seitenkopf: `RestorePanel` in Trigger und Einspiel-Dialog zerlegen

Erstellt am 2026-09-07 gegen `main` = `7eadb5e`, unter Berücksichtigung von `feat/export-verb-92`
(`817cc93`, PR #108, noch nicht gemergt). Quellen: Issue #91, `docs/designs/Aktionszeile-80-2026-09-06.md`,
`docs/UI-Designsprache.md` (§2.5, §4.2, §5, §7, §7.2, §8.7, §10, §12), `docs/DECISIONS.md` (Eintrag
2026-09-07 zu §8.7) und der Code. Alle Zeilenangaben sind auf `main` verifiziert, wenn nicht anders
vermerkt.

Der Plan enthält keinen Code. Er beschreibt Absicht, Verträge, Grenzfälle und Reihenfolge; was der
Implementer tippt, entscheidet er selbst.

---

## 0. Ausgangslage — was am Code stimmt, was überholt ist

### 0.1 Was heute wo sitzt

- `web/src/app/shared/seven-tv/restore-panel.ts` ist **eine** Komponente mit vier Anteilen: Knopf
  (`:46-54`), verstecktes `<input type="file">` (`:56-63`, `class="hidden"`, `accept` JSON),
  Fehlerbanner (`:65-67`), Dispatch nach `kind` (`:96-138`), Token-Prompt-vor-Bestätigung für das
  Purge-Protokoll (`:140-159`) und Restore-Confirm samt Slot-Vorschau (`:161-188`). Der
  programmatische `click()` auf das File-Input steht bei `:92-94`, gerufen aus dem Klick-Handler
  `:51` — heute eine echte Nutzergeste.
- Der Aufrufer: `usage-stats-page.html:707-716` — Set-Gate `@if (activeEmoteSetId(); as setId)`
  bei `:709`, Kommentar „Stays in the flow rather than in the dock" bei `:710-712`,
  `@if (!isCoarse())` bei `:713`, `<app-restore-panel>` bei `:714`. Import in
  `usage-stats-page.ts:121`, Komponenten-Import `:209`.
- Der Seitenkopf: `usage-stats-page.html:6-56`. Drei Knöpfe: Exportieren (`:11-20`, gesperrt bei
  leerem Raster), Übertragen (`:34-44`, innerhalb `@if (!isCoarse() && activeEmoteSetId())` bei
  `:33`, gesperrt bei leerem Raster / `arbiter.activeRun()` / `!importScopeCurrent()`),
  Aktualisieren (`:46-54`). Der erklärende Kommentar zum Übertragen-Knopf steht bei `:21-32`.
- Die Lesefehler-Schlüssel (`restore.import.errors.*`, `de.json:827-837`, `en.json` analog) entstehen
  an diesen Stellen — **die Parser liegen in `shared/export/`, nicht in `shared/seven-tv/`**:
  - `notJson` — `shared/export/read-envelope.ts:22`
  - `csvInsteadOfJson` — `read-envelope.ts:21`
  - `wrongKind` — `read-envelope.ts:28` und `:33`; außerdem `shared/export/import-source-parser.ts:38`
    und `:44` sowie `shared/export/purge-run-export.ts:135`, `:145`, `:151`
  - `wrongChannel` — `purge-run-export.ts:141`
  - `wrongSet` — `purge-run-export.ts:148`
  - `votingExport` — `purge-run-export.ts:103` (Tabelle) und `import-source-parser.ts:32`
  - **zusätzlich, im Ticket nicht genannt, aber ebenfalls Lesefehler dieses Weges:** `wrongVersion`
    (`import-source-parser.ts:41`, `purge-run-export.ts:138`), `noRows`
    (`import-source-parser.ts:68`), `noRestorableRows` (`purge-run-export.ts:164`). Der Dialog trägt
    alle **neun**, nicht sechs — die drei fehlenden sind heute genauso Banner im Panel.
- Die beiden Prompt-Reihenfolgen: Purge-Protokoll → Token-Prompt **vor** Bestätigung
  (`restore-panel.ts:150-158`); Emote-Liste/Nutzungs-Export → `startImportFlow`
  (`shared/seven-tv/import-flow.ts:41-102`), Token-Prompt **nach** Bestätigung (`:87-101`, Kommentar
  `:32-36`). §7.2 hält das als Absicht fest („Dieser Unterschied ist Absicht, kein Nachzügler").
- **Der Dialog-Vertrag der App:** `shared/ui/dialog.ts:5-9` — „the CDK allows exactly one dialog
  open at a time in this app"; darauf beruht die eine feste `DIALOG_TITLE_ID`. Dialoge werden in
  der App nie gestapelt, sondern **nacheinander** geöffnet (Token-Prompt schließt → Confirm öffnet;
  Confirm schließt → Token-Prompt öffnet). Das entscheidet Falle 2 (s. 1.2).
- Fokus: `shared/ui/dialog-shell.ts:72-73` — der CDK-Default `first-tabbable` landet auf dem ersten
  fokussierbaren Element; der Body (`:70`) steht **vor** der Aktionszeile (`:74-76`).
- Tests, die den alten Ort greifen: `restore-panel.spec.ts` (20 Fälle, drivt `onFileSelected`
  direkt, `:205-214`), `web/e2e/emote-import.e2e.spec.ts:335-470` („push flow: the file path") und
  `:472-550` („push flow: rejection") — beide über `page.locator('input[type="file"]')` (`:360`,
  `:486`) **ohne** vorherigen Klick, und beide erwarten nach einem Fehler
  `getByRole('dialog')).toHaveCount(0)` (`:510`, `:549`); Audit-Szenario
  `web/e2e/audit/ui-audit.audit.ts:671-716` (`usage-stats-restore-import-error`, ebenfalls direktes
  `setInputFiles`). Die Szenarien `:797-811` und `:829-843` greifen den Übertragen-Knopf über
  `main header button` **`.nth(1)`** — die Position im Seitenkopf ist damit Vertrag.

### 0.2 Was Ticket und Entwurf sagen und der Code oder das Log widerlegt

| Behauptung | Befund |
|---|---|
| Ticket: „`DECISIONS.md:2469` ist zu präzisieren" | **Überholt.** Die Präzisierung steht bereits auf `main` im Eintrag vom 2026-09-07, `docs/DECISIONS.md:107-115` („Der Seitenkopf **ist** Fluss … der Umzug selbst ist ein eigenes Issue (#91)"). Der alte Satz selbst steht auf `main` bei `DECISIONS.md:2894` (auf `feat/export-verb-92` bei `:2974`), nicht bei `:2469` — die Zeilennummer im Ticket ist veraltet. #91 schreibt einen **eigenen** Eintrag über den Umzug und wiederholt die Präzisierung nicht. |
| Entwurf, Erfolgskriterien: „Erste unabhängige Probe: Mod-Discord-Vorstellung (#68)" | **Verworfen** am 2026-09-07, `DECISIONS.md:60-70` („eine ausbleibende Frage ist kein Urteil"). Der Plan führt #68 nicht als Bedingung. #91 wird wie die Dock-Kurzform behandelt: gebaut, nach eigenem Urteil gemergt, DECISIONS-Eintrag „auf Widerruf". |
| Ticket: Lesefehler-Liste mit sechs Schlüsseln | **Unvollständig.** Neun Schlüssel existieren (`de.json:828-836`); `wrongVersion`, `noRows`, `noRestorableRows` fehlen im Ticket (Fundorte oben). |
| Aufgabenstellung: Parser unter `shared/seven-tv/` | **Falscher Pfad.** `import-source-parser.ts` und `purge-run-export.ts` liegen in `web/src/app/shared/export/` (dort auch `read-envelope.ts`). Die Zeilennummern aus dem Ticket (`:32`, `:103`, `:141`, `:148`) stimmen. |
| Aufgabenstellung: „die `usage-stats-restore-*`-Szenarien" im Audit-Harness | Es gibt genau **ein** solches Szenario: `usage-stats-restore-import-error` (`ui-audit.audit.ts:674`). |
| Entwurf: Seitenkopf-Knopf heißt „In Kanal kopieren…" | **Überholt durch #92** (`817cc93`): `import.copyButton` = „Übertragen…" / „Transfer…", Dialogtitel „Emotes übertragen". Der Plan setzt auf diesem Stand auf. Die Audit-Kommentare `:801-802` sind dort bereits nachgezogen. |
| Entwurf: Komponentenname `RestoreTrigger` | Arbeitstitel. Zwei der drei Dateisorten sind **kein** Restore, sondern ein Import (`restore-panel.ts:28-32`). Der Plan benennt die neuen Teile nach dem Weg, nicht nach einem der drei Ziele (s. 1.1). Der i18n-Namensraum `restore.import.*` bleibt — Schlüssel umzubenennen kauft nichts. |
| `restore-panel.ts:185` liest `this.setId()` zur Bestätigungszeit, `:143` zur Parse-Zeit | Kein Bug-Befund, aber ein Unterschied: zwischen Parse und Bestätigung kann ein Kanalwechsel in derselben Route die Set-Id austauschen. Der neue Schnitt übernimmt die Capture-Disziplin von `openImportTarget()` (`usage-stats-page.ts:1177-1199`): Kanal und Set werden **beim Klick** eingefroren und bis zum `startRestore` durchgereicht. |

Nicht verifiziert: die Vergleichszahlen des Audit-Harness (auf `main` 20 `contrastViolations` und
8 × 20 px Overflow, auf dem #92-Branch 22 px) stammen aus der Aufgabenstellung, nicht aus einem
eigenen Lauf. Task 8 misst sie selbst.

---

## 1. Der Schnitt — Verträge, keine Implementierung

### 1.1 Drei Teile statt einem

**`FileImportDialog`** (`web/src/app/shared/seven-tv/file-import-dialog.ts`, Selector
`app-file-import-dialog`, Öffner `openFileImportDialog` über `openAppDialog`, §7): der
Einspiel-Dialog. Er **liest** und **prüft** die Datei und meldet Lesefehler; er startet **keinen**
Lauf und öffnet **keinen** weiteren Dialog.

- Eingabe (`DIALOG_DATA`): der beim Klick eingefrorene Kanalname und die eingefrorene aktive Set-Id.
  Beides braucht `parsePurgeRunProtocol` (`purge-run-export.ts:118-121`).
- Rückgabe (`DialogRef.close`): ein diskriminiertes Ergebnis mit genau zwei Formen — „Restore" mit
  den restaurierbaren `PurgeRunRow`s (aus `parsePurgeRunProtocol`) oder „Import" mit der
  `ImportSource` (aus `parseImportSource`); `undefined` bei Abbrechen/Escape/Backdrop.
- Zeilenreihenfolge im Body (wird in §7.3 als Vertrag festgehalten, s. Task 7, und darf deshalb
  nach Regel 12 getestet werden): (1) Überschrift, (2) die **Liste der drei zulässigen Dateisorten**
  — Purge-Protokoll (Wiederherstellen), Emote-Liste (Kopieren), Nutzungs-Export (Kopieren), je als
  eigener Listeneintrag mit dem Hinweis „als JSON" —, (3) das **Datei-Bedienelement**, (4) das
  Fehlerbanner (`NoticeBanner` `error`, nur bei Fehler), (5) Aktionszeile mit **nur** Abbrechen. Es
  gibt keinen „Weiter"-Knopf: die Dateiauswahl selbst ist der Vollzug.
- Das Datei-Bedienelement sitzt **im** Dialog (Falle 1). Ob es ein sichtbar gestyltes natives
  `<input type="file">` ist oder ein sichtbarer Knopf plus verstecktes Input, entscheidet der
  Implementer nach §5.1/§5.2 — beides ist ein Klick **innerhalb** des offenen Dialogs, also eine
  frische Nutzergeste; Enter/Space auf einem Knopf zählt als Aktivierung und darf ein verstecktes
  Input öffnen. Verboten ist allein, den Dateidialog nach `closed` eines CDK-Dialogs zu öffnen.
  Das Input behält `accept` auf JSON und den heutigen Reset des Werts nach jeder Auswahl
  (`restore-panel.ts:99-100`), sonst feuert die Korrektur derselben Datei kein `change`.
- Das Datei-Bedienelement ist das **erste fokussierbare Element** im Dialog. Damit landet der
  CDK-Default `first-tabbable` von selbst darauf (`dialog-shell.ts:72-73`: der Body kommt vor der
  Aktionszeile) — kein `cdkFocusInitial`. Das ist die Antwort auf offene Frage 6 des Entwurfs und
  bleibt mit §7 vereinbar („Abbrechen steht zuerst" gilt für die Aktionszeile). Ein verstecktes
  Input ist nicht fokussierbar; dann muss der sichtbare Knopf das erste fokussierbare Element sein.
- Bei **Fehler bleibt der Dialog offen** und zeigt das Banner; die Liste der Sorten steht darüber
  und ist die Erklärung, was stattdessen passt. Jeder neue Versuch setzt das Banner zurück
  (heutiger Vertrag, `restore-panel.spec.ts:433-443`).
- Bei **Erfolg schließt der Dialog** mit dem Ergebnis. Alles Weitere passiert außerhalb.
- Der Dialog hat eine sichtbare Überschrift (`dialogTitle`), das Datei-Bedienelement ein Label
  (§5.2; heutiges `restore.import.fileLabel` reicht als `aria-label`, ein sichtbares Label ist
  besser).

**`startRestoreFlow`** (`web/src/app/shared/seven-tv/restore-flow.ts`): die Restore-Kette als
Funktion, Spiegelbild von `startImportFlow` in `import-flow.ts`. Nimmt dieselbe Art
Abhängigkeiten-Objekt entgegen (Dialog, `EmoteAdminService`, `SevenTvTokenService`,
`SevenTvRestoreService`, `SevenTvRunArbiter`), dazu die eingefrorenen Werte (Kanalname, Set-Id)
und die Zeilen. Reihenfolge **unverändert**: fehlt der Token → Token-Prompt, bei `true` weiter,
sonst Ende; dann Restore-Confirm mit lebender Slot-Vorschau (heute `restore-panel.ts:161-177`);
bei Bestätigung `startRestore` mit den **eingefrorenen** Werten. Vor dem Start dieselbe stille
Arbiter-Sperre wie `import-flow.ts:63-69` (heute fehlt sie im Restore-Zweig; ein Lauf, der
zwischen Dateiauswahl und Bestätigung anderswo gestartet ist, darf nicht doppelt laufen). Die
Begründung, warum das eine Funktion und kein Service ist, steht in `import-flow.ts:13-20` und gilt
hier wörtlich.

**`FileImportTrigger`** (`web/src/app/shared/seven-tv/file-import-trigger.ts`, Selector
`app-file-import-trigger`): der Knopf im Seitenkopf. Inputs wie heute (`channelName`, `setId`,
beide required). Beim Klick friert er beide Werte ein, öffnet `openFileImportDialog` und
verteilt das Ergebnis: „Restore" → `startRestoreFlow`, „Import" → `startImportFlow` mit dem
eingefrorenen Kanalnamen als Ziel (heute `restore-panel.ts:127-137`, Kommentar `:125-126`: **kein
eigener Token-Prompt** davor). Gesperrt, solange `arbiter.activeRun()` nicht `null` ist — ohne
Hinweistext (§4.2). Er injiziert seine Dienste selbst, wie heute `RestorePanel`; die Seite
bekommt **keine** neue Methode (Coverage, s. 2.4).

`RestorePanel` (`restore-panel.ts` + `restore-panel.spec.ts`) wird gelöscht, sobald alle Fälle
migriert sind (Task 4). Kein Alias, kein Re-Export.

### 1.2 Die drei Fallen, entschieden

1. **Verbrauchte Geste.** Das File-Input sitzt im Dialog (1.1). Der Trigger öffnet nur einen
   CDK-Dialog, nie den Dateidialog. Der Implementer darf `restore-panel.ts:92-94` nicht in den
   Trigger übernehmen.
2. **Zwei Prompt-Reihenfolgen aus einem offenen Dialog.** Sie laufen **nicht** aus dem offenen
   Dialog heraus. Der Einspiel-Dialog schließt mit dem geprüften Ergebnis, danach startet der
   Trigger die passende Kette — beide Ketten öffnen ihre Dialoge nacheinander, wie heute. Damit
   bleibt der Ein-Dialog-Vertrag aus `dialog.ts:5-9` gewahrt und die Reihenfolge je Kette
   unverändert (Restore: Token → Confirm; Import: Confirm → Token). Verschachtelte Dialoge, die
   der Entwurf als Aufwandstreiber nennt, entstehen so gar nicht erst — der Aufwand verlagert sich
   in den sauberen Rückgabevertrag des Dialogs. Der Entwurf nennt das M; mit diesem Schnitt ist es
   eher S/M.
3. **Gates erben sich nicht.** Der Trigger steht im Seitenkopf **innerhalb** des bestehenden
   `@if (!isCoarse() && activeEmoteSetId())` (`usage-stats-page.html:33`), das heute nur den
   Übertragen-Knopf umschließt — ein Block, zwei Knöpfe. Beide Bedingungen sind Pflicht: kein
   7TV-Schreibweg ohne Maus (§2.5), kein Datei-Weg ohne eigenes aktives Set (Entwurf,
   Randbedingung „Aktives Set" — bewusst anders als die Import-Sektion im Dock). Zusätzlich sperrt
   der Trigger auf `!importScopeCurrent()` genau wie der Übertragen-Knopf (`:39`): er friert beim
   Klick `channelName()` **und** `activeEmoteSetId()` ein, und das ist exakt das Fenster, das
   `importScopeIsCurrent` schließt (Kommentar `:27-32`). **Nicht** übernommen wird
   `atlasOrder().length === 0` — der Datei-Weg braucht keine Zeilen im Raster. Damit die
   `importScopeCurrent`-Sperre nicht als Seiten-Logik in den Trigger wandert, nimmt der Trigger
   sie als Input entgegen (ein Boolean „Scope aktuell", Default wahr) und rechnet seine
   Gesamtsperre daraus plus dem Arbiter — in einer reinen Funktion nach dem Muster
   `import-shortcut.ts`/`import-shortcut.spec.ts`, damit die Sperrentscheidung ohne TestBed
   testbar ist.

### 1.3 Reihenfolge im Seitenkopf

Exportieren · Übertragen… · **Datei einspielen…** · Aktualisieren — die Reihenfolge aus der Tabelle
des Entwurfs („Gewählter Ansatz"). Der neue Knopf kommt **nach** Übertragen, damit
`main header button.nth(1)` in `ui-audit.audit.ts:810` und `:841` weiter Übertragen trifft; der
neue Knopf ist `.nth(2)`. Der Kommentar `usage-stats-page.html:21-32` wird so ergänzt, dass er den
gemeinsamen `@if`-Block und die beiden Knöpfe erklärt (welche Sperren geteilt, welche nicht).
Variante `appButton="neutral"` wie die Nachbarn; §8.7 sagt nichts zur Optik, §4.2 verlangt hier
nichts Destruktives.

### 1.4 i18n (Regel 7 — beide Locale-Dateien, nur Transloco, keine `ApiErrorCodes`)

- `restore.import.trigger`: neuer Wortlaut „Datei einspielen…" / „Import file…" (mit Ellipse, weil
  ein Dialog folgt — wie „Übertragen…").
- `restore.import.hint` (`de.json:825`): entfällt; ersetzt durch den Dialog-Titel und **drei**
  Listeneinträge (je Sorte ein Schlüssel, damit die Liste eine Liste ist und kein Satz mit Kommas,
  der bei 360 px umbricht — §12 „längere deutsche Strings sind der häufigste Umbruch-Bruch").
- `restore.import.fileLabel` (`de.json:826`): bleibt, ggf. als sichtbares Label.
- `restore.import.errors.*`: **unverändert**, alle neun.
- Neue Schlüssel in `de.json` **und** `en.json` im selben Commit; keine ungenutzten Schlüssel
  hinterlassen.

### 1.5 Grenzfälle, die der Implementer abdecken muss

- Nativer Dateidialog abgebrochen → kein `change` → Dialog bleibt offen, kein Banner.
- Dieselbe Datei nach einem Fehler erneut gewählt → `change` feuert (Wert-Reset), Banner wird
  ersetzt oder gelöscht.
- Escape/Backdrop im Einspiel-Dialog → `undefined` → der Trigger tut nichts.
- Token-Prompt oder Confirm abgebrochen → kein Lauf; der Einspiel-Dialog ist bereits zu, der
  Nutzer wählt bei Bedarf neu. (Heute identisch: die Datei ist verbraucht, das Panel steht leer.)
- Kanalwechsel in derselben Route, während ein Dialog der Kette offen ist → alle Ketten arbeiten
  mit den beim Klick eingefrorenen Werten, nie mit den lebenden Signalen.
- Arbiter wird zwischen Dateiauswahl und Bestätigung belegt → stiller Abbruch vor `startRestore`
  (neu) bzw. vor `startImport` (bestehend, `import-flow.ts:67`).
- Zeigerart wechselt auf `coarse`, während der Dialog offen ist → DialogShell wird Sheet
  (`dialog-shell.ts:22-26`); der Trigger verschwindet mit dem Gate. Kein Sonderfall nötig.
- Voting-Export → `votingExport`-Banner im Dialog; Purge-Protokoll fremder Kanal/fremdes Set →
  `wrongChannel`/`wrongSet` im Dialog; CSV statt JSON → `csvInsteadOfJson`; kein EmotePurge-Export →
  `wrongKind`; Emote-Liste ohne gültige Zeilen → `noRows`; Protokoll ohne `done`-Zeilen →
  `noRestorableRows`; fremde Formatversion → `wrongVersion`.

---

## 2. Tasks

Branch `feat/datei-weg-91`, aufgesetzt auf `origin/feat/export-verb-92` (`817cc93`), weil der Plan
den umbenannten Seitenkopf voraussetzt; nach dem Merge von #108 auf `origin/main` rebasen. Jeder
Task läuft als eigener Subagent (`sonnet` für 1–6, 8; `opus` für den Review-Checkpoint in 9).
Commits: Conventional Commits, mehrere logische Commits (Regel 2), **vor jedem Commit fragen**
(Regel 1). Regel 19 (Member-Reihenfolge, per ESLint `member-ordering` teilweise erzwungen) gilt
für alle neuen Klassen.

### Task 1 — Restore-Kette als Funktion herauslösen (`restore-flow.ts` + Spec)

**Ziel:** Die Token→Confirm→`startRestore`-Kette aus `restore-panel.ts:140-188` wird eine reine
Funktion neben `import-flow.ts`, mit dem Vertrag aus 1.1 (eingefrorene Werte, Arbiter-Sperre vor
dem Start). `RestorePanel` ruft sie in diesem Task **noch** selbst auf — verhaltensneutraler
Refactor, alle bestehenden Specs bleiben grün.

**Spec (`restore-flow.spec.ts`, Vorlage `import-flow.spec.ts` — ohne TestBed, `dialog.open` als
`vi.fn()`, unterschieden nach Aufrufreihenfolge und Nebenwirkungen):** Token fehlt → erst ein
Dialog, `getSetStatus` noch nicht gerufen, `startRestore` nicht gerufen; Token-Prompt bestätigt →
zweiter Dialog öffnet, `getSetStatus` mit dem eingefrorenen Kanal; Confirm bestätigt →
`startRestore` mit eingefrorener Set-Id, eingefrorenem Kanal und den übergebenen Zeilen; Token
vorhanden → direkt Confirm; Token-Prompt abgebrochen → kein Confirm, kein Lauf; Confirm abgebrochen
→ kein Lauf; Arbiter belegt bei Bestätigung → kein Lauf; Slot-Vorschau: `capacity === null` → keine
Projektion, Fehler von `getSetStatus` → keine Projektion. **Nicht** getestet: Dialog-Interna,
Wortlaut.

**Fertig:** `npm --prefix web test -- --watch=false` grün inklusive unveränderter
`restore-panel.spec.ts`; `lint` und `format:check` grün. Commit `refactor(seven-tv): …`.

### Task 2 — Einspiel-Dialog (`file-import-dialog.ts` + Spec + i18n)

**Ziel:** Der Dialog aus 1.1: Liste der Sorten **vor** dem Datei-Bedienelement, File-Input im
Dialog, alle neun Lesefehler als Banner im Dialog, Schließen mit dem Ergebnisvertrag, erstes
fokussierbares Element = Datei-Bedienelement. Öffner `openFileImportDialog` im selben Commit (§7).
Neue i18n-Schlüssel nach 1.4 in beiden Locales. Noch kein Aufrufer außer der Spec — der Dialog
darf in diesem Task tot im Baum stehen, wenn Task 3 im selben PR folgt.

**Spec (`file-import-dialog.spec.ts`; Muster für `DIALOG_DATA`/`DialogRef`-Provider:
`import-target-dialog.spec.ts:118-127`; Dateiauswahl wie `restore-panel.spec.ts:205-214` direkt am
Handler, weil `file.text()` asynchron ist und zoneless nichts signalisiert):** je Dateisorte das
Ergebnis, mit dem `close` gerufen wird (Purge-Protokoll → „Restore" mit nur `done`-Zeilen;
Emote-Liste und Nutzungs-Export → „Import", Ziel bleibt Sache des Aufrufers); je Fehlerschlüssel
ein Fall, in dem `close` **nicht** gerufen wird und das Banner den Schlüssel zeigt (die
Übersetzung identifiziert die Meldung, sie ist nicht Prüfgegenstand — Regel 12); Banner-Reset beim
nächsten Versuch; Abbrechen ruft `close` ohne Wert; Accessibility: der Dialog hat einen
zugänglichen Namen, das Datei-Bedienelement einen, und das Banner die Rolle `alert`. **Nicht**
getestet: Klassen, Layout, Sortenliste als Wortlaut, Fokus (das ist CDK-Verhalten und wird in Task
5 per E2E geprüft).

Die Fälle aus `restore-panel.spec.ts:355-444` (Abweisungen, Validierung, Reset) wandern hierher;
die Kettenfälle `:224-353` sind seit Task 1 in `restore-flow.spec.ts` bzw. gehören zu Task 3.

**Fertig:** Vitest grün, `lint`, `format:check` grün; beide Locale-Dateien tragen dieselben neuen
Schlüssel; `restore.import.hint` in keiner Datei mehr referenziert. Commit `feat(seven-tv): …`.

### Task 3 — Trigger (`file-import-trigger.ts` + Sperrfunktion + Spec)

**Ziel:** Der Knopf aus 1.1 mit der Sperrentscheidung aus 1.2 (3) als reine Funktion (Muster
`import-shortcut.ts`), Verteilung des Dialog-Ergebnisses auf `startRestoreFlow` bzw.
`startImportFlow` mit eingefrorenen Werten, kein eigener Token-Prompt vor dem Import.

**Spec:** Sperrfunktion ohne TestBed (Arbiter belegt → gesperrt; Scope nicht aktuell → gesperrt;
beides frei → offen). Komponente mit TestBed und `Dialog.open` als `vi.fn()`: Klick öffnet genau
einen Dialog mit den eingefrorenen Daten; Ergebnis „Restore" → nächster Dialog ist der
Token-Prompt bzw. bei vorhandenem Token der Confirm (Nachweis über `getSetStatus`, wie
`restore-panel.spec.ts:224-263`); Ergebnis „Import" → nächster Dialog ist der Import-Confirm, und
erst nach dessen Bestätigung der Token-Prompt (`restore-panel.spec.ts:286-317` als Vorlage — die
Reihenfolgen-Asymmetrie ist der Vertrag aus §7.2); `undefined` → kein weiterer Dialog; `disabled`
folgt `arbiter.activeRun()` (`restore-panel.spec.ts:446-459`). **Nicht** getestet: Beschriftung,
Klassen.

**Fertig:** Vitest, `lint`, `format:check` grün. Kann mit Task 2 in einen Commit, wenn der Dialog
sonst ohne Aufrufer wäre.

### Task 4 — Umzug in den Seitenkopf, `RestorePanel` entfernen, DECISIONS + Designsprache

**Ziel:** `<app-file-import-trigger>` im Seitenkopf an der Position aus 1.3, innerhalb des
bestehenden `@if` bei `usage-stats-page.html:33`, mit `importScopeCurrent()` als Input;
Kommentar `:21-32` ergänzt; Block `:707-716` samt Kommentar `:710-712` entfernt; Import
`usage-stats-page.ts:121` und Eintrag `:209` getauscht — **sonst keine Änderung an der Seite**.
`restore-panel.ts` und `restore-panel.spec.ts` gelöscht; keine verbliebene Referenz
(`grep -rn RestorePanel web/src web/e2e`).

**Im selben Commit (Regel 3):**

- `docs/DECISIONS.md`, neuer oberster Eintrag „2026-09-XX — Der Datei-Weg zieht in den Seitenkopf:
  Trigger und Einspiel-Dialog statt `RestorePanel` (#91), auf Widerruf". Inhalt: `**Betrifft:**`
  mit allen berührten Dateien; der Fall (unauffindbar unter dem Raster, n = 1, Zitat aus dem
  Ticket); warum ein Dialog und kein nackter Dateidialog (Sorten sichtbar **vor** der Suche,
  Fehler am Ort der Auswahl); warum das File-Input im Dialog sitzt (verbrauchte Geste nach
  `closed`); warum die Ketten **nach** dem Schließen laufen und nicht daraus (Ein-Dialog-Vertrag
  `dialog.ts`, Reihenfolgen aus §7.2 unverändert); welche Sperren der Trigger trägt und welche
  bewusst nicht (`atlasOrder`); Capture-Disziplin; Benennung (`file-import-*`, weil zwei der drei
  Wege kein Restore sind; i18n-Namensraum bleibt); **„auf Widerruf"** wie die Dock-Kurzform — der
  Ort ist umkehrbar, §8.7 bleibt wortgleich, der Satz vom 2026-08-06 („bleibt im Fluss") bleibt
  stehen, die Präzisierung dazu steht bereits im Eintrag vom 2026-09-07 und wird **verwiesen, nicht
  wiederholt**. Kein Bezug auf #68 als Bedingung.
- `docs/UI-Designsprache.md`: §2.5 (`:115-116`) nennt „Mass-Delete- wie Restore-Panel" — auf den
  Trigger umformulieren (er verschwindet auf `coarse` mit demselben Gate); §4.2 Referenzliste
  (`:184`) um den Trigger als weiteren Auslöser ergänzen; **neuer §7.3 „Einspiel-Dialog (#91)"**
  mit der Zeilenreihenfolge aus 1.1 als Vertrag, dem Ergebnisvertrag und dem Satz, dass die Ketten
  nacheinander laufen; §8.7 Referenzzeile bleibt (Seitenkopf ist schon genannt). Nur Ist-Stand,
  keine Vorgeschichte (Konvention seit 2026-08-07).
- `docs/designs/Aktionszeile-80-2026-09-06.md` bleibt unverändert — er ist Entwurf, kein Ist-Stand.

**Fertig:** `npm --prefix web test -- --watch=false`, `lint`, `format:check` grün; die App startet
(`npm --prefix web start` gegen laufende Api) und der Knopf steht sichtbar oben, öffnet den
Dialog, der Dateidialog öffnet aus dem Dialog heraus (Handprobe, weil kein Test eine echte
Nutzergeste hat); auf `pointer: coarse` (DevTools-Emulation) ist der Knopf weg. Commit
`feat(usage-stats): …` mit DECISIONS + Designsprache.

### Task 5 — E2E nachziehen (`web/e2e/emote-import.e2e.spec.ts`)

**Ziel:** Beide Beschreibungsblöcke (`:335-470`, `:472-550`) laufen über den neuen Weg.

- Ein Helfer öffnet den Einspiel-Dialog über den Seitenkopf-Knopf (locale-fest per Position
  `main header button` `.nth(2)` oder per `aria-label`, falls einer gesetzt wird — Begründung wie
  `ui-audit.audit.ts:801-806`), lokalisiert das File-Input **im** Dialog und liefert es.
- Ein Fall prüft die Fokusführung: nach dem Öffnen ist das Datei-Bedienelement fokussiert
  (`toBeFocused`) — das ist die Prüfung zu offener Frage 6 und gehört hierher, nicht in die Unit-Spec.
- Ein Fall prüft die DOM-Reihenfolge Sortenliste **vor** Datei-Bedienelement (Muster `:463-467`,
  zulässig, weil §7.3 sie als Vertrag führt).
- **Falle im Wartepunkt:** Nach `setInputFiles` ist der Einspiel-Dialog noch offen, bis
  `file.text()` aufgelöst ist; dann schließt er, und der Confirm öffnet. Ein Warten auf
  `#app-dialog-title` (`:385`, `:427`, `:460`) trifft deshalb **den noch offenen Einspiel-Dialog**.
  Warten muss auf ein Merkmal, das nur der Confirm hat (Titel mit Anzahl und Kanal, oder erst das
  Verschwinden des Einspiel-Titels, dann der neue Titel). Ohne das klickt `Abbrechen` (`:389`) auf
  den falschen Dialog, und der Fehler sieht aus wie ein Timing-Flake.
- **Abweisungen invertieren:** `:510` und `:549` erwarten heute „kein Dialog". Neu: der
  Einspiel-Dialog ist offen, das `alert` steht **im** Dialog, und es gibt genau einen Dialog. Nach
  dem zweiten Fehler im selben Test bleibt derselbe Dialog offen (kein erneutes Öffnen nötig).
- Der Kommentar `:336-339` („RestorePanel.onFileSelected") wird auf die neuen Namen umgestellt.
- Der Block `running import: channel switch` (`:551 ff.`) ist nicht betroffen — prüfen, dass er
  nicht doch `input[type="file"]` benutzt.

**Fertig:** `npm --prefix web run e2e` komplett grün — **nur bei freiem `:5151`** (vorher
`dotnet run` beenden; das Fehlerbild einer laufenden Api ist irreführend, CLAUDE.md „Tests").
Laufzeit als Kennzahl im Blick (1,5–1,7 min normal; ein roter Lauf mit deutlich längerer Laufzeit
ist Speicherdruck, nicht Regression). Commit `test(e2e): …`.

### Task 6 — Audit-Harness nachziehen (`web/e2e/audit/ui-audit.audit.ts`)

**Ziel:**

- `usage-stats-restore-import-error` (`:671-716`): `afterLoad` klickt zuerst den Trigger (`main
  header button` `.nth(2)`), wählt dann die fremde Protokolldatei im Dialog und wartet auf das
  `alert`. Der Screenshot zeigt jetzt den Dialog im Fehlerzustand — das ist der Zustand, der
  gesehen werden soll (Banner unter Liste und Datei-Bedienelement, 360 px, de **und** en).
- **Neues Szenario** `usage-stats-file-import-dialog`: der Dialog im Ausgangszustand (Liste, kein
  Fehler). Begründung: die drei Listeneinträge sind die längsten neuen Strings; §12 verlangt für
  jede neue Fläche ein Szenario, und der Ausgangszustand hat sonst nirgends ein Bild.
- Die Positionsverweise `:810` und `:841` bleiben `.nth(1)`; der Kommentar `:801-806` erwähnt
  künftig, dass danach der Einspiel-Trigger folgt.

**Fertig:** Der Harness läuft durch (`cd web && npx playwright test --config=playwright.audit.config.ts`,
vorher `.audit-out/` leeren); die Auswertung kommt in Task 8.

### Task 7 — (in Task 4 enthalten: §7.3 und DECISIONS) — hier nur die Prüfung der Doku-Konsistenz

**Ziel:** Ein Subagent (`haiku`) grept nach verbliebenen Erwähnungen: `RestorePanel`,
`app-restore-panel`, `restore-panel.ts`, `restore.import.hint`, „Restore-Panel" in
`docs/UI-Designsprache.md` und `DESIGN.md`; historische Einträge in `docs/DECISIONS.md` und
`docs/superpowers/plans/2026-08-07-mobile-ansicht.md` bleiben **unangetastet** (Log, kein
Ist-Stand). `CLAUDE.md` erwähnt das Panel nicht — nichts zu tun.

**Fertig:** Trefferliste ist leer bis auf Log und Entwurf; Befund an die Hauptsession.

### Task 8 — Gates, Coverage, Audit-Vergleich

**Ziel:** Fertig im Sinne der Projekt-`CLAUDE.md`.

1. `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`,
   `npm --prefix web run format:check`, `npm --prefix web run e2e` (freier `:5151`).
2. **Coverage — erst nach dem Commit** (`scripts/coverage-local.mjs` misst nur Committetes; „0
   geänderte Dateien" ist eine Nichtmessung): `node scripts/coverage-local.mjs --frontend-only`.
   Erwartung: die drei neuen Dateien und ihre Specs sind vollständig gedeckt (neue Dateien messen
   nah an Sonar); `usage-stats-page.ts` ist nur um den Import geändert und darf den Nenner nicht
   mit neuen Methoden füllen (deshalb 1.1: keine Seitenlogik). Fällt die Zahl unter 80 %, ist die
   erste Frage, ob ein Fall aus `restore-panel.spec.ts` beim Migrieren verloren ging — die Liste
   der 20 alten Fälle gegen die drei neuen Specs abhaken.
3. **Audit-Vergleichslauf** — der Befund heißt „schlechter als die Basis", nicht „ungleich null":
   - Basis: `git checkout 817cc93` (Stand #92; nach Merge von #108 der Merge-Commit auf `main`),
     `.audit-out/` leeren, Harness laufen lassen, `metrics/` nach
     `<scratchpad>/audit-base/` kopieren.
   - Kopf: Branch auschecken, `.audit-out/` leeren, Harness laufen lassen, `metrics/` nach
     `<scratchpad>/audit-head/`.
   - Je Datei (Szenario × Viewport × Locale × Theme) `horizontalOverflowPx`, Anzahl
     `contrastViolations`, Anzahl `smallTargetsUnder24` vergleichen; für die zwei Datei-Weg-Szenarien
     (eines geändert, eines neu) gilt zusätzlich das absolute Gate aus §12 (Overflow 0, Kontrast 0
     auf `serious`/`critical`) — sie haben keine Basis, gegen die man „schlechter" messen könnte.
   - Screenshots der beiden Szenarien in de und en bei 360 px sichten (Liste, Banner, Knopf).
4. **Regel 16 / Handprobe im Browser** (kein Test hat eine echte Nutzergeste): Klick → Dialog →
   Klick im Dialog → nativer Dateidialog öffnet; Tab-Reihenfolge landet zuerst auf dem
   Datei-Bedienelement; ein echtes Purge-Protokoll aus der Dev-DB durchlaufen (Token-Prompt vor
   Confirm), eine Emote-Liste durchlaufen (Confirm vor Token-Prompt).

**Fertig:** alle vier Punkte belegt; Zahlen und Abweichungen im Bericht an die Hauptsession.

### Task 9 — Zweitmeinung und Merge-Vorbereitung

- `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` (ohne `--scope` reviewt
  Codex den Working Tree und meldet bei sauberem Tree falsche Entwarnung). Findings sind Input,
  kein Auftrag; widersprechen sich Opus-Review und Codex bei einem P1/P2, entscheidet Fable.
- PR-Beschreibung nennt: Ort auf Widerruf, keine Migration, kein Backend, kein neuer Fehlercode;
  Audit-Vergleichszahlen; Handprobe.
- Der PR wartet auf den Merge von #108 (Rebase), nicht umgekehrt.

---

## 3. Reihenfolge und Abhängigkeiten

1 → 2 → 3 → 4 (Kette; 2 und 3 dürfen in einem Commit landen) → 5 und 6 parallel → 7 parallel zu 5/6
→ 8 → 9. Tasks 1–4 sind je ein Subagent mit frischem Kontext; die Hauptsession prüft nach jedem Task
die Fertig-Bedingung, bevor der nächste startet.

## 4. Was dieser Plan bewusst nicht tut

- Er ändert die Prompt-Reihenfolgen nicht (§7.2 „Nicht angleichen").
- Er macht den Seitenkopf nicht sticky und legt den Datei-Weg nicht ins Dock — beides ist im
  Entwurf mit Grund verworfen und im DECISIONS-Eintrag vom 2026-09-07 festgehalten.
- Er führt #68 nicht als Probe oder Bedingung.
- Er rührt weder `docs/designs/Aktionszeile-80-2026-09-06.md` noch historische
  DECISIONS-Einträge an.
- Er fügt der Seite keine Methode hinzu: alles Neue lebt in `shared/seven-tv/` und ist dort
  getestet.
