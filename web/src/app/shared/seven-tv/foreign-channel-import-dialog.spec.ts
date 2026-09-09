import { DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import {
  ForeignChannelImportDialog,
  ForeignChannelImportResult,
} from './foreign-channel-import-dialog';

const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen' },
  import: {
    foreignChannel: {
      title: 'Fremden Kanal importieren',
      channelLabel: 'Kanalname',
      placeholder: 'z. B. handofblood',
      invalidChannelName: 'Kein gültiger Twitch-Kanalname.',
      load: 'Set laden',
      reload: 'Neu laden',
      retry: 'Erneut versuchen',
      continue: 'Weiter',
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden ({{ loaded }} von {{ totalCount }}).',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        ariaLabel: 'Sortierung',
        none: 'Set-Reihenfolge',
        topAllTime: 'Top',
        trending: 'Trend',
      },
    },
  },
  errors: {
    status: { notFound: 'Nicht gefunden.' },
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

function response(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
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
    ...overrides,
  };
}

describe('ForeignChannelImportDialog', () => {
  let fixture: ComponentFixture<ForeignChannelImportDialog>;
  let component: ForeignChannelImportDialog;
  let host: HTMLElement;
  let httpMock: HttpTestingController;
  let closed: (ForeignChannelImportResult | undefined)[];

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    closed = [];

    await TestBed.configureTestingModule({
      imports: [
        ForeignChannelImportDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: DialogRef,
          useValue: { close: (result?: ForeignChannelImportResult) => closed.push(result) },
        },
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ForeignChannelImportDialog);
    component = fixture.componentInstance;
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

  it('rejects an obviously invalid channel name locally, without making a request', () => {
    component['channelNameControl'].setValue('ab');
    component['submit']();
    fixture.detectChanges();

    httpMock.expectNone(() => true);
    expect(host.textContent).toContain('Kein gültiger Twitch-Kanalname.');
  });

  it('loads the channel and renders its set once the request resolves', () => {
    component['channelNameControl'].setValue('HandOfBlood');
    component['submit']();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
    expect(req.request.method).toBe('GET');
    req.flush(response());
    fixture.detectChanges();

    expect(host.textContent).toContain('#handofblood');
    expect(host.querySelector('app-foreign-emote-grid')).not.toBeNull();
  });

  it('surfaces a load failure and offers a retry that re-issues the request', () => {
    component['channelNameControl'].setValue('unknownchannel');
    component['submit']();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/seventv/channels/unknownchannel/emotes')
      .flush({ errorCode: 'not_a_known_code' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(host.textContent).toContain('Nicht gefunden.');

    button('Erneut versuchen').click();
    httpMock.expectOne('/api/seventv/channels/unknownchannel/emotes').flush(response());
  });

  it('reload bypasses the cache via refresh=true', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    fixture.detectChanges();

    button('Neu laden').click();

    const refreshReq = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/channels/handofblood/emotes' &&
        candidate.params.get('refresh') === 'true',
    );
    refreshReq.flush(response());
  });

  it('keeps "Weiter" disabled until a selection exists, then closes with the picked rows', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    fixture.detectChanges();

    expect(button('Weiter').disabled).toBe(true);

    const picked = response().emotes;
    component['onSelectionChange'](picked);
    fixture.detectChanges();
    expect(button('Weiter').disabled).toBe(false);

    button('Weiter').click();

    expect(closed).toEqual([
      { channelName: 'handofblood', sevenTvUserId: 'user-1', emoteSetId: 'set-1', rows: picked },
    ]);
  });

  it('cancel closes the dialog with no result', () => {
    button('Abbrechen').click();
    expect(closed).toEqual([undefined]);
  });
});
