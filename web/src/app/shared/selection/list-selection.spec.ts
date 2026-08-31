import { computed, signal } from '@angular/core';
import { describe, expect, it } from 'vitest';

import { ListSelection } from './list-selection';

interface Row {
  id: string;
  label: string;
}

function rows(...ids: string[]): Row[] {
  return ids.map((id) => ({ id, label: id.toUpperCase() }));
}

function click(shiftKey = false): MouseEvent {
  return { shiftKey } as MouseEvent;
}

// The item source is a signal, mirroring how the pages pass a computed() — a plain array would
// never invalidate selectedItems(), so the reorder/refetch cases below could not be observed.
function setup(...ids: string[]) {
  const items = signal(rows(...ids));
  const selection = new ListSelection<Row>(items, (row) => row.id);
  const byId = (id: string): Row => items().find((row) => row.id === id)!;
  return { items, selection, byId };
}

describe('ListSelection', () => {
  it('starts with nothing selected', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    expect(selection.selectedKeys()).toEqual([]);
    expect(selection.selectedItems()).toEqual([]);
    expect(selection.isSelected(byId('a'))).toBe(false);
  });

  it('toggles a row on a plain click', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('b'), click());
    expect(selection.isSelected(byId('b'))).toBe(true);
    expect(selection.selectedKeys()).toEqual(['b']);
    expect(selection.selectedItems()).toEqual([byId('b')]);

    selection.onRowClick(byId('b'), click());
    expect(selection.isSelected(byId('b'))).toBe(false);
    expect(selection.selectedKeys()).toEqual([]);
  });

  it('selects a contiguous range on shift-click', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('d'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('selects a range regardless of click direction (end before start)', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('d'), click());
    selection.onRowClick(byId('b'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['b', 'c', 'd']);
  });

  it('a shift-click with no prior anchor behaves like a plain toggle', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('c'), click(true));

    expect(selection.selectedKeys()).toEqual(['c']);
  });

  it('survives a refetch that replaces every item object with an equal-keyed one', () => {
    const { items, selection, byId } = setup('a', 'b', 'c');
    selection.onRowClick(byId('b'), click());

    // Same rows, brand new object identities — what an HTTP refetch produces.
    items.set(rows('a', 'b', 'c'));

    expect(selection.selectedKeys()).toEqual(['b']);
    expect(selection.isSelected(byId('b'))).toBe(true);
    // Resolved against the new objects, so a delete path reading selectedItems() submits fresh data
    // instead of double-counting the stale ones.
    expect(selection.selectedItems()).toEqual([byId('b')]);
    expect(selection.selectedItems()[0]).toBe(byId('b'));
  });

  it('resolves the shift range against the current order, not the order at anchor time', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd', 'e');
    selection.onRowClick(byId('a'), click());

    // Sort direction flipped after the anchor was set.
    items.set(rows('e', 'd', 'c', 'b', 'a'));
    selection.onRowClick(byId('c'), click(true));

    // 'a' sits last now, so the range from the anchor to 'c' is c-b-a. A position-index anchor
    // would have produced e-d-c here — a completely different set of rows.
    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
  });

  it('falls back to a plain toggle when the anchor is no longer in the list', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());

    // 'a' (the anchor) and 'b' filtered out of view.
    items.set(rows('c', 'd'));

    expect(() => selection.onRowClick(byId('d'), click(true))).not.toThrow();
    // Only the clicked row was added — and the invisible 'a' stays authoritatively selected.
    expect(selection.selectedKeys().sort()).toEqual(['a', 'd']);
    expect(selection.selectedItems()).toEqual([byId('d')]);
  });

  it('clear() empties the keys and resets the shift-click anchor', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('c'), click(true));
    expect(selection.selectedKeys()).toHaveLength(3);

    selection.clear();

    expect(selection.selectedKeys()).toEqual([]);
    expect(selection.selectedItems()).toEqual([]);
    // Anchor was reset — a subsequent shift-click has nothing to range from.
    selection.onRowClick(byId('e'), click(true));
    expect(selection.selectedKeys()).toEqual(['e']);
  });

  it('retainVisible() keeps visible selections and drops filtered-out ones', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('c'), click(true)); // a, b, c selected

    // A filter change hides 'a' and 'b'.
    items.set(rows('c', 'd'));
    selection.retainVisible();

    // The invisible rows are gone from the authoritative key set — nothing off-screen can reach
    // the delete path — while the still-visible 'c' survives the filter change (S2-16).
    expect(selection.selectedKeys()).toEqual(['c']);

    // The anchor ('c') is still visible, so shift-click ranges keep working from it.
    selection.onRowClick(byId('d'), click(true));
    expect(selection.selectedKeys().sort()).toEqual(['c', 'd']);
  });

  it('retainVisible() resets the anchor when the anchored row was filtered out', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());

    items.set(rows('b', 'c', 'd'));
    selection.retainVisible();

    expect(selection.selectedKeys()).toEqual([]);
    // Anchor was reset — a shift-click has nothing to range from and degrades to a toggle.
    selection.onRowClick(byId('d'), click(true));
    expect(selection.selectedKeys()).toEqual(['d']);
  });

  it('adds a whole group without dropping what was already selected', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd');

    selection.onRowClick(byId('a'), click());
    selection.selectMany([byId('c'), byId('d')]);

    expect(selection.selectedKeys().sort()).toEqual(['a', 'c', 'd']);
  });

  it('never deselects on a second group action', () => {
    // The atlas's per-band "mark all" is add-only on purpose: a toggle would let one stray click
    // wipe a hand-built selection, and the next step after this button is an irreversible delete.
    const { selection, byId } = setup('a', 'b');

    selection.selectMany([byId('a'), byId('b')]);
    selection.selectMany([byId('a'), byId('b')]);

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b']);
  });

  it('leaves an empty group alone, anchor included', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('a'), click());
    selection.selectMany([]);
    // The anchor is still 'a', so this shift-click ranges a..c rather than degrading to a toggle.
    selection.onRowClick(byId('c'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
  });

  it('moves the shift anchor to the end of the group it added', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd');

    selection.selectMany([byId('a'), byId('b')]);
    selection.onRowClick(byId('d'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('a shift-click after selectMany() stays additive, matching the marked anchor it leaves behind', () => {
    // Regression guard for #40: selectMany() always leaves its anchor marked, so a shift-click
    // right after it must keep extending, never flip into a deselect.
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('a'), click()); // pre-existing selection, unrelated to the group below
    selection.selectMany([byId('c'), byId('d')]); // anchor moves to 'd', which selectMany leaves marked

    selection.onRowClick(byId('e'), click(true)); // range d-e must be added, not removed

    expect(selection.selectedKeys().sort()).toEqual(['a', 'c', 'd', 'e']);
  });

  it('a shift-click after a deselecting click removes the whole range, leaving marks outside it untouched', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e', 'f');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('f'), click()); // a and f selected individually, outside the range below
    selection.onRowClick(byId('c'), click());
    selection.onRowClick(byId('e'), click(true)); // range c-e selected, anchor 'e' is marked

    selection.onRowClick(byId('d'), click()); // deselects 'd' — anchor now points at an unmarked row
    selection.onRowClick(byId('c'), click(true)); // shift-click ranges c-d, anchor 'd' says "deselect"

    expect(selection.selectedKeys().sort()).toEqual(['a', 'e', 'f']);
  });

  it('a further shift-click after a deselecting one keeps deselecting in the same direction', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e', 'f');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('f'), click(true)); // a-f all selected, anchor 'f' marked

    selection.onRowClick(byId('c'), click()); // deselect c, anchor 'c' now unmarked
    selection.onRowClick(byId('d'), click(true)); // range c-d removed, anchor 'd' unmarked

    selection.onRowClick(byId('b'), click(true)); // another shift-click: still deselecting, range b-d

    expect(selection.selectedKeys().sort()).toEqual(['a', 'e', 'f']);
  });

  it('notifies a computed() that reads the selection', () => {
    const { selection, byId } = setup('a', 'b', 'c');
    // Regression guard: with a plain mutable set instead of a signal, these stayed frozen at their
    // first value, which left the mass-delete button reading "(0)" and disabled forever.
    const keyCount = computed(() => selection.selectedKeys().length);
    const itemLabels = computed(() => selection.selectedItems().map((row) => row.label));

    expect(keyCount()).toBe(0);
    expect(itemLabels()).toEqual([]);

    selection.onRowClick(byId('a'), click());
    expect(keyCount()).toBe(1);
    expect(itemLabels()).toEqual(['A']);

    selection.onRowClick(byId('a'), click());
    expect(keyCount()).toBe(0);
    expect(itemLabels()).toEqual([]);
  });
});
