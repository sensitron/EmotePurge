import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AllowedRoles,
  CastVoteResult,
  VoteSessionResults,
  VoteSessionSummary,
  VoteType,
} from './vote-session.model';

@Injectable({ providedIn: 'root' })
export class VoteSessionService {
  private readonly http = inject(HttpClient);

  list(channelName: string): Observable<VoteSessionSummary[]> {
    return this.http.get<VoteSessionSummary[]>(`/api/channels/${channelName}/vote-sessions`);
  }

  create(channelName: string, title: string, allowedVoterRoles: AllowedRoles): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(`/api/channels/${channelName}/vote-sessions`, {
      title,
      allowedVoterRoles,
    });
  }

  end(channelName: string, sessionId: number): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(`/api/channels/${channelName}/vote-sessions/${sessionId}/end`, {});
  }

  // Deliberately reachable without auth — anonymous share-link visitors need to see results too.
  getResults(channelName: string, sessionId: number): Observable<VoteSessionResults> {
    return this.http.get<VoteSessionResults>(`/api/channels/${channelName}/vote-sessions/${sessionId}/results`);
  }

  castVote(channelName: string, sessionId: number, emoteId: string, type: VoteType): Observable<CastVoteResult> {
    return this.http.post<CastVoteResult>(`/api/channels/${channelName}/vote-sessions/${sessionId}/votes`, {
      emoteId,
      type,
    });
  }
}
