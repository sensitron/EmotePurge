import { AbstractControl, ValidationErrors } from '@angular/forms';

/**
 * Lower-case only — deliberately, because everything is normalized *before* it is matched. Mirrors
 * `ChannelNameValidation.Pattern` in the Api, which does exactly the same (Regel 9: a client
 * validator has to reproduce the server's normalization, not just copy its regex; the overview form
 * tripped over that difference once by matching mixed case against the raw input instead).
 */
export const CHANNEL_NAME_PATTERN = /^[a-z0-9_]{4,25}$/;

/** The frontend twin of `ChannelName.Normalize` — trimmed and lower-cased, the form every channel
 *  is stored and looked up under. Twitch logins are commonly typed with capitals ("HandOfBlood"). */
export function normalizeChannelName(value: string): string {
  return value.trim().toLowerCase();
}

/** Reactive-forms validator over the *normalized* value, so "HandOfBlood " is valid input. */
export function channelNameValidator(control: AbstractControl<string>): ValidationErrors | null {
  return CHANNEL_NAME_PATTERN.test(normalizeChannelName(control.value ?? ''))
    ? null
    : { channelName: true };
}
