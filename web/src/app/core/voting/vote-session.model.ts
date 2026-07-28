// Matches EmotePurge.Core.Entities.AllowedRoles ([Flags], numeric — no JsonStringEnumConverter).
export enum AllowedRoles {
  Everyone = 1,
  Subs = 2,
  VIPs = 4,
  Mods = 8,
  Broadcaster = 16,
}

// Matches EmotePurge.Core.Entities.VoteType (numeric).
export enum VoteType {
  Keep = 1,
  Delete = 2,
}

export interface VoteSessionSummary {
  id: number;
  title: string;
  allowedVoterRoles: number;
  isActive: boolean;
  startedAt: string;
  endedAt: string | null;
}

export interface VoteSessionResult {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  totalUseCount: number;
  normalizedUsageScore: number;
  keepVotes: number;
  deleteVotes: number;
  score: number;
  myVote: VoteType | null;
}

export interface VoteSessionResults {
  sessionId: number;
  title: string;
  isActive: boolean;
  startedAt: string;
  endedAt: string | null;
  emotes: VoteSessionResult[];
}

export interface CastVoteResult {
  voteId: number;
  emoteId: string;
  type: VoteType;
  updatedAt: string;
}

// A session the current user has ever cast a vote in, across any channel — see
// GET /api/vote-sessions/mine.
export interface MyVoteSession {
  sessionId: number;
  title: string;
  channelName: string;
  isActive: boolean;
  startedAt: string;
  endedAt: string | null;
  lastVotedAt: string;
}
