import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, Subscription, catchError, concatMap, delay, from, map, of, tap } from 'rxjs';

import { EmoteAdminService } from '../emotes/emote-admin.service';
import { SevenTvTokenService } from './seven-tv-token.service';

const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v3/gql';
// Sequential, not parallel — per Architectur.md Modul D spec (250-300ms delay between requests).
const DELETE_DELAY_MS = 275;

const REMOVE_EMOTE_MUTATION = `
  mutation RemoveEmote($setId: ObjectID!, $emoteId: ObjectID!) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: REMOVE) {
        id
      }
    }
  }
`;

export interface DeleteQueueEmote {
  emoteId: string; // internal id — used for sync-deleted and for the host page's optimistic removal.
  sevenTvEmoteId: string;
  name: string;
}

export type DeleteItemStatus = 'pending' | 'in-progress' | 'done' | 'failed' | 'cancelled';

export interface DeleteQueueItem extends DeleteQueueEmote {
  status: DeleteItemStatus;
  errorMessage?: string;
}

interface DeleteOneResult {
  success: boolean;
  errorMessage?: string;
}

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly http = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly translocoService = inject(TranslocoService);

  private runSubscription: Subscription | null = null;
  private currentChannelName: string | null = null;

  readonly queue = signal<DeleteQueueItem[]>([]);
  readonly isRunning = signal(false);
  readonly progress = computed(() => {
    const items = this.queue();
    const finished = items.filter((item) => item.status === 'done' || item.status === 'failed').length;
    return { finished, total: items.length };
  });

  startDelete(setId: string, channelName: string, emotes: DeleteQueueEmote[]): void {
    if (this.isRunning() || emotes.length === 0) {
      return;
    }

    const token = this.tokenService.getToken();
    if (!token) {
      return;
    }

    this.currentChannelName = channelName;
    this.queue.set(emotes.map((emote) => ({ ...emote, status: 'pending' as DeleteItemStatus })));
    this.isRunning.set(true);

    this.runSubscription = from(emotes)
      .pipe(
        concatMap((emote) => {
          this.setStatus(emote.emoteId, 'in-progress');
          return this.deleteOne(setId, emote.sevenTvEmoteId, token).pipe(
            tap((result) => this.setStatus(emote.emoteId, result.success ? 'done' : 'failed', result.errorMessage)),
            delay(DELETE_DELAY_MS),
          );
        }),
      )
      .subscribe({
        complete: () => this.finish(),
        error: () => this.finish(),
      });
  }

  /** Cancel = unsubscribing the RxJS chain — idiomatic and simpler than hand-rolled cooperative
   *  cancellation. Items already 'done'/'failed' keep their outcome; the rest become 'cancelled'. */
  cancel(): void {
    this.runSubscription?.unsubscribe();
    this.runSubscription = null;
    this.queue.update((items) =>
      items.map((item) => (item.status === 'pending' || item.status === 'in-progress' ? { ...item, status: 'cancelled' } : item)),
    );
    this.finish();
  }

  /** Clears the panel after the admin has acknowledged a finished/cancelled run. */
  reset(): void {
    this.queue.set([]);
  }

  private deleteOne(setId: string, sevenTvEmoteId: string, token: string): Observable<DeleteOneResult> {
    return this.http
      .post<{ errors?: { message?: string }[] }>(
        SEVEN_TV_GQL_ENDPOINT,
        { query: REMOVE_EMOTE_MUTATION, variables: { setId, emoteId: sevenTvEmoteId } },
        { headers: { Authorization: `Bearer ${token}` } },
      )
      .pipe(
        map((response) => {
          const gqlErrorMessage = response?.errors?.[0]?.message;
          return gqlErrorMessage ? { success: false, errorMessage: gqlErrorMessage } : { success: true };
        }),
        catchError((error: HttpErrorResponse) => of({ success: false, errorMessage: this.describeHttpError(error) })),
      );
  }

  private describeHttpError(error: HttpErrorResponse): string {
    if (error.status === 401 || error.status === 403) {
      // Invalid/expired token — drop it so the UI falls back to the token-input prompt instead
      // of silently reusing the same bad token on the next delete attempt.
      this.tokenService.clearToken();
      return this.translocoService.translate('massDelete.errors.tokenInvalid');
    }
    if (error.status === 429) {
      return this.translocoService.translate('massDelete.errors.rateLimited');
    }
    if (error.status === 0) {
      return this.translocoService.translate('massDelete.errors.networkError');
    }
    return this.translocoService.translate('massDelete.errors.genericStatus', { status: error.status });
  }

  private setStatus(emoteId: string, status: DeleteItemStatus, errorMessage?: string): void {
    this.queue.update((items) =>
      items.map((item) => (item.emoteId === emoteId ? { ...item, status, errorMessage } : item)),
    );
  }

  private finish(): void {
    this.isRunning.set(false);
    this.runSubscription = null;

    const channelName = this.currentChannelName;
    const doneIds = this.queue()
      .filter((item) => item.status === 'done')
      .map((item) => item.emoteId);

    if (channelName && doneIds.length > 0) {
      this.emoteAdminService.syncDeleted(channelName, doneIds).subscribe();
    }
  }
}
