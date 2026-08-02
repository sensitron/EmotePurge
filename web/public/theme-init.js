/*
 * Anti-FOUC guard. Runs synchronously from <head>, long before Angular boots, and stamps the
 * resolved theme onto <html> so the very first paint already uses the right token block.
 *
 * An EXTERNAL file rather than an inline <script>, and that is not a style choice: the Api sends
 * `script-src 'self'` without 'unsafe-inline' on every response, including wwwroot and the SPA
 * fallback (src/EmotePurge.Api/Program.cs). An inline script would be blocked outright. A
 * 'sha256-…' entry in that header would work but breaks silently on any reformat of this file —
 * and only inside the container, never under `ng serve`, where no CSP applies. 'self' does not
 * have that failure mode.
 *
 * ThemeService reads the attribute this sets as its starting point instead of determining it a
 * second time. If this file ever fails to run, the CSS falls back to the dark token block, which
 * is what every user saw before the light mode existed.
 */
(function () {
  var stored = null;
  try {
    stored = localStorage.getItem('emotepurge.theme');
  } catch {
    // Storage can be unavailable (private mode, blocked cookies) — fall through to the system
    // preference, which is what an unset preference means anyway.
  }

  var theme =
    stored === 'light' || stored === 'dark'
      ? stored
      : window.matchMedia('(prefers-color-scheme: light)').matches
        ? 'light'
        : 'dark';

  document.documentElement.dataset.theme = theme;
})();
