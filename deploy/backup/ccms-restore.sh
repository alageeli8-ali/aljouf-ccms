#!/usr/bin/env bash
# استعادة نسخة احتياطية (الوثيقة 25 §4). لا تكتب أبدًا فوق قاعدة قائمة: تُستعاد إلى قاعدة جديدة باسم يحدده المسؤول،
# ثم يُحوّل التطبيق إليها بعد التحقق. المستندات تُستعاد إلى مجلد جديد فارغ.
#
# الاستخدام: ccms-restore.sh <ملف.tar.enc> <اسم القاعدة الجديدة> <مجلد المستندات الجديد> [مجلد المفاتيح الجديد]
# المتغيرات: PGHOST PGPORT PGUSER PGPASSWORD (حساب يملك CREATE DATABASE)، BACKUP_PASSPHRASE_FILE، RESTORE_OWNER (افتراضي ccms_owner)
set -euo pipefail
umask 077
[[ -f /etc/ccms/backup.env ]] && source /etc/ccms/backup.env
ARCHIVE="${1:?ملف النسخة}"; TARGET_DB="${2:?اسم القاعدة الجديدة}"; TARGET_STORAGE="${3:?مجلد المستندات الجديد}"; TARGET_KEYS="${4:-}"
: "${BACKUP_PASSPHRASE_FILE:?BACKUP_PASSPHRASE_FILE مطلوب}"
RESTORE_OWNER="${RESTORE_OWNER:-ccms_owner}"

if [[ -f "$ARCHIVE.sha256" ]]; then sha256sum -c "$ARCHIVE.sha256" >/dev/null || { echo "بصمة ملف النسخة لا تطابق — قد يكون تالفًا" >&2; exit 3; }; fi
if psql -X -At -c "SELECT 1 FROM pg_database WHERE datname = '$TARGET_DB'" postgres | grep -q 1; then
  echo "القاعدة $TARGET_DB موجودة — الاستعادة لا تكتب فوق قاعدة قائمة" >&2; exit 4
fi
if [[ -e "$TARGET_STORAGE" && -n "$(ls -A "$TARGET_STORAGE" 2>/dev/null)" ]]; then echo "مجلد المستندات الهدف غير فارغ" >&2; exit 5; fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass "file:$BACKUP_PASSPHRASE_FILE" -in "$ARCHIVE" | tar -C "$WORK" -xf -
DIR="$(find "$WORK" -mindepth 1 -maxdepth 1 -type d | head -1)"
( cd "$DIR" && sha256sum -c SHA256SUMS >/dev/null ) || { echo "بصمات محتوى النسخة لا تطابق البيان" >&2; exit 6; }
echo "سلامة النسخة: البصمات مطابقة ($(grep created_utc "$DIR/manifest.txt"))"

psql -X -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"$TARGET_DB\" OWNER $RESTORE_OWNER" postgres
pg_restore --exit-on-error --no-owner --role="$RESTORE_OWNER" --dbname "$TARGET_DB" "$DIR/db.dump"
mkdir -p "$TARGET_STORAGE"; tar -C "$TARGET_STORAGE" -xf "$DIR/storage.tar"
[[ -n "$TARGET_KEYS" && -f "$DIR/keys.tar" ]] && { mkdir -p "$TARGET_KEYS"; tar -C "$TARGET_KEYS" -xf "$DIR/keys.tar"; }
META="${TARGET_STORAGE%/}.restore-meta"; mkdir -p "$META"
cp "$DIR/rowcounts.txt" "$DIR/audit_head.txt" "$DIR/manifest.txt" "$META/"
echo "اكتملت الاستعادة إلى $TARGET_DB و$TARGET_STORAGE — شغّل: ccms-restore-verify.sh $TARGET_DB $TARGET_STORAGE $META"
