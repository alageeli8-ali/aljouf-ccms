"""حمل واقعي: 30 جلسة متزامنة بزمن تفكير 1–3 ث، ومزيج استخدام يومي (قوائم ثم بحث ولوحة وتقرير أحيانًا)."""
import random, statistics, time, json, concurrent.futures as cf
import requests
exec(open('load.py').read().split("rows = []")[0])
weights = [('قائمة الاجتماعات (صفحة 1)', 4), ('قائمة المهام (صفحة 1)', 5), ('قائمة المهام المتأخرة', 2), ('قائمة القرارات', 3), ('قائمة الجهات', 2),
           ('البحث الموحد (كلمتان)', 1), ('البحث برقم قرار', 1), ('لوحة الإدارة العليا (5 سنوات، كل الجهات)', 1), ('تقرير الالتزام JSON', 1),
           ('سجل المستندات للسجلات', 1), ('لوحة المعلومات الشخصية', 4)]
byname = {e[0]: e for e in endpoints}
pool = [byname[n] for n, w in weights for _ in range(w)]
def user(i):
    rnd = random.Random(i); out = []
    for _ in range(12):
        name, s, path = rnd.choice(pool)
        out.append((name, timed(s, path)[0]))
        time.sleep(rnd.uniform(1, 3))
    return out
t0 = time.perf_counter()
with cf.ThreadPoolExecutor(30) as ex: res = [x for r in ex.map(user, range(30)) for x in r]
wall = time.perf_counter() - t0
xs = sorted(x for _, x in res)
print(f'حمل واقعي: {len(xs)} طلب من 30 جلسة بزمن تفكير خلال {wall:.0f} ث ({len(xs)/wall:.1f} طلب/ث) — p50={statistics.median(xs):.0f} ms p95={xs[int(len(xs)*.95)-1]:.0f} ms max={xs[-1]:.0f} ms')
json.dump({'n': len(xs), 'wall': wall, 'p50': statistics.median(xs), 'p95': xs[int(len(xs)*.95)-1], 'max': xs[-1]}, open('result2.json', 'w'))
