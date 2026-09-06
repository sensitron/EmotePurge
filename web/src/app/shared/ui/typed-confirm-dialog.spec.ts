import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { TypedConfirmDialog, TypedConfirmDialogData } from './typed-confirm-dialog';

// Only the keys this dialog translates.
const DE_TRANSLATIONS = {
  common: {
    cancel: 'Abbrechen',
    typedConfirmHint: 'Zum Fortfahren exakt „{{text}}“ eingeben.',
  },
};

const REQUIRED_TEXT = 'HandOfBlood';

interface Harness {
  fixture: ComponentFixture<TypedConfirmDialog>;
  input: HTMLInputElement;
  confirmButton: HTMLButtonElement;
  cancelButton: HTMLButtonElement;
  hint(): HTMLElement | null;
  type(value: string): void;
  pressEnterOnInput(): void;
}

describe('TypedConfirmDialog', () => {
  let close: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    close = vi.fn();

    const data: TypedConfirmDialogData = {
      title: 'Channel löschen',
      message: 'Dies kann nicht rückgängig gemacht werden.',
      requiredText: REQUIRED_TEXT,
      inputLabel: 'Channel-Name eingeben',
      confirmLabel: 'Löschen',
    };

    await TestBed.configureTestingModule({
      imports: [
        TypedConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close } },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): Harness {
    const fixture = TestBed.createComponent(TypedConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    function findButton(label: string): HTMLButtonElement {
      const found = buttons().find((button) => button.textContent?.trim() === label);
      if (!found) {
        throw new Error(`no button labelled "${label}"`);
      }
      return found;
    }

    const input = host.querySelector<HTMLInputElement>('#typed-confirm-input');
    if (!input) {
      throw new Error('typed-confirm-input not found');
    }

    return {
      fixture,
      input,
      get confirmButton() {
        return findButton('Löschen');
      },
      get cancelButton() {
        return findButton('Abbrechen');
      },
      hint: () => host.querySelector<HTMLElement>('#typed-confirm-hint'),
      type: (value: string) => {
        input.value = value;
        input.dispatchEvent(new Event('input'));
        fixture.detectChanges();
      },
      pressEnterOnInput: () => {
        input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
        fixture.detectChanges();
      },
    };
  }

  it('unlocks the confirm button once the exact required text is typed', () => {
    const dialog = render();
    expect(dialog.confirmButton.disabled).toBe(true);

    dialog.type(REQUIRED_TEXT);

    expect(dialog.confirmButton.disabled).toBe(false);
  });

  it('unlocks with surrounding whitespace, because the match is trimmed', () => {
    const dialog = render();

    dialog.type(`  ${REQUIRED_TEXT}  `);

    expect(dialog.confirmButton.disabled).toBe(false);
  });

  it('does not unlock on a case mismatch, because the match is case-sensitive', () => {
    const dialog = render();

    dialog.type(REQUIRED_TEXT.toLowerCase());

    expect(dialog.confirmButton.disabled).toBe(true);
  });

  it('ignores Enter while the typed text does not match — the bypass the disabled attribute cannot stop', () => {
    const dialog = render();

    dialog.type('not it');
    dialog.pressEnterOnInput();

    expect(close).not.toHaveBeenCalled();
  });

  it('closes with true on Enter once the typed text matches', () => {
    const dialog = render();

    dialog.type(REQUIRED_TEXT);
    dialog.pressEnterOnInput();

    expect(close).toHaveBeenCalledExactlyOnceWith(true);
  });

  it('describes the locked input with a hint that actually exists, and drops both once it matches', () => {
    const dialog = render();

    const describedBy = dialog.input.getAttribute('aria-describedby');
    expect(describedBy).toBe('typed-confirm-hint');
    // A dangling reference (an id the DOM has no element for) is the bug this guards against.
    expect(dialog.fixture.nativeElement.querySelector(`#${describedBy}`)).not.toBeNull();

    dialog.type(REQUIRED_TEXT);

    expect(dialog.input.getAttribute('aria-describedby')).toBeNull();
    expect(dialog.hint()).toBeNull();
  });

  it('closes with false on cancel', () => {
    const dialog = render();

    dialog.cancelButton.click();

    expect(close).toHaveBeenCalledExactlyOnceWith(false);
  });
});
