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
  // Secret ballot: while the session runs, only managers see the tallies. Describes the session,
  // not the viewer's rights — reported to everyone so the list can badge it.
  hideResultsUntilEnd: boolean;
}

// Body of POST /api/channels/{name}/vote-sessions. An object rather than positional arguments:
// three of the five fields are optional, and "everything else default, but hidden results" was
// unreadable as create(ch, title, roles, undefined, undefined, true).
export interface CreateVoteSessionRequest {
  title: string;
  allowedVoterRoles: AllowedRoles;
  // Omitted = count chat usage from now.
  startedAt?: string;
  // Omitted = the session covers all non-archived channel emotes dynamically; a non-empty list
  // becomes the session's fixed ballot (local emote ids from the results/usage models).
  emoteIds?: string[];
  // Omitted/false = tallies visible to voters throughout, the behaviour every session had before.
  hideResultsUntilEnd?: boolean;
}

export interface VoteSessionResult {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  // Manager-only context: null = withheld (or no longer computed for archived ballot members).
  // Data presence doubles as the permission signal — no separate canSeeUsage lookup needed.
  totalUseCount: number | null;
  // null together = withheld: a running secret-ballot session shows no tallies to non-managers.
  // Same "null = withheld, not zero" convention as totalUseCount, and again the data presence is
  // the permission signal. myVote is never withheld — the voter always sees their own ballot.
  keepVotes: number | null;
  deleteVotes: number | null;
  // Net keep − delete; chat usage is deliberately not part of the score anymore.
  score: number | null;
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
  // Distinct voters across the session — the UI flags thin participation with this. Stays visible
  // on a secret ballot: turnout says nothing about which way the votes went.
  voterCount: number;
  // The session's setting, reported even once it no longer withholds anything (ended session).
  hideResultsUntilEnd: boolean;
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
