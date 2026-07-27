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
          7tv.app-Session:
        </p>
        <ol class="mb-3 list-decimal space-y-1 pl-5 text-slate-400">
          <li>Auf <span class="text-slate-200">7tv.app</span> einloggen (falls noch nicht geschehen).</li>
          <li>
            Entwicklertools öffnen (F12) → Tab "Application" (Chrome) bzw. "Speicher" (Firefox) →
            "Local Storage" → "https://7tv.app".
          </li>
          <li>
            Den Eintrag mit dem Schlüssel
            <code class="rounded bg-slate-900 px-1 py-0.5 text-slate-200">7tv-token</code>
            suchen und dessen Wert kopieren.
          </li>
          <li>Den kopierten Wert unten einfügen.</li>
        </ol>
        <p class="mb-3 rounded-md border border-amber-800 bg-amber-950/40 px-3 py-2 text-xs text-amber-300">
          ⚠ Dieses Token gewährt vollen Schreibzugriff auf deinen 7TV-Account (u. a. Emotes löschen).
          Behandle es wie ein Passwort — gib es niemandem weiter. Es bleibt nur in diesem
          Browser-Tab (sessionStorage), wird nie an unseren Server geschickt und ist nach dem
          Schließen des Tabs wieder weg.
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
