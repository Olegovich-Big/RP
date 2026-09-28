const { test, expect } = require('@playwright/test');
const { execFileSync } = require('node:child_process');
const path = require('node:path');
const proxy = 'http://localhost:8080';
function compose(...args) {
    execFileSync('docker', ['compose', '-f', path.join(__dirname, '../compose.yaml'), ...args], { stdio: 'pipe' });
}
async function submit(page) {
    await page.goto(proxy);
    const text = `SignalR ${Date.now()} !`;
    await page.locator('textarea').fill(text);
    await Promise.all([page.waitForURL(/Summary\?id=/i), page.locator('input[type=submit]').click()]);
    return { text, path: new URL(page.url()).pathname + new URL(page.url()).search };
}
function expectedRank(text) { return [...text].filter(c => !/[a-zA-Zа-яА-ЯёЁ]/.test(c)).length / [...text].length; }

test('WebSocket push updates three replicas without navigation; late subscriber reads snapshot', async ({ browser }) => {
    test.setTimeout(150000);
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    const sockets = [];
    page.on('websocket', ws => sockets.push(ws.url()));
    compose('stop', 'rankcalculator');
    let peers = [];
    let navigationCounts;
    let submission;
    let started;
    try {
        submission = await submit(page);
        await expect(page.locator('#pending')).toBeVisible();
        await expect(page.locator('#connection-status')).toContainText('Ожидаем результат');
        peers = await Promise.all([context.newPage(), context.newPage()]);
        await peers[0].goto('http://localhost:5001' + submission.path);
        await peers[1].goto('http://localhost:5002' + submission.path);
        for (const peer of peers) await expect(peer.locator('#connection-status')).toContainText('Ожидаем результат');
        expect(sockets.some(url => url.includes('/hubs/evaluation'))).toBe(true);
        navigationCounts = [0, 0, 0];
        [page, ...peers].forEach((p, i) => p.on('framenavigated', frame => {
            if (frame === p.mainFrame()) navigationCounts[i]++;
        }));
    } finally { started = Date.now(); compose('start', 'rankcalculator'); }
    for (const p of [page, ...peers]) {
        await expect(p.locator('#result')).toBeVisible();
        expect(Number(await p.locator('#rank').innerText())).toBeCloseTo(expectedRank(submission.text), 10);
        await expect(p.locator('#pending')).toBeHidden();
    }
    expect(Date.now() - started).toBeGreaterThanOrEqual(2500);
    expect(navigationCounts).toEqual([0, 0, 0]);
    expect(errors).toEqual([]);
    const latePage = await context.newPage();
    await latePage.goto(proxy + submission.path);
    await expect(latePage.locator('#result')).toBeVisible();
    await expect(latePage.locator('#connection-status')).toHaveText('Результат получен');
    await context.close();
});

test('reconnect catches a result completed while the browser was offline', async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();
    compose('stop', 'rankcalculator');
    let submission;
    try {
        submission = await submit(page);
        await expect(page.locator('#connection-status')).toContainText('Ожидаем результат');
        await context.setOffline(true);
    } finally { compose('start', 'rankcalculator'); }
    // Node-side polling is only a test assertion, not the browser's update mechanism.
    await expect.poll(async () => {
        const response = await fetch(proxy + submission.path);
        return /id="rank">[0-9]/.test(await response.text());
    }, { timeout: 60000 }).toBe(true);
    await expect(page.locator('#pending')).toBeVisible();
    let navigations = 0;
    page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
    await context.setOffline(false);
    await expect(page.locator('#result')).toBeVisible();
    expect(Number(await page.locator('#rank').innerText())).toBeCloseTo(expectedRank(submission.text), 10);
    expect(navigations).toBe(0);
    await context.close();
});
