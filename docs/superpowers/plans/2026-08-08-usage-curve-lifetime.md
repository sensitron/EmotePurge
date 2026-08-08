# Nutzungskurve sagt nur, was sie weiß — Umsetzungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Die Nutzungskurve zeichnet die Zeit vor dem Hinzufügen eines Emotes nicht mehr als
Nulllinie, und die Zeile unter der Kurve sagt etwas über *dieses* Emote statt über den Channel.

**Architecture:** Reine Frontend-Arbeit. Zwei pure Helfer in
`web/src/app/shared/emotes/usage-series.ts` bekommen einen optionalen `drawFrom`-Tag bzw. entstehen
neu; die Sparkline reicht ihn durch; die beiden Aufrufstellen (Sidecar der Usage-Seite,
Drilldown-Dialog) liefern ihn aus `firstSeenAt` und tauschen ihre Beschriftungszeile. Die
channelweite Live-Zahl wandert einmalig an die Tracking-Zeile im Seitenkopf. **Kein Backend-Eingriff,
keine Migration** — alle Daten liegen in beiden Oberflächen bereits vor.

**Tech Stack:** Angular 22 (Standalone, Signals, zoneless), Tailwind, Transloco, Vitest über
`@angular/build:unit-test`, Playwright.

**Spec:** [`docs/superpowers/specs/2026-08-08-usage-curve-lifetime-design.md`](../specs/2026-08-08-usage-curve-lifetime-design.md)
· **Prototyp:** `docs/superpowers/prototypes/2026-08-08-usage-curve.html` (führt genau diese Logik aus)

## Global Constraints

- **Regel 1: vor jedem `git commit` den Nutzer fragen.** Der Plan nennt die Commit-Befehle, aber
  keiner davon wird ohne Rückfrage ausgeführt.
- **Regel 2: Conventional Commits**, mehrere logisch getrennte Commits statt eines Sammel-Commits.
- **Regel 3:** Der Commit, der eine Konvention ändert, enthält seinen `docs/DECISIONS.md`-Eintrag im
  selben Commit (hier: Task 6).
- **Regel 12: keine isolierten Komponententests.** Neue *pure* Utilities in `shared/` bekommen einen
  co-located `*.spec.ts`; Komponenten selbst werden über Playwright abgedeckt. **Es entsteht kein
  `usage-sparkline.spec.ts`.**
- **Regel 18:** `npm --prefix web run format` (Prettier) und `npm --prefix web run lint` müssen grün
  sein, bevor committet wird.
- **i18n:** Jeder neue Key steht in **beiden** Dateien, `web/public/i18n/de.json` **und**
  `web/public/i18n/en.json`, an derselben Stelle — die beiden Dateien laufen Zeile für Zeile parallel
  und sollen es bleiben. Der Paritätstest greift nur für `ApiErrorCodes`; hier ist es Disziplin.
- **Die E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht.** Antwortet dort eine echte Api mit
  401, schickt der `apiAuthInterceptor` die App auf die Login-Seite und rund die halbe Suite fällt
  mit „element not found" durch — quer über Dateien, die mit der Änderung nichts zu tun haben. Vor
  jedem Playwright-Lauf ein laufendes `dotnet run` beenden.
- **Der Zeitraum in den E2E-Tests ist „alle Zeit"** und läuft von `trackedSince` des Mocks
  (`2026-06-12T09:14:00Z`, s. `mockActiveEmoteSet`) bis heute. Tageszahlen dürfen deshalb **nicht**
  hart geprüft werden, Offsets ab `from` schon: Offset 0 = `2026-06-12`, Offset 3 = `2026-06-15`.
- **Befehle** (aus `web/` heraus oder mit `--prefix web`):
  - eine Vitest-Datei: `npm --prefix web test -- --watch=false --include="src/app/shared/emotes/usage-series.spec.ts"`
  - auf Testnamen einengen: zusätzlich `--filter="liveDayCoverage"` (**nicht** `-t`/`--grep`, die
    kennt dieser Builder nicht)
  - ein Playwright-Test: `npm --prefix web run e2e -- usage-atlas.e2e.spec.ts -g "the curve states its scale"`

## File Structure

| Datei | Verantwortung |
|---|---|
| `web/src/app/shared/emotes/usage-series.ts` | **geändert** — pure Geometrie und Zählung zwischen Server-Antwort und Sparkline. Bekommt `drawFrom` in `toPolylinePoints`/`seriesPeak` und die zwei neuen Funktionen `liveDayCoverage`/`liveDayCaptionKey` |
| `web/src/app/shared/emotes/usage-series.spec.ts` | **geändert** — deckt die neuen Fälle ab |
| `web/src/app/shared/emotes/usage-sparkline.ts` | **geändert** — neues Input `drawFrom`, sonst unverändert |
| `web/public/i18n/de.json`, `en.json` | **geändert** — neuer `usageStats.chart`-Block, neuer `usageStats.liveDaysInRange`, `usageStats.drilldown.liveDays` entfällt |
| `web/src/app/features/usage-stats/usage-stats-page.ts` + `.html` | **geändert** — Sidecar-Zeile, Tracking-Zeile, `drawFrom` |
| `web/src/app/shared/emotes/emote-drilldown-dialog.ts` | **geändert** — dieselbe Zeile, dasselbe `drawFrom` |
| `web/e2e/usage-atlas.e2e.spec.ts` | **geändert** — ein Test umgeschrieben, drei neu |
| `docs/UI-Designsprache.md`, `docs/DECISIONS.md` | **geändert** — Task 6 |

Warum `liveDayCaptionKey` **in `usage-series.ts`** liegt und nicht je Komponente: Sidecar und Dialog
müssen denselben Satz sagen. Zwei Kopien derselben Fallunterscheidung driften auseinander, sobald
jemand nur eine anfasst — und die drei Formen sind genau die Stelle, an der ein Fehler wieder eine
unbelegte Aussage produzieren würde. Eine Funktion, ein Spec, zwei Aufrufer.

---

### Task 1: `drawFrom` in `toPolylinePoints` und `seriesPeak`

**Files:**
- Modify: `web/src/app/shared/emotes/usage-series.ts:62-89` (`toPolylinePoints`), `:136-147` (`seriesPeak`)
- Test: `web/src/app/shared/emotes/usage-series.spec.ts` (bestehende Datei erweitern)

