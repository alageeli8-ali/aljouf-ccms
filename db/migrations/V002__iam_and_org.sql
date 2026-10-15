-- V002: الهوية والهيكل التنظيمي (05 §3.1)

CREATE TABLE org.org_units (
  id               uuid PRIMARY KEY,
  code             text NOT NULL UNIQUE,
  name_ar          text NOT NULL,
  name_en          text,
  parent_id        uuid REFERENCES org.org_units(id),
  manager_user_id  uuid,
  external_ref     text,
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE iam.users (
  id                  uuid PRIMARY KEY,
  entra_object_id     text UNIQUE,
  email               text NOT NULL,
  display_name_ar     text NOT NULL,
  display_name_en     text,
  job_title           text,
  user_type           text NOT NULL DEFAULT 'internal' CHECK (user_type IN ('internal','guest','external','local_breakglass')),
  org_unit_id         uuid REFERENCES org.org_units(id),
  external_entity_id  uuid,
  clearance           text NOT NULL DEFAULT 'restricted' CHECK (clearance IN ('public','restricted','secret','top_secret')),
  preferred_language  text NOT NULL DEFAULT 'ar' CHECK (preferred_language IN ('ar','en')),
  preferred_calendar  text NOT NULL DEFAULT 'gregorian' CHECK (preferred_calendar IN ('gregorian','hijri')),
  status              text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled')),
  last_login_at       timestamptz,
  created_at          timestamptz NOT NULL DEFAULT now(),
  created_by          uuid,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  updated_by          uuid
);
CREATE UNIQUE INDEX users_email_uq ON iam.users (lower(email));
CREATE TRIGGER users_touch BEFORE UPDATE ON iam.users FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

ALTER TABLE org.org_units ADD CONSTRAINT org_units_manager_fk FOREIGN KEY (manager_user_id) REFERENCES iam.users(id);

CREATE TABLE org.external_entities (
  id                   uuid PRIMARY KEY,
  name_ar              text NOT NULL,
  name_en              text,
  entity_type          text NOT NULL DEFAULT 'government',
  contact_name         text,
  contact_email        text,
  contact_phone        text,
  liaison_user_id      uuid NOT NULL REFERENCES iam.users(id),
  correspondence_code  text,
  status               text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE iam.users ADD CONSTRAINT users_external_entity_fk FOREIGN KEY (external_entity_id) REFERENCES org.external_entities(id);

-- كتالوج الأدوار والصلاحيات (06-rbac). يُزرع في V005، والتقييم في وحدة IAM.
CREATE TABLE iam.roles (
  code         text PRIMARY KEY,
  scope        text NOT NULL CHECK (scope IN ('system','body','relation')),
  name_ar      text NOT NULL,
  name_en      text NOT NULL,
  sort_order   int  NOT NULL DEFAULT 0
);

CREATE TABLE iam.permissions (
  code         text PRIMARY KEY,
  scope        text NOT NULL CHECK (scope IN ('system','body','task','entity')),
  description_ar text NOT NULL
);

CREATE TABLE iam.role_permissions (
  role_code        text NOT NULL REFERENCES iam.roles(code),
  permission_code  text NOT NULL REFERENCES iam.permissions(code),
  PRIMARY KEY (role_code, permission_code)
);

CREATE TABLE iam.system_role_assignments (
  id          uuid PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES iam.users(id),
  role_code   text NOT NULL REFERENCES iam.roles(code),
  valid_from  date NOT NULL DEFAULT current_date,
  valid_to    date,
  granted_by  uuid REFERENCES iam.users(id),
  reason      text,
  created_at  timestamptz NOT NULL DEFAULT now(),
  CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX sra_user_idx ON iam.system_role_assignments (user_id);

-- بيانات الدخول المحلي (بيئات التطوير والاختبار وحساب الطوارئ). كلمات المرور بخوارزمية PBKDF2 من ASP.NET Core Identity.
CREATE TABLE iam.local_credentials (
  user_id          uuid PRIMARY KEY REFERENCES iam.users(id),
  password_hash    text NOT NULL,
  failed_attempts  int  NOT NULL DEFAULT 0,
  locked_until     timestamptz,
  password_set_at  timestamptz NOT NULL DEFAULT now(),
  must_change      boolean NOT NULL DEFAULT false
);

CREATE TABLE iam.sessions (
  id              uuid PRIMARY KEY,
  user_id         uuid NOT NULL REFERENCES iam.users(id),
  auth_method     text NOT NULL CHECK (auth_method IN ('local','sso')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  last_seen_at    timestamptz NOT NULL DEFAULT now(),
  expires_at      timestamptz NOT NULL,
  ip              text,
  user_agent_hash text,
  revoked_at      timestamptz,
  revoked_reason  text
);
CREATE INDEX sessions_user_idx ON iam.sessions (user_id) WHERE revoked_at IS NULL;

-- حالة طلبات الدخول الموحد الجارية (state/nonce/PKCE) — تُحذف بعد الاستخدام أو الانتهاء.
CREATE TABLE iam.sso_requests (
  state          text PRIMARY KEY,
  nonce          text NOT NULL,
  code_verifier  text NOT NULL,
  return_url     text NOT NULL DEFAULT '/',
  created_at     timestamptz NOT NULL DEFAULT now()
);
