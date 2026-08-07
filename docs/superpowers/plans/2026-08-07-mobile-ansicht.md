# Mobile-Ansicht Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auf Touch-Geräten gibt es keinen 7TV-Schreibzugriff mehr; der Tap auf ein Emote öffnet eindeutig die Detailansicht, die dort als Bottom-Sheet erscheint.

**Architecture:** Ein `PointerModeService` kapselt `(pointer: coarse)` als Signal und ist das einzige Gate. Selektion, Mass-Delete und Restore verschwinden darunter; die ganze Emote-Zelle wird zum Drilldown-Trigger. Der Drilldown bleibt ein CDK-Dialog — nur Pane-Geometrie (per Media Query) und Shell-Chrome (Griff, Drag) ändern sich. Dazu drei eigenständige Bugfixes: fehlender Dialog-Scroll, Sprite-Bildwechsel, Channelzeilen-Umbruch.

**Tech Stack:** Angular 22 (Standalone, Signals, zoneless), Tailwind CSS v4, `@angular/cdk` (Dialog, Virtual Scroll), Transloco, Vitest (via `@angular/build:unit-test`), Playwright.

**Spec:** [`docs/superpowers/specs/2026-08-07-mobile-ansicht-design.md`](../specs/2026-08-07-mobile-ansicht-design.md)

## Global Constraints

- **Nur `web/`.** Kein Backend-Anteil, keine API-Änderung, keine EF-Migration.
- **Angular-Konventionen aus [`web/.claude/CLAUDE.md`](../../../web/.claude/CLAUDE.md)**: Standalone; **kein** `standalone: true`, **kein** explizites `ChangeDetectionStrategy.OnPush` (beides Default ab v22); `input()`/`output()` statt Decorators; `inject()` statt Konstruktor-Injektion; **kein** `@HostBinding`/`@HostListener` — Host-Bindings in das `host`-Objekt; `NgOptimizedImage` für Bilder; `class`/`style`-Bindings statt `ngClass`/`ngStyle`; native Control Flow (`@if`/`@for`).
- **Member-Reihenfolge in Klassen:** `input()`/`output()` → `inject()` → `signal()`/`computed()`/Felder → `constructor()` mit `effect()`s → public/protected Methoden → private Helper.
- **Sprache:** Bezeichner, Typen und Kommentare in neuem Code **englisch**. Projektdokumentation deutsch, Commit-Messages englisch.
- **Commits:** Conventional Commits, mehrere logisch getrennte Commits. **Regel 1 des Repos: vor jedem `git commit` erst den Nutzer fragen.** Die Commit-Schritte unten sind deshalb als „vorschlagen und fragen" zu lesen, nicht als „ausführen".
- **UI-Designsprache** [`docs/UI-Designsprache.md`](../../UI-Designsprache.md) ist verbindlich; `shared/ui/` nicht nachbauen.
- **Formatierung:** nach jeder Task `npm --prefix web run format` und `npm --prefix web run lint`.
- **Testbefehle:** `npm --prefix web test -- --watch=false` (Vitest), `npm --prefix web run e2e` (Playwright). Es gibt **keine** `vitest.config.ts` — die Konfiguration steht in `web/angular.json` unter `test`, Setup in `web/src/test-setup.ts`.
- **i18n-Pflicht (Regel 7):** Jeder neue Übersetzungs-Key braucht denselben Eintrag in `web/public/i18n/de.json` **und** `web/public/i18n/en.json`.
- **Kein neuer Overlay-Stack.** CDK-Dialog bleibt der einzige Weg nach oben.

---

## File Structure

**Neu:**

| Datei | Verantwortung |
|---|---|
| `web/src/app/core/pointer/pointer-mode.service.ts` | Einzige Stelle im Frontend, die `(pointer: coarse)` abfragt. Liefert `isCoarse` als Signal |
| `web/src/app/core/pointer/pointer-mode.service.spec.ts` | Spec dazu |
| `web/src/app/shared/ui/sheet-drag-policy.ts` | Reine Funktion `shouldDismiss(distancePx, velocityPxPerMs)` + Schwellenkonstanten |
| `web/src/app/shared/ui/sheet-drag-policy.spec.ts` | Spec dazu |
| `web/src/app/shared/ui/sheet-drag.ts` | `SheetDrag`-Directive: Pointer-Mechanik, ruft die Policy und `DialogRef.close()` |
| `web/src/app/shared/emotes/emote-sprite.ts` | `<app-emote-sprite>`: zeigt das Bild erst, wenn es für den *aktuellen* URL geladen ist |
| `web/src/app/shared/emotes/emote-sprite.spec.ts` | Spec dazu |
| `web/e2e/touch-mobile.e2e.spec.ts` | Playwright-Spec im Touch-Kontext |

**Geändert:**

| Datei | Änderung |
|---|---|
| `web/src/styles.css` | Pane-Scroll + `max-height`; `@media (pointer: coarse)`-Block für die Sheet-Geometrie; Sheet-Keyframes |
| `web/src/app/shared/ui/button.ts` | `lg`-Größe bekommt `min-h-11` |
| `web/src/app/shared/ui/dialog-shell.ts` | Sheet-Chrome (Griffbalken, obere Rundung) + `SheetDrag` auf der Hülle |
| `web/src/app/features/overview/overview-page.html` | Zeile zweizeilig unterhalb `sm` |
| `web/src/app/features/usage-stats/usage-stats-page.{html,ts}` | Gate, Selektions-Hygiene, Tap-ist-Detail, `EmoteSprite` |
| `web/src/app/features/voting/vote-session-detail-page.{html,ts}` | dieselben vier Punkte |
| `web/src/app/shared/emotes/emote-drilldown-dialog.ts` | `EmoteSprite` im Header |
| `web/e2e/audit/ui-audit.audit.ts` | Fälle für Sheet und zweizeilige Channelzeile |
| `docs/DECISIONS.md`, `docs/UI-Designsprache.md` | Vertrag + §7 |

**Reihenfolge-Logik:** Tasks 1–4 sind voneinander unabhängig und können in beliebiger Folge landen. Ab Task 5 hängt alles an Task 1. Tasks 9–11 hängen zusätzlich an Task 2.

---

## Task 1: PointerModeService

**Files:**
- Create: `web/src/app/core/pointer/pointer-mode.service.ts`
- Test: `web/src/app/core/pointer/pointer-mode.service.spec.ts`

**Interfaces:**
- Consumes: nichts.
- Produces: `PointerModeService` mit `readonly isCoarse: Signal<boolean>`. Alle späteren Tasks lesen ausschließlich dieses Signal.

**Hinweis zum Decorator:** Öffne zuerst `web/src/app/core/theme/theme.service.ts` und übernimm **die dort tatsächlich verwendete Decorator-Form** (`@Service()` oder `@Injectable({ providedIn: 'root' })`). Die Vorlage unten nutzt `@Service()` gemäß `web/.claude/CLAUDE.md`; wenn `theme.service.ts` `@Injectable` verwendet, dann auch hier `@Injectable({ providedIn: 'root' })`.

- [ ] **Step 1: Write the failing test**

`web/src/app/core/pointer/pointer-mode.service.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PointerModeService } from './pointer-mode.service';

/**
 * Same shape as the fake in core/theme/theme.service.spec.ts: jsdom has no matchMedia, and a real
 * listener set is what lets the change and teardown cases assert on behaviour rather than on call
 * counts. The global stub in test-setup.ts always reports `matches: false`, so anything asserting a
 * coarse pointer has to install its own.
 */
class FakeMediaQueryList {
  readonly listeners = new Set<(event: MediaQueryListEvent) => void>();

  constructor(public matches: boolean) {}

  addEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.add(listener);
  }

  removeEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.delete(listener);
  }

  emit(matches: boolean): void {
    this.matches = matches;
    for (const listener of this.listeners) {
      listener({ matches } as MediaQueryListEvent);
    }
  }
}

let coarseQuery: FakeMediaQueryList;
let queriedFor: string[];

function installMatchMedia(coarse: boolean): void {
  coarseQuery = new FakeMediaQueryList(coarse);
  queriedFor = [];
  vi.stubGlobal('matchMedia', (query: string) => {
    queriedFor.push(query);
    return coarseQuery;
  });
}

describe('PointerModeService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('asks for the primary pointing device, not for any of them', () => {
    installMatchMedia(false);
    TestBed.inject(PointerModeService);

    // `any-pointer: coarse` would also be true for a desktop with a touchscreen attached, which is
    // exactly the machine that still has DevTools and must keep the delete engine.
    expect(queriedFor).toContain('(pointer: coarse)');
  });

  it('reports a mouse as not coarse', () => {
    installMatchMedia(false);

    expect(TestBed.inject(PointerModeService).isCoarse()).toBe(false);
  });

  it('reports a finger as coarse', () => {
    installMatchMedia(true);

    expect(TestBed.inject(PointerModeService).isCoarse()).toBe(true);
  });

  it('follows a change of pointing device', () => {
    installMatchMedia(true);
    const service = TestBed.inject(PointerModeService);
    expect(service.isCoarse()).toBe(true);

    // Plugging a mouse into a tablet, or the browser's device emulation being switched off.
    coarseQuery.emit(false);

    expect(service.isCoarse()).toBe(false);
  });

  it('drops the media listener when the injector goes down', () => {
    installMatchMedia(true);
    TestBed.inject(PointerModeService);
    expect(coarseQuery.listeners.size).toBe(1);

    TestBed.resetTestingModule();

    expect(coarseQuery.listeners.size).toBe(0);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm --prefix web test -- --watch=false`
