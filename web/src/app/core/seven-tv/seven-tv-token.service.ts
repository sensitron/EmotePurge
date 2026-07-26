import { Injectable, signal } from '@angular/core';

// sessionStorage, never localStorage — per Grundsatz 4 (Zero-Knowledge für Schreib-Tokens) in
// Architectur.md: the token stays in this browser tab only, cleared on tab close, and is never
// sent to our own backend, only ever attached to the direct 7tv.io GraphQL call.
const TOKEN_STORAGE_KEY = 'ep_7tv_write_token';

@Injectable({ providedIn: 'root' })
export class SevenTvTokenService {
  readonly hasToken = signal(sessionStorage.getItem(TOKEN_STORAGE_KEY) !== null);

  getToken(): string | null {
    return sessionStorage.getItem(TOKEN_STORAGE_KEY);
  }

  setToken(token: string): void {
    sessionStorage.setItem(TOKEN_STORAGE_KEY, token);
    this.hasToken.set(true);
  }

  clearToken(): void {
    sessionStorage.removeItem(TOKEN_STORAGE_KEY);
    this.hasToken.set(false);
  }
}
