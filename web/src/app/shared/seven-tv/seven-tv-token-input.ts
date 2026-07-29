import { Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';

@Component({
  selector: 'app-seven-tv-token-input',
  imports: [ReactiveFormsModule, TranslocoPipe],
  template: `
    @if (tokenService.hasToken()) {
      <div class="flex items-center justify-between rounded-md bg-slate-800 px-3 py-2 text-sm">
        <span class="text-emerald-400">{{ 'sevenTvToken.tokenSet' | transloco }}</span>
        <button type="button" class="text-slate-400 hover:underline" (click)="tokenService.clearToken()">
          {{ 'sevenTvToken.remove' | transloco }}
        </button>
      </div>
    } @else {
      <div class="rounded-md bg-slate-800 px-3 py-3 text-sm">
        <p class="mb-2 text-slate-300">
          {{ 'sevenTvToken.intro' | transloco }}
        </p>
        <ol class="mb-3 list-decimal space-y-1 pl-5 text-slate-400">
          <li>{{ 'sevenTvToken.step1' | transloco }}</li>
          <li>{{ 'sevenTvToken.step2' | transloco }}</li>
          <li>
            {{ 'sevenTvToken.step3Prefix' | transloco }}
            <code class="rounded bg-slate-900 px-1 py-0.5 text-slate-200">7tv-token</code>
            {{ 'sevenTvToken.step3Suffix' | transloco }}
          </li>
          <li>{{ 'sevenTvToken.step4' | transloco }}</li>
        </ol>
        <p class="mb-3 rounded-md border border-amber-800 bg-amber-950/40 px-3 py-2 text-xs text-amber-300">
          {{ 'sevenTvToken.securityWarning' | transloco }}
        </p>
        <form class="flex gap-2" (submit)="onSubmit($event)">
          <label class="sr-only" for="seven-tv-token-input-field">{{ 'sevenTvToken.placeholder' | transloco }}</label>
          <input
            id="seven-tv-token-input-field"
            type="password"
            [formControl]="tokenControl"
            [placeholder]="'sevenTvToken.placeholder' | transloco"
            class="flex-1 rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-sm placeholder:text-slate-400 focus:border-purple-500 focus:outline-none"
          />
          <button
            type="submit"
            class="rounded-md bg-purple-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-purple-500"
          >
            {{ 'sevenTvToken.save' | transloco }}
          </button>
        </form>
      </div>
    }
  `,
})
export class SevenTvTokenInput {
  protected readonly tokenService = inject(SevenTvTokenService);
  protected readonly tokenControl = new FormControl('', { nonNullable: true, validators: [Validators.required] });

  protected onSubmit(event: Event): void {
    event.preventDefault();
    if (this.tokenControl.invalid) {
      return;
    }
    this.tokenService.setToken(this.tokenControl.value.trim());
    this.tokenControl.reset('');
  }
}