**Interfaces:**
- Consumes: nichts
- Produces:
  - `toPolylinePoints(points: readonly SparklinePoint[], width: number, height: number, drawFrom?: string): string`
  - `seriesPeak(points: readonly SparklinePoint[], drawFrom?: string): { useCount: number; date: string } | null`
  - `drawFrom` ist ein ISO-Tag `yyyy-MM-dd`, **kein** Timestamp.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

An `describe('toPolylinePoints', …)` in `web/src/app/shared/emotes/usage-series.spec.ts` anhängen
(innerhalb des bestehenden `describe`-Blocks, nach dem letzten `it`):

```ts
  it('leaves out the days before drawFrom without moving the rest', () => {
    const points = fillDailySeries(
      [
        { date: '2026-07-03', useCount: 10 },
        { date: '2026-07-04', useCount: 5 },
      ],
      '2026-07-01',
      '2026-07-05',
    );
    // 5 points over width 100 → stepX 25, so the 03. sits at x=50 with or without drawFrom. That is
    // the whole point: the curve must stay aligned with the live bands underneath it, which keep
    // spanning the full range.
    expect(toPolylinePoints(points, 100, 40, '2026-07-03')).toBe('50,0 75,20 100,40');
  });

  it('changes nothing when drawFrom lies before the range', () => {
    const points = fillDailySeries(
      [{ date: '2026-07-02', useCount: 8 }],
      '2026-07-01',
      '2026-07-03',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-06-01')).toBe(toPolylinePoints(points, 100, 40));
  });

  it('draws nothing when drawFrom lies after the range', () => {
    const points = fillDailySeries(
      [{ date: '2026-07-02', useCount: 8 }],
      '2026-07-01',
      '2026-07-03',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-08-01')).toBe('');
  });

  it('gives a single visible day one day-step instead of the full width', () => {
    // The emote added today: one drawable day. The old single-point branch paints the full width,
    // which would claim the whole range again — the exact statement this change removes.
    const points = fillDailySeries(
      [{ date: '2026-07-05', useCount: 3 }],
      '2026-07-01',
      '2026-07-05',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,0 100,0');
  });

  it('keeps a single unused visible day on the baseline', () => {
    const points = fillDailySeries([], '2026-07-01', '2026-07-05');
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,40 100,40');
  });
```

An `describe('seriesPeak', …)` anhängen:

```ts
  it('ignores the days before drawFrom, so the axis matches the curve', () => {
    // In practice the leading stretch is all zeroes, so this changes no pixel today. It is here to
    // keep the y-axis label and the drawn line reading the same set of days on purpose rather than
    // by accident.
    const points = [
      { date: '2026-07-01', useCount: 90 },
      { date: '2026-07-02', useCount: 4 },
    ];
    expect(seriesPeak(points, '2026-07-02')).toEqual({ useCount: 4, date: '2026-07-02' });
  });
```

- [ ] **Step 2: Tests laufen lassen und Fehlschlag bestätigen**

```
npm --prefix web test -- --watch=false --include="src/app/shared/emotes/usage-series.spec.ts"
```

Erwartet: FAIL. Die neuen Fälle laufen gegen die alte Signatur, `drawFrom` wird ignoriert — z. B.
`expected '0,40 25,40 50,0 75,20 100,40' to be '50,0 75,20 100,40'`. Der Fall „draws nothing" und die
beiden Stummel-Fälle scheitern ebenfalls. **Die 21 bestehenden Tests müssen dabei grün bleiben.**

- [ ] **Step 3: `toPolylinePoints` umbauen**

`web/src/app/shared/emotes/usage-series.ts:62-89` vollständig ersetzen durch:

```ts
/**
 * SVG polyline points in a 0..width / 0..height viewBox, y inverted (0 at the bottom edge). The
 * maximum is clamped to >= 1 so an all-zero series draws a flat baseline instead of dividing by
 * zero. A single point renders as a full-width flat line — one dot would be invisible.
 *
 * `drawFrom` (ISO day) is the first day the line may speak for: everything before it is left
 * undrawn rather than drawn as zero, because a baseline over days the emote did not exist on reads
 * as "unused" and is the opposite verdict. The x mapping keeps counting from the start of the array
 * regardless, so the curve stays aligned with the live bands, which span the whole range.
 */
export function toPolylinePoints(
  points: readonly SparklinePoint[],
  width: number,
  height: number,
  drawFrom?: string,
): string {
  if (points.length === 0) {
    return '';
  }

  // Plain string comparison: for `yyyy-MM-dd` the lexicographic order is the chronological one, and
  // the rest of this file counts UTC days the same way.
  const firstVisible = drawFrom ? points.findIndex((point) => point.date >= drawFrom) : 0;
  if (firstVisible === -1) {
    return '';
  }

  const visible = points.slice(firstVisible);
  const max = Math.max(1, ...visible.map((point) => point.useCount));
  const yOf = (useCount: number) => round(height - (useCount / max) * height);

  if (points.length === 1) {
    const y = yOf(points[0].useCount);
    return `0,${y} ${round(width)},${y}`;
  }

  const stepX = width / (points.length - 1);

  // A polyline with one coordinate draws nothing at all, so a single visible day gets one day-step
  // centred on its own position — never the full width, which is what the branch above does for a
  // one-day range and what would re-state the whole span here.
  if (visible.length === 1) {
    const x = firstVisible * stepX;
    const y = yOf(visible[0].useCount);
    return `${round(Math.max(0, x - stepX / 2))},${y} ${round(Math.min(width, x + stepX / 2))},${y}`;
  }

  return visible
    .map((point, index) => `${round((firstVisible + index) * stepX)},${yOf(point.useCount)}`)
    .join(' ');
}
```

- [ ] **Step 4: `seriesPeak` umbauen**

`web/src/app/shared/emotes/usage-series.ts:136-147` vollständig ersetzen durch:

```ts
/**
 * The busiest day; on a tie the earliest wins. `null` for an empty or all-zero series. `drawFrom`
 * excludes the days the curve does not draw, so the axis label and the line describe the same days.
 */
export function seriesPeak(
  points: readonly SparklinePoint[],
  drawFrom?: string,
): { useCount: number; date: string } | null {
  let peak: SparklinePoint | null = null;
  for (const point of points) {
    if (drawFrom && point.date < drawFrom) {
      continue;
    }
    if (point.useCount > (peak?.useCount ?? 0)) {
      peak = point;
    }
  }
  return peak ? { useCount: peak.useCount, date: peak.date } : null;
}
```

