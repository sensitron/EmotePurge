# Usage-Stats: Bänder benennen und zeigen

**Datum:** 2026-08-09
**Betrifft:** `web/src/app/shared/emotes/usage-bands.ts`, `web/src/app/shared/grid/atlas-grid.ts`, `web/src/app/features/usage-stats/usage-stats-page.{ts,html}`, `web/public/i18n/{de,en}.json`, `web/e2e/{usage-atlas,landing}.e2e.spec.ts`, `docs/UI-Designsprache.md`, `docs/DECISIONS.md`
**Prototyp:** [`docs/superpowers/prototypes/2026-08-09-verteilung-baender.html`](../prototypes/2026-08-09-verteilung-baender.html)

## Problem

Die Kopfzeilen der Pareto-Bänder auf der Usage-Stats-Seite sind nicht verständlich. Die drei Hints lauten heute „die erste Hälfte der Nutzung", „bis 80 % der Nutzung" und „das letzte Fünftel der Nutzung" — drei Gründe, warum das stolpert:

1. **„Erste Hälfte" und „letztes Fünftel" lesen sich zeitlich oder als Listenposition.** Gemeint ist ein Anteil am Nutzungsvolumen. Das Wort, das fehlt, ist genau das.
2. **Die Zeile mischt zwei Größen ohne Einheit.** Gerendert steht dort `TRAGENDE EMOTES · die erste Hälfte der Nutzung · 4` — der Hint ist ein Prozentanteil, die Zahl dahinter eine Emote-Anzahl.
3. **Die Bezugssysteme wechseln.** „Bis 80 %" ist kumuliert, „das letzte Fünftel" nicht. Der Leser muss selbst rekonstruieren, dass die 80 % die 50 % einschließen.

Dazu kommt ein zweites, davon unabhängiges Problem: Die Bänder gliedern das Sheet, tauchen im Verteilungs-Streifen darüber aber nur als binäre Zweiteilung auf (`bg-accent-fg` für den heavy-Anteil, `bg-fg-disabled` für alles andere). Bei einem konzentrierten Set ist dieser Akzent **genau ein Balken von 96** und damit praktisch unsichtbar.

## Entscheidungen

### 1. Kopfzeilen benennen ihre Einheiten

```
TRAGEND       52 % der Nutzung · 4 Emotes ──────────────────
REGELMÄSSIG   28 % der Nutzung · 28 Emotes ─────────────────
SELTEN        20 % der Nutzung · 142 Emotes ────────────────
NIE BENUTZT   im Zeitraum · 572 Emotes ───────  alle markieren
```

Beide Zahlen tragen ihre Einheit. Als Größe wird „der Nutzung" verwendet — dasselbe Wort, das der Verteilungs-Abschnitt bereits benutzt („Das obere Fünftel trägt X der gesamten Nutzung").

### 2. Der Prozentwert ist gemessen, nicht definiert

Die 50/80-Marken sind Schwellen der Berechnung, nicht die tatsächlichen Anteile: Der Schnitt liegt beim ersten Emote, das die kumulierte Summe über die Marke hebt, und nimmt Gleichstände mit. Über die sechs Dev-Channels mit nennenswerter Nutzung liegen die realen Anteile bei 50,0–53,0 % bzw. 80,1–80,9 %; zwei Mini-Sets mit fünf bzw. einem genutzten Emote liegen darüber (83,3 % und 100 %), weil die Granularität dort grob wird. Feste „50 %" wären also falsch.

**Berechnung:**

- **Nenner:** Gesamtnutzung des **ganzen** Sets (`emotes()`), nicht der gefilterten Ansicht.
- **Zähler:** Summe der `totalUseCount` der **sichtbaren** Emotes dieses Bands.

Ohne aktiven Filter ergibt das exakt den Bandanteil. Mit aktivem Namensfilter bleibt die Zeile trotzdem wahr — „diese 3 sichtbaren Emotes stehen für 4 % der Nutzung" — statt „52 % · 3 Emotes" zu behaupten. Die Emote-Anzahl daneben ist bereits heute die sichtbare; beide Zahlen beschreiben damit dasselbe.

**Rundung:** `Math.round(share * 100)`. Ergibt das 0 bei einem Anteil größer null, wird `<1 %` angezeigt, damit ein gefiltertes Band nicht als „0 %" dasteht. Diese Regel bekommt eine **eigene** Formatierungsfunktion `formatBandShare`; das bestehende `formatPercent` bleibt unangetastet, weil es auch den `concentration`-Satz rendert, der wörtlich so bleiben soll.

