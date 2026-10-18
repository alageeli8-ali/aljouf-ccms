"""قياس زمن الاستجابة على قاعدة الحجم الواقعي. الاستخدام: python3 load.py"""
import statistics, time, json, sys, concurrent.futures as cf
import requests
import os
BASE = os.environ.get('CCMS_BASE', 'http://127.0.0.1:5100')
def login(who):
    s = requests.Session()
    t = s.get(BASE + '/api/v1/auth/csrf').json()['token']
    r = s.post(BASE + '/api/v1/auth/login', json={'email': f'{who}@sample.jsdo.test', 'password': 'Ccms@Sample2026'}, headers={'X-CSRF-Token': t})
    assert r.ok, r.text
    return s
ceo, aud, gov = login('ceo'), login('auditor'), login('governance')
Y = 'from=2022-01-01&to=2026-12-31'
endpoints = [
    ('قائمة الاجتماعات (صفحة 1)', ceo, '/api/v1/meetings?pageSize=50'),
    ('قائمة المهام (صفحة 1)', ceo, '/api/v1/tasks?pageSize=50'),
    ('قائمة المهام المتأخرة', ceo, '/api/v1/tasks?pageSize=50&overdue=true'),
    ('قائمة القرارات', ceo, '/api/v1/resolutions?pageSize=50'),
    ('قائمة الجهات', ceo, '/api/v1/bodies?pageSize=50'),
    ('البحث الموحد (كلمتان)', ceo, '/api/v1/search?q=' + requests.utils.quote('خطة التحول')),
    ('البحث برقم قرار', ceo, '/api/v1/search?q=PF-17/40/2'),
    ('لوحة الإدارة العليا (5 سنوات، كل الجهات)', ceo, '/api/v1/reports/executive?' + Y),
    ('تقرير الاجتماعات JSON (5 سنوات)', ceo, '/api/v1/reports/run/meetings?' + Y),
    ('تقرير الالتزام JSON', ceo, '/api/v1/reports/run/compliance?' + Y),
    ('تقرير القرارات Excel', ceo, '/api/v1/reports/run/resolutions?format=xlsx&' + Y),
    ('تقرير أداء الجهات PDF', ceo, '/api/v1/reports/run/body_performance?format=pdf&' + Y),
    ('سجل التدقيق (صفحة 1 من 200 ألف)', aud, '/api/v1/audit/events?pageSize=50'),
    ('سجل المستندات للسجلات', gov, '/api/v1/records/register'),
    ('لوحة المعلومات الشخصية', ceo, '/api/v1/dashboard'),
]
def timed(s, path):
    t = time.perf_counter(); r = s.get(BASE + path); dt = (time.perf_counter() - t) * 1000
    if r.status_code != 200: raise SystemExit(f'{path} -> {r.status_code} {r.text[:200]}')
    return dt, len(r.content)
rows = []
for name, s, path in endpoints:
    timed(s, path)  # تسخين
    xs = []; size = 0
    for _ in range(10):
        dt, size = timed(s, path); xs.append(dt)
    xs.sort()
    rows.append((name, statistics.median(xs), xs[int(len(xs) * .9) - 1], xs[-1], size))
    print(f'{name:45s} p50={rows[-1][1]:7.0f} ms  p90={rows[-1][2]:7.0f} ms  max={rows[-1][3]:7.0f} ms  size={size/1024:7.0f} KB', flush=True)
# حمل متزامن: 20 مستخدمًا افتراضيًا × 15 طلبًا مختلطًا
mix = [e for e in endpoints if 'PDF' not in e[0] and 'Excel' not in e[0]]
def worker(i):
    out = []
    for j in range(15):
        name, s, path = mix[(i + j) % len(mix)]
        out.append(timed(s, path)[0])
    return out
t0 = time.perf_counter()
with cf.ThreadPoolExecutor(20) as ex: res = [x for r in ex.map(worker, range(20)) for x in r]
wall = time.perf_counter() - t0
res.sort()
print(f'\nحمل متزامن: {len(res)} طلب من 20 جلسة خلال {wall:.1f} ث ({len(res)/wall:.1f} طلب/ث) — p50={statistics.median(res):.0f} ms p95={res[int(len(res)*.95)-1]:.0f} ms max={res[-1]:.0f} ms')
json.dump({'rows': rows, 'concurrent': {'n': len(res), 'wall': wall, 'p50': statistics.median(res), 'p95': res[int(len(res)*.95)-1], 'max': res[-1]}}, open('perf-result.json', 'w'), ensure_ascii=False)
