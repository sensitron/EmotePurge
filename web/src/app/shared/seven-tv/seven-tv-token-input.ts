import { Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';

@Component({
  selector: 'app-seven-tv-token-input',
  imports: [Button, ReactiveFormsModule, TranslocoPipe],
  template: `
    @if (tokenService.hasToken()) {
      <div class="flex items-center justify-between rounded-md bg-surface-inset px-3 py-2 text-sm">
        <span class="text-success-fg">{{ 'sevenTvToken.tokenSet' | transloco }}</span>
        <button
          type="button"
          class="text-fg-muted hover:underline"
          (click)="tokenService.clearToken()"
        >
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
        <p
          class="mb-3 rounded-md border border-warning-border bg-warning-wash px-3 py-2 text-xs text-warning-fg"
        >
          {{ 'sevenTvToken.securityWarning' | transloco }}
        </p>
        <form class="flex gap-2" (submit)="onSubmit($event)">
          <label class="sr-only" for="seven-tv-token-input-field">{{
            'sevenTvToken.placeholder' | transloco
          }}</label>
          <input
            id="seven-tv-token-input-field"
            type="password"
            [formControl]="tokenControl"
            [placeholder]="'sevenTvToken.placeholder' | transloco"
            class="app-input flex-1"
          />
          <button type="submit" appButton="primary" buttonSize="lg">
            {{ 'sevenTvToken.save' | transloco }}
          </button>
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

  protected onSubmit(event: Event): void {
    event.preventDefault();
    if (this.tokenControl.invalid) {
      return;
    }
    this.tokenService.setToken(this.tokenControl.value.trim());
    this.tokenControl.reset('');
  }
}
