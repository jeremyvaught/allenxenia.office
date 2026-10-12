import { test, expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHmac } from 'node:crypto';

function code(key: string) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let buffer = 0, bits = 0;
  const bytes: number[] = [];
  for (const letter of key) {
    buffer = (buffer << 5) | alphabet.indexOf(letter); bits += 5;
    if (bits >= 8) { bits -= 8; bytes.push((buffer >>> bits) & 255); }
  }
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const value = hash.readUInt32BE(hash[19] & 15) & 0x7fffffff;
  return String(value % 1000000).padStart(6, '0');
}

async function login(page: Page, email: string, password: string) {
  await page.goto('/Account/Login');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}

test('tenant and manager complete a private maintenance workflow at 320px', async ({ page, browser }, testInfo) => {
  const directory = readFileSync('artifacts/browser-fixture/current.txt', 'utf8');
  const account = JSON.parse(readFileSync(join(directory, 'accounts.json'), 'utf8'))[testInfo.project.name];
  await page.setViewportSize({ width: 320, height: 800 });
  await page.emulateMedia({ colorScheme: 'dark' });
  await login(page, account.tenantEmail, account.password);
  await page.getByRole('link', { name: 'Report an issue' }).click();
  await page.getByLabel('Brief description').fill(`Leaking sink ${testInfo.project.name}`);
  await page.getByLabel('Where is it?').fill('Kitchen');
  await page.getByLabel('Details', { exact: true }).fill('Water under the basin. Please inspect it.');
  await page.getByRole('button', { name: 'Submit maintenance request' }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(`Leaking sink ${testInfo.project.name}`);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => getComputedStyle(document.body).fontFamily)).toContain('sans-serif');
  const path = new URL(page.url()).pathname;
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

  const managerContext = await browser.newContext({ baseURL: 'http://127.0.0.1:5088', viewport: { width: 320, height: 800 },
    colorScheme: 'light', isMobile: testInfo.project.name === 'mobile', hasTouch: testInfo.project.name === 'mobile' });
  const manager = await managerContext.newPage();
  try {
    await login(manager, account.managerEmail, account.password);
    await manager.getByLabel('Verification code').fill(code(account.key));
    await manager.getByRole('button', { name: 'Verify sign-in', exact: true }).click();
    await manager.getByRole('link', { name: 'Manage maintenance' }).click();
    await manager.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) }).click();
    await manager.getByLabel('Priority', { exact: true }).selectOption('2');
    await manager.getByLabel('Trade', { exact: true }).selectOption('1');
    await manager.getByRole('button', { name: 'Triage', exact: true }).click();
    await manager.getByRole('button', { name: 'Assign to me', exact: true }).click();
    await manager.getByRole('button', { name: 'Start', exact: true }).click();
    await manager.getByLabel('Your comment', { exact: true }).fill('Private supplier details');
    await manager.getByRole('checkbox', { name: /Internal note/ }).check();
    await manager.getByRole('button', { name: 'Add comment', exact: true }).click();
    await page.reload();
    await expect(page.getByText('Private supplier details')).toHaveCount(0);
    await manager.getByLabel('Resolution', { exact: true }).fill('Replaced the seal and tested for leaks.');
    await manager.getByRole('button', { name: 'Complete', exact: true }).click();
    await expect(manager.getByText('Verify the work before closing this request.', { exact: false })).toBeVisible();
    await manager.getByRole('button', { name: 'Verify work and close', exact: true }).click();
    await page.goto(path);
    await expect(page.getByText('Replaced the seal and tested for leaks.', { exact: true }).first()).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(manager.locator('html')).toHaveAttribute('data-theme', 'light');
    expect(await manager.evaluate(() => getComputedStyle(document.body).fontFamily)).toContain('sans-serif');
    await page.screenshot({ path: `artifacts/browser/tenant-${testInfo.project.name}.png`, fullPage: true });
    await manager.screenshot({ path: `artifacts/browser/manager-${testInfo.project.name}.png`, fullPage: true });
  } finally { await managerContext.close(); }
});
