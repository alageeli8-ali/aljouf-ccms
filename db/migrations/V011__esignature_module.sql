-- V011: وحدة التوقيع الإلكتروني المستقلة (09، 21-esignature-delivery، ADR-020).
-- الوحدة اختيارية بالكامل: إدارة المحاضر ودورة اعتمادها تعمل دونها بالمسار البديل (ADR-018).
-- ثلاثة أنواع منفصلة لا يُعرض أحدها على أنه الآخر:
--   in_app_approval        موافقة إلكترونية موثقة داخل النظام (ليست توقيعًا بشهادة تشفير)
--   provider_esign         توقيع إلكتروني عبر مزوّد خارجي
--   pdf_digital_signature  توقيع رقمي تشفيري (CMS) مضمن في ملف PDF بشهادة الموقِّع

CREATE SCHEMA IF NOT EXISTS sig;
GRANT USAGE ON SCHEMA sig TO ccms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA sig GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ccms_app;

-- ============ 1. الإعدادات: القيم المسموحة بأسماء صريحة لكل نوع ============
-- يُسقط قيدا القيمة مؤقتًا لتحويل القيم القائمة ثم يُعادان
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'config.feature_flags'::regclass AND contype = 'c'
           AND pg_get_constraintdef(oid) LIKE '%allowed_values%' LOOP
    EXECUTE format('ALTER TABLE config.feature_flags DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
UPDATE config.feature_flags SET
  allowed_values = ARRAY['none','mock','national_provider'],
  value = CASE value WHEN 'sandbox' THEN 'mock' ELSE value END,
  description_ar = 'none: لم يُختر؛ mock: مزوّد تجريبي للاختبار فقط وليس خدمة توقيع إنتاجية (ممنوع في الإنتاج)؛ national_provider: مزوّد معتمد (بعد التعاقد وتنفيذ موصله)',
  description_en = 'none: not selected; mock: TEST-ONLY simulator, not a production signing service (blocked in production); national_provider: accredited provider (after contracting and implementing its adapter)'
WHERE key = 'E_SIGNATURE_PROVIDER';
UPDATE config.feature_flags SET
  allowed_values = ARRAY['in_app_approval','provider_esign','pdf_digital_signature'],
  default_value = 'in_app_approval',
  -- remote_certificate كانت القيمة الافتراضية والوحدة غير منفذة قبل هذا الترحيل؛ فتبدأ كل البيئات بالنوع الآمن in_app_approval
  value = CASE value WHEN 'otp_confirmation' THEN 'provider_esign' ELSE 'in_app_approval' END,
  name_ar = 'نوع التوقيع', name_en = 'Signature type',
  description_ar = 'in_app_approval: موافقة إلكترونية موثقة داخل النظام (ليست توقيعًا رقميًا بشهادة)؛ provider_esign: توقيع إلكتروني عبر مزوّد خارجي؛ pdf_digital_signature: توقيع رقمي تشفيري على PDF بشهادة الموقّع عبر المزوّد',
  description_en = 'in_app_approval: documented in-app electronic approval (NOT a certificate-based digital signature); provider_esign: e-signature through an external provider; pdf_digital_signature: cryptographic PDF signature with the signer''s certificate via the provider'
WHERE key = 'E_SIGNATURE_METHOD';
UPDATE config.feature_flags SET description_ar = 'إصدار ملف PDF موقّع: نسخة التوقيع النهائية من المزوّد أو PDF يحمل التوقيعات الرقمية، إضافة إلى سجل الاعتماد والتوقيعات في PDF المحضر المعتمد',
  description_en = 'Produce a signed PDF output (the provider''s final copy or a PDF carrying the digital signatures) besides the approval and signature record in the approved minutes PDF'
WHERE key = 'E_SIGNATURE_PDF_OUTPUT';
UPDATE config.feature_flags SET description_ar = 'يعيد الموقّع التحقق من هويته (كلمة المرور أو الدخول الموحد) قبل كل توقيع أو موافقة',
  description_en = 'Signers must re-authenticate (password or SSO) before every signature or approval'
WHERE key = 'E_SIGNATURE_REAUTH_REQUIRED';
UPDATE config.feature_flags SET description_ar = 'يوقّع الموقّعون داخل كل مرحلة واحدًا تلو الآخر بالترتيب بدل التوازي (الافتراضي لمسارات التوقيع)',
  description_en = 'Signers within each step sign one after another instead of in parallel (default for signing routes)'
WHERE key = 'E_SIGNATURE_SEQUENTIAL';
UPDATE config.feature_flags SET description_ar = 'مسار التوقيع الافتراضي يشمل الأعضاء (الحاضرين أو كل الأعضاء حسب سياسة الجهة)' WHERE key = 'E_SIGNATURE_MEMBER_REQUIRED';
UPDATE config.feature_flags SET description_ar = 'مسار التوقيع الافتراضي يشمل رئيس الجهة بعد الأعضاء' WHERE key = 'E_SIGNATURE_CHAIR_REQUIRED';
UPDATE config.feature_flags SET description_ar = 'مسار التوقيع الافتراضي يبدأ بأمين السر معدًّا للمحضر' WHERE key = 'E_SIGNATURE_SECRETARY_REQUIRED';
ALTER TABLE config.feature_flags ADD CONSTRAINT feature_flags_enum_value_ck CHECK (value_type <> 'enum' OR value = ANY(allowed_values));

-- ============ 2. الصلاحيات ============
INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('esign.request',       'body',   'بدء طلب توقيع إلكتروني على إصدار محضر'),
  ('esign.manage',        'body',   'إدارة طلبات التوقيع الجارية: الإبطال، والتحويل للمسار البديل، والتعليق والاستئناف، وإعادة الإرسال'),
  ('esign.verify',        'body',   'الاطلاع على تقارير التحقق من التوقيعات والنسخ الموقعة'),
  ('esign.routes.manage', 'system', 'تهيئة مسارات التوقيع لكل جهة أو نوع اجتماع')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('secretary','esign.request'), ('governance_officer','esign.request'),
  ('secretary','esign.manage'), ('governance_officer','esign.manage'),
  ('chair','esign.verify'), ('vice_chair','esign.verify'), ('secretary','esign.verify'), ('member','esign.verify'),
  ('governance_officer','esign.verify'), ('auditor','esign.verify'),
  ('system_admin','esign.routes.manage'), ('governance_officer','esign.routes.manage'),
  ('secretary','minutes.sign')
ON CONFLICT DO NOTHING;

-- ============ 3. مسارات التوقيع (لكل جهة أو نوع اجتماع؛ الافتراضي من الإعدادات المركزية) ============
CREATE TABLE sig.routes (
  id               uuid PRIMARY KEY,
  body_id          uuid REFERENCES bodies.bodies(id),           -- فارغ = كل الجهات
  meeting_type     text CHECK (meeting_type IN ('regular','extraordinary','urgent','circulation')),  -- فارغ = كل الأنواع
  name_ar          text NOT NULL,
  name_en          text,
  -- المراحل بالترتيب: [{order, role ∈ secretary|members|chair, allowDelegation}]
  steps            jsonb NOT NULL,
  sequential       boolean,                                       -- فارغ = E_SIGNATURE_SEQUENTIAL
  signature_type   text CHECK (signature_type IN ('in_app_approval','provider_esign','pdf_digital_signature')),  -- فارغ = E_SIGNATURE_METHOD
  expiry_days      int NOT NULL DEFAULT 14 CHECK (expiry_days BETWEEN 1 AND 90),
  active           boolean NOT NULL DEFAULT true,
  version          int NOT NULL DEFAULT 1,
  created_by       uuid NOT NULL REFERENCES iam.users(id),
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid REFERENCES iam.users(id),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  CHECK (jsonb_typeof(steps) = 'array' AND jsonb_array_length(steps) BETWEEN 1 AND 3)
);
CREATE UNIQUE INDEX routes_one_active ON sig.routes (COALESCE(body_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(meeting_type, '*')) WHERE active;
REVOKE DELETE, TRUNCATE ON sig.routes FROM ccms_app;

-- ============ 4. طلبات التوقيع (مرتبطة بإصدار ثابت وبصمته) ============
CREATE TABLE sig.requests (
  id                      uuid PRIMARY KEY,
  reference_no            text NOT NULL UNIQUE,
  subject_type            text NOT NULL DEFAULT 'minutes' CHECK (subject_type IN ('minutes')),
  minutes_id              uuid NOT NULL REFERENCES minutes.minutes(id),
  minutes_version_id      uuid NOT NULL REFERENCES minutes.minutes_versions(id),
  version_no              int  NOT NULL,
  body_id                 uuid NOT NULL REFERENCES bodies.bodies(id),
  content_hash            text NOT NULL,                       -- بصمة محتوى الإصدار (SHA-256)
  document_id             uuid NOT NULL REFERENCES docs.documents(id),  -- نسخة PDF المرسلة للتوقيع، لا تُستبدل
  document_sha256         text NOT NULL,
  document_size           bigint NOT NULL,
  signature_type          text NOT NULL CHECK (signature_type IN ('in_app_approval','provider_esign','pdf_digital_signature')),
  provider                text NOT NULL,                       -- internal | mock | national_provider
  provider_ref            text,
  sequential              boolean NOT NULL,
  route_id                uuid REFERENCES sig.routes(id),
  route_snapshot          jsonb NOT NULL,
  settings_snapshot       jsonb NOT NULL,                      -- قيم الإعدادات وقت الإنشاء (لا تتغير بتغييرها لاحقًا)
  status                  text NOT NULL DEFAULT 'queued' CHECK (status IN
                            ('queued','in_progress','on_hold','completed','declined','voided','cancelled','expired','failed')),
  status_reason           text,
  current_document_id     uuid REFERENCES docs.documents(id),  -- آخر مراجعة موقعة (للتوقيع الرقمي المتتابع)
  current_document_sha256 text,
  signed_document_id      uuid REFERENCES docs.documents(id),  -- النسخة النهائية الموقعة
  signed_document_sha256  text,
  verification            jsonb,                               -- تقرير التحقق وقت الاكتمال
  expires_at              timestamptz NOT NULL,
  dispatch_attempts       int NOT NULL DEFAULT 0,
  next_attempt_at         timestamptz,
  last_error              text,
  pending_provider_cancel boolean NOT NULL DEFAULT false,
  created_by              uuid NOT NULL REFERENCES iam.users(id),
  created_at              timestamptz NOT NULL DEFAULT now(),
  completed_at            timestamptz,
  closed_by               uuid REFERENCES iam.users(id),
  closed_at               timestamptz,
  updated_at              timestamptz NOT NULL DEFAULT now(),
  CHECK (status <> 'completed' OR completed_at IS NOT NULL),
  CHECK (status NOT IN ('voided','cancelled') OR (status_reason IS NOT NULL AND closed_at IS NOT NULL))
);
-- طلب حي واحد لكل إصدار
CREATE UNIQUE INDEX requests_one_live ON sig.requests (minutes_version_id) WHERE status IN ('queued','in_progress','on_hold');
CREATE INDEX requests_minutes_idx ON sig.requests (minutes_id, created_at DESC);
REVOKE DELETE, TRUNCATE ON sig.requests FROM ccms_app;

CREATE TABLE sig.signers (
  id                  uuid PRIMARY KEY,
  request_id          uuid NOT NULL REFERENCES sig.requests(id),
  user_id             uuid NOT NULL REFERENCES iam.users(id),   -- الموقّع المطلوب
  signer_role         text NOT NULL CHECK (signer_role IN ('secretary','member','chair')),
  step_order          int  NOT NULL,
  position            int  NOT NULL,                           -- الترتيب الكلي
  allow_delegation    boolean NOT NULL DEFAULT false,
  status              text NOT NULL DEFAULT 'waiting' CHECK (status IN ('waiting','pending','signed','declined','cancelled')),
  acted_by_user_id    uuid REFERENCES iam.users(id),           -- من نفّذ فعليًا (يختلف عند التوقيع بالنيابة)
  delegation_id       uuid REFERENCES iam.delegations(id),
  signed_at           timestamptz,
  decline_reason      text,
  signed_document_sha256 text,                                 -- بصمة المستند الذي وقّعه (المراجعة السابقة لتوقيعه)
  evidence            jsonb,                                   -- إعادة التحقق، وبيانات الشهادة، ومعرّف المزوّد
  provider_signer_ref text,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (request_id, user_id),
  UNIQUE (request_id, position),
  CHECK (status <> 'signed' OR (signed_at IS NOT NULL AND acted_by_user_id IS NOT NULL)),
  CHECK (status <> 'declined' OR decline_reason IS NOT NULL)
);
REVOKE DELETE, TRUNCATE ON sig.signers FROM ccms_app;

-- سجل الأحداث (إلحاقي): كل إجراء واستجابة مزوّد، دون أسرار المزوّد
CREATE TABLE sig.events (
  id             uuid PRIMARY KEY,
  request_id     uuid REFERENCES sig.requests(id),
  signer_id      uuid REFERENCES sig.signers(id),
  kind           text NOT NULL,
  source         text NOT NULL CHECK (source IN ('system','user','provider','worker','admin')),
  actor_user_id  uuid REFERENCES iam.users(id),
  data           jsonb NOT NULL DEFAULT '{}',
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX events_request_idx ON sig.events (request_id, created_at);
REVOKE UPDATE, DELETE, TRUNCATE ON sig.events FROM ccms_app;
GRANT SELECT, INSERT ON sig.events TO ccms_app;

-- استدعاءات المزوّد الراجعة (إلحاقي): التحقق من توقيع الرسالة، ومنع التكرار، وربطها بالطلب الصحيح
CREATE TABLE sig.provider_callbacks (
  id                 uuid PRIMARY KEY,
  provider           text NOT NULL,
  provider_event_id  text NOT NULL,
  provider_ref       text,
  request_id         uuid REFERENCES sig.requests(id),
  signature_valid    boolean NOT NULL,
  payload_sha256     text NOT NULL,
  payload            jsonb,                                    -- بعد حذف أي حقل سري
  outcome            text NOT NULL CHECK (outcome IN ('applied','duplicate','rejected_signature','rejected_mismatch','rejected_unknown','deferred_disabled','deferred_on_hold','ignored')),
  detail             text,
  received_at        timestamptz NOT NULL DEFAULT now(),
  applied_at         timestamptz
);
CREATE UNIQUE INDEX callbacks_dedupe ON sig.provider_callbacks (provider, provider_event_id) WHERE signature_valid;
REVOKE DELETE, TRUNCATE ON sig.provider_callbacks FROM ccms_app;
GRANT SELECT, INSERT ON sig.provider_callbacks TO ccms_app;
GRANT UPDATE (outcome, detail, applied_at) ON sig.provider_callbacks TO ccms_app;

-- منح إعادة التحقق من الهوية: قصيرة الأجل ولاستخدام واحد
CREATE TABLE sig.reauth_grants (
  id          uuid PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES iam.users(id),
  token_hash  text NOT NULL UNIQUE,
  method      text NOT NULL CHECK (method IN ('password','sso')),
  created_at  timestamptz NOT NULL DEFAULT now(),
  expires_at  timestamptz NOT NULL,
  used_at     timestamptz,
  used_for    uuid
);
REVOKE DELETE, TRUNCATE ON sig.reauth_grants FROM ccms_app;

-- ============ 5. ربط إقرارات المحضر بطلبات الوحدة ============
-- إسقاط قيود القيم القائمة بتعريفها لا باسمها (أسماء مولدة تختلف بين الترحيلات)
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conrelid::regclass AS tbl, conname FROM pg_constraint
           WHERE contype = 'c' AND (
             (conrelid = 'minutes.minutes_signoffs'::regclass AND pg_get_constraintdef(oid) LIKE '%method%') OR
             (conrelid = 'minutes.minutes_approvals'::regclass AND pg_get_constraintdef(oid) LIKE '%method%') OR
             (conrelid = 'docs.document_links'::regclass AND pg_get_constraintdef(oid) LIKE '%purpose%')) LOOP
    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', c.tbl, c.conname);
  END LOOP;
END $$;
ALTER TABLE minutes.minutes_signoffs ADD CONSTRAINT minutes_signoffs_method_check CHECK (method IN
  ('attestation','wet_signature','wet_signature_evidence','electronic_signature','in_app_approval','provider_esign','pdf_digital_signature'));
ALTER TABLE minutes.minutes_signoffs
  ADD COLUMN signature_request_id uuid REFERENCES sig.requests(id),
  ADD COLUMN signature_signer_id  uuid REFERENCES sig.signers(id),
  ADD COLUMN on_behalf_of_user_id uuid REFERENCES iam.users(id);

ALTER TABLE minutes.minutes_approvals ADD CONSTRAINT minutes_approvals_method_check CHECK (method IN
  ('in_system','recorded_external','electronic_signature','attestation','wet_signature','in_app_approval','provider_esign','pdf_digital_signature'));

ALTER TABLE docs.document_links ADD CONSTRAINT document_links_purpose_ck CHECK (purpose IN
  ('formation_decision','agenda_memo','attachment','evidence','wet_signature','approval_evidence','minutes_pdf','signed_pdf','deliverable','assignment_letter','signature_document'));

-- ============ 6. بيانات المزوّد التجريبي (mock) — للاختبار فقط ============
-- تمثل ما يحتفظ به مزوّد خارجي لديه؛ لا تُستخدم إلا حين E_SIGNATURE_PROVIDER = mock وفي غير الإنتاج.
CREATE TABLE sig.mock_keys (
  name        text PRIMARY KEY,                -- root | signer:{userId}
  pfx_base64  text NOT NULL,                   -- شهادة اختبارية بمفتاحها؛ ليست شهادة موثوقة
  created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE sig.mock_envelopes (
  provider_ref     text PRIMARY KEY,
  correlation_id   uuid NOT NULL,
  reference        text NOT NULL,
  original_base64  text NOT NULL,
  original_sha256  text NOT NULL,
  current_base64   text NOT NULL,
  sequential       boolean NOT NULL,
  signers          jsonb NOT NULL,
  status           text NOT NULL CHECK (status IN ('sent','completed','declined','cancelled','expired')),
  expires_at       timestamptz NOT NULL,
  event_seq        int NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now()
);
