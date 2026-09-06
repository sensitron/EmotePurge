export interface SlotProjection {
  projected: number;
  capacity: number;
  overflow: boolean;
}

/**
 * Projects a channel's occupied slots forward by a pending addition — the restore and import
 * confirm dialogs' "you'll have N / capacity" line. `null` when 7TV reports no usable capacity
 * (`null` or `<= 0`): without a real denominator there is nothing to project.
 *
 * Not a replacement for `shared/emotes/slot-budget.ts`: that one models a *removal* and clamps the
 * result into range, because a pending delete can never push occupancy below zero. This one models
 * an *addition*, where going over capacity is exactly the case worth flagging — clamping it away
 * would hide the one thing this function exists to report (`overflow`).
 */
export function projectSlots(
  occupied: number,
  capacity: number | null,
  toAdd: number,
): SlotProjection | null {
  if (capacity === null || capacity <= 0) {
    return null;
  }

  const projected = occupied + toAdd;
  return { projected, capacity, overflow: projected > capacity };
}
