export interface AuthUser {
  twitchUserId: string;
  login: string;
  displayName: string;
  tokenExpiresAtUtc: string;
  isGlobalAdmin: boolean;
  /** Null for a session created before this claim existed — the avatar falls back to a monogram. */
  profileImageUrl: string | null;
}
