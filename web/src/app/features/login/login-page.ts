import { Component, inject } from '@angular/core';

import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'app-login-page',
  template: `
    <div class="flex min-h-screen items-center justify-center bg-slate-950 px-4">
      <div class="w-full max-w-sm rounded-lg bg-slate-900 p-8 text-center shadow-xl">
        <h1 class="mb-2 text-2xl font-semibold text-white">Emote Purge</h1>
        <p class="mb-6 text-sm text-slate-400">
          Melde dich mit deinem Twitch-Account an, um den Bot für deinen Channel zu verwalten.
        </p>
        <button
          type="button"
          class="w-full rounded-md bg-purple-600 px-4 py-2 font-medium text-white transition hover:bg-purple-500"
          (click)="login()"
        >
          Mit Twitch einloggen
        </button>
      </div>
    </div>
  `,
})
export class LoginPage {
  private readonly authService = inject(AuthService);

  protected login(): void {
    this.authService.login();
  }
}
