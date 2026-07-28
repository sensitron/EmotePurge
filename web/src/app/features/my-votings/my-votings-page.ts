import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
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
        <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">{{ message | transloco }}</p>
      }

      @if (sessions().length === 0) {
        <p class="text-sm text-slate-400">{{ 'myVotings.noSessions' | transloco }}</p>
      } @else {
        <ul class="flex max-h-128 flex-col gap-2 overflow-y-auto rounded-md border border-slate-800 p-2">
          @for (session of sessions(); track session.sessionId) {
            <li class="rounded-md bg-slate-900 px-4 py-3">
              <div class="flex items-center justify-between">
                <a
                  [routerLink]="['/channels', session.channelName, 'vote-sessions', session.sessionId]"
                  class="font-medium hover:underline"
                >
                  {{ session.title }}
                </a>
                <span [class]="session.isActive ? 'text-sm text-emerald-400' : 'text-sm text-slate-500'">
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
  private readonly authService = inject(AuthService);

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

  private handleError(error: HttpErrorResponse): void {
    if (error.status === 401) {
      this.authService.handleSessionExpired();
      return;
    }
    this.errorMessage.set(apiErrorTranslationKey(error));
  }
}