Expected: FAIL — `Failed to resolve import "./pointer-mode.service"`.

- [ ] **Step 3: Write the implementation**

`web/src/app/core/pointer/pointer-mode.service.ts`:

```typescript
import { DestroyRef, Service, inject, signal } from '@angular/core';

/**
 * The primary pointing device, not any attached one. `any-pointer: coarse` is true for a desktop
 * with a touchscreen plugged in — a machine that still has DevTools and therefore still has a way
 * to obtain the 7TV write token, so it must keep the delete engine.
 */
const COARSE_POINTER_QUERY = '(pointer: coarse)';

/**
 * Whether the app is being pointed at with a finger.
 *
 * The single place in the frontend that touches `matchMedia`. It gates capability, not layout:
 * width decides what fits (the sidecar from `lg` up), the pointer decides what can be operated at
 * all. A half-width desktop window has hover, the group-hover trigger and precise clicks — nothing
 * is broken there — while a phone has no hover, no 44 px-safe 20 px target, and no DevTools to read
 * the 7TV token out of local storage.
 *
 * For purely visual hiding prefer Tailwind's `pointer-coarse:` variant; this signal is for the
 * cases where a handler, a service call or an ARIA attribute has to disappear, which CSS cannot do.
 */
@Service()
export class PointerModeService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly coarse = signal(false);

  readonly isCoarse = this.coarse.asReadonly();

  constructor() {
    const query = matchMedia(COARSE_POINTER_QUERY);
    this.coarse.set(query.matches);

    const onChange = (event: MediaQueryListEvent) => this.coarse.set(event.matches);
    query.addEventListener('change', onChange);
    this.destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS, alle fünf Fälle.

- [ ] **Step 5: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/core/pointer/
git commit -m "feat(web): add PointerModeService as the single coarse-pointer gate"
```

---

## Task 2: Dialoge scrollen, lg-Buttons treffen 44 px

Eigenständiger Bugfix, unabhängig von allem anderen. Heute setzt weder Panel noch `DialogShell` noch Inhalt ein `overflow-y`, während CDK den Dokument-Scroll blockiert und das Pane auf `100vh` deckelt — überzähliger Inhalt läuft sichtbar aus dem Panel und ist nicht erreichbar.

**Files:**
- Modify: `web/src/styles.css:549-553` (Regel `.cdk-overlay-pane.app-dialog-panel`)
- Modify: `web/src/app/shared/ui/button.ts:53-56` (`SIZE_CLASSES`)

**Interfaces:**
- Consumes: nichts.
- Produces: `.cdk-overlay-pane.app-dialog-panel` ist ab jetzt der Scroll-Container jedes Dialogs. Task 11 hängt daran (`scrollTop` des Panes ist die Gestenbedingung).

**Warum das Pane und nicht der Shell-Body:** Der Weg von der Pane-Höhe bis zum Inhalt führt über zwei Komponenten-Hosts (`app-<dialog>` und `app-dialog-shell`), die beide `display: inline` sind und die Höhenkette brechen. Das Pane zum Scroll-Container zu machen ist **eine** Regel und braucht keinen einzigen Komponenten-Eingriff. Preis: Kopfzeile und Aktionsreihe scrollen mit. Bei 28 rem Dialogbreite ist das vertretbar; im Sheet bleibt der Griff über `position: sticky` stehen (Task 10).

- [ ] **Step 1: Bestehende Regel ersetzen**

In `web/src/styles.css` diesen Block:

```css
.cdk-overlay-pane.app-dialog-panel {
  width: 100%;
  max-width: min(28rem, calc(100vw - 2rem));
  color: var(--ep-fg);
}
```

ersetzen durch:

```css
/* The pane is the scroll container, deliberately — not the shell's body. Between the pane's height
   and the content sit two component hosts (app-<dialog> and app-dialog-shell), both display:inline
   by default, and a percentage height chain does not survive them. Scrolling the pane is one rule
   and touches no component. The cost is that the heading and the action row scroll along; at 28rem
   that is acceptable, and the sheet's grab handle stays put via position:sticky.

   Without this, CDK blocks the document scroll and caps the pane at 100vh while nothing declares
   overflow — so a dialog taller than the window ran off it and was unreachable. */
.cdk-overlay-pane.app-dialog-panel {
  width: 100%;
  max-width: min(28rem, calc(100vw - 2rem));
  max-height: calc(100dvh - 2rem);
  overflow-y: auto;
  overscroll-behavior: contain;
  color: var(--ep-fg);
}
```

- [ ] **Step 2: `lg`-Buttons auf 44 px**

In `web/src/app/shared/ui/button.ts` den `SIZE_CLASSES`-Block ersetzen:

```typescript
/**
 * `lg` carries a 44 px floor rather than growing its padding: that is the WCAG 2.5.8 target size the
 * design language already demands for popover rows (§7.1), and `lg` is exactly the tier the
 * flow-carrying buttons use — dialog confirms, the mass-delete trigger, the dialog close button that
 * on a phone is the only way out of the sheet other than the backdrop. `md` is unchanged: it sits in
 * dense toolbars where a mouse is the only realistic pointer.
 */
const SIZE_CLASSES: Record<ButtonSize, string> = {
  md: 'px-3 py-1.5',
  lg: 'min-h-11 px-4 py-2',
};
```

- [ ] **Step 3: Suiten laufen lassen**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS (keine bestehende Suite prüft Button-Höhen; ein Fehlschlag hier bedeutet, dass eine Snapshot-artige Zusicherung existiert — dann diese anpassen, nicht die Klasse zurücknehmen).

Run: `npm --prefix web run e2e`
Expected: PASS.

- [ ] **Step 4: Im Browser gegenprüfen**

Run: `npm --prefix web start` (Api parallel auf `:5151`, s. Root-`CLAUDE.md`)
Prüfen: Browserfenster auf ~500 px Höhe verkleinern, auf einer Usage-Stats-Seite einen Emote-Drilldown öffnen. Erwartet: Der Dialog ist innen scrollbar und der Schließen-Button erreichbar. **Vorher** lief er unerreichbar aus dem Panel.

- [ ] **Step 5: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/styles.css web/src/app/shared/ui/button.ts
git commit -m "fix(web): make dialogs scroll instead of overflowing off the pane"
```

---

## Task 3: EmoteSprite

Behebt: Beim Hovern über noch nicht geladene Sprites zeigt das Sidecar das **alte Bild** zu den **neuen Zahlen**. Ursache: `[ngSrc]` auf einem dauerhaft montierten Knoten — `@if (inspected(); as emote)` ist nur eine Null-Prüfung und fällt wegen des `order[0]`-Fallbacks nie auf `null`, also wird beim Hover bloß das Attribut umgebunden und der Browser zeichnet weiter das alte dekodierte Bitmap.

**Files:**
- Create: `web/src/app/shared/emotes/emote-sprite.ts`
- Test: `web/src/app/shared/emotes/emote-sprite.spec.ts`
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html` (Zeilen ~132–139 Readout, ~374–378 Atlas-Zelle, ~475–481 Sidecar)
- Modify: `web/src/app/features/voting/vote-session-detail-page.html` (Zeilen ~234–241 Ballot-Zelle, ~387–394 Sidecar)
- Modify: `web/src/app/shared/emotes/emote-drilldown-dialog.ts` (Zeilen ~64–70)

**Interfaces:**
- Consumes: nichts.
- Produces: `<app-emote-sprite [url] [size] [spriteClass] [dimmed] />` — `url: string` (required), `size: number` (required, **pro Aufrufstelle konstant**), `spriteClass: string` (Default `'h-full w-full object-contain p-1'`), `dimmed: boolean` (Default `false`).

