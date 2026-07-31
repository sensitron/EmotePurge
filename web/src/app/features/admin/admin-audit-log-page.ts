import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuditLogEntry } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';

const PAGE_SIZE = 25;

const EMPTY_PAGE: PagedResult<AuditLogEntry> = {
  items: [],
  page: 1,
  pageSize: PAGE_SIZE,
  totalCount: 0,
  totalPages: 0,
};

/**
 * One translation key per `AuditActions` constant. A lookup rather than a string transform
 * ("channel.join" → "channelJoin"), so a newly added action shows up as an obvious gap here instead
 * of silently producing a key that exists in neither locale file.
 */
const ACTION_KEYS: Record<string, string> = {
  'channel.join': 'admin.audit.actions.channelJoin',
  'channel.leave': 'admin.audit.actions.channelLeave',
  'channel.purge': 'admin.audit.actions.channelPurge',
  'voteSession.create': 'admin.audit.actions.voteSessionCreate',
  'voteSession.end': 'admin.audit.actions.voteSessionEnd',
  'voteSession.delete': 'admin.audit.actions.voteSessionDelete',
  'emotes.syncDeleted': 'admin.audit.actions.emotesSyncDeleted',
};

interface AuditDetail {
  key: string;
  params: Record<string, string | number>;
}

/** A row as the template consumes it — every derivation done once, in TypeScript. */
interface AuditRow {
  id: number;
  occurredAtUtc: string;
  timestamp: string;
  actorLogin: string;
  /** Translation key for the action, or null when this build does not know the action. */
  actionKey: string | null;
  /** The raw action string, shown verbatim when `actionKey` is null. */
  action: string;
  channelName: string | null;
  detail: AuditDetail | null;
}

/**
 * `detailsJson` is free-form per action and arrives as raw text from a jsonb column this frontend
 * never validates, so every step is defensive: a parse failure, a non-object payload or an
 * unexpected member type all degrade to "no detail" instead of throwing inside change detection and
 * blanking the whole page over one malformed row.
 */
function parseDetail(detailsJson: string | null): AuditDetail | null {
  if (!detailsJson) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(detailsJson);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }

  const details = parsed as Record<string, unknown>;
  if (typeof details['emoteCount'] === 'number') {
    return { key: 'admin.audit.details.emoteCount', params: { count: details['emoteCount'] } };
  }
  if (typeof details['title'] === 'string') {
    return { key: 'admin.audit.details.title', params: { title: details['title'] } };
  }
  return null;
}

/**
 * The audit log: who did what, when, to which channel. Read-only by construction — entries are
 * written by the services that perform the actions, inside those actions' own transactions, and
 * nothing in the UI can create, edit or delete one.
 */
@Component({
  selector: 'app-admin-audit-log-page',
  imports: [Button, EmptyState, NoticeBanner, Pager, RouterLink, SkeletonRows, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.audit.title' | transloco }}</h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.audit.refreshTitle' | transloco"
        >
          {{ 'admin.audit.refresh' | transloco }}
        </button>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      } @else if (rows().length === 0) {
        <app-empty-state
          icon="📋"
          [title]="'admin.audit.empty' | transloco"
          [description]="'admin.audit.emptyHint' | transloco"
        />
      } @else {
        <ul class="flex flex-col gap-2">
          @for (row of rows(); track row.id) {
            <li class="app-card flex flex-col gap-1 px-4 py-3">
              <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <span class="font-medium text-slate-100">
                  @if (row.actionKey; as key) {
                    {{ key | transloco }}
                  } @else {
                    <!-- An action this build has no label for: better raw than hidden. -->
                    {{ row.action }}
                  }
                </span>
                @if (row.channelName; as channel) {
                  <a
                    [routerLink]="['/channels', channel, 'usage-stats']"
                    class="max-w-full truncate text-sm text-slate-300 underline-offset-2 hover:text-purple-300 hover:underline"
                  >
                    #{{ channel }}
                  </a>
                }
              </div>

              <p class="flex flex-wrap gap-x-2 gap-y-1 text-xs text-slate-400">
                <time [attr.datetime]="row.occurredAtUtc">{{ row.timestamp }}</time>
                <span aria-hidden="true">·</span>
                <span>{{ 'admin.audit.actor' | transloco: { actor: row.actorLogin } }}</span>
                @if (row.detail; as detail) {
                  <span aria-hidden="true">·</span>
                  <span>{{ detail.key | transloco: detail.params }}</span>
                }
              </p>
            </li>
          }
        </ul>

        <app-pager
          [page]="page()"
          [totalPages]="totalPages()"
          (pageChange)="onPageChange($event)"
        />
      }
    </div>
  `,
})
export class AdminAuditLogPage {
  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);

  protected readonly page = signal(1);

  // Same rxResource shape as vote-session-list-page.ts: setting `page` is the whole reload trigger,
  // no hand-written effect and no double request.
  private readonly auditLogResource = rxResource({
    params: () => ({ page: this.page() }),
    stream: ({ params }) => this.adminService.listAuditLog(params.page, PAGE_SIZE),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.auditLogResource.isLoading());
  protected readonly totalPages = computed(() => this.auditLogResource.value().totalPages);

  // Reads `lang()` so a language switch re-formats the timestamps: LOCALE_ID is fixed at bootstrap
  // and cannot follow one (same reasoning as the other two admin pages). Seconds are shown because
  // several audited actions can legitimately land in the same minute.
  protected readonly rows = computed<AuditRow[]>(() => {
    const locale = toLocale(this.languageService.lang());
    return this.auditLogResource.value().items.map((entry) => ({
      id: entry.id,
      occurredAtUtc: entry.occurredAtUtc,
      timestamp: new Date(entry.occurredAtUtc).toLocaleString(locale, {
        dateStyle: 'short',
        timeStyle: 'medium',
      }),
      actorLogin: entry.actorLogin,
      actionKey: ACTION_KEYS[entry.action] ?? null,
      action: entry.action,
      channelName: entry.channelName,
      detail: parseDetail(entry.detailsJson),
    }));
  });

  protected readonly errorMessage = computed(() => {
    const error = this.auditLogResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  protected reload(): void {
    this.auditLogResource.reload();
  }

  protected onPageChange(newPage: number): void {
    this.page.set(newPage);
  }
}
