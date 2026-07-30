import { describe, expect, it, vi } from 'vitest';

import { EmoteUsageFilter } from './emote-usage-filter';

interface Row {
  emoteName: string;
  totalUseCount: number;
}

const ROWS: Row[] = [
  { emoteName: 'peepoHappy', totalUseCount: 0 },
  { emoteName: 'peepoSad', totalUseCount: 5 },
  { emoteName: 'catJAM', totalUseCount: 120 },
];

describe('EmoteUsageFilter', () => {
  it('starts inactive and passes everything through', () => {
    const filter = new EmoteUsageFilter<Row>();

    expect(filter.isAnyActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
  });

  it('filters by min/max count and bare-substring name query', () => {
    const filter = new EmoteUsageFilter<Row>();

    filter.setMinCount('1');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoSad', 'catJAM']);

    filter.setMaxCount('10');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoSad']);

    // A bare query is an unanchored substring match — only typing * or ? opts into glob anchoring.
    filter.setMinCount('');
    filter.setMaxCount('');
    filter.setNameFilter('peepo');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoHappy', 'peepoSad']);

    expect(filter.isAnyActive()).toBe(true);
  });

  it('reset() clears every filter and notifies the host once', () => {
    const onChange = vi.fn();
    const filter = new EmoteUsageFilter<Row>(onChange);
    filter.setMinCount('1');
    filter.setNameFilter('cat');
    onChange.mockClear();

    filter.reset();

    expect(filter.isAnyActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('toggleUnused() sets and clears the 0/0 range', () => {
    const filter = new EmoteUsageFilter<Row>();

    filter.toggleUnused();
    expect(filter.isUnusedActive()).toBe(true);
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoHappy']);

    filter.toggleUnused();
    expect(filter.isUnusedActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
  });
});
