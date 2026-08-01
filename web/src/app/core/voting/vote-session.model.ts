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
  // Size of the session's explicit ballot; null = dynamic "all emotes" session.
  emoteCount: number | null;
}

export interface VoteSessionResult {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  // Manager-only context: null = withheld (or no longer computed for archived ballot members).
  // Data presence doubles as the permission signal — no separate canSeeUsage lookup needed.
  totalUseCount: number | null;
  keepVotes: number;
  deleteVotes: number;
  // Net keep − delete; chat usage is deliberately not part of the score anymore.
  score: number;
  // A subset-session member that left the 7TV set mid-session: still listed, voting closed.
  isArchived: boolean;
  myVote: VoteType | null;
}

export interface VoteSessionResults {
  sessionId: number;
  title: string;
  isActive: boolean;
  startedAt: string;
  endedAt: string | null;
  // Distinct voters across the session — the UI flags thin participation with this.
  voterCount: number;
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
