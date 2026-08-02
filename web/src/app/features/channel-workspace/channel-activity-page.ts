import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, signal } from '@angular/core';
import { rxResource, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { debounceTime } from 'rxjs';

import { AuditLogEntry } from '../../core/audit/audit.model';
import { ChannelAuditService } from '../../core/channels/channel-audit.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { ACTION_KEYS, CHANNEL_SCOPED_ACTIONS } from '../../shared/audit/audit-actions';
import { AuditLogList } from '../../shared/audit/audit-log-list';
import { toAuditRows } from '../../shared/audit/audit-row';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';

const PAGE_SIZE = 25;

/** Long enough to swallow keystrokes of one word, short enough to still feel live. */
const FILTER_DEBOUNCE_MS = 300;

const EMPTY_PAGE: PagedResult<AuditLogEntry> = {
  items: [],
  page: 1,
  pageSize: PAGE_SIZE,
  totalCount: 0,
  totalPages: 0,
};

/**
 * The channel's own activity feed: who joined or left, who created, ended or deleted a vote session,
 * who reported emotes as deleted, who triggered a resync. Same rows as the global-admin log, scoped
 * to one channel — and only for its management team, because the rows name people.
 *
 * Read-only by construction: entries are written by the services that perform the actions, and
 * nothing here can create, edit or delete one.
 */
@Component({
  selector: 'app-channel-activity-page',
  imports: [
    AuditLogList,
    Button,
    EmptyState,
    NoticeBanner,
    Pager,
    SegmentedControl,
    SkeletonRows,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">
          {{ 'channelWorkspace.activity.title' | transloco }}
        </h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'channelWorkspace.activity.refreshTitle' | transloco"
        >
          {{ 'channelWorkspace.activity.refresh' | transloco }}
        </button>
      </header>

      <!-- Sticky below the shell header (top-14) + the workspace tabs (h-10): top-24, the same
           contract the admin log follows. py-2 gives the blur a surface instead of a hard edge. -->
      <div class="app-sticky-bar top-24 flex flex-col gap-3 py-2">
        <app-segmented-control
          [options]="actionOptions"
          [value]="actionFilter()"
          (valueChange)="onActionFilterChange($event)"
          [ariaLabel]="'channelWorkspace.activity.filter.actionLabel' | transloco"
        />
        <!-- Filter-toolbar field: no visible label by design, aria-label + title instead (§5.2). -->
        <div class="flex flex-wrap items-center gap-2">
          <input
            type="text"
            class="app-input-sm w-44"
            [value]="actorFilter()"
            (input)="onActorFilterInput($event)"
            [placeholder]="'channelWorkspace.activity.filter.actorPlaceholder' | transloco"
            [attr.aria-label]="'channelWorkspace.activity.filter.actorLabel' | transloco"
            [title]="'channelWorkspace.activity.filter.actorLabel' | transloco"
          />
          @if (hasActiveFilters()) {
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'channelWorkspace.activity.filter.reset' | transloco }}
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
            [title]="'channelWorkspace.activity.filter.noMatches' | transloco"
            [description]="'channelWorkspace.activity.filter.noMatchesHint' | transloco"
          >
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'channelWorkspace.activity.filter.reset' | transloco }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            icon="📋"
            [title]="'channelWorkspace.activity.empty' | transloco"
            [description]="'channelWorkspace.activity.emptyHint' | transloco"
          />
        }
      } @else {
        <!-- showChannel off: every row here carries this channel, and the link would point at the
             page the reader is already on. -->
        <app-audit-log-list [rows]="rows()" [showChannel]="false" />

        <app-pager
          [page]="page()"
          [totalPages]="totalPages()"
          (pageChange)="onPageChange($event)"
        />
      }
    </div>
  `,
})
export class ChannelActivityPage {
  readonly channelName = input.required<string>();

  private readonly channelAuditService = inject(ChannelAuditService);
  private readonly languageService = inject(LanguageService);

  protected readonly page = signal(1);

  protected readonly actionFilter = signal('');
  protected readonly actorFilter = signal('');

  private readonly debouncedActorFilter = toSignal(
    toObservable(this.actorFilter).pipe(debounceTime(FILTER_DEBOUNCE_MS)),
    { initialValue: '' },
  );

  /**
   * "All" plus one segment per action a channel's log can actually contain — built from
   * `CHANNEL_SCOPED_ACTIONS`, not from the full table: the two user-scoped actions are global and
   * would be segments that can only ever return nothing.
   */
  protected readonly actionOptions: SegmentedControlOption[] = [
    { value: '', labelKey: 'channelWorkspace.activity.filter.all' },
    ...CHANNEL_SCOPED_ACTIONS.map((value) => ({ value, labelKey: ACTION_KEYS[value] })),
  ];

  protected readonly hasActiveFilters = computed(
    () => this.actionFilter() !== '' || this.actorFilter() !== '',
  );

  // Reading the required channelName() input inside `params` rather than in the constructor is what
  // keeps this off NG0950: rxResource evaluates it lazily, after the route inputs are applied.
  private readonly auditLogResource = rxResource({
    params: () => ({
      channel: this.channelName(),
      page: this.page(),
      action: this.actionFilter(),
      actor: this.debouncedActorFilter(),
    }),
    stream: ({ params }) =>
      this.channelAuditService.listAuditLog(params.channel, params.page, PAGE_SIZE, {
        action: params.action || undefined,
        actor: params.actor || undefined,
      }),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.auditLogResource.isLoading());
  protected readonly totalPages = computed(() => this.auditLogResource.value().totalPages);

  // Reads `lang()` so a language switch re-formats the timestamps: LOCALE_ID is fixed at bootstrap
  // and cannot follow one.
  protected readonly rows = computed(() =>
    toAuditRows(this.auditLogResource.value().items, toLocale(this.languageService.lang())),
  );

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

  // Every filter change jumps back to page 1: the old page number belongs to the old result set, and
  // a filter that shrinks the set below the current page would otherwise show a stray empty page.
  protected onActionFilterChange(action: string): void {
    this.actionFilter.set(action);
    this.page.set(1);
  }

  protected onActorFilterInput(event: Event): void {
    this.actorFilter.set((event.target as HTMLInputElement).value);
    this.page.set(1);
  }

  protected resetFilters(): void {
    this.actionFilter.set('');
    this.actorFilter.set('');
    this.page.set(1);
  }
}
