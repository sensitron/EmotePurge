import { Component, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';

@Component({
  selector: 'app-seven-tv-token-input',
  imports: [Button, NoticeBanner, ReactiveFormsModule, TranslocoPipe],
  template: `
    @if (tokenService.hasToken()) {
      <div class="flex items-center justify-between rounded-md bg-surface-inset px-3 py-2 text-sm">
        <span class="text-success-fg">{{ 'sevenTvToken.tokenSet' | transloco }}</span>
        <button type="button" appButton="neutral" (click)="tokenService.clearToken()">
          {{ 'sevenTvToken.remove' | transloco }}
        </button>
      </div>
    } @else {
      <div class="rounded-md bg-surface-inset px-3 py-3 text-sm">
        <p class="mb-2 text-fg-secondary">
          {{ 'sevenTvToken.intro' | transloco }}
        </p>
        <ol class="mb-3 list-decimal space-y-1 pl-5 text-fg-muted">
          <li>{{ 'sevenTvToken.step1' | transloco }}</li>
          <li>{{ 'sevenTvToken.step2' | transloco }}</li>
          <li>
            {{ 'sevenTvToken.step3Prefix' | transloco }}
            <!-- One step further from the surface than the panel around it, so the chip separates
                 from its container in whichever direction "further" runs in the current mode. -->
            <code class="rounded bg-surface-inset-hover px-1 py-0.5 text-fg-body">7tv-token</code>
            {{ 'sevenTvToken.step3Suffix' | transloco }}
          </li>
          <li>{{ 'sevenTvToken.step4' | transloco }}</li>
        </ol>
        <app-notice-banner class="mb-3 block" variant="warning">
          {{ 'sevenTvToken.securityWarning' | transloco }}
        </app-notice-banner>
        <form class="flex flex-col gap-1" (submit)="onSubmit($event)">
          <div class="flex gap-2">
            <label class="sr-only" for="seven-tv-token-input-field">{{
              'sevenTvToken.placeholder' | transloco
            }}</label>
            <input
              id="seven-tv-token-input-field"
              type="password"
              [formControl]="tokenControl"
              [placeholder]="'sevenTvToken.placeholder' | transloco"
              [attr.aria-invalid]="showRequiredError() ? 'true' : null"
              [attr.aria-describedby]="showRequiredError() ? 'seven-tv-token-error' : null"
              class="app-input flex-1"
              (input)="showRequiredError.set(false)"
            />
            <button type="submit" appButton="primary" buttonSize="lg">
              {{ 'sevenTvToken.save' | transloco }}
            </button>
          </div>
          <!-- §5.3: a required field that silently does nothing on submit was this file's
               long-standing exception to the field-error pattern. -->
          @if (showRequiredError()) {
            <p id="seven-tv-token-error" class="text-sm text-danger-fg">
              {{ 'sevenTvToken.required' | transloco }}
            </p>
          }
        </form>
      </div>
    }
  `,
})
export class SevenTvTokenInput {
  protected readonly tokenService = inject(SevenTvTokenService);
  protected readonly tokenControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required],
  });
  /** Set by a submit that had nothing to save, cleared as soon as the user types — the control's
   *  own `touched`/`dirty` state would flag the field before the user ever pressed the button. */
  protected readonly showRequiredError = signal(false);

  protected onSubmit(event: Event): void {
    event.preventDefault();
    if (this.tokenControl.value.trim().length === 0) {
      this.showRequiredError.set(true);
      return;
    }
    this.tokenService.setToken(this.tokenControl.value.trim());
    this.tokenControl.reset('');
  }
}
