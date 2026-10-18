-- V013: التقارير ولوحات المؤشرات، والأرشفة وإدارة السجلات، والتكاملات المؤسسية (23-reports-records-integrations، ADR-022).

CREATE SCHEMA IF NOT EXISTS integ;
GRANT USAGE ON SCHEMA integ TO ccms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA integ GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ccms_app;

-- ============ 1. مفاتيح التكاملات الجديدة: لكل تكامل مفتاحه المستقل ============
INSERT INTO config.feature_flags (key, category, value_type, value, default_value, allowed_values, parent_key, connector_code, name_ar, name_en,
                                  description_ar, description_en, sort_order) VALUES
  ('DIRECTORY_SYNC_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'directory',
   'مزامنة دليل المستخدمين (Entra ID / AD)', 'User directory sync (Entra ID / AD)',
   'قراءة المستخدمين من دليل Microsoft Entra ID (ومنه حسابات Active Directory المزامنة) لمطابقة الحسابات وتحديث بياناتها؛ مستقل عن الدخول الموحد',
   'Read users from Microsoft Entra ID (including synced Active Directory accounts) to match accounts and refresh their profile; independent of SSO', 64),
  ('DOCUMENT_REPOSITORY_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'document_repository',
   'مستودع الوثائق المؤسسي', 'Enterprise document repository',
   'إيداع نسخ من المستندات المعتمدة والمؤرشفة في مستودع الوثائق (SharePoint عبر Microsoft Graph)؛ النسخة الأصلية تبقى في النظام',
   'Deposit copies of approved and archived documents in the document repository (SharePoint via Microsoft Graph); the original stays in the system', 65),
  ('VIDEO_MEETINGS_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'video_meetings',
   'منصة الاجتماعات المرئية', 'Video meetings platform',
   'إنشاء رابط اجتماع مرئي (Microsoft Teams عبر Graph) للاجتماع؛ تعطيله لا يمنع إدخال الرابط يدويًا',
   'Create a video meeting link (Microsoft Teams via Graph) for a meeting; disabling it does not prevent entering a link manually', 66)
ON CONFLICT (key) DO NOTHING;

-- ============ 2. الصلاحيات ============
INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('records.manage',              'system', 'إدارة السجلات: التصنيف وقواعد الاحتفاظ والأرشفة والحجز القانوني وطلبات الإتلاف وإيداع المستودع'),
  ('records.destruction.approve', 'system', 'اعتماد طلبات إتلاف المستندات أو رفضها (غير مقدّم الطلب)'),
  ('audit.export',                'system', 'تصدير تقرير التغييرات والتدقيق')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('governance_officer', 'records.manage'), ('system_admin', 'records.manage'),
  ('governance_officer', 'records.destruction.approve'), ('auditor', 'records.destruction.approve'), ('auditor', 'audit.export')
ON CONFLICT DO NOTHING;

-- ============ 3. قواعد الاحتفاظ (افتراضية حتى اعتماد سياسة المكتب — ASM-44) ============
CREATE TABLE docs.retention_rules (
  code            text PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9_]{2,40}$'),
  name_ar         text NOT NULL,
  name_en         text NOT NULL,
  purposes        text[] NOT NULL,             -- أغراض المستندات التي تنطبق عليها القاعدة
  retention_years int  NOT NULL CHECK (retention_years BETWEEN 0 AND 100),
  archive_after_years int NOT NULL DEFAULT 1 CHECK (archive_after_years BETWEEN 0 AND 100),
  final_action    text NOT NULL CHECK (final_action IN ('permanent','destroy','review')),
  legal_basis     text,
  active          boolean NOT NULL DEFAULT true,
  version         int NOT NULL DEFAULT 1,
  updated_by      uuid REFERENCES iam.users(id),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
INSERT INTO docs.retention_rules (code, name_ar, name_en, purposes, retention_years, archive_after_years, final_action, legal_basis) VALUES
  ('governance_records', 'سجلات الحوكمة المعتمدة', 'Approved governance records',
   ARRAY['minutes_pdf','signed_pdf','signature_document','approval_evidence','wet_signature','formation_decision'], 0, 2, 'permanent',
   'افتراضي: المحاضر المعتمدة والموقعة وقرارات التشكيل وأدلة الاعتماد تُحفظ بصفة دائمة — يُستبدل بالمرجع المعتمد لدى المكتب'),
  ('working_papers', 'أوراق العمل والمذكرات', 'Working papers and memos', ARRAY['agenda_memo','attachment'], 10, 2, 'review',
   'افتراضي: مراجعة بعد عشر سنوات قبل أي إتلاف'),
  ('execution_evidence', 'أدلة التنفيذ ومخرجات المهام', 'Execution evidence and deliverables', ARRAY['evidence','deliverable'], 7, 2, 'destroy',
   'افتراضي: سبع سنوات من الرفع'),
  ('correspondence', 'خطابات التكليف', 'Assignment letters', ARRAY['assignment_letter'], 10, 2, 'review', 'افتراضي'),
  ('ai_recordings', 'تسجيلات التفريغ الصوتي', 'Transcription recordings', ARRAY['ai_recording'], 1, 0, 'destroy',
   'افتراضي: سنة واحدة من الرفع؛ النص المفرّغ مسودة مستقلة');

