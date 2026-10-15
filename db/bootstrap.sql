-- تهيئة قاعدة البيانات وأدوارها — يشغّلها مسؤول قاعدة البيانات مرة واحدة بحساب مدير (superuser).
-- الاستخدام:
--   psql -h <host> -U postgres -v owner_pw=... -v app_pw=... -v db=ccms -f db/bootstrap.sql
--
-- ccms_owner : يملك المخططات وتُشغَّل به الترحيلات وقت النشر فقط.
-- ccms_app   : حساب التطبيق وقت التشغيل؛ بلا صلاحيات DDL، وبلا UPDATE/DELETE على سجلات التدقيق (FR-AUD-05).

\set ON_ERROR_STOP on

SELECT format('CREATE ROLE ccms_owner LOGIN PASSWORD %L', :'owner_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ccms_owner') \gexec
SELECT format('CREATE ROLE ccms_app LOGIN PASSWORD %L', :'app_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ccms_app') \gexec

SELECT format('CREATE DATABASE %I OWNER ccms_owner ENCODING ''UTF8'' TEMPLATE template0', :'db')
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'db') \gexec

\connect :db
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO ccms_owner;
GRANT USAGE ON SCHEMA public TO ccms_app;
GRANT CONNECT ON DATABASE :"db" TO ccms_app;