### 3. Bandnamen in Singular-Klassenform

| Key | heute (de) | neu (de) | heute (en) | neu (en) |
|---|---|---|---|---|
| `heavy` | Tragende Emotes | **Tragend** | Carrying the set | **Backbone** |
| `regular` | Regelmäßig | Regelmäßig | Regulars | **Regular** |
| `rare` | Selten | Selten | Rare | Rare |
| `dead` | Nie benutzt | Nie benutzt | Never used | Never used |

Grund: Die Titel erscheinen nicht nur über einer Gruppe, sondern auch im Sidecar und in der Inspector-Leiste hinter dem Rang eines **einzelnen** Emotes — `#3 · Tragende Emotes` bricht dort. Nach der Änderung liest es sich als `#3 · Tragend`.

### 4. Der Verteilungs-Streifen zeigt die Bänder

Die Verteilungszeile besteht künftig aus zwei übereinanderliegenden Elementen:

1. **Die bestehende Kurve**, aber jeder der 96 Balken in der Farbe seines Bandes statt der heutigen Zweiteilung. Jeder Balken bekommt einen **3-px-Sockel** (`min-h-[3px]` statt `min-h-px`): ohne ihn ist ein Balken des toten Schwanzes einen Pixel hoch, und eine Farbe auf einem Pixel sieht niemand. Der Sockel macht „nie benutzt" zu einem durchgehenden flachen Streifen — was der Sache entspricht, denn das ist keine kleine Zahl, sondern eine eigene Kategorie.
2. **Ein flacher Segmentbalken** darunter (22 px), dessen Segmentbreiten die Nutzungsanteile sind, mit Inline-Labels `52 % tragend`, `28 % regelmäßig`, `20 % selten`. Ein Label wird nur gesetzt, wenn sein Segment mindestens 9 % breit ist; sonst schieben sich die Beschriftungen schmaler Segmente übereinander.

