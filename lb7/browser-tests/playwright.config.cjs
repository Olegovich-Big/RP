const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({
  testDir: '.', testMatch: '*.spec.cjs', timeout: 120000,
  expect: { timeout: 15000 }, workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { headless: true, trace: 'retain-on-failure', screenshot: 'only-on-failure' }
});