-- ============ 4. المستندات: إصدارات، وثبات المحتوى، ودورة حياة السجل ============
ALTER TABLE docs.documents
  ADD COLUMN series_id           uuid,
  ADD COLUMN previous_version_id uuid REFERENCES docs.documents(id),
  ADD COLUMN is_current          boolean NOT NULL DEFAULT true,
  ADD COLUMN superseded_at       timestamptz,
  ADD COLUMN version_note        text,
  ADD COLUMN locked              boolean NOT NULL DEFAULT false,   -- موقّع أو معتمد: لا يُستبدل إلا بإصدار جديد مسبب
  ADD COLUMN records_class       text REFERENCES docs.retention_rules(code),
  ADD COLUMN lifecycle_status    text NOT NULL DEFAULT 'active' CHECK (lifecycle_status IN ('active','archived','destruction_pending','destroyed')),
  ADD COLUMN archived_at         timestamptz,
  ADD COLUMN retention_until     date,
  ADD COLUMN legal_hold          boolean NOT NULL DEFAULT false,
  ADD COLUMN legal_hold_reason   text,
  ADD COLUMN destroyed_at        timestamptz,
  ADD COLUMN destroyed_by        uuid REFERENCES iam.users(id);
UPDATE docs.documents SET series_id = id WHERE series_id IS NULL;
ALTER TABLE docs.documents ALTER COLUMN series_id SET NOT NULL;
CREATE UNIQUE INDEX documents_series_version ON docs.documents (series_id, version_no);
CREATE UNIQUE INDEX documents_series_current ON docs.documents (series_id) WHERE is_current;
ALTER TABLE docs.documents ADD CONSTRAINT documents_legal_hold_reason CHECK (NOT legal_hold OR legal_hold_reason IS NOT NULL);
ALTER TABLE docs.documents ADD CONSTRAINT documents_destroyed CHECK ((lifecycle_status = 'destroyed') = (destroyed_at IS NOT NULL));

-- التصنيف السجلي والقفل للمستندات القائمة حسب غرض ربطها
UPDATE docs.documents d SET
  records_class = (SELECT r.code FROM docs.retention_rules r JOIN docs.document_links l ON l.purpose = ANY(r.purposes) WHERE l.document_id = d.id ORDER BY r.retention_years = 0 DESC LIMIT 1),
  locked = EXISTS (SELECT 1 FROM docs.document_links l WHERE l.document_id = d.id AND l.purpose IN ('minutes_pdf','signed_pdf','signature_document','approval_evidence','wet_signature'));
UPDATE docs.documents d SET retention_until = (d.uploaded_at AT TIME ZONE 'Asia/Riyadh')::date + make_interval(years => r.retention_years)
  FROM docs.retention_rules r WHERE r.code = d.records_class AND r.final_action <> 'permanent';

-- ثبات المحتوى: لا يُعدّل ملف مسجل ولا بصمته ولا مفتاح تخزينه أبدًا؛ الاستبدال بإصدار جديد فقط، ولا حذف لسجل مستند.
CREATE OR REPLACE FUNCTION docs.documents_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.file_name IS DISTINCT FROM OLD.file_name OR NEW.content_type IS DISTINCT FROM OLD.content_type OR NEW.size_bytes IS DISTINCT FROM OLD.size_bytes
     OR NEW.sha256 IS DISTINCT FROM OLD.sha256 OR NEW.storage_key IS DISTINCT FROM OLD.storage_key OR NEW.series_id IS DISTINCT FROM OLD.series_id
     OR NEW.version_no IS DISTINCT FROM OLD.version_no OR NEW.uploaded_by IS DISTINCT FROM OLD.uploaded_by OR NEW.uploaded_at IS DISTINCT FROM OLD.uploaded_at THEN
    RAISE EXCEPTION 'document content is immutable; register a new version instead' USING ERRCODE = '23514';
  END IF;
  IF OLD.locked AND NOT NEW.locked THEN
    RAISE EXCEPTION 'a signed or approved document cannot be unlocked' USING ERRCODE = '23514';
  END IF;
  IF OLD.lifecycle_status = 'destroyed' AND NEW.lifecycle_status <> 'destroyed' THEN
    RAISE EXCEPTION 'a destroyed document cannot be restored' USING ERRCODE = '23514';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER documents_immutable BEFORE UPDATE ON docs.documents FOR EACH ROW EXECUTE FUNCTION docs.documents_immutable();
REVOKE DELETE, TRUNCATE ON docs.documents FROM ccms_app;

