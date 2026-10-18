// اختبار الواجهة المتجاوبة (24 §7): 11 صفحة × (حاسب، لوحي، جوال) × (عربي، إنجليزي) على بيانات seed-sample.
// يقيس التمرير الأفقي غير المقصود وأخطاء الصفحة، ويلتقط لقطات مختارة. الاستخدام: node responsive-check.mjs <meetingId> <minutesId> <taskId> <bodyId> (يتطلب playwright وخادم تطوير على 5000)
import { chromium } from 'playwright';
import fs from 'node:fs';

const BASE = 'http://127.0.0.1:5000';
const OUT = '/home/claude/aljouf-ccms/docs/screenshots/responsive';
fs.mkdirSync(OUT, { recursive: true });
const [MEETING, MINUTES, TASK, BODY] = process.argv.slice(2);
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });

const viewports = {
  desktop: { width: 1440, height: 900, isMobile: false },
  tablet: { width: 820, height: 1180, isMobile: true, hasTouch: true },
  mobile: { width: 390, height: 844, isMobile: true, hasTouch: true, deviceScaleFactor: 2 },
};
const pages = [
  ['dashboard', '/'], ['meetings', '/meetings'], ['meeting', `/meetings/${MEETING}`], ['minutes', `/minutes/${MINUTES}`], ['tasks', '/tasks'],
  ['task', `/tasks/${TASK}`], ['body', `/bodies/${BODY}`], ['reports', '/reports'], ['search', '/search'], ['records', '/records'], ['integrations', '/integrations'],
];
const capture = new Set(['dashboard', 'meeting', 'tasks', 'task', 'reports', 'minutes']);

const states = {};
for (const who of ['governance', 'admin']) {
  const ctx = await browser.newContext();
  const csrf = await (await ctx.request.get(`${BASE}/api/v1/auth/csrf`)).json();
  const r = await ctx.request.post(`${BASE}/api/v1/auth/login`, { data: { email: `${who}@sample.jsdo.test`, password: 'Ccms@Sample2026' }, headers: { 'X-CSRF-Token': csrf.token } });
  if (!r.ok()) throw new Error(`login ${who} ${r.status()}`);
  states[who] = await ctx.storageState();
  await ctx.close();
}

const results = [];
for (const [vpName, vp] of Object.entries(viewports)) {
  for (const lang of ['ar', 'en']) {
    for (const [name, path] of pages) {
      const who = name === 'integrations' ? 'admin' : 'governance';
      const ctx = await browser.newContext({ ...vp, viewport: { width: vp.width, height: vp.height }, locale: lang === 'ar' ? 'ar-SA' : 'en-US', storageState: states[who] });
      await ctx.addInitScript((l) => { try { localStorage.setItem('ccms.lang', l); } catch { /* */ } }, lang);
      const page = await ctx.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));
      const t0 = Date.now();
      await page.goto(BASE + path, { waitUntil: 'networkidle' });
      await page.waitForTimeout(700);
      const load = Date.now() - t0;
      const m = await page.evaluate(() => ({
        overflow: document.documentElement.scrollWidth - window.innerWidth,
        dir: document.documentElement.dir,
        blank: document.body.innerText.trim().length < 20,
        // عناصر نصية مقصوصة خارج عرض الشاشة (ليست داخل حاوية تمرير أفقي مقصودة كالجداول واللوحات)
        clipped: (() => {
          const W = window.innerWidth; const out = [];
          // المساحة المرئية = تقاطع العنصر مع كل أصل يقصّ المحتوى (overflow ≠ visible)؛ القصّ داخل حاوية مقصودة (تبويبات بقائمة «…»، نص مختصر) ليس عيبًا.
          for (const el of document.querySelectorAll('body *')) {
            if (el.children.length || !el.textContent.trim() || el.closest('.ant-drawer')) continue;
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            let left = r.left, right = r.right, scrolls = false;
            for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
              const ox = getComputedStyle(p).overflowX;
              if (ox === 'visible') continue;
              if ((ox === 'auto' || ox === 'scroll') && p.scrollWidth > p.clientWidth) { scrolls = true; break; }
              const pr = p.getBoundingClientRect(); left = Math.max(left, pr.left); right = Math.min(right, pr.right);
            }
            if (scrolls || right <= left) continue;
            if (right > W + 2 || left < -2) out.push(el.textContent.trim().slice(0, 30));
          }
          return out;
        })(),
        headerOverflow: (() => { const h = document.querySelector('.app-header'); return h ? h.scrollHeight > h.clientHeight + 1 || h.scrollWidth > h.clientWidth + 1 : false; })(),
      }));
      const ok = m.overflow <= 1 && m.clipped.length === 0 && !m.headerOverflow && errors.length === 0 && !m.blank && m.dir === (lang === 'ar' ? 'rtl' : 'ltr');
      results.push({ vp: vpName, lang, page: name, overflow: m.overflow, clipped: m.clipped.length, clippedSample: m.clipped.slice(0, 3), headerOverflow: m.headerOverflow, dir: m.dir, errors: errors.length, blank: m.blank, loadMs: load, ok });
      if (capture.has(name) && true) await page.screenshot({ path: `${OUT}/${vpName}-${lang}-${name}.png`, fullPage: vpName === 'mobile' });
      console.log(`${ok ? '✓' : '✗'} ${vpName.padEnd(7)} ${lang} ${name.padEnd(12)} overflow=${m.overflow}px clipped=${m.clipped.length}${m.clipped.length ? ' [' + m.clipped.slice(0, 3).join(' | ') + ']' : ''} hdr=${m.headerOverflow} dir=${m.dir} errors=${errors.length} load=${load}ms${errors.length ? ' ' + errors[0] : ''}`);
      await ctx.close();
    }
  }
}
fs.writeFileSync('responsive-result.json', JSON.stringify(results, null, 1));
console.log(`\n${results.filter((r) => r.ok).length}/${results.length} ناجح`);
await browser.close();
