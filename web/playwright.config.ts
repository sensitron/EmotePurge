import { defineConfig, devices } from '@playwright/test';

// All specs mock every `/api/**` call via page.route (see e2e/support/mocks.ts) — the dev server's
// proxy target (the .NET Api on :5151) never needs to be reachable, so these run standalone in CI
// without Postgres/Redis/a live Twitch OAuth app.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:4300',
    trace: 'on-first-retry',
    // Every spec asserts on hardcoded German copy. The app now auto-detects the browser language
    // (see LanguageService.resolveInitialLang) — without pinning this, a CI runner's default
    // Chromium locale (en-US) would render English text and break those assertions.
    locale: 'de-DE',
  },
  webServer: {
    command: 'npx ng serve --port 4300 --proxy-config proxy.conf.json',
    url: 'http://localhost:4300',
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      testIgnore: /touch-mobile\.e2e\.spec\.ts/,
    },
    // A second project rather than a per-test context: `pointer: coarse` follows from the device
    // descriptor's hasTouch/isMobile, and those are context-level options that a test cannot set for
    // itself. Only touch-mobile.e2e.spec.ts runs here — every other spec asserts desktop behaviour
    // and would have to be rewritten for a viewport it was never about.
    {
      name: 'mobile-chrome',
      use: { ...devices['Pixel 5'] },
      testMatch: /touch-mobile\.e2e\.spec\.ts/,
    },
  ],
});
