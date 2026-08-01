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

### 2.1 Kartenfläche

- **Was gilt:** `.app-card` (in `web/src/styles.css`) ist die **einzige** Kartenoberfläche: `border-slate-800`-Rand, `bg-slate-900`-Fläche, `radius-lg`. Keine randlosen `bg-slate-900`-Rechtecke, keine eigenen Karten-Klassenketten.
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
  | `purple` | Broadcaster |
  | `blue` | Moderator |
  | `emerald` | 7TV-Editor · Bot aktiv · „läuft"-Zustände |
  | `slate` | inaktiv/neutral |
  | `amber` | degradiert/Warnung |
  | `red` | Fehler/getrennt |

- **Referenz:** `web/src/app/shared/ui/status-badge.ts`; Verwendung `overview-page.html`, `admin-channels-page.ts`.

### 4.4 NoticeBanner

- **Was gilt:** Jede seitenweite Meldung ist ein `<app-notice-banner>`; keine Ad-hoc-Fehlerboxen oder gefärbten Absätze. `variant="error"` rendert `role="alert"` (wird vorgelesen), `info`/`warning` bleiben `role="status"` (still). Aktions-Button in den `[notice-action]`-Slot (rechtsbündig).
- **Wann anwenden:** `error` = fehlgeschlagener Request (Text via `apiErrorTranslationKey`, s. 9), `warning` = degradierter Zustand (Worker down, Reauth nötig, Bot inaktiv), `info` = gutartiger Wartezustand (Sync ausstehend).
- **Referenz:** `web/src/app/shared/ui/notice-banner.ts`; Verwendung `overview-page.html`, `usage-stats-page.html`.

## 5. Formulare & Validierung

### 5.1 Inputs

- **Was gilt:** `.app-input` ist der einzige Input-Stil, `.app-input-sm` die kompakte Variante für Filter-Toolbars. Beide bringen expliziten `color` mit (nötig im CDK-Overlay außerhalb der Shell-DOM).
- **Referenz:** `web/src/styles.css`.

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
    <p id="feld-id-error" class="text-sm text-red-400">{{ 'x.y.error' | transloco }}</p>
  }
  ```

  Fest: Fehlertext `text-sm text-red-400`, Fehler-`<p>` mit `id`, Input mit `aria-invalid` + `aria-describedby` nur im Fehlerfall. Formular-**übergreifende** Fehler (Request fehlgeschlagen) laufen dagegen über `NoticeBanner variant="error"` (4.4), nicht über Feldfehler.
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

## 8. Navigation

### 8.1 Tab-Leisten (Router-Link-Muster)

- **Was gilt:** Tab-Leisten sind Router-Links, **kein** ARIA-Tabs-Pattern (`role="tablist"`/`aria-selected` sind hier falsch, da echte Navigationen). Kanonisches Snippet — inklusive `ariaCurrentWhenActive="page"`, das ist Pflicht:

  ```html
  <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-slate-800">
    <a
      [routerLink]="['...', 'tab']"
      routerLinkActive
      ariaCurrentWhenActive="page"
      #tab="routerLinkActive"
      [class]="
        tab.isActive
          ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
          : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
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

- **Was gilt:** Seitenweise Listen nutzen `<app-pager [page] [totalPages] (pageChange)>` gegen ein `PagedResult<T>` vom Backend (`items/page/pageSize/totalCount/totalPages`); der Pager versteckt sich bei einer Seite selbst. `create`/`delete` laden die Liste neu statt optimistisch zu patchen (verschiebt sonst die Paginierung); reine In-Place-Änderungen dürfen lokal patchen.
- **Referenz:** `web/src/app/shared/pagination/pager.ts`; Verwendung `admin-audit-log-page.ts`, `vote-session-list-page.ts`.

### 8.5 Sticky-Ebenen (Header · Tabs · Filter)

- **Was gilt:** Die Seite scrollt als **ein Dokument** (kein App-Frame mit innerem Scroll-Container — der bräche CDK-Virtual-Scroll, Router-Scroll-Restoration und das Einklappen der Mobile-Browserleiste). Drei Ebenen bleiben dabei per `position: sticky` sichtbar, mit **festen Höhen als Vertrag**:

  | Ebene | Höhe | `top` | z |
  |---|---|---|---|
  | Shell-Header (`app-shell.ts`) | `h-14` | `top-0` | `z-30` |
  | Tab-Leisten (§8.1) | `h-10` | `top-14` | `z-20` (via `.app-sticky-bar`) |
  | Filter-Toolbars | variabel (darf wrappen) | `top-24` | `z-20` (via `.app-sticky-bar`) |

  Sticky-Leisten nutzen die Primitive **`.app-sticky-bar`** (`styles.css`): sticky + `z-20` + abgedunkelter Blur-Hintergrund; nur der `top`-Offset kommt als Tailwind-Klasse an der Verwendungsstelle. Filter-Toolbars bekommen zusätzlich `py-2`, damit der Blur eine Fläche hat. **Neue Seite mit Filter-Toolbar ⇒ `app-sticky-bar top-24 py-2`**, neue Tab-Leiste ⇒ Snippet aus §8.1.