- [ ] **Step 5: Tests laufen lassen und Erfolg bestätigen**

```
npm --prefix web test -- --watch=false --include="src/app/shared/emotes/usage-series.spec.ts"
```

Erwartet: PASS, 27 Tests (21 bestehende + 6 neue).

- [ ] **Step 6: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add web/src/app/shared/emotes/usage-series.ts web/src/app/shared/emotes/usage-series.spec.ts
git commit -m "feat(web): let the curve start where the emote does"
```

---

### Task 2: `liveDayCoverage` und `liveDayCaptionKey`

**Files:**
- Modify: `web/src/app/shared/emotes/usage-series.ts` (zwei Funktionen anhängen, Import ergänzen)
- Test: `web/src/app/shared/emotes/usage-series.spec.ts`

**Interfaces:**
- Consumes: `SparklinePoint` aus Task 1s Datei, `pluralKey` aus `web/src/app/core/i18n/plural.ts`
- Produces:
  - `liveDayCoverage(points: readonly SparklinePoint[], liveDays: readonly string[], drawFrom?: string): { live: number; unused: number }`
  - `liveDayCaptionKey(coverage: { live: number; unused: number }, hasLiveDays: boolean): string | null`
  - Die Keys, die `liveDayCaptionKey` zurückgibt, sind vollständige Transloco-Keys:
    `usageStats.chart.unusedOnLiveDays.{one,other}`, `usageStats.chart.usedOnAllLiveDays.{one,other}`,
    `usageStats.chart.liveLegend`. Task 3 legt sie in beiden Locale-Dateien an.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

Am Ende von `web/src/app/shared/emotes/usage-series.spec.ts` anhängen:

```ts
describe('liveDayCoverage', () => {
  const week = fillDailySeries(
    [
      { date: '2026-07-02', useCount: 4 },
      { date: '2026-07-05', useCount: 1 },
    ],
    '2026-07-01',
    '2026-07-07',
  );
  const live = ['2026-07-01', '2026-07-02', '2026-07-05', '2026-07-06'];

  it('counts the live days the emote went unused on', () => {
    expect(liveDayCoverage(week, live)).toEqual({ live: 4, unused: 2 });
  });

  it('leaves out live days before the emote entered the set', () => {
    // The 01. and the 02. drop out of both numbers, not just the numerator: they are not days the
    // emote could have been used on, so they belong in neither.
    expect(liveDayCoverage(week, live, '2026-07-05')).toEqual({ live: 2, unused: 1 });
  });

  it('reports no live days when the emote arrived after the last of them', () => {
    expect(liveDayCoverage(week, live, '2026-07-07')).toEqual({ live: 0, unused: 0 });
  });

  it('ignores live days outside the rendered range', () => {
    expect(liveDayCoverage(week, ['2026-06-30', '2026-08-01'])).toEqual({ live: 0, unused: 0 });
  });

  it('reports nothing without live days', () => {
    expect(liveDayCoverage(week, [])).toEqual({ live: 0, unused: 0 });
  });
});

describe('liveDayCaptionKey', () => {
  it('names the live days the emote went unused on', () => {
    expect(liveDayCaptionKey({ live: 12, unused: 9 }, true)).toBe(
      'usageStats.chart.unusedOnLiveDays.other',
    );
  });

  it('drops the "1 of 1" wording for a single live day', () => {
    expect(liveDayCaptionKey({ live: 1, unused: 1 }, true)).toBe(
      'usageStats.chart.unusedOnLiveDays.one',
    );
  });

  it('states the positive case rather than "0 unused"', () => {
    expect(liveDayCaptionKey({ live: 12, unused: 0 }, true)).toBe(
      'usageStats.chart.usedOnAllLiveDays.other',
    );
    expect(liveDayCaptionKey({ live: 1, unused: 0 }, true)).toBe(
      'usageStats.chart.usedOnAllLiveDays.one',
    );
  });

  it('falls back to naming the bands when none of them fall inside the emote lifetime', () => {
    // The bands span the whole width regardless of when the emote arrived, so without this form the
    // green would stand on screen unexplained — in the very case where it is least obvious.
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, true)).toBe('usageStats.chart.liveLegend');
  });

  it('stays silent when there are no live days at all', () => {
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, false)).toBeNull();
  });
});
```

Den Import-Block oben in der Spec-Datei erweitern:

```ts
import {
  fillDailySeries,
  fillOffsetSeries,
  liveBands,
  liveDayCaptionKey,
  liveDayCoverage,
  offsetsToDates,
  seriesPeak,
  toPolylinePoints,
} from './usage-series';
```

- [ ] **Step 2: Tests laufen lassen und Fehlschlag bestätigen**

```
npm --prefix web test -- --watch=false --include="src/app/shared/emotes/usage-series.spec.ts"
```

Erwartet: FAIL beim Build/Transform der Spec — `liveDayCoverage`/`liveDayCaptionKey` sind kein Export
von `./usage-series`.

- [ ] **Step 3: Die beiden Funktionen implementieren**

Ganz oben in `web/src/app/shared/emotes/usage-series.ts`, über den bestehenden Import:

```ts
import { pluralKey } from '../../core/i18n/plural';
```

Nach `seriesPeak` (also vor der privaten `rangeDates`-Funktion) anhängen:

```ts
/**
 * How many of the days the emote could have been used on the stream was live, and on how many of
 * those it went unused. Only days at or after `drawFrom` count: a live day before the emote entered
 * the set is not a day it could have been used. Without `drawFrom` the whole range counts, which is
 * the honest reading when 7TV reported no date for the emote.
 *
 * The denominator is live days and never the length of the range — a missing ChannelLiveDay row
 * means "no data", never "offline", so "of 13 days" would state an absence nobody measured.
 */
export function liveDayCoverage(
  points: readonly SparklinePoint[],
  liveDays: readonly string[],
  drawFrom?: string,
): { live: number; unused: number } {
  const live = new Set(liveDays);
  let liveCount = 0;
  let unused = 0;
  for (const point of points) {
    if (drawFrom && point.date < drawFrom) {
      continue;
    }
    if (!live.has(point.date)) {
      continue;
    }
    liveCount++;
    if (point.useCount === 0) {
      unused++;
    }
  }
  return { live: liveCount, unused };
}

