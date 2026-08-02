/**
 * Severity tiers of the slot-budget bar, purely derived from how full the set is. Named after the
 * meaning rather than the hue for the same reason `StatusBadgeTone` is: the value behind each tier
 * differs per theme, so a tier called `red` would be promising something it cannot keep.
 */
export type SlotBudgetTone = 'success' | 'warning' | 'danger';

/** Above this share of the capacity the bar warns, above the second one it alarms. */
const WARN_AT_PERCENT = 80;
const CRITICAL_AT_PERCENT = 95;

export interface SlotBudget {
  capacity: number;
  /** Slots taken right now, clamped into [0, capacity]. */
  occupied: number;
  /** What would be left occupied once the pending removal runs, clamped into [0, occupied]. */
  projected: number;
  occupiedPercent: number;
  projectedPercent: number;
  /** Whether the projection differs from the current state — the bar only splits when it does. */
  hasPendingRemoval: boolean;
  tone: SlotBudgetTone;
}

/**
 * Turns the raw set status plus a pending removal count into everything the bar renders.
 *
 * Returns `null` when 7TV reported no capacity: without a denominator there is no budget to show,
 * and assuming 1000 would be wrong for exactly the subscriber sets where headroom matters most.
 *
 * Both inputs are clamped rather than trusted. `occupied` can legitimately exceed `capacity` after
 * a 7TV subscription lapses (the set keeps its emotes, the limit drops), and `pendingRemoval` comes
 * from a selection that a concurrent live update may have shrunk.
 */
export function slotBudget(
  capacity: number | null,
  occupied: number,
  pendingRemoval: number,
): SlotBudget | null {
  if (capacity === null || capacity <= 0) {
    return null;
  }

  const clampedOccupied = Math.min(Math.max(occupied, 0), capacity);
  const clampedRemoval = Math.min(Math.max(pendingRemoval, 0), clampedOccupied);
  const projected = clampedOccupied - clampedRemoval;

  return {
    capacity,
    occupied: clampedOccupied,
    projected,
    occupiedPercent: (clampedOccupied / capacity) * 100,
    projectedPercent: (projected / capacity) * 100,
    hasPendingRemoval: clampedRemoval > 0,
    // Graded on the current occupancy, not the projection: the colour answers "how full is the set
    // right now", and a large selection must not make a full set look healthy before it is emptied.
    tone: toneFor((clampedOccupied / capacity) * 100),
  };
}

function toneFor(percent: number): SlotBudgetTone {
  if (percent >= CRITICAL_AT_PERCENT) {
    return 'danger';
  }
  return percent >= WARN_AT_PERCENT ? 'warning' : 'success';
}
