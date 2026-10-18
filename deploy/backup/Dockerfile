# حاوية النسخ الاحتياطي: أدوات PostgreSQL 16 المطابقة لإصدار الخادم + openssl للتشفير (25 §3)
FROM postgres:16
RUN apt-get update && apt-get install -y --no-install-recommends openssl ca-certificates && rm -rf /var/lib/apt/lists/*
COPY ccms-backup.sh ccms-restore.sh ccms-restore-verify.sh /usr/local/bin/
RUN chmod 755 /usr/local/bin/ccms-*.sh
ENTRYPOINT ["/usr/local/bin/ccms-backup.sh"]