/**
 * The transloco key for the line under the curve, or `null` when it must stay silent. Three forms,
 * because the honest sentence differs: some live days went unused, all of them were used, or none of
 * the live days fall inside the emote's lifetime — and in that last case the green bands are still
 * on screen and still need naming.
 *
 * It lives here rather than in either component because the sidecar and the drilldown dialog have to
 * say the same thing; two copies of this decision would drift the moment somebody touched one.
 */
export function liveDayCaptionKey(
  coverage: { live: number; unused: number },
  hasLiveDays: boolean,
): string | null {
  if (coverage.live === 0) {
    return hasLiveDays ? 'usageStats.chart.liveLegend' : null;
  }
  const base = coverage.unused === 0 ? 'usedOnAllLiveDays' : 'unusedOnLiveDays';
  return pluralKey(coverage.live, `usageStats.chart.${base}`);
}
```

- [ ] **Step 4: Tests laufen lassen und Erfolg bestätigen**

```
npm --prefix web test -- --watch=false --include="src/app/shared/emotes/usage-series.spec.ts"
```

Erwartet: PASS, 37 Tests (27 aus Task 1 plus 10 neue).

- [ ] **Step 5: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add web/src/app/shared/emotes/usage-series.ts web/src/app/shared/emotes/usage-series.spec.ts
git commit -m "feat(web): count the live days an emote could have been used on"
```

---

### Task 3: Sparkline-Input und die neuen Texte

Dieser Task ändert **kein sichtbares Verhalten**. Er legt das Input und die Keys an, die Task 4 und 5
beide brauchen, und zieht `chartLabel` an seinen richtigen Ort um.

**Files:**
- Modify: `web/src/app/shared/emotes/usage-sparkline.ts`
- Modify: `web/public/i18n/de.json:478-501` und `:540`, `web/public/i18n/en.json` an denselben Zeilen
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html:520` (Key-Umzug)
- Modify: `web/src/app/shared/emotes/emote-drilldown-dialog.ts:110` (Key-Umzug)

**Interfaces:**
- Consumes: `toPolylinePoints(..., drawFrom?)` aus Task 1
- Produces: Input `drawFrom` an `<app-usage-sparkline>`, Typ `string | null`, Default `null`;
  Transloco-Keys `usageStats.chart.label`, `usageStats.chart.unusedOnLiveDays.{one,other}`,
  `usageStats.chart.usedOnAllLiveDays.{one,other}`, `usageStats.chart.liveLegend`,
  `usageStats.liveDaysInRange.{one,other}`

- [ ] **Step 1: Das Input an der Sparkline anlegen**

In `web/src/app/shared/emotes/usage-sparkline.ts` bei den übrigen `input()`-Deklarationen ergänzen —
**vor** `inject()`/State, so schreibt es die Member-Order in `web/.claude/CLAUDE.md` vor:

```ts
  /**
   * ISO day the line may start speaking for; everything before it stays undrawn. `null` means
   * unknown (7TV reported no date), and then nothing is trimmed — see toPolylinePoints.
   */
  readonly drawFrom = input<string | null>(null);
```

Und das bestehende `polylinePoints`-Computed ersetzen:

```ts
  protected readonly polylinePoints = computed(() =>
    toPolylinePoints(this.points(), VIEW_WIDTH, VIEW_HEIGHT, this.drawFrom() ?? undefined),
  );
```

Das `?? undefined` steht hier und nicht beim Aufrufer: `firstSeenAt` ist `string | null`, und das
Input soll ohne Umrechnung daran gebunden werden können.

- [ ] **Step 2: Die neuen Texte anlegen**

In `web/public/i18n/de.json` **zwischen** `"neverUsed"` (Zeile 477) und `"drilldown"` (Zeile 478)
einfügen:

```json
      "chart": {
        "label": "Tagesverlauf der Nutzung",
        "unusedOnLiveDays": {
          "one": "Am einzigen Live-Tag nicht benutzt",
          "other": "An {{unused}} von {{live}} Live-Tagen nicht benutzt"
        },
        "usedOnAllLiveDays": {
          "one": "Am einzigen Live-Tag benutzt",
          "other": "An allen {{live}} Live-Tagen benutzt"
        },
        "liveLegend": "Grün: Stream war live"
      },
```

In `de.json` die Zeile `"chartLabel": "Tagesverlauf der Nutzung",` (Zeile 481, im `drilldown`-Block)
**löschen**. `"liveDays"` (Zeile 486) bleibt vorerst stehen — Task 5 entfernt sie, wenn die letzte
Aufrufstelle weg ist.

Direkt **vor** `"trackedSince"` (Zeile 540) einfügen:

```json
      "liveDaysInRange": {
        "one": "Im gewählten Zeitraum war der Stream an einem Tag live.",
        "other": "Im gewählten Zeitraum war der Stream an {{ live }} Tagen live."
      },
```

Dieselben drei Eingriffe in `web/public/i18n/en.json` an denselben Zeilen:

```json
      "chart": {
        "label": "Daily usage history",
        "unusedOnLiveDays": {
          "one": "Unused on the only live day",
          "other": "Unused on {{unused}} of {{live}} live days"
        },
        "usedOnAllLiveDays": {
          "one": "Used on the only live day",
          "other": "Used on all {{live}} live days"
        },
        "liveLegend": "Green: the stream was live"
      },
```

```json
      "liveDaysInRange": {
        "one": "In the selected range the stream was live on one day.",
        "other": "In the selected range the stream was live on {{ live }} days."
      },
```

- [ ] **Step 3: Die zwei Verweise auf `chartLabel` umhängen**

`web/src/app/features/usage-stats/usage-stats-page.html:520`:

```html
                  [ariaLabel]="'usageStats.chart.label' | transloco"
```

`web/src/app/shared/emotes/emote-drilldown-dialog.ts:110`:

```html
              [ariaLabel]="'usageStats.chart.label' | transloco"
