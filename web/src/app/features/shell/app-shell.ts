import { Component, computed, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService, WorkerHealthStatus } from '../../core/health/worker-health.service';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';

const STATUS_DOT_CLASS: Record<WorkerHealthStatus, string> = {
  connected: 'bg-emerald-500',
  stale: 'bg-amber-500',
  unknown: 'bg-slate-600',
};

const STATUS_LABEL_KEY: Record<WorkerHealthStatus, string> = {
  connected: 'shell.workerStatus.connected',
  stale: 'shell.workerStatus.stale',
  unknown: 'shell.workerStatus.unknown',
};

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterOutlet, TranslocoPipe, LanguageSwitcher],
  template: `
    <div class="min-h-screen bg-slate-950 text-slate-100">
      <header class="sticky top-0 z-10 border-b border-slate-800 bg-slate-950 px-4 py-3">
        <div class="mx-auto flex max-w-5xl items-center justify-between">
          <a routerLink="/" class="text-lg font-semibold">Emote Purge</a>

          <div class="flex items-center gap-4">
            <app-language-switcher />

            <span
              class="inline-flex items-center gap-2 text-xs text-slate-400"
              [attr.title]="statusLabelKey() | transloco"
            >
              <span class="h-2.5 w-2.5 rounded-full" [class]="statusDotClass()"></span>
              {{ statusLabelKey() | transloco }}
            </span>

            @if (currentUser(); as user) {
              <a routerLink="/my-votings" class="text-sm text-slate-400 hover:underline">{{
                'shell.myVotings' | transloco
              }}</a>
              <span class="text-sm text-slate-400">{{ user.displayName }}</span>
              <button
                type="button"
                class="rounded-md border border-slate-700 px-3 py-1.5 text-sm text-slate-300 transition hover:bg-slate-800"
                (click)="logout()"
              >
                {{ 'shell.logout' | transloco }}
              </button>
            } @else {
              <a
                routerLink="/login"
                class="rounded-md bg-purple-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-purple-500"
              >
                {{ 'shell.login' | transloco }}
              </a>
            }
          </div>
        </div>
      </header>

      <main class="mx-auto max-w-5xl px-4 py-8">
        <router-outlet />
      </main>
    </div>
  `,
})
export class AppShell {
  private readonly authService = inject(AuthService);
  private readonly healthService = inject(WorkerHealthService);
  private readonly router = inject(Router);

  protected readonly currentUser = this.authService.currentUser;
  protected readonly statusDotClass = computed(() => STATUS_DOT_CLASS[this.healthService.status()]);
  protected readonly statusLabelKey = computed(() => STATUS_LABEL_KEY[this.healthService.status()]);

  constructor() {
    // AppShell is mounted for every route (overview, usage-stats, vote-sessions), unlike
    // ensureLoaded()'s other callers (authGuard only guards the overview route). Without this,
    // a logged-in user landing directly on a usage-stats or vote-session deep link never gets
    // currentUser populated, and the header wrongly shows "Login" despite a valid session cookie.
    // ensureLoaded() is idempotent (cached via an internal isLoaded flag), so this never causes a
    // duplicate /api/auth/me call when authGuard also runs.
    this.authService.ensureLoaded().subscribe();

    // Consumes a return URL stashed by AuthService.login()/stashReturnUrl() (e.g. a route guard
    // redirecting a logged-out visitor) once the session is confirmed, sending them back to the
    // page they originally tried to reach instead of the fixed post-login redirect the backend
    // always uses.
    effect(() => {
      if (this.currentUser()) {
        const returnUrl = this.authService.consumeReturnUrl();
        if (returnUrl) {
          this.router.navigateByUrl(returnUrl);
        }
      }
    });
  }

  protected logout(): void {
    this.authService.logout();
  }
}
