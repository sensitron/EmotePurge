import { Component, computed, input } from '@angular/core';

import { SparklinePoint, liveBands, toPolylinePoints } from './usage-series';

/** Fixed viewBox the polyline is computed against; the SVG itself scales to its container. */
const VIEW_WIDTH = 100;
const VIEW_HEIGHT = 40;

/**
 * A hand-rolled SVG polyline — one line does not justify a chart library, and the app's other bars
 * are hand-rolled divs for the same reason. `preserveAspectRatio="none"` stretches the fixed
 * viewBox to the container, so the stroke needs `vector-effect="non-scaling-stroke"` — without it
 * the non-uniform scaling distorts the line width. Decorative colors only; the host renders the
 * numbers as text next to it, so the graphic never carries meaning alone (WCAG 1.4.1).
 *
 * Live days (A10) render as full-height washed bands *behind* the line, not as a bottom strip: a
 * zero-usage day draws the polyline exactly on the bottom edge, where a strip would collide with
 * the very days the marker exists to explain. Low fill-opacity on the success-dot token keeps the
 * band a background; the host's text line (live-day count with a legend swatch) carries the
 * meaning, same division of labour as the peak line.
 */
@Component({
  selector: 'app-usage-sparkline',
  template: `
    <svg
      class="block h-full w-full text-accent-fg"
      [attr.viewBox]="'0 0 ' + viewWidth + ' ' + viewHeight"
      preserveAspectRatio="none"
      role="img"
      [attr.aria-label]="ariaLabel()"
    >
      @for (band of bands(); track band.x) {
        <rect
          class="text-success-dot"
          [attr.x]="band.x"
          y="0"
          [attr.width]="band.width"
          [attr.height]="viewHeight"
          fill="currentColor"
          fill-opacity="0.18"
        />
      }
      @if (polylinePoints(); as linePoints) {
        <polyline
          [attr.points]="linePoints"
          fill="none"
          stroke="currentColor"
          stroke-width="1.5"
          stroke-linejoin="round"
          stroke-linecap="round"
          vector-effect="non-scaling-stroke"
        />
      }
    </svg>
  `,
})
export class UsageSparkline {
  readonly points = input.required<readonly SparklinePoint[]>();
  readonly ariaLabel = input.required<string>();
  /** ISO dates with live coverage; empty (the default) renders no bands — see the class comment. */
  readonly liveDays = input<readonly string[]>([]);

  protected readonly viewWidth = VIEW_WIDTH;
  protected readonly viewHeight = VIEW_HEIGHT;

  protected readonly polylinePoints = computed(() =>
    toPolylinePoints(this.points(), VIEW_WIDTH, VIEW_HEIGHT),
  );

  protected readonly bands = computed(() => liveBands(this.points(), this.liveDays(), VIEW_WIDTH));
}
