# UI-Designsprache

Verbindliche Vorgabe für alle UI-Arbeit unter `web/`. Ergebnis der UI/UX-Überarbeitung (Wellen 1–3, 2026-07-30/31) und des Admin-Bereich-Audits vom 2026-07-31.

Format pro Regel: **Was gilt** · **Wann anwenden** · **Referenz** (Dateipfad der Muster-Implementierung). Die Begründungen („warum") stehen in [DECISIONS.md](DECISIONS.md) — dieses Dokument ist das **Wie**, DECISIONS.md bleibt das **Warum**-Log. Bei Widerspruch zwischen einem älteren DECISIONS-Wortlaut und diesem Dokument gilt dieses Dokument (der präzisierende DECISIONS-Eintrag vom 2026-07-31 dokumentiert die Auflösungen).

Wer neue UI baut, arbeitet die [Checkliste in Abschnitt 11](#11-checkliste-neue-ui-bauen) ab und verifiziert per [Audit-Harness (Abschnitt 12)](#12-verifikation-per-ui-audit-harness) — dann braucht es kein neues UI/UX-Audit.

---

## 1. Geltung

- Gilt für alles unter `web/` — neue Seiten, neue Komponenten, Änderungen an bestehenden.
- Ergänzt [`web/.claude/CLAUDE.md`](../web/.claude/CLAUDE.md) (Angular-Konventionen, Signals, Standalone) — beide gelten kumulativ.
- Bestandscode wird nicht rückwirkend umgeschrieben (CLAUDE.md-Sprachregel gilt analog): Abweichungen werden bei der nächsten Berührung der Stelle behoben, nicht in Sammel-Refactorings.

## 2. Flächen & Karten

### 2.0 Farbe kommt aus Tokens, nicht aus der Palette

- **Was gilt:** Kein Template, keine Varianten-Map und keine Komponentenklasse schreibt eine Tailwind-Paletten-Farbe (`slate-*`, `purple-*`, `red-*`, `amber-*`, `emerald-*`, `blue-*`, `pink-*`, `white`, `black`) direkt. Erlaubt sind ausschließlich die semantischen Utilities aus dem Tokensatz. Paletten-Namen stehen an genau **einer** Stelle: im Tokenblock von `web/src/styles.css`.

  | Rolle | Utilities |
  |---|---|
  | Flächen | `bg-page` · `bg-surface` (die eine Kartenfläche, s. 2.1) · `bg-surface-inset` · `bg-surface-inset-hover` · `bg-field` |
  | Ränder | `border-border` · `border-border-strong` · `border-border-field` (Bedienelemente, 3:1-Pflicht — s. 5.1) |
  | Text | `text-fg` · `text-fg-body` · `text-fg-secondary` · `text-fg-muted` · `text-fg-disabled` |
  | Akzent | `bg-accent` · `bg-accent-solid` (+`-hover`) · `bg-accent-selected` · `text-accent-fg` (Akzent **als Text**) · `bg-accent-wash` · `text-on-accent` (Text **auf** gefüllter Akzentfläche) |
  | Töne | `{success,warning,danger,info,neutral}-{wash,fg,solid,dot}` — `wash` = getönte Fläche, `fg` = Schrift darauf, `solid` = gefüllte Fläche mit `on-accent`-Schrift, `dot` = kleine bedeutungstragende Grafik (Statuspunkt, Balkenfüllung; schuldet 3:1, nicht 4,5:1) |
  | Sonstiges | `shadow-overlay` (Popover/Dialog) · `bg-emote-canvas` (Bildfläche einer Emote-Kachel) · `.app-page-glow` |

- **Wann anwenden:** Immer. Braucht eine neue UI eine Farbe, die es als Token nicht gibt, wird **das Token ergänzt** — mit Wert für **beide** Modi und mit gerechnetem Kontrastnachweis in der Commit-Message — nicht die Palette benutzt. Unterscheiden sich die Modi strukturell statt nur im Wert, ist das zuerst ein Hinweis, dass das Token falsch geschnitten ist; erst danach eine CSS-Variante. **Es gibt keine themefeste Farbe mehr** — `bg-emote-canvas` war die eine dokumentierte Ausnahme und wurde nach dem Ansehen zurückgenommen (s. 2.4). Ein eigenes Token heißt „eigene Rolle", nicht „fester Wert".
- **Tone-Namen sind Bedeutungen, keine Farben.** `StatusBadgeTone` und `SlotBudgetTone` heißen `accent · info · success · neutral · warning · danger` — nicht `purple`/`blue`/`emerald`. Ein Aufrufer, der `red` verlangt, verlangt einen Farbwert, den es seit dem hellen Modus nicht mehr gibt (`danger` ist dunkel `red-950`/`red-300`, hell `red-50`/`red-700`). Das gilt für jede neue Ton-Union.
- **Gefüllte Buttons werden im Hover dunkler — in beiden Modi.** `*-solid-hover` liegt immer eine Stufe unter `*-solid`. Die Regel ist keine Optik, sondern der einzige Weg, den Hover kontrastsicher zu halten: `on-accent` ist in beiden Modi weiß, ein *hellerer* Hover kann Kontrast also nur wegnehmen. Genau daran waren im Dunkeln `accent` (4,1:1) und `success` (3,7:1) unter AA gerutscht, und zwar unbemerkt, weil axe keinen Hover auswerten kann.
- **Die Werte stehen bewusst nicht hier**, sondern nur in `web/src/styles.css`. Eine zweite Wertetabelle in Markdown driftet ab dem ersten nachgezogenen Token; der Tokenblock im Code wird bei jeder Farbänderung zwangsläufig angefasst und kann von sich selbst nicht abweichen. Dieses Dokument führt die **Rollen**, der Code die Werte, `docs/Konzept-Light-Mode.md` §5 den datierten Kontrastnachweis.
- **Erzwungen**, nicht erbeten: `npm run lint` fährt `web/scripts/check-color-tokens.mjs` mit und verbietet Paletten-Utilities unterhalb `web/src/app/`.
- **Referenz:** `web/src/styles.css` (Tokenblock), `docs/Konzept-Light-Mode.md` §4.

### 2.1 Kartenfläche

- **Was gilt:** `.app-card` (in `web/src/styles.css`) ist die **einzige** Kartenoberfläche: `border`-Rand, `surface`-Fläche, `radius-lg`, plus die Elevation aus `--ep-shadow-card`. Keine randlosen `bg-surface`-Rechtecke, keine eigenen Karten-Klassenketten.
- **Die Tiefenwirkung entsteht pro Modus anders, und das ist Absicht:** dunkel trennt über Flächenhelligkeit (der Rand liegt bei 1,4:1 und trägt die Karte nicht), hell über einen echten Elevationsschatten (Weiß auf `slate-50` sind 1,05:1 und könnten es nicht). Die *Richtung* bleibt in beiden Modi gleich — eine erhöhte Fläche entfernt sich vom Grund, eine eingelassene (`surface-inset`) geht zu ihm zurück. Nur die physikalische Richtung von „eingelassen" dreht sich; genau dafür sind Rollennamen da.
- **Wann anwenden:** Jede abgegrenzte Inhaltsfläche — Listen-Zeilen, Formular-Boxen, Monitoring-Karten, statische Sektionen.
- **Referenz:** `web/src/styles.css` (`.app-card`), Verwendung überall unter `web/src/app/features/`.

### 2.2 Hover nur bei Klickbarkeit

- **Was gilt:** `.app-card-interactive` (lila Rand-Hover + Glow-Schatten) kommt **nur** auf tatsächlich klickbare Karten (Stretched-Link-Zeilen). Statische Karten bleiben beim Basisstil — Hover darf nie einen Klick versprechen, den es nicht gibt.
- **Wann anwenden:** Genau dann, wenn die Karte den Stretched-Link-Kontrakt (2.3) erfüllt. Bedingte Anwendung ist erlaubt (`[class]`-Binding schaltet `app-card-interactive relative` nur bei Klickbarkeit zu).
- **Referenz:** `web/src/app/features/admin/admin-channels-page.ts` (bedingt), `web/src/app/features/overview/overview-page.html`.

### 2.3 Stretched-Link-Kontrakt (vollflächig klickbare Karten)

- **Was gilt:** Klickbare Listen-Karten nutzen das Stretched-Link-Pattern über `.app-card-link` (Inclusive-Components-„Cards"-Muster). Der Kontrakt hat drei Pflichtteile:
  1. Kartencontainer ist `relative` (+ `app-card-interactive`).
  2. **Ein** kurzer echter Link (Titel/Name) trägt `app-card-link` — sein `::after` dehnt die Klickfläche über die ganze Karte; Screenreader hören nur den kurzen Namen.
  3. **Jede** Sekundäraktion in der Karte (Buttons, weitere Links) liegt in einem Container mit `relative z-10` und bleibt separat klick- und fokussierbar.

  Kanonisches Markup:

  ```html
  <li class="app-card app-card-interactive relative flex ...">
    <a [routerLink]="[...]" class="app-card-link max-w-full truncate font-medium">#{{ name }}</a>
    <div class="relative z-10 ml-auto flex gap-2">
      <button type="button" appButton="danger" (click)="...">…</button>
    </div>
  </li>
  ```

- **Wann anwenden:** Jede Listen-Karte, deren primäre Aktion „öffnen/ansehen" ist. **Nicht:** die ganze Karte als `<a>` wrappen (ungültig bei inneren Buttons, aufgeblähter Accessible Name) oder ein JS-Klick-Handler auf dem Container.
- **Referenz:** `web/src/app/features/overview/overview-page.html`, `web/src/app/features/admin/admin-channels-page.ts`, `web/src/app/features/voting/vote-session-list-page.html`.

### 2.4 Bildfläche einer Emote-Kachel

- **Was gilt:** Die Fläche, auf der ein 7TV-Emote gezeichnet wird, ist `bg-emote-canvas` — ein **eigenes Token**, nicht `surface-inset`. Grund: das Bildmaterial ist fremd, für dunkle Chats gezeichnet und enthält weiße Schrift und helle Outlines. Diese Fläche wird deshalb irgendwann anders entschieden werden müssen als „irgendeine eingelassene Fläche", und dann muss es eine Zeile sein.
- **Sie folgt dem Theme.** Der erste Entwurf hielt sie in beiden Modi dunkel, um das Fremdmaterial zu schützen. Nach dem Ansehen zurückgenommen: ein fast schwarzer Balken auf **jeder** Karte ist auf einer hellen Seite das lauteste Element, und er steht auf jeder Kachel — während die Emotes, die er schützt, die Minderheit sind. Preis der Rücknahme: ein weiß umrandetes Emote verliert im Hellen seine Kontur. Der Handel ist bewusst und steht an genau einer Stelle.
- **Der Selektions-Wash liegt auf der Karte, nicht unter dem Bild.** Sonst kämpfen Wash und Bildmaterial um dieselben Pixel — der `inset-ring` (8.5) trägt die Auswahl, die Fläche verstärkt sie nur.
- **Referenz:** `web/src/styles.css` (`--color-emote-canvas`), `web/src/app/features/usage-stats/usage-stats-page.html`, `web/src/app/features/voting/vote-session-detail-page.html`.

## 3. Typografie-Hierarchie

- **Was gilt:** Vier Ebenen, feste Klassenketten:

  | Ebene | Klassen | Element |
  |---|---|---|
  | Seitentitel | `text-2xl font-bold tracking-tight` | `<h1>` in Layouts, `<h2>` auf Seiten ohne eigenes Layout-`<h1>` |
  | Sektionstitel | `text-lg font-semibold` | `<h2>` |
  | Kartentitel | `text-base font-semibold` | `<h3>` |
  | Listen-Karten-Titellink | `font-medium` (Textgröße erbt vom Kontext) | `<a class="app-card-link">` / `<span>` |

  Karten-`<h3>`s tragen **nie** die Sektionsgröße `text-lg` — genau diese Kollision (Admin-Monitoring) war ein Audit-Befund und ist behoben.
- **Wann anwenden:** Immer. Das Heading-**Level** folgt der Dokumentstruktur (eine Seite unter einem Layout-`<h1>` beginnt bei `<h2>`), die **Optik** folgt der Tabelle — beides ist unabhängig voneinander einzuhalten.
- **Ausnahme:** Die Landing-Page (`web/src/app/features/landing/landing-page.html`) ist bewusst Marketing-skaliert (`text-4xl`/`sm:text-5xl`-Hero, `sm:text-3xl`-Sektionen) und folgt der Tabelle nicht.
- **Referenz:** `web/src/app/features/admin/admin-layout.ts` (Seitentitel), `web/src/app/features/usage-stats/usage-stats-page.html` (Sektionstitel), `web/src/app/features/admin/admin-monitoring-page.ts` (Kartentitel).

## 4. Buttons, Badges, Banner

### 4.1 Buttons: `appButton`

- **Was gilt:** Jeder Button/Aktions-Link nutzt die Attribut-Direktive `appButton` (`web/src/app/shared/ui/button.ts`) — keine kopierten Utility-Ketten. Varianten `primary`/`neutral`/`outline`/`danger`/`danger-solid`, Größen `md` (Default)/`lg`. Element-spezifisches Layout (`ml-auto`, `relative z-10`, …) bleibt am eigenen `class`-Attribut, Angular merged beides.

  | Variante | Einsatz |
  |---|---|
  | `primary` | die eine Haupt-Aktion eines Kontexts (Login, Erstellen, Speichern) |
  | `neutral` | Sekundäraktionen mit Fläche (Aktualisieren, Kopieren) |
  | `outline` | leise Sekundäraktionen, Abbrechen in Dialogen |
  | `danger` | siehe 4.2 |
  | `danger-solid` | siehe 4.2 |

- **Referenz:** `web/src/app/shared/ui/button.ts`.

### 4.2 Destruktiv-Stufung: Flow-Position, nicht Schwere

- **Was gilt:** Die zwei Destruktiv-Stufen kodieren die **Position im Bestätigungs-Flow**, nicht die Schwere der Aktion:
  - `danger` (Outline): der **auslösende** destruktive Button im Seitenkontext, der neben anderen Controls steht und noch einen Bestätigungsschritt vor sich hat (Channel verlassen, Session löschen, Channel-Purge öffnen).
  - `danger-solid` (gefüllt): der **ausführende** Button — der Bestätigen-Button in `ConfirmDialog`/`TypedConfirmDialog`/Mass-Delete-Dialog sowie der Seiten-Haupt-CTA des Mass-Delete-Panels.

  Merksatz: Outline löst aus, Solid vollzieht. Dass die unwiderrufliche Purge per Outline **ausgelöst** und das reversible Verlassen per Solid **bestätigt** wird, ist damit korrekt.
- **Wann anwenden:** Jede destruktive Aktion bekommt beide Stufen: `danger`-Auslöser → Dialog → `danger-solid`-Bestätigung. Ein destruktiver Button ohne Bestätigungsdialog ist nicht vorgesehen.
- **Referenz:** Auslöser: `web/src/app/features/channel-workspace/channel-workspace-layout.ts`, `web/src/app/features/admin/admin-channels-page.ts`. Vollzug: `web/src/app/shared/ui/confirm-dialog.ts`, `web/src/app/shared/seven-tv/mass-delete-panel.ts`.

### 4.3 StatusBadge

- **Was gilt:** Jedes status-artige Label (Rolle, Bot-, Session-, Worker-Zustand) ist ein `<app-status-badge>` mit einem der festen Tones — der Baustein kennt nur Töne, die Bedeutung liegt beim Aufrufer. Gelebte Zuordnung (beibehalten, nicht umdeuten):

  | Tone | Verwendung im Bestand |
  |---|---|
  | `accent` | Broadcaster |
  | `info` | Moderator |
  | `success` | 7TV-Editor · Bot aktiv · „läuft"-Zustände |
  | `neutral` | inaktiv/neutral |
  | `warning` | degradiert/Warnung |
  | `danger` | Fehler/getrennt |

  Die Tones hießen bis 2026-08-02 `purple`/`blue`/`emerald`/`slate`/`amber`/`red`. Die Namen sind mit dem hellen Modus zu Bedeutungen geworden, weil der Wert dahinter pro Modus ein anderer ist — s. 2.0.

- **Referenz:** `web/src/app/shared/ui/status-badge.ts`; Verwendung `overview-page.html`, `admin-channels-page.ts`.

### 4.4 NoticeBanner

- **Was gilt:** Jede seitenweite Meldung ist ein `<app-notice-banner>`; keine Ad-hoc-Fehlerboxen oder gefärbten Absätze. `variant="error"` rendert `role="alert"` (wird vorgelesen), `info`/`warning` bleiben `role="status"` (still). Aktions-Button in den `[notice-action]`-Slot (rechtsbündig).
- **Wann anwenden:** `error` = fehlgeschlagener Request (Text via `apiErrorTranslationKey`, s. 9), `warning` = degradierter Zustand (Worker down, Reauth nötig, Bot inaktiv), `info` = gutartiger Wartezustand (Sync ausstehend).
- **Referenz:** `web/src/app/shared/ui/notice-banner.ts`; Verwendung `overview-page.html`, `usage-stats-page.html`.

## 5. Formulare & Validierung

### 5.1 Inputs

- **Was gilt:** `.app-input` ist der einzige Input-Stil, `.app-input-sm` die kompakte Variante für Filter-Toolbars. Beide bringen expliziten `color` mit (nötig im CDK-Overlay außerhalb der Shell-DOM).
- **Der Rand ist Vertrag, nicht Optik:** Er trägt `slate-500` und muss gegen die Fläche darunter **mindestens 3:1** erreichen (WCAG 1.4.11 — ein Eingabefeld wird durch seinen Rand überhaupt erst als Bedienelement erkennbar). Der frühere `slate-700` kam auf 1,7:1. Wer einen input-artigen Trigger von Hand nachbaut statt `.app-input` zu benutzen (der DateTime-Trigger tut das), schuldet denselben Wert — und einen Hover, der **heller** wird, nicht dunkler.
- **Referenz:** `web/src/styles.css`, `web/src/app/shared/datetime/datetime-picker.ts` (nachgebauter Trigger).

### 5.2 Label-Pflicht

- **Was gilt:** Jedes Feld hat entweder ein sichtbares `<label for="…">` + `id` am Input, oder — nur in Filter-Toolbars, wo kein sichtbares Label vorgesehen ist — `[attr.aria-label]` (+ `[title]` für den Maus-Tooltip).
- **Referenz:** sichtbares Label: `web/src/app/shared/ui/typed-confirm-dialog.ts`; `aria-label`-Fall: `web/src/app/features/usage-stats/usage-stats-page.html` (Filterleiste).

### 5.3 Feldfehler-Muster

- **Was gilt:** Feldbezogene Validierungsfehler folgen einem festen Muster:

  ```html
  <input
    id="feld-id"
    [formControl]="control"
    class="app-input"
    [attr.aria-invalid]="control.invalid && control.touched ? 'true' : null"
    [attr.aria-describedby]="control.invalid && control.touched ? 'feld-id-error' : null"
  />
  @if (control.invalid && control.touched) {
    <p id="feld-id-error" class="text-sm text-danger-fg">{{ 'x.y.error' | transloco }}</p>
  }
  ```

  Fest: Fehlertext `text-sm text-danger-fg`, Fehler-`<p>` mit `id`, Input mit `aria-invalid` + `aria-describedby` nur im Fehlerfall. Formular-**übergreifende** Fehler (Request fehlgeschlagen) laufen dagegen über `NoticeBanner variant="error"` (4.4), nicht über Feldfehler.
- **Wann anwenden:** Jedes Feld mit Client-Validierung, dessen Fehler sichtbar wird. Ein still bleibendes `invalid` ohne Anzeige (7TV-Token-Input) ist die zu vermeidende Ausnahme.
- **Referenz:** `web/src/app/features/voting/vote-session-list-page.html` (Titel-Feld), `web/src/app/features/admin/admin-channels-page.ts` (Channel-Join).

### 5.4 Validierungs-Fallen

- **Was gilt:**
  - Client-Validatoren denken die serverseitige **Normalisierung** mit, nicht nur die Server-Regex (CLAUDE.md Regel 9): Channel-Namen validieren über `channelNameValidator` aus `web/src/app/core/channels/channel-name.ts`, das den **normalisierten** Wert prüft (Nutzer tippen `HandOfBlood`).
  - `(ngSubmit)` feuert **nie** auf einem `<form>` mit nur einem standalone `[formControl]` — stattdessen `(submit)="onSubmit($event)"` mit `event.preventDefault()`.
  - Deaktivierte Submit-/Bestätigen-Buttons erklären ihren Grund als **Text** daneben, nicht nur per Ausgrauung (WCAG); Enter-Pfade prüfen die Bedingung selbst statt sich auf `disabled` zu verlassen.
- **Referenz:** `web/src/app/core/channels/channel-name.ts`, `web/src/app/shared/ui/typed-confirm-dialog.ts` (Hint + Enter-Pfad).

## 6. Lade- & Leerzustände

### 6.1 Skeleton vs. Spinner (NN/g-Regel)

- **Was gilt:** **Skeleton für Seiten-/Listen-Ladevorgänge, disabled-Button (Label bleibt konstant) für isolierte Aktionen.** Keine „Lädt…"-Textzeilen, keine Spinner für Seitenladen.
  - Listen: `<app-skeleton-rows [count]="3" />`.
  - Abweichende Formen (Grids): handgerolltes Skeleton nach demselben A11y-Muster — **ein** `role="status"`-Element mit übersetztem `aria-label`, die Schimmer-Blöcke (`.app-skeleton`) in einem `aria-hidden="true"`-Container, Zellen in der Form des echten Inhalts.
  - Aktionen (Refresh, Join, Purge): Button `[disabled]="isLoading()"`, Label bleibt.
- **Referenz:** `web/src/app/shared/ui/skeleton-rows.ts`, Grid-Variante `web/src/app/features/usage-stats/usage-stats-page.html`.

### 6.2 EmptyState

- **Was gilt:** Jeder Leerzustand ist ein `<app-empty-state>` mit `title` (warum leer) + möglichst `description` und projiziertem CTA (was als Nächstes tun). Optionales Emoji-`icon` im lila Quadrat. Kein nackter grauer Satz.
- **Wann anwenden:** Liste/Grid ohne Einträge, Filter ohne Treffer — aber erst **nach** abgeschlossenem Laden (Skeleton verhindert das Aufblitzen des EmptyState während `rxResource`-Loads mit `defaultValue`).
- **Referenz:** `web/src/app/shared/ui/empty-state.ts`; Verwendung `overview-page.html` (📺), `usage-stats-page.html` (😀/🔍).

## 7. Dialoge

- **Was gilt:** **Jeder** Dialog läuft über `Dialog.open(..., { backdropClass: 'app-dialog-backdrop', panelClass: 'app-dialog-panel' })` aus `@angular/cdk/dialog` — nie `window.confirm`, nie handgebaute Overlays. Fokus-Trap, Escape, Backdrop-Klick, `aria-modal`, Fokus-Rückgabe kommen vom CDK.
- **Wahlkriterium:**
  - `ConfirmDialog` (`shared/ui/confirm-dialog.ts`): destruktive Aktion, die eine Ja/Nein-Bestätigung braucht (Channel verlassen, Session löschen). Aufrufer übergibt fertig übersetzte `message`/`confirmLabel`; schließt nur bei explizitem Bestätigen mit `true`.
  - `TypedConfirmDialog` (`shared/ui/typed-confirm-dialog.ts`): Aktion ist unwiderruflich **und** zeilenbezogen (Channel-Purge) — Nachtippen beweist, *welche* Zeile gemeint war. Vergleich getrimmt, aber case-sensitiv. Bei `title` zusätzlich `ariaLabelledBy` an `Dialog.open` übergeben.
  - Eigener Dialog nur, wenn keiner der beiden passt (z. B. Mass-Delete mit Fortschritt) — dann gleiche `backdropClass`/`panelClass`-Konvention.
- **CDK-Fallen (beide live gefunden):**
  1. Der Overlay-Container hängt **außerhalb** der App-Shell-DOM — er erbt keine Textfarbe. Dialog-Panel und `.app-input` brauchen explizites `color` (haben sie; bei neuen Overlay-Styles daran denken).
  2. CDK injiziert seine Overlay-Styles zur Laufzeit **hinter** allen Bundle-Stylesheets. Panel-Chrome muss deshalb unlayered und mit erhöhter Spezifität definiert werden (`.cdk-overlay-pane.app-dialog-panel`) — neue Panel-Regeln nach demselben Muster.
- **Referenz:** `web/src/app/shared/ui/confirm-dialog.ts`, `web/src/app/shared/ui/typed-confirm-dialog.ts`, `web/src/styles.css` (Dialog-Klassen), Aufrufer `channel-workspace-layout.ts`, `admin-channels-page.ts`.

### 7.1 Popover (nicht-modal)

- **Was gilt:** Nicht-modale Dropdowns laufen über **`<app-popover>`** (`shared/ui/popover.ts`) — nie ein weiteres handgebautes `relative`-Wrapper-plus-`absolute`-Panel. Das Primitive bringt Panel-Chrome, `max-w-[calc(100vw-2rem)]`, Außenklick- und Escape-Dismiss mit. Der Host setzt den `position: relative`-Wrapper mit dem Marker `data-popover-anchor` um Trigger **und** Popover; Klicks darin zählen nie als Außenklick (sonst schließt der öffnende Klick das Panel im selben Dispatch wieder).
- **Vertrag:** gerendert = offen. Das Panel versteckt sich nie selbst, es emittiert `closed`; das Sichtbarkeits-Signal **und** die Fokus-Rückgabe an den Trigger gehören dem Host. Padding bringt der Inhalt mit — das Panel ist padding-frei, damit Full-Bleed-Menüzeilen und gepolsterte Formulare beide hineinpassen.
- **Abgrenzung zu §7:** Popover ≠ Dialog. Kein Fokus-Trap, kein `aria-modal`, kein Backdrop. Sobald die Interaktion den Rest der Seite blockieren soll, ist es ein CDK-Dialog. Und **kein** CDK-Overlay für den Popover-Fall: diese Panels öffnen aus Sticky-Leisten heraus und müssen deren Stacking-Kontext erben (§8.5), was ein an `<body>` gehängter Overlay-Container nicht kann.
- **Mobil:** Menüzeilen `min-h-11 sm:min-h-9` (§10, 44-px-Komfortziel bei Touch). Ein geöffnetes Popover gehört mit `afterLoad` in den Audit-Harness (§12) — geschlossen sagen die Overflow- und Touch-Target-Metriken nichts über es aus.
- **Referenz:** `web/src/app/shared/ui/popover.ts`; Verwendung `shared/datetime/date-range-menu.ts`.

## 8. Navigation

### 8.1 Tab-Leisten (Router-Link-Muster)

- **Was gilt:** Tab-Leisten sind Router-Links, **kein** ARIA-Tabs-Pattern (`role="tablist"`/`aria-selected` sind hier falsch, da echte Navigationen). Kanonisches Snippet — inklusive `ariaCurrentWhenActive="page"`, das ist Pflicht:

  ```html
  <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-border">
    <a
      [routerLink]="['...', 'tab']"
      routerLinkActive
      ariaCurrentWhenActive="page"
      #tab="routerLinkActive"
      [class]="
        tab.isActive
          ? 'flex items-center border-b-2 border-accent px-3 text-sm text-fg transition'
          : 'flex items-center border-b-2 border-transparent px-3 text-sm text-fg-muted transition hover:text-fg-body'
      "
    >{{ 'x.tab' | transloco }}</a>
  </nav>
  ```

  Die Klassenkette ist bewusst (noch) keine Primitive — bei Änderungen **alle** Vorkommen synchron halten: `admin-layout.ts` (4×), `channel-workspace-layout.ts` (2×). `h-10` und `flex items-center` (statt `py-2`) sind Teil des Sticky-Vertrags aus §8.5 — die Tab-Leisten-Höhe ist der `top`-Offset der Filter-Toolbars.
- **Referenz:** `web/src/app/features/admin/admin-layout.ts`.

### 8.2 In-Page-Anker

- **Was gilt:** Anker laufen über `routerLink` + `fragment`, **nie** über nackte `href="#…"` (löst gegen `<base href="/">` auf und bricht). Der Router scrollt selbst (`withInMemoryScrolling` + `onSameUrlNavigation: 'reload'` sind in `app.config.ts` konfiguriert — nicht entfernen, der Reload-Teil macht den zweiten Klick auf denselben Anker funktionsfähig).
- **Referenz:** `web/src/app/app.config.ts`, `web/src/app/features/landing/landing-page.html`.

### 8.3 Rollen-Sichtbarkeit

- **Was gilt:** Sichtbarkeit von Navigation/Bereichen entscheidet ein **gelesenes Feld** (z. B. `isGlobalAdmin` aus dem gecachten `/me`), nie ein provozierter Fehler (403-Probing). Guards für Rollen-Bereiche leiten auf `/` (nicht `/login`) und stashen keine Return-URL. Autoritativ bleibt immer der serverseitige Filter.
- **Referenz:** `web/src/app/core/auth/admin.guard.ts`, `web/src/app/features/shell/app-shell.ts`.

### 8.4 Pagination

- **Was gilt:** Seitenweise Listen nutzen `<app-pager [page] [totalPages] [scrollTarget] (pageChange)>` gegen ein `PagedResult<T>` vom Backend (`items/page/pageSize/totalCount/totalPages`); der Pager versteckt sich bei einer Seite selbst. `create`/`delete` laden die Liste neu statt optimistisch zu patchen (verschiebt sonst die Paginierung); reine In-Place-Änderungen dürfen lokal patchen.
- **Ein Seitenwechsel repositioniert den Viewport — `[scrollTarget]` ist Pflicht, sobald die Liste länger als ein Bildschirm werden kann.** Der Pager steht unter seiner Liste, der Klick passiert also immer am unteren Dokumentende; ohne Reposition kommen die Zeilen der nächsten Seite komplett *oberhalb* des Viewports an und der Leser schaut weiter auf das Ende einer Liste, durch die er nie gescrollt ist. Übergeben wird eine Template-Referenz auf den Kopf der Ergebnisregion:

  ```html
  <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">…</h2>
  …
  <app-pager [page]="page()" [totalPages]="totalPages()" [scrollTarget]="resultsTop" … />
  ```

  Drei Teile, alle notwendig:
  - **`tabindex="-1"`** — der Pager fokussiert das Ziel (WCAG 2.4.3). Ohne Fokusverschiebung fällt der Fokus beim Seitenwechsel auf `<body>`, weil der geklickte Button samt Liste in den Lade-Zweig verschwindet.
  - **`scroll-mt-*` = Höhe des Sticky-Stapels über der Seite** (§8.5): `scroll-mt-24` unter einer Tab-Leiste (14 + 10), `scroll-mt-14` direkt unter der Shell. Ohne das parkt `scrollIntoView` das Ziel hinter dem Header. Die Filter-Toolbar bekommt keinen Anteil — sie ist selbst sticky und pinnt an ihrem eigenen `top`.
  - **Reposition vor dem `pageChange`-Emit**, im Pager gekapselt: der Emit setzt das Page-Signal, die Resource fällt auf ihren `defaultValue` zurück, und Liste wie Pager werden durch das Skeleton ersetzt. Alles danach liefe gegen ein totes Element.

  Instant, nie `behavior: 'smooth'`: das ist ein Inhaltsaustausch, kein geführter Rundgang, und mehrere Bildschirmhöhen zu animieren ist langsamer als der Leser und ein Motion-Trigger. Die Seitenzeile des Pagers ist `role="status"` und damit eine Live-Region — die Fokusverschiebung sagt *was* auf dem Schirm steht, nie *welche Seite*.
- **Der Pager bleibt bewusst im Lade-`@else` und wird nicht während des Ladens weitergerendert.** Angulars `resource` gibt bei einem Params-Wechsel `value()` auf den `defaultValue` zurück (nur ein `reload()` mit gleichen Params behält den Stream), `totalPages()` ist während des Ladens also `0` und der Pager würde sich ohnehin selbst verstecken. Ihn stehen zu lassen bräuchte in jeder Seite einen klebrigen Zweitzustand — und der Fokus ist durch die Reposition bereits gerettet.
- **Nicht in der URL:** die Seitennummer ist reiner Signal-State, kein Query-Param. Reload und Zurück-Button landen also auf Seite 1. Bewusst offen — ein Query-Param würde den Router in den Scroll-Pfad hängen (`scrollPositionRestoration`, `app.config.ts`) und sich mit der Anker-Reposition überlagern.
- **Referenz:** `web/src/app/shared/pagination/pager.ts` (+ `pager.spec.ts`); Verwendung `admin-audit-log-page.ts`, `admin-users-page.ts`, `channel-activity-page.ts`, `my-votings-page.ts` (`scroll-mt-14`), `vote-session-list-page.html` (Anker ist die `<ul>` — die Seite hat über den Zeilen keine eigene Überschrift, nur die Anlege-Karte, und genau dorthin darf ein Seitenwechsel *nicht* zurückspringen).

### 8.5 Sticky-Ebenen (Header · Tabs · Filter)

- **Was gilt:** Die Seite scrollt als **ein Dokument** (kein App-Frame mit innerem Scroll-Container — der bräche CDK-Virtual-Scroll, Router-Scroll-Restoration und das Einklappen der Mobile-Browserleiste). Drei Ebenen bleiben dabei per `position: sticky` sichtbar, mit **festen Höhen als Vertrag**:

  | Ebene | Höhe | `top` | z |
  |---|---|---|---|
  | Shell-Header (`app-shell.ts`) | `h-14` | `top-0` | `z-30` |
  | Tab-Leisten (§8.1) | `h-10` | `top-14` | `z-20` (via `.app-sticky-bar`) |
  | Filter-Toolbars | variabel (darf wrappen) | `top-24` | `z-20` (via `.app-sticky-bar`) |

  Sticky-Leisten nutzen die Primitive **`.app-sticky-bar`** (`styles.css`): sticky + `z-20` + abgedunkelter Blur-Hintergrund; nur der `top`-Offset kommt als Tailwind-Klasse an der Verwendungsstelle. Filter-Toolbars bekommen zusätzlich `py-2`, damit der Blur eine Fläche hat. **Neue Seite mit Filter-Toolbar ⇒ `app-sticky-bar top-24 py-2`**, neue Tab-Leiste ⇒ Snippet aus §8.1.
- **Virtualisierte Emote-Grids scrollen mit dem Dokument:** `<cdk-virtual-scroll-viewport scrollWindow>` — kein innerer Scroll-Container, keine feste Viewport-Höhe, kein Rahmen um das Grid. Die Zeilen laufen beim Scrollen bewusst unter den transluzenten Sticky-Leisten durch. Zum Vertrag gehört die Regel `cdk-virtual-scroll-viewport[scrollWindow] { overflow-anchor: none; }` in `styles.css` (bei `scrollWindow` wendet CDK `.cdk-virtual-scrollable` nicht an, das Scroll-Anchoring des Browsers würde sonst auf dem Dokument jittern) sowie `minBufferPx`/`maxBufferPx` ≥ 1×/2× Zeilenhöhe (die CDK-Defaults sind kleiner als eine Grid-Zeile). Die `ROW_HEIGHT_PX`-Konstanten der Seiten müssen die Kartenhöhen-Arithmetik als Kommentar nennen.
- **z-Leiter (verbindlich):** dekorativer Glow `-z-10` < Karten-Action-Container/Stretched-Link `z-10` (§2.3, unverändert) < Sticky-Leisten `z-20` < Shell-/Landing-Header `z-30` (dessen Mobile-Disclosure liegt als `z-20` **im** Header-Kontext und damit über allem). Dropdowns, die aus einer Sticky-Leiste heraus öffnen (z. B. das Zeitraum-Menü der Usage-Stats, `shared/ui/popover.ts` via `shared/datetime/date-range-menu.ts`), erben deren `z-20`-Kontext und liegen damit über dem Content; Dropdowns im Content (Datetime-Picker im Create-Formular, `z-30` im `z-10`-Karten-Kontext) bleiben unter den Leisten — sie öffnen nach unten, weg davon.
- **Warum feste Höhen:** `sticky` braucht für gestapelte Ebenen exakte `top`-Offsets. `h-14`/`h-10` sind deshalb keine Optik, sondern Berechnungsgrundlage (`top-24` = 14 + 10) — wer sie ändert, zieht alle `top`-Werte nach. Die Filter-Toolbar selbst darf beliebig hoch wrappen, ihr eigener `top` hängt nur von den Ebenen **über** ihr ab.
- **Selektions- und Hover-Zustände malen ausschließlich *innerhalb* der Kartenfläche** — konkret `inset-ring-2 inset-ring-accent` statt `ring-2` (Tailwind v4; `ring-inset` gibt es dort nicht mehr). Grund ist genau diese Sticky-Konstruktion: Scroll-Container und Sticky-Leisten sind beide exakt die Content-Box von `<main class="mx-auto max-w-5xl px-4">`, und ein *outset* `ring-2` malt 2 px **außerhalb** der Border-Box. Bei den Karten der ersten und letzten Grid-Spalte lagen diese 2 px damit links und rechts neben dem Hintergrundkasten der Leiste und schimmerten beim Durchscrollen durch. Die Leiste breiter zu machen wurde verworfen: das hätte den Randeffekt nur verdeckt, statt ihn zu vermeiden, und jede künftige Leiste hätte mitziehen müssen. **Neuer Selektionszustand auf einer Karte ⇒ `inset-ring-*`.**
- **Die Fokus-Outline bleibt bewusst outset** (globaler `:focus-visible`-Ring, §10) — sie ist transient und darf an den Randspalten nicht abgeschnitten werden. Genau deshalb steht in `styles.css` weiterhin `contain: layout style` statt CDKs `contain: content` auf `.cdk-virtual-scroll-content-wrapper`: Paint-Containment würde sie kappen. Diese Regel nicht „aufräumen".
- **Referenz:** `web/src/styles.css` (`.app-sticky-bar`, CDK-Containment-Block), `web/src/app/features/admin/admin-audit-log-page.ts` (Toolbar), `web/src/app/features/shell/app-shell.ts` (Header), `usage-stats-page.html` + `vote-session-detail-page.html` (Selektionsring).

### 8.6 Rücknavigation (hierarchischer Up-Link)

- **Was gilt:** Jede Seite, die kein Wurzelknoten ist, trägt oben links genau **einen** Up-Link auf ihren Elternknoten der *Informations*-Hierarchie — als Primitive `<app-back-link [link] [label] />` (`shared/ui/back-link.ts`), nie als handgebauter Anker und nie als `history.back()`.
- **Warum kein Verlaufs-Zurück:** Die tiefen Seiten werden regelmäßig per Deep-Link betreten und überspringen dabei ihre Liste — eine Vote-Session wird direkt nach dem Anlegen aus den Usage-Stats geöffnet, außerdem aus My-Votings und aus dem Admin-Bereich. Der Browser-Verlauf zeigt dort also gerade *nicht* nach oben; ein „Zurück" wäre sachlich falsch. Zielgenaue Up-Links sind überall konstruierbar, weil `paramsInheritanceStrategy: 'always'` (`app.config.ts`) jeder Kindroute `channelName` und `sessionId` mitgibt.
- **Warum kein Breadcrumb:** NN/g empfiehlt Breadcrumbs ab drei Ebenen — hier bildet aber die Tab-Leiste (§8.1) die Ebenen 2/3 bereits dauerhaft ab, und nur *eine* Seite (`vote-sessions/:sessionId`) liegt überhaupt auf Ebene 4. Ein Breadcrumb hätte die Tabs verdoppelt und als vierte Leiste den Höhen-Vertrag aus §8.5 gebrochen. Der Up-Link scrollt bewusst mit dem Inhalt weg, ist also keine Sticky-Ebene.
- **Wo er steht:** als erstes Element der Seite, in einer Zeile mit deren Überschrift (`flex flex-wrap items-center gap-x-4 gap-y-2`) — so wie es `ChannelWorkspaceLayout` vorgibt. Auf einer Detailseite steht er **außerhalb** des Lade-`@if`, damit der Ausweg auch beim Laden und nach einem Fehler existiert. Seiten unterhalb eines Layouts mit Up-Link (Usage-Stats, Vote-Session-Liste) bekommen **keinen zweiten** — sie erben den des Layouts.
- **Label:** der Eigenname des Ziels, nicht „Zurück" — und wo das Ziel bereits einen Key hat, **derselbe** Key (die Vote-Session-Detailseite beschriftet ihren Up-Link mit `channelWorkspace.tabs.voting`, dem Namen des Tabs, auf dem sie landet). Generische Ziele stehen unter `nav.*`.
- **Barrierefreiheit:** echter `<a routerLink>` (in neuem Tab öffenbar, kein `<button (click)="navigate()">`), der Pfeil `←` ist `aria-hidden` und damit dekorativ, der zugängliche Name wird in der Primitive auf `nav.backTo` („Zurück zu {{target}}") verbreitert — enthält den sichtbaren Text und erfüllt damit WCAG 2.5.3. Fokus über den globalen `:focus-visible`-Ring.
- **Referenz:** `web/src/app/shared/ui/back-link.ts` (+ `back-link.spec.ts`); Verwendung `channel-workspace-layout.ts`, `admin-layout.ts`, `my-votings-page.ts`, `vote-session-detail-page.html`; Flow-Test `web/e2e/back-navigation.e2e.spec.ts`.

## 9. i18n-Pflichten

- **Was gilt:**
  - Jeder sichtbare Text (auch `aria-label`, Skeleton-Labels, `title`-Tooltips) ist ein Transloco-Key mit Eintrag in **beiden** Locales (`web/public/i18n/de.json` + `en.json`). Keine hartkodierten Strings im Template.
  - Request-Fehlertexte kommen **ausschließlich** aus `apiErrorTranslationKey(error)` (`web/src/app/core/i18n/api-error.ts`): bekannter Backend-`errorCode` → `errors.api.<code>`, sonst Status-Fallback (`errors.status.*`), sonst `errors.generic`. Ein neuer Backend-`ApiErrorCode` braucht den Eintrag in `api-error.ts` **und** beiden Locales — `api-error.spec.ts` erzwingt das (CLAUDE.md Regel 7). 401 wird nie auf Seitenebene behandelt (globaler `apiAuthInterceptor`).
  - Button-/Toggle-Labels bleiben **konstant**; Zustand kodiert `aria-pressed` + Optik, nicht ein Label-Tausch (verhindert Breiten-Sprünge und Toolbar-Umbrüche).
  - Ungenutzte Keys werden beim Entfernen eines Features aus beiden Locales gelöscht.
- **Referenz:** `web/src/app/core/i18n/api-error.ts` + `api-error.spec.ts`; Toggle-Muster `web/src/app/shared/emotes/emote-usage-filter.ts`.

## 10. Accessibility-Checkliste

Basis: `web/.claude/CLAUDE.md` — **AXE-pass und WCAG-AA-Minimum sind Pflicht.** Konkret in diesem Projekt:

- [ ] **Fokus:** Der globale `:focus-visible`-Ring (lila, 2 px, Offset — `styles.css`) gilt für alles Interaktive. Nie `outline-none`/`focus:outline-none` ohne gleichwertigen Ersatz.
- [ ] **Touch-Targets:** ≥ 24 × 24 px für alles Interaktive (WCAG 2.5.8 AA), 44 px als Komfortziel für Primäraktionen — die Vote-Buttons erreichen es unterhalb `sm` (`min-h-11 sm:min-h-6`). Ausnahme „equivalent target" (kleines Control in vollflächig klickbarer Karte) ist zulässig, wird aber **im Template als Kommentar dokumentiert**.
- [ ] **ARIA-Rollen:** `role="alert"` nur für Fehler (NoticeBanner `error`), `role="status"` für stille Meldungen und Skeleton-Wrapper; `radiogroup` + Roving-Tabindex für SegmentedControl (`shared/ui/segmented-control.ts` wiederverwenden, nicht nachbauen) — es selektiert bei Fokus und taugt deshalb nur, wenn ein Wechsel billig ist; hängt am Wechsel ein Refetch, gehört die Auswahl in ein Popover-Menü (`shared/datetime/date-range-menu.ts`), wo Pfeile nur den Fokus bewegen und Enter/Space committet (von APG für genau diesen Fall erlaubt); `ariaCurrentWhenActive="page"` auf aktiven Nav-/Tab-Links.
- [ ] **Feldfehler:** Muster aus 5.3 (`aria-invalid` + `aria-describedby` + Fehler-`id`).
- [ ] **Disabled erklärt sich:** Grund als sichtbarer Text neben dem Button (TypedConfirm-Hint-Muster), nicht nur Ausgrauung.
- [ ] **Dekoratives versteckt:** Emoji-Icons und Skeleton-Schimmer `aria-hidden="true"`.
- [ ] **Accessible Names kurz:** Stretched-Link-Karten lassen den Screenreader nur den kurzen Titel hören (2.3), keine ganze Karte als Linktext.
- [ ] **Kontrast, in beiden Modi:** Text 4,5:1, Ränder/Fokusringe/bedeutungstragende Grafiken 3:1 — **gerechnet für hell UND dunkel, nicht geschätzt** (2.0: ein neues Token bringt beide Werte plus den Nachweis in der Commit-Message mit). Die schwächste zulässige Textstufe ist `text-fg-muted` (7,0:1 auf der Karte im Dunkeln, 7,6:1 im Hellen); die frühere `slate-500`-Stufe erreichte nur 3,7:1 und existiert nicht mehr. Input-Ränder: `border-border-field`, s. 5.1.
- [ ] **Hover-Zustände mitrechnen:** Ein Hover ist ein eigener Zustand und schuldet denselben Kontrast wie der Ruhezustand. **Kein Werkzeug prüft das** — axe kennt nur, was gerade gerendert ist. Für gefüllte Buttons erledigt die Regel aus 2.0 das (`*-solid-hover` immer eine Stufe dunkler); alles Handgebaute rechnet selbst nach.
- [ ] **axe-Kontrastgate:** Der Audit-Harness (12) fährt `@axe-core/playwright` mit der Regel `color-contrast` pro Zustand und schreibt `contrastViolations`. **Gate: 0 auf `serious`/`critical`.** Grenze, die man kennen muss: axe rechnet nur, was es als Text über einer bestimmbaren Fläche erkennt — halbtransparente Stapel verweigert es, und Grafik-Kontrast (1.4.11: Ampelpunkte, Balkenfüllungen, Ränder) deckt die Regel gar nicht ab. Beides bleibt Handarbeit.
- [ ] **Kontrast/nativ:** `color-scheme` wird **pro Theme** im Tokenblock auf `:root` gesetzt (nicht mehr fest auf `body`) — dadurch folgen `input[type="time"]`, Scrollbars und Autofill dem Modus von selbst. Farbpaare der Primitives (Badge-Tones, Banner-Varianten) nicht ad hoc neu mischen — sie sind Tokens (2.0).

## 11. Checkliste „Neue UI bauen"

Vor dem Abschluss jeder UI-Änderung abhaken:

1. [ ] **Primitives statt Utility-Ketten:** `appButton`, `StatusBadge`, `NoticeBanner`, `EmptyState`, `SkeletonRows`, `SegmentedControl`, `ConfirmDialog`/`TypedConfirmDialog`, `Pager`, `ThemeMenu`, `.app-card*`, `.app-input*` — nichts davon nachbauen.
2. [ ] **Farbe aus Tokens** (2.0): keine Paletten-Utility unter `web/src/app/` — `npm run lint` erzwingt das. Fehlt ein Token, wird es **ergänzt**, mit Werten für **beide** Modi und gerechnetem Kontrast in der Commit-Message. Und: **beide Modi angesehen**, nicht nur den, in dem gerade gearbeitet wurde.
3. [ ] **Flächen:** `.app-card`; `app-card-interactive` + Stretched-Link-Kontrakt nur bei echter Klickbarkeit (2.3).
4. [ ] **Typo-Skala** eingehalten (Abschnitt 3), Heading-Level folgt Dokumentstruktur.
5. [ ] **Destruktiv-Flow:** `danger`-Auslöser → Dialog → `danger-solid`-Vollzug (4.2).
6. [ ] **Ladezustand:** Skeleton (Seite/Liste) bzw. disabled-Button (Aktion) — kein Lade-Text (6.1).
7. [ ] **Leerzustand:** `EmptyState` mit Warum + CTA (6.2).
8. [ ] **Fehlerpfad:** `apiErrorTranslationKey` + `NoticeBanner error`; Feldfehler nach 5.3.
9. [ ] **i18n:** alle Keys in **beiden** Locales, konstante Labels (9).
10. [ ] **A11y-Checkliste** (10) durchgegangen; neue Formulare mit Label + Feldfehler-ARIA.
11. [ ] **Audit-Harness** gelaufen, Gates grün inkl. `contrastViolations` (12); bei neuen Seiten: Szenario ergänzt.
12. [ ] Konvention geändert/präzisiert? → DECISIONS-Eintrag im selben Commit (CLAUDE.md Regel 3) und dieses Dokument aktualisieren.

## 12. Verifikation per UI-Audit-Harness

- **Was er ist:** Playwright-Harness getrennt von der e2e-Suite: rendert die UI-Zustands-Matrix (~30 Szenarien × 3 Viewports 360/768/1280 × de/en × **dunkel/hell**, API gemockt) und schreibt pro Zustand einen Full-Page-Screenshot + JSON-Metriken.
- **Theme ist die vierte Dimension, und sie läuft bewusst nicht voll.** Dunkel deckt alle drei Viewports ab, **hell nur 1280** — dieselbe Abwägung, die die `en`-Locale schon macht. Begründung: Layoutbrüche sind theme-unabhängig (Farbe ändert keine Kastengrößen), und was am hellen Modus wirklich zu prüfen ist, ist der Kontrast — den misst der axe-Gate in *jedem* Zustand. Das hält die Laufzeit bei ~1,3× statt 2×. **Ausnahme:** in der Welle, die einen Modus erstmals ausliefert, den Skip herausnehmen und die volle Matrix in beiden Modi durchsehen.
- **Wann laufen lassen:** Nach jeder Layout-/Style-Änderung mit Flächenwirkung, bei jeder neuen Seite (vorher als Szenario in `web/e2e/audit/ui-audit.audit.ts` ergänzen — Route mocken, Edge-Case-Daten mit langen Namen verwenden) und vor Abschluss jeder UI-Welle.
- **Wie:**

  ```
  cd web
  npx playwright test --config=playwright.audit.config.ts
  ```

  (Kein npm-Script; startet selbst `ng serve` auf Port 4300.) Output unter `web/.audit-out/` (gitignored): `shots/<szenario>--<viewport>--<locale>--<theme>.png`, `metrics/<szenario>--<viewport>--<locale>--<theme>.json`. **Vor einem Lauf, dessen Metriken man auswertet, `.audit-out/` leeren** — die Dateinamen haben sich mit der Theme-Dimension geändert, alte Dateien bleiben sonst liegen und verfälschen jede Auszählung über das Verzeichnis.
- **Metriken lesen:** Pro JSON-Datei:
  - `horizontalOverflowPx` — horizontaler Seiten-Overflow in px. **Gate: muss 0 sein.**
  - `smallTargetsUnder24` — interaktive Elemente < 24 px (WCAG 2.5.8). **Gate: keine neuen Einträge gegenüber dem letzten Lauf** (bestehende Einträge sind dokumentierte „equivalent target"-Ausnahmen).
  - `targets24to43` — Elemente unter dem 44-px-Komfortziel: beobachten, kein hartes Gate.
  - `beyondRightEdge` — Elemente jenseits der rechten Viewport-Kante: wie Overflow behandeln.
  - `contrastViolations` — axe-Befunde der Regel `color-contrast`, gefiltert auf `serious`/`critical`. **Gate: muss leer sein.** Was axe nicht sieht, steht in §10.
- Screenshots zusätzlich sichten (de **und** en — längere deutsche Strings sind der häufigste Umbruch-Bruch; und in der Auslieferungswelle eines Modus beide Themes).
- **Referenz:** `web/playwright.audit.config.ts`, `web/e2e/audit/ui-audit.audit.ts`.
