import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

import { ChannelService } from '../channels/channel.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

/** Same shape as the delete's REMOVE, with ADD and the alias to restore under. `name` restores the
 *  chat alias the emote had at delete time — without it 7TV falls back to the emote's default name,
 *  which for renamed emotes would not be the one the chat knows. */
const ADD_EMOTE_MUTATION = `
  mutation AddEmote($setId: ObjectID!, $emoteId: ObjectID!, $name: String) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: ADD, name: $name) {
        id
      }
    }
  }
`;

const ADD_OPERATION: RunOperation = {
  label: 'restore',
  buildRequest: (setId, emote) => ({
    query: ADD_EMOTE_MUTATION,
    variables: { setId, emoteId: emote.sevenTvEmoteId, name: emote.name },
  }),
};

/** Outcome of the closing resync trigger. 'cooldown' is not a failure: the per-channel cooldown
 *  (429) means a sync just ran or is about to — the periodic worker heals the view within its
 *  60s tick either way. */
export type ResyncTriggerState = 'idle' | 'pending' | 'succeeded' | 'cooldown' | 'failed';

/**
 * The restore half of A6: re-adds emotes to the 7TV set, in the browser, over the same run engine
 * (pacing, backoff, token) as the delete — ADD draws tickets from the same `emote_set_change`
 * bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * No `sync-restored` endpoint on purpose: a restored emote briefly still showing as archived is
 * the conservative direction (unlike the delete case, where stale rows would be offered as delete
 * candidates), and `UpsertEmote` heals it — including `ArchivedAt = null` — within the periodic
 * resync. The closing A8 resync trigger only shortens that window.
 */
@Injectable({ providedIn: 'root' })
export class SevenTvRestoreService {
  private readonly channelService = inject(ChannelService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  private currentChannelName: string | null = null;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

  startRestore(setId: string, channelName: string, emotes: RunQueueEmote[]): void {
    const started = this.engine.start(setId, emotes, ADD_OPERATION, (result) =>
      this.onRunComplete(channelName, result),
    );
    if (!started) {
      return;
    }
    this.currentChannelName = channelName;
    this.resyncTrigger.set('idle');
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.resyncTrigger.set('idle');
    this.currentChannelName = null;
  }

  /** Same page-follows-user reasoning as the delete service's counterpart. */
  resetIfChannelChanged(channelName: string): void {
    if (
      this.isRunning() ||
      this.currentChannelName === null ||
      this.currentChannelName === channelName
    ) {
      return;
    }
    this.reset();
  }

  private onRunComplete(channelName: string, result: RunResult): void {
    if (result.doneIds.length === 0) {
      return;
    }
    this.resyncTrigger.set('pending');
    this.channelService.resync(channelName).subscribe({
      next: () => this.resyncTrigger.set('succeeded'),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
    });
  }
}
