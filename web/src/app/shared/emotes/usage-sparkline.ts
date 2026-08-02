import { Component, computed, input } from '@angular/core';

import { SparklinePoint, toPolylinePoints } from './usage-series';

/** Fixed viewBox the polyline is computed against; the SVG itself scales to its container. */
const VIEW_WIDTH = 100;
const VIEW_HEIGHT = 40;

/**
 * A hand-rolled SVG polyline — one line does not justify a chart library, and the app's other bars
 * are hand-rolled divs for the same reason. `preserveAspectRatio="none"` stretches the fixed
 * viewBox to the container, so the stroke needs `vector-effect="non-scaling-stroke"` — without it
 * the non-uniform scaling distorts the line width. Decorative colors only; the host renders the
 * numbers as text next to it, so the graphic never carries meaning alone (WCAG 1.4.1).
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

  protected readonly viewWidth = VIEW_WIDTH;
  protected readonly viewHeight = VIEW_HEIGHT;

  protected readonly polylinePoints = computed(() =>
    toPolylinePoints(this.points(), VIEW_WIDTH, VIEW_HEIGHT),
  );
}