```

- [ ] **Step 4: Prüfen, dass kein Verweis übrig ist**

```
cd web && grep -rn "drilldown.chartLabel" src public e2e
```

Erwartet: **kein Treffer** (Exit-Code 1, keine Ausgabe). Ein `tsc`-Lauf hilft hier nicht — der Key
steht in Templates, nicht im TypeScript, und ein fehlender Transloco-Key bricht den Build nicht,
sondern rendert stumm den Key selbst.

- [ ] **Step 5: Die volle Unit-Suite laufen lassen**

```
npm --prefix web test -- --watch=false
```

Erwartet: PASS, keine Regression. Es gibt keinen Test für die Sparkline-Komponente und soll keinen
geben (Regel 12) — dieser Schritt beweist nur, dass das neue Input nichts kaputt gemacht hat.

- [ ] **Step 6: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add web/src/app/shared/emotes/usage-sparkline.ts web/public/i18n/de.json web/public/i18n/en.json web/src/app/features/usage-stats/usage-stats-page.html web/src/app/shared/emotes/emote-drilldown-dialog.ts
git commit -m "chore(web): give the sparkline a start day and the chart its own texts"
```

---

### Task 4: Der Sidecar und die Tracking-Zeile

**Files:**
- Modify: `web/src/app/features/usage-stats/usage-stats-page.ts:469-480` (neue Computeds)
- Modify: `web/src/app/features/usage-stats/usage-stats-page.html:194-197` (Tracking-Zeile), `:516-521` (`drawFrom`), `:532-545` (Zeile)
- Test: `web/e2e/usage-atlas.e2e.spec.ts`

**Interfaces:**
- Consumes: `liveDayCoverage`, `liveDayCaptionKey`, `seriesPeak(…, drawFrom)` aus Tasks 1–2; Input
  `drawFrom` aus Task 3
- Produces: `openAtlas(page: Page, emotes?: MockEmoteUsage[])` in `usage-atlas.e2e.spec.ts` bekommt
  einen zweiten, optionalen Parameter — Task 5 benutzt ihn.

- [ ] **Step 1: Die fehlschlagenden E2E-Tests schreiben**

Zuerst `openAtlas` in `web/e2e/usage-atlas.e2e.spec.ts:47` parametrisieren, damit ein einzelner Test
die Totals variieren kann (Playwright bevorzugt die **zuletzt** registrierte Route, ein zweites
`mockUsageTotals` nach `openAtlas` käme also zu spät):

```ts
async function openAtlas(page: Page, emotes: MockEmoteUsage[] = EMOTES): Promise<void> {
```

und in derselben Funktion:

```ts
  await mockUsageTotals(page, 'sensitron', emotes);
```

Dazu den Import erweitern:

```ts
import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockMyChannels,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';
```

Dann den bestehenden Test `'the curve states its scale, and the green bands say what they are'`
(Zeilen 170-193) vollständig ersetzen:

```ts
  test('the curve states its scale, and the green bands say what the emote did on them', async ({
    page,
  }) => {
    // catJAM's 900 uses, as a curve peaking at 700. Distinct from every other number the sidecar
    // prints, so an exact-text match can tell the axis apart from the totals below it.
    // Live offsets counted from the mocked tracking start 2026-06-12: the 15., 16., 17. and 21.
    // The curve puts 700 on the 15. and nothing on the other three.
    await mockUsageChannelSeries(
      page,
      'sensitron',
      {
        e1: [
          [1, 200],
          [3, 700],
        ],
      },
      [3, 4, 5, 9],
    );
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    // The axis is aria-hidden — the peak line carries the same maximum in words, which is what
    // keeps the graphic from meaning anything on its own. Asserted on the rendered text all the
    // same, because a scale nobody can read is the thing this exists to prevent.
    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar.getByText('0', { exact: true })).toBeVisible();
    // The emote-specific statement, not the channel-wide one that used to stand here and read the
    // same for every emote. Both numbers are pinned: they follow the offsets above, not the calendar.
    await expect(sidecar).toContainText('An 3 von 4 Live-Tagen nicht benutzt');
    await expect(sidecar).not.toContainText('Live an');
  });
```

Danach diese drei Tests neu hinzufügen, direkt darunter:

```ts
  test('the channel-wide live count is stated once, above the sheet', async ({ page }) => {
    // It answers a question about the stream, not about any one emote, so it belongs to the page.
    await mockUsageChannelSeries(page, 'sensitron', { e1: [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(page);

    await expect(page.getByText(/Im gewählten Zeitraum war der Stream an 4 Tagen live\./)).toBeVisible();
  });

  test('says nothing about live days for a range with no coverage', async ({ page }) => {
    // "0 of 57 days" would report an absence we never measured: a range older than the live poll has
    // no coverage data at all, which is not the same as a channel that never went live.
    await mockUsageChannelSeries(page, 'sensitron', {
      e1: [
        [1, 200],
        [3, 700],
      ],
    });
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar).not.toContainText('Live-Tag');
    await expect(page.getByText(/war der Stream an/)).toHaveCount(0);
  });

  test('draws no line for the days before the emote entered the set', async ({ page }) => {
    // The emote joined the set on the 20., eight days into the range. A baseline over the days
    // before that reads as "unused" where it should read as "did not exist" — the whole point.
    await mockUsageChannelSeries(page, 'sensitron', { e1: [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteId === 'e1' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );
    const sidecar = page.getByRole('complementary');

    const points = await sidecar.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and the curve has nothing on it. Singular,
    // because "An 1 von 1 Live-Tagen" is not a sentence.
    await expect(sidecar).toContainText('Am einzigen Live-Tag nicht benutzt');
  });
```

- [ ] **Step 2: E2E laufen lassen und Fehlschlag bestätigen**

Erst sicherstellen, dass auf `:5151` nichts lauscht:

```
netstat -ano | grep LISTENING | grep :5151
```

Erwartet: keine Ausgabe. Dann:

```
npm --prefix web run e2e -- usage-atlas.e2e.spec.ts
```

Erwartet: FAIL in den vier oben genannten Tests — der Sidecar zeigt noch „Live an 4 von … Tagen", die
Tracking-Zeile hat nur einen Satz, und die Polyline beginnt bei `0,`.

- [ ] **Step 3: Die Computeds in der Seite ergänzen**

In `web/src/app/features/usage-stats/usage-stats-page.ts` das bestehende `inspectedPeak`
(Zeile 480) ersetzen und die neuen Computeds unmittelbar dahinter einfügen:

