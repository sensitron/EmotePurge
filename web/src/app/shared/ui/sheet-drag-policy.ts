/**
 * Whether a downward drag on a bottom sheet ends in dismissal.
 *
 * Split out of the directive so the decision is testable without a DOM, pointer events or a
 * synthetic clock — the same separation ReconnectPolicy and TwitchWatchdogPolicy have from the
 * transports they steer. The directive owns the mechanics; this owns the judgement.
 *
 * Starting values, to be re-judged on a real device. Distance and speed are alternatives because
 * the two natural gestures are different: a deliberate drag travels far and slowly, a flick travels
 * little and fast. The travel floor is what keeps the flick branch from firing on the few pixels a
 * finger moves during an ordinary tap — short movements are fast movements by definition.
 */
export const SHEET_DISMISS_DISTANCE_PX = 96;
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
