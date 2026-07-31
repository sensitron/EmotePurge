import { NgOptimizedImage } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService, WorkerHealthStatus } from '../../core/health/worker-health.service';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';
import { Button } from '../../shared/ui/button';

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
  imports: [Button, NgOptimizedImage, RouterLink, RouterOutlet, TranslocoPipe, LanguageSwitcher],
  host: {
    '(keydown.escape)': 'onEscape()',
    '(document:click)': 'onDocumentClick($event)',
  },
  template: `
    <div class="isolate min-h-screen bg-slate-950 text-slate-100">
      <!-- Subtle top glow, same radial-gradient technique as the landing hero but far dimmer —
           decorative only, sits behind everything via -z-10 (root is isolate). The mask fades the
           whole layer to zero towards its bottom edge; without it the radial circles are still
           visibly colored where the container ends, which reads as a hard horizontal cut. -->
      <div
        class="pointer-events-none absolute inset-x-0 top-0 -z-10 h-96 bg-[radial-gradient(circle_at_25%_0%,rgba(147,51,234,0.14),transparent_55%),radial-gradient(circle_at_80%_0%,rgba(236,72,153,0.1),transparent_50%)] mask-[linear-gradient(to_bottom,black_30%,transparent)]"
        aria-hidden="true"
      ></div>
      <header
        class="sticky top-0 z-10 border-b border-slate-800 bg-slate-950/80 px-4 py-3 backdrop-blur"
      >
        <div class="relative mx-auto flex max-w-5xl items-center justify-between gap-3">
          <!-- Logo and worker-health dot form one anchored group — a lone justify-between middle
               child would float detached between logo and menu button on narrow viewports. -->
          <div class="flex min-w-0 items-center gap-3">
            <a
              routerLink="/"
              class="flex items-center gap-2 text-lg font-semibold whitespace-nowrap"
            >
              <img ngSrc="logo.png" width="24" height="24" alt="" class="h-6 w-6" />
              Emote Purge
            </a>
            <!-- Dot always visible, text label only when there's room. -->
            <span
              class="inline-flex min-w-0 items-center gap-2 text-xs text-slate-400"
              [attr.title]="statusLabelKey() | transloco"
            >
              <span class="h-2.5 w-2.5 shrink-0 rounded-full" [class]="statusDotClass()"></span>
              <span class="hidden truncate md:inline">{{ statusLabelKey() | transloco }}</span>
              <span class="sr-only md:hidden">{{ statusLabelKey() | transloco }}</span>
            </span>
          </div>

          <!-- Desktop: everything inline, as before. -->
          <div class="hidden items-center gap-4 md:flex">
            <app-language-switcher />

            @if (currentUser(); as user) {
              <a
                routerLink="/my-votings"
                class="px-1 py-2 text-sm text-slate-400 hover:underline"
                >{{ 'shell.myVotings' | transloco }}</a
              >
              <span class="text-sm text-slate-400">{{ user.displayName }}</span>
              <button type="button" appButton="outline" (click)="logout()">
                {{ 'shell.logout' | transloco }}
              </button>
            } @else {
              <a routerLink="/login" appButton="primary">
                {{ 'shell.login' | transloco }}
              </a>
            }
          </div>

          <!-- Mobile: disclosure menu button (W3C disclosure pattern, not role="menu"). -->
          <button
            #menuButton
            type="button"
            data-shell-menu
            class="inline-flex h-11 w-11 items-center justify-center rounded-md border border-slate-700 text-slate-300 transition hover:bg-slate-800 md:hidden"
            [attr.aria-expanded]="menuOpen()"
            aria-controls="app-shell-menu"
            [attr.aria-label]="'shell.menu' | transloco"
            (click)="toggleMenu()"
          >
            @if (menuOpen()) {
              <svg
                class="h-5 w-5"
                viewBox="0 0 20 20"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                aria-hidden="true"
              >
                <path d="M5 5l10 10M15 5L5 15" stroke-linecap="round" />
              </svg>
            } @else {
              <svg
                class="h-5 w-5"
                viewBox="0 0 20 20"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                aria-hidden="true"
              >
                <path d="M3 6h14M3 10h14M3 14h14" stroke-linecap="round" />
              </svg>
            }
          </button>

          @if (menuOpen()) {
            <nav
              id="app-shell-menu"
              data-shell-menu
              class="absolute inset-x-0 top-full z-20 mt-3 flex flex-col gap-1 rounded-md border border-slate-800 bg-slate-900 p-2 shadow-lg md:hidden"
            >
              @if (currentUser(); as user) {
                <a
                  routerLink="/my-votings"
                  class="rounded-md px-3 py-3 text-sm text-slate-200 transition hover:bg-slate-800"
                  (click)="closeMenu()"
                >
                  {{ 'shell.myVotings' | transloco }}
                </a>
                <div class="flex items-center justify-between gap-3 rounded-md px-3 py-3">
                  <span class="text-sm text-slate-400">{{ user.displayName }}</span>
                  <app-language-switcher />
                </div>
                <button
                  type="button"
                  appButton="outline"
                  class="py-3 text-left"
                  (click)="closeMenu(); logout()"
                >
                  {{ 'shell.logout' | transloco }}
                </button>
              } @else {
                <div class="flex items-center justify-end rounded-md px-3 py-2">
                  <app-language-switcher />
                </div>
                <a
                  routerLink="/login"
                  appButton="primary"
                  class="py-3 text-center"
                  (click)="closeMenu()"
                >
                  {{ 'shell.login' | transloco }}
                </a>
              }
            </nav>
          }
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
  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');

  protected readonly currentUser = this.authService.currentUser;
  protected readonly statusDotClass = computed(() => STATUS_DOT_CLASS[this.healthService.status()]);
  protected readonly statusLabelKey = computed(() => STATUS_LABEL_KEY[this.healthService.status()]);
  protected readonly menuOpen = signal(false);

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

  protected toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  protected closeMenu(): void {
    this.menuOpen.set(false);
  }

  protected onEscape(): void {
    if (this.menuOpen()) {
      this.closeMenu();
      this.menuButton().nativeElement.focus();
    }
  }

  protected onDocumentClick(event: Event): void {
    // The disclosure closes on any click outside itself; clicks on the toggle button or inside
    // the panel are handled by their own handlers (both carry data-shell-menu).
    if (this.menuOpen() && !(event.target as HTMLElement).closest('[data-shell-menu]')) {
      this.closeMenu();
    }
  }
}
