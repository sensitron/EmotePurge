import { Dialog } from '@angular/cdk/dialog';
import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, firstValueFrom, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ForeignChannelImportTrigger } from './foreign-channel-import-trigger';

// Only the key this trigger itself renders.
const DE_TRANSLATIONS = { import: { foreignChannel: { trigger: 'Fremder Kanal' } } };

describe('ForeignChannelImportTrigger', () => {
  let activeRun: WritableSignal<SevenTvRunKind | null>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    activeRun = signal<SevenTvRunKind | null>(null);
    dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));

    await TestBed.configureTestingModule({
      imports: [
        ForeignChannelImportTrigger,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: EmoteAdminService,
          useValue: {
            getSetStatus: vi.fn(() => new Subject()),
            listEmotes: vi.fn(() => of([])),
            getSetWarning: vi.fn(() => new Subject()),
          } as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvImportService,
          useValue: { startImport: vi.fn() } as unknown as SevenTvImportService,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: SevenTvRunArbiter, useValue: { activeRun } as unknown as SevenTvRunArbiter },
        { provide: Dialog, useValue: { open: dialogOpen } as unknown as Dialog },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): { button: HTMLButtonElement; detect: () => void } {
    const fixture = TestBed.createComponent(ForeignChannelImportTrigger);
    fixture.detectChanges();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button');
    if (!button) {
      throw new Error('no trigger button rendered');
    }
    return { button, detect: () => fixture.detectChanges() };
  }

  it('opens the foreign-channel picker on click', () => {
    const { button } = render();

    button.click();

    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('locks while any 7TV run is active and unlocks when it ends', () => {
    // The arbiter's rule for every run-starting control. No reason text of its own: the running
    // progress in the dock is the reason (§4.2).
    const { button, detect } = render();
    expect(button.disabled).toBe(false);

    activeRun.set('delete');
    detect();
    expect(button.disabled).toBe(true);

    activeRun.set(null);
    detect();
    expect(button.disabled).toBe(false);
  });
});
