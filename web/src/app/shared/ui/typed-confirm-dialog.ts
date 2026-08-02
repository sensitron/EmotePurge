import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from './button';

/** Caller passes already-translated strings, exactly like ConfirmDialog — the call sites
 *  interpolate params (channel name, counts) via TranslocoService anyway. */
export interface TypedConfirmDialogData {
  /** Rendered as the dialog heading under the id `typed-confirm-dialog-title`. A caller that passes
   *  one should also pass `ariaLabelledBy: 'typed-confirm-dialog-title'` to `Dialog.open`, the way
   *  DeleteConfirmDialog's call site does — that is what names the dialog for assistive tech. */
  title?: string;
  message: string;
  /** The exact, case-sensitive text the user must retype before confirming (e.g. the channel name). */
  requiredText: string;
  inputLabel: string;
  confirmLabel: string;
}

/**
 * ConfirmDialog's harder sibling for actions that are irreversible *and* target-specific: the
 * confirm button unlocks only once the user has retyped `requiredText` verbatim. A plain confirm
 * proves the user clicked; retyping the name proves they know *which* row they are destroying —
 * the failure mode a one-row mis-click produces, which no yes/no dialog can catch.
 *
 * Same CDK plumbing as ConfirmDialog (focus trap, Escape, backdrop click, aria-modal come from the
 * container), closes with `true` only on explicit confirm.
 */
@Component({
  selector: 'app-typed-confirm-dialog',
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-surface p-6 shadow-overlay">
      @if (data.title; as title) {
        <h2 id="typed-confirm-dialog-title" class="mb-3 text-lg font-semibold text-fg">
          {{ title }}
        </h2>
      }
      <p class="mb-4 text-sm whitespace-pre-line text-fg-body">{{ data.message }}</p>

      <label class="mb-1 block text-sm text-fg-secondary" for="typed-confirm-input">
        {{ data.inputLabel }}
      </label>
      <input
        id="typed-confirm-input"
        type="text"
        class="app-input mb-5 w-full"
        autocomplete="off"
        autocapitalize="off"
        autocorrect="off"
        spellcheck="false"
        [attr.aria-describedby]="matches() ? null : 'typed-confirm-hint'"
        [value]="typedText()"
        (input)="onInput($event)"
        (keydown.enter)="confirmIfMatching()"
      />

      <div class="flex flex-wrap items-center justify-end gap-2">
        <!-- The reason the confirm button is disabled, in text — a greyed-out button alone is not a
             perceivable explanation (WCAG), and it is the whole point of this dialog. -->
        @if (!matches()) {
          <p id="typed-confirm-hint" class="mr-auto text-xs text-fg-muted">
            {{ 'common.typedConfirmHint' | transloco: { text: data.requiredText } }}
          </p>
        }
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close(false)">
          {{ 'common.cancel' | transloco }}
        </button>
        <button
          type="button"
          appButton="danger-solid"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="!matches()"
          (click)="confirmIfMatching()"
        >
          {{ data.confirmLabel }}
        </button>
      </div>
    </div>
  `,
})
export class TypedConfirmDialog {
  protected readonly data = inject<TypedConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly typedText = signal('');

  // Trimmed (people paste with a trailing space) but case-sensitive: a channel name typed in the
  // wrong case is still a sign the user copied it from somewhere other than the row they clicked.
  protected readonly matches = computed(() => this.typedText().trim() === this.data.requiredText);

  protected onInput(event: Event): void {
    this.typedText.set((event.target as HTMLInputElement).value);
  }

  protected confirmIfMatching(): void {
    // Guarded on its own instead of relying on the button's disabled state — Enter in the input
    // reaches this too, and that path has no disabled attribute to stop it.
    if (this.matches()) {
      this.dialogRef.close(true);
    }
  }
}
