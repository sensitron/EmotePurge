import { describe, expect, it } from 'vitest';

import { slotBudget } from './slot-budget';

describe('slotBudget', () => {
  it('projects the free slots a pending removal would create', () => {
    const budget = slotBudget(1000, 847, 235);

    expect(budget).not.toBeNull();
    expect(budget!.occupied).toBe(847);
    expect(budget!.projected).toBe(612);
    expect(budget!.occupiedPercent).toBeCloseTo(84.7);
    expect(budget!.projectedPercent).toBeCloseTo(61.2);
    expect(budget!.hasPendingRemoval).toBe(true);
  });

  it('returns null when 7TV reported no capacity', () => {
    // No denominator means no budget. Falling back to 1000 would understate how full a
    // subscriber's larger set is — and overstate a smaller one.
    expect(slotBudget(null, 847, 0)).toBeNull();
  });

  it('returns null for a non-positive capacity', () => {
    expect(slotBudget(0, 10, 0)).toBeNull();
    expect(slotBudget(-5, 10, 0)).toBeNull();
  });

  it('clamps an occupancy above the capacity', () => {
    // Real state after a 7TV subscription lapses: the set keeps its emotes, the limit drops.
    const budget = slotBudget(600, 900, 0);

    expect(budget!.occupied).toBe(600);
    expect(budget!.occupiedPercent).toBe(100);
    expect(budget!.tone).toBe('red');
  });

  it('clamps a pending removal larger than the occupancy', () => {
    const budget = slotBudget(1000, 10, 50);

    expect(budget!.projected).toBe(0);
    expect(budget!.projectedPercent).toBe(0);
  });

  it('ignores a negative pending removal', () => {
    const budget = slotBudget(1000, 100, -5);

    expect(budget!.projected).toBe(100);
    expect(budget!.hasPendingRemoval).toBe(false);
  });

  it('reports no pending removal when nothing is selected', () => {
    const budget = slotBudget(1000, 100, 0);

    expect(budget!.hasPendingRemoval).toBe(false);
    expect(budget!.projected).toBe(budget!.occupied);
  });

  it('grades the tone at the 80 and 95 percent boundaries', () => {
    expect(slotBudget(1000, 799, 0)!.tone).toBe('emerald');
    expect(slotBudget(1000, 800, 0)!.tone).toBe('amber');
    expect(slotBudget(1000, 949, 0)!.tone).toBe('amber');
    expect(slotBudget(1000, 950, 0)!.tone).toBe('red');
  });

  it('grades the tone on the current occupancy, not on the projection', () => {
    // A selection big enough to empty the set must not repaint a full set as healthy before the
    // delete has actually run.
    const budget = slotBudget(1000, 990, 900);

    expect(budget!.tone).toBe('red');
    expect(budget!.projected).toBe(90);
  });
});
