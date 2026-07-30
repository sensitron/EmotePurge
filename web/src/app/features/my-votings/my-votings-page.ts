import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { MyVoteSession } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { Pager } from '../../shared/pagination/pager';

@Component({
  selector: 'app-my-votings-page',
  imports: [RouterLink, Pager, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-6">
      <h2 class="text-lg font-medium">{{ 'shell.myVotings' | transloco }}</h2>

      @if (errorMessage(); as message) {
        <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300" role="alert">{{ message | transloco }}</p>
      }

      @if (sessions().length === 0) {
        <p class="text-sm text-slate-400">{{ 'myVotings.noSessions' | transloco }}</p>
      } @else {
        <ul class="flex max-h-128 flex-col gap-2 overflow-y-auto rounded-md border border-slate-800 p-2">
          @for (session of sessions(); track session.sessionId) {
            <li class="relative rounded-md bg-slate-900 px-4 py-3 transition hover:bg-slate-800/70">
              <div class="flex items-start justify-between gap-3">
                <a
                  [routerLink]="['/channels', session.channelName, 'vote-sessions', session.sessionId]"
                  class="app-card-link min-w-0 font-medium hover:underline"
                >
                  {{ session.title }}
                </a>
                <span [class]="session.isActive ? 'shrink-0 text-sm text-emerald-400' : 'shrink-0 text-sm text-slate-500'">
                  {{ (session.isActive ? 'voting.list.statusActive' : 'voting.list.statusEnded') | transloco }}
                </span>
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

  protected readonly sessions = signal<MyVoteSession[]>([]);
  protected readonly page = signal(1);
  protected readonly totalPages = signal(0);
  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    // No route params here (unlike the channel-scoped voting pages), so a direct constructor call
    // is safe — nothing to defer via effect().
    this.load();
  }

  private load(): void {
    this.voteSessionService.listMine(this.page()).subscribe({
      next: (result) => {
        this.sessions.set(result.items);
        this.totalPages.set(result.totalPages);
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected onPageChange(newPage: number): void {
    this.page.set(newPage);
    this.load();
  }

  // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
  // /api/ call in the app.
  private handleError(error: HttpErrorResponse): void {
    this.errorMessage.set(apiErrorTranslationKey(error));
  }
}
