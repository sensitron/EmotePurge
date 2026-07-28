import { expect, test } from '@playwright/test';

import { mockAuthMe, mockTwitchLoginRedirect, mockWorkerHealth } from './support/mocks';

test.describe('unauthenticated visitor', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockWorkerHealth(page);
    await mockTwitchLoginRedirect(page);
  });

  test('is redirected from the overview to /login and can trigger the Twitch login redirect', async ({ page }) => {
    await page.goto('/');

    await expect(page).toHaveURL(/\/login$/);
    const loginButton = page.getByRole('button', { name: 'Mit Twitch einloggen' });
    await expect(loginButton).toBeVisible();

    const loginRequest = page.waitForRequest('**/api/auth/twitch/login');
    await loginButton.click();
    await loginRequest;
  });

  test('an authGuard-protected deep link stashes the return URL before redirecting to /login', async ({ page }) => {
    await page.goto('/my-votings');

    await expect(page).toHaveURL(/\/login$/);
    const returnUrl = await page.evaluate(() => sessionStorage.getItem('ep_return_url'));
    expect(returnUrl).toBe('/my-votings');
  });
});
