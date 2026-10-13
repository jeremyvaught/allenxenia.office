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

test('first manager login needs only a password, with optional single-verification setup later', async ({ browser }, testInfo) => {
  const directory = readFileSync('artifacts/browser-fixture/current.txt', 'utf8');
  const account = JSON.parse(readFileSync(join(directory, 'accounts.json'), 'utf8'))[testInfo.project.name];
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:5088', javaScriptEnabled: false,
    viewport: { width: 320, height: 800 }, colorScheme: 'dark' });
  try {
    const page = await context.newPage();
    await login(page, account.firstManagerEmail, account.password);
    await expect(page).toHaveURL('http://127.0.0.1:5088/Portal');
    await expect(page.getByRole('heading', { name: 'Welcome to your office', exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Set up account security', exact: true })).toBeVisible();
    await expect(page.getByLabel('Code from your authenticator')).toHaveCount(0);
    await expect(page.getByText('No active tenant membership.', { exact: false })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('first-login-dark-320.png'), fullPage: true });
    await page.getByRole('link', { name: 'Building help', exact: true }).click();
    await page.goto('/Portal');
    await page.getByRole('link', { name: 'Set up account security', exact: true }).click();
    const key = (await page.locator('code').innerText()).trim();
    await page.getByLabel('Code from your authenticator', { exact: true }).fill(code(key));
    await page.getByRole('button', { name: 'Verify and enable', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Keep these codes safe', exact: true })).toBeVisible();
    await expect(page.getByLabel('Password', { exact: true })).toHaveCount(0);
    await expect(page.locator('section ul li')).toHaveCount(10);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByRole('link', { name: 'Continue to your office', exact: true }).click();
    await expect(page).toHaveURL('http://127.0.0.1:5088/Portal');
    await expect(page.getByRole('link', { name: 'Manage maintenance', exact: true })).toBeVisible();
  } finally { await context.close(); }
});

test('public maintenance entry returns a native tenant sign-in to the unsubmitted form', async ({ browser }, testInfo) => {
  const directory = readFileSync('artifacts/browser-fixture/current.txt', 'utf8');
  const account = JSON.parse(readFileSync(join(directory, 'accounts.json'), 'utf8'))[testInfo.project.name];
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:5088', javaScriptEnabled: false,
    viewport: { width: 320, height: 800 }, colorScheme: 'dark' });
  try {
    const page = await context.newPage();
    await page.goto('/');
    await page.getByRole('link', { name: 'Report a maintenance issue', exact: true }).click();
    await expect(page).toHaveURL(/\/Account\/Login\?/);
    await expect(page.locator('[name="ReturnUrl"]')).toHaveValue('/Maintenance/Create');
    await page.getByLabel('Email', { exact: true }).fill(account.tenantEmail);
    await page.getByLabel('Password', { exact: true }).fill(account.password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page).toHaveURL('http://127.0.0.1:5088/Maintenance/Create');
    await expect(page.getByRole('heading', { name: 'What needs attention?', exact: true })).toBeVisible();
    await expect(page.getByLabel('Brief description', { exact: true })).toHaveValue('');
    await expect(page.getByRole('button', { name: 'Submit maintenance request', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { await context.close(); }
});

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
  await page.goto('/Maintenance');
  await page.getByLabel('Find a request by title or location', { exact: true }).fill('KITCHEN');
  await page.getByRole('button', { name: 'Search requests', exact: true }).click();
  await expect(page.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) })).toBeVisible();
  expect(new URL(page.url()).search).toBe('');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.goto(path);
  await page.getByLabel('Your comment', { exact: true }).fill('Tenant update typed before management changes.');

  const managerContext = await browser.newContext({ baseURL: 'http://127.0.0.1:5088', viewport: { width: 320, height: 800 },
    colorScheme: 'light', isMobile: Boolean(testInfo.project.use.isMobile), hasTouch: Boolean(testInfo.project.use.hasTouch) });
  const manager = await managerContext.newPage();
  try {
    await login(manager, account.managerEmail, account.password);
    await manager.getByLabel('Verification code').fill(code(account.key));
    await manager.getByRole('button', { name: 'Verify sign-in', exact: true }).click();
    await manager.getByRole('link', { name: 'Contractor contacts' }).click();
    await manager.getByRole('link', { name: 'Add a contractor' }).click();
    await expect(manager.getByLabel('Trade', { exact: true }).locator('option', { hasText: 'Heating and cooling' })).toHaveCount(1);
    await manager.getByLabel('Contact name').fill('Reliable plumbing');
    await manager.getByLabel('Email (optional)').fill('plumber@example.test');
    await manager.getByRole('button', { name: 'Save contact' }).click();
    await manager.getByRole('link', { name: 'Edit Reliable plumbing' }).click();
    const contactPath = new URL(manager.url()).pathname;
    await manager.goto('/Maintenance/Manage');
    await manager.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) }).click();
    await expect(manager.getByLabel('Priority', { exact: true })).toHaveValue('1');
    await expect(manager.getByLabel('Trade', { exact: true }).locator('option', { hasText: 'Heating and cooling' })).toHaveCount(1);
    await manager.getByLabel('Priority', { exact: true }).selectOption('2');
    await manager.getByLabel('Trade', { exact: true }).selectOption('1');
    await manager.getByRole('button', { name: 'Triage', exact: true }).click();
    await manager.getByLabel('Assign to', { exact: true }).selectOption({ label: 'Reliable plumbing (contractor)' });
    await manager.getByRole('button', { name: 'Save assignment', exact: true }).click();
    await manager.getByRole('button', { name: 'Start', exact: true }).click();
    const requestPath = new URL(manager.url()).pathname;
    await manager.goto(contactPath);
    await manager.getByRole('checkbox', { name: /Active/ }).uncheck();
    await manager.getByRole('button', { name: 'Save contact' }).click();
    await manager.goto(requestPath);
    await expect(manager.getByText('Assigned to: Reliable plumbing', { exact: true })).toBeVisible();
    await expect(manager.getByLabel('Assign to', { exact: true }).locator('option', { hasText: 'Reliable plumbing' })).toHaveCount(0);
    await manager.getByLabel('Your comment', { exact: true }).fill('Private supplier details');
    await manager.getByRole('checkbox', { name: /Internal note/ }).check();
    await manager.getByRole('button', { name: 'Add comment', exact: true }).click();
    await page.getByRole('button', { name: 'Add comment', exact: true }).click();
    await expect(page.getByText('Tenant update typed before management changes.', { exact: true })).toBeVisible();
    await expect(page.getByText('Private supplier details')).toHaveCount(0);
    await manager.reload();
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
    await manager.goto('/Knowledge/Manage');
    await manager.getByRole('link', { name: 'Write an article' }).click();
    const helpTitle = `Entry guidance ${testInfo.project.name}`;
    await manager.getByLabel('Article title').fill(helpTitle);
    await manager.getByLabel('Guidance (plain text)').fill('Approved public directions.');
    await manager.getByRole('button', { name: 'Save draft', exact: true }).click();
    const articleId = await manager.locator('form[action="/Knowledge/Save"] input[name="Id"]').inputValue();
    const guestContext = await browser.newContext({ baseURL: 'http://127.0.0.1:5088', viewport: { width: 320, height: 800 } });
    const guest = await guestContext.newPage();
    try {
      let response = await guest.goto(`/Knowledge/Article/${articleId}`);
      expect(response?.status()).toBe(404);
      await manager.getByRole('button', { name: 'Publish reviewed revision', exact: true }).click();
      await guest.goto(`/Knowledge/Article/${articleId}`);
      await expect(guest.getByText('Approved public directions.', { exact: true })).toBeVisible();
      await manager.getByLabel('Guidance (plain text)').fill('Private entry details <script>window.leaked = true</script>');
      await manager.getByLabel('Who can read it?').selectOption('ActiveTenant');
      await manager.getByRole('button', { name: 'Save draft', exact: true }).click();
      await guest.reload();
      await expect(guest.getByText('Approved public directions.', { exact: true })).toBeVisible();
      await manager.getByRole('button', { name: 'Publish reviewed revision', exact: true }).click();
      response = await guest.reload();
      expect(response?.status()).toBe(404);
      await guest.goto(`/Knowledge?search=${encodeURIComponent(helpTitle)}`);
      await expect(guest.getByRole('link', { name: helpTitle, exact: true })).toHaveCount(0);
      await page.goto(`/Knowledge/TenantArticle/${articleId}`);
      await expect(page.getByText('Private entry details <script>window.leaked = true</script>', { exact: true })).toBeVisible();
      expect(await page.evaluate(() => 'leaked' in window)).toBe(false);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: `artifacts/browser/help-${testInfo.project.name}.png`, fullPage: true });
      await manager.getByRole('button', { name: 'Withdraw article', exact: true }).click();
      response = await page.reload();
      expect(response?.status()).toBe(404);
    } finally { await guestContext.close(); }


    await manager.goto('/Management');
    const addedSuite = `Additional suite ${testInfo.project.name}`;
    await manager.getByLabel('Suite name', { exact: true }).fill(addedSuite);
    await manager.getByRole('button', { name: 'Add suite', exact: true }).click();
    await expect(manager.getByRole('status')).toContainText(`Suite ${addedSuite} added`);
    await manager.getByLabel('Suite for existing account').selectOption({ label: addedSuite });
    await manager.getByLabel('Existing account email').fill(account.tenantEmail);
    await manager.getByLabel('Reason for granting access').fill('Additional suite approved.');
    await manager.getByRole('button', { name: 'Grant access to this suite', exact: true }).click();
    await expect(manager.getByRole('status')).toContainText('Suite access granted');
    await page.goto('/Maintenance/Create');
    await expect(page.getByLabel('Office', { exact: true }).locator('option', { hasText: addedSuite })).toHaveCount(1);
    await manager.getByLabel('Suite', { exact: true }).selectOption({ label: addedSuite });
    const invitedEmail = `invited-${testInfo.project.name}@example.test`;
    await manager.getByLabel('Email', { exact: true }).fill(invitedEmail);
    await manager.getByRole('button', { name: 'Record invitation', exact: true }).click();
    let invitation = manager.locator('article').filter({ hasText: invitedEmail });
    await invitation.getByLabel('Reason for invitation change').fill('Replace the activation link.');
    await invitation.getByRole('button', { name: 'Reissue invitation', exact: true }).click();
    await expect(manager.getByRole('status')).toContainText('Previous links are invalid');
    invitation = manager.locator('article').filter({ hasText: invitedEmail });
    await invitation.getByLabel('Reason for invitation change').fill('Invitation no longer needed.');
    await invitation.getByRole('button', { name: 'Revoke invitation', exact: true }).click();
    await expect(manager.locator('article').filter({ hasText: invitedEmail })).toContainText('Revoked');
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/access-${testInfo.project.name}.png`, fullPage: true });


    await manager.goto('/Billing/Manage');
    await manager.getByLabel('Tenant membership').selectOption(account.membershipId);
    await manager.getByRole('button', { name: 'Review new rent account', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Open rent account');
    await manager.getByRole('button', { name: 'Confirm reviewed change', exact: true }).click();
    const billingPath = new URL(manager.url()).pathname;
    await manager.getByLabel('Monthly rent (USD)').fill('1000.00');
    await manager.getByLabel('Manager-set due day').fill('31');
    await manager.getByLabel('Terms start on').fill('2026-01-01');
    await manager.getByRole('button', { name: 'Review rent terms', exact: true }).click();
    await expect(manager.getByText('$1,000.00 USD', { exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.getByRole('button', { name: 'Confirm reviewed change', exact: true }).click();
    await manager.getByLabel('Rent year').fill('2026');
    await manager.getByLabel('Rent month', { exact: true }).selectOption('2');
    await manager.getByRole('button', { name: 'Review calendar rent', exact: true }).click();
    await expect(manager.getByText('2026-02-28', { exact: true })).toBeVisible();
    await manager.getByRole('button', { name: 'Confirm reviewed change', exact: true }).click();
    await manager.getByLabel('One-time amount (USD)').fill('19.75');
    await manager.getByLabel('Charge description').fill('Approved opening charge');
    await manager.getByLabel('Charge due on').fill('2026-02-28');
    await manager.getByRole('button', { name: 'Review one-time charge', exact: true }).click();
    await expect(manager.getByText('$19.75 USD', { exact: true })).toBeVisible();
    await manager.getByRole('button', { name: 'Confirm reviewed change', exact: true }).click();
    const accountId = billingPath.split('/').pop();
    await page.goto(`/Billing/Account/${accountId}`);
    await expect(page.getByText('Approved opening charge', { exact: true })).toBeVisible();
    await expect(page.getByText('View verified balances before paying', { exact: true })).toHaveCount(2);
    await expect(page.locator('form[action*="Prepare"]')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/browser/billing-tenant-${testInfo.project.name}.png`, fullPage: true });
    await manager.screenshot({ path: `artifacts/browser/billing-manager-${testInfo.project.name}.png`, fullPage: true });


    await manager.goto('/Maintenance/Manage');
    await manager.getByRole('link', { name: 'Record a tenant request', exact: true }).click();
    await manager.getByLabel('Office', { exact: true }).selectOption(account.membershipId);
    const behalfTitle = `Phone report ${testInfo.project.name}`;
    await manager.getByLabel('Brief description').fill(behalfTitle);
    await manager.getByLabel('Where is it?').fill('Entrance');
    await manager.getByLabel('Details', { exact: true }).fill('Tenant reported a stuck door by phone.');
    await manager.getByRole('button', { name: 'Submit maintenance request', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText(behalfTitle);
    await expect(manager.getByText('Request recorded by manager for tenant', { exact: false })).toBeVisible();
    await page.goto(new URL(manager.url()).pathname.replace('ManageDetails', 'Details'));
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(behalfTitle);
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);


    await manager.goto('/Maintenance/Manage');
    await expect(manager.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) })).toHaveCount(0);
    await manager.getByLabel('Status', { exact: true }).selectOption('Closed');
    await manager.getByRole('button', { name: 'Apply filters', exact: true }).click();
    await expect(manager.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) })).toBeVisible();
    await manager.getByRole('link', { name: 'Reset filters', exact: true }).click();
    await manager.getByLabel('Status', { exact: true }).selectOption('Submitted');
    await manager.getByLabel('Assignee', { exact: true }).selectOption('unassigned');
    await manager.getByLabel('Minimum age (days)').fill('0');
    await manager.getByLabel('Order', { exact: true }).selectOption('Newest');
    await manager.getByRole('button', { name: 'Apply filters', exact: true }).click();
    await expect(manager.getByRole('link', { name: new RegExp(behalfTitle) })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/queue-${testInfo.project.name}.png`, fullPage: true });
    await manager.goto('/Portal');
    await manager.getByRole('link', { name: 'Maintenance aging report', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Maintenance aging');
    await expect(manager.getByRole('link', { name: new RegExp(behalfTitle) })).toBeVisible();
    await expect(manager.getByRole('link', { name: new RegExp(`Leaking sink ${testInfo.project.name}`) })).toHaveCount(0);
    await manager.getByLabel('Age group', { exact: true }).selectOption('AwaitingVerification');
    await manager.getByRole('button', { name: 'View report', exact: true }).click();
    await expect(manager.getByRole('link', { name: new RegExp(behalfTitle) })).toHaveCount(0);
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.getByRole('link', { name: 'Reset report', exact: true }).click();
    for (const select of await manager.locator('select').all()) {
      const bounds = await select.boundingBox();
      expect(bounds!.height).toBeGreaterThanOrEqual(44);
      expect(bounds!.width).toBeGreaterThanOrEqual(44);
    }
    await manager.screenshot({ path: `artifacts/browser/report-${testInfo.project.name}.png`, fullPage: true });
    await manager.goto('/Portal');
    await manager.getByRole('link', { name: 'Rent balance report', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Rent balances');
    await expect(manager.getByRole('region', { name: 'Recorded balance totals' })).toBeVisible();
    await expect(manager.getByRole('region', { name: 'Unresolved financial work' })).toBeVisible();
    await manager.getByLabel('Balance group', { exact: true }).selectOption('Held');
    await manager.getByRole('button', { name: 'View report', exact: true }).click();
    await expect(manager.getByLabel('Balance group', { exact: true })).toHaveValue('Held');
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await manager.locator('select, button').all()) {
      const bounds = await control.boundingBox();
      if (bounds) { expect(bounds.height).toBeGreaterThanOrEqual(44); expect(bounds.width).toBeGreaterThanOrEqual(44); }
    }
    await manager.screenshot({ path: `artifacts/browser/rent-report-${testInfo.project.name}.png`, fullPage: true });
    await manager.goto('/Portal');
    await manager.getByRole('link', { name: 'Financial exceptions', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Financial exceptions');
    await expect(manager.getByText(`acct_browser_${testInfo.project.name.replaceAll('-', '_')}`, { exact: false })).toBeVisible();
    await expect(manager.getByRole('heading', { name: 'Verified cash movements', exact: true })).toBeVisible();
    await expect(manager.getByText('Allocation / debt disposition pending', { exact: true })).toBeVisible();
    await expect(manager.getByText(`Transaction txn_browser_${testInfo.project.name.replaceAll('-', '_')}`, { exact: true })).toBeVisible();
    await manager.getByLabel('Assign case owner', { exact: true }).selectOption({ label: account.managerEmail });
    await manager.getByLabel('Assignment reason', { exact: true }).fill('Review original Stripe outcome.');
    await manager.getByRole('button', { name: 'Assign owner', exact: true }).click();
    await manager.getByLabel('Escalation reason', { exact: true }).fill('Awaiting provider investigation.');
    await manager.getByRole('button', { name: 'Record escalation', exact: true }).click();
    await expect(manager.getByText('Escalated', { exact: true }).first()).toBeVisible();
    await manager.getByText('Recent recovery history', { exact: true }).click();
    await expect(manager.getByText('Review original Stripe outcome.', { exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/financial-cases-${testInfo.project.name}.png`, fullPage: true });
    await manager.getByRole('link', { name: 'Review debt disposition', exact: true }).click();
    const refundPath = new URL(manager.url()).pathname;
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Allocate this refund');
    await manager.getByLabel('Returned amount for this charge (USD)', { exact: true }).fill('5.00');
    await manager.getByLabel('Debt disposition', { exact: true }).selectOption('CreditDebt');
    await manager.getByLabel('Reason for this disposition', { exact: true }).fill('Credit the verified returned portion of the original charge.');
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/refund-prepare-${testInfo.project.name}.png`, fullPage: true });
    await manager.getByRole('button', { name: 'Review refund disposition', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Review refund disposition');
    await expect(manager.getByText('Reduce debt by the returned amount', { exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/refund-review-${testInfo.project.name}.png`, fullPage: true });
    await manager.getByRole('button', { name: 'Confirm refund disposition', exact: true }).click();
    await expect(manager.getByText('Allocation / debt disposition recorded — provider agreement pending', { exact: true })).toBeVisible();
    await expect(manager.getByRole('link', { name: 'Review debt disposition', exact: true })).toHaveCount(0);
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.getByLabel('Refund or dispute reference', { exact: true }).fill(`re_browser_${testInfo.project.name.replaceAll('-', '_')}`);
    await manager.getByLabel('Verification reason', { exact: true }).fill('Recheck original refund evidence without issuing a refund.');
    await manager.getByRole('button', { name: 'Request cash-source verification', exact: true }).click();
    await expect(manager.getByRole('heading', { name: 'Cash-source verification', exact: true })).toBeVisible();
    await expect(manager.getByText('Verification queued', { exact: false })).toBeVisible();
    await expect(manager.getByText('Allocation / debt disposition recorded — provider agreement pending', { exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.screenshot({ path: `artifacts/browser/cash-source-queue-${testInfo.project.name}.png`, fullPage: true });
    await page.goto(refundPath);
    await expect(page).toHaveURL(/\/Account\/Denied/);

    await page.goto(`/Autopay?accountId=${account.autopayAccountId}`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Monthly autopay');
    await page.getByRole('button', { name: 'Review monthly autopay', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Review monthly autopay');
    await expect(page.getByRole('checkbox')).not.toBeChecked();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/browser/autopay-review-${testInfo.project.name}.png`, fullPage: true });
    await page.getByRole('link', { name: 'Cancel and return to autopay', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Review monthly autopay', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Review monthly autopay', exact: true }).click();
    await page.getByRole('checkbox').check();
    await page.getByRole('button', { name: 'Confirm monthly autopay consent', exact: true }).click();
    await expect(page.getByText('Preparing payment-method setup', { exact: true })).toBeVisible();
    await expect(page.getByText('Verified active', { exact: true })).toHaveCount(0);
    await page.getByLabel('When to stop', { exact: true }).selectOption('EndOfCurrentRentMonth');
    await page.getByRole('button', { name: 'Review stopping autopay', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Review stopping autopay');
    await page.getByRole('checkbox').check();
    await page.getByRole('button', { name: 'Confirm stop request', exact: true }).click();
    await expect(page.getByText('Stop requested · verification pending', { exact: true })).toBeVisible();
    await expect(page.getByText('Verified stopped', { exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await page.locator('main .button, main .quiet-button').all()) {
      const bounds = await control.boundingBox(); expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
    await page.screenshot({ path: `artifacts/browser/autopay-stop-${testInfo.project.name}.png`, fullPage: true });

    await page.goto(`/Autopay?accountId=${account.autopaySetupAccountId}`);
    await expect(page.getByRole('button', { name: 'Continue payment-method setup', exact: true })).toBeVisible();
    await expect(page.getByText('Saving a card does not pay rent or confirm active autopay.', { exact: false })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const methodButton = await page.getByRole('button', { name: 'Continue payment-method setup', exact: true }).boundingBox();
    expect(methodButton!.height).toBeGreaterThanOrEqual(44);
    await page.screenshot({ path: `artifacts/browser/autopay-method-${testInfo.project.name}.png`, fullPage: true });
    await page.getByRole('button', { name: 'Continue payment-method setup', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Payment-method setup is unavailable right now');
    await expect(page.getByText('Verified active', { exact: true })).toHaveCount(0);

    await page.goto(`/Payments?accountId=${account.paymentAccountId}`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Choose what to pay');
    await page.getByRole('button', { name: 'Review selected payment', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Choose an eligible payment');
    await expect(page.getByRole('alert')).toContainText('Check recent requests');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const link of await page.locator('main .button, main .quiet-button').all()) {
      const bounds = await link.boundingBox(); expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
    await page.screenshot({ path: `artifacts/browser/payment-problem-${testInfo.project.name}.png`, fullPage: true });
    await page.getByRole('link', { name: 'View billing and payment requests', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Your billing records');
    await expect(page.getByText('Payment collection is unavailable.', { exact: false })).toHaveCount(0);
    await page.goto(`/Payments?accountId=${account.paymentAccountId}`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Choose what to pay');
    await expect(page.getByRole('checkbox')).toHaveCount(2);
    for (const checkbox of await page.getByRole('checkbox').all()) await checkbox.check();
    await page.getByRole('button', { name: 'Review selected payment', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Review your payment');
    await expect(page.getByText('$10.25 USD', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/browser/payment-review-${testInfo.project.name}.png`, fullPage: true });
    await page.getByRole('link', { name: 'Cancel and return to balances', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Choose what to pay');
    await expect(page.getByRole('checkbox')).toHaveCount(2);
    await expect(page.getByRole('heading', { name: 'Recent payment requests', exact: true })).toHaveCount(0);
    for (const checkbox of await page.getByRole('checkbox').all()) await checkbox.check();
    await page.getByRole('button', { name: 'Review selected payment', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Review your payment');
    await page.getByRole('button', { name: 'Confirm and prepare checkout', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Preparing checkout');
    const paymentPath = new URL(page.url()).pathname;
    await expect(page.getByText('App records read', { exact: false })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Continue to secure checkout', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/browser/payment-status-${testInfo.project.name}.png`, fullPage: true });
    await page.getByRole('link', { name: 'View account balances', exact: true }).click();
    await expect(page.getByRole('checkbox', { disabled: true })).toHaveCount(2);
    await page.getByRole('link', { name: /^View payment / }).click();
    await expect(page).toHaveURL(new RegExp(paymentPath + '$'));

    await page.goto('/FinancialExceptions');
    await expect(page).toHaveURL(/\/Account\/Denied/);
    await page.goto('/Reports/Maintenance');
    await expect(page).toHaveURL(/\/Account\/Denied/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Access unavailable');

    await manager.goto('/Portal');
    await manager.getByRole('link', { name: 'Completion time report', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Maintenance completion times');
    await expect(manager.getByRole('heading', { name: 'Repair completion', exact: true })).toBeVisible();
    await expect(manager.getByRole('heading', { name: 'Manager verification', exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await manager.locator('main button, main select, main .quiet-button').all()) {
      expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    }
    await manager.screenshot({ path: `artifacts/browser/completion-report-${testInfo.project.name}.png`, fullPage: true });
    await page.goto('/Reports/Completion');
    await expect(page).toHaveURL(/\/Account\/Denied/);

    await manager.goto(`/Billing/ManageAccount/${account.paymentAccountId}`);
    await manager.getByLabel('Reason for ending tenancy', { exact: true }).fill('Tenant has left this suite. Reconcile existing provider instructions.');
    await manager.getByRole('button', { name: 'Review tenancy end', exact: true }).click();
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('End tenancy and revoke access');
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await manager.getByRole('button', { name: 'Confirm reviewed change', exact: true }).click();
    await expect(manager.getByRole('region', { name: 'Pending billing closure' })).toBeVisible();
    await expect(manager.getByRole('button', { name: 'Review tenancy end', exact: true })).toHaveCount(0);
    await expect(manager.getByRole('button', { name: 'Review calendar rent', exact: true })).toHaveCount(0);
    const endedPayment = await page.goto(paymentPath); expect(endedPayment!.status()).toBe(404);
    await manager.goto('/Billing/Closures');
    await expect(manager.getByRole('heading', { level: 1 })).toHaveText('Pending billing closures');
    await expect(manager.getByText('Tenant has left this suite. Reconcile existing provider instructions.', { exact: true })).toBeVisible();
    expect(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const link of await manager.locator('main .quiet-button').all()) {
      const bounds = await link.boundingBox(); expect(bounds!.height).toBeGreaterThanOrEqual(44);
    }
    await manager.screenshot({ path: `artifacts/browser/billing-closure-${testInfo.project.name}.png`, fullPage: true });

  } finally { await managerContext.close(); }
});
