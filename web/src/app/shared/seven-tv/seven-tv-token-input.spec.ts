import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SevenTvTokenInput } from './seven-tv-token-input';

// Only the keys this component translates — not the full app translation file.
const DE_TRANSLATIONS = {
  sevenTvToken: {
    tokenSet: 'Token hinterlegt',
    remove: 'Entfernen',
    intro: 'So findest du dein Token.',
    step1: 'Schritt 1',
    step2: 'Schritt 2',
    step3Prefix: 'Suche nach',
    step3Suffix: 'im Netzwerk-Tab.',
    step4: 'Schritt 4',
    securityWarning: 'Teile dieses Token mit niemandem.',
    placeholder: '7TV-Token',
    save: 'Speichern',
    required: 'Bitte ein Token eingeben.',
  },
};

/** The fake stands in for the whole service — the component reads `hasToken` and calls
 *  `setToken`/`clearToken` on it. Typed as `SevenTvTokenService` itself (not a hand-rolled
 *  interface) so a wrong field or method name is a compile error, not a silently-passing spec. */
function createFakeTokenService(): SevenTvTokenService {
  const fake: SevenTvTokenService = {
    hasToken: signal(false),
    getToken: vi.fn(() => null),
    setToken: vi.fn(),
    clearToken: vi.fn(),
  };
  return fake;
}

describe('SevenTvTokenInput', () => {
  let tokenService: SevenTvTokenService;

  beforeEach(async () => {
    tokenService = createFakeTokenService();

    await TestBed.configureTestingModule({
      imports: [
        SevenTvTokenInput,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [{ provide: SevenTvTokenService, useValue: tokenService }],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): ComponentFixture<SevenTvTokenInput> {
    const fixture = TestBed.createComponent(SevenTvTokenInput);
    fixture.detectChanges();
    return fixture;
  }

  function input(fixture: ComponentFixture<SevenTvTokenInput>): HTMLInputElement {
    return fixture.nativeElement.querySelector('#seven-tv-token-input-field');
  }

  function submitButton(fixture: ComponentFixture<SevenTvTokenInput>): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button[type="submit"]');
  }

  function typeInto(fixture: ComponentFixture<SevenTvTokenInput>, value: string): void {
    const field = input(fixture);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(fixture: ComponentFixture<SevenTvTokenInput>): void {
    submitButton(fixture).click();
    fixture.detectChanges();
  }

  it('sets the required error and does not store anything when the value is empty', () => {
    const fixture = render();

    submit(fixture);

    expect(fixture.nativeElement.querySelector('#seven-tv-token-error')).not.toBeNull();
    expect(tokenService.setToken).not.toHaveBeenCalled();
  });

  it('treats a whitespace-only value the same as empty — trimmed before the emptiness check', () => {
    const fixture = render();

    typeInto(fixture, '   ');
    submit(fixture);

    expect(fixture.nativeElement.querySelector('#seven-tv-token-error')).not.toBeNull();
    expect(tokenService.setToken).not.toHaveBeenCalled();
  });

  it('stores the trimmed token on a successful submit', () => {
    const fixture = render();

    typeInto(fixture, '  my-secret-token  ');
    submit(fixture);

    expect(tokenService.setToken).toHaveBeenCalledExactlyOnceWith('my-secret-token');
  });

  it('clears the field back to empty after a successful submit, so the secret does not linger', () => {
    const fixture = render();

    typeInto(fixture, 'my-secret-token');
    submit(fixture);

    expect(input(fixture).value).toBe('');
  });

  it('clears the required error again as soon as the user types', () => {
    const fixture = render();
    submit(fixture);
    expect(fixture.nativeElement.querySelector('#seven-tv-token-error')).not.toBeNull();

    typeInto(fixture, 'a');

    expect(fixture.nativeElement.querySelector('#seven-tv-token-error')).toBeNull();
  });

  describe('accessibility wiring', () => {
    it('carries no aria-invalid or aria-describedby before the field has ever been submitted', () => {
      const fixture = render();

      const field = input(fixture);
      expect(field.getAttribute('aria-invalid')).toBeNull();
      expect(field.getAttribute('aria-describedby')).toBeNull();
    });

    it('sets aria-invalid and points aria-describedby at the error paragraph once shown', () => {
      const fixture = render();

      submit(fixture);

      const field = input(fixture);
      expect(field.getAttribute('aria-invalid')).toBe('true');
      const describedBy = field.getAttribute('aria-describedby');
      expect(describedBy).toBe('seven-tv-token-error');
      // The dangling-reference bug: the id the field points at must actually exist in the DOM.
      expect(fixture.nativeElement.querySelector(`#${describedBy}`)).not.toBeNull();
    });

    it('drops both aria attributes again once the error is cleared by typing', () => {
      const fixture = render();
      submit(fixture);

      typeInto(fixture, 'a');

      const field = input(fixture);
      expect(field.getAttribute('aria-invalid')).toBeNull();
      expect(field.getAttribute('aria-describedby')).toBeNull();
    });
  });
});
