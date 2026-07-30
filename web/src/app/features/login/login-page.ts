import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';
import { Button } from '../../shared/ui/button';

@Component({
  selector: 'app-login-page',
  template: `
    <div
      class="relative isolate flex min-h-screen flex-col items-center justify-center gap-6 bg-slate-950 px-4 text-slate-100"
    >
      <!-- Same subtle top glow as the app shell — the login page renders outside the shell,
           so it carries its own copy of the brand background. -->
      <div
        class="pointer-events-none absolute inset-x-0 top-0 -z-10 h-96 bg-[radial-gradient(circle_at_25%_0%,rgba(147,51,234,0.14),transparent_55%),radial-gradient(circle_at_80%_0%,rgba(236,72,153,0.1),transparent_50%)] mask-[linear-gradient(to_bottom,black_30%,transparent)]"
        aria-hidden="true"
      ></div>
      <div class="absolute top-4 right-4">
        <app-language-switcher />
      </div>
      <a routerLink="/welcome" class="flex items-center gap-2 text-xl font-semibold">
        <span
          class="inline-block h-3 w-3 rounded bg-linear-to-br from-purple-500 to-pink-500"
          aria-hidden="true"
        ></span>
        Emote Purge
      </a>
      <div class="app-card w-full max-w-sm p-8 text-center shadow-xl">
        <h1 class="mb-2 text-2xl font-semibold text-white">{{ 'login.title' | transloco }}</h1>
        <p class="mb-6 text-sm text-slate-400">
          {{ 'login.subtitle' | transloco }}
        </p>
        <button type="button" appButton="primary" buttonSize="lg" class="w-full" (click)="login()">
          {{ 'login.loginButton' | transloco }}
        </button>
      </div>
    </div>
  `,
  imports: [Button, RouterLink, TranslocoPipe, LanguageSwitcher],
})
export class LoginPage {
  private readonly authService = inject(AuthService);

  protected login(): void {
    this.authService.login();
  }
}
