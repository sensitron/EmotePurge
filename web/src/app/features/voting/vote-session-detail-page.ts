import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Dialog } from '@angular/cdk/dialog';
import { ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';

import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { LIVE_EVENT_TYPES, LiveEvent, channelLiveUrl } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import {
  VoteSessionResult,
  VoteSessionResults,
  VoteType,
} from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import {
  EmoteDrilldownData,
  EmoteDrilldownDialog,
} from '../../shared/emotes/emote-drilldown-dialog';
import { CSV_MIME } from '../../shared/export/csv';
import { ExportChoice, ExportDialog, ExportDialogData } from '../../shared/export/export-dialog';
import { JSON_MIME } from '../../shared/export/export-envelope';
import { downloadFile } from '../../shared/export/file-download';
import {
  VotingExportInput,
  votingCsv,
  votingExportFilename,
  votingJson,
  withheldFields,
} from '../../shared/export/voting-export';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { VoteAudienceBadge } from '../../shared/voting/vote-audience-badge';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { UsageRangeMenu } from '../../shared/emotes/usage-range-menu';
import {
  ATLAS_GAP_PX,
  ATLAS_STICKY_TOP_PX,
  SIDECAR_GAP_PX,
  atlasColumns,
} from '../../shared/grid/atlas-grid';
import { chunkIntoRows } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

/**
 * The ballot cell, in two sizes.
 *
 * This is not the usage atlas, and the difference is the task: there the cell is a thing to mark in
 * bulk, here it carries two vote targets that have to stay real targets. So the cell does NOT
 * shrink to 64 px everywhere — below roughly 600 px of room it grows instead, because the people
 * voting are viewers and viewers are on phones. At 96 px the emote is more than twice the size it
 * had on the old card (40 px), the vote buttons keep their 44 px height, and three fit across a
 * phone where two cards did.
 *
 * Above that width the pointer is precise and the reader is usually a moderator working through the
 * result, so the dense 64 px cell takes over.
 *
 * The threshold is measured against the CONTAINER, not the viewport — the sheet sits next to a
 * sidecar from `lg` up, so the window's width says nothing about the room the cells actually have.
 */
const CELL_WIDE_PX = 64;
const CELL_NARROW_PX = 96;
const STRIP_WIDE_PX = 24;
const STRIP_NARROW_PX = 44;
const NARROW_BELOW_PX = 600;

/** Ratio bar under the vote strip, plus the gutter between rows. */
const RATIO_BAR_PX = 2;

// Votes from other people arrive one by one. Half a second is short enough to feel live and long
// enough that a moderator clicking through ten emotes produces one refetch, not ten. The same
// window also collapses a `usage.flushed` that lands next to a vote into a single refetch.
const VOTE_RELOAD_DEBOUNCE_MS = 500;

// Below this many distinct voters the results view carries a "thin participation" notice — a
// handful of votes reads like a community verdict once it's sorted and scored, and it isn't one.
const LOW_PARTICIPATION_THRESHOLD = 5;

// A curated ballot of up to two grid rows is scanned faster than any filter is typed — the
// filter toolbar only earns its sticky row above that.
const FILTER_TOOLBAR_MIN_EMOTES = 13;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [
    BackLink,
    Button,
    EmptyState,
    NoticeBanner,
    VoteAudienceBadge,
    ScrollingModule,
    NgOptimizedImage,
    MassDeletePanel,
    UsageRangeMenu,
    TranslocoPipe,
  ],
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
  private readonly dialog = inject(Dialog);

  // Lazy on purpose — reading the required channelName input during construction would throw
  // NG0950; the computed is first evaluated inside liveReload's toObservable effect.
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));

  protected readonly voteType = VoteType;
  protected readonly currentUser = this.authService.currentUser;

  // Measured, not derived from the window — see the note at CELL_WIDE_PX, and the same defect
  // fixed on the usage atlas: the shell caps content at 1024 px while window.innerWidth kept
  // counting to 2560, so a wide monitor got eight stretched cards in 992 px.
  private readonly sheetRef = viewChild.required<ElementRef<HTMLElement>>('sheet');
  private readonly stickyBarRef = viewChild<ElementRef<HTMLElement>>('stickyBar');
  protected readonly sheetWidth = signal(0);

  protected readonly cellPx = computed(() =>
    this.sheetWidth() > 0 && this.sheetWidth() < NARROW_BELOW_PX ? CELL_NARROW_PX : CELL_WIDE_PX,
  );
  protected readonly stripPx = computed(() =>
    this.cellPx() === CELL_NARROW_PX ? STRIP_NARROW_PX : STRIP_WIDE_PX,
  );
  /** Enough room for the thumb icon beside the tally; below it the number carries the button. */
  protected readonly showVoteIcons = computed(() => this.stripPx() >= STRIP_NARROW_PX);
  protected readonly rowHeight = computed(
    () => this.cellPx() + this.stripPx() + RATIO_BAR_PX + ATLAS_GAP_PX,
  );
  protected readonly columns = computed(() => atlasColumns(this.sheetWidth(), this.cellPx()));
  protected readonly sidecarTop = signal(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);

  protected readonly results = signal<VoteSessionResults | null>(null);
  protected readonly skeletonCells = Array.from({ length: 10 }, (_, i) => i);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);

  // The one place on this page that asks for the permission instead of inferring it from the data,
  // and it has to: hasUsageData() below reads null-only rows as "not a manager", which is also what
  // a fully archived subset ballot looks like — a manager would then lose the end button on exactly
  // the session most likely to need ending. Same resource the list page uses for its own end button.
  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });

  protected readonly canManage = computed(
    () => this.permissionsResource.value()?.canManage ?? false,
  );

  // The server reports TotalUseCount as null to everyone CanManageChannelAsync rejects, so data
  // presence *is* the permission verdict — no separate GET /permissions round-trip needed. (An
  // all-archived subset ballot also yields null-only rows; hiding the usage UI is right there too,
  // since no usage is being computed for it.)
  protected readonly hasUsageData = computed(() =>
    (this.results()?.emotes ?? []).some((emote) => emote.totalUseCount !== null),
  );

  // Card selection exists solely to feed the mass-delete panel, so voters without delete power get
  // plain, non-interactive cards — a selection they can build but never act on is dead UI. The
  // usage verdict doubles as the gate (same CanManageChannelAsync behind both). Known trade-off: a
  // 7TV editor who is not also a channel manager gets no usage data either and loses the delete
  // entry point on this page — the usage-stats grid keeps it for them.
  protected readonly canSelectForDelete = this.hasUsageData;

  // Same data-presence-as-permission reading as hasUsageData: the server nulls the tallies of a
  // running secret-ballot session for everyone it does not consider a manager.
  protected readonly talliesWithheld = computed(() =>
    (this.results()?.emotes ?? []).some((emote) => emote.keepVotes === null),
  );

  // The mirror image: the session is a secret ballot and still running, but this viewer sees
  // through it. Worth saying out loud — otherwise a manager has no way to tell that the numbers on
  // their screen are not the numbers the voters are looking at.
  protected readonly showManagerHiddenNotice = computed(() => {
    const results = this.results();
    return !!results && results.hideResultsUntilEnd && results.isActive && !this.talliesWithheld();
  });

  protected readonly voterCount = computed(() => this.results()?.voterCount ?? 0);
  protected readonly showLowParticipation = computed(
    () => this.voterCount() > 0 && this.voterCount() < LOW_PARTICIPATION_THRESHOLD,
  );
  // The caveat stays visible after the session ends — that is where it matters most, since the
  // final numbers are what the mass-delete acts on. Only the wording switches: "bisher"/"so far"
  // promises votes that may still come in, which is no longer true once voting is closed.
  protected readonly lowParticipationKey = computed(() =>
    pluralKey(
      this.voterCount(),
      this.results()?.isActive
        ? 'voting.detail.lowParticipation'
        : 'voting.detail.lowParticipationEnded',
    ),
  );

  protected readonly showFilterToolbar = computed(
    () => this.orderedEmotes().length >= FILTER_TOOLBAR_MIN_EMOTES,
  );

  // Freezes the card order (by emote id) across post-vote reloads, since the backend sorts by
  // score — without this, voting an emote's score down to the bottom instantly yanks its card to
  // the end of the list while the user is still looking at it. On a secret ballot the server sends
  // name order instead (score order would leak the ranking), and freezing that is equally right:
  // the reveal then happens through the numbers appearing, not through the cards jumping.
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

  /**
   * The emote the readout is describing, held by id rather than by object: every vote reloads the
   * results, and an object reference would pin the panel to a stale copy. Falls back to the first
   * row so the panel is never empty.
   */
  private readonly inspectedId = signal<string | null>(null);
  protected readonly inspected = computed(() => {
    const list = this.emotes();
    const id = this.inspectedId();
    return (id ? list.find((emote) => emote.emoteId === id) : undefined) ?? list[0] ?? null;
  });

  constructor() {
    // Deferred, not called directly — see the identical comment in VoteSessionListPage.
    effect(() => this.load());

    effect((onCleanup) => {
      const element = this.sheetRef().nativeElement;
      this.sheetWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.sheetWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // The toolbar only exists above FILTER_TOOLBAR_MIN_EMOTES, so this one is optional — without it
    // the sidecar pins directly under the workspace tabs, which is exactly right.
    effect((onCleanup) => {
      const element = this.stickyBarRef()?.nativeElement;
      if (!element) {
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);
        return;
      }
      const measure = () =>
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + element.offsetHeight + SIDECAR_GAP_PX);
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // Live tally *and* live usage, off the one channel stream this page already holds open.
    // Results reload on every relevant event; the channel-status side-load only when a
    // `channel.synced` was among them — that value changes on a 7TV sync, not on a vote or a
    // flush, and refetching it per event would triple the request volume.
    // No echo suppression — one's own vote already reloads through vote(), and the debounce merges
    // that with the push it caused; loadResults is idempotent either way.
    liveReload(this.liveUrl, {
      accept: (event) => this.isRelevantLiveEvent(event),
      debounceMs: VOTE_RELOAD_DEBOUNCE_MS,
    }).subscribe((seen) => {
      this.loadResults({ freeze: false });
      if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
        this.loadActiveEmoteSetId();
      }
    });
  }

  // One guarded entry point for click/Enter/Space on the sprite. preventDefault only fires on the
  // keyboard path and only when the sprite is selectable — an unselectable sprite must keep Space's
  // default page scroll. Also pins the readout, so a tap on a touch screen (where nothing hovers)
  // still tells the voter which emote they are looking at.
  protected onCardActivate(emote: VoteSessionResult, event: MouseEvent | KeyboardEvent): void {
    this.inspectedId.set(emote.emoteId);
    if (!this.canSelectForDelete()) {
      return;
    }
    if (event.type === 'keydown') {
      event.preventDefault();
    }
    this.selection.onRowClick(emote, event as MouseEvent);
  }

  protected inspect(emote: VoteSessionResult): void {
    this.inspectedId.set(emote.emoteId);
  }

  /** Share of the keep votes in the ratio bar under the strip; null while a tally is withheld. */
  protected keepShare(emote: VoteSessionResult): number | null {
    if (emote.keepVotes === null || emote.deleteVotes === null) {
      return null;
    }
    const total = emote.keepVotes + emote.deleteVotes;
    return total === 0 ? null : (emote.keepVotes / total) * 100;
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
  // Signed on purpose: the score is a net vote balance now, and a bare "2" hides whether the
  // community is two votes for or two votes against.
  protected formatScore(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang()), {
      maximumFractionDigits: 0,
      signDisplay: 'exceptZero',
    }).format(value);
  }

  // Full, untruncated stats wording as a tooltip — the visible lines may still ellipsize on narrow
  // cards. Omits whichever half the server withheld, so the tooltip never states a number the card
  // deliberately doesn't have.
  protected statsTitle(emote: VoteSessionResult): string {
    const score =
      emote.score === null
        ? this.translocoService.translate('voting.detail.resultsHiddenShort')
        : `${this.translocoService.translate('voting.detail.scoreLabel')} ${this.formatScore(emote.score)}`;
    if (emote.totalUseCount === null) {
      return score;
    }
    const usage = this.translocoService.translate('usageStats.usageLabel');
    return `${emote.totalUseCount}x ${usage} · ${score}`;
  }

  protected keepButtonTitle(emote: VoteSessionResult): string {
    return this.voteButtonTitle(
      emote,
      emote.myVote === VoteType.Keep ? 'voting.detail.retractVote' : 'voting.detail.keepAriaLabel',
      emote.keepVotes,
    );
  }

  protected deleteButtonTitle(emote: VoteSessionResult): string {
    return this.voteButtonTitle(
      emote,
      emote.myVote === VoteType.Delete
        ? 'voting.detail.retractVote'
        : 'voting.detail.deleteAriaLabel',
      emote.deleteVotes,
    );
  }

  protected refresh(): void {
    this.load();
  }

  // Opened from the card's info icon; only rendered for viewers with usage access (hasUsageData),
  // since /usage-stats/daily sits behind the usage-stats authorization filter. The range is the
  // session's own usage window; the vote block carries the card's tallies — null inside stays
  // "withheld" and the dialog renders nothing for it.
  protected openDrilldown(emote: VoteSessionResult): void {
    const results = this.results();
    if (!results) {
      return;
    }
    const data: EmoteDrilldownData = {
      channelName: this.channelName(),
      from: results.startedAt.slice(0, 10),
      to: (results.endedAt ?? new Date().toISOString()).slice(0, 10),
      emoteId: emote.emoteId,
      emoteName: emote.emoteName,
      imageUrl: emote.imageUrl,
      vote: {
        keepVotes: emote.keepVotes,
        deleteVotes: emote.deleteVotes,
        score: emote.score,
        myVote: emote.myVote,
      },
    };
    this.dialog.open<void>(EmoteDrilldownDialog, {
      data,
      backdropClass: 'app-dialog-backdrop',
      panelClass: 'app-dialog-panel',
      ariaLabelledBy: 'emote-drilldown-title',
    });
  }

  // Exports the *visible* list (filtered + frozen order). Client-side serialization of the loaded
  // read model on purpose (A12): the `null`s in it are the server's visibility verdict, so the
  // withheld tally/usage columns drop out without this page re-implementing the secret-ballot rule.
  protected openExport(): void {
    const results = this.results();
    if (!results) {
      return;
    }
    const input: VotingExportInput = {
      channelName: this.channelName(),
      results,
      rows: this.emotes(),
    };
    const withheld = withheldFields(input.rows);
    const noticeKeys: string[] = [];
    if (withheld.includes('keepVotes')) {
      noticeKeys.push('export.withheldTallies');
    }
    if (withheld.includes('totalUseCount')) {
      noticeKeys.push('export.withheldUsage');
    }
    const data: ExportDialogData = {
      rowCount: input.rows.length,
      filtered: input.rows.length !== this.orderedEmotes().length,
      // This page has no grid selection to export — the ballot itself already is the subset.
      selectionCount: 0,
      noticeKeys,
    };
    this.dialog
      .open<ExportChoice | undefined>(ExportDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        ariaLabelledBy: 'export-dialog-title',
      })
      .closed.subscribe((choice) => {
        if (choice?.format === 'csv') {
          downloadFile(votingExportFilename(input, 'csv'), votingCsv(input), CSV_MIME);
        } else if (choice?.format === 'json') {
          downloadFile(votingExportFilename(input, 'json'), votingJson(input), JSON_MIME);
        }
      });
  }

  /**
   * Confirmed, unlike the same action in the list: here the button sits right next to "refresh" in
   * the header, and ending a session cannot be undone. Names the session for the same reason the
   * delete dialog does — the title is the only thing distinguishing two sessions of one channel.
   */
  protected endSession(title: string): void {
    const data: ConfirmDialogData = {
      message: this.translocoService.translate('voting.detail.endConfirm', { title }),
      confirmLabel: this.translocoService.translate('voting.list.end'),
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
        this.errorMessage.set(null);
        this.voteSessionService.end(this.channelName(), Number(this.sessionId())).subscribe({
          // Full reload, not a local patch of isActive: the endpoint answers with a summary while
          // this page holds results, and ending a secret ballot unseals every tally at once — the
          // page after the click shows materially more than the page before it.
          next: () => this.load({ freeze: false }),
          error: (error: HttpErrorResponse) => this.errorMessage.set(apiErrorTranslationKey(error)),
        });
      });
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

  /**
   * `vote.changed` is per session and must match this one. `usage.flushed` is channel-scoped and
   * carries no session id — the stream is already this channel's, so its arrival alone is the
   * signal. Usage no longer feeds the score, but the flush event stays subscribed: managers see
   * the raw usage figures as context on every card (and filter on them), and chat usage only
   * moves on the worker's 30 s batch flush — without the refetch those numbers would sit at
   * whatever the first load happened to catch.
   *
   * `channel.synced` means the emote inventory itself moved (mass delete, 7TV add/remove, set
   * swap) — the ballot and the archived badges are stale, and so is the set id the mass-delete
   * panel binds to. The server only fires it when a sync actually changed something, so this does
   * not reintroduce the reload-on-a-timer the side-load comment in the constructor warns about.
   */
  private isRelevantLiveEvent(event: LiveEvent): boolean {
    if (
      event.type === LIVE_EVENT_TYPES.usageFlushed ||
      event.type === LIVE_EVENT_TYPES.channelSynced
    ) {
      return true;
    }
    return (
      event.type === LIVE_EVENT_TYPES.voteChanged && event.sessionId === Number(this.sessionId())
    );
  }

  // The tally in parentheses is dropped rather than shown as "(0)" or "(null)" when the server
  // withheld it — the tooltip is the one place a hidden number could still slip out.
  private voteButtonTitle(
    emote: VoteSessionResult,
    labelKey: string,
    tally: number | null,
  ): string {
    if (emote.isArchived) {
      return this.translocoService.translate('voting.detail.archivedVoteDisabled');
    }
    const label = this.translocoService.translate(labelKey);
    return tally === null ? label : `${label} (${tally})`;
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

  /** Split out of load() so the live-update path can refetch the tally alone — this value only
   *  changes when the 7TV set is resynced, never as a result of a vote. */
  private loadActiveEmoteSetId(): void {
    this.channelService.getStatus(this.channelName()).subscribe({
      next: (status) => this.activeEmoteSetId.set(status.activeEmoteSetId),
      error: () => this.activeEmoteSetId.set(null),
    });
  }
}
