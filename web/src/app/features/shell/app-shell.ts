import { Component, computed, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService, WorkerHealthStatus } from '../../core/health/worker-health.service';

const STATUS_DOT_CLASS: Record<WorkerHealthStatus, string> = {
  connected: 'bg-emerald-500',
  stale: 'bg-amber-500',
  unknown: 'bg-slate-600',
};

const STATUS_LABEL: Record<WorkerHealthStatus, string> = {
  connected: 'Worker verbunden',
  stale: 'Worker getrennt',
  unknown: 'Worker-Status unbekannt',
};

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterOutlet],
  template: `
    <div class="min-h-screen bg-slate-950 text-slate-100">
      <header class="border-b border-slate-800 px-4 py-3">
        <div class="mx-auto flex max-w-5xl items-center justify-between">
          <a routerLink="/" class="text-lg font-semibold">Emote Purge</a>

          <div class="flex items-center gap-4">
            <span class="inline-flex items-center gap-2 text-xs text-slate-400" [attr.title]="statusLabel()">
              <span class="h-2.5 w-2.5 rounded-full" [class]="statusDotClass()"></span>
              {{ statusLabel() }}
            </span>

            @if (currentUser(); as user) {
              <span class="text-sm text-slate-400">{{ user.displayName }}</span>
              <button
                type="button"
                class="rounded-md border border-slate-700 px-3 py-1.5 text-sm text-slate-300 transition hover:bg-slate-800"
                (click)="logout()"
              >
                Logout
              </button>
            } @else {
              <a
                routerLink="/login"
                class="rounded-md bg-purple-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-purple-500"
              >
                Login
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
  protected readonly statusLabel = computed(() => STATUS_LABEL[this.healthService.status()]);

  constructor() {
    // Consumes a return URL stashed by AuthService.login() (e.g. from an anonymous vote-session
    // visitor) once the session is confirmed, sending them back where they clicked "login" from
    // instead of leaving them on the fixed post-login redirect the backend always uses.
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