```ts
  /**
   * The day the inspected emote entered the set — the first day its curve may speak for. `null` when
   * 7TV reported no date: then nothing is trimmed and nothing is claimed.
   */
  protected readonly inspectedDrawFrom = computed(
    () => this.inspected()?.firstSeenAt?.slice(0, 10) ?? null,
  );

  protected readonly inspectedPeak = computed(() =>
    seriesPeak(this.inspectedPoints(), this.inspectedDrawFrom() ?? undefined),
  );

  /**
   * How many live days this emote could have been used on, and how many of those it went unused —
   * the emote-specific counterpart of the channel-wide count that used to stand under the curve and
   * read identically for every row.
   */
  protected readonly inspectedCoverage = computed(() =>
    liveDayCoverage(
      this.inspectedPoints(),
      this.liveDayDates(),
      this.inspectedDrawFrom() ?? undefined,
    ),
  );

  protected readonly inspectedLiveKey = computed(() =>
    liveDayCaptionKey(this.inspectedCoverage(), this.liveDayDates().length > 0),
  );

  /** Channel-wide and range-dependent, so it is stated once at the top rather than on every emote. */
  protected readonly liveDaysInRangeKey = computed(() => {
    const count = this.liveDayDates().length;
    return count > 0 ? pluralKey(count, 'usageStats.liveDaysInRange') : null;
  });
```

In den **bestehenden** Import aus `'../../shared/emotes/usage-series'` die zwei Namen
`liveDayCaptionKey` und `liveDayCoverage` einsortieren — alphabetisch, das erzwingt ESLint. Die
übrigen Namen dieser Import-Zeile unverändert stehen lassen (sie ist je nach Stand länger als hier
gezeigt):

```ts
import {
  fillOffsetSeries,
  liveDayCaptionKey,
  liveDayCoverage,
  offsetsToDates,
  seriesPeak,
} from '../../shared/emotes/usage-series';
```

`pluralKey` ist bereits importiert (`usage-stats-page.ts:27`) und braucht nichts.

- [ ] **Step 4: Die Tracking-Zeile ergänzen**

`web/src/app/features/usage-stats/usage-stats-page.html:194-197` ersetzen durch:

```html
  @if (trackedSince(); as since) {
    <p class="text-xs text-fg-muted">
      {{ 'usageStats.trackedSince' | transloco: { date: formatDate(since) } }}
      <!-- The channel-wide live count, stated once here instead of on every emote. Without a
           denominator on purpose: a missing ChannelLiveDay row means "no data", never "offline". -->
      @if (liveDaysInRangeKey(); as liveKey) {
        {{ liveKey | transloco: { live: liveDayDates().length } }}
      }
    </p>
```

- [ ] **Step 5: `drawFrom` an die Sparkline binden**

`web/src/app/features/usage-stats/usage-stats-page.html:516-521` ersetzen durch:

```html
                <app-usage-sparkline
                  class="block h-full min-w-0 flex-1"
                  [points]="inspectedPoints()"
                  [liveDays]="liveDayDates()"
                  [drawFrom]="inspectedDrawFrom()"
                  [ariaLabel]="'usageStats.chart.label' | transloco"
                />
```

- [ ] **Step 6: Die Zeile unter der Kurve austauschen**

`web/src/app/features/usage-stats/usage-stats-page.html:532-545` (Kommentar und `@if`-Block)
ersetzen durch:

```html
              <!-- What this emote did on the days we know the stream was live. Silent without any
                   coverage, and reduced to naming the bands when none of them fall inside the
                   emote's lifetime — the bands span the whole range either way. -->
              @if (inspectedLiveKey(); as liveKey) {
                <p class="flex items-center gap-1.5 font-mono text-[11px] text-fg-muted">
                  <span
                    class="inline-block h-2 w-2 shrink-0 rounded-sm bg-success-dot"
                    aria-hidden="true"
                  ></span>
                  {{ liveKey | transloco: inspectedCoverage() }}
                </p>
              }
```

- [ ] **Step 7: E2E laufen lassen und Erfolg bestätigen**

```
npm --prefix web run e2e -- usage-atlas.e2e.spec.ts
```

Erwartet: PASS, alle Tests der Datei. Fällt „the channel-wide live count is stated once" mit einem
fehlenden Leerzeichen zwischen den beiden Sätzen durch, ist die Ursache Angulars
Whitespace-Behandlung im `@if` — dann `{{ 'usageStats.trackedSince' … }}` und den `@if`-Block in
**derselben** Zeile lassen und stattdessen ein `&#32;` vor dem `@if` setzen.

