import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';

export interface ImportPreview {
  /** Source rows the run would actually submit — everything except rows already in the target. */
  toAdd: ImportRow[];
  /** Count of source rows whose `sevenTvEmoteId` is already in the target set (excluded from
   *  `toAdd`, not double-counted with `nameCollisions`). */
  alreadyPresent: number;
  /** Names from `toAdd` that 7TV will reject for colliding with an existing name in the target
   *  set — informational only, the rows stay in `toAdd` (7TV decides, not this preview). */
  nameCollisions: string[];
}

/**
 * Projects an `ImportSource` onto a target channel's current emotes, splitting it into what the
 * run would add, what it would skip (same 7TV id already present, regardless of name), and what
 * it would add but 7TV will likely reject (a different id claiming an already-used name).
 *
 * Both comparisons are ordinal: identity by `sevenTvEmoteId` (exact string equality — these are
 * 7TV object ids, not display text), name collisions by exact (`===`, case-sensitive) string
 * equality, matching how 7TV itself treats emote names.
 */
export function buildImportPreview(
  source: ImportSource,
  targetEmotes: EmoteListItem[],
): ImportPreview {
  const targetIds = new Set(targetEmotes.map((emote) => emote.sevenTvEmoteId));
  const targetNames = new Set(targetEmotes.map((emote) => emote.name));

  const toAdd: ImportRow[] = [];
  const nameCollisions = new Set<string>();
  let alreadyPresent = 0;

  for (const row of source.rows) {
    if (targetIds.has(row.sevenTvEmoteId)) {
      alreadyPresent++;
      continue;
    }
    toAdd.push(row);
    if (targetNames.has(row.name)) {
      nameCollisions.add(row.name);
    }
  }

  return { toAdd, alreadyPresent, nameCollisions: [...nameCollisions] };
}
