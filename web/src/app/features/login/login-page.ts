import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';

@Component({
  selector: 'app-login-page',
  imports: [TranslocoPipe, LanguageSwitcher],
  template: `
    <div class="flex min-h-screen flex-col items-center justify-center gap-6 bg-slate-950 px-4">
      <app-language-switcher />
      <div class="w-full max-w-sm rounded-lg bg-slate-900 p-8 text-center shadow-xl">
        <h1 class="mb-2 text-2xl font-semibold text-white">{{ 'login.title' | transloco }}</h1>
        <p class="mb-6 text-sm text-slate-400">
          {{ 'login.subtitle' | transloco }}
        </p>
        <button
          type="button"
          class="w-full rounded-md bg-purple-600 px-4 py-2 font-medium text-white transition hover:bg-purple-500"
          (click)="login()"
        >
          {{ 'login.loginButton' | transloco }}
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
