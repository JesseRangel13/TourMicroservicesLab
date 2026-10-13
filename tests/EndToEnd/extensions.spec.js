import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';

const settings = JSON.parse(readFileSync(new URL('../../.local/provisioner.json', import.meta.url), 'utf8').replace(/^\uFEFF/, ''));
const users = settings.Users ?? settings.users;
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64');

async function login(page, name) {
  const user = users.find(x => (x.Name ?? x.name) === name);
  await page.goto('/login');
  await page.getByLabel('Username').fill(name);
  await page.evaluate(password => { document.querySelector('input[name="password"]').value = password; }, user.Password ?? user.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page).toHaveURL('https://localhost:8443/');
}
async function commit(input, value) {
  await input.fill(value); await input.dispatchEvent('change'); await input.blur();
}

test('Admin image upload displays a private image and removal hides it; booking dashboard remains explicit', async ({ page }) => {
  await login(page, 'Admin');
  await page.goto('/admin/catalog');
  const name = `EXT-001 browser ${randomUUID()}`;
  await expect(page.getByText('Catalog form connected.', { exact: true })).toBeVisible();
  await commit(page.getByLabel('Name', { exact: true }), name);
  await commit(page.getByLabel('Description', { exact: true }), 'Small synthetic local image evidence.');
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/catalog\/[0-9a-f-]{36}$/);
  await expect(page.getByText('Image controls connected.', { exact: true })).toBeVisible();
  await page.locator('input[type="file"]').setInputFiles({ name: 'tour.png', mimeType: 'image/png', buffer: png });
  await page.getByRole('button', { name: 'Upload / retry same image', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('img[alt="Tour image"]')?.naturalWidth === 1);
  await page.getByRole('button', { name: 'Remove', exact: true }).click();
  await expect(page.getByText('Image unavailable.', { exact: true })).toBeVisible();
  await page.goto('/my-bookings');
  await expect(page.getByRole('heading', { name: 'My bookings', exact: true })).toBeVisible();
  await expect(page.getByText(/Eventually consistent DynamoDB projection/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Rebuild next batch', exact: true })).toBeVisible();
});