-- طلبات الإتلاف: مقدّم الطلب غير المعتمد، وشهادة إتلاف بالبيانات الوصفية بعد التنفيذ
CREATE TABLE docs.destruction_requests (
  id              uuid PRIMARY KEY,
  reference_no    text NOT NULL UNIQUE,
  document_ids    uuid[] NOT NULL CHECK (cardinality(document_ids) BETWEEN 1 AND 500),
  reason          text NOT NULL,
  status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','rejected','executed')),
  requested_by    uuid NOT NULL REFERENCES iam.users(id),
  requested_at    timestamptz NOT NULL DEFAULT now(),
  decided_by      uuid REFERENCES iam.users(id),
  decided_at      timestamptz,
  decision_note   text,
  executed_by     uuid REFERENCES iam.users(id),
  executed_at     timestamptz,
  certificate     jsonb,                       -- لكل مستند: الاسم والبصمة والتصنيف والقاعدة وتاريخ الإتلاف
  CHECK (decided_by IS NULL OR decided_by <> requested_by),
  CHECK (status NOT IN ('approved','rejected','executed') OR decided_at IS NOT NULL)
);
REVOKE DELETE, TRUNCATE ON docs.destruction_requests FROM ccms_app;

-- إيداعات مستودع الوثائق المؤسسي
CREATE TABLE docs.repository_exports (
  id            uuid PRIMARY KEY,
  document_id   uuid NOT NULL REFERENCES docs.documents(id),
  connector     text NOT NULL,
  status        text NOT NULL CHECK (status IN ('succeeded','failed')),
  remote_id     text,
  remote_url    text,
  error         text,
  exported_by   uuid NOT NULL REFERENCES iam.users(id),
  exported_at   timestamptz NOT NULL DEFAULT now()
);
REVOKE UPDATE, DELETE, TRUNCATE ON docs.repository_exports FROM ccms_app;
GRANT SELECT, INSERT ON docs.repository_exports TO ccms_app;

-- ============ 5. سجل استدعاءات الموصلات: بيانات تشغيلية دون حمولة أو أسرار ============
CREATE TABLE integ.connector_calls (
  id              uuid PRIMARY KEY,
  connector       text NOT NULL,
  operation       text NOT NULL,
  status          text NOT NULL CHECK (status IN ('succeeded','failed','retrying','timeout','skipped_disabled','denied','circuit_open','not_configured')),
  attempt         int  NOT NULL DEFAULT 1,
  duration_ms     int,
  http_status     int,
  error           text,
  actor_user_id   uuid REFERENCES iam.users(id),
  correlation_id  text,
  created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX connector_calls_idx ON integ.connector_calls (connector, created_at DESC);
REVOKE UPDATE, DELETE, TRUNCATE ON integ.connector_calls FROM ccms_app;
GRANT SELECT, INSERT ON integ.connector_calls TO ccms_app;

-- ============ 6. فهارس مساعدة للتقارير والبحث ============
CREATE INDEX IF NOT EXISTS minutes_approved_idx ON minutes.minutes (approved_at);
CREATE INDEX IF NOT EXISTS tasks_completed_idx ON tasks.tasks (completed_at);
CREATE INDEX IF NOT EXISTS resolutions_issued_idx ON resolutions.resolutions (issued_at);
CREATE INDEX IF NOT EXISTS audit_occurred_idx ON audit.audit_events (occurred_at);

-- ============ 7. تعبئة السلسلة والتصنيف السجلي والقفل آليًا ============
CREATE OR REPLACE FUNCTION docs.documents_defaults() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.series_id IS NULL THEN NEW.series_id := NEW.id; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER documents_defaults BEFORE INSERT ON docs.documents FOR EACH ROW EXECUTE FUNCTION docs.documents_defaults();

-- عند ربط المستند بغرضه: يُحدد صنفه السجلي ومدة احتفاظه، ويُقفل إن كان موقّعًا أو معتمدًا
CREATE OR REPLACE FUNCTION docs.document_links_classify() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r docs.retention_rules;
BEGIN
  SELECT * INTO r FROM docs.retention_rules WHERE NEW.purpose = ANY(purposes) AND active ORDER BY (final_action = 'permanent') DESC LIMIT 1;
  UPDATE docs.documents d SET
    records_class = coalesce(d.records_class, r.code),
    retention_until = CASE WHEN d.records_class IS NULL AND r.code IS NOT NULL AND r.final_action <> 'permanent'
                           THEN (d.uploaded_at AT TIME ZONE 'Asia/Riyadh')::date + make_interval(years => r.retention_years) ELSE d.retention_until END,
    locked = d.locked OR NEW.purpose IN ('minutes_pdf','signed_pdf','signature_document','approval_evidence','wet_signature')
  WHERE d.id = NEW.document_id;
  RETURN NEW;
END $$;
CREATE TRIGGER document_links_classify AFTER INSERT ON docs.document_links FOR EACH ROW EXECUTE FUNCTION docs.document_links_classify();
