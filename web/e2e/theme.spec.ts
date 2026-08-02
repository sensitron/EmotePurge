import { expect, test } from '@playwright/test';

import { AUTH_USER, mockAuthMe, mockMyChannels, mockWorkerHealth } from './support/mocks';

/**
 * The theme is the one piece of UI state that has to be correct *before* Angular exists — hence the
 * FOUC test below, which is the only one here that would still catch a regression if all the others
 * were deleted. The rest of the e2e suite keeps running in a single mode: those specs assert
 * behaviour, and behaviour does not depend on the palette.
 */
test.describe('theme', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockWorkerHealth(page);
  });

  test('follows the system preference when nothing has been chosen', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto('/welcome');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  });

  test('a system change reaches the open page while the preference is system', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    // No reload: matchMedia's change listener in ThemeService is what has to fire here.
    await page.emulateMedia({ colorScheme: 'light' });

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

  test('an explicit choice survives a reload and outranks the system preference', async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');
    await page.evaluate(() => localStorage.setItem('emotepurge.theme', 'light'));

    await page.reload();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    // And it stays put when the system moves underneath it.
    await page.emulateMedia({ colorScheme: 'dark' });
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

  test('no flash of the wrong theme: the attribute is set before Angular boots', async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.addInitScript(() => {
      // Runs before any page script. A MutationObserver would miss an attribute that is already
      // there by the time it starts, so record the state at the first opportunity Angular could
      // possibly have had — the moment <app-root> appears in the DOM.
      const record = () => {
        (window as unknown as Record<string, unknown>)['__themeAtBoot'] =
          document.documentElement.dataset['theme'] ?? null;
      };
      document.addEventListener('DOMContentLoaded', record, { once: true });
    });

    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    // Set by public/theme-init.js, which is a render-blocking <script src> in <head>. If it ever
    // regressed to running after bootstrap — or got blocked by the Api's script-src CSP — this
    // would be null and the user would see the dark page flash before the light one.
    const themeAtBoot = await page.evaluate(
      () => (window as unknown as Record<string, unknown>)['__themeAtBoot'],
    );
    expect(themeAtBoot).toBe('light');
  });

  test('the header menu switches the mode and persists the choice', async ({ page }) => {
    // The switcher lives in the app shell, and /welcome and /login both render outside it — so
    // this one needs a signed-in session to have a header at all.
    await mockAuthMe(page, AUTH_USER);
    await mockMyChannels(page, []);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    await page.getByRole('button', { name: 'Darstellung wählen' }).click();
    await page.getByRole('menuitemradio', { name: 'Hell' }).click();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const stored = await page.evaluate(() => localStorage.getItem('emotepurge.theme'));
    expect(stored).toBe('light');
  });
});
