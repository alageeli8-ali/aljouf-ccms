-- V001: مخطط لكل وحدة (ADR-008) وصلاحيات حساب التطبيق.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ccms_app') THEN
    RAISE EXCEPTION 'الدور ccms_app غير موجود؛ شغّل db/bootstrap.sql أولًا';
  END IF;
END $$;

CREATE SCHEMA IF NOT EXISTS iam;
CREATE SCHEMA IF NOT EXISTS org;
CREATE SCHEMA IF NOT EXISTS bodies;
CREATE SCHEMA IF NOT EXISTS meetings;
CREATE SCHEMA IF NOT EXISTS minutes;
CREATE SCHEMA IF NOT EXISTS resolutions;
CREATE SCHEMA IF NOT EXISTS tasks;
CREATE SCHEMA IF NOT EXISTS docs;
CREATE SCHEMA IF NOT EXISTS notify;
CREATE SCHEMA IF NOT EXISTS config;
CREATE SCHEMA IF NOT EXISTS audit;

DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY['iam','org','bodies','meetings','minutes','resolutions','tasks','docs','notify','config','audit'] LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO ccms_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ccms_app', s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO ccms_app', s);
  END LOOP;
END $$;

-- دالة مساعدة لتحديث updated_at
CREATE OR REPLACE FUNCTION public.touch_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END $$;
