import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { normalizeChannelName } from '../channels/channel-name';
import { ForeignEmoteSetResponse } from './foreign-emote-set.model';

/**
 * The one client for `GET /api/seventv/channels/{channelName}/emotes` (spec §4, T1's endpoint).
 *
 * Deliberately thin: the backend owns the 60 s cache, the coalescing and the breaker (spec §6, T2)
 * — this service only shapes the request. Error mapping is left to the caller via the existing
 * `apiErrorTranslationKey`, the same as every other feature service in `core/`: the four new error
 * codes this endpoint introduces (spec §5) are registered by T1 in `KNOWN_API_ERROR_CODES`, so
 * nothing here needs to know their literal strings.
 */
@Injectable({ providedIn: 'root' })
export class ForeignEmoteSetService {
  private readonly http = inject(HttpClient);

  /**
   * `refresh: true` bypasses the backend's 60 s cache (E3's "reload anyway" escape hatch) — still
   * subject to the same rate-limit policy and breaker on the server, this only changes the query
   * string.
   */
  load(
    channelName: string,
    options: { refresh?: boolean } = {},
  ): Observable<ForeignEmoteSetResponse> {
    const normalized = normalizeChannelName(channelName);
    const params = options.refresh ? new HttpParams().set('refresh', 'true') : undefined;
    return this.http.get<ForeignEmoteSetResponse>(`/api/seventv/channels/${normalized}/emotes`, {
      params,
    });
  }
}
