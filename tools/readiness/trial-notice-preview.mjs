// معاينة تنبيه بيئة التجربة: يُستبدل رد /system/info بالبيئة Trial (الخادم المحلي بيئة تطوير؛ بيئة Trial الفعلية تتطلب Npgsql)
import { chromium } from 'playwright';
const BASE = 'http://localhost:5000', OUT = '/home/claude/aljouf-ccms/docs/screenshots/trial';
const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
const vps = { desktop: { viewport: { width: 1440, height: 900 } }, mobile: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 } };
for (const [vp, opt] of Object.entries(vps)) for (const lang of ['ar', 'en']) {
  const ctx = await b.newContext(opt);
  await ctx.addInitScript((l) => { try { localStorage.setItem('ccms.lang', l); } catch {} }, lang);
  await ctx.route('**/api/v1/system/info', async (r) => { const res = await r.fetch(); const j = await res.json(); j.environment = 'Trial'; await r.fulfill({ response: res, json: j }); });
  const p = await ctx.newPage();
  await p.goto(BASE + '/login', { waitUntil: 'networkidle' }); await p.waitForTimeout(500);
  await p.screenshot({ path: `${OUT}/${vp}-${lang}-login.png`, fullPage: vp === 'mobile' });
  const csrf = await (await ctx.request.get(`${BASE}/api/v1/auth/csrf`)).json();
  await ctx.request.post(`${BASE}/api/v1/auth/login`, { data: { email: 'secretary@sample.jsdo.test', password: 'Ccms@Sample2026' }, headers: { 'X-CSRF-Token': csrf.token } });
  await p.goto(BASE + '/', { waitUntil: 'networkidle' }); await p.waitForTimeout(500);
  const tag = await p.locator('.app-header .ant-tag').first().innerText();
  await p.screenshot({ path: `${OUT}/${vp}-${lang}-header.png` });
  console.log(vp, lang, 'tag:', tag, 'overflow:', await p.evaluate(() => document.documentElement.scrollWidth - innerWidth));
  await ctx.close();
}
await b.close();
