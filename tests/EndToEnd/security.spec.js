import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const settings = JSON.parse(readFileSync(fileURLToPath(new URL('../../.local/provisioner.json', import.meta.url)), 'utf8').replace(/^\uFEFF/, ''));
const users = settings.Users ?? settings.users;

async function fillAndCommit(input, value) {
  await input.fill(value);
  // Blazor's default @bind listens to change, not the input event emitted by fill.
  await input.dispatchEvent('change');
  await input.blur();
}

async function expectProjection(page, name) {
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByRole('table')).toBeVisible();
  const rows = page.locator('tbody tr');
  // Evidence is retained between runs; UUID ordering does not put new sessions on page one.
  for (let number = 1; number <= 100; number++) {
    await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeEnabled();
    const target = page.getByRole('cell', { name, exact: true });
    if (await target.count()) {
      await expect(target).toBeVisible();
      return;
    }
    if (await rows.count() === 0) break;
    const firstSession = await rows.first().locator('td').first().innerText();
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    // Page text changes before the request completes; wait for the actual row replacement.
    await expect(page.locator('tbody tr:first-child td:first-child')).not.toHaveText(firstSession);
  }
  await expect(page.getByRole('cell', { name, exact: true })).toBeVisible();
}

async function login(context, name) {
  const user = users.find(u => (u.Name ?? u.name) === name);
  if (!user) throw new Error(`Private seed missing: ${name}`);
  const page = await context.newPage();
  await page.goto('/login');
  await page.getByLabel('Username').fill(name);
  await page.getByLabel('Password', { exact: true }).waitFor({ state: 'visible' });
  // Submit the real SSR form without a password-bearing fill action in failure call logs.
  await page.evaluate(password => {
    const input = document.querySelector('input[name="password"]');
    if (!input) throw new Error('Password form input missing.');
    input.value = password;
  }, user.Password ?? user.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page).toHaveURL('https://localhost:8443/');
  await page.goto('/account');
  await expect(page.getByText(`Signed in as ${name}.`)).toBeVisible();
  return page;
}

async function reserve(page, tourId) {
  await page.goto(`/tours/${tourId}`);
  await page.getByLabel('I accept this price and quantity.').check();
  await page.getByRole('button', { name: 'Submit reservation', exact: true }).click();
  await expect(page).toHaveURL(/\/reservations\/[0-9a-f-]{36}$/);
  const id = page.url().split('/').at(-1);
  await expect(page.getByText(new RegExp(`${id} — Confirmed`))).toBeVisible();
  return id;
}

test('Alice, Bob and Admin interactive circuits isolate lists, ownership and operation controls', async ({ browser }) => {
  // Contexts have independent cookies and live circuits. No saved auth state or JWTs on disk.
  const contexts = await Promise.all([browser.newContext({ baseURL: 'https://localhost:8443' }), browser.newContext({ baseURL: 'https://localhost:8443' }), browser.newContext({ baseURL: 'https://localhost:8443' })]);
  try {
    const [alice, bob, admin] = await Promise.all(contexts.map((c, i) => login(c, ['Alice', 'Bob', 'Admin'][i])));
    const name = `LAB-007 browser ${randomUUID()}`;
    await admin.goto('/admin/catalog');
    // Static HTML is not readiness: this indicator appears only after an interactive render.
    await expect(admin.getByText('Catalog form connected.', { exact: true })).toBeVisible();
    await fillAndCommit(admin.getByLabel('Name', { exact: true }), name);
    await fillAndCommit(admin.getByLabel('Description', { exact: true }), 'Preserved browser evidence; payments and email are fictional.');
    await expect(admin.getByRole('button', { name: 'Create', exact: true })).toBeEnabled();
    await admin.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(admin).toHaveURL(/\/admin\/catalog\/[0-9a-f-]{36}$/);
    const tourId = admin.url().split('/').at(-1);
    await admin.getByRole('button', { name: 'Add session', exact: true }).click();
    await expect(admin.getByText('Session created.', { exact: true })).toBeVisible();
    const [aliceId, bobId] = await Promise.all([reserve(alice, tourId), reserve(bob, tourId)]);
    for (const [page, own, foreign] of [[alice, aliceId, bobId], [bob, bobId, aliceId]]) {
      await page.goto('/reservations');
      // Clicking invokes the scoped typed client over the live circuit; SSR is insufficient.
      await page.getByRole('button', { name: 'Reload / filter', exact: true }).click();
      await expect(page.getByRole('link', { name: own, exact: true })).toBeVisible();
      await expect(page.getByRole('link', { name: foreign, exact: true })).toHaveCount(0);
      await page.goto(`/reservations/${foreign}`);
      await expect(page.getByRole('alert')).toContainText('HTTP 404');
    }
    await admin.goto(`/reservations/${aliceId}`);
    await expect(admin.getByText(new RegExp(`${aliceId} — Confirmed`))).toBeVisible();
    await admin.goto('/operations');
    await admin.getByRole('button', { name: 'Refresh diagnostics and fault configuration', exact: true }).click();
    await expect(admin.getByText(/Persisted mode/)).toBeVisible();
    await admin.getByRole('combobox', { name: /^Service\b/ }).selectOption('Reservations');
    await admin.getByRole('button', { name: 'Refresh diagnostics and fault configuration', exact: true }).click();
    await expect(admin.getByText(/Persisted mode/)).toBeVisible();
    await alice.goto('/tour-projections');
    await expectProjection(alice, name);
    await alice.goto(`/reservations/${aliceId}`);
    await alice.getByRole('button', { name: 'Cancel with a full simulated refund', exact: true }).click();
    await expect(alice.getByText(new RegExp(`${aliceId} — Cancelled`))).toBeVisible();
    await bob.goto(`/reservations/${bobId}`);
    await expect(bob.getByText(new RegExp(`${bobId} — Confirmed`))).toBeVisible();
  } finally { await Promise.all(contexts.map(c => c.close())); }
});

test('forged forms fail; logout invalidates an existing interactive circuit within the revalidation window', async ({ browser }) => {
  const context = await browser.newContext({ baseURL: 'https://localhost:8443' });
  try {
    const account = await login(context, 'Alice');
    const cookie = (await context.cookies()).find(c => c.name === '__Host-tourlab');
    expect(cookie?.secure).toBe(true); expect(cookie?.httpOnly).toBe(true); expect(cookie?.sameSite).toBe('Strict');
    // No antiforgery token; credentials are intentionally fictional so real accounts are not locked.
    const forgedLogin = await context.request.post('/auth/login', { form: { username: 'nonexistent', password: 'fictional' } });
    expect(forgedLogin.status()).toBe(400);
    const forgedLogout = await context.request.post('/auth/logout', { form: {} });
    expect(forgedLogout.status()).toBe(400);
    const logout = await context.newPage();
    await logout.goto('/logout');
    await logout.getByRole('button', { name: /Sign out|Logout|Log out/i }).click();
    await expect(logout).toHaveURL(/\/login$/);
    // Existing circuit remains open while another tab signs out and changes the security stamp.
    await expect(account.getByText(/Your session expired|Sign in with an authorized account/)).toBeVisible({ timeout: 45_000 });
    expect((await context.cookies()).some(c => c.name === '__Host-tourlab')).toBe(false);
  } finally { await context.close(); }
});
