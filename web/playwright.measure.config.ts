import { defineConfig, devices } from '@playwright/test';

// Config for the measurement harnesses (`e2e/*.measure.ts`), kept separate from
// `playwright.config.ts` so `npm run e2e` never runs them: they hit the real 7TV CDN, take minutes
// across the repetitions a usable result needs, and assert nothing.
//
//   MEASURE_EMOTES=/tmp/emotes.json MEASURE_LABEL=before MEASURE_PAUSE=1200 \
//     npx playwright test --config playwright.measure.config.ts
//
// Port 4301, so a measurement never collides with a suite run on 4300. See
// `docs/Untersuchung-Emote-Bildladen-2026-08-29.md` for what these numbers mean and how badly a
// single run misleads.
export default defineConfig({
  testDir: './e2e',
  testMatch: /\.measure\.ts$/,
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  timeout: 300_000,
  use: {
    baseURL: 'http://localhost:4301',
    locale: 'de-DE',
  },
  webServer: {
    command: 'npx ng serve --port 4301 --proxy-config proxy.conf.json',
    url: 'http://localhost:4301',
    reuseExistingServer: true,
    timeout: 180_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
