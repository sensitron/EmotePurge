import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable, debounceTime, filter, switchMap } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { LIVE_EVENT_TYPES, LiveEvent, channelLiveUrl } from '../../core/live/live-event.model';
import { LiveUpdateService } from '../../core/live/live-update.service';
import {
  VoteSessionResult,
  VoteSessionResults,
  VoteType,
} from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

// Row height (px) fed to CdkVirtualScrollViewport — see the identical comment in UsageStatsPage.
// Taller than the usage-stats grid: each card also carries the stats lines and the vote buttons.
// Card h-44 (176) + row py-2 (16). One height for all breakpoints: below `sm` the buttons sit
// side by side at 44px, which needs *less* height than the stacked desktop pair.
//
// What the 176 has to hold (desktop, the taller of the two): p-2 16 + name header h-5 20 + image
// block h-10 40 + the two stacked stat lines 2x15 + vote buttons (2x min-h-6 + gap-1) 52 + three
// gap-1 12 = 170. Stacking the score under the usage cost 10 of the 16 px that were spare, so it
// still fits without touching this constant — but the remaining 6 px is the whole budget. Anything
// further added to the card raises h-44 and this number together.
const ROW_HEIGHT_PX = 192;

// Votes from other people arrive one by one, and every single one shifts *all* scores (the score is
// min-max normalized across the channel). Half a second is short enough to feel live and long
// enough that a moderator clicking through ten emotes produces one refetch, not ten. The same
// window also collapses a `usage.flushed` that lands next to a vote into a single refetch.
const LIVE_RELOAD_DEBOUNCE_MS = 500;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [
    Button,
    EmptyState,
    NoticeBanner,
    ScrollingModule,
    NgOptimizedImage,
    MassDeletePanel,
    EmoteCardHeader,
    TranslocoPipe,
  ],
  host: {
    '(window:resize)': 'onResize()',
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
  private readonly liveUpdateService = inject(LiveUpdateService);

  // Lazy on purpose — reading the required channelName input during construction would throw
  // NG0950; the computed is first evaluated inside the toObservable effect below.
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));

  protected readonly voteType = VoteType;
  protected readonly currentUser = this.authService.currentUser;

  private readonly viewportWidth = signal(window.innerWidth);
  protected readonly columns = computed(() => computeGridColumns(this.viewportWidth()));
  protected readonly rowHeight = ROW_HEIGHT_PX;

  protected readonly results = signal<VoteSessionResults | null>(null);
  protected readonly skeletonCells = Array.from({ length: 10 }, (_, i) => i);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);

  // Mirrors the server's own gate for the raw usage figure: GET .../results only fills
  // TotalUseCount when CanManageChannelAsync passes — and for everyone else it reports 0, not null.
  // Without this flag the card cannot tell "used 0 times" from "not allowed to know", so it showed
  // a flat "0x Nutzung" on every emote to every voter. The same CanManageChannelAsync answers
  // GET /permissions, so the two verdicts come from one source.
  protected readonly canSeeUsage = signal(false);

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

  // Prune, don't clear (S2-16): narrowing a filter keeps the still-visible part of the selection,
  // while anything filtered out is dropped so the delete path never holds an off-screen emote.
  protected readonly usageFilter = new EmoteUsageFilter<VoteSessionResult>(() =>
    this.selection.retainVisible(),
  );

  protected readonly emotes = computed(() => this.usageFilter.apply(this.orderedEmotes()));

  protected readonly rows = computed(() => chunkIntoRows(this.emotes(), this.columns()));
  protected readonly selection = new ListSelection(this.emotes, (emote) => emote.emoteId);

  protected readonly emoteCountKey = computed(() =>
    pluralKey(this.orderedEmotes().length, 'emoteCount'),
  );

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

    // Kept out of load(): the verdict depends on the channel alone, while load() runs again after
    // every single vote — refetching permissions there would be one wasted request per click.
    effect(() => this.loadPermissions(this.channelName()));

    // Live tally *and* live usage, off the one channel stream this page already holds open.
    // Results only: re-running the channel status side-load on every incoming event would triple
    // the request volume for a value that changes on a 7TV sync, not on a vote or a flush.
    // No echo suppression — one's own vote already reloads through vote(), and the debounce merges
    // that with the push it caused; loadResults is idempotent either way.
    toObservable(this.liveUrl)
      .pipe(
        switchMap((url) => this.liveUpdateService.stream(url)),
        filter((event) => this.isRelevantLiveEvent(event)),
        debounceTime(LIVE_RELOAD_DEBOUNCE_MS),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.loadResults({ freeze: false }));
  }

  /**
   * `vote.changed` is per session and must match this one. `usage.flushed` is channel-scoped and
   * carries no session id — the stream is already this channel's, so its arrival alone is the
   * signal. Listening to it is not optional: chat usage only moves on the worker's 30 s batch
   * flush, and the score is normalized usage plus the keep/delete delta, so a session nobody votes
   * in kept showing the usage counts and scores it was first loaded with, indefinitely.
   */
  private isRelevantLiveEvent(event: LiveEvent): boolean {
    if (event.type === LIVE_EVENT_TYPES.usageFlushed) {
      return true;
    }
    return (
      event.type === LIVE_EVENT_TYPES.voteChanged && event.sessionId === Number(this.sessionId())
    );
  }

  protected onResize(): void {
    this.viewportWidth.set(window.innerWidth);
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
    return new Intl.NumberFormat(toLocale(this.languageService.lang()), {
      maximumFractionDigits: 1,
    }).format(value);
  }

  // Full, untruncated stats wording as a tooltip — the visible lines may still ellipsize on narrow
  // cards. Omits the usage half for a viewer who is not shown it, so the tooltip never states a
  // number the card deliberately withholds.
  protected statsTitle(emote: VoteSessionResult): string {
    const score = `${this.translocoService.translate('voting.detail.scoreLabel')} ${this.formatScore(emote.score)}`;
    if (!this.canSeeUsage()) {
      return score;
    }
    const usage = this.translocoService.translate('usageStats.usageLabel');
    return `${emote.totalUseCount}x ${usage} · ${score}`;
  }

  protected keepButtonTitle(emote: VoteSessionResult): string {
    const labelKey =
      emote.myVote === VoteType.Keep ? 'voting.detail.retractVote' : 'voting.detail.keepAriaLabel';
    return `${this.translocoService.translate(labelKey)} (${emote.keepVotes})`;
  }

  protected deleteButtonTitle(emote: VoteSessionResult): string {
    const labelKey =
      emote.myVote === VoteType.Delete
        ? 'voting.detail.retractVote'
        : 'voting.detail.deleteAriaLabel';
    return `${this.translocoService.translate(labelKey)} (${emote.deleteVotes})`;
  }

  private load(options: { freeze: boolean } = { freeze: true }): void {
    this.loadResults(options);
    this.loadActiveEmoteSetId();
  }

  private loadResults(options: { freeze: boolean }): void {
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
  }

  /** UI visibility only — the server decides what it actually reports. A failure hides the usage
   *  figures rather than guessing, which is the harmless direction: the score stays visible. */
  private loadPermissions(channelName: string): void {
    this.channelService.getPermissions(channelName).subscribe({
      next: (permissions) => this.canSeeUsage.set(permissions.canManage),
      error: () => this.canSeeUsage.set(false),
    });
  }

  /** Split out of load() so the live-update path can refetch the tally alone — this value only
   *  changes when the 7TV set is resynced, never as a result of a vote. */
  private loadActiveEmoteSetId(): void {
    this.channelService.getStatus(this.channelName()).subscribe({
      next: (status) => this.activeEmoteSetId.set(status.activeEmoteSetId),
      error: () => this.activeEmoteSetId.set(null),
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
        ? this.voteSessionService.retractVote(
            this.channelName(),
            Number(this.sessionId()),
            emote.emoteId,
          )
        : this.voteSessionService.castVote(
            this.channelName(),
            Number(this.sessionId()),
            emote.emoteId,
            type,
          );

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
    this.errorMessage.set(
      error.status === 403 ? 'voting.detail.errors.forbidden' : apiErrorTranslationKey(error),
    );
  }

  protected onDeleted(deletedIds: string[]): void {
    this.results.update((results) =>
      results
        ? {
            ...results,
            emotes: results.emotes.filter((emote) => !deletedIds.includes(emote.emoteId)),
          }
        : results,
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
