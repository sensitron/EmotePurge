import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { debounceTime } from 'rxjs';

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
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';

const PAGE_SIZE = 25;

/** Long enough to swallow keystrokes of one word, short enough to still feel live. */
const FILTER_DEBOUNCE_MS = 300;

/** Actions without a channel dimension — the channel filter is meaningless while one is selected. */
const CHANNELLESS_ACTIONS: ReadonlySet<string> = new Set([
  'user.revokeSessions',
  'user.invalidateRoleCache',
]);

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
  'channel.resync': 'admin.audit.actions.channelResync',
  'voteSession.create': 'admin.audit.actions.voteSessionCreate',
  'voteSession.end': 'admin.audit.actions.voteSessionEnd',
  'voteSession.delete': 'admin.audit.actions.voteSessionDelete',
  'emotes.syncDeleted': 'admin.audit.actions.emotesSyncDeleted',
  'user.revokeSessions': 'admin.audit.actions.userRevokeSessions',
  'user.invalidateRoleCache': 'admin.audit.actions.userInvalidateRoleCache',
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
  if (typeof details['removedEntries'] === 'number') {
    return {
      key: 'admin.audit.details.removedEntries',
      params: { count: details['removedEntries'] },
    };
  }
  if (typeof details['title'] === 'string') {
    return { key: 'admin.audit.details.title', params: { title: details['title'] } };
  }
  // `login` is deliberately not rendered: user-scoped actions already carry the target's login in
  // their details, but the row would then read "by sensitron · handofblood" with nothing saying
  // which of the two names is the target. Unchanged from how user.revokeSessions is shown today.
  return null;
}

/**
 * The audit log: who did what, when, to which channel. Read-only by construction — entries are
 * written by the services that perform the actions, inside those actions' own transactions, and
 * nothing in the UI can create, edit or delete one.
 */
