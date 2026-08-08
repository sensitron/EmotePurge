import { DOCUMENT } from '@angular/common';
import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { Avatar } from './avatar';
import { DisplayPreferences } from './display-preferences';
import { Popover } from './popover';

/**
 * Everything personal in the app frame behind one trigger: who you are, where your own pages are,
 * how the app looks, which language it speaks, and the way out.
 *
 * The reason is the rule that what stands in the app header stands on every screen in every
 * session. Six permanent controls measured against that are five too many, and the argument holds
 * on a desktop just as it does on a phone — which is why this replaces both the desktop cluster and
 * the mobile disclosure with one thing rather than two.
 *
 * Disclosure semantics, deliberately not role="menu": the panel holds mixed children — router
 * links, two radiogroups, a button — and role="menu" requires menuitem children, which a radiogroup
 * inside it is not. This is a step back from what theme-menu.ts did (menuitemradio) and the same
 * decision the shell's own disclosure already took.
 *
 * It calls ensureLoaded() itself because it renders on the landing and login pages too, outside the
 * shell that is otherwise the only caller. The call is idempotent, so the shell's own stays.
 */
@Component({
  selector: 'app-account-menu',
  imports: [Avatar, DisplayPreferences, Popover, RouterLink, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <!-- 44 px in a 56 px header leaves 6 px of air top and bottom. The plate inside is 32 px and
           is painted before the picture arrives, so nothing in this box ever changes size. -->
      <button
        #trigger
        type="button"
        class="inline-flex h-11 w-11 items-center justify-center rounded-md text-fg-muted transition hover:text-fg disabled:cursor-default"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="triggerLabel()"
        [disabled]="!authResolved()"
        (click)="toggle()"
      >
        @if (!authResolved()) {
          <!-- Reserved, silent, letterless: the shape is final, only its content resolves. No
               spinner — it costs one roundtrip, and a spinner in the header would be louder than
               the thing it reports. -->
          <app-avatar displayName="" />
        } @else if (currentUser(); as user) {
          <app-avatar [displayName]="user.displayName" [imageUrl]="user.profileImageUrl" />
        } @else {
          <svg
            class="h-5 w-5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="3.25" />
            <path
              d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"
            />
          </svg>
        }
      </button>

      @if (isOpen()) {
        <app-popover align="end" width="w-64" [ariaLabel]="triggerLabel()" (closed)="close()">
          <div class="flex flex-col">
            @if (currentUser(); as user) {
              <!-- No hover on this row. It carries the same rhythm as the entries below it but is
                   not clickable, and a hover must never promise a click that is not there. -->
              <div class="flex items-center gap-3 border-b border-border px-3 py-3">
                <app-avatar
                  [displayName]="user.displayName"
                  [imageUrl]="user.profileImageUrl"
                  [size]="36"
                />
                <!-- font-medium, not semibold: semibold is reserved for headings, and a fifth
                     weight would be a fifth level in a four-level scale. -->
                <span class="truncate text-sm font-medium text-fg">{{ user.displayName }}</span>
              </div>

              <a
                routerLink="/my-votings"
                class="flex min-h-11 items-center px-3 text-sm text-fg-body transition hover:bg-surface-inset"
                (click)="close()"
              >
                {{ 'shell.myVotings' | transloco }}
              </a>

              @if (user.isGlobalAdmin) {
                <!-- Visibility only — /admin is behind adminGuard and every admin endpoint behind
                     GlobalAdminAuthorizationFilter. The flag rides along on the cached /me. -->
                <a
                  routerLink="/admin"
                  class="flex min-h-11 items-center px-3 text-sm text-fg-body transition hover:bg-surface-inset"
                  (click)="close()"
                >
                  {{ 'shell.admin' | transloco }}
                </a>
              }

              <div class="border-t border-border">
                <app-display-preferences />
              </div>

              <button
                type="button"
                class="flex min-h-11 items-center border-t border-border px-3 text-left text-sm text-fg-body transition hover:bg-surface-inset"
                (click)="logout()"
              >
                {{ 'shell.logout' | transloco }}
              </button>
            } @else {
              <app-display-preferences />
            }
          </div>
        </app-popover>
      }
    </div>
  `,
})
export class AccountMenu {
  private readonly authService = inject(AuthService);
  private readonly transloco = inject(TranslocoService);
  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly currentUser = this.authService.currentUser;
  protected readonly authResolved = this.authService.isResolved;
  protected readonly isOpen = signal(false);

  /**
   * Translated imperatively rather than through the pipe, because it carries an interpolated name
   * into an attribute. Reading activeLang is what makes it follow a language switch made in this
   * very panel — translate() is a plain call and would otherwise never re-run.
   */
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  protected readonly triggerLabel = computed(() => {
    this.activeLang();
    const user = this.currentUser();
    return user
      ? this.transloco.translate('account.trigger', { name: user.displayName })
      : this.transloco.translate('account.preferencesTrigger');
  });

  constructor() {
    // The landing and login pages render outside AppShell, which is otherwise the only caller.
    // Idempotent, so the shell's own call is untouched and no second request is made.
    this.authService.ensureLoaded().subscribe();
  }

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected logout(): void {
    this.close();
    this.authService.logout();
  }
}