- **Virtualisierte Emote-Grids scrollen mit dem Dokument:** `<cdk-virtual-scroll-viewport scrollWindow>` — kein innerer Scroll-Container, keine feste Viewport-Höhe, kein Rahmen um das Grid. Die Zeilen laufen beim Scrollen bewusst unter den transluzenten Sticky-Leisten durch. Zum Vertrag gehört die Regel `cdk-virtual-scroll-viewport[scrollWindow] { overflow-anchor: none; }` in `styles.css` (bei `scrollWindow` wendet CDK `.cdk-virtual-scrollable` nicht an, das Scroll-Anchoring des Browsers würde sonst auf dem Dokument jittern) sowie `minBufferPx`/`maxBufferPx` ≥ 1×/2× Zeilenhöhe (die CDK-Defaults sind kleiner als eine Grid-Zeile). Die `ROW_HEIGHT_PX`-Konstanten der Seiten müssen die Kartenhöhen-Arithmetik als Kommentar nennen.
- **z-Leiter (verbindlich):** dekorativer Glow `-z-10` < Karten-Action-Container/Stretched-Link `z-10` (§2.3, unverändert) < Sticky-Leisten `z-20` < Shell-/Landing-Header `z-30` (dessen Mobile-Disclosure liegt als `z-20` **im** Header-Kontext und damit über allem). Dropdowns, die aus einer Sticky-Leiste heraus öffnen (z. B. das Custom-Range-Popover der Usage-Stats, `shared/datetime/date-range-popover.ts`), erben deren `z-20`-Kontext und liegen damit über dem Content; Dropdowns im Content (Datetime-Picker im Create-Formular, `z-30` im `z-10`-Karten-Kontext) bleiben unter den Leisten — sie öffnen nach unten, weg davon.
- **Warum feste Höhen:** `sticky` braucht für gestapelte Ebenen exakte `top`-Offsets. `h-14`/`h-10` sind deshalb keine Optik, sondern Berechnungsgrundlage (`top-24` = 14 + 10) — wer sie ändert, zieht alle `top`-Werte nach. Die Filter-Toolbar selbst darf beliebig hoch wrappen, ihr eigener `top` hängt nur von den Ebenen **über** ihr ab.
- **Selektions- und Hover-Zustände malen ausschließlich *innerhalb* der Kartenfläche** — konkret `inset-ring-2 inset-ring-purple-500` statt `ring-2` (Tailwind v4; `ring-inset` gibt es dort nicht mehr). Grund ist genau diese Sticky-Konstruktion: Scroll-Container und Sticky-Leisten sind beide exakt die Content-Box von `<main class="mx-auto max-w-5xl px-4">`, und ein *outset* `ring-2` malt 2 px **außerhalb** der Border-Box. Bei den Karten der ersten und letzten Grid-Spalte lagen diese 2 px damit links und rechts neben dem Hintergrundkasten der Leiste und schimmerten beim Durchscrollen durch. Die Leiste breiter zu machen wurde verworfen: das hätte den Randeffekt nur verdeckt, statt ihn zu vermeiden, und jede künftige Leiste hätte mitziehen müssen. **Neuer Selektionszustand auf einer Karte ⇒ `inset-ring-*`.**
- **Die Fokus-Outline bleibt bewusst outset** (globaler `:focus-visible`-Ring, §10) — sie ist transient und darf an den Randspalten nicht abgeschnitten werden. Genau deshalb steht in `styles.css` weiterhin `contain: layout style` statt CDKs `contain: content` auf `.cdk-virtual-scroll-content-wrapper`: Paint-Containment würde sie kappen. Diese Regel nicht „aufräumen".
- **Referenz:** `web/src/styles.css` (`.app-sticky-bar`, CDK-Containment-Block), `web/src/app/features/admin/admin-audit-log-page.ts` (Toolbar), `web/src/app/features/shell/app-shell.ts` (Header), `usage-stats-page.html` + `vote-session-detail-page.html` (Selektionsring).

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
- [ ] **ARIA-Rollen:** `role="alert"` nur für Fehler (NoticeBanner `error`), `role="status"` für stille Meldungen und Skeleton-Wrapper; `radiogroup` + Roving-Tabindex für SegmentedControl (`shared/ui/segmented-control.ts` wiederverwenden, nicht nachbauen); `ariaCurrentWhenActive="page"` auf aktiven Nav-/Tab-Links.
- [ ] **Feldfehler:** Muster aus 5.3 (`aria-invalid` + `aria-describedby` + Fehler-`id`).
- [ ] **Disabled erklärt sich:** Grund als sichtbarer Text neben dem Button (TypedConfirm-Hint-Muster), nicht nur Ausgrauung.
- [ ] **Dekoratives versteckt:** Emoji-Icons und Skeleton-Schimmer `aria-hidden="true"`.
- [ ] **Accessible Names kurz:** Stretched-Link-Karten lassen den Screenreader nur den kurzen Titel hören (2.3), keine ganze Karte als Linktext.
- [ ] **Kontrast/nativ:** `color-scheme: dark` bleibt auf `body`; Farbpaare der Primitives (Badge-Tones, Banner-Varianten) nicht ad hoc neu mischen.

