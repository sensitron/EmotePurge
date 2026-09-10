import { firstValueFrom, of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { filterAlreadyPresent } from './already-present-filter';

interface Row {
  sevenTvEmoteId: string;
  name: string;
}

function fakeAdminService(targetEmotes: EmoteListItem[] | 'error'): EmoteAdminService {
  const listEmotes =
    targetEmotes === 'error'
      ? vi.fn(() => throwError(() => new Error('network error')))
      : vi.fn(() => of(targetEmotes));
  return { listEmotes } as unknown as EmoteAdminService;
}

describe('filterAlreadyPresent', () => {
  it('passes every row through unfiltered when the target set is empty', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result = await firstValueFrom(
      filterAlreadyPresent(fakeAdminService([]), 'sensitron', rows),
    );

    expect(result).toEqual({ rows, skipped: 0, available: true });
  });

  it('drops a row already present in the target set, keyed on sevenTvEmoteId regardless of name', () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    // Same 7TV id as row 1, but a *different* alias — exactly the #149/T5 hole: 7TV's addEmote
    // only checks the alias string, not the emote id, so this would otherwise be pushed twice.
    const target: EmoteListItem[] = [{ sevenTvEmoteId: '7tv-1', name: 'SomeOtherAlias' }];

    return firstValueFrom(filterAlreadyPresent(fakeAdminService(target), 'sensitron', rows)).then(
      (result) => {
        expect(result).toEqual({ rows: [rows[1]], skipped: 1, available: true });
      },
    );
  });

  it('drops every row when all are already present, without erroring on the empty result', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result = await firstValueFrom(
      filterAlreadyPresent(fakeAdminService(target), 'sensitron', rows),
    );

    expect(result).toEqual({ rows: [], skipped: 2, available: true });
  });

  it('fetches the given channel — a fresh call, never a cached/shared result', async () => {
    const adminService = fakeAdminService([]);

    await firstValueFrom(filterAlreadyPresent(adminService, 'sensitron', []));

    expect(adminService.listEmotes).toHaveBeenCalledWith('sensitron');
    expect(adminService.listEmotes).toHaveBeenCalledTimes(1);
  });

  // Fails open: a run the user already confirmed must not be blocked by a failed best-effort check.
  // But it must not read as a clean all-clear either — that is `available`'s whole job, see the two
  // cases below.
  it('fails open on a failed fetch, returning every row unfiltered and reporting the check as unavailable', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result = await firstValueFrom(
      filterAlreadyPresent(fakeAdminService('error'), 'sensitron', rows),
    );

    expect(result).toEqual({ rows, skipped: 0, available: false });
  });

  // The whole point of `available`: "nothing needed skipping" and "nothing could be checked" must
  // never collapse into the same `skipped: 0` a caller can no longer tell apart (the defect this
  // filter used to have).
  it('distinguishes a successful check that found nothing to skip from a check that could not run at all', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const checked = await firstValueFrom(
      filterAlreadyPresent(fakeAdminService([]), 'sensitron', rows),
    );
    const unverified = await firstValueFrom(
      filterAlreadyPresent(fakeAdminService('error'), 'sensitron', rows),
    );

    expect(checked).toEqual({ rows, skipped: 0, available: true });
    expect(unverified).toEqual({ rows, skipped: 0, available: false });
    expect(checked.available).not.toBe(unverified.available);
  });
});
