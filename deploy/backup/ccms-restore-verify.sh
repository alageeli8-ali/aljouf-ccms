#!/usr/bin/env bash
# التحقق من نسخة مستعادة قبل اعتمادها (الوثيقة 25 §5). «وجود ملف النسخة ليس دليلًا على إمكانية الاستعادة»:
# هذا السكربت يثبت أن القاعدة المستعادة مكتملة وسليمة وأن الملفات مطابقة لبصماتها وأن التطبيق يعمل عليها.
#
# الاستخدام: ccms-restore-verify.sh <القاعدة المستعادة> <مجلد المستندات المستعاد> <مجلد بيانات الاستعادة> [مسار الترحيلات]
# اختياري: CCMS_HOST_DLL (مسار Ccms.Host.dll) و CCMS_APP_CS (سلسلة اتصال حساب التطبيق بالقاعدة المستعادة) لتشغيل التطبيق عليها والتحقق من جاهزيته.
set -uo pipefail
DB="${1:?القاعدة}"; STORAGE="${2:?مجلد المستندات}"; META="${3:?مجلد البيانات}"; MIGRATIONS="${4:-}"
fail=0; ok() { echo "  ✔ $*"; }; bad() { echo "  ✘ $*"; fail=1; }
q() { psql -X -At -v ON_ERROR_STOP=1 -c "$1" "$DB"; }

echo "1) أعداد الصفوف مقارنة ببيان النسخة"
while IFS='|' read -r table expected; do
  actual="$(q "SELECT count(*) FROM $table")"
  [[ "$actual" == "$expected" ]] && ok "$table = $actual" || bad "$table: المتوقع $expected والفعلي $actual"
done < "$META/rowcounts.txt"

echo "2) سلسلة التدقيق"
broken="$(q "SELECT coalesce(first_broken_seq::text, 'none') FROM audit.verify_chain()")"
[[ "$broken" == "none" ]] && ok "السلسلة سليمة" || bad "السلسلة مكسورة عند $broken"
head_now="$(q "SELECT max(chain_seq) || '|' || (SELECT hash FROM audit.audit_events ORDER BY chain_seq DESC LIMIT 1) FROM audit.audit_events")"
[[ "$head_now" == "$(cat "$META/audit_head.txt")" ]] && ok "آخر حدث وبصمته مطابقان لوقت النسخ" || bad "رأس السلسلة لا يطابق"

echo "3) المستندات: وجود كل ملف ومطابقة بصمته"
total=0; missing=0; mismatch=0
while IFS='|' read -r key sha; do
  total=$((total+1))
  f="$STORAGE/$key"
  if [[ ! -f "$f" ]]; then missing=$((missing+1)); echo "     مفقود: $key"; continue; fi
  [[ "$(sha256sum "$f" | cut -d' ' -f1)" == "$sha" ]] || { mismatch=$((mismatch+1)); echo "     بصمة مختلفة: $key"; }
done < <(q "SELECT storage_key, sha256 FROM docs.documents WHERE lifecycle_status <> 'destroyed' AND deleted_at IS NULL")
(( missing == 0 && mismatch == 0 )) && ok "$total ملف موجود ومطابق" || bad "$missing مفقود و$mismatch مختلف من $total"
files_on_disk="$(find "$STORAGE" -type f | wc -l)"
referenced="$(q "SELECT count(*) FROM docs.documents WHERE deleted_at IS NULL")"
(( files_on_disk > referenced )) && echo "  ⓘ $((files_on_disk - referenced)) ملف في التخزين بلا سجل (يتيم) — لا يؤثر في الاستعادة؛ راجعه بفحص التخزين الدوري"
destroyed="$(q "SELECT count(*) FROM docs.documents WHERE lifecycle_status = 'destroyed'")"
ok "المتلف وفق الاحتفاظ ($destroyed) سجله الوصفي باقٍ دون ملف — متوقع"

echo "4) الترحيلات"
applied="$(q "SELECT count(*) FROM public.schema_migrations" 2>/dev/null || echo "?")"
if [[ -n "$MIGRATIONS" ]]; then
  files="$(ls -1 "$MIGRATIONS"/V*.sql | wc -l)"
  [[ "$applied" == "$files" ]] && ok "مطبق $applied من $files" || bad "مطبق $applied ومتاح $files"
else ok "مطبق $applied"; fi

echo "5) الإعدادات والأسرار"
flags="$(q "SELECT count(*) FROM config.feature_flags")"; ok "$flags مفتاح إعداد محفوظ"
secrets="$(q "SELECT count(*) FROM config.secrets")"; ok "$secrets سر مشفر (يُفك فقط بالمفتاح الرئيسي نفسه Secrets:MasterKey)"

if [[ -n "${CCMS_HOST_DLL:-}" && -n "${CCMS_APP_CS:-}" ]]; then
  echo "6) تشغيل التطبيق على النسخة المستعادة"
  port=$(( 20000 + RANDOM % 20000 ))
  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$port" ConnectionStrings__App="$CCMS_APP_CS" Storage__Path="$STORAGE" \
    Workers__Enabled=false dotnet "$CCMS_HOST_DLL" > "$META/app.log" 2>&1 &
  pid=$!
  for _ in $(seq 1 30); do curl -fs "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1 && break; sleep 1; done
  if curl -fs "http://127.0.0.1:$port/health/ready" | grep -q ready; then ok "التطبيق جاهز على القاعدة المستعادة"; else bad "التطبيق لم يصبح جاهزًا (انظر $META/app.log)"; fi
  curl -fs "http://127.0.0.1:$port/api/v1/system/info" >/dev/null && ok "الواجهة البرمجية تستجيب" || bad "الواجهة البرمجية لا تستجيب"
  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
fi

echo
(( fail == 0 )) && echo "نتيجة التحقق: ناجح — النسخة قابلة للاستعادة" || echo "نتيجة التحقق: فاشل — لا تُعتمد النسخة"
exit $fail
