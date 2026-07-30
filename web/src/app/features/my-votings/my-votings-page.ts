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
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { StatusBadge } from '../../shared/ui/status-badge';

const EMPTY_PAGE: PagedResult<MyVoteSession> = { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 };

@Component({
  selector: 'app-my-votings-page',
  imports: [Button, EmptyState, NoticeBanner, RouterLink, Pager, StatusBadge, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-6">
      <h2 class="text-lg font-medium">{{ 'shell.myVotings' | transloco }}</h2>

      @if (errorMessage(); as message) {
        <app-notice-banner variant="error">{{ message | transloco }}</app-notice-banner>
      }

      @if (sessions().length === 0 && !errorMessage()) {
        <app-empty-state
          [title]="'myVotings.noSessions' | transloco"
          [description]="'myVotings.noSessionsHint' | transloco"
        >
          <a routerLink="/" appButton="neutral">{{ 'myVotings.goToOverview' | transloco }}</a>
        </app-empty-state>
      } @else {
        <ul class="flex flex-col gap-2">
          @for (session of sessions(); track session.sessionId) {
            <li class="relative rounded-md bg-slate-900 px-4 py-3 transition hover:bg-slate-800/70">
              <div class="flex items-start justify-between gap-3">
                <a
                  [routerLink]="['/channels', session.channelName, 'vote-sessions', session.sessionId]"
                  class="app-card-link min-w-0 font-medium hover:underline"
                >
                  {{ session.title }}
                </a>
                <app-status-badge class="shrink-0" [tone]="session.isActive ? 'emerald' : 'slate'">
                  {{ (session.isActive ? 'voting.list.statusActive' : 'voting.list.statusEnded') | transloco }}
                </app-status-badge>
              </div>
              <div class="mt-1 text-sm text-slate-400">#{{ session.channelName }}</div>
            </li>
          }
        </ul>
        <app-pager [page]="page()" [totalPages]="totalPages()" (pageChange)="onPageChange($event)" />
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
