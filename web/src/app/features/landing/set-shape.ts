import { Component } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { groupIntoUsageBands, usageBandThresholds } from '../../shared/emotes/usage-bands';

/**
 * The shape of an emote set, drawn with the product's own arithmetic.
 *
 * The landing page has nothing to show. There are no screenshots, no numbers we are allowed to
 * quote, no testimonials (PRODUCT.md, "Evidence on Hand"), and the one thing a visitor actually
 * wants to see — their set, sorted by what the chat really uses — only exists after they have
 * joined a channel and waited. The honest substitute is not a picture of the tool but the *finding*
 * the tool produces: a handful of emotes carry the chat and the long tail sits there unused.
 *
 * So this is a diagram, declared as one in its caption. What makes it worth the space is that it is
 * not drawn by hand: the cells run through `usageBandThresholds` / `groupIntoUsageBands` /
 * `usageFillPercent` — the same three functions the real atlas calls — and they carry the same four
 * band names from the same translation keys. A visitor who logs in meets this figure again as the
 * actual page, with their own set in it.
 *
 * The counts themselves are a Zipf curve, not measured data, which is why the caption says so.
 *
 * What the figure does NOT borrow from the atlas is the *fill*. The first build drew each cell with
 * the atlas's own hairline usage bar, on the theory that the same notation everywhere is worth
 * something. Rendered, it inverted the message: without artwork on it a cell showed nothing but a
 * 2 px underline, and the dead band — whose cells carry a visible void plate — became the loudest
 * thing in the picture, which is the opposite of what the figure says. The cells here take a
 * vertical fill instead, so the head is dense and the tail is empty.
 */

/** Busiest emote in the diagram. Sets the scale; nothing reads the absolute value. */
const SCHEMATIC_PEAK = 3200;
/** How many of the diagram's emotes were used at all. */
const SCHEMATIC_USED = 254;
/** …and how many were never used once. The tail is the point of the whole figure. */
const SCHEMATIC_DEAD = 130;
/** Zipf exponent. Just above 1 gives a head steep enough to be visible at 12 px per cell. */
const SCHEMATIC_FALLOFF = 1.05;
/** Per-cell stagger of the reveal, in ms. 384 cells land in well under a second. */
const REVEAL_STEP_MS = 1.5;
/** See `shapeFill` — both values were read off the rendered figure. */
const SHAPE_FILL_EXPONENT = 0.35;
const SHAPE_FILL_FLOOR = 8;

interface ShapeBand {
  key: string;
  count: number;
  cells: { fill: number; delayMs: number }[];
}

function schematicCounts(): number[] {
  const counts: number[] = [];
  for (let rank = 1; rank <= SCHEMATIC_USED; rank += 1) {
    counts.push(Math.max(1, Math.round(SCHEMATIC_PEAK / Math.pow(rank, SCHEMATIC_FALLOFF))));
  }
  for (let i = 0; i < SCHEMATIC_DEAD; i += 1) {
    counts.push(0);
  }
  return counts;
}

/**
 * Fill height as a share of the cell, measured against the busiest emote in the whole set.
 *
 * Against the GLOBAL peak, not the band's own — the atlas measures each emote against its band
 * because a moderator is comparing within a group there, but this figure exists to show the
 * collapse ACROSS the groups, and a per-band peak fills the top of every band to 100 % and flattens
 * exactly the thing being shown.
 *
 * The exponent was picked against the rendered figure, not derived. Linear leaves everything past
 * the first dozen at sub-pixel height; logarithmic overcorrects and made "regelmäßig" and "selten"
 * two shades of half-full, which is the same flatness with a different cause. `0.35` keeps a
 * visible slope across all three used bands. The floor matters as much: a rarely-used emote must
 * never render as an empty cell, because empty is what "never used once" means here.
 */
function shapeFill(count: number, peak: number): number {
  if (count <= 0) {
    return 0;
  }
  return Math.max(SHAPE_FILL_FLOOR, Math.round(Math.pow(count / peak, SHAPE_FILL_EXPONENT) * 100));
}

function buildBands(): ShapeBand[] {
  const counts = schematicCounts();
  const thresholds = usageBandThresholds(counts);
  const peak = Math.max(...counts);
  const total = counts.reduce((sum, count) => sum + count, 0);
  let rank = 0;

  return groupIntoUsageBands(counts, (count) => count, thresholds, total).map((band) => ({
    key: band.key,
    count: band.items.length,
    cells: band.items.map((count) => ({
      fill: shapeFill(count, peak),
      // Ranked, not per band: the reveal has to read as one sweep from the head into the tail.
      delayMs: Math.round(rank++ * REVEAL_STEP_MS),
    })),
  }));
}

@Component({
  selector: 'app-set-shape',
  imports: [TranslocoPipe],
  template: `
    <figure class="flex flex-col gap-5">
      @for (band of bands; track band.key) {
        <div class="flex flex-col gap-1.5">
          <div class="flex items-baseline gap-3">
            <h3 class="text-[11px] font-semibold tracking-[0.13em] text-fg-secondary uppercase">
              {{ 'usageStats.bands.' + band.key + '.title' | transloco }}
            </h3>
            <span class="font-mono text-[11px] text-fg-muted">{{ band.count }}</span>
            <span class="h-px min-w-4 flex-1 bg-border"></span>
          </div>
          <!-- auto-fill rather than a measured column count: the figure has no interaction and no
             virtual scroller, so the browser can do the packing and the whole thing survives a
             resize without a ResizeObserver. -->
          <div
            class="grid grid-cols-[repeat(auto-fill,minmax(0.875rem,1fr))] gap-0.75"
            aria-hidden="true"
          >
            @for (cell of band.cells; track $index) {
              <span
                class="app-shape-cell relative block aspect-square bg-surface-inset"
                [style.animation-delay.ms]="cell.delayMs"
              >
                @if (cell.fill > 0) {
                  <i
                    class="absolute inset-x-0 bottom-0 block bg-accent-fg/70"
                    [style.height.%]="cell.fill"
                  ></i>
                }
              </span>
            }
          </div>
        </div>
      }
      <figcaption class="text-xs text-fg-muted">
        {{ 'landing.shape.caption' | transloco }}
      </figcaption>
    </figure>
  `,
  styles: `
    /* The one authored moment on the page: the set fills in from the busiest emote into the dead
       tail, which is the same direction the eye has to travel to understand the figure. Starts from
       an already-laid-out grid — only paint changes — so nothing reflows while it runs. */
    .app-shape-cell {
      animation: app-shape-in 320ms cubic-bezier(0.16, 1, 0.3, 1) backwards;
    }

    @keyframes app-shape-in {
      from {
        opacity: 0;
        transform: scale(0.6);
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .app-shape-cell {
        animation: none;
      }
    }
  `,
})
export class SetShape {
  protected readonly bands = buildBands();
}