@Component({
  selector: 'app-admin-audit-log-page',
  imports: [
    Button,
    EmptyState,
    NoticeBanner,
    Pager,
    RouterLink,
    SegmentedControl,
    SkeletonRows,
    TranslocoPipe,
  ],
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

      <!-- Sticky below header (top-14) + admin tabs (h-10): top-24. py-2 gives the blur a surface
           instead of a hard edge against the rows scrolling through underneath (design doc §8.5). -->
      <div class="app-sticky-bar top-24 flex flex-col gap-3 py-2">
        <app-segmented-control
          [options]="actionOptions"
          [value]="actionFilter()"
          (valueChange)="onActionFilterChange($event)"
          [ariaLabel]="'admin.audit.filter.actionLabel' | transloco"
        />
        <!-- Filter-toolbar fields: no visible label by design, aria-label + title instead (§5.2).
             Live filtering per keystroke like the emote grid; requests are debounced in the class. -->
        <div class="flex flex-wrap items-center gap-2">
          <input
            type="text"
            class="app-input-sm w-44 disabled:cursor-not-allowed disabled:opacity-50"
            [value]="channelFilter()"
            (input)="onChannelFilterInput($event)"
            [disabled]="channelFilterDisabled()"
            [placeholder]="'admin.audit.filter.channelPlaceholder' | transloco"
            [attr.aria-label]="'admin.audit.filter.channelLabel' | transloco"
            [title]="
              (channelFilterDisabled()
                ? 'admin.audit.filter.channelNotApplicable'
                : 'admin.audit.filter.channelLabel'
              ) | transloco
            "
          />
          <input
            type="text"
            class="app-input-sm w-44"
            [value]="actorFilter()"
            (input)="onActorFilterInput($event)"
            [placeholder]="'admin.audit.filter.actorPlaceholder' | transloco"
            [attr.aria-label]="'admin.audit.filter.actorLabel' | transloco"
            [title]="'admin.audit.filter.actorLabel' | transloco"
          />
          @if (hasActiveFilters()) {
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'admin.audit.filter.reset' | transloco }}
            </button>
          }
        </div>
      </div>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      } @else if (rows().length === 0) {
        @if (hasActiveFilters()) {
          <app-empty-state
            icon="🔍"
            [title]="'admin.audit.filter.noMatches' | transloco"
            [description]="'admin.audit.filter.noMatchesHint' | transloco"
          >
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'admin.audit.filter.reset' | transloco }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            icon="📋"
            [title]="'admin.audit.empty' | transloco"
            [description]="'admin.audit.emptyHint' | transloco"
          />
        }
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
                    class="max-w-full truncate font-medium text-slate-300 underline-offset-2 hover:text-purple-300 hover:underline"
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

  // Live filter state, updated per keystroke — same feel as the emote grid's filter. That one is
  // client-side over an in-memory array; here a paged endpoint sits behind every change, so the
  // text values reach the request only through the debounced signals below.
  protected readonly actionFilter = signal('');
  protected readonly channelFilter = signal('');
  protected readonly actorFilter = signal('');

  private readonly debouncedChannelFilter = toSignal(
    toObservable(this.channelFilter).pipe(debounceTime(FILTER_DEBOUNCE_MS)),
    { initialValue: '' },
  );
  private readonly debouncedActorFilter = toSignal(
    toObservable(this.actorFilter).pipe(debounceTime(FILTER_DEBOUNCE_MS)),
    { initialValue: '' },
  );

  /** True while an action without a channel dimension is selected — the channel input is disabled
   *  then, and a previously typed channel value has already been cleared by the action handler. */
  protected readonly channelFilterDisabled = computed(() =>
    CHANNELLESS_ACTIONS.has(this.actionFilter()),
  );

  /** "All" plus one segment per known action, reusing the row labels — the filter can only ever
   *  offer what this build can name. */
  protected readonly actionOptions: SegmentedControlOption[] = [
    { value: '', labelKey: 'admin.audit.filter.all' },
    ...Object.entries(ACTION_KEYS).map(([value, labelKey]) => ({ value, labelKey })),
  ];

  protected readonly hasActiveFilters = computed(
    () => this.actionFilter() !== '' || this.channelFilter() !== '' || this.actorFilter() !== '',
  );

  // Same rxResource shape as vote-session-list-page.ts: setting `page` or a filter is the whole
  // reload trigger, no hand-written effect. The action segment applies immediately; the text
  // filters flow in through their debounced counterparts.
  private readonly auditLogResource = rxResource({
    params: () => ({
      page: this.page(),
      action: this.actionFilter(),
      // Short-circuited while disabled: the debounced signal lags the cleared raw value by one
      // debounce window, and that window must not produce a request with the stale channel.
      channel: this.channelFilterDisabled() ? '' : this.debouncedChannelFilter(),
      actor: this.debouncedActorFilter(),
    }),
    stream: ({ params }) =>
      this.adminService.listAuditLog(params.page, PAGE_SIZE, {
        action: params.action || undefined,
        channel: params.channel || undefined,
        actor: params.actor || undefined,
      }),
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

  // Every filter change jumps back to page 1: the old page number belongs to the old result set,
  // and a filter that shrinks the set below the current page would otherwise show a stray empty page.
  protected onActionFilterChange(action: string): void {
    this.actionFilter.set(action);
    if (CHANNELLESS_ACTIONS.has(action)) {
      // A stale channel value combined with a channel-less action could only ever match nothing;
      // clearing it here is what makes disabling the input honest.
      this.channelFilter.set('');
    }
    this.page.set(1);
  }

  protected onChannelFilterInput(event: Event): void {
    this.channelFilter.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }

  protected onActorFilterInput(event: Event): void {
    this.actorFilter.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }

  protected resetFilters(): void {
    this.actionFilter.set('');
    this.channelFilter.set('');
    this.actorFilter.set('');
    this.page.set(1);
  }
}
