const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({
    testDir: '.',
    testMatch: '*.spec.cjs',
    timeout: 120000,
    expect: { timeout: 60000 },
    workers: 1,
    use: { headless: true, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
    reporter: [['list'], ['html', { open: 'never' }]]
});
