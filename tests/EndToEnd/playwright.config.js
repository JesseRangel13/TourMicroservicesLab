import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.', testMatch: '*.spec.js', workers: 1, fullyParallel: false,
  retries: 0, timeout: 120_000, expect: { timeout: 45_000 },
  outputDir: 'test-results', reporter: 'line',
  use: {
    baseURL: 'https://localhost:8443', browserName: 'chromium',
    channel: process.env.LAB_BROWSER_CHANNEL || 'chrome',
    ignoreHTTPSErrors: false, trace: 'off', screenshot: 'off', video: 'off'
  }
});
