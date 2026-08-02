import { NgOptimizedImage } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { logoSrc } from '../../shared/branding/logo';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';
import { Button } from '../../shared/ui/button';

@Component({
  selector: 'app-login-page',
  template: `
    <div
      class="relative isolate flex min-h-screen flex-col items-center justify-center gap-6 bg-page px-4 text-fg"
    >
      <!-- Same subtle top glow as the app shell — the login page renders outside the shell,
           so it brings the brand background along itself. -->
      <div class="app-page-glow" aria-hidden="true"></div>
      <div class="absolute top-4 right-4">
        <app-language-switcher />
      </div>
      <a routerLink="/welcome" class="flex items-center gap-2 text-xl font-semibold">
        <img [ngSrc]="logoSrc()" width="28" height="28" alt="" class="h-7 w-7" />
        Emote Purge
      </a>
      <div class="app-card w-full max-w-sm p-8 text-center shadow-overlay">
        <h1 class="mb-2 text-2xl font-semibold text-fg">{{ 'login.title' | transloco }}</h1>
        <p class="mb-6 text-sm text-fg-muted">
          {{ 'login.subtitle' | transloco }}
        </p>
        <button type="button" appButton="primary" buttonSize="lg" class="w-full" (click)="login()">
          {{ 'login.loginButton' | transloco }}
        </button>
      </div>
    </div>
  `,
  imports: [Button, NgOptimizedImage, RouterLink, TranslocoPipe, LanguageSwitcher],
})
export class LoginPage {
  private readonly authService = inject(AuthService);

  protected readonly logoSrc = logoSrc();

  protected login(): void {
    this.authService.login();
  }
}
