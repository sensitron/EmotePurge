import { NgOptimizedImage } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { filter, map } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { WorkerHealthService, WorkerHealthStatus } from '../../core/health/worker-health.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';
import { Button } from '../../shared/ui/button';
import { ThemeMenu } from '../../shared/ui/theme-menu';

const STATUS_DOT_CLASS: Record<WorkerHealthStatus, string> = {
  connected: 'bg-success-dot',
  stale: 'bg-warning-dot',
  unknown: 'bg-fg-disabled',
};

const STATUS_LABEL_KEY: Record<WorkerHealthStatus, string> = {
  connected: 'shell.workerStatus.connected',
  stale: 'shell.workerStatus.stale',
  unknown: 'shell.workerStatus.unknown',
};

/**
 * The content column, in two widths.
 *
 * `MEASURE` is what the whole app used to run at, and it is right for almost everything here: the
 * lists, the admin tables, the monitoring grids and every paragraph of prose want a line the eye can
 * return from, not the full monitor. The two sprite sheets are the exception — there, width is not
 * decoration but rows: another 500px is another column of emotes and that much less scrolling on a
 * 900-emote set. So the shell does not widen; individual routes ask to, via `data.wideLayout`.
 *
 * Header and main share the class on purpose. The header's rule already spans the viewport (the
 * border sits on the outer element), so what moves with the route is only the logo's alignment with
 * the content underneath it — which is the point of matching them.
 */
const MEASURE = 'max-w-5xl';
const WIDE = 'max-w-[96rem]';

@Component({
  selector: 'app-shell',
  imports: [
    Button,
    NgOptimizedImage,
    RouterLink,
    RouterOutlet,
    TranslocoPipe,
    LanguageSwitcher,
    ThemeMenu,
  ],
  host: {
    '(keydown.escape)': 'onEscape()',
    '(document:click)': 'onDocumentClick($event)',
  },
  template: `
    <div class="isolate min-h-screen bg-page text-fg">
      <!-- h-14 is a contract, not styling: the sticky tab bars pin at top-14 and the sticky
           filter toolbars at top-24, both assuming exactly this header height (design doc §8.5).
           z-30 keeps the header (and its mobile disclosure) above the z-20 sticky bars — it is a
           utility here so it beats .app-sticky-bar's own z-20 (utilities layer after components).
           Reusing that class rather than repeating the blur: the translucency has to be denser in
           light than in dark, and --ep-sticky-alpha is where that lives. -->
      <header class="app-sticky-bar top-0 z-30 h-14 border-b border-border px-4">
        <div [class]="headerRowClass()">
          <!-- Logo and worker-health dot form one anchored group — a lone justify-between middle
               child would float detached between logo and menu button on narrow viewports. -->
          <div class="flex min-w-0 items-center gap-3">
            <a
              routerLink="/"
              class="flex items-center gap-2 text-lg font-semibold whitespace-nowrap"
            >
              <img
                [ngSrc]="logoSrc"
                width="24"
                height="24"
                disableOptimizedSrcset
                alt=""
                class="h-6 w-6"
              />
              Emote Purge
            </a>
            <!-- Dot always visible, text label only when there's room. -->
            <span
              class="inline-flex min-w-0 items-center gap-2 text-xs text-fg-muted"
              [attr.title]="statusLabelKey() | transloco"
            >
              <span class="h-2.5 w-2.5 shrink-0 rounded-full" [class]="statusDotClass()"></span>
              <span class="hidden truncate md:inline">{{ statusLabelKey() | transloco }}</span>
              <span class="sr-only md:hidden">{{ statusLabelKey() | transloco }}</span>
            </span>
          </div>

          <!-- Desktop: everything inline, as before. -->
          <div class="hidden items-center gap-4 md:flex">
            <!-- Theme and language sit together: both are personal display preferences rather than
                 domain actions, so they belong in the same corner of the header. -->
            <app-theme-menu />
            <app-language-switcher />

            @if (currentUser(); as user) {
              <!-- Visibility only — /admin is behind adminGuard and every admin endpoint behind
                   GlobalAdminAuthorizationFilter. The flag rides along on the cached /me. -->
              @if (user.isGlobalAdmin) {
                <a routerLink="/admin" class="px-1 py-2 text-sm text-fg-muted hover:underline">{{
                  'shell.admin' | transloco
                }}</a>
              }
              <a routerLink="/my-votings" class="px-1 py-2 text-sm text-fg-muted hover:underline">{{
                'shell.myVotings' | transloco
              }}</a>
              <span class="text-sm text-fg-muted">{{ user.displayName }}</span>
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
            class="inline-flex h-11 w-11 items-center justify-center rounded-md border border-border-strong text-fg-secondary transition hover:bg-surface-inset md:hidden"
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
              class="absolute inset-x-0 top-full z-20 mt-3 flex flex-col gap-1 rounded-md border border-border bg-surface p-2 shadow-overlay md:hidden"
            >
              @if (currentUser(); as user) {
                @if (user.isGlobalAdmin) {
                  <a
                    routerLink="/admin"
                    class="rounded-md px-3 py-3 text-sm text-fg-body transition hover:bg-surface-inset"
                    (click)="closeMenu()"
                  >
                    {{ 'shell.admin' | transloco }}
                  </a>
                }
                <a
                  routerLink="/my-votings"
                  class="rounded-md px-3 py-3 text-sm text-fg-body transition hover:bg-surface-inset"
                  (click)="closeMenu()"
                >
                  {{ 'shell.myVotings' | transloco }}
                </a>
                <!-- The h-14 header has no room for another control on narrow viewports (§8.5
                     height contract), so the theme menu joins the language switcher down here. -->
                <div class="flex items-center justify-between gap-3 rounded-md px-3 py-3">
                  <span class="text-sm text-fg-muted">{{ user.displayName }}</span>
                  <div class="flex items-center gap-2">
                    <app-theme-menu />
                    <app-language-switcher />
                  </div>
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
                <div class="flex items-center justify-end gap-2 rounded-md px-3 py-2">
                  <app-theme-menu />
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

      <main [class]="mainClass()">
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
  protected readonly logoSrc = LOGO_SRC;

  private readonly contentWidth = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.activeWidthClass()),
    ),
    { initialValue: this.activeWidthClass() },
  );

  protected readonly headerRowClass = computed(
    () => `relative mx-auto flex h-full items-center justify-between gap-3 ${this.contentWidth()}`,
  );
  protected readonly mainClass = computed(() => `mx-auto px-4 py-8 ${this.contentWidth()}`);

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

  /**
   * Looks for `data.wideLayout` down the whole activated chain. Walked rather than read off one
   * level: the two sheet pages sit three deep (shell → channel workspace → page), and route data
   * does not reach downwards on its own.
   *
   * `snapshot` is guarded because this also runs from the field initializer, and AppShell is
   * constructed *during* the navigation that activates it — the child routes exist by then, their
   * snapshots do not. Reading `.data` off the missing snapshot threw in the constructor, which took
   * the whole shell down with it and rendered an empty page.
   */
  private activeWidthClass(): string {
    for (
      let route: ActivatedRoute | null = this.router.routerState.root;
      route;
      route = route.firstChild
    ) {
      if (route.snapshot?.data?.['wideLayout'] === true) {
        return WIDE;
      }
    }
    return MEASURE;
  }
}
