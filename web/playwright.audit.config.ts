import { defineConfig } from '@playwright/test';

// UI/UX-audit harness — NOT part of the regular e2e suite (testMatch *.audit.ts deliberately
// misses the main config's *.spec.ts pattern and vice versa). Run on demand for before/after
// screenshots and overflow/touch-target metrics: npx playwright test --config=playwright.audit.config.ts
// Output lands in web/.audit-out/ (gitignored).
export default defineConfig({
  testDir: './e2e/audit',
  testMatch: '**/*.audit.ts',
  fullyParallel: true,
  workers: 4,
  timeout: 60_000,
  reporter: 'list',
  outputDir: './.audit-out/test-results',
  use: {
    baseURL: 'http://localhost:4300',
  },
  webServer: {
    command: 'npx ng serve --port 4300 --proxy-config proxy.conf.json',
    url: 'http://localhost:4300',
    reuseExistingServer: true,
    timeout: 180_000,
  },
  projects: [
    { name: 'de', use: { locale: 'de-DE' } },
    { name: 'en', use: { locale: 'en-US' } },
  ],
});
