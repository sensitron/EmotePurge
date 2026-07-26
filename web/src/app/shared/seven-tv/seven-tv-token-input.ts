import { Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';

import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';

@Component({
  selector: 'app-seven-tv-token-input',
  imports: [ReactiveFormsModule],
  template: `
    @if (tokenService.hasToken()) {
      <div class="flex items-center justify-between rounded-md bg-slate-800 px-3 py-2 text-sm">
        <span class="text-emerald-400">7TV-Token gesetzt</span>
        <button type="button" class="text-slate-400 hover:underline" (click)="tokenService.clearToken()">
          Entfernen
        </button>
      </div>
    } @else {
      <div class="rounded-md bg-slate-800 px-3 py-3 text-sm">
        <p class="mb-2 text-slate-300">
          Zum Löschen brauchst du dein eigenes 7TV-Schreib-Token aus deiner eingeloggten
          7tv.app-Session. Es bleibt nur in diesem Browser-Tab (sessionStorage) und wird nie an
          unseren Server geschickt.
        </p>
        <form class="flex gap-2" (submit)="onSubmit($event)">
          <input
            type="password"
            [formControl]="tokenControl"
            placeholder="7TV-Token einfügen"
            class="flex-1 rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-sm placeholder:text-slate-600 focus:border-purple-500 focus:outline-none"
          />
          <button
            type="submit"
            class="rounded-md bg-purple-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-purple-500"
          >
            Speichern
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
