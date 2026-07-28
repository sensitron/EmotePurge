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
  },
  webServer: {
    command: 'npx ng serve --port 4300 --proxy-config proxy.conf.json',
    url: 'http://localhost:4300',
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
