import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { apiErrorTranslationKey } from './api-error';

function errorWith(status: number, body: unknown = null): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('apiErrorTranslationKey', () => {
  it('prefers a recognized errorCode over the status', () => {
    expect(apiErrorTranslationKey(errorWith(409, { errorCode: 'vote_session_ended' }))).toBe(
      'errors.api.vote_session_ended',
    );
  });

  it('falls back to the status for an unrecognized errorCode', () => {
    expect(apiErrorTranslationKey(errorWith(404, { errorCode: 'something_new' }))).toBe(
      'errors.status.notFound',
    );
  });

  // The bodyless responses: all four authorization endpoint filters answer with a bare Forbid(),
  // Results.NotFound() without a body is used in three places, and the rate limiter sends nothing.
  it.each([
    [0, 'errors.status.offline'],
    [401, 'errors.status.unauthorized'],
    [403, 'errors.status.forbidden'],
    [404, 'errors.status.notFound'],
    [409, 'errors.status.conflict'],
    [429, 'errors.status.rateLimited'],
    [500, 'errors.status.server'],
    [503, 'errors.status.server'],
  ])('maps a bodyless %i to %s', (status, expected) => {
    expect(apiErrorTranslationKey(errorWith(status))).toBe(expected);
  });

  it('keeps errors.generic for statuses with no specific message', () => {
    expect(apiErrorTranslationKey(errorWith(418))).toBe('errors.generic');
  });
});
