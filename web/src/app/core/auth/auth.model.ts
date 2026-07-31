export interface AuthUser {
  twitchUserId: string;
  login: string;
  displayName: string;
  tokenExpiresAtUtc: string;
  isGlobalAdmin: boolean;
}
