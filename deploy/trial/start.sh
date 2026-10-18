#!/usr/bin/env bash
# يُشغَّل عند كل استئناف لمساحة Codespaces: يعيد تشغيل الحاويات (البيانات محفوظة في وحدات Docker داخل المساحة)،
# ويحدّث اسم المضيف إن تغير. أول إنشاء يتولاه setup.sh.
set -euo pipefail
cd "$(dirname "$0")/../.."
[[ -f .env ]] || { echo "لم تُهيأ التجربة بعد — شغّل: bash deploy/trial/setup.sh"; exit 0; }
if [[ -n "${CODESPACE_NAME:-}" && -n "${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-}" ]]; then
  sed -i "s|^CCMS_HOSTNAME=.*|CCMS_HOSTNAME=${CODESPACE_NAME}-8080.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}|" .env
fi
for i in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 2; done
docker compose -f docker-compose.yml -f deploy/trial/docker-compose.trial.yml up -d db app
echo "التجربة تعمل — الرابط وكلمة المرور في deploy/trial/.trial-credentials"
