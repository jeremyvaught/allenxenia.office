import { test, expect } from '@playwright/test';

test('system theme follows OS, while an explicit choice survives reload', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByLabel('Theme', { exact: true })).toHaveValue('system');
  await page.getByLabel('Theme', { exact: true }).selectOption('light');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByLabel('Theme', { exact: true })).toHaveValue('light');
  await page.getByLabel('Theme', { exact: true }).selectOption('system');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.emulateMedia({ colorScheme: 'light' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  expect(await page.evaluate(() => localStorage.getItem('allen-office-theme'))).toBeNull();
});

test('public pages fit a 320px viewport and serve compiled CSS', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 800 });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  for (const route of ['/', '/Home/Privacy', '/Account/Login', '/Invitation/Forgot']) {
    await page.goto(route);
    await expect(page.locator('h1')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await page.evaluate(() => getComputedStyle(document.body).fontFamily)).toContain('sans-serif');
    const css = await page.locator('link[rel="stylesheet"]').getAttribute('href');
    const response = await page.request.get(css!);
    expect(response.status()).toBe(200);
  }
  expect(errors).toEqual([]);
});