## 11. Checkliste „Neue UI bauen"

Vor dem Abschluss jeder UI-Änderung abhaken:

1. [ ] **Primitives statt Utility-Ketten:** `appButton`, `StatusBadge`, `NoticeBanner`, `EmptyState`, `SkeletonRows`, `SegmentedControl`, `ConfirmDialog`/`TypedConfirmDialog`, `Pager`, `.app-card*`, `.app-input*` — nichts davon nachbauen.
2. [ ] **Flächen:** `.app-card`; `app-card-interactive` + Stretched-Link-Kontrakt nur bei echter Klickbarkeit (2.3).
3. [ ] **Typo-Skala** eingehalten (Abschnitt 3), Heading-Level folgt Dokumentstruktur.
4. [ ] **Destruktiv-Flow:** `danger`-Auslöser → Dialog → `danger-solid`-Vollzug (4.2).
5. [ ] **Ladezustand:** Skeleton (Seite/Liste) bzw. disabled-Button (Aktion) — kein Lade-Text (6.1).
6. [ ] **Leerzustand:** `EmptyState` mit Warum + CTA (6.2).
7. [ ] **Fehlerpfad:** `apiErrorTranslationKey` + `NoticeBanner error`; Feldfehler nach 5.3.
8. [ ] **i18n:** alle Keys in **beiden** Locales, konstante Labels (9).
9. [ ] **A11y-Checkliste** (10) durchgegangen; neue Formulare mit Label + Feldfehler-ARIA.
10. [ ] **Audit-Harness** gelaufen, Gates grün (12); bei neuen Seiten: Szenario ergänzt.
11. [ ] Konvention geändert/präzisiert? → DECISIONS-Eintrag im selben Commit (CLAUDE.md Regel 3) und dieses Dokument aktualisieren.

## 12. Verifikation per UI-Audit-Harness

- **Was er ist:** Playwright-Harness getrennt von der e2e-Suite: rendert die UI-Zustands-Matrix (~20 Szenarien × 3 Viewports 360/768/1280 × de/en, API gemockt) und schreibt pro Zustand einen Full-Page-Screenshot + JSON-Metriken.
- **Wann laufen lassen:** Nach jeder Layout-/Style-Änderung mit Flächenwirkung, bei jeder neuen Seite (vorher als Szenario in `web/e2e/audit/ui-audit.audit.ts` ergänzen — Route mocken, Edge-Case-Daten mit langen Namen verwenden) und vor Abschluss jeder UI-Welle.
- **Wie:**

  ```
  cd web
  npx playwright test --config=playwright.audit.config.ts
  ```

  (Kein npm-Script; startet selbst `ng serve` auf Port 4300.) Output unter `web/.audit-out/` (gitignored): `shots/<szenario>--<viewport>--<locale>.png`, `metrics/<szenario>--<viewport>--<locale>.json`.
- **Metriken lesen:** Pro JSON-Datei:
  - `horizontalOverflowPx` — horizontaler Seiten-Overflow in px. **Gate: muss 0 sein.**
  - `smallTargetsUnder24` — interaktive Elemente < 24 px (WCAG 2.5.8). **Gate: keine neuen Einträge gegenüber dem letzten Lauf** (bestehende Einträge sind dokumentierte „equivalent target"-Ausnahmen).
  - `targets24to43` — Elemente unter dem 44-px-Komfortziel: beobachten, kein hartes Gate.
  - `beyondRightEdge` — Elemente jenseits der rechten Viewport-Kante: wie Overflow behandeln.
- Screenshots zusätzlich sichten (de **und** en — längere deutsche Strings sind der häufigste Umbruch-Bruch).
- **Referenz:** `web/playwright.audit.config.ts`, `web/e2e/audit/ui-audit.audit.ts`.
