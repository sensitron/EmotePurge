import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AdminUser } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { AuthService } from '../../core/auth/auth.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';

const PAGE_SIZE = 25;

const EMPTY_PAGE: PagedResult<AdminUser> = {
  items: [],
  page: 1,
  pageSize: PAGE_SIZE,
  totalCount: 0,
  totalPages: 0,
};

/**
 * Every user who ever logged in, with derived token status and one action: revoking their sessions
 * (a forced logout — the server also drops their stored Twitch tokens). The plain ConfirmDialog is
 * enough here, unlike the purge's typed confirmation: revoking is recoverable (the user just logs
 * in again), so the typed-name barrier would be ceremony without a matching risk.
 */
@Component({
  selector: 'app-admin-users-page',
  imports: [Button, EmptyState, NoticeBanner, Pager, SkeletonRows, StatusBadge, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.users.title' | transloco }}</h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.users.refreshTitle' | transloco"
        >
          {{ 'admin.users.refresh' | transloco }}
        </button>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      } @else if (rows().length === 0) {
        <app-empty-state icon="👤" [title]="'admin.users.empty' | transloco" />
      } @else {
        <ul class="flex flex-col gap-2">
          @for (row of rows(); track row.twitchUserId) {
            <li class="app-card flex flex-col gap-2 px-4 py-3">
              <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
                <span class="max-w-full truncate font-medium text-slate-100">
                  {{ row.displayName }}
                  @if (row.showLogin) {
                    <span class="font-normal text-slate-400">({{ row.twitchUsername }})</span>
                  }
                </span>
                <app-status-badge [tone]="row.hasRefreshToken ? 'emerald' : 'slate'">
                  {{
                    (row.hasRefreshToken ? 'admin.users.token.connected' : 'admin.users.token.none')
                      | transloco
                  }}
                </app-status-badge>

                <div class="ml-auto flex items-center gap-2">
                  <button
                    type="button"
                    appButton="danger"
                    [disabled]="pendingUserId() === row.twitchUserId"
                    (click)="confirmRevoke(row)"
                  >
                    {{ 'admin.users.revoke.button' | transloco }}
                  </button>
                </div>
              </div>

              <p class="flex flex-wrap gap-x-2 gap-y-1 text-xs text-slate-400">
                <span>{{ 'admin.users.id' | transloco: { id: row.twitchUserId } }}</span>
                <span aria-hidden="true">·</span>
                <span>{{ 'admin.users.lastLogin' | transloco: { date: row.lastLoginFormatted } }}</span>
                @if (row.tokenExpiresFormatted; as expires) {
                  <span aria-hidden="true">·</span>
                  <span>{{ 'admin.users.tokenExpires' | transloco: { date: expires } }}</span>
                }
                @if (row.sessionsRevokedFormatted; as revoked) {
                  <span aria-hidden="true">·</span>
                  <span>{{ 'admin.users.sessionsRevokedAt' | transloco: { date: revoked } }}</span>
                }
                @if (row.twitchTokenScopes; as scopes) {
                  <span aria-hidden="true">·</span>
                  <span class="break-all">{{ 'admin.users.scopes' | transloco: { scopes } }}</span>
                }
              </p>
            </li>
          }
        </ul>

        <app-pager [page]="page()" [totalPages]="totalPages()" (pageChange)="onPageChange($event)" />
      }
    </div>
  `,
})
export class AdminUsersPage {
  private readonly adminService = inject(AdminService);
  private readonly authService = inject(AuthService);
  private readonly dialog = inject(Dialog);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  protected readonly page = signal(1);

  private readonly usersResource = rxResource({
    params: () => ({ page: this.page() }),
    stream: ({ params }) => this.adminService.listUsers(params.page, PAGE_SIZE),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.usersResource.isLoading());
  protected readonly totalPages = computed(() => this.usersResource.value().totalPages);

  /** Blocks a second click on the row an action is already running against. */
  protected readonly pendingUserId = signal<string | null>(null);

  // Separate from the resource's error so a failed action survives the reload after it (same
  // reasoning as admin-channels-page.ts).
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.usersResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  // Reads `lang()` so a language switch re-formats the dates (LOCALE_ID is bootstrap-static, same
  // as the other admin pages).
  protected readonly rows = computed(() => {
    const locale = toLocale(this.languageService.lang());
    const format = (iso: string) =>
      new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' });
    return this.usersResource.value().items.map((user) => ({
      ...user,
      // Hide the login when it is just the lowercased display name — that is Twitch's normal case,
      // and "HandOfBlood (handofblood)" twice per row is noise.
      showLogin: user.twitchUsername.toLowerCase() !== user.displayName.toLowerCase(),
      lastLoginFormatted: format(user.lastLogin),
      tokenExpiresFormatted: user.twitchAccessTokenExpiresAtUtc
        ? format(user.twitchAccessTokenExpiresAtUtc)
        : null,
      sessionsRevokedFormatted: user.sessionsValidFromUtc ? format(user.sessionsValidFromUtc) : null,
    }));
  });

  protected reload(): void {
    this.usersResource.reload();
  }

  protected onPageChange(newPage: number): void {
    this.page.set(newPage);
  }

  protected confirmRevoke(user: AdminUser): void {
    let message = this.translocoService.translate('admin.users.revoke.message', {
      name: user.displayName,
    });
    // Revoking one's own sessions is allowed — it is a normal logout, everywhere — but the dialog
    // says so, because "the admin page just logged me out" reads as a bug without the hint.
    if (user.twitchUserId === this.authService.currentUser()?.twitchUserId) {
      message += '\n\n' + this.translocoService.translate('admin.users.revoke.selfHint');
    }

    const data: ConfirmDialogData = {
      message,
      confirmLabel: this.translocoService.translate('admin.users.revoke.confirm'),
    };

    this.dialog
      .open<boolean>(ConfirmDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
      })
      .closed.subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }
        this.actionError.set(null);
        this.pendingUserId.set(user.twitchUserId);
        this.adminService.revokeSessions(user.twitchUserId).subscribe({
          next: () => {
            this.pendingUserId.set(null);
            // Full reload: the row's sessionsValidFromUtc and token badge both changed server-side.
            this.usersResource.reload();
          },
          error: (error: HttpErrorResponse) => {
            this.pendingUserId.set(null);
            this.actionError.set(apiErrorTranslationKey(error));
          },
        });
      });
  }
}
