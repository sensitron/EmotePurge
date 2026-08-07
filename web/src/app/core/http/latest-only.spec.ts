import { Subject, throwError } from 'rxjs';
import { describe, expect, it } from 'vitest';

import { latestOnly } from './latest-only';

describe('latestOnly', () => {
  it('passes the value through when nothing superseded the request', () => {
    const guard = latestOnly<string>();
    const request = new Subject<string>();
    const seen: string[] = [];

    request.pipe(guard).subscribe((value) => seen.push(value));
    request.next('answer');

    expect(seen).toEqual(['answer']);
  });

  it('drops the answer of a request that was superseded before it came back', () => {
    // The regression this guards: the usage-stats page fires one series request against a
    // placeholder range and a second against the resolved one. Without this, whichever answers
    // last wins, and the sidecar silently draws a year-wide axis for a channel tracked for days.
    const guard = latestOnly<string>();
    const stale = new Subject<string>();
    const current = new Subject<string>();
    const seen: string[] = [];

    stale.pipe(guard).subscribe((value) => seen.push(value));
    current.pipe(guard).subscribe((value) => seen.push(value));

    current.next('resolved range');
    stale.next('placeholder range');

    expect(seen).toEqual(['resolved range']);
  });

  it('drops the failure of a superseded request, so it cannot raise an error state', () => {
    const guard = latestOnly<string>();
    const stale = new Subject<string>();
    const current = new Subject<string>();
    let failed = false;

    stale.pipe(guard).subscribe({ error: () => (failed = true) });
    current.pipe(guard).subscribe({ error: () => (failed = true) });

    stale.error(new Error('too late'));

    expect(failed).toBe(false);
  });

  it('still reports the failure of the request that is current', () => {
    const guard = latestOnly<string>();
    let failed = false;

    throwError(() => new Error('current'))
      .pipe(guard)
      .subscribe({ error: () => (failed = true) });

    expect(failed).toBe(true);
  });

  it('counts generations per instance, so two guards do not supersede each other', () => {
    const totals = latestOnly<string>();
    const series = latestOnly<string>();
    const totalsRequest = new Subject<string>();
    const seriesRequest = new Subject<string>();
    const seen: string[] = [];

    totalsRequest.pipe(totals).subscribe((value) => seen.push(value));
    seriesRequest.pipe(series).subscribe((value) => seen.push(value));

    totalsRequest.next('totals');
    seriesRequest.next('series');

    expect(seen).toEqual(['totals', 'series']);
  });
});
