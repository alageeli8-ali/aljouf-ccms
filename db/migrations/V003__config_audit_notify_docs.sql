-- V003: الإعدادات المركزية، والتدقيق، والإشعارات، والمستندات

-- مفاتيح الإعداد (07-feature-flags). القيمة نص مقيد بالنوع؛ parent_key هو المفتاح الأعلى الذي يحجبها.
CREATE TABLE config.feature_flags (
  key             text PRIMARY KEY CHECK (key ~ '^[A-Z][A-Z0-9_]+$'),
  category        text NOT NULL CHECK (category IN ('ai','esignature','integration','platform')),
  value_type      text NOT NULL CHECK (value_type IN ('boolean','enum')),
  value           text NOT NULL,
  default_value   text NOT NULL,
  allowed_values  text[],
  parent_key      text REFERENCES config.feature_flags(key),
  connector_code  text,
  name_ar         text NOT NULL,
  name_en         text NOT NULL,
  description_ar  text NOT NULL,
  sort_order      int  NOT NULL DEFAULT 0,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  updated_by      uuid REFERENCES iam.users(id),
  CHECK (value_type <> 'boolean' OR value IN ('true','false')),
  CHECK (value_type <> 'enum' OR value = ANY(allowed_values))
);

-- سجل تغييرات الإعدادات: كل محاولة، ناجحة أو مرفوضة (المرحلة 1، البند 5).
CREATE TABLE config.feature_flag_history (
  id              uuid PRIMARY KEY,
  flag_key        text NOT NULL,
  old_value       text,
  new_value       text,
  changed_at      timestamptz NOT NULL DEFAULT now(),
  changed_by      uuid REFERENCES iam.users(id),
  actor_display   text NOT NULL,
  reason          text,
  result          text NOT NULL CHECK (result IN ('succeeded','rejected_validation','confirmation_required','denied','failed')),
  result_detail   text,
  impact          jsonb,
  correlation_id  text
);
CREATE INDEX ffh_key_idx ON config.feature_flag_history (flag_key, changed_at DESC);

-- حالة اتصال مزوّدي الخدمات، مستقلة عن تفعيل الخاصية.
CREATE TABLE config.connector_status (
  code        text PRIMARY KEY,
  status      text NOT NULL CHECK (status IN ('not_configured','healthy','degraded','failing','disabled')),
  detail      text,
  latency_ms  int,
  checked_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE config.numbering_sequences (
  scope_key   text NOT NULL,
  year        int  NOT NULL,
  last_value  int  NOT NULL DEFAULT 0,
  PRIMARY KEY (scope_key, year)
);

-- سجل التدقيق بسلسلة بصمات (FR-AUD-01/02)
CREATE TABLE audit.audit_events (
  id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  occurred_at           timestamptz NOT NULL DEFAULT now(),
  actor_user_id         uuid,
  on_behalf_of_user_id  uuid,
  actor_ip              text,
  correlation_id        text,
  module                text NOT NULL,
  action                text NOT NULL,
  entity_type           text NOT NULL,
  entity_id             text,
  outcome               text NOT NULL DEFAULT 'success' CHECK (outcome IN ('success','denied','failure')),
  classification        text,
  before                jsonb,
  after                 jsonb,
  prev_hash             text NOT NULL,
  hash                  text NOT NULL
);
CREATE INDEX audit_entity_idx ON audit.audit_events (entity_type, entity_id);
CREATE INDEX audit_actor_idx ON audit.audit_events (actor_user_id, occurred_at);

-- الإشعارات
CREATE TABLE notify.in_app_notifications (
  id          uuid PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES iam.users(id),
  event_type  text NOT NULL,
  title_ar    text NOT NULL,
  title_en    text NOT NULL,
  link        text,
  severity    text NOT NULL DEFAULT 'info' CHECK (severity IN ('info','warning','critical')),
  dedupe_key  text UNIQUE,
  is_read     boolean NOT NULL DEFAULT false,
  created_at  timestamptz NOT NULL DEFAULT now(),
  read_at     timestamptz
);
CREATE INDEX notif_user_idx ON notify.in_app_notifications (user_id, is_read, created_at DESC);

CREATE TABLE notify.outbox_messages (
  id                 uuid PRIMARY KEY,
  event_type         text NOT NULL,
  channel            text NOT NULL CHECK (channel IN ('email','sms','calendar')),
  recipient_user_id  uuid REFERENCES iam.users(id),
  recipient_address  text NOT NULL,
  subject            text NOT NULL,
  payload            jsonb NOT NULL DEFAULT '{}',
  dedupe_key         text NOT NULL UNIQUE,
  status             text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','sent','logged','failed','suppressed')),
  attempts           int NOT NULL DEFAULT 0,
  next_attempt_at    timestamptz NOT NULL DEFAULT now(),
  last_error         text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  sent_at            timestamptz
);
CREATE INDEX outbox_pending_idx ON notify.outbox_messages (next_attempt_at) WHERE status = 'pending';

-- المستندات (05 §3.8)
CREATE TABLE docs.documents (
  id                uuid PRIMARY KEY,
  file_name         text NOT NULL,
  content_type      text NOT NULL,
  size_bytes        bigint NOT NULL CHECK (size_bytes >= 0),
  sha256            text NOT NULL,
  storage_provider  text NOT NULL DEFAULT 'filesystem' CHECK (storage_provider IN ('filesystem','s3','sharepoint')),
  storage_key       text NOT NULL,
  classification    text NOT NULL DEFAULT 'restricted' CHECK (classification IN ('public','restricted','secret','top_secret')),
  version_no        int NOT NULL DEFAULT 1,
  scan_status       text NOT NULL DEFAULT 'not_scanned' CHECK (scan_status IN ('pending','clean','infected','not_scanned')),
  uploaded_by       uuid NOT NULL REFERENCES iam.users(id),
  uploaded_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz
);

CREATE TABLE docs.document_links (
  id            uuid PRIMARY KEY,
  document_id   uuid NOT NULL REFERENCES docs.documents(id),
  owner_type    text NOT NULL CHECK (owner_type IN ('body','meeting','minutes','resolution','task')),
  owner_id      uuid NOT NULL,
  purpose       text NOT NULL DEFAULT 'attachment' CHECK (purpose IN ('formation_decision','agenda_memo','attachment','evidence','minutes_pdf','wet_signature','signed_pdf')),
  created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX doclinks_owner_idx ON docs.document_links (owner_type, owner_id);

-- سجل التدقيق وسجل الإعدادات للإلحاق فقط من حساب التطبيق
REVOKE UPDATE, DELETE, TRUNCATE ON audit.audit_events FROM ccms_app;
REVOKE UPDATE, DELETE, TRUNCATE ON config.feature_flag_history FROM ccms_app;
GRANT SELECT, INSERT ON audit.audit_events, config.feature_flag_history TO ccms_app;