- [ ] **Step 8: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add web/src/app/features/usage-stats/usage-stats-page.ts web/src/app/features/usage-stats/usage-stats-page.html web/e2e/usage-atlas.e2e.spec.ts
git commit -m "feat(web): let the sidecar say what this emote did on the live days"
```

---

### Task 5: Der Drilldown-Dialog

**Files:**
- Modify: `web/src/app/shared/emotes/emote-drilldown-dialog.ts:106-111` (`drawFrom`), `:126-139` (Zeile), `:251-256` (Computeds)
- Modify: `web/public/i18n/de.json:486`, `web/public/i18n/en.json:486` (`drilldown.liveDays` entfernen)
- Test: `web/e2e/usage-atlas.e2e.spec.ts`

**Interfaces:**
- Consumes: alles aus Tasks 1–4, inklusive `openAtlas(page, emotes?)`
- Produces: nichts für spätere Tasks

- [ ] **Step 1: Den fehlschlagenden E2E-Test schreiben**

In `web/e2e/usage-atlas.e2e.spec.ts` anhängen:

```ts
  test('the drilldown curve keeps quiet about the days before the emote existed', async ({
    page,
  }) => {
    // Same statement as in the sidecar, from the other data path: the dialog loads its own per-emote
    // series with ISO live days, while the sidecar reads the batch response's offsets.
    await mockUsageDaily(
      page,
      'sensitron',
      [{ date: '2026-06-21', useCount: 40 }],
      ['2026-06-15', '2026-06-16', '2026-06-21'],
    );
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteName === 'Sadge' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );

    await cell(page, 'Sadge').hover();
    await page.getByRole('button', { name: 'Details zu Sadge anzeigen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Sadge');

    const points = await dialog.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and it was used that day — so the positive
    // form, not "0 unused".
    await expect(dialog).toContainText('Am einzigen Live-Tag benutzt');
    await expect(dialog).not.toContainText('Live an');

    // Closed first: the CDK dialog hides everything behind it from the accessibility tree.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
```

- [ ] **Step 2: E2E laufen lassen und Fehlschlag bestätigen**

```
npm --prefix web run e2e -- usage-atlas.e2e.spec.ts -g "keeps quiet about the days"
```

Erwartet: FAIL — der Dialog zeigt noch „Live an 3 von … Tagen", und die Polyline beginnt bei `0,`.

- [ ] **Step 3: Die Computeds im Dialog ergänzen**

In `web/src/app/shared/emotes/emote-drilldown-dialog.ts` die beiden Computeds `peak` und `yMax`
ersetzen — sie stehen **direkt hinter** `points`, das unverändert bleibt — und die neuen dahinter
einfügen:

```ts
  /** The day this emote entered the set; `null` when 7TV reported none, and then nothing is trimmed. */
  protected readonly drawFrom = computed(() => this.data.firstSeenAt?.slice(0, 10) ?? null);

  protected readonly peak = computed(() =>
    seriesPeak(this.points(), this.drawFrom() ?? undefined),
  );
  protected readonly yMax = computed(() => this.peak()?.useCount ?? 0);

  protected readonly coverage = computed(() =>
    liveDayCoverage(this.points(), this.series()?.liveDays ?? [], this.drawFrom() ?? undefined),
  );

  protected readonly liveKey = computed(() =>
    liveDayCaptionKey(this.coverage(), (this.series()?.liveDays.length ?? 0) > 0),
  );
```

Der Aufruf vom Stimmzettel liefert kein `firstSeenAt` (`vote-session-detail-page.ts:495-508`), dort
ist `drawFrom` also `null` und alles verhält sich wie bisher — das ist so gewollt und derselbe Grund,
aus dem der Dialog dort schon heute keine „Im Set"-Zeile zeigt.

In den **bestehenden** Import aus `'./usage-series'` dieselben zwei Namen einsortieren, alphabetisch,
alles andere unverändert:

```ts
import {
  fillDailySeries,
  liveDayCaptionKey,
  liveDayCoverage,
  seriesPeak,
  SparklinePoint,
} from './usage-series';
```

**Der Dialog bekommt die channelweite Live-Zahl bewusst nicht.** Er ist ein Modal, die Zeile im
Seitenkopf liegt dahinter — aber seine eigene Zeile trägt Tupfer und das Wort „Live-Tag" und ist
damit Legende genug. Die Zahl zusätzlich hier zu zeigen, wäre genau die Redundanz, die Task 4
entfernt.

- [ ] **Step 4: `drawFrom` binden und die Zeile austauschen**

`emote-drilldown-dialog.ts:106-111`:

```html
            <app-usage-sparkline
              class="block h-full min-w-0 flex-1"
              [points]="points()"
              [liveDays]="series()!.liveDays"
              [drawFrom]="drawFrom()"
              [ariaLabel]="'usageStats.chart.label' | transloco"
            />
```

`emote-drilldown-dialog.ts:126-139` (Kommentar und `@if`-Block) ersetzen durch:

```html
            <!-- What this emote did on the days we know the stream was live. Silent without any
                 coverage — an older range predates the poll's data, and a count over days we never
                 measured would be a false statement. -->
            @if (liveKey(); as key) {
              <p class="flex items-center gap-1.5 text-xs text-fg-muted">
                <span class="inline-block h-2 w-2 rounded-sm bg-success-dot" aria-hidden="true"></span>
                {{ key | transloco: coverage() }}
              </p>
            }
```

- [ ] **Step 5: Den toten Key entfernen**

Jetzt ist `usageStats.drilldown.liveDays` nirgends mehr referenziert. Zeile 486 in
`web/public/i18n/de.json` **und** in `web/public/i18n/en.json` löschen, dann gegenprüfen:

```
cd web && grep -rn "drilldown.liveDays\|drilldown\": {" --include="*.ts" --include="*.html" src e2e && grep -rn "\"liveDays\"" public/i18n
```

Erwartet: kein Treffer auf `drilldown.liveDays`, und `public/i18n` zeigt keine `"liveDays"`-Zeile mehr.

- [ ] **Step 6: E2E laufen lassen und Erfolg bestätigen**

```
npm --prefix web run e2e -- usage-atlas.e2e.spec.ts
```

Erwartet: PASS, alle Tests der Datei.

- [ ] **Step 7: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add web/src/app/shared/emotes/emote-drilldown-dialog.ts web/public/i18n/de.json web/public/i18n/en.json web/e2e/usage-atlas.e2e.spec.ts
git commit -m "feat(web): give the drilldown curve the same lifetime and live-day reading"
```

---

### Task 6: Dokumentation, Gesamtlauf, Detektor

**Files:**
- Modify: `docs/UI-Designsprache.md:107`
- Modify: `docs/DECISIONS.md` (neuer Eintrag ganz oben)
- Modify: `docs/superpowers/specs/2026-08-08-usage-curve-lifetime-design.md` (Status)

**Interfaces:**
- Consumes: den fertigen Zustand aus Tasks 1–5
- Produces: nichts

- [ ] **Step 1: Die Designsprache nachziehen**

`docs/UI-Designsprache.md:107` endet heute mit:

> … **Der Drilldown-Dialog bleibt** — er ist der einzige Weg auf dem Stimmzettel, unterhalb `lg`, per Touch und per Tastatur, und trägt zusätzlich Y-Achse, Spitzensatz, Live-Tage-Legende und Erstnutzung.

Diesen Teilsatz ersetzen durch:

> … **Der Drilldown-Dialog bleibt** — er ist der einzige Weg auf dem Stimmzettel, unterhalb `lg`, per Touch und per Tastatur, und trägt zusätzlich den Zeitraum, Erst- und Letztnutzung und den Abstimmungsblock. Y-Achse, Spitzensatz und die Live-Tage-Zeile stehen in beiden.

Der Satz war bereits vor dieser Änderung falsch: `a2d2602` hat Y-Achse und Live-Tage-Zeile in den
Sidecar gebracht, die Aufzählung nennt sie aber weiter als Alleinstellung des Dialogs.

- [ ] **Step 2: Den Entscheidungs-Eintrag schreiben**

Ganz oben in `docs/DECISIONS.md` einfügen (das Log ist absteigend nach Datum sortiert):

```markdown
## 2026-08-08 — Die Kurve schweigt, wo sie nichts weiß, und Live-Zahlen tragen keinen Nenner

**Betrifft:** `web/src/app/shared/emotes/usage-series.ts`, `usage-sparkline.ts`,
`emote-drilldown-dialog.ts`, `web/src/app/features/usage-stats/usage-stats-page.{ts,html}`,
`web/public/i18n/*.json`

Drei zusammenhängende Änderungen an der Nutzungskurve, alle aus derselben Regel:

**1. Die Linie beginnt bei `firstSeenAt`, nicht am Zeitraumanfang.** Vorher lief sie über den ganzen
Channel-Zeitraum, also auch über Tage, an denen es das Emote noch nicht gab — eine Nulllinie, die
Nichtnutzung behauptet, wo nichts existierte. Am Drilldown von `GAMBA` beobachtet: zwölf flache Tage
für ein Emote, das an Tag dreizehn dazukam. Das ist dieselbe Fehlerklasse, die
`rangeStartsBeforeTracking` auf Seitenebene längst abfängt („we weren't counting yet" ≠ „this emote
is dead"), nur eine Ebene tiefer. Die x-Achse bleibt der Channel-Zeitraum, damit die Kurven im
Sidecar-Atlas untereinander vergleichbar bleiben; die Live-Bänder laufen weiter über die volle
Breite. Bei `firstSeenAt = null` wird nichts verkürzt — der Wert heißt „unbekannt", nie „neu"
(`Emote.cs:23-25`).

Voraussetzung dafür war, dass `firstSeenAt` das echte 7TV-Beitrittsdatum trägt und nicht unseren
Sync-Zeitpunkt. Das ist seit dem v4-GraphQL-Umbau vom 2026-08-03 der Fall und wurde vor dem Entwurf
über alle Schreibpfade geprüft.

**2. Die Zeile unter der Kurve sagt etwas über das Emote.** „Live an 12 von 13 Tagen" war für jedes
Emote identisch — eine Aussage über den Stream, angebracht an einem Emote. Sie wird zur Kehrseite:
„An 9 von 12 Live-Tagen nicht benutzt", gezählt nur über Live-Tage innerhalb der Lebenszeit des
Emotes. Das ist zugleich das bessere Signal für die einzige Entscheidung, für die es die Seite gibt.
Drei Formen, weil der ehrliche Satz sich unterscheidet (ungenutzt / alle genutzt / keine zählbaren
Live-Tage, aber sichtbare Bänder); die Auswahl liegt in `liveDayCaptionKey`, damit Sidecar und Dialog
nicht auseinanderlaufen können. Die channelweite Zahl steht jetzt einmalig an der Tracking-Zeile.

**3. Live-Zahlen tragen keinen Nenner mehr.** „von 13 Tagen" behauptete für jeden Tag ohne
`ChannelLiveDay`-Zeile, der Stream sei offline gewesen. `ChannelLiveDay.cs:8-9` verbietet genau diese
Lesart: eine fehlende Zeile heißt „keine Daten". Zeilen entstehen erst seit dem Poll-Worker, ein
Backfill existiert nicht. Der Nenner der neuen Zeile sind ausschließlich Tage, für die eine Live-Zeile
vorliegt; die Zeile im Seitenkopf nennt gar keinen. Bei null Live-Tagen schweigen beide, statt „0" zu
sagen.

Kein Backend-Eingriff, keine Migration. Der Drilldown vom Stimmzettel bleibt unverändert, weil
`VoteSessionResult` kein `firstSeenAt` trägt — dort verhält sich der Dialog wie vorher, was zu seinem
Bestand passt (er zeigt dort aus demselben Grund schon keine „Im Set"-Zeile).

Spec: `docs/superpowers/specs/2026-08-08-usage-curve-lifetime-design.md`.
```

- [ ] **Step 3: Den Status in der Spec nachziehen**

In `docs/superpowers/specs/2026-08-08-usage-curve-lifetime-design.md` die Statuszeile ändern zu:

```markdown
**Stand:** 2026-08-08 · **Status:** umgesetzt
```

- [ ] **Step 4: Formatierung und Lint**

```
npm --prefix web run format && npm --prefix web run lint && cd web && npx prettier --check .
```

Erwartet: alles grün. `format` schreibt, `lint` und `prettier --check` prüfen.

- [ ] **Step 5: Beide Suiten vollständig laufen lassen**

```
npm --prefix web test -- --watch=false
```

Erwartet: PASS. Basis vor dieser Arbeit waren 498 Tests in 56 Dateien; erwartet sind jetzt 514
(16 neue in `usage-series.spec.ts` — 6 aus Task 1, 10 aus Task 2).

Dann prüfen, dass auf `:5151` nichts lauscht, und:

```
npm --prefix web run e2e
```

Erwartet: PASS. Basis waren 80 Tests, erwartet sind 83 — 82 nach Task 4 (drei neue, einer davon
ersetzt den gleichnamigen Bestandstest, den der Plan versehentlich noch einmal aufführt) plus einer
aus Task 5. **Fällt hier rund die halbe Suite
über mehrere Dateien hinweg mit „element not found" durch, ist eine Api auf `:5151` die Ursache und
nicht diese Änderung** — beenden und neu laufen lassen, bevor irgendetwas debuggt wird.

- [ ] **Step 6: Den mechanischen Detektor einmal laufen lassen**

Genau einmal, nicht in einer Schleife, erst wenn die UI fertig ist:

```
node C:\Users\admin\.claude\skills\impeccable\scripts\detect.mjs --json web/src/app/features/usage-stats/usage-stats-page.html web/src/app/shared/emotes/emote-drilldown-dialog.ts web/src/app/shared/emotes/usage-sparkline.ts
```

Befunde in einem Rutsch beheben, höchstens eine Bestätigungsrunde, dann aufhören.

- [ ] **Step 7: Am Gerät ansehen**

```
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api --launch-profile lan
npm --prefix web run start:lan
```

Vier Fälle in echten Daten durchsehen: ein lange etabliertes Emote (Kurve unverändert), ein frisch
hinzugekommenes (kurzer Strich statt langer Nulllinie), ein archiviertes ohne `firstSeenAt` (nichts
verkürzt), und ein Zeitraum ohne Live-Abdeckung (beide Zeilen schweigen). **Danach `dotnet run`
beenden**, sonst fällt der nächste E2E-Lauf durch.

- [ ] **Step 8: Commit (erst nach Rückfrage beim Nutzer)**

```bash
git add docs/UI-Designsprache.md docs/DECISIONS.md docs/superpowers/specs/2026-08-08-usage-curve-lifetime-design.md docs/superpowers/plans/2026-08-08-usage-curve-lifetime.md docs/superpowers/prototypes/2026-08-08-usage-curve.html
git commit -m "docs: record why the usage curve stays silent about days it cannot speak for"
```
