import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { signal } from '@angular/core';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { ForeignEmoteGrid } from './foreign-emote-grid';

// Only the keys this component translates.
const DE_TRANSLATIONS = {
  import: {
    foreignChannel: {
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden ({{ loaded }} von {{ totalCount }}).',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        ariaLabel: 'Sortierung',
        none: 'Set-Reihenfolge',
        topAllTime: '7TV global · Top aller Zeiten',
        trending: '7TV global · Trend',
      },
    },
  },
};

/** jsdom has no ResizeObserver, and the CDK viewport reads it during init too. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function row(overrides: Partial<ForeignEmoteRow> = {}): ForeignEmoteRow {
  return {
    sevenTvEmoteId: 'e1',
    name: 'catJAM',
    defaultName: 'catJAM',
    imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
    topAllTime: null,
    trending: null,
    ...overrides,
  };
}

describe('ForeignEmoteGrid', () => {
  let fixture: ComponentFixture<ForeignEmoteGrid>;
  let component: ForeignEmoteGrid;
  let host: HTMLElement;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    await TestBed.configureTestingModule({
      imports: [
        ForeignEmoteGrid,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    fixture = TestBed.createComponent(ForeignEmoteGrid);
    component = fixture.componentInstance;
    host = fixture.nativeElement;
  });

  function render(
    emotes: ForeignEmoteRow[],
    truncated = false,
    totalCount: number | null = null,
  ): void {
    fixture.componentRef.setInput('emotes', emotes);
    fixture.componentRef.setInput('truncated', truncated);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.detectChanges();
  }

  it('shows the empty-set message and no controls when the source set has no emotes', () => {
    render([]);

    expect(host.textContent).toContain('Das aktive 7TV-Set dieses Kanals hat keine Emotes.');
    expect(host.querySelector('[role="group"]')).toBeNull();
  });

  it('exposes the grid as an accessible group', () => {
    render([row()]);

    const group = host.querySelector('[role="group"]');
    expect(group).not.toBeNull();
    expect(group?.getAttribute('aria-label')).toBe('Emote-Auswahl');
  });

  it('does not preselect the 7TV-global score sort — the set order radio is the checked one', () => {
    render([row()]);

    expect(component['sortMode']()).toBe('none');
    const checked = host.querySelector('[role="radio"][aria-checked="true"]');
    expect(checked?.textContent?.trim()).toBe('Set-Reihenfolge');
  });

  it('toggles a single row on click and emits the updated selection', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    const b = row({ sevenTvEmoteId: 'b' });
    render([a, b]);

    const emitted: ForeignEmoteRow[][] = [];
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);

    expect(component['selection'].isSelected(a)).toBe(true);
    expect(component['selection'].isSelected(b)).toBe(false);
    expect(emitted).toEqual([[a]]);

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].isSelected(a)).toBe(false);
    expect(emitted.at(-1)).toEqual([]);
  });

  it('selects a contiguous range on a shift-click, only the selected rows appear in toAdd', () => {
    const rows = ['a', 'b', 'c', 'd', 'e'].map((id) => row({ sevenTvEmoteId: id }));
    render(rows);

    component['onCellClick'](rows[0], { shiftKey: false } as MouseEvent);
    component['onCellClick'](rows[3], { shiftKey: true } as MouseEvent);

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
    // 'e' was never part of the range — the whole point of a per-row selection (spec F4).
    expect(component['selection'].isSelected(rows[4])).toBe(false);
  });

  it('keeps a selection intact across a score-sort change', () => {
    const a = row({ sevenTvEmoteId: 'a', topAllTime: 5 });
    const b = row({ sevenTvEmoteId: 'b', topAllTime: 50 });
    render([a, b]);

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    // Still selected, and still resolves to the very same row object even though the display
    // order flipped — ListSelection is keyed, not positional (spec F4 / list-selection.ts).
    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selection'].isSelected(a)).toBe(true);
    expect(component['selection'].selectedItems()).toEqual([a]);
  });

  it('announces a truncated load without silently dropping it (spec F3/AK5)', () => {
    render([row(), row({ sevenTvEmoteId: 'e2' })], true, 10);

    const status = host.querySelector('[role="status"]');
    expect(status).not.toBeNull();
    expect(status?.textContent).toContain('2');
    expect(status?.textContent).toContain('10');
  });

  it('labels a cell with the set alias, and additionally the default name when it differs', () => {
    const aliased = row({ name: 'PogChamp2', defaultName: 'PogChamp' });
    const plain = row({ sevenTvEmoteId: 'e2', name: 'catJAM', defaultName: 'catJAM' });

    expect(component['cellLabel'](aliased)).toBe('PogChamp2 (PogChamp)');
    expect(component['cellLabel'](plain)).toBe('catJAM');
  });

  it('formats the active 7TV-global score compactly, and a missing score as a dash', () => {
    render([row()]);
    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    expect(component['scoreBadge'](row({ topAllTime: 12400 }))).toBe('12,4k');
    expect(component['scoreBadge'](row({ topAllTime: 42 }))).toBe('42');
    expect(component['scoreBadge'](row({ topAllTime: null }))).toBe('–');
  });
});
