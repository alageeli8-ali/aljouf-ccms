#!/usr/bin/env bash
# تهيئة بيئة التجربة في GitHub Codespaces (25 §11). يُشغَّل آليًا عند إنشاء المساحة، ويمكن إعادة تشغيله بأمان.
#   1) يولّد .env بأسرار عشوائية وكلمة مرور للحسابات التجريبية (لا تُرفع إلى المستودع)
#   2) يبني صورة الإنتاج (Npgsql) من Dockerfile
#   3) ينشئ القاعدة وأدوارها، ويطبق الترحيلات، ويضيف البيانات الوهمية، ويشغّل التطبيق
#   4) يشغّل فحص الدخان ويطبع الرابط ومكان كلمة المرور
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"
COMPOSE=(docker compose -f docker-compose.yml -f deploy/trial/docker-compose.trial.yml)
CRED="$ROOT/deploy/trial/.trial-credentials"
log() { printf '\n\033[1;32m== %s\033[0m\n' "$*"; }

rand() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c "${1:-32}"; }

if [[ -z "${CODESPACE_NAME:-}" || -z "${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-}" ]]; then
  HOSTNAME_TRIAL="${CCMS_HOSTNAME:-localhost}"
  echo "تنبيه: خارج Codespaces — اسم المضيف: $HOSTNAME_TRIAL"
else
  HOSTNAME_TRIAL="${CODESPACE_NAME}-8080.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}"
fi

log "1/6 الأسرار"
if [[ ! -f .env ]]; then
  umask 077
  TRIAL_PASSWORD="Trial-$(rand 16)"
  cat > .env <<EOF
# مولّد آليًا لبيئة التجربة — لا يُرفع إلى المستودع (.gitignore)
CCMS_VERSION=trial
CCMS_ENVIRONMENT=Trial
CCMS_HOSTNAME=$HOSTNAME_TRIAL
PG_SUPERUSER_PASSWORD=$(rand 32)
CCMS_OWNER_PASSWORD=$(rand 32)
CCMS_APP_PASSWORD=$(rand 32)
CCMS_SECRETS_MASTER_KEY=$(openssl rand -base64 32)
CCMS_MOCK_CALLBACK_SECRET=$(rand 48)
LOCAL_LOGIN_ENABLED=true
EMAIL_PROVIDER=none
BACKUP_PASSPHRASE_FILE=$ROOT/deploy/trial/.backup-pass
TRIAL_PASSWORD=$TRIAL_PASSWORD
EOF
  rand 48 > deploy/trial/.backup-pass
  echo "تم توليد .env"
else
  # المساحة أعيد فتحها باسم جديد أحيانًا: حدّث اسم المضيف فقط
  sed -i "s|^CCMS_HOSTNAME=.*|CCMS_HOSTNAME=$HOSTNAME_TRIAL|" .env
  echo ".env موجود — حُدّث اسم المضيف"
fi
set -a; source .env; set +a

log "2/6 بناء صورة الإنتاج (أول مرة: 5–10 دقائق)"
"${COMPOSE[@]}" build app

log "3/6 قاعدة البيانات"
"${COMPOSE[@]}" up -d db
for i in $(seq 1 60); do
  "${COMPOSE[@]}" exec -T db pg_isready -U postgres >/dev/null 2>&1 && break
  sleep 2
done
"${COMPOSE[@]}" exec -T db psql -q -U postgres -v owner_pw="$CCMS_OWNER_PASSWORD" -v app_pw="$CCMS_APP_PASSWORD" -v db=ccms -f /bootstrap.sql

log "4/6 الترحيلات"
"${COMPOSE[@]}" run --rm migrate

log "5/6 البيانات الوهمية والتشغيل"
"${COMPOSE[@]}" run --rm --no-deps -e ASPNETCORE_ENVIRONMENT=Trial app seed-sample --password "$TRIAL_PASSWORD"
"${COMPOSE[@]}" up -d app
for i in $(seq 1 60); do
  [[ "$(docker inspect -f '{{.State.Health.Status}}' "$("${COMPOSE[@]}" ps -q app)" 2>/dev/null)" == healthy ]] && break
  sleep 3
done
"${COMPOSE[@]}" ps

log "6/6 فحص الدخان"
bash deploy/trial/smoke.sh "http://localhost:8080" "$TRIAL_PASSWORD" "$HOSTNAME_TRIAL"

umask 077
cat > "$CRED" <<EOF
رابط التجربة: https://$HOSTNAME_TRIAL/
كلمة المرور لكل الحسابات التجريبية: $TRIAL_PASSWORD
الحسابات (البريد <الاسم>@sample.jsdo.test):
  governance مسؤول المجالس     admin     مدير النظام       ceo       الإدارة العليا
  chair      رئيس اللجنة       secretary أمين السر         member    عضو
  auditor    المدقق            manager   مدير إدارة        executor  موظف مكلف
  external   ممثل جهة خارجية (يعمل عند تفعيل البوابة)
EOF
log "جاهز"
cat "$CRED"
echo
echo "المنفذ خاص بحسابك في GitHub افتراضيًا. للمشاركة: تبويب PORTS ← 8080 ← Port Visibility ← Public (25 §11)."