`size` muss pro Aufrufstelle konstant bleiben: `NgOptimizedImage` beanstandet nachträglich geänderte `width`/`height`. Alle sechs Aufrufstellen sind quadratisch (64, 96, 56, 56, 28, 40), ein Wert genügt.

- [ ] **Step 1: Write the failing test**

`web/src/app/shared/emotes/emote-sprite.spec.ts`:

```typescript
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { EmoteSprite } from './emote-sprite';

@Component({
  imports: [EmoteSprite],
  template: `<app-emote-sprite [url]="url()" [size]="64" />`,
})
class Host {
  readonly url = signal('https://cdn.7tv.app/emote/aaa/2x.webp');
}

describe('EmoteSprite', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  function image(): HTMLImageElement {
    return fixture.nativeElement.querySelector('img');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('keeps the image invisible until it has loaded', () => {
    // It stays in the DOM — removing it would mean it never starts loading — and it keeps its box,
    // so revealing it costs no layout shift.
    expect(image().style.visibility).toBe('hidden');
  });

  it('reveals the image once it has loaded', () => {
    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('');
  });

  // The actual bug: the sidecar's <img> is never rebuilt, so a plain [ngSrc] swap left the previous
  // emote's bitmap on screen next to the new emote's numbers.
  it('hides the previous emote again the moment the url changes', () => {
    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(image().style.visibility).toBe('');

    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  it('ignores a load that belongs to a url already superseded', () => {
    const stale = image();
    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();

    // The slow first request finishing after the pointer has already moved on.
    stale.dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  it('leaves a broken image hidden so the plate shows through', () => {
    image().dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm --prefix web test -- --watch=false`
Expected: FAIL — `Failed to resolve import "./emote-sprite"`.

- [ ] **Step 3: Write the implementation**

`web/src/app/shared/emotes/emote-sprite.ts`:

```typescript
import { NgOptimizedImage } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';

/**
 * One emote sprite, drawn only once it belongs to the emote next to it.
 *
 * The sidecar and the readout line bind their `<img>` on a node that is never rebuilt — their `@if`
 * is a null check that, thanks to the `order[0]` fallback, never actually goes null. So a hover only
 * rebound the attribute: every text node beside it re-rendered synchronously while the browser kept
 * painting the previous, already-decoded bitmap until the new one arrived. A wrong picture next to
 * right numbers is misinformation, and on a virtualized, lazily-loading atlas the window in which it
 * happens is real rather than theoretical.
 *
 * Hidden rather than removed: an `<img>` outside the DOM never starts loading, and `visibility`
 * keeps the box so revealing it costs no layout shift. What shows through meanwhile is the caller's
 * `app-sprite-cell` plate, which until now was never visible at all because the stale image covered
 * it. A failed load stays hidden for the same reason — a broken-image glyph says less than the plate.
 *
 * Not wrapped around that plate on purpose: the six call sites size and position their own container
 * (14, 12, 7 and 4 rem boxes, one of them the ballot's `app-sprite-cell-void`), so this owns the
 * picture and nothing else.
 */
@Component({
  selector: 'app-emote-sprite',
  imports: [NgOptimizedImage],
  template: `
    <img
      [ngSrc]="url()"
      [width]="size()"
      [height]="size()"
      alt=""
      [class]="spriteClass()"
      [class.opacity-40]="dimmed()"
      [style.visibility]="settled() ? null : 'hidden'"
      (load)="onSettled(url())"
      (error)="onFailed()"
    />
  `,
  host: { class: 'contents' },
})
export class EmoteSprite {
  readonly url = input.required<string>();
  /**
   * Edge length in px, for the intrinsic size NgOptimizedImage requires. Must be constant per call
   * site — the directive objects to width/height changing after init.
   */
  readonly size = input.required<number>();
  readonly spriteClass = input('h-full w-full object-contain p-1');
  /** Archived ballot members, which stay listed but read as spent. */
  readonly dimmed = input(false);

  private readonly loadedUrl = signal<string | null>(null);

  protected readonly settled = computed(() => this.loadedUrl() === this.url());

  protected onSettled(url: string): void {
    // Compared against the current url rather than trusted: a slow first request can land after the
    // pointer has already moved to the next emote, and that late load must not reveal the wrong art.
    if (url === this.url()) {
      this.loadedUrl.set(url);
    }
  }

  protected onFailed(): void {
    this.loadedUrl.set(null);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS, alle fünf Fälle.

- [ ] **Step 5: Alle sechs Aufrufstellen umstellen**

Jedes `<img [ngSrc]="…">` durch `<app-emote-sprite>` ersetzen. In jeder betroffenen Komponente `EmoteSprite` zu `imports` hinzufügen und den nun ungenutzten `NgOptimizedImage`-Import entfernen, falls die Datei kein weiteres Bild hat.

`usage-stats-page.html` — Readout-Zeile (heutige Zeilen 133–140):

```html
        <span class="app-sprite-cell flex h-7 w-7 shrink-0 items-center justify-center">
          <app-emote-sprite
            [url]="emote.imageUrl"
            [size]="28"
            spriteClass="h-full w-full object-contain p-0.5"
          />
        </span>
```

`usage-stats-page.html` — Atlas-Zelle (heutige Zeilen 373–378):

```html
                      <app-emote-sprite [url]="emote.imageUrl" [size]="64" />
```

`usage-stats-page.html` — Sidecar (heutige Zeilen 474–481):

```html
              <app-emote-sprite [url]="emote.imageUrl" [size]="56" />
```

`vote-session-detail-page.html` — Ballot-Zelle (heutige Zeilen 234–241):

```html
                  <app-emote-sprite
                    [url]="emote.imageUrl"
                    [size]="96"
                    [dimmed]="emote.isArchived"
                  />
```

`vote-session-detail-page.html` — Sidecar (heutige Zeilen 387–394):

```html
              <app-emote-sprite
                [url]="emote.imageUrl"
                [size]="56"
                [dimmed]="emote.isArchived"
              />
```

`emote-drilldown-dialog.ts` — Header (heutige Zeilen 64–70):

```html
          <app-emote-sprite
            [url]="data.imageUrl"
            [size]="40"
            spriteClass="max-h-10 max-w-10 object-contain"
          />
```

`disableOptimizedSrcset` entfällt überall: es stand an zwei von sechs Stellen und ist mangels konfiguriertem `IMAGE_LOADER` (`app.config.ts` provided keinen) ohnehin wirkungslos.

- [ ] **Step 6: Suiten und Browser**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS.

Run: `npm --prefix web run e2e`
Expected: PASS. **Falls der Audit-Fall `usage-stats-drilldown` bricht:** dessen `afterLoad` greift per `[aria-label*="Emote1PogU"]:not([aria-pressed])` — dieser Selektor betrifft Buttons, nicht Bilder, und darf unverändert bleiben.

Im Browser (`npm --prefix web start`): Netzwerk auf „Slow 3G" drosseln, Usage-Stats-Seite frisch laden, zügig über mehrere noch nicht geladene Emotes fahren. Erwartet: Das Sidecar zeigt eine leere Plattenfläche statt des vorigen Bildes.

- [ ] **Step 7: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/shared/emotes/ web/src/app/features/usage-stats/usage-stats-page.html web/src/app/features/voting/vote-session-detail-page.html
git commit -m "fix(web): stop the sidecar showing the previous emote's sprite"
```

---

## Task 4: Channelzeile zweizeilig unterhalb sm

**Files:**
- Modify: `web/src/app/features/overview/overview-page.html:55-60` und `:95`

**Interfaces:**
- Consumes: nichts. Produces: nichts.

Der Umbruch ist heute Zufall: Die rechte Gruppe hat kein `min-w-0`, der Hinweistext `overview.notTrackedYet` kein `truncate`, und der deutsche Satz ist mit 58 Zeichen 26 % länger als der englische. Also fällt die *ganze* rechte Gruppe rechtsbündig auf eine zweite Zeile und bricht dort erneut.

- [ ] **Step 1: Zeilencontainer umstellen**

`overview-page.html`, heutige Zeilen 55–60 ersetzen:

```html
            <!-- Below sm every row takes the same deliberate two-line shape: identity on top,
                 role and action underneath and left-aligned. It used to be one wrapping row, which
                 held for the short branches ("Hinzufügen", "Bot aktiv", both whitespace-nowrap) and
                 broke for the one branch with a sentence in it — the whole right-hand group fell to
                 a second line, right-aligned by ml-auto, and wrapped again inside it. Making the
                 break intentional also makes long channel names and long role chains harmless. -->
            <li
              [class]="
                'flex flex-col items-start gap-x-4 gap-y-2 px-3 py-3 sm:flex-row sm:flex-wrap sm:items-center' +
                (channel.isTracked ? ' relative transition-colors hover:bg-surface-inset' : '')
              "
            >
```

