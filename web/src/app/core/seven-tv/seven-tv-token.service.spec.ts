import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { SevenTvTokenService } from './seven-tv-token.service';

describe('SevenTvTokenService', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('hasToken starts false when nothing is stored', () => {
    const service = TestBed.inject(SevenTvTokenService);
    expect(service.hasToken()).toBe(false);
    expect(service.getToken()).toBeNull();
  });

  it('hasToken picks up a token already present in sessionStorage at construction', () => {
    sessionStorage.setItem('ep_7tv_write_token', 'pre-existing');
    const service = TestBed.inject(SevenTvTokenService);
    expect(service.hasToken()).toBe(true);
    expect(service.getToken()).toBe('pre-existing');
  });

  it('setToken persists to sessionStorage and flips hasToken', () => {
    const service = TestBed.inject(SevenTvTokenService);

    service.setToken('abc123');

    expect(service.hasToken()).toBe(true);
    expect(service.getToken()).toBe('abc123');
    expect(sessionStorage.getItem('ep_7tv_write_token')).toBe('abc123');
  });

  it('clearToken removes it from sessionStorage and flips hasToken back', () => {
    const service = TestBed.inject(SevenTvTokenService);
    service.setToken('abc123');

    service.clearToken();

    expect(service.hasToken()).toBe(false);
    expect(service.getToken()).toBeNull();
    expect(sessionStorage.getItem('ep_7tv_write_token')).toBeNull();
  });
});
