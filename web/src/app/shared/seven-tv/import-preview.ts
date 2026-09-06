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
  /** Names from `toAdd` that 7TV will reject outright, regardless of the target set's contents —
   *  see `isNameRejectedBySevenTv` for what that covers and why it is deliberately narrow.
   *  Informational only, same as `nameCollisions`: the rows stay in `toAdd`. */
  invalidNames: string[];
}

/**
 * Projects an `ImportSource` onto a target channel's current emotes, splitting it into what the
 * run would add, what it would skip (same 7TV id already present, regardless of name), and what
 * it would add but 7TV will likely reject — either for colliding with an already-used name
 * (`nameCollisions`) or for the name itself being unwritable (`invalidNames`).
 *
 * Both name checks are informational: `toAdd` is not filtered by either of them, 7TV decides.
 * The identity comparison is ordinal (`sevenTvEmoteId`, exact string equality — these are 7TV
 * object ids, not display text); the collision comparison is exact (`===`, case-sensitive)
 * string equality, matching how 7TV itself treats emote names.
 */
export function buildImportPreview(
  source: ImportSource,
  targetEmotes: EmoteListItem[],
): ImportPreview {
  const targetIds = new Set(targetEmotes.map((emote) => emote.sevenTvEmoteId));
  const targetNames = new Set(targetEmotes.map((emote) => emote.name));

  const toAdd: ImportRow[] = [];
  const nameCollisions = new Set<string>();
  const invalidNames = new Set<string>();
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
    if (isNameRejectedBySevenTv(row.name)) {
      invalidNames.add(row.name);
    }
  }

  return {
    toAdd,
    alreadyPresent,
    nameCollisions: [...nameCollisions],
    invalidNames: [...invalidNames],
  };
}

/**
 * True for a name 7TV is known to reject when creating an emote in the target set.
 *
 * Deliberately narrow: the only thing we have *evidence* for is that 7TV rejects non-ASCII
 * characters in the emote name itself (the alias is not affected — 7TV does allow umlauts
 * there). That evidence is two live rejections observed during manual testing of this feature,
 * both `Failed to parse "String": invalid emote name` for a name containing an umlaut (`Hänno`,
 * `HörMalZuBrudi`). We do not otherwise know 7TV's full allowed character set, and guessing at
 * it has gone wrong twice already in this project (#33, #37 — code and test mock shared the same
 * wrong assumption, so the tests stayed green while the behavior was broken). So this check stays
 * exactly as narrow as what was actually observed rejected; if a further rejection reason is
 * observed, extend this check, do not loosen it into a guessed-at allow-list.
 */
function isNameRejectedBySevenTv(name: string): boolean {
  return [...name].some((char) => (char.codePointAt(0) ?? 0) > 0x7f);
}
