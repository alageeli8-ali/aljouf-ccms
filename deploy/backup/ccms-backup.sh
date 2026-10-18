#!/usr/bin/env bash
# نسخة احتياطية كاملة لنظام إدارة المجالس واللجان (الوثيقة 25 §3).
# تشمل: قاعدة البيانات (pg_dump بصيغة custom)، ومجلد المستندات، ومفاتيح حماية البيانات، وملف الإعدادات غير السرية.
# الناتج مشفر (AES-256 عبر openssl، PBKDF2) بعبارة مرور من ملف خارج المستودع، مع بيان بصمات SHA-256 وأعداد الصفوف.
#
# المتغيرات (من البيئة أو /etc/ccms/backup.env):
#   PGHOST PGPORT PGUSER PGDATABASE PGPASSWORD   حساب المالك (ccms_owner) أو حساب قراءة مخصص للنسخ
#   CCMS_STORAGE        مجلد المستندات (افتراضي /var/lib/ccms/storage)
#   CCMS_KEYS           مجلد مفاتيح حماية البيانات (افتراضي /var/lib/ccms/keys)
#   CCMS_CONFIG         ملف إعدادات غير سرية يُحفظ مع النسخة (اختياري)
#   BACKUP_DIR          مجلد النسخ (افتراضي /var/backups/ccms) — يُنسخ بعدها إلى موقع خارج الخادم
#   BACKUP_PASSPHRASE_FILE  ملف عبارة التشفير (إلزامي؛ صلاحية 600؛ تُحفظ نسخة منه في خزنة المكتب)
#   KEEP_DAILY KEEP_WEEKLY KEEP_MONTHLY  الاحتفاظ (افتراضي 14 / 8 / 12)
set -euo pipefail
umask 077

[[ -f /etc/ccms/backup.env ]] && source /etc/ccms/backup.env
: "${PGDATABASE:?PGDATABASE مطلوب}"
: "${BACKUP_PASSPHRASE_FILE:?BACKUP_PASSPHRASE_FILE مطلوب}"
CCMS_STORAGE="${CCMS_STORAGE:-/var/lib/ccms/storage}"
CCMS_KEYS="${CCMS_KEYS:-/var/lib/ccms/keys}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/ccms}"
KEEP_DAILY="${KEEP_DAILY:-14}"; KEEP_WEEKLY="${KEEP_WEEKLY:-8}"; KEEP_MONTHLY="${KEEP_MONTHLY:-12}"

[[ -r "$BACKUP_PASSPHRASE_FILE" ]] || { echo "تعذر قراءة ملف عبارة التشفير" >&2; exit 2; }
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
NAME="ccms-$STAMP"
mkdir -p "$WORK/$NAME" "$BACKUP_DIR"

echo "[$(date -u +%T)] تفريغ قاعدة البيانات $PGDATABASE"
pg_dump --format=custom --compress=6 --no-owner --file "$WORK/$NAME/db.dump" "$PGDATABASE"
# أعداد صفوف الجداول الحرجة للتحقق بعد الاستعادة
psql -X -At -v ON_ERROR_STOP=1 "$PGDATABASE" > "$WORK/$NAME/rowcounts.txt" <<'SQL'
SELECT 'iam.users', count(*) FROM iam.users UNION ALL
SELECT 'bodies.bodies', count(*) FROM bodies.bodies UNION ALL
SELECT 'meetings.meetings', count(*) FROM meetings.meetings UNION ALL
SELECT 'minutes.minutes', count(*) FROM minutes.minutes UNION ALL
SELECT 'minutes.minutes_versions', count(*) FROM minutes.minutes_versions UNION ALL
SELECT 'resolutions.resolutions', count(*) FROM resolutions.resolutions UNION ALL
SELECT 'tasks.tasks', count(*) FROM tasks.tasks UNION ALL
SELECT 'docs.documents', count(*) FROM docs.documents UNION ALL
SELECT 'sig.requests', count(*) FROM sig.requests UNION ALL
SELECT 'sig.signers', count(*) FROM sig.signers UNION ALL
SELECT 'ai.outputs', count(*) FROM ai.outputs UNION ALL
SELECT 'audit.audit_events', count(*) FROM audit.audit_events UNION ALL
SELECT 'config.feature_flags', count(*) FROM config.feature_flags;
SQL
psql -X -At -v ON_ERROR_STOP=1 -c "SELECT max(chain_seq) || '|' || (SELECT hash FROM audit.audit_events ORDER BY chain_seq DESC LIMIT 1) FROM audit.audit_events" "$PGDATABASE" > "$WORK/$NAME/audit_head.txt"

echo "[$(date -u +%T)] أرشفة المستندات والمفاتيح"
tar -C "$CCMS_STORAGE" -cf "$WORK/$NAME/storage.tar" .
[[ -d "$CCMS_KEYS" ]] && tar -C "$CCMS_KEYS" -cf "$WORK/$NAME/keys.tar" .
[[ -n "${CCMS_CONFIG:-}" && -f "$CCMS_CONFIG" ]] && cp "$CCMS_CONFIG" "$WORK/$NAME/appsettings.snapshot.json"
{
  echo "created_utc=$STAMP"
  echo "database=$PGDATABASE"
  echo "pg_dump=$(pg_dump --version)"
  echo "host=$(hostname)"
} > "$WORK/$NAME/manifest.txt"
( cd "$WORK/$NAME" && sha256sum -- * > SHA256SUMS )

echo "[$(date -u +%T)] التشفير"
tar -C "$WORK" -cf - "$NAME" | openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass "file:$BACKUP_PASSPHRASE_FILE" -out "$BACKUP_DIR/$NAME.tar.enc"
sha256sum "$BACKUP_DIR/$NAME.tar.enc" > "$BACKUP_DIR/$NAME.tar.enc.sha256"

# الاحتفاظ: آخر N يومية، وأحدث نسخة لكل أسبوع لآخر N أسابيع، ولكل شهر لآخر N أشهر
mapfile -t ALL < <(ls -1 "$BACKUP_DIR"/ccms-*.tar.enc 2>/dev/null | sort -r)
declare -A KEEP=()
i=0; for f in "${ALL[@]}"; do (( i < KEEP_DAILY )) && KEEP["$f"]=1; i=$((i+1)); done
declare -A W=() M=()
for f in "${ALL[@]}"; do
  d="$(basename "$f" | sed -E 's/ccms-([0-9]{8}).*/\1/')"
  wk="$(date -u -d "$d" +%G-W%V)"; mo="${d:0:6}"
  if [[ -z "${W[$wk]:-}" && ${#W[@]} -lt $KEEP_WEEKLY ]]; then W[$wk]=1; KEEP["$f"]=1; fi
  if [[ -z "${M[$mo]:-}" && ${#M[@]} -lt $KEEP_MONTHLY ]]; then M[$mo]=1; KEEP["$f"]=1; fi
done
for f in "${ALL[@]}"; do [[ -n "${KEEP[$f]:-}" ]] || rm -f -- "$f" "$f.sha256"; done

echo "[$(date -u +%T)] اكتملت النسخة: $BACKUP_DIR/$NAME.tar.enc ($(du -h "$BACKUP_DIR/$NAME.tar.enc" | cut -f1))"
