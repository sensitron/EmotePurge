import { describe, expect, it } from 'vitest';

import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { buildImportPreview } from './import-preview';

function source(rows: ImportRow[]): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'sensitron' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

describe('buildImportPreview', () => {
  it('separates already-present rows (matched by id) from name collisions and the rest', () => {
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'existing-1', name: 'AlreadyThere' },
      { sevenTvEmoteId: 'existing-2', name: 'PogU' },
    ];

    const result = buildImportPreview(
      source([
        // Same id as an existing target emote, but a different name — still alreadyPresent, not
        // a collision: identity is by sevenTvEmoteId, never by name.
        { sevenTvEmoteId: 'existing-1', name: 'RenamedOnSource' },
        // New id whose name collides with a target emote's name — stays in toAdd, and is reported.
        { sevenTvEmoteId: 'new-1', name: 'PogU' },
        // Clean addition, no overlap at all.
        { sevenTvEmoteId: 'new-2', name: 'Kappa' },
      ]),
      target,
    );

    expect(result.alreadyPresent).toBe(1);
    expect(result.toAdd).toEqual([
      { sevenTvEmoteId: 'new-1', name: 'PogU' },
      { sevenTvEmoteId: 'new-2', name: 'Kappa' },
    ]);
    expect(result.nameCollisions).toEqual(['PogU']);
    expect(result.invalidNames).toEqual([]);
  });

  it('compares names ordinally — a case difference is not a collision', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'pogu' }]), target);

    expect(result.nameCollisions).toEqual([]);
    expect(result.toAdd).toEqual([{ sevenTvEmoteId: 'new-1', name: 'pogu' }]);
  });

  it('reports a name colliding with more than one duplicate target name only once', () => {
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'existing-1', name: 'Dupe' },
      { sevenTvEmoteId: 'existing-2', name: 'Dupe' },
    ];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'Dupe' }]), target);

    expect(result.nameCollisions).toEqual(['Dupe']);
  });

  it('returns an empty toAdd list when every source row is already present', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'existing-1', name: 'PogU' }]),
      target,
    );

    expect(result.toAdd).toEqual([]);
    expect(result.alreadyPresent).toBe(1);
    expect(result.nameCollisions).toEqual([]);
  });

  // 7TV v4's alias validator accepts Unicode — v3's did not, which is what the old version of this
  // check (and this test) got wrong. See `isNameRejectedBySevenTv` for the evidence.
  it('does not flag a Unicode alias — 7TV v4 accepts letters of any script in the alias', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(
      source([
        { sevenTvEmoteId: 'new-1', name: 'Gänsehosen' },
        { sevenTvEmoteId: 'new-2', name: 'Привет' },
        { sevenTvEmoteId: 'new-3', name: 'Kappa' },
      ]),
      target,
    );

    expect(result.invalidNames).toEqual([]);
    expect(result.toAdd.map((row) => row.sevenTvEmoteId)).toEqual(['new-1', 'new-2', 'new-3']);
  });

  it('flags an alias containing a space as invalid — measured rejected by 7TV v4', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(
      source([
        { sevenTvEmoteId: 'new-1', name: 'Two Words' },
        { sevenTvEmoteId: 'new-2', name: 'Kappa' },
      ]),
      target,
    );

    expect(result.invalidNames).toEqual(['Two Words']);
    // Informational only, like nameCollisions: the rows are not filtered out of toAdd.
    expect(result.toAdd.map((row) => row.sevenTvEmoteId)).toEqual(['new-1', 'new-2']);
  });

  it('flags an alias containing a measured-rejected punctuation character', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'a/b' }]), target);

    expect(result.invalidNames).toEqual(['a/b']);
  });

  it('flags an alias over 100 characters as invalid', () => {
    const target: EmoteListItem[] = [];
    const tooLong = 'a'.repeat(101);

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: tooLong }]), target);

    expect(result.invalidNames).toEqual([tooLong]);
  });

  it('does not flag an alias at exactly the 100-character limit', () => {
    const target: EmoteListItem[] = [];
    const atLimit = 'a'.repeat(100);

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: atLimit }]), target);

    expect(result.invalidNames).toEqual([]);
  });

  it('does not flag a name that is only an ASCII name collision', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'PogU' }]), target);

    expect(result.nameCollisions).toEqual(['PogU']);
    expect(result.invalidNames).toEqual([]);
  });

  it('reports a name that is both an invalid name and a collision in both lists', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'a/b' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'a/b' }]), target);

    expect(result.nameCollisions).toEqual(['a/b']);
    expect(result.invalidNames).toEqual(['a/b']);
  });
});
