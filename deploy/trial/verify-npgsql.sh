#!/usr/bin/env bash
# تشغيل حزمة الاختبارات الكاملة (142 اختبارًا) بمشغّل الإنتاج Npgsql بدل موصل التطوير المصغّر (24 §10، R-22).
# لم يكن ممكنًا في بيئة التطوير دون اتصال بـ NuGet؛ يُشغَّل هنا في Codespaces مرة واحدة على الأقل قبل الاعتماد على التجربة.
# يستخدم قاعدة اختبار مستقلة في حاوية مؤقتة (لا يمس قاعدة التجربة)، ويكتب التقرير في docs/test-results/npgsql-run.md.
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"
PW="$(openssl rand -hex 16)"
NAME=ccms-npgsql-testdb

docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_PASSWORD="$PW" -p 127.0.0.1:5433:5432 postgres:16 >/dev/null
trap 'docker rm -f "$NAME" >/dev/null 2>&1 || true' EXIT
for i in $(seq 1 60); do docker exec "$NAME" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 2; done
sleep 2

cd src/backend
dotnet build Ccms.Tests -p:UseNpgsql=true -v q
CCMS_TEST_ADMIN_CS="Host=127.0.0.1;Port=5433;Database=postgres;Username=postgres;Password=$PW" \
  dotnet run --no-build --project Ccms.Tests -p:UseNpgsql=true -- "$ROOT/docs/test-results/npgsql-run.md"
echo "التقرير: docs/test-results/npgsql-run.md"