- [ ] **Step 2: Rechte Gruppe umstellen**

`overview-page.html`, heutige Zeile 95 ersetzen:

```html
              <div
                class="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-2 sm:ml-auto sm:justify-end"
              >
```

`min-w-0` neu, `ml-auto`/`justify-end` erst ab `sm` — unterhalb steht die Zeile links, wo die Zeile darüber auch beginnt.

- [ ] **Step 3: Im Browser prüfen**

Run: `npm --prefix web start`
Prüfen: DevTools-Geräteansicht auf 360 px, Seite „Meine Channels". Erwartet: alle Zeilen gleich geformt, der lange Hinweis linksbündig unter dem Namen. Bei ≥ 640 px unverändert einzeilig.

Zusätzlich mit `?lang=en` bzw. über den Sprachumschalter gegenprüfen — der englische Satz ist kürzer und darf ab `sm` weiterhin auf eine Zeile passen.

- [ ] **Step 4: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/features/overview/overview-page.html
git commit -m "fix(web): give the channel row a deliberate two-line shape on narrow screens"
```

---

## Task 5: Schreibsperre auf der Usage-Stats-Seite

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts`
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html` (Readout ~128, Band-Header ~321, Restore-Panel ~578, Dock ~586)

**Interfaces:**
- Consumes: `PointerModeService.isCoarse` (Task 1).
- Produces: `protected readonly isCoarse: Signal<boolean>` auf `UsageStatsPage` — Task 7 baut darauf auf.

- [ ] **Step 1: Signal in die Komponente holen**

In `usage-stats-page.ts` bei den übrigen `inject()`-Aufrufen (vor den Signal-Feldern, s. Member-Reihenfolge):

```typescript
  /**
   * Capability, not layout: no 7TV write access without a mouse. The write token can only be
   * obtained from DevTools' local-storage view on 7tv.app, which a phone does not have — so
   * selection, mass delete and protocol re-import are desktop work, and the tap on a cell is freed
   * up to mean one thing (see onCellClick). Width stays responsible for what fits; this is
   * responsible for what can be operated.
   */
  protected readonly isCoarse = inject(PointerModeService).isCoarse;
```

Import ergänzen:

```typescript
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
```

- [ ] **Step 2: Selektion beim Moduswechsel leeren**

Im `constructor()` von `UsageStatsPage`, zu den übrigen `effect()`s:

```typescript
    // A selection made in a desktop window would otherwise survive invisibly into the touch mode and
    // reappear on the way back.
    effect(() => {
      if (this.isCoarse()) {
        this.selection.clear();
      }
    });
```

- [ ] **Step 3: Die vier Stellen im Template gaten**

**Readout-Zeile** (heutige Zeile 130) — Klassenliste ergänzen:

```html
        class="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-border py-2 text-xs text-fg-muted lg:hidden pointer-coarse:hidden"
```

Der Kommentar darüber (heutige Zeilen 123–127) bekommt einen Zusatzsatz:

```html
         Hidden on a coarse pointer: the sticky bar is `max-sm:static` there, so it scrolls away, and
         the only input that could feed it is the tap — which now opens the drilldown instead. It
         stays for a narrow desktop window, where hover feeds it and the bar really does pin.
