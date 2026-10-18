#!/usr/bin/env bash
# فحص دخان لبيئة التجربة بعد التشغيل (25 §11): يتحقق عبر الواجهة البرمجية الحقيقية أن الصورة (بمشغّل Npgsql) تعمل:
# الصحة، والدخول، والقوائم والتفاصيل، والبحث، واللوحة، والتصدير PDF وExcel، وكتابة فعلية، وسلامة سلسلة التدقيق، ورفض غير المخول.
# الاستخدام: smoke.sh <الأساس http://localhost:8080> <كلمة مرور الحسابات> [اسم المضيف العام]
# الطلبات المصادقة عبر localhost: الكعكات Secure خارج التطوير، وcurl يرسلها إلى localhost فقط عبر HTTP.
# اسم المضيف العام (Codespaces) يُفحص بطلب غير مصادق للتأكد من قبوله في AllowedHosts.
set -uo pipefail
BASE="${1:?الأساس}"; PW="${2:?كلمة المرور}"; HOST="${3:-}"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
pass=0; fail=0
ok()  { echo "  ✔ $*"; pass=$((pass + 1)); }
bad() { echo "  ✘ $*"; fail=$((fail + 1)); }
H=()

# req <جرة الكعكات> <الطريقة> <المسار> [جسم JSON] — يطبع رمز الحالة ويحفظ الجسم في $WORK/body
req() {
  local jar="$1" method="$2" path="$3" data="${4:-}" token=""
  if [[ "$method" != GET ]]; then
    token="$(curl -s "${H[@]}" -c "$jar" -b "$jar" "$BASE/api/v1/auth/csrf" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
  fi
  curl -s -o "$WORK/body" -w '%{http_code}' "${H[@]}" -c "$jar" -b "$jar" -X "$method" \
    ${token:+-H "X-CSRF-Token: $token"} ${data:+-H 'Content-Type: application/json' --data "$data"} "$BASE$path"
}
first_id() { grep -o '"id":"[0-9a-f-]\{36\}"' "$WORK/body" | head -1 | cut -d'"' -f4; }
expect() { local want="$1" got="$2" what="$3"; [[ "$got" == "$want" ]] && ok "$what ($got)" || { bad "$what: المتوقع $want والفعلي $got — $(head -c 300 "$WORK/body")"; }; }

echo "فحص الدخان على $BASE"
expect 200 "$(req "$WORK/anon" GET /health/ready)" "الجاهزية (قاعدة البيانات متصلة)"
if [[ -n "$HOST" ]]; then
  code="$(curl -s -o "$WORK/body" -w '%{http_code}' -H "Host: $HOST" "$BASE/health/ready")"
  expect 200 "$code" "اسم المضيف العام مقبول ($HOST)"
  code="$(curl -s -o "$WORK/body" -w '%{http_code}' -H "Host: evil.example.com" "$BASE/health/ready")"
  expect 400 "$code" "اسم مضيف غريب مرفوض"
fi
expect 200 "$(req "$WORK/anon" GET /api/v1/system/info)" "معلومات النظام"
grep -q '"environment":"Trial"' "$WORK/body" && ok "البيئة Trial" || bad "البيئة ليست Trial: $(cat "$WORK/body")"
expect 200 "$(req "$WORK/anon" GET /)" "الواجهة"
expect 401 "$(req "$WORK/anon" GET /api/v1/meetings)" "طلب دون جلسة مرفوض"
expect 401 "$(req "$WORK/bad" POST /api/v1/auth/login '{"email":"governance@sample.jsdo.test","password":"Ccms@Sample2026"}')" "الكلمة المنشورة في المستودع لا تعمل"

