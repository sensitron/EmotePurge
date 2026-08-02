import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { PagedResult } from '../../core/models/paged-result.model';
import { MyVoteSession } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { Pager } from '../../shared/pagination/pager';
import { BackLink } from '../../shared/ui/back-link';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';

const EMPTY_PAGE: PagedResult<MyVoteSession> = {
  items: [],
  page: 1,
  pageSize: 20,
  totalCount: 0,
  totalPages: 0,
};

@Component({
  selector: 'app-my-votings-page',
  imports: [
    BackLink,
    EmptyState,
    NoticeBanner,
    RouterLink,
    Pager,
    SkeletonRows,
    StatusBadge,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-6">
      <!-- Sibling of the overview, not a child of it — but still a leaf the user has to get out
           of, and it had a way back only while empty (design doc §8.6). -->
      <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <!-- The heading doubles as the pager's scroll/focus target (§8.4). scroll-mt-14, not the
             scroll-mt-24 of the tabbed pages: this route hangs directly off the shell, so the h-14
             header is the only sticky layer above it (§8.5). -->
        <h2 #resultsTop tabindex="-1" class="scroll-mt-14 text-2xl font-bold tracking-tight">
          {{ 'shell.myVotings' | transloco }}
        </h2>
      </div>

      @if (errorMessage(); as message) {
        <app-notice-banner variant="error">{{ message | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="4" />
      } @else if (sessions().length === 0 && !errorMessage()) {
        <!-- No CTA back to the overview any more: the up-link above is always there now, and the
             empty state repeating it was the duplicate myVotings.goToOverview key. -->
        <app-empty-state
          icon="🗳️"
          [title]="'myVotings.noSessions' | transloco"
          [description]="'myVotings.noSessionsHint' | transloco"
        />
      } @else {
        <ul class="flex flex-col gap-2">
          @for (session of sessions(); track session.sessionId) {
            <li class="app-card app-card-interactive relative px-4 py-3">
              <div class="flex items-start justify-between gap-3">
                <a
                  [routerLink]="[
                    '/channels',
                    session.channelName,
                    'vote-sessions',
                    session.sessionId,
                  ]"
                  class="app-card-link min-w-0 font-medium hover:underline"
                >
                  {{ session.title }}
                </a>
                <app-status-badge
                  class="shrink-0"
                  [tone]="session.isActive ? 'success' : 'neutral'"
                >
                  {{
                    (session.isActive ? 'voting.list.statusActive' : 'voting.list.statusEnded')
                      | transloco
                  }}
                </app-status-badge>
              </div>
              <div class="mt-1 text-sm text-fg-muted">#{{ session.channelName }}</div>
            </li>
          }
        </ul>
        <app-pager
          [page]="page()"
          [totalPages]="totalPages()"
          [scrollTarget]="resultsTop"
          (pageChange)="onPageChange($event)"
        />
      }
    </div>
  `,
})
export class MyVotingsPage {
  private readonly voteSessionService = inject(VoteSessionService);

  protected readonly page = signal(1);

  // Follows the rxResource pilot from VoteSessionListPage (S3-17/S3-30): `params` reads `page()`,
  // so setting the signal is the whole reload trigger — no hand-written load()/effect() pair.
  private readonly sessionsResource = rxResource({
    params: () => this.page(),
    stream: ({ params }) => this.voteSessionService.listMine(params),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly sessions = computed(() => this.sessionsResource.value().items);
  protected readonly totalPages = computed(() => this.sessionsResource.value().totalPages);
  protected readonly isLoading = computed(() => this.sessionsResource.isLoading());

  // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
  // /api/ call in the app.
  protected readonly errorMessage = computed(() => {
    const error = this.sessionsResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  protected onPageChange(newPage: number): void {
    this.page.set(newPage);
  }
}
