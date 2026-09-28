const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
const { execFileSync } = require('node:child_process');
const path = require('node:path');
const base = 'http://localhost:8080';
const password = 'Test-' + randomUUID();
const composeFile = path.resolve(__dirname, '../compose.yaml');
function compose(...args) { execFileSync('docker', ['compose', '-f', composeFile, ...args], { stdio: 'pipe' }); }

async function register(page, login) {
  await page.goto(base + '/Account/Register');
  await page.getByLabel('Логин', { exact: true }).fill(login);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByLabel('Повторите пароль', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Зарегистрироваться', exact: true }).click();
  await expect(page).toHaveURL(/\/Account\/Login\?registered=True/i);
}
async function login(page, name, url = base + '/Account/Login', secret = password) {
  await page.goto(url);
  await page.getByLabel('Логин', { exact: true }).fill(name);
  await page.getByLabel('Пароль', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
}

test('authentication, ownership, replicas, forgery and logout', async ({ browser }) => {
  const aliceName = 'alice_' + randomUUID().replaceAll('-', '').slice(0, 12);
  const bobName = 'bob_' + randomUUID().replaceAll('-', '').slice(0, 12);
  const alice = await browser.newContext();
  const bob = await browser.newContext();
  const anonymous = await browser.newContext();
  const a = await alice.newPage();
  const b = await bob.newPage();
  try {
    await a.goto(base + '/');
    await expect(a).toHaveURL(/\/Account\/Login\?ReturnUrl=/);
    const anonymousPost = await anonymous.request.post(base + '/', { form: { Text: 'unauthorized' }, maxRedirects: 0 });
    expect(anonymousPost.status()).toBe(302);
    expect(anonymousPost.headers().location).toContain('/Account/Login');

    await register(a, aliceName);
    // Duplicate login with another case is rejected across replicas.
    await b.goto('http://localhost:5002/Account/Register');
    await b.getByLabel('Логин', { exact: true }).fill(aliceName.toUpperCase());
    await b.getByLabel('Пароль', { exact: true }).fill(password);
    await b.getByLabel('Повторите пароль', { exact: true }).fill(password);
    await b.getByRole('button', { name: 'Зарегистрироваться' }).click();
    await expect(b.getByText('Этот логин уже занят.')).toBeVisible();
    await register(b, bobName);
    await login(a, aliceName, base + '/Account/Login', password + 'bad');
    await expect(a.getByText('Неверный логин или пароль.')).toBeVisible();
    await login(a, aliceName.toUpperCase(), base + '/Account/Login?ReturnUrl=https%3A%2F%2Fexample.com');
    await expect(a).toHaveURL(base + '/');
    await login(b, bobName);
    await expect(b).toHaveURL(base + '/');
    const cookie = (await alice.cookies()).find(c => c.name === 'Valuator.PA7.Auth');
    expect(cookie).toBeTruthy();
    expect(cookie.httpOnly).toBe(true);
    expect(cookie.sameSite).toBe('Lax');
    const bobCookie = (await bob.cookies()).find(c => c.name === cookie.name);
    const csrfFailure = await alice.request.post(base + '/', { form: { Text: 'without CSRF token' } });
    expect(csrfFailure.status()).toBe(400);

    let summaryPath;
    compose('stop', 'rankcalculator');
    try {
      // Attempt to supply Bob as author: the server must ignore these fields.
      await a.goto('http://localhost:5001/');
      const token = await a.locator('input[name="__RequestVerificationToken"]').last().inputValue();
      const posted = await alice.request.post('http://localhost:5002/', {
        form: { Text: 'Private text ' + randomUUID(), AuthorId: bobName, Author: bobName, __RequestVerificationToken: token }, maxRedirects: 0
      });
      expect(posted.status()).toBe(302);
      summaryPath = posted.headers().location;
      await a.goto(base + summaryPath);
      await expect(a.locator('#pending')).toBeVisible();
      for (const endpoint of [base, 'http://localhost:5001', 'http://localhost:5002']) {
        const denied = await bob.request.get(endpoint + summaryPath);
        expect(denied.status()).toBe(404);
        expect(await denied.text()).not.toContain('id="similarity"');
        const anon = await anonymous.request.get(endpoint + summaryPath, { maxRedirects: 0 });
        expect(anon.status()).toBe(302);
      }
    } finally { compose('start', 'rankcalculator'); }

    await expect(a.locator('#rank')).toBeVisible({ timeout: 60000 });
    const expectedRank = await a.locator('#rank').textContent();
    for (const endpoint of [base, 'http://localhost:5001', 'http://localhost:5002']) {
      await a.goto(endpoint + summaryPath);
      await expect(a.locator('#rank')).toHaveText(expectedRank);
      const own = await alice.request.get(endpoint + summaryPath);
      expect(own.headers()['cache-control']).toContain('no-store');
      expect((await bob.request.get(endpoint + summaryPath + '&AuthorId=' + bobName)).status()).toBe(404);
    }
    // A modified authentication ticket must not authenticate.
    await anonymous.addCookies([{ ...cookie, value: cookie.value.slice(0, 20) + (cookie.value[20] === 'A' ? 'B' : 'A') + cookie.value.slice(21) }]);
    expect((await anonymous.request.get(base + summaryPath, { maxRedirects: 0 })).status()).toBe(302);
    expect(bobCookie.value).not.toBe(cookie.value);
    // GET does not log out; POST must carry antiforgery protection.
    await a.goto(base + '/Account/Logout');
    expect((await alice.request.get(base + summaryPath)).status()).toBe(200);
    expect((await alice.request.post(base + '/Account/Logout')).status()).toBe(400);
    await a.getByRole('button', { name: 'Выйти', exact: true }).last().click();
    await expect(a).toHaveURL(base + '/Account/Login');
    expect((await alice.request.get(base + summaryPath, { maxRedirects: 0 })).status()).toBe(302);
    await login(a, aliceName);
    await a.goto(base + summaryPath);
    await expect(a.locator('#rank')).toBeVisible();
  } finally { await alice.close(); await bob.close(); await anonymous.close(); }
});