G="$WORK/gov"
expect 200 "$(req "$G" POST /api/v1/auth/login "{\"email\":\"governance@sample.jsdo.test\",\"password\":\"$PW\"}")" "دخول مسؤول المجالس"
expect 200 "$(req "$G" GET /api/v1/me)" "الحساب الحالي"
expect 200 "$(req "$G" GET /api/v1/bodies)" "قائمة الجهات"; BODY="$(first_id)"
[[ -n "$BODY" ]] && expect 200 "$(req "$G" GET "/api/v1/bodies/$BODY")" "تفاصيل الجهة" || bad "لا جهات في البيانات الوهمية"
expect 200 "$(req "$G" GET /api/v1/meetings)" "قائمة الاجتماعات"; MTG="$(first_id)"
[[ -n "$MTG" ]] && expect 200 "$(req "$G" GET "/api/v1/meetings/$MTG")" "تفاصيل الاجتماع"
expect 200 "$(req "$G" GET /api/v1/tasks)" "قائمة المهام"; TASK="$(first_id)"
[[ -n "$TASK" ]] && expect 200 "$(req "$G" GET "/api/v1/tasks/$TASK")" "تفاصيل المهمة"
expect 200 "$(req "$G" GET '/api/v1/tasks?overdue=true')" "المهام المتأخرة"
expect 200 "$(req "$G" GET /api/v1/resolutions)" "قائمة القرارات"
expect 200 "$(req "$G" GET /api/v1/minutes)" "قائمة المحاضر"
expect 200 "$(req "$G" GET '/api/v1/search?q=%D8%AE%D8%B7%D8%A9')" "البحث الموحد"
expect 200 "$(req "$G" GET /api/v1/dashboard)" "لوحة المعلومات"
expect 200 "$(req "$G" GET /api/v1/notifications)" "الإشعارات"
[[ -n "$TASK" ]] && expect 201 "$(req "$G" POST "/api/v1/tasks/$TASK/comments" '{"text":"فحص الدخان لبيئة التجربة","shareWithEntity":false}')" "كتابة: تعليق على مهمة"

C="$WORK/ceo"
expect 200 "$(req "$C" POST /api/v1/auth/login "{\"email\":\"ceo@sample.jsdo.test\",\"password\":\"$PW\"}")" "دخول الإدارة العليا"
expect 200 "$(req "$C" GET /api/v1/reports/executive)" "لوحة الإدارة العليا"
expect 200 "$(req "$C" GET '/api/v1/reports/run/meetings?format=pdf')" "تصدير PDF"
head -c 5 "$WORK/body" | grep -q '%PDF-' && ok "ملف PDF صالح" || bad "التصدير ليس PDF"
expect 200 "$(req "$C" GET '/api/v1/reports/run/resolutions?format=xlsx')" "تصدير Excel"
head -c 2 "$WORK/body" | grep -q 'PK' && ok "ملف Excel صالح" || bad "التصدير ليس xlsx"

A="$WORK/aud"
expect 200 "$(req "$A" POST /api/v1/auth/login "{\"email\":\"auditor@sample.jsdo.test\",\"password\":\"$PW\"}")" "دخول المدقق"
expect 200 "$(req "$A" GET /api/v1/audit/integrity)" "فحص سلامة سلسلة التدقيق"
grep -q '"intact":true' "$WORK/body" && ok "السلسلة سليمة" || bad "السلسلة: $(head -c 300 "$WORK/body")"

M="$WORK/mem"
expect 200 "$(req "$M" POST /api/v1/auth/login "{\"email\":\"member@sample.jsdo.test\",\"password\":\"$PW\"}")" "دخول عضو"
expect 403 "$(req "$M" POST /api/v1/bodies '{"code":"SMOKE-X","bodyType":"permanent_committee","nameAr":"محاولة غير مخولة","nameEn":"Unauthorized attempt"}')" "العضو لا ينشئ جهة"
expect 403 "$(req "$M" GET /api/v1/audit/events)" "العضو لا يرى سجل التدقيق"

echo
echo "النتيجة: $pass ناجح، $fail فاشل"
[[ $fail -eq 0 ]]
