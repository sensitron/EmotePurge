/**
 * Whether a downward drag on a bottom sheet ends in dismissal.
 *
 * Split out of the directive so the decision is testable without a DOM, pointer events or a
 * synthetic clock — the same separation ReconnectPolicy and TwitchWatchdogPolicy have from the
 * transports they steer. The directive owns the mechanics; this owns the judgement.
 *
 * Distance and speed are alternatives because the two natural gestures are different: a deliberate
 * drag travels far and slowly, a flick travels little and fast. The travel floor is what keeps the
 * flick branch from firing on the few pixels a finger moves during an ordinary tap — short movements
 * are fast movements by definition.
 *
 * The distance was 96 px until it was judged against a thumb (2026-08-07) and settled at 72. The
 * argument for the shorter pull: by the time the finger has travelled that far the sheet has been
 * following it for a while, so it has visibly agreed to move, and springing back then reads as the
 * sheet refusing rather than as a gesture left unfinished. 72 px is about a third of the way down a
 * phone-sized sheet and still three times the travel floor, so nothing about the boundary between a
 * tap and a drag changes. The speed threshold stayed where it was; what was wrong there was the
 * measurement, not the number (see VELOCITY_WINDOW_MS in sheet-drag.ts).
 */
export const SHEET_DISMISS_DISTANCE_PX = 72;
export const SHEET_DISMISS_VELOCITY_PX_PER_MS = 0.5;
export const SHEET_MIN_TRAVEL_PX = 24;

export function shouldDismiss(distancePx: number, velocityPxPerMs: number): boolean {
  if (distancePx < SHEET_MIN_TRAVEL_PX) {
    return false;
  }
  return (
    distancePx >= SHEET_DISMISS_DISTANCE_PX || velocityPxPerMs >= SHEET_DISMISS_VELOCITY_PX_PER_MS
  );
}
