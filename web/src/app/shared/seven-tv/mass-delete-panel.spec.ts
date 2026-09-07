import { Dialog } from '@angular/cdk/dialog';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { DeletableEmote, MassDeletePanel } from './mass-delete-panel';

/**
 * Only the row-composition contract (design doc §8.7): constructive group before the destructive
 * action, a neutral exit after it, and no leftover gap when the host page has nothing to project —
 * checked through accessible button names/order, never through the Tailwind classes that produce
 * the spacing (Regel 12). The delete/restore *flows* this panel also drives are exercised
 * elsewhere-equivalent components (`restore-panel.spec.ts`), not here.
 */

const DE_TRANSLATIONS = {
  massDelete: {
    deleteButton: 'Löschen ({{ count }})',
    clearSelection: 'Auswahl aufheben',
  },
};

const DELETE_LABEL = 'Löschen (2)';
const CLEAR_LABEL = 'Auswahl aufheben';
const VOTE_LABEL = 'Zur Abstimmung stellen';

const EMOTES: DeletableEmote[] = [
  { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

@Component({
  selector: 'app-host',
  imports: [MassDeletePanel],
  template: `
    <app-mass-delete-panel
      [setId]="setId"
      [channelName]="channelName"
      [selectedEmotes]="emotes"
      [leadingActionsPresent]="leadingActionsPresent"
    >
      @if (projectVoteButton) {
        <button type="button" selection-actions>{{ voteLabel }}</button>
      }
    </app-mass-delete-panel>
  `,
})
class HostComponent {
  setId = 'set-1';
  channelName = 'somechannel';
  emotes = EMOTES;
  leadingActionsPresent = false;
  projectVoteButton = false;
  voteLabel = VOTE_LABEL;
}

describe('MassDeletePanel row composition', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        HostComponent,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: EmoteAdminService,
          useValue: {} as unknown as EmoteAdminService,
        },
        {
          provide: SevenTvDeleteService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            lastRun: signal(null),
          } as unknown as SevenTvDeleteService,
        },
        {
          provide: SevenTvRestoreService,
          useValue: {
            isRunning: signal(false),
            queue: signal([]),
            syncReport: signal('idle'),
            rateLimitPauseSeconds: signal(0),
            resyncTrigger: signal('idle'),
          } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvRunArbiter,
          useValue: {
            activeRun: signal<SevenTvRunKind | null>(null),
          } as unknown as SevenTvRunArbiter,
        },
        {
          provide: SevenTvTokenService,
          useValue: { hasToken: signal(true) } as unknown as SevenTvTokenService,
        },
        { provide: Dialog, useValue: { open: vi.fn() } as unknown as Dialog },
      ],
    }).compileComponents();

    await TestBed.inject(TranslocoService).load('de');
  });

  function render(setup: Partial<HostComponent> = {}): {
    fixture: ComponentFixture<HostComponent>;
    buttonNames(): string[];
  } {
    const fixture = TestBed.createComponent(HostComponent);
    Object.assign(fixture.componentInstance, setup);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      buttonNames: () =>
        Array.from(host.querySelectorAll('button'))
          .map((button) => button.textContent?.trim() ?? '')
          .filter((text) => text.length > 0),
    };
  }

  it('renders only the destructive action and the neutral exit when nothing is projected (voting-detail default)', () => {
    const { buttonNames } = render({ leadingActionsPresent: false, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('keeps the destructive action and exit unchanged even if leadingActionsPresent is true but nothing was actually projected', () => {
    // leadingActionsPresent only controls the gap/grouping wrapper, not what appears inside the
    // slot — an empty projection still shows no leading button, just (invisibly) reserves the gap.
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: false });

    expect(buttonNames()).toEqual([DELETE_LABEL, CLEAR_LABEL]);
  });

  it('orders the projected constructive group before the destructive action, and the neutral exit after it', () => {
    const { buttonNames } = render({ leadingActionsPresent: true, projectVoteButton: true });

    expect(buttonNames()).toEqual([VOTE_LABEL, DELETE_LABEL, CLEAR_LABEL]);
  });

  it('omits the neutral exit once the selection is empty, keeping the destructive action last among the rest', () => {
    const { buttonNames } = render({
      leadingActionsPresent: true,
      projectVoteButton: true,
      emotes: [],
    });

    // With an empty selection, "Löschen (0)" itself is still rendered (disabled elsewhere), but
    // "Auswahl aufheben" — the clear-selection escape hatch — has nothing left to clear.
    expect(buttonNames()).toEqual([VOTE_LABEL, 'Löschen (0)']);
  });
});
