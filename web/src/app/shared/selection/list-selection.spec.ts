import { describe, expect, it } from 'vitest';

import { ListSelection } from './list-selection';

function click(shiftKey = false): MouseEvent {
  return { shiftKey } as MouseEvent;
}

describe('ListSelection', () => {
  const items = ['a', 'b', 'c', 'd', 'e'];

  it('starts with nothing selected', () => {
    const selection = new ListSelection(() => items);
    expect(selection.selected()).toEqual([]);
    expect(selection.isSelected('a')).toBe(false);
  });

  it('toggles selection on a plain click', () => {
    const selection = new ListSelection(() => items);

    selection.onRowClick('b', 1, click());
    expect(selection.isSelected('b')).toBe(true);
    expect(selection.selected()).toEqual(['b']);

    selection.onRowClick('b', 1, click());
    expect(selection.isSelected('b')).toBe(false);
    expect(selection.selected()).toEqual([]);
  });

  it('selects a contiguous range on shift-click', () => {
    const selection = new ListSelection(() => items);

    selection.onRowClick('a', 0, click());
    selection.onRowClick('d', 3, click(true));

    expect(selection.selected().sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('selects a range regardless of click direction (end before start)', () => {
    const selection = new ListSelection(() => items);

    selection.onRowClick('d', 3, click());
    selection.onRowClick('b', 1, click(true));

    expect(selection.selected().sort()).toEqual(['b', 'c', 'd']);
  });

  it('a shift-click with no prior anchor behaves like a plain toggle', () => {
    const selection = new ListSelection(() => items);

    selection.onRowClick('c', 2, click(true));

    expect(selection.selected()).toEqual(['c']);
  });

  it('clear() empties the selection and resets the shift-click anchor', () => {
    const selection = new ListSelection(() => items);
    selection.onRowClick('a', 0, click());
    selection.onRowClick('c', 2, click(true));

    selection.clear();

    expect(selection.selected()).toEqual([]);
    // Anchor was reset — a subsequent shift-click has nothing to range from.
    selection.onRowClick('e', 4, click(true));
    expect(selection.selected()).toEqual(['e']);
  });
});