```

**„alle auswählen" im Band `dead`** (heutige Zeilen 320–329) — die Bedingung erweitern:

```html
                @if (row.band === 'dead' && !isCoarse()) {
```

**Restore-Panel** (heutige Zeilen 578–583):

```html
  <!-- Gated with the delete engine: re-importing a protocol replays 7TV writes and needs the same
       token, so on a phone it would be the one write path left standing. -->
  @if (activeEmoteSetId(); as setId) {
    @if (!isCoarse()) {
      <app-restore-panel [setId]="setId" [channelName]="channelName()" />
    }
  }
```

**Dock** (heutige Zeile 588) — die Bedingung erweitern:

```html
  @if (dockVisible() && !isCoarse()) {
```

Der Vote-Session-Button ist per `ngProjectAs` ins Dock eingeschossen und verschwindet mit ihm — beabsichtigt: eine Session zu kuratieren ist Bildschirmarbeit, das Abstimmen danach bleibt mobil voll nutzbar.

Token-Dialog, Lösch-Bestätigung und Vote-Session-Anlage brauchen **keine** eigene Sperre — sie hängen alle an einem dieser Einstiege.

Die Slot-Budget-Leiste (heutige Zeilen 208–214) bleibt: sie informiert, sie schreibt nichts.

- [ ] **Step 4: Suiten laufen lassen**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS. Der globale `matchMedia`-Stub in `web/src/test-setup.ts` meldet immer `matches: false`, jede bestehende Suite läuft also weiter im Maus-Modus.

Run: `npm --prefix web run e2e`
Expected: PASS — `devices['Desktop Chrome']` ist `pointer: fine`.

- [ ] **Step 5: Im Browser prüfen**

Run: `npm --prefix web start`
Prüfen: DevTools-Geräteemulation einschalten (das setzt `pointer: coarse`). Erwartet: Kein Dock beim Antippen von Emotes, kein Restore-Panel, kein „alle auswählen" am Band `dead`, keine Readout-Zeile. Emulation aus ⇒ alles wieder da, Auswahl leer.

- [ ] **Step 6: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/features/usage-stats/
git commit -m "feat(web): hide selection and 7TV writes on coarse pointers"
```

---

## Task 6: Schreibsperre auf der Voting-Seite

**Files:**
- Modify: `web/src/app/features/voting/vote-session-detail-page.ts`
- Modify: `web/src/app/features/voting/vote-session-detail-page.html` (Readout ~104, Mass-Delete-Panel ~149)

**Interfaces:**
- Consumes: `PointerModeService.isCoarse` (Task 1).
- Produces: `protected readonly isCoarse: Signal<boolean>` auf `VoteSessionDetailPage` — Task 8 baut darauf auf.

- [ ] **Step 1: Signal und Hygiene-Effect**

In `vote-session-detail-page.ts`, bei den `inject()`-Aufrufen:

```typescript
  /** See UsageStatsPage: no 7TV write access without a mouse. */
  protected readonly isCoarse = inject(PointerModeService).isCoarse;
```

Import:

```typescript
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
```

Im `constructor()`:

```typescript
    effect(() => {
      if (this.isCoarse()) {
        this.selection.clear();
      }
    });
```

- [ ] **Step 2: Readout-Zeile gaten**

Heutige Zeile 106, Klassenliste ergänzen:

```html
        class="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-border py-2 text-xs text-fg-muted lg:hidden pointer-coarse:hidden"
```

- [ ] **Step 3: Mass-Delete-Panel gaten**

Heutige Zeile 149:

```html
  @if (canSelectForDelete() && !isCoarse() && activeEmoteSetId(); as setId) {
```

- [ ] **Step 4: Suiten und Browser**

Run: `npm --prefix web test -- --watch=false` — Expected: PASS
Run: `npm --prefix web run e2e` — Expected: PASS

Im Browser mit Geräteemulation: Auf einer Vote-Session-Detailseite ist kein Mass-Delete-Panel und keine Readout-Zeile sichtbar; der Keep/Delete-Streifen an jeder Zelle bleibt bedienbar.

- [ ] **Step 5: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/features/voting/
git commit -m "feat(web): hide the ballot's delete engine on coarse pointers"
```

---

## Task 7: Tap ist Detail — Usage-Atlas

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts` (`onCellClick`, ~596–600)
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html` (Zelle ~354–416, Drilldown-Trigger ~417–453)

**Interfaces:**
- Consumes: `isCoarse` (Task 5), bestehende `openDrilldown(emote)` und `selection.onRowClick(...)`.
- Produces: nichts für spätere Tasks.

Die 64-px-Zelle wird auf `coarse` zum Drilldown-Trigger; der 20-px-Trigger oben links entfällt dort ersatzlos. 64 px liegen deutlich über den 44 px Mindestgröße.

- [ ] **Step 1: `onCellClick` verzweigen**

`usage-stats-page.ts`, Methode `onCellClick` öffnen. Sie setzt heute `inspectedId`, `activeIndex` und löst danach die Selektion aus. **Unmittelbar vor dem Selektionsaufruf** einfügen:

```typescript
    // On a coarse pointer the cell has only one meaning left. Returning before the selection call
    // rather than gating the whole method keeps inspectedId/activeIndex in sync, which is what the
    // sidecar and the roving tab stop read.
    if (this.isCoarse()) {
      this.openDrilldown(emote);
      return;
    }
```

- [ ] **Step 2: ARIA der Zelle umschalten**

`usage-stats-page.html`, in der Zelle (heutige Zeilen 361–363) ersetzen:

```html
                      [attr.aria-pressed]="isCoarse() ? null : selection.isSelected(emote)"
                      [attr.aria-label]="
                        isCoarse()
                          ? ('usageStats.drilldown.open' | transloco: { name: emote.emoteName })
                          : emote.emoteName + ' · ' + formatCount(emote.totalUseCount) + '×'
                      "
```

- [ ] **Step 3: Den 20-px-Trigger auf coarse entfernen**

`usage-stats-page.html`: Den zweiten `<button>` im Zellen-Wrapper (heutige Zeilen 428–453, der mit `tabindex="-1"` und dem drei-Balken-SVG) in einen `@if`-Block hüllen und die `pointer-coarse:opacity-100`-Klasse aus seiner Klassenliste entfernen — sie hatte genau den Zweck, ihn auf Touch dauerhaft sichtbar zu halten, und ist damit gegenstandslos:

```html
                    @if (!isCoarse()) {
                      <button
                        type="button"
                        [class]="
                          'absolute top-0 left-0 flex h-5 w-5 items-center justify-center opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 ' +
                          (activeIndex() === row.startIndex + i ? 'opacity-100' : '')
                        "
                        tabindex="-1"
                        [style.background-color]="'var(--ep-sprite-scrim)'"
                        [attr.aria-label]="
                          'usageStats.drilldown.open' | transloco: { name: emote.emoteName }
                        "
                        (click)="openDrilldown(emote)"
                      >
                        <svg
                          viewBox="0 0 10 10"
                          class="h-3 w-3 fill-accent-fg"
                          aria-hidden="true"
                          focusable="false"
                        >
                          <rect x="0" y="6" width="2" height="4" />
                          <rect x="4" y="3" width="2" height="7" />
                          <rect x="8" y="0" width="2" height="10" />
                        </svg>
                      </button>
                    }
```

Den Kommentar darüber (heutige Zeilen 417–427) am Ende ergänzen:

```html
                       On a coarse pointer this is gone: with no selection left to compete with, the
                       whole 64 px cell is the trigger, which beats a 20 px overlay by every measure.
```

`onAtlasKeydown` und der Roving-Tabindex bleiben **unverändert**. Auf `fine` gilt weiterhin: Enter öffnet den Drilldown (mit `preventDefault`, damit der native Klick nicht selektiert), Space selektiert. Auf `coarse` öffnen beide den Drilldown — der `preventDefault` im Enter-Zweig muss bleiben, sonst feuerte zusätzlich der native Klick und der Dialog ginge doppelt auf.

- [ ] **Step 4: Suiten und Browser**

Run: `npm --prefix web test -- --watch=false` — Expected: PASS
Run: `npm --prefix web run e2e` — Expected: PASS

Im Browser mit Geräteemulation: Tap auf eine beliebige Atlas-Zelle öffnet den Drilldown; keine Auswahl-Markierung erscheint; oben links sitzt kein kleines Balken-Symbol mehr. Emulation aus: Selektion und der kleine Trigger sind wieder da.

- [ ] **Step 5: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/features/usage-stats/
git commit -m "feat(web): make the whole atlas cell open the drilldown on touch"
```

---

## Task 8: Tap ist Detail — Ballot

**Files:**
- Modify: `web/src/app/features/voting/vote-session-detail-page.ts` (`onCardActivate`, ~353–362)
- Modify: `web/src/app/features/voting/vote-session-detail-page.html` (Sprite-Fläche ~218–232, Drilldown-Trigger ~350–371)

**Interfaces:**
- Consumes: `isCoarse` (Task 6), bestehende `hasUsageData()`, `canSelectForDelete()`, `openDrilldown(emote)`.
- Produces: nichts für spätere Tasks.

Die Sprite-Fläche ist heute nur Klickziel, wenn `canSelectForDelete()`. Auf `coarse` tritt an dessen Stelle `hasUsageData()` — dieselbe Bedingung, die schon heute den Drilldown-Trigger gatet, weil `/usage-stats/daily` hinter dem Usage-Autorisierungsfilter sitzt. Ohne Usage-Daten bleibt die Fläche tot.

- [ ] **Step 1: Ableitung für die Interaktivität**

In `vote-session-detail-page.ts`, bei den `computed()`-Feldern (nach `canSelectForDelete`):

```typescript
  /**
   * What the sprite face does when it is touched or clicked. Two jobs on one surface was fine while
   * hover revealed a separate 20 px drilldown trigger; on a finger it was a coin toss. With the
   * delete engine gone on coarse pointers the face is free, so it carries the drilldown — gated on
   * hasUsageData for the same reason the trigger is: /usage-stats/daily sits behind the usage access
   * filter and a plain voter's tap could only earn a 403.
   */
  protected readonly cellAction = computed<'drilldown' | 'select' | 'none'>(() => {
    if (this.isCoarse()) {
      return this.hasUsageData() ? 'drilldown' : 'none';
    }
    return this.canSelectForDelete() ? 'select' : 'none';
  });
```

- [ ] **Step 2: `onCardActivate` verzweigen**

Am Anfang von `onCardActivate` einfügen (vor allem Bestehenden außer dem `inspect`-Teil — die Methode setzt heute `inspectedId` und löst die Selektion aus; die Verzweigung gehört **nach** dem `inspectedId`-Setzen und **vor** den Selektionsaufruf):

```typescript
    if (this.cellAction() === 'drilldown') {
      this.openDrilldown(emote);
      return;
    }
    if (this.cellAction() !== 'select') {
      return;
    }
```

- [ ] **Step 3: Sprite-Fläche umstellen**

`vote-session-detail-page.html`, heutige Zeilen 218–232 ersetzen:

```html
                <div
                  [attr.role]="cellAction() === 'none' ? null : 'button'"
                  [attr.tabindex]="cellAction() === 'none' ? null : 0"
                  [attr.aria-pressed]="
                    cellAction() === 'select' ? selection.isSelected(emote) : null
                  "
                  [attr.aria-label]="
                    cellAction() === 'drilldown'
                      ? ('usageStats.drilldown.open' | transloco: { name: emote.emoteName })
                      : cellAction() === 'select'
                        ? emote.emoteName
                        : null
                  "
                  [class]="
                    'app-sprite-cell relative block ' +
                    (emote.isArchived ? 'app-sprite-cell-void ' : '') +
                    (cellAction() === 'none' ? '' : 'cursor-pointer ')
                  "
                  [style.height.px]="cellPx()"
                  (click)="onCardActivate(emote, $event)"
                  (mousedown)="canSelectForDelete() && $event.shiftKey && $event.preventDefault()"
                  (keydown.enter)="onCardActivate(emote, $any($event))"
                  (keydown.space)="onCardActivate(emote, $any($event))"
                  (mouseenter)="inspect(emote)"
                >
```

Die Selektions-Markierung darunter (heutige Zeile 250) bleibt an `canSelectForDelete()` gebunden — auf `coarse` ist die Auswahl leer, also zeichnet sie ohnehin nichts.

- [ ] **Step 4: Den 20-px-Trigger auf coarse entfernen**

Heutige Zeile 350, Bedingung erweitern, und `pointer-coarse:opacity-100` aus der Klassenliste in Zeile 353 streichen:

```html
                @if (hasUsageData() && !isCoarse()) {
                  <button
                    type="button"
                    class="absolute top-0 left-0 flex h-5 w-5 items-center justify-center opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
```

- [ ] **Step 5: Suiten und Browser**

Run: `npm --prefix web test -- --watch=false` — Expected: PASS
Run: `npm --prefix web run e2e` — Expected: PASS

Im Browser mit Geräteemulation, als Moderator auf einer Vote-Session mit Usage-Daten: Tap auf die Sprite-Fläche öffnet den Drilldown, die Keep/Delete-Knöpfe darunter stimmen weiterhin ab. Als reiner Voter (ohne Usage-Rechte) ist die Sprite-Fläche tot und trägt kein `role="button"`.

- [ ] **Step 6: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/features/voting/
git commit -m "feat(web): make the ballot sprite open the drilldown on touch"
```

---

## Task 9: Entlassen-oder-zurück als reine Funktion

**Files:**
- Create: `web/src/app/shared/ui/sheet-drag-policy.ts`
- Test: `web/src/app/shared/ui/sheet-drag-policy.spec.ts`

**Interfaces:**
- Consumes: nichts.
- Produces: `shouldDismiss(distancePx: number, velocityPxPerMs: number): boolean` sowie `SHEET_DISMISS_DISTANCE_PX`, `SHEET_DISMISS_VELOCITY_PX_PER_MS`, `SHEET_MIN_TRAVEL_PX`. Task 11 ruft nur `shouldDismiss`.

**Abweichung von der Spec:** §5.4 nennt „Weg ≥ 96 px **oder** Geschwindigkeit ≥ 0,5 px/ms". Wörtlich genommen entließe ein 4-px-Zucken bei hoher Geschwindigkeit das Sheet. Deshalb zusätzlich eine Mindeststrecke von 24 px, unter der gar nichts entlässt. Das gehört in den DECISIONS-Eintrag (Task 12).

- [ ] **Step 1: Write the failing test**

`web/src/app/shared/ui/sheet-drag-policy.spec.ts`:

```typescript
import { describe, expect, it } from 'vitest';

import {
  SHEET_DISMISS_DISTANCE_PX,
  SHEET_DISMISS_VELOCITY_PX_PER_MS,
  SHEET_MIN_TRAVEL_PX,
  shouldDismiss,
} from './sheet-drag-policy';

describe('shouldDismiss', () => {
  it('keeps the sheet when it was barely moved', () => {
    expect(shouldDismiss(10, 0)).toBe(false);
  });

  it('dismisses on distance alone, however slowly it was dragged', () => {
    expect(shouldDismiss(SHEET_DISMISS_DISTANCE_PX, 0)).toBe(true);
  });

  it('dismisses a short but fast flick', () => {
    // The gesture people actually make: a quick flick down, released long before 96 px.
    expect(shouldDismiss(SHEET_MIN_TRAVEL_PX, SHEET_DISMISS_VELOCITY_PX_PER_MS)).toBe(true);
  });

  it('ignores speed below the travel floor', () => {
    // Otherwise a 4 px twitch during a tap — which is fast, because it is short — would close it.
    expect(shouldDismiss(SHEET_MIN_TRAVEL_PX - 1, 99)).toBe(false);
  });

  it('never dismisses on an upward or zero drag', () => {
    expect(shouldDismiss(0, 5)).toBe(false);
    expect(shouldDismiss(-200, 5)).toBe(false);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm --prefix web test -- --watch=false`
Expected: FAIL — `Failed to resolve import "./sheet-drag-policy"`.

- [ ] **Step 3: Write the implementation**

`web/src/app/shared/ui/sheet-drag-policy.ts`:

```typescript
/**
 * Whether a downward drag on a bottom sheet ends in dismissal.
 *
 * Split out of the directive so the decision is testable without a DOM, pointer events or a
 * synthetic clock — the same separation ReconnectPolicy and TwitchWatchdogPolicy have from the
 * transports they steer. The directive owns the mechanics; this owns the judgement.
 *
 * Starting values, to be re-judged on a real device. Distance and speed are alternatives because
 * the two natural gestures are different: a deliberate drag travels far and slowly, a flick travels
 * little and fast. The travel floor is what keeps the flick branch from firing on the few pixels a
 * finger moves during an ordinary tap — short movements are fast movements by definition.
 */
export const SHEET_DISMISS_DISTANCE_PX = 96;
export const SHEET_DISMISS_VELOCITY_PX_PER_MS = 0.5;
export const SHEET_MIN_TRAVEL_PX = 24;

export function shouldDismiss(distancePx: number, velocityPxPerMs: number): boolean {
  if (distancePx < SHEET_MIN_TRAVEL_PX) {
    return false;
  }
  return (
    distancePx >= SHEET_DISMISS_DISTANCE_PX || velocityPxPerMs >= SHEET_DISMISS_VELOCITY_PX_PER_MS
  );
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS, alle fünf Fälle.

- [ ] **Step 5: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/app/shared/ui/sheet-drag-policy.ts web/src/app/shared/ui/sheet-drag-policy.spec.ts
git commit -m "feat(web): add the bottom-sheet dismissal policy"
```

---

## Task 10: Sheet-Erscheinung

**Files:**
- Modify: `web/src/styles.css` (nach dem Dialog-Block aus Task 2)
- Modify: `web/src/app/shared/ui/dialog-shell.ts`

**Interfaces:**
- Consumes: `PointerModeService.isCoarse` (Task 1), die Pane-Scroll-Regel (Task 2).
- Produces: `DialogShell` rendert im Sheet-Modus einen Griff mit `data-sheet-handle`. Task 11 greift genau dieses Attribut.

**Abweichung von der Spec:** §5.1 sah vor, dass `openAppDialog()` den `PointerModeService` liest und die Panel-Klasse wählt. Das geht nicht ohne Umbau: `openAppDialog` ist eine freie Funktion, die aus Komponenten-*Methoden* aufgerufen wird — dort gibt es keinen Injection-Context für `inject()`, und die Alternative wäre, neun `open…()`-Wrapper-Signaturen zu ändern. Die Geometrie erledigt stattdessen eine `@media (pointer: coarse)`-Regel auf der bestehenden Pane-Klasse, `dialog.ts` bleibt **unverändert**. Das ist zugleich robuster: der Modus ist damit live statt beim Öffnen eingefroren. Gehört in den DECISIONS-Eintrag (Task 12).

- [ ] **Step 1: Sheet-Geometrie in `styles.css`**

Direkt hinter die in Task 2 geänderte `.cdk-overlay-pane.app-dialog-panel`-Regel:

```css
/* The same dialog, docked to the bottom edge, whenever it is being pointed at with a finger. Not a
   second overlay system and not a second panel class: two overlay stacks would have to be kept in
   step forever, and the pane class never needs to change — only its geometry does. Driven by a media
   query rather than by a class chosen at open time, so it is also live rather than frozen.

   The wrapper override needs !important: CDK's GlobalPositionStrategy writes align-items as an
   inline style on .cdk-global-overlay-wrapper, and inline styles lose to nothing else. */
@media (pointer: coarse) {
  .cdk-global-overlay-wrapper:has(> .app-dialog-panel) {
    align-items: flex-end !important;
  }

  .cdk-overlay-pane.app-dialog-panel {
    max-width: none;
    max-height: 85dvh;
    transition: transform 220ms cubic-bezier(0.16, 1, 0.3, 1);
    animation: app-sheet-in 260ms cubic-bezier(0.16, 1, 0.3, 1);
  }
}

@keyframes app-sheet-in {
  from {
    transform: translateY(100%);
  }
}

@media (pointer: coarse) and (prefers-reduced-motion: reduce) {
  .cdk-overlay-pane.app-dialog-panel {
    transition: none;
    animation: none;
  }
}
```

- [ ] **Step 2: `DialogShell` um den Sheet-Modus erweitern**

`web/src/app/shared/ui/dialog-shell.ts` vollständig ersetzen:

```typescript
import { Component, computed, inject, input } from '@angular/core';

import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { DIALOG_TITLE_ID } from './dialog';
import { SheetDrag } from './sheet-drag';

/**
 * The inside of every CDK dialog: surface, padding, heading, body, action row.
 *
 * The hull (`rounded-lg bg-surface p-6 shadow-overlay`) stood hand-written in nine components, and
 * around it the details had drifted the way copies do — five heading variants, three heading
 * margins, two of the nine with no heading and therefore no accessible name at all.
 *
 * Spacing is the shell's job, not the caller's: the body is a flex column, so a dialog composes its
 * content out of siblings instead of stacking `mb-*` on each one. Content that belongs together
 * more tightly than the default rhythm wraps itself in its own `flex flex-col gap-1`.
 *
 * The width comes from `.cdk-overlay-pane.app-dialog-panel` (styles.css) — the pane owns it, not the
 * content. Three dialogs used to also set their own `w-[26rem]`/`w-[28rem]`, which either matched
 * the pane cap or silently fought it.
 *
 * On a coarse pointer the same dialog is a bottom sheet: the pane's geometry comes from a media
 * query in styles.css, and what is added here is the chrome that only a sheet has — a grab handle
 * and the drag that dismisses it. Sticky, because the pane is the scroll container, and a handle
 * that scrolls out of reach is not a handle.
 */
@Component({
  selector: 'app-dialog-shell',
  imports: [SheetDrag],
  template: `
    <div [class]="hullClasses()" [appSheetDrag]="isSheet()">
      @if (isSheet()) {
        <div
          data-sheet-handle
          class="sticky -top-6 -mx-6 -mt-6 flex touch-none justify-center bg-surface pt-2 pb-3"
          aria-hidden="true"
        >
          <span class="h-1 w-9 rounded-full bg-border-strong"></span>
        </div>
      }

      <!-- For a heading that is more than text (the drilldown's emote thumbnail). The projected
           markup carries id="app-dialog-title" on its own h2 — see DIALOG_TITLE_ID. -->
      <ng-content select="[dialog-header]" />
      @if (dialogTitle(); as title) {
        <h2 [id]="titleId" class="text-lg font-semibold text-balance text-fg">{{ title }}</h2>
      }

      <div class="flex flex-col gap-3"><ng-content /></div>

      <!-- Cancel goes first, always: the CDK's first-tabbable autoFocus default then lands on the
           harmless control, which is what makes an explicit cdkFocusInitial unnecessary. -->
      <div class="flex flex-wrap items-center justify-end gap-2">
        <ng-content select="[dialog-actions]" />
      </div>
    </div>
  `,
})
export class DialogShell {
  /** Already translated. Omitted only when a `[dialog-header]` renders the heading instead. */
  readonly dialogTitle = input<string>();

  private readonly pointerMode = inject(PointerModeService);

  protected readonly isSheet = this.pointerMode.isCoarse;

  protected readonly hullClasses = computed(
    () =>
      'flex flex-col gap-4 bg-surface p-6 shadow-overlay ' +
      (this.isSheet() ? 'rounded-t-2xl' : 'rounded-lg'),
  );

  protected readonly titleId = DIALOG_TITLE_ID;
}
```

`touch-none` auf dem Griff verhindert, dass der Browser die Abwärtsbewegung als Scroll deutet, bevor die Geste greift.

- [ ] **Step 3: Suiten laufen lassen**

Run: `npm --prefix web test -- --watch=false`
Expected: FAIL — `SheetDrag` existiert noch nicht (Task 11). Das ist erwartet; Task 10 und 11 landen in einem Commit. Wer sie einzeln prüfen will, legt zuerst Task 11 Step 1 an.

- [ ] **Step 4: Weiter zu Task 11**

Kein eigener Commit — Task 11 committet beide.

---

## Task 11: SheetDrag-Directive

**Files:**
- Create: `web/src/app/shared/ui/sheet-drag.ts`

**Interfaces:**
- Consumes: `shouldDismiss` (Task 9), das `data-sheet-handle`-Attribut (Task 10), die Pane-Scroll-Regel (Task 2), `DialogRef` aus `@angular/cdk/dialog`.
- Produces: `SheetDrag` mit dem Input `appSheetDrag: boolean`.

- [ ] **Step 1: Write the implementation**

`web/src/app/shared/ui/sheet-drag.ts`:

```typescript
import { DialogRef } from '@angular/cdk/dialog';
import { Directive, ElementRef, inject, input } from '@angular/core';

import { shouldDismiss } from './sheet-drag-policy';

/**
 * Drag-to-dismiss for the bottom-sheet form of a dialog.
 *
 * Applied to the shell's hull but transforming the overlay pane, because the pane is what the
 * geometry and the scrolling live on (styles.css) — moving the hull inside a scrolling pane would
 * fight that scroll rather than replace it. `closest` rather than an injected reference: the pane is
 * CDK's element, created outside the app's DOM, and there is no token for it.
 *
 * The gesture only starts on the handle or with the pane scrolled to the top. Otherwise a downward
 * drag means "scroll the content up", and taking it would make a long sheet unreadable.
 *
 * Dismissal goes through DialogRef.close(), so it is not a fourth way out — backdrop tap and Escape
 * are untouched, and every consumer's close handling keeps working unchanged.
 */
@Directive({
  selector: '[appSheetDrag]',
  host: {
    '(pointerdown)': 'onPointerDown($event)',
    '(pointermove)': 'onPointerMove($event)',
    '(pointerup)': 'onPointerEnd($event)',
    '(pointercancel)': 'onPointerEnd($event)',
  },
})
export class SheetDrag {
  /** Off while the dialog is a centred card — there is nothing to drag it out of. */
  readonly appSheetDrag = input(false);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialogRef = inject(DialogRef, { optional: true });

  private pane: HTMLElement | null = null;
  private pointerId: number | null = null;
  private startY = 0;
  private startedAt = 0;
  private distance = 0;

  protected onPointerDown(event: PointerEvent): void {
    if (!this.appSheetDrag() || this.pointerId !== null) {
      return;
    }

    const pane = this.host.nativeElement.closest<HTMLElement>('.cdk-overlay-pane');
    if (!pane) {
      return;
    }

    const target = event.target as HTMLElement | null;
    const onHandle = target?.closest('[data-sheet-handle]') != null;
    if (!onHandle && pane.scrollTop > 0) {
      return;
    }

    this.pane = pane;
    this.pointerId = event.pointerId;
    this.startY = event.clientY;
    this.startedAt = event.timeStamp;
    this.distance = 0;
    // The spring-back transition would otherwise animate every move event.
    pane.style.transition = 'none';
    this.host.nativeElement.setPointerCapture(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.pointerId !== event.pointerId || !this.pane) {
      return;
    }

    // No resistance upwards: a sheet that follows the finger past its docked edge reads as broken.
    this.distance = Math.max(0, event.clientY - this.startY);
    this.pane.style.transform = `translateY(${this.distance}px)`;
  }

  protected onPointerEnd(event: PointerEvent): void {
    if (this.pointerId !== event.pointerId || !this.pane) {
      return;
    }

    const pane = this.pane;
    const distance = this.distance;
    // Guarded against 0 so a same-timestamp release cannot produce Infinity.
    const elapsedMs = Math.max(1, event.timeStamp - this.startedAt);

    this.pane = null;
    this.pointerId = null;
    pane.style.transition = '';

    if (shouldDismiss(distance, distance / elapsedMs)) {
      this.dialogRef?.close();
      return;
    }

    pane.style.transform = '';
  }
}
```

- [ ] **Step 2: Suiten laufen lassen**

Run: `npm --prefix web test -- --watch=false`
Expected: PASS. Der globale `matchMedia`-Stub meldet `matches: false`, also rendert `DialogShell` in allen bestehenden Specs weiter die zentrierte Form ohne Griff.

Run: `npm --prefix web run e2e`
Expected: PASS.

- [ ] **Step 3: Auf einem echten Gerät prüfen**

Run: `npm --prefix web start -- --host 0.0.0.0`
Vom Handy im selben Netz die Dev-URL öffnen, eine Usage-Stats-Seite laden, ein Emote antippen. Zu prüfen:

1. Das Sheet fährt von unten ein und sitzt bündig an der Unterkante.
2. Der Griff bleibt beim Scrollen des Inhalts oben stehen.
3. Am Griff nach unten ziehen und loslassen ⇒ es schließt.
4. Wenig ziehen und loslassen ⇒ es federt zurück.
5. Inhalt nach unten scrollen, dann im Inhalt nach unten ziehen ⇒ es scrollt, das Sheet bewegt sich **nicht**.
6. Wieder ganz nach oben scrollen, dann im Inhalt ziehen ⇒ jetzt bewegt sich das Sheet.
7. Backdrop antippen ⇒ es schließt. Schließen-Button ist mindestens 44 px hoch.

Fühlen sich die Schwellen falsch an, `SHEET_DISMISS_DISTANCE_PX` / `SHEET_DISMISS_VELOCITY_PX_PER_MS` in `sheet-drag-policy.ts` anpassen — die Specs prüfen das Verhalten der Funktion, nicht die konkreten Zahlen, und bleiben grün.

- [ ] **Step 4: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/src/styles.css web/src/app/shared/ui/dialog-shell.ts web/src/app/shared/ui/sheet-drag.ts
git commit -m "feat(web): present dialogs as a draggable bottom sheet on touch"
```

---

## Task 12: Absicherung und Dokumentation

**Files:**
- Create: `web/e2e/touch-mobile.e2e.spec.ts`
- Modify: `web/playwright.config.ts` (zweites Projekt)
- Modify: `web/e2e/audit/ui-audit.audit.ts` (zwei Fälle)
- Modify: `docs/DECISIONS.md`
- Modify: `docs/UI-Designsprache.md`

**Interfaces:**
- Consumes: alles Vorherige. Produces: nichts.

- [ ] **Step 1: Touch-Projekt in der Playwright-Config**

In `web/playwright.config.ts` das `projects`-Array ersetzen:

```typescript
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    // A second project rather than a per-test context: `pointer: coarse` follows from the device
    // descriptor's hasTouch/isMobile, and those are context-level options that a test cannot set for
    // itself. Only touch-mobile.e2e.spec.ts runs here — every other spec asserts desktop behaviour
    // and would have to be rewritten for a viewport it was never about.
    {
      name: 'mobile-chrome',
      use: { ...devices['Pixel 5'] },
      testMatch: /touch-mobile\.e2e\.spec\.ts/,
    },
  ],
```

Und im ersten Projekt die Touch-Datei ausschließen:

```typescript
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      testIgnore: /touch-mobile\.e2e\.spec\.ts/,
    },
```

- [ ] **Step 2: Write the failing test**

`web/e2e/touch-mobile.e2e.spec.ts`:

```typescript
import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

// The contract this file exists for: no 7TV write access without a mouse. The token can only be read
// out of DevTools' local-storage view on 7tv.app, which a phone does not have — so selection, mass
// delete and protocol re-import are desktop work, and the tap on a cell means one thing.
test.describe('touch: reading and voting only', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageChannelSeries(page, 'sensitron');
    await mockUsageDaily(page, 'sensitron', [
      { date: '2026-07-02', useCount: 4 },
      { date: '2026-07-05', useCount: 19 },
    ]);
  });

  test('tapping a cell opens the drilldown instead of selecting it', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron');
    await page.goto('/channels/sensitron/usage-stats');

    const cell = page.locator('[data-atlas-index="0"]');
    await expect(cell).toBeVisible();
    // The tell that the cell is no longer a toggle: on a mouse it carries aria-pressed.
    await expect(cell).not.toHaveAttribute('aria-pressed', /.*/);

    await cell.tap();

    await expect(page.locator('#app-dialog-title')).toBeVisible();
  });

  test('the dock and the delete engine never appear', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron');
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await page.keyboard.press('Escape');

    // Marking is what used to raise the dock; with selection gone there is nothing to raise it.
    await expect(page.getByRole('button', { name: /Löschen/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Protokoll/ })).toHaveCount(0);
  });

  test('the drilldown arrives as a sheet docked to the bottom edge', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron');
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    const pane = page.locator('.cdk-overlay-pane.app-dialog-panel');
    const paneBox = (await pane.boundingBox())!;
    const viewport = page.viewportSize()!;

    // Flush with the bottom, full width — the centred card is neither.
    expect(paneBox.y + paneBox.height).toBeGreaterThanOrEqual(viewport.height - 1);
    expect(paneBox.width).toBe(viewport.width);
  });

  test('the sheet closes when the backdrop is tapped', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron');
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    await page.locator('.app-dialog-backdrop').tap({ position: { x: 10, y: 10 } });

    await expect(page.locator('#app-dialog-title')).toHaveCount(0);
  });
});
```

**Vor dem Schreiben** `web/e2e/support/mocks.ts` öffnen und die tatsächlichen Signaturen von `mockUsageTotals`, `mockChannelStatus`, `mockActiveEmoteSet` und `mockUsageChannelSeries` übernehmen — `ui-audit.audit.ts` ruft `mockUsageTotals(page, 'sensitron', usageEmotes(24))` mit einem dritten Argument, das hier gebraucht werden könnte. Die Assertions bleiben wie oben.

Die Drag-Geste selbst wird hier **nicht** geprüft: die Entscheidung liegt in `shouldDismiss` (Task 9) und die Pointer-Mechanik wird live verifiziert (Task 11, Step 3).

- [ ] **Step 3: Run the touch spec**

Run: `npm --prefix web run e2e -- --project=mobile-chrome`
Expected: PASS, alle vier Fälle.

Run: `npm --prefix web run e2e`
Expected: PASS, beide Projekte.

- [ ] **Step 4: Audit-Fälle ergänzen**

In `web/e2e/audit/ui-audit.audit.ts` neben dem Fall `usage-stats-drilldown` einen zweiten anlegen. Der Harness rendert bereits auf 360 × 800; der Unterschied ist die Zeigerart, die dort nicht gesetzt ist — deshalb dokumentiert der Fall, was er zeigt und was nicht:

```typescript
  {
    // The overview's two-line row shape below sm. The long "not tracked yet" sentence is the branch
    // that used to drop the whole right-hand group onto a second, right-aligned line and wrap again
    // inside it; German is 26 % longer than English, so this is the locale that shows it.
    slug: 'overview-narrow-rows',
    path: '/channels',
    setup: async (page) => {
      await authedShell(page);
    },
  },
```

**Abweichung von der Spec:** §9 verlangte einen Audit-Fall für das Sheet. Der entfällt — der Audit-Harness setzt keine Zeigerart, dort erschiene der Dialog weiterhin zentriert, und ein Screenshot, der die zentrierte Form zeigt und „Sheet" heißt, ist schlimmer als keiner. Der Sheet-Zustand bleibt dem Playwright-Touch-Projekt vorbehalten. Gehört in den DECISIONS-Eintrag (Task 12) — dort steht die Begründung bereits.

Run: `npx --prefix web playwright test --config=playwright.audit.config.ts`
Expected: Screenshots in `web/.audit-out/`, Fall `overview-narrow-rows` in allen drei Viewports vorhanden.

- [ ] **Step 5: DECISIONS-Eintrag**

Neuer Eintrag oben in `docs/DECISIONS.md` (absteigend nach Datum), mit `**Betrifft:**`-Zeile. Inhalt, in eigenen Worten:

- **Der Vertrag**: kein 7TV-Schreibzugriff ohne Maus. Was darunter fällt (Selektion, Mass-Delete, Restore-Panel, Vote-Session-Anlage) und was ausdrücklich bleibt (Lesen, Drilldown, Abstimmen, Export, Slot-Budget-Leiste).
- **Warum die Zeigerart und nicht die Breite**: Ein halbiertes Desktop-Fenster hat Hover, den Group-Hover-Trigger und präzise Klicks. Das Token hängt an DevTools, nicht an Pixeln. `pointer: coarse` statt `any-pointer: coarse`, damit ein Desktop mit angestecktem Touchscreen alles behält.
- **Warum kein zweiter Overlay-Stack**: Die Sheet-Geometrie ist eine Media-Query auf der bestehenden Pane-Klasse; `dialog.ts` bleibt unverändert. Abweichung von Spec §5.1 mit Begründung (freie Funktion ohne Injection-Context; als Nebeneffekt live statt beim Öffnen eingefroren).
- **Warum das Pane der Scroll-Container ist** und nicht der Shell-Body: zwei `display: inline`-Komponenten-Hosts brechen die Höhenkette. Preis: Kopf und Aktionsreihe scrollen mit.
- **Die Mindeststrecke von 24 px** in `shouldDismiss`, die die Spec nicht vorsah: kurze Bewegungen sind per Definition schnell, sonst schlösse ein Zucken beim Tippen das Sheet.
- **Warum der Drilldown-Dialog bleibt**: Auf der Voting-Seite ist er die einzige Quelle für Kurve, Peak, Live-Tage und First/Last-Used — die Seite ruft keinen Usage-Endpunkt auf. `firstUsedDate` existiert nur in `/usage-stats/daily`.
- **Kein Hinweistext auf Touch**, dass Löschen am Rechner passiert — visuell fehlt nichts.

- [ ] **Step 6: UI-Designsprache ergänzen**

In `docs/UI-Designsprache.md`:

- **§7 (Dialoge)**: Das Sheet als zweite Erscheinung desselben Overlays aufnehmen — Media-Query-getrieben, Griff mit `data-sheet-handle`, `85dvh`, Pane als Scroll-Container. Die bestehende Aussage „Die Breite gehört dem Pane … nie dem Inhalt" bleibt gültig und wird um die Höhe erweitert.
- **§7.1**: Der 20-px-Drilldown-Trigger entfällt auf `coarse`; dort ist die ganze Zelle das Ziel. Die 44-px-Forderung gilt jetzt über `min-h-11` an der Button-Größe `lg` statt nur für Popover-Zeilen.
- Falls das Dokument eine Stelle zu Sidecar/Dock/Selektion führt: den Zeigermodus als Gate dort nennen.

- [ ] **Step 7: Format, lint, Commit vorschlagen**

```bash
npm --prefix web run format && npm --prefix web run lint
git add web/e2e/ web/playwright.config.ts docs/DECISIONS.md docs/UI-Designsprache.md
git commit -m "test(web): cover the touch contract and record the decision"
```

---

## Abschluss

- [ ] **Vollständiger Durchlauf**

```bash
npm --prefix web test -- --watch=false
npm --prefix web run e2e
npm --prefix web run lint
npm --prefix web run format:check
dotnet build EmotePurge.slnx
```

Alle fünf müssen grün sein. `dotnet build` steht mit dabei, weil der Api-Docker-Build `web/` in seine `web-build`-Stage zieht — ein gebrochener Frontend-Build fiele sonst erst beim Deploy auf.

- [ ] **Live-Gegenprobe auf einem echten Gerät** (Regel 16 sinngemäß): Übersicht, Usage-Stats, eine Vote-Session — Tap, Sheet, Drag, Abstimmen. Emulation reicht dafür nicht.
