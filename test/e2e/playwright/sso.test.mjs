import { chromium } from 'playwright';

const JF = process.env.JF_URL || 'http://jellyfin:8096';
const PROVIDER = process.env.PROVIDER || 'jellyfin';
const AK_USER = process.env.AK_USER || 'akadmin';
const AK_PASS = process.env.AK_PASS || 'akadmin-password';
const START = `${JF}/sso/OID/${'start'}/${PROVIDER}`;
const SHOT = '/work/shots';

function log(...a) { console.log('[e2e]', ...a); }

async function shot(page, name) {
  try { await page.screenshot({ path: `${SHOT}/${name}.png`, fullPage: true }); } catch {}
}

const browser = await chromium.launch({ args: ['--no-sandbox'] });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
page.on('console', m => log('PAGE:', m.type(), m.text()));
page.on('pageerror', e => log('PAGEERROR:', e.message));

let ok = false;
try {
  log('navigating to', START);
  await page.goto(START, { waitUntil: 'domcontentloaded', timeout: 45000 });
  await page.waitForTimeout(2000);
  log('after start, url =', page.url());
  await shot(page, '01-after-start');

  // authentik identification (username) stage
  const uid = page.locator('input[name="uidField"]');
  await uid.waitFor({ state: 'visible', timeout: 45000 });
  log('authentik identification stage visible');
  await uid.click();
  await uid.fill(AK_USER);
  await shot(page, '02-username');
  await page.getByRole('button', { name: /log in|continue/i }).first().click();

  // wait for the identification->password stage swap: the password stage shows
  // "<user> / Not you?" and a password field with this placeholder.
  await page.getByText('Not you?', { exact: false }).waitFor({ state: 'visible', timeout: 45000 });
  const pw = page.getByPlaceholder(/please enter your password/i);
  await pw.waitFor({ state: 'visible', timeout: 45000 });
  await pw.click();
  await pw.fill(AK_PASS);
  // verify the value actually landed before submitting (avoid stale-element race)
  for (let i = 0; i < 5 && (await pw.inputValue()) !== AK_PASS; i++) {
    await page.waitForTimeout(500);
    await pw.fill(AK_PASS);
  }
  log('password entered, value length =', (await pw.inputValue()).length);
  await shot(page, '03-password');
  await pw.press('Enter');

  // back to jellyfin origin
  log('waiting for redirect back to jellyfin...');
  await page.waitForURL(/\/\/jellyfin:8096\//, { timeout: 45000 });
  log('back on jellyfin, url =', page.url());
  await shot(page, '04-back-on-jellyfin');

  // plugin JS builds credentials and stores them, then redirects to /web
  await page.waitForFunction(() => {
    try {
      const c = JSON.parse(localStorage.getItem('jellyfin_credentials'));
      return !!(c && c.Servers && c.Servers[0] && c.Servers[0].AccessToken && c.Servers[0].UserId);
    } catch (e) { return false; }
  }, { timeout: 45000 });
  // Credentials are stored => SSO login succeeded. Mark success now; the read
  // below is best-effort because the plugin immediately navigates to /web,
  // which can destroy the evaluation context mid-read.
  ok = true;
  await shot(page, '05-logged-in');

  let server = null;
  for (let i = 0; i < 12 && !server; i++) {
    try {
      const creds = await page.evaluate(() => localStorage.getItem('jellyfin_credentials'));
      if (creds) {
        const parsed = JSON.parse(creds);
        if (parsed.Servers && parsed.Servers[0] && parsed.Servers[0].AccessToken) server = parsed.Servers[0];
      }
    } catch (e) { /* navigation in progress */ }
    if (!server) await page.waitForTimeout(500);
  }
  if (server) {
    log('SUCCESS: jellyfin_credentials set. UserId =', server.UserId, 'AccessToken len =', (server.AccessToken || '').length);
    console.log('E2E_RESULT ' + JSON.stringify({ userId: server.UserId, accessToken: server.AccessToken }));
  } else {
    log('SUCCESS: credentials were stored (confirmed by waitForFunction); details read raced with navigation.');
  }
} catch (err) {
  log('FAILURE:', err.message);
  await shot(page, '99-failure');
  try { const html = await page.content(); console.log('---PAGE HTML (first 1500)---\n' + html.slice(0, 1500)); } catch {}
} finally {
  await browser.close();
}
process.exit(ok ? 0 : 1);
