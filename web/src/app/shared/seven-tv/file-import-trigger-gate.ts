/**
 * Whether the file-import trigger (#91, plan §1.2 point 3) — the button that opens the file-based
 * restore/import dialog from the usage-stats page header — should be disabled.
 *
 * Deliberately NOT part of this gate, because both are already enforced by the `@if` block the
 * trigger sits in on the page (`usage-stats-page.html`, the same block that gates "Übertragen…"):
 * - `!isCoarse()` — no 7TV write path without a mouse (§2.5).
 * - an active 7TV set (`activeEmoteSetId()`) — there is no channel/set to validate a file against
 *   without one.
 *
 * Also deliberately NOT part of it, unlike the neighbouring "Übertragen…" button this trigger
 * otherwise mirrors: `atlasOrder().length === 0`. The file path needs no rows loaded in the grid —
 * the chosen file supplies its own.
 *
 * `importScopeCurrent` mirrors `importScopeIsCurrent` (`import-scope.ts`): a channel switch inside
 * the route can leave `channelName()` pointing at the new channel while the set status/rows still
 * belong to the old one. The page computes that boolean the same way it does for "Übertragen…" and
 * hands it in as an input, rather than this gate recomputing it — that keeps the gate a pure
 * function taking only booleans, testable without a TestBed (same shape as `import-shortcut.ts`).
 */
export interface FileImportTriggerGateState {
  /** `SevenTvRunArbiter.activeRun() !== null` — any of the three 7TV-writing runs, not just this one. */
  readonly hasActiveRun: boolean;
  /** See `importScopeIsCurrent` — false during the window right after a same-route channel switch. */
  readonly importScopeCurrent: boolean;
}

export function fileImportTriggerDisabled(state: FileImportTriggerGateState): boolean {
  return state.hasActiveRun || !state.importScopeCurrent;
}
