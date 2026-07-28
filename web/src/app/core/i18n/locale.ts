import { AppLang } from './language.service';

/** BCP-47 tags for `Intl`/`toLocaleDateString` — Angular's `LOCALE_ID`/`DatePipe` is bootstrap-time
 * static and can't react to a runtime language switch, so date formatting goes through these
 * instead wherever it must update live (see `datetime-picker.ts`, `vote-session-detail-page.ts`). */
const LOCALE_TAGS: Record<AppLang, string> = {
  de: 'de-DE',
  en: 'en-US',
};

export function toLocale(lang: AppLang): string {
  return LOCALE_TAGS[lang];
}
