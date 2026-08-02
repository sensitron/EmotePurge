import { EmoteDailyUsage } from '../../core/usage-stats/usage-stat.model';

/**
 * Pure helpers between the sparse `/usage-stats/daily` response and the sparkline. The server only
 * transports days with actual usage; the fixed-width array the polyline needs is built here.
 */

export interface SparklinePoint {
  /** ISO date, `yyyy-MM-dd`. */
  date: string;
  useCount: number;
}

const DAY_MS = 86_400_000;

/**
 * Fills [from, to] day by day; missing days become 0. Days outside the range are ignored — the
 * server clips the range already, this just refuses to trust it. An invalid or inverted range
 * yields `[]`.
 */
export function fillDailySeries(
  days: readonly EmoteDailyUsage[],
  from: string,
  to: string,
): SparklinePoint[] {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) {
    return [];
  }

  const byDate = new Map(days.map((day) => [day.date, day.useCount]));
  const points: SparklinePoint[] = [];
  for (let time = start; time <= end; time += DAY_MS) {
    const date = new Date(time).toISOString().slice(0, 10);
    points.push({ date, useCount: byDate.get(date) ?? 0 });
  }
  return points;
}

/**
 * SVG polyline points in a 0..width / 0..height viewBox, y inverted (0 at the bottom edge). The
 * maximum is clamped to >= 1 so an all-zero series draws a flat baseline instead of dividing by
 * zero. A single point renders as a full-width flat line — one dot would be invisible.
 */
export function toPolylinePoints(
  points: readonly SparklinePoint[],
  width: number,
  height: number,
): string {
  if (points.length === 0) {
    return '';
  }

  const max = Math.max(1, ...points.map((point) => point.useCount));
  if (points.length === 1) {
    const y = round(height - (points[0].useCount / max) * height);
    return `0,${y} ${round(width)},${y}`;
  }

  const stepX = width / (points.length - 1);
  return points
    .map((point, index) => {
      const y = height - (point.useCount / max) * height;
      return `${round(index * stepX)},${round(y)}`;
    })
    .join(' ');
}

/** The busiest day; on a tie the earliest wins. `null` for an empty or all-zero series. */
export function seriesPeak(
  points: readonly SparklinePoint[],
): { useCount: number; date: string } | null {
  let peak: SparklinePoint | null = null;
  for (const point of points) {
    if (point.useCount > (peak?.useCount ?? 0)) {
      peak = point;
    }
  }
  return peak ? { useCount: peak.useCount, date: peak.date } : null;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}
