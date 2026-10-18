import { chromium } from 'playwright';
const BASE='http://127.0.0.1:5000';
const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
const ctx = await b.newContext({ viewport:{width:390,height:844}, isMobile:true, hasTouch:true, deviceScaleFactor:2 });
const csrf = await (await ctx.request.get(`${BASE}/api/v1/auth/csrf`)).json();
await ctx.request.post(`${BASE}/api/v1/auth/login`, { data:{ email:'governance@sample.jsdo.test', password:'Ccms@Sample2026' }, headers:{'X-CSRF-Token':csrf.token} });
for (const lang of ['ar','en']) {
  await ctx.addInitScript((l)=>{try{localStorage.setItem('ccms.lang',l)}catch{}}, lang);
  const p = await ctx.newPage(); await p.goto(BASE+'/', {waitUntil:'networkidle'});
  await p.locator('.app-header button').first().click(); await p.waitForTimeout(500);
  await p.screenshot({ path:`/home/claude/aljouf-ccms/docs/screenshots/responsive/mobile-${lang}-nav-drawer.png` });
  await p.locator('.ant-drawer a[href="/meetings"]').click(); await p.waitForTimeout(800);
  console.log(lang, p.url(), 'drawer visible after nav:', await p.locator('.ant-drawer-content-wrapper').isVisible().catch(()=>false));
  await p.close();
}
await b.close();
