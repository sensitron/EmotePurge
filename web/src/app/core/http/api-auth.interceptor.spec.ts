import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../auth/auth.service';
import { apiAuthInterceptor } from './api-auth.interceptor';

describe('apiAuthInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let handleSessionExpired: () => void;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiAuthInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);

    handleSessionExpired = vi.fn<() => void>();
    vi.spyOn(TestBed.inject(AuthService), 'handleSessionExpired').mockImplementation(
      handleSessionExpired,
    );
  });

  afterEach(() => {
    httpMock.verify();
  });

  function flush(url: string, status: number): void {
    httpMock.expectOne(url).flush(null, { status, statusText: 'x' });
  }

  it('resets the session on a 401 from an API call', () => {
    http.get('/api/channels/mine').subscribe({ next: () => undefined, error: () => undefined });
    flush('/api/channels/mine', 401);

    expect(handleSessionExpired).toHaveBeenCalledOnce();
  });

  it('re-throws the error so the caller can still render a message', () => {
    let status: number | undefined;
    http.get('/api/channels/mine').subscribe({ error: (error) => (status = error.status) });
    flush('/api/channels/mine', 401);

    expect(status).toBe(401);
  });

  it('leaves non-401 API errors alone', () => {
    http.get('/api/channels/foo/join').subscribe({ next: () => undefined, error: () => undefined });
    flush('/api/channels/foo/join', 403);

    expect(handleSessionExpired).not.toHaveBeenCalled();
  });

  // The whole point of the /api/ restriction: the 7TV write token is a different credential, and its
  // expiry must not sign the user out of EmotePurge.
  it('ignores a 401 from the 7TV endpoint', () => {
    http
      .post('https://7tv.io/v3/gql', {})
      .subscribe({ next: () => undefined, error: () => undefined });
    flush('https://7tv.io/v3/gql', 401);

    expect(handleSessionExpired).not.toHaveBeenCalled();
  });

  // Anonymous visitors 401 here on every page load; treating that as an expiry would bounce the
  // public landing page to /login.
  it('ignores the expected 401 from /api/auth/me', () => {
    http.get('/api/auth/me').subscribe({ next: () => undefined, error: () => undefined });
    flush('/api/auth/me', 401);

    expect(handleSessionExpired).not.toHaveBeenCalled();
  });

  it('ignores a 401 from /api/auth/logout', () => {
    http.post('/api/auth/logout', {}).subscribe({ next: () => undefined, error: () => undefined });
    flush('/api/auth/logout', 401);

    expect(handleSessionExpired).not.toHaveBeenCalled();
  });
});
