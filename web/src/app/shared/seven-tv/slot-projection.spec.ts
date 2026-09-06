import { describe, expect, it } from 'vitest';

import { projectSlots } from './slot-projection';

describe('projectSlots', () => {
  it('returns null when capacity is null', () => {
    expect(projectSlots(10, null, 5)).toBeNull();
  });

  it('returns null when capacity is zero or negative', () => {
    expect(projectSlots(0, 0, 5)).toBeNull();
    expect(projectSlots(0, -1, 5)).toBeNull();
  });

  it('reports no overflow when the projection lands exactly on capacity', () => {
    expect(projectSlots(995, 1000, 5)).toEqual({
      projected: 1000,
      capacity: 1000,
      overflow: false,
    });
  });

  it('reports overflow when the projection exceeds capacity by one', () => {
    expect(projectSlots(996, 1000, 5)).toEqual({ projected: 1001, capacity: 1000, overflow: true });
  });
});
