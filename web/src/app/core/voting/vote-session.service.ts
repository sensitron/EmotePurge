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

  create(
    channelName: string,
    title: string,
    allowedVoterRoles: AllowedRoles,
    startedAt?: string,
  ): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(`/api/channels/${channelName}/vote-sessions`, {
      title,
      allowedVoterRoles,
      ...(startedAt ? { startedAt } : {}),
    });
  }

  end(channelName: string, sessionId: number): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(`/api/channels/${channelName}/vote-sessions/${sessionId}/end`, {});
  }

  // Requires login + being part of the session's target audience (VoteAudienceFilter,
  // voteSessionAccessGuard) — anonymous viewing was removed, see CLAUDE.md decision log.
  getResults(channelName: string, sessionId: number): Observable<VoteSessionResults> {
    return this.http.get<VoteSessionResults>(`/api/channels/${channelName}/vote-sessions/${sessionId}/results`);
  }

  castVote(channelName: string, sessionId: number, emoteId: string, type: VoteType): Observable<CastVoteResult> {
    return this.http.post<CastVoteResult>(`/api/channels/${channelName}/vote-sessions/${sessionId}/votes`, {
      emoteId,
      type,
    });
  }

  // Returns the emote to the neutral (unvoted) state — clicking the same vote button again.
  retractVote(channelName: string, sessionId: number, emoteId: string): Observable<void> {
    return this.http.delete<void>(`/api/channels/${channelName}/vote-sessions/${sessionId}/votes/${emoteId}`);
  }
}
