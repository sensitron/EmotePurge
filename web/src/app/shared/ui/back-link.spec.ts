import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { BackLink } from './back-link';

// Only the keys this primitive translates — not the full app translation file.
const DE_TRANSLATIONS = {
  nav: {
    backTo: 'Zurück zu {{target}}',
  },
};

@Component({
  imports: [BackLink],
  template: `
    <app-back-link link="/" label="Übersicht" />
    <app-back-link [link]="['/channels', 'sensitron', 'vote-sessions']" label="Votings" />
  `,
})
class Host {}

describe('BackLink', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        Host,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideRouter([])],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): HTMLAnchorElement[] {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return Array.from(fixture.nativeElement.querySelectorAll('a'));
  }

  it('renders a real link, not a button — the up-link must be openable in a new tab', () => {
    const [overview] = render();

    expect(overview.tagName).toBe('A');
    expect(overview.getAttribute('href')).toBe('/');
  });

  it('resolves an array link built from route params', () => {
    const [, voteSessions] = render();

    expect(voteSessions.getAttribute('href')).toBe('/channels/sensitron/vote-sessions');
  });

  it('shows the destination as visible text and widens it to "back to X" for screen readers', () => {
    const [overview] = render();

    // The arrow is decorative, so the visible label is the destination alone...
    expect(overview.textContent?.trim()).toBe('←Übersicht');
    expect(overview.querySelector('[aria-hidden="true"]')?.textContent).toBe('←');
    // ...while the accessible name states the function and still contains the visible text
    // (WCAG 2.5.3 "Label in Name").
    expect(overview.getAttribute('aria-label')).toBe('Zurück zu Übersicht');
  });

  it('translates the accessible name per destination', () => {
    const [, voteSessions] = render();

    expect(voteSessions.getAttribute('aria-label')).toBe('Zurück zu Votings');
  });
});
