import { ImportRow, ImportSource, dedupeImportRows } from '../../core/seven-tv/import-source';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import {
  ForeignChannelImportResult,
  openForeignChannelImportDialog,
} from './foreign-channel-import-dialog';
import { ImportFlowDeps, startImportFlow } from './import-flow';
import { openImportTargetDialog } from './import-target-dialog';

/**
 * The third import source, wired to the chain the other two already use (spec T4): pick a foreign
 * channel and its emotes → pick the target channel → the ordinary confirm/token/run flow.
 *
 * Dialogs run one after another, never nested — each `closed` fires after its dialog is gone, which
 * is what keeps the app's one-dialog-at-a-time rule (`shared/ui/dialog.ts`) intact and lets
 * `startImportFlow` keep its own ordering (confirm first, 7TV token only afterwards).
 *
 * Fremd ist die Quelle, nie das Ziel: the target still comes from `listMine()` inside
 * `ImportTargetDialog`, so an untracked channel can be read here but never written into. That is
 * existing behaviour of that dialog, not something this flow adds or could opt out of.
 */
export function startForeignChannelImportFlow(deps: ImportFlowDeps): void {
  openForeignChannelImportDialog(deps.dialog).closed.subscribe((picked) => {
    if (!picked) {
      return;
    }
    startTargetPick(deps, buildForeignImportSource(picked), picked.channelName);
  });
}

/**
 * The picked rows as an `ImportSource`. The name taken over is the source set's **alias** (`name`),
 * not `defaultName`: the copy should keep the name the source channel knew the emote by, which is
 * also what the ADD mutation sends. It is the reason the target set's collision hint matters for
 * this source at all — an alias is far likelier to clash than a global base name (spec E7).
 *
 * `discardedRows` is `0` for the same reason it is for the tracked-channel grid: these rows came out
 * of our own endpoint already parsed into `ForeignEmoteRow`, so there is nothing left to reject.
 * `dedupeImportRows` still runs — the engine never deduplicates, and this is the one place that can
 * promise it for this source.
 */
export function buildForeignImportSource(picked: ForeignChannelImportResult): ImportSource {
  const deduped = dedupeImportRows(picked.rows.map(toImportRow));
  return {
    origin: { kind: 'seventv-channel', channelName: picked.channelName },
    rows: deduped.rows,
    duplicatesCollapsed: deduped.duplicatesCollapsed,
    discardedRows: 0,
  };
}

function toImportRow(row: ForeignEmoteRow): ImportRow {
  return { sevenTvEmoteId: row.sevenTvEmoteId, name: row.name };
}

/**
 * `forcedScope: 'selection'` is the point of this step (spec §7): the user has just marked emotes
 * one by one in the picker, so offering "the selection or everything visible" would ask the same
 * question twice and let the second answer overrule the first. With the scope forced there is no
 * radiogroup at all, and the counts below are only carried for completeness — nothing renders them.
 *
 * `currentChannelName` is the *source* channel, which is what excludes it from the target list: a
 * copy of a set into itself is the one target that can never make sense.
 */
function startTargetPick(
  deps: ImportFlowDeps,
  source: ImportSource,
  sourceChannelName: string,
): void {
  openImportTargetDialog(deps.dialog, {
    currentChannelName: sourceChannelName,
    visibleCount: source.rows.length,
    selectionCount: source.rows.length,
    forcedScope: 'selection',
  }).closed.subscribe((choice) => {
    if (!choice) {
      return;
    }
    startImportFlow(deps, source, choice.channelName);
  });
}
