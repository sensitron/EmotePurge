import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { FileImportStep } from './file-import-step';
import { ForeignChannelStep } from './foreign-channel-step';
import { ImportSourceDialog, ImportSourceDialogResult } from './import-source-dialog';

const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen' },
  restore: {
    import: {
      title: 'Datei importieren',
      sorts: {
        purgeRun: 'Purge-Protokoll (Wiederherstellen) als JSON',
        emoteList: 'Emote-Liste (Kopieren) als JSON',
        usageExport: 'Nutzungs-Export (Kopieren) als JSON',
      },
      fileLabel: 'Datei auswählen',
    },
  },
  import: {
    source: {
      title: 'Emotes importieren',
      legend: 'Woher kommen die Emotes?',
      back: 'Zurück',
      file: { label: 'Aus einer Datei', hint: 'Purge-Protokoll, Emote-Liste oder Nutzungs-Export' },
      channel: { label: 'Aus einem Kanal', hint: 'Das aktive 7TV-Set eines Twitch-Kanals' },
    },
    foreignChannel: {
      title: 'Aus einem Kanal importieren',
      channelLabel: 'Kanalname',
      placeholder: 'z. B. handofblood',
      invalidChannelName: 'Kein gültiger Twitch-Kanalname.',
      load: 'Set laden',
      reload: 'Neu laden',
      retry: 'Erneut versuchen',
      continue: 'Weiter',
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden.',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        label: 'Sortieren nach',
        none: 'Set-Reihenfolge',
        topAllTime: 'Top',
        trending: 'Trend',
        scoreHint: 'Die Zahl sagt, in wie vielen 7TV-Sets das Emote steckt.',
      },
    },
  },
};

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

const FOREIGN_SET: ForeignEmoteSetResponse = {
  channelName: 'handofblood',
  sevenTvUserId: 'user-1',
  emoteSetId: 'set-1',
  totalCount: 1,
  truncated: false,
  emotes: [
    {
      sevenTvEmoteId: 'e1',
      name: 'catJAM',
      defaultName: 'catJAM',
      imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
      topAllTime: null,
      trending: null,
    },
  ],
};

describe('ImportSourceDialog', () => {
  let fixture: ComponentFixture<ImportSourceDialog>;
  let host: HTMLElement;
  let httpMock: HttpTestingController;
  let closed: (ImportSourceDialogResult | undefined)[];
  let addPanelClass: ReturnType<typeof vi.fn>;
  let removePanelClass: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    closed = [];
    addPanelClass = vi.fn();
    removePanelClass = vi.fn();

    await TestBed.configureTestingModule({
      imports: [
        ImportSourceDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: { channelName: 'somechannel', setId: 'set-current' } },
        {
          provide: DialogRef,
          useValue: {
            close: (result?: ImportSourceDialogResult) => closed.push(result),
            overlayRef: { addPanelClass, removePanelClass },
          },
        },
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ImportSourceDialog);
    host = fixture.nativeElement;
    fixture.detectChanges();
  });

  function button(label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === label,
    );
    if (!found) {
      throw new Error(`no button labelled "${label}"`);
    }
    return found;
  }

  /** A source row of the first step. Its accessible name is label plus hint, deliberately — the
   *  same two-line label the export dialog's options use (§7.4). */
  function sourceOption(label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find((candidate) =>
      candidate.textContent?.trim().startsWith(label),
    );
    if (!found) {
      throw new Error(`no source option labelled "${label}"`);
    }
    return found;
  }

  function heading(): string {
    return host.querySelector('h2')?.textContent?.trim() ?? '';
  }

  function goToChannelStep(): void {
    sourceOption('Aus einem Kanal').click();
    fixture.detectChanges();
  }

  /** Loads a set in the channel step and marks one emote in it — what unlocks "Weiter". */
  function markOneEmote(): void {
    const step = fixture.debugElement.query(By.directive(ForeignChannelStep))
      .componentInstance as ForeignChannelStep;
    const internals = step as unknown as {
      channelNameControl: { setValue(value: string): void };
      submit(): void;
      onSelectionChange(rows: unknown[]): void;
    };

    internals.channelNameControl.setValue('handofblood');
    internals.submit();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(FOREIGN_SET);
    fixture.detectChanges();

    internals.onSelectionChange(FOREIGN_SET.emotes);
    fixture.detectChanges();
  }

  describe('the source choice is the first step (spec E1)', () => {
    it('offers every source and starts none of them', () => {
      expect(heading()).toBe('Emotes importieren');
      expect(sourceOption('Aus einer Datei')).toBeTruthy();
      expect(sourceOption('Aus einem Kanal')).toBeTruthy();
      // Nothing of either branch is mounted yet — the choice is a choice, not a preview.
      expect(host.querySelector('input[type="file"]')).toBeNull();
      expect(host.querySelector('app-foreign-channel-step')).toBeNull();
    });

    it('names the chosen branch in the heading and can be walked back', () => {
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();
      expect(heading()).toBe('Datei importieren');
      expect(host.querySelector('input[type="file"]')).not.toBeNull();

      button('Zurück').click();
      fixture.detectChanges();
      expect(heading()).toBe('Emotes importieren');
      expect(host.querySelector('input[type="file"]')).toBeNull();
    });

    it('has no way back on the first step, since there is nowhere to go', () => {
      expect(
        Array.from(host.querySelectorAll('button')).some(
          (candidate) => candidate.textContent?.trim() === 'Zurück',
        ),
      ).toBe(false);
    });
  });

  describe('the pane widens for the grid and for nothing else', () => {
    it('stays narrow on every form state, including the channel step before a set has loaded', () => {
      // All three carry a form's worth of content and looked lost at 72rem. Entering the channel
      // branch is explicitly not enough — the grid is what needs the width.
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();
      expect(addPanelClass).not.toHaveBeenCalled();

      button('Zurück').click();
      fixture.detectChanges();
      goToChannelStep();

      expect(addPanelClass).not.toHaveBeenCalled();
    });

    it('widens when the set arrives and narrows again when it is gone', () => {
      goToChannelStep();
      markOneEmote();
      expect(addPanelClass).toHaveBeenCalledWith('app-dialog-panel-wide');

      // The one resize coincides with "Set laden"; going back takes the width with it.
      button('Zurück').click();
      fixture.detectChanges();

      expect(removePanelClass).toHaveBeenLastCalledWith('app-dialog-panel-wide');
    });
  });

  describe('closing contract', () => {
    it('closes with whatever the file step read, unchanged', () => {
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();

      const step = fixture.debugElement.query(By.directive(FileImportStep))
        .componentInstance as FileImportStep;
      step.picked.emit({ kind: 'restore', rows: [] });

      expect(closed).toEqual([{ kind: 'restore', rows: [] }]);
    });

    it('keeps "Weiter" locked until the channel step has something to carry forward', () => {
      goToChannelStep();
      expect(button('Weiter').disabled).toBe(true);

      markOneEmote();

      expect(button('Weiter').disabled).toBe(false);
    });

    it('closes with the foreign source and its picked rows', () => {
      goToChannelStep();
      markOneEmote();

      button('Weiter').click();

      expect(closed).toEqual([
        {
          kind: 'foreign',
          picked: {
            channelName: 'handofblood',
            sevenTvUserId: 'user-1',
            emoteSetId: 'set-1',
            rows: FOREIGN_SET.emotes,
          },
        },
      ]);
    });

    it('cancel closes with no result, from any step', () => {
      goToChannelStep();

      button('Abbrechen').click();

      expect(closed).toEqual([undefined]);
    });
  });
});
