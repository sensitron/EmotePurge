import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { VoteSessionResult, VoteSessionResults, VoteType } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

// Row height (px) fed to CdkVirtualScrollViewport — see the identical comment in UsageStatsPage.
// Taller than the usage-stats grid: each card also carries a score line and two vote buttons.
const ROW_HEIGHT_PX = 176;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [ScrollingModule, NgOptimizedImage, MassDeletePanel, EmoteCardHeader, TranslocoPipe],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  templateUrl: './vote-session-detail-page.html',
})
export class VoteSessionDetailPage {
  readonly channelName = input.required<string>();
  readonly sessionId = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);
  private readonly translocoService = inject(TranslocoService);
  private readonly languageService = inject(LanguageService);

  protected readonly voteType = VoteType;
  protected readonly currentUser = this.authService.currentUser;

  protected readonly rowHeight = ROW_HEIGHT_PX;
  protected readonly columns = signal(computeGridColumns(window.innerWidth));

  protected readonly results = signal<VoteSessionResults | null>(null);
  protected readonly canManage = signal(false);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);

  // Freezes the card order (by emote id) across post-vote reloads, since the backend sorts by
  // score descending — without this, voting an emote's score down to the bottom instantly yanks
  // its card to the end of the list while the user is still looking at it.
  protected readonly orderedEmoteIds = signal<string[] | null>(null);

  protected readonly orderedEmotes = computed(() => {
    const results = this.results();
    if (!results) {
      return [];
    }
    const order = this.orderedEmoteIds();
    if (!order) {
      return results.emotes;
    }

    const byId = new Map(results.emotes.map((emote) => [emote.emoteId, emote]));
    const ordered: VoteSessionResult[] = [];
    for (const id of order) {
      const emote = byId.get(id);
      if (emote) {
        ordered.push(emote);
        byId.delete(id);
      }
    }
    // Emotes not present in the frozen order (e.g. synced since the last freeze) are appended at
    // the end, in the backend's current order among themselves.
    for (const emote of results.emotes) {
      if (byId.has(emote.emoteId)) {
        ordered.push(emote);
      }
    }
    return ordered;
  });

  protected readonly usageFilter = new EmoteUsageFilter<VoteSessionResult>(() => this.selection.clear());

  protected readonly emotes = computed(() => this.usageFilter.apply(this.orderedEmotes()));

  protected readonly rows = computed(() => chunkIntoRows(this.emotes(), this.columns()));
  protected readonly selection = new ListSelection(this.emotes, (emote) => emote.emoteId);

  protected readonly emoteCountKey = computed(() => pluralKey(this.orderedEmotes().length, 'emoteCount'));

  // Resolved items rather than selection.selectedKeys(): the delete engine needs sevenTvEmoteId and
  // the display name, which only the loaded row carries. Should a selected emote vanish from the
  // list between selecting and deleting (archived by the periodic 7TV resync), it silently drops
  // out here — the conservative direction, since it can only ever delete fewer emotes than shown.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  constructor() {
    // Deferred, not called directly — see the identical comment in VoteSessionListPage.
    effect(() => this.load());
  }

  protected updateColumns(): void {
    this.columns.set(computeGridColumns(window.innerWidth));
  }

  protected formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'short',
    });
  }

  // LOCALE_ID is never set app-wide (bootstrap-time static, can't react to a runtime language
  // switch), so DecimalPipe always formatted with 'en-US' regardless of the active UI language —
  // same reasoning as formatDateTime()/toLocale() above, just for numbers instead of dates.
  protected formatScore(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang()), { maximumFractionDigits: 1 }).format(value);
  }

  protected keepButtonTitle(emote: VoteSessionResult): string {
    const labelKey = emote.myVote === VoteType.Keep ? 'voting.detail.retractVote' : 'voting.detail.keepAriaLabel';
    return `${this.translocoService.translate(labelKey)} (${emote.keepVotes})`;
  }

  protected deleteButtonTitle(emote: VoteSessionResult): string {
    const labelKey = emote.myVote === VoteType.Delete ? 'voting.detail.retractVote' : 'voting.detail.deleteAriaLabel';
    return `${this.translocoService.translate(labelKey)} (${emote.deleteVotes})`;
  }

  private load(options: { freeze: boolean } = { freeze: true }): void {
    const channelName = this.channelName();
    const sessionId = Number(this.sessionId());

    // voteSessionAccessGuard already verified login + audience eligibility before this component
    // was even mounted — this call should always succeed for whoever reached this page normally.
    this.voteSessionService.getResults(channelName, sessionId).subscribe({
      next: (results) => {
        this.results.set(results);
        if (options.freeze) {
          this.orderedEmoteIds.set(results.emotes.map((emote) => emote.emoteId));
        }
        // No selection.clear() here on purpose: ListSelection keys by emote id, so the freshly
        // deserialized objects this assigns resolve back to the same selection. Clearing would
        // throw away a 50-emote selection on every single vote, since vote() reloads through here.
      },
      error: () => this.errorMessage.set('voting.detail.errors.loadFailed'),
    });

    this.channelService.getStatus(channelName).subscribe({
      next: (status) => {
        this.canManage.set(true);
        this.activeEmoteSetId.set(status.activeEmoteSetId);
      },
      error: () => this.canManage.set(false),
    });
  }

  protected refresh(): void {
    this.load();
  }

  protected vote(emote: VoteSessionResult, type: VoteType): void {
    // Defensive fallback, not the primary gate — voteSessionAccessGuard already required a login
    // to reach this page, but the session cookie/token can still expire while already viewing it.
    if (!this.currentUser()) {
      this.authService.login(window.location.pathname);
      return;
    }

    this.errorMessage.set(null);

    // Clicking the same vote type again retracts it, returning the emote to the neutral state —
    // otherwise a Keep vote could only ever be overwritten by a Delete vote, never undone.
    const request$: Observable<unknown> =
      emote.myVote === type
        ? this.voteSessionService.retractVote(this.channelName(), Number(this.sessionId()), emote.emoteId)
        : this.voteSessionService.castVote(this.channelName(), Number(this.sessionId()), emote.emoteId, type);

    request$.subscribe({
      next: () => this.load({ freeze: false }),
      error: (error: HttpErrorResponse) => this.handleVoteError(error),
    });
  }

  // Was a second, competing status→message mapping alongside apiErrorTranslationKey; the codes it
  // re-derived by status (vote_session_ended, vote_session_not_found, emote_not_eligible) all exist
  // in the response bodies, so it can share the one mapping now. 401 is gone entirely —
  // apiAuthInterceptor handles it app-wide.
  //
  // 403 stays a special case on purpose: it comes from VoteEligibilityFilter and means one specific
  // thing here — the wrong role for *this session's* audience. The generic 403 message points at the
  // mod-role cache instead, which would be actively misleading in, say, a subs-only session.
  private handleVoteError(error: HttpErrorResponse): void {
    this.errorMessage.set(error.status === 403 ? 'voting.detail.errors.forbidden' : apiErrorTranslationKey(error));
  }

  protected onDeleted(deletedIds: string[]): void {
    this.results.update((results) =>
      results ? { ...results, emotes: results.emotes.filter((emote) => !deletedIds.includes(emote.emoteId)) } : results,
    );
    this.selection.clear();
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.load();
  }
}