Der Segmentbalken rechnet über das **ganze** Set, nicht über die gefilterte Ansicht — die Verteilungszeile beschreibt ausdrücklich das Set („Das ganze Set, nach Nutzung gereiht"), während die Kopfzeile im Sheet beschreibt, was unter ihr liegt. Bei aktivem Filter divergieren die beiden Prozentwerte deshalb bewusst.

Die Kopfzeilen im Sheet bekommen zusätzlich einen 8-px-Farbtupfer in derselben Bandfarbe. Die Farbe ist die einzige Verbindung zwischen Streifen und Sheet und muss deshalb nirgends erklärt werden.

### 5. Vier Helligkeitsstufen, kein zweiter Farbton

| Band | Füllung |
|---|---|
| `heavy` | `bg-accent-fg` |
| `regular` | `bg-accent-fg/55` |
| `rare` | `bg-accent-fg/25` |
| `dead` | `bg-fg-disabled/40` |

Eine Rampe der bestehenden Akzentfarbe, keine neue Farbfamilie: die Bänder sind eine Rangfolge, keine Kategorien. Damit entstehen **keine neuen Farbtokens**, `DESIGN.md` muss nicht regeneriert werden.

**Barrierefreiheit:** Die Segmente und Balken tragen keinen Text; die Bandzugehörigkeit steht immer zusätzlich als Wort da (Kopfzeile, Segment-Label, Sidecar). Farbe ist damit redundant codiert — WCAG 1.4.1 ist erfüllt, und für die Flächen gilt keine Kontrastanforderung, weil sie keine wesentliche Information allein tragen. Kurve und Segmentbalken sind `aria-hidden` (die Kurve ist es schon heute); die Labelzeile unter dem Balken bleibt es **nicht** — sie ist die zugängliche Fassung derselben Aussage. Der Farbtupfer in der Kopfzeile ist rein dekorativ und trägt kein `aria-label`.

## Verworfene Varianten

Alle drei am Prototyp mit echten Zahlen geprüft, damit die Frage nicht in drei Monaten neu aufgemacht wird.

**Gestrichelte Linien an den Bandgrenzen** (der ursprüngliche Vorschlag) scheitert an der Instabilität ihrer Position. Die x-Achse des Streifens ist der Rang, nicht die kumulierte Nutzung, also sitzen die Marken dort, wo das Band endet:

| | 50-%-Linie | 80-%-Linie |
|---|---|---|
| knirpz, x-Achse = ganzes Set | 0,5 % | 4,3 % |
| knirpz, x-Achse = nur genutzte | 2,3 % | 18,4 % |
| lililinanana, x-Achse = nur genutzte | 24,4 % | 100,0 % |

Bei einem konzentrierten Set kleben beide Linien am linken Rand und ihre Labels überlappen; bei einem flachen verschwindet die zweite am rechten Rand. Es gibt keine Achsenwahl, die über alle Channels trägt. Die Fassung, die die Linien auseinanderzieht, kostet zudem den toten Schwanz — also genau das, was die Seite zeigen soll.

**Zwei gestapelte Balken** (Nutzung über Emote-Anzahl) zeigen die Pareto-Schere am deutlichsten, kosten aber die doppelte Höhe und wiederholen, was die Kopfzeilen im Sheet ohnehin sagen.

**Lorenz-/Konzentrationskurve** platziert die Marken zwar immer stabil auf halber und auf 80 % Höhe, ist bei diesen Daten aber nach 5 % der Breite schon fast am Anschlag und danach 95 % lang flach. Sie zeigt Konzentration und sonst nichts; die Kopf-Knie-Schwanz-Form, wegen der der Streifen gebaut wurde, geht verloren.

## Umsetzung

### `web/src/app/shared/emotes/usage-bands.ts`

`groupIntoUsageBands` bekommt einen vierten Parameter und zwei zusätzliche Rückgabefelder:

```ts
export function groupIntoUsageBands<T>(
  items: readonly T[],
  count: (item: T) => number,
  thresholds: UsageBandThresholds,
  totalUsage: number,
): { key: UsageBandKey; items: T[]; peak: number; usage: number; share: number }[]
```

`usage` ist die Summe der Zähler im Band, `share` ist `usage / totalUsage` (0 bei `totalUsage === 0`). Der Nenner wird hereingereicht statt intern gebildet, weil er sich auf das ganze Set bezieht, die `items` aber die gefilterte Ansicht sind.

Neue Funktion, die `heavyBuckets` in der Page vollständig ersetzt:

```ts
export function usageBandBars(
  counts: readonly number[],
  thresholds: UsageBandThresholds,
  bars: number,
): UsageBandKey[]
```

Zählt die Bandgrößen über **alle** übergebenen Counts (das ganze Set), leitet daraus Balkenindizes ab und liefert für jeden Balken seinen Band-Key. Jedes nicht leere Band bekommt mindestens einen Balken — das tragende rundet bei einem konzentrierten Set sonst auf null, und ausgerechnet das wichtigste Band wäre unsichtbar. Läuft die Summe über `bars`, wird am Ende gekappt; Bänder, für die dann kein Balken übrig ist, entfallen.

Ebenfalls hier, damit die Klassennamen an einem Ort stehen und der Tailwind-Scanner sie als vollständige Literale sieht:

```ts
export const USAGE_BAND_FILL: Record<UsageBandKey, string> = {
  heavy: 'bg-accent-fg',
  regular: 'bg-accent-fg/55',
  rare: 'bg-accent-fg/25',
  dead: 'bg-fg-disabled/40',
};
```

### `web/src/app/shared/grid/atlas-grid.ts`

`AtlasRow` führt den Anteil mit:

```ts
| { kind: 'band'; band: UsageBandKey; count: number; share: number }
```

`packAtlasRows` reicht `share` aus dem Band durch.

### `web/src/app/features/usage-stats/usage-stats-page.ts`

- Neues `computed` `totalUsage` über `emotes()`.
- `bands` ruft `groupIntoUsageBands` mit `totalUsage()`.
- `heavyBuckets` entfällt, dafür `bandBars = computed(() => usageBandBars(emotes().map(…), bandThresholds(), distribution().length))`.
- Neues `computed` `usageSegments` für den Segmentbalken. Es gruppiert ein zweites Mal, diesmal über `emotes()` statt über `sortedEmotes()`, und behält die Bänder mit `usage > 0` — der Balken beschreibt das ganze Set, `bands()` beschreibt die Ansicht.
- Neue Formatierungsfunktion `formatBandShare` mit der `<1 %`-Regel.

### `web/src/app/features/usage-stats/usage-stats-page.html`

- Verteilungszeile: Kurve auf `bandBars()` umstellen, `min-h-px` → `min-h-[3px]`, Segmentbalken plus Labels ergänzen.
- Band-Kopfzeile: Farbtupfer, `share`-Hint, Einheit hinter der Anzahl.

### i18n

```json
"bands": {
  "heavy":    { "title": "Tragend" },
  "regular":  { "title": "Regelmäßig" },
  "rare":     { "title": "Selten" },
  "dead":     { "title": "Nie benutzt", "hint": "im Zeitraum" },
  "share":    "{{share}} der Nutzung",
  "count":    "{{count}} Emotes",
  "countOne": "1 Emote",
  "selectAll": "alle markieren"
}
```

Die drei Live-Bänder teilen sich einen Hint, deshalb ein gemeinsamer Schlüssel `share` statt drei identischer. `countOne` ist ein eigener Schlüssel, weil Transloco hier ohne MessageFormat-Plugin läuft und ein Band durchaus aus genau einem Emote bestehen kann.

### Mitbetroffen: die Landingpage

`web/src/app/features/landing/set-shape.ts:109` verwendet **dieselben** `usageStats.bands.*.title`-Schlüssel. Die neuen Titel erscheinen dort automatisch — gewollt, denn es ist dieselbe Einteilung. Die Bandfarben und der Segmentbalken bleiben dort bewusst außen vor: Die Landing-Grafik erklärt das Konzept, sie muss keine Werte tragen.

## Tests

Keine bestehende Unit-Test-Datei prüft Anzeigetexte — `usage-bands.spec.ts` und `atlas-grid.spec.ts` testen nur Keys und Zahlen. Die Textänderung bricht dort nichts, die Signaturänderungen schon.

**Anzupassen:**

- `usage-bands.spec.ts`: Aufrufe von `groupIntoUsageBands` um den vierten Parameter ergänzen.
- `atlas-grid.spec.ts`: `twoBands()`-Helper und die `packAtlasRows`-Fälle um `share` ergänzen.
- `web/e2e/usage-atlas.e2e.spec.ts` Zeilen 79–82: `'Tragende Emotes'` → `'Tragend'`.
- `web/e2e/landing.e2e.spec.ts` Zeile 30: dieselbe Ersetzung in der Bandliste.

**Neu (Regel 12):**

- `usageBandBars`: mindestens ein Balken je nicht leerem Band; Summe der Balken entspricht der angeforderten Anzahl; Kappung bei mehr Bändern als Balken; leeres Set.
- `groupIntoUsageBands`: `share` bei ungefilterter Eingabe entspricht dem Bandanteil; bei gefilterter Eingabe fällt er entsprechend kleiner aus; `totalUsage === 0` ergibt 0 statt `NaN`.
- Die `<1 %`-Regel der Prozentformatierung.

`npm --prefix web run e2e` läuft nur, wenn auf `:5151` keine Api lauscht — und Docker Desktop wurde in dieser Session gestartet, wodurch per Restart-Policy auch `emotepurge-dev-api` (Port 8080) und `emotepurge-dev-worker` hochgekommen sind. Der Api-Container bindet 8080, nicht 5151, kollidiert also nicht; ein lokal laufendes `dotnet run` wäre das Problem.

## Dokumentation

- **`docs/UI-Designsprache.md`**: Die vier Bandfüllungen als verbindliche Rampe aufnehmen (Abschnitt zum Sprite-Sheet/den Bändern), inklusive der Begründung „Rangfolge, keine Kategorien" und der Redundanz-Regel (Farbe nie allein tragend).
- **`docs/DECISIONS.md`**: Ein Eintrag im selben Commit wie die Farbeinführung (Regel 3) — er ändert eine visuelle Konvention. Inhalt: warum gemessene statt definierter Prozente, warum der Nenner das ganze Set ist, warum der Sockel, und die drei verworfenen Varianten in zwei Sätzen mit Verweis auf diese Spec.

## Bewusst nicht Teil dieser Arbeit

- Die Bandschwellen selbst (50 %/80 %) bleiben unverändert.
- Der `concentration`-Satz rechts im Verteilungs-Abschnitt bleibt wörtlich, wie er ist.
- Der Segmentbalken bekommt keine Interaktion (kein Hover, kein Klick-Filter). Er ist ein Orientierungsmittel, kein Bedienelement.
- Die Landing-Grafik bekommt keine Farben.
