import { HttpErrorResponse } from '@angular/common/http';

// Mirrors src/EmotePurge.Api/Validation/ApiErrorCodes.cs — the API returns only these stable,
// language-neutral codes (never prose), so translation happens exactly once, here.
const KNOWN_API_ERROR_CODES = new Set([
  'invalid_channel_name',
  'invalid_channel_or_session_id',
  'emote_ids_empty',
  'invalid_date_format',
  'from_after_to',
  'range_too_large',
  'invalid_oauth_state',
  'twitch_token_exchange_failed',
  'twitch_user_info_unavailable',
  'vote_session_title_empty',
  'vote_session_roles_empty',
  'vips_not_supported',
  'started_at_in_future',
  'channel_not_joined',
  'emote_id_empty',
  'invalid_vote_type',
  'channel_not_found',
  'vote_session_not_found',
  'vote_session_ended',
  'emote_not_eligible',
  'unexpected_error',
]);

/** Resolves an HTTP error from the EmotePurge API to a translation key — `errors.api.<code>` for a
 * recognized `errorCode` body, `errors.generic` otherwise (missing body, unrecognized code, or a
 * bare `Forbid()`/`Unauthorized()` with no body at all). */
export function apiErrorTranslationKey(error: HttpErrorResponse): string {
  const code = (error.error as { errorCode?: string } | null)?.errorCode;
  return code && KNOWN_API_ERROR_CODES.has(code) ? `errors.api.${code}` : 'errors.generic';
}
