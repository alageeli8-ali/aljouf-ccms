-- V012: وحدة الذكاء الاصطناعي المستقلة (10، 22-ai-delivery، ADR-021).
-- الوحدة اختيارية بالكامل: المحاضر والقرارات والمهام والبحث التقليدي تعمل دونها.
-- كل مخرج مسودة للمراجعة البشرية؛ الوحدة لا تكتب في جداول الوحدات الأساسية إطلاقًا.

CREATE SCHEMA IF NOT EXISTS ai;
GRANT USAGE ON SCHEMA ai TO ccms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA ai GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ccms_app;

-- ============ 1. أنواع إعدادات جديدة: عدد صحيح بحدود، ونص بطول أقصى ============
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint
           WHERE conrelid = 'config.feature_flags'::regclass AND contype = 'c'
             AND pg_get_constraintdef(oid) LIKE '%value_type%' AND pg_get_constraintdef(oid) LIKE '%boolean%' AND pg_get_constraintdef(oid) LIKE '%enum%'
             AND pg_get_constraintdef(oid) NOT LIKE '%allowed_values%' AND pg_get_constraintdef(oid) NOT LIKE '%true%' LOOP
    EXECUTE format('ALTER TABLE config.feature_flags DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
ALTER TABLE config.feature_flags ADD CONSTRAINT feature_flags_value_type_ck CHECK (value_type IN ('boolean','enum','integer','text'));
ALTER TABLE config.feature_flags
  ADD COLUMN min_value  bigint,
  ADD COLUMN max_value  bigint,
  ADD COLUMN max_length int;
ALTER TABLE config.feature_flags ADD CONSTRAINT feature_flags_integer_ck CHECK (value_type <> 'integer' OR (value ~ '^-?[0-9]{1,18}$'
  AND (min_value IS NULL OR value::bigint >= min_value) AND (max_value IS NULL OR value::bigint <= max_value)));
ALTER TABLE config.feature_flags ADD CONSTRAINT feature_flags_text_ck CHECK (value_type <> 'text' OR max_length IS NULL OR length(value) <= max_length);

-- ============ 2. توزيع الوظائف العشر على المفاتيح الستة (22 §2) ============
UPDATE config.feature_flags SET
  name_ar = 'التلخيص والصياغة', name_en = 'Summaries and drafting',
  description_ar = 'تلخيص المحاضر السابقة، ومسودة جدول الأعمال من المفتوح، ومسودة المحضر من الملاحظات، والملخص التنفيذي — كلها مسودات للمراجعة',
  description_en = 'Summaries of past minutes, agenda drafts from open items, minutes drafts from notes and the executive summary — all drafts for human review'
WHERE key = 'AI_MINUTES_SUMMARY_ENABLED';
UPDATE config.feature_flags SET
  description_ar = 'اقتراح القرارات والتوصيات من نص المحضر مع الاقتباس الذي استُخرجت منه',
  description_en = 'Suggest decisions and recommendations from the minutes text with the quote they came from'
WHERE key = 'AI_DECISION_EXTRACTION_ENABLED';
UPDATE config.feature_flags SET
  description_ar = 'اقتراح المهام والمسؤولين والتواريخ من النص، واقتراح ربط المهمة بالقرار الصحيح',
  description_en = 'Suggest tasks, owners and dates from the text, and suggest linking a task to the right resolution'
WHERE key = 'AI_TASK_EXTRACTION_ENABLED';
UPDATE config.feature_flags SET
  description_ar = 'البحث باللغة الطبيعية والإجابة عن الأسئلة من المحاضر والقرارات والمهام ضمن صلاحيات المستخدم وبمراجع',
  description_en = 'Natural-language search and answers from minutes, resolutions and tasks within the user''s access, with references'
WHERE key = 'AI_SMART_SEARCH_ENABLED';
UPDATE config.feature_flags SET
  description_ar = 'تحويل تسجيلات مصرح بها إلى نص بموافقة موثقة من الحضور، متى كانت خدمة التفريغ متاحة ومعتمدة',
  description_en = 'Transcribe authorised recordings with documented attendee consent, when a transcription service is available and approved'
WHERE key = 'AI_AUDIO_TRANSCRIPTION_ENABLED';

-- ============ 3. إعدادات المزوّد والنموذج والاتصال والحدود ============
-- عنوان المزوّد مقيد بقائمة مضيفين من بيئة الخادم (Ai:AllowedHosts)؛ ومفتاح API لا يُخزن هنا إطلاقًا (مخزن الأسرار §4).
INSERT INTO config.feature_flags (key, category, value_type, value, default_value, allowed_values, parent_key, connector_code, name_ar, name_en,
                                  description_ar, description_en, sort_order, min_value, max_value, max_length) VALUES
  ('AI_PROVIDER', 'ai', 'enum', 'none', 'none', ARRAY['none','mock','openai_compatible'], 'AI_ENABLED', 'ai',
   'مزوّد الذكاء الاصطناعي', 'AI provider',
   'none: لم يُختر؛ mock: مزوّد تجريبي للاختبار فقط وليس نموذجًا لغويًا (ممنوع في الإنتاج)؛ openai_compatible: خادم نموذج معتمد بواجهة متوافقة',
   'none: not selected; mock: TEST-ONLY simulator, not a language model (blocked in production); openai_compatible: an approved model server with a compatible API',
   16, NULL, NULL, NULL),
  ('AI_PROVIDER_HOSTING', 'ai', 'enum', 'on_premises', 'on_premises', ARRAY['on_premises','external'], 'AI_ENABLED', NULL,
   'موقع استضافة المزوّد', 'Provider hosting',
   'on_premises: داخل شبكة المكتب؛ external: مزوّد خارجي معتمد — لا يُرسل إليه أعلى من «مقيد»، وتُحجب المعرّفات الشخصية قبل الإرسال',
   'on_premises: inside the office network; external: an approved external provider — never receives above "restricted", and personal identifiers are masked before sending',
   17, NULL, NULL, NULL),
  ('AI_MAX_CLASSIFICATION', 'ai', 'enum', 'public', 'public', ARRAY['none','public','restricted','secret'], 'AI_ENABLED', NULL,
   'أعلى تصنيف مسموح بمعالجته', 'Highest classification allowed',
   'لا تُرسل إلى المزوّد بيانات أعلى تصنيفًا من هذا وفق السياسة المعتمدة؛ none يمنع إرسال أي بيانات',
   'Data classified above this level is never sent to the provider, per the approved policy; none blocks sending any data',
   18, NULL, NULL, NULL),
  ('AI_BASE_URL', 'ai', 'text', '', '', NULL, 'AI_ENABLED', NULL,
   'عنوان خادم النموذج', 'Model server URL',
   'عنوان الواجهة المتوافقة (مثل https://llm.internal/v1)؛ يُقبل فقط إن كان مضيفه في القائمة المسموحة ببيئة الخادم',
   'Base URL of the compatible API (e.g. https://llm.internal/v1); accepted only if its host is in the server environment allow-list',
   19, NULL, NULL, 300),
  ('AI_MODEL', 'ai', 'text', '', '', NULL, 'AI_ENABLED', NULL,
   'النموذج', 'Model', 'اسم النموذج المعتمد لدى المزوّد', 'Name of the approved model at the provider', 20, NULL, NULL, 100),
  ('AI_TRANSCRIPTION_MODEL', 'ai', 'text', '', '', NULL, 'AI_ENABLED', NULL,
   'نموذج التفريغ الصوتي', 'Transcription model', 'فارغ = خدمة التفريغ غير متاحة', 'Empty = no transcription service available', 21, NULL, NULL, 100),
  ('AI_TIMEOUT_SECONDS', 'ai', 'integer', '60', '60', NULL, 'AI_ENABLED', NULL,
   'المهلة الزمنية (ثانية)', 'Timeout (seconds)', 'أقصى مدة لانتظار رد المزوّد قبل اعتبار الطلب فاشلًا', 'Maximum wait for a provider response before the request fails',
   22, 5, 300, NULL),
  ('AI_DAILY_REQUESTS_PER_USER', 'ai', 'integer', '50', '50', NULL, 'AI_ENABLED', NULL,
   'حد الطلبات اليومي لكل مستخدم', 'Daily requests per user', 'يُرفض ما يتجاوزه حتى اليوم التالي (بتوقيت الرياض)', 'Requests beyond it are refused until the next day (Riyadh time)',
   23, 1, 10000, NULL),
  ('AI_MONTHLY_REQUEST_LIMIT', 'ai', 'integer', '3000', '3000', NULL, 'AI_ENABLED', NULL,
   'حد الطلبات الشهري', 'Monthly request limit', 'إجمالي الطلبات المسموحة شهريًا لكل المستخدمين', 'Total requests allowed per month across all users',
   24, 10, 1000000, NULL),
  ('AI_MONTHLY_TOKEN_LIMIT', 'ai', 'integer', '3000000', '3000000', NULL, 'AI_ENABLED', NULL,
   'حد الاستهلاك الشهري (وحدات)', 'Monthly token limit', 'إجمالي وحدات الاستهلاك (tokens) كما يبلغها المزوّد، أو تقديرها إن لم يبلغها', 'Total tokens as reported by the provider, or estimated when not reported',
   25, 1000, 1000000000, NULL),
  ('AI_USAGE_ALERT_PERCENT', 'ai', 'integer', '80', '80', NULL, 'AI_ENABLED', NULL,
   'نسبة التنبيه عند الاقتراب من الحد', 'Usage alert threshold (%)', 'يُنبَّه مديرو النظام عند بلوغ هذه النسبة من أي حد شهري', 'System admins are alerted when any monthly limit reaches this share',
   26, 50, 99, NULL),
  ('AI_RETENTION_DAYS', 'ai', 'integer', '90', '90', NULL, 'AI_ENABLED', NULL,
   'مدة الاحتفاظ بالمسودات غير المحفوظة (يوم)', 'Retention of unsaved drafts (days)',
   'بعدها يُحذف محتوى المسودات التي لم يحفظها أحد ويبقى سجلها؛ المحفوظ والمطبق لا يُحذف',
   'After this, the content of drafts nobody saved is purged and their record kept; saved and applied outputs are never purged',
   27, 7, 3650, NULL)
ON CONFLICT (key) DO NOTHING;

-- ============ 4. مخزن الأسرار: مفاتيح API مشفرة (AES-256-GCM) بمفتاح رئيسي من بيئة الخادم ============
CREATE TABLE config.secrets (
  name         text PRIMARY KEY CHECK (name ~ '^[A-Z][A-Z0-9_]+$'),
  ciphertext   text NOT NULL,                 -- nonce ‖ tag ‖ ciphertext بترميز base64
  key_id       text NOT NULL,                 -- بصمة المفتاح الرئيسي المستخدم (للتدوير)
  fingerprint  text NOT NULL,                 -- أول 8 خانات من SHA-256 للقيمة: للتعرف لا للاسترجاع
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid REFERENCES iam.users(id)
);
CREATE TABLE config.secret_history (
  id             uuid PRIMARY KEY,
  name           text NOT NULL,
  action         text NOT NULL CHECK (action IN ('set','cleared','denied')),
  fingerprint    text,
  reason         text,
  changed_by     uuid REFERENCES iam.users(id),
  actor_display  text NOT NULL,
  changed_at     timestamptz NOT NULL DEFAULT now()
);
REVOKE UPDATE, DELETE, TRUNCATE ON config.secret_history FROM ccms_app;
GRANT SELECT, INSERT ON config.secret_history TO ccms_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON config.secrets TO ccms_app;

-- ============ 5. الصلاحيات ============
INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('ai.use',       'body',   'استخدام وظائف الذكاء الاصطناعي على سجلات الجهة التي يطّلع عليها (مسودات للمراجعة فقط)'),
  ('ai.logs.view', 'system', 'الاطلاع على سجل تشغيل الذكاء الاصطناعي والاستهلاك (دون محتوى)')
ON CONFLICT DO NOTHING;
INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('chair','ai.use'), ('vice_chair','ai.use'), ('secretary','ai.use'), ('member','ai.use'),
  ('governance_officer','ai.use'), ('executive','ai.use'),
  ('system_admin','ai.logs.view'), ('auditor','ai.logs.view'), ('governance_officer','ai.logs.view')
ON CONFLICT DO NOTHING;

-- ============ 6. المخرجات: مسودات قابلة للمراجعة، لا تُحذف إن حُفظت ============
CREATE TABLE ai.outputs (
  id              uuid PRIMARY KEY,
  function        text NOT NULL CHECK (function IN ('minutes_summary','agenda_draft','minutes_draft','transcription','extract_decisions',
                                                     'extract_tasks','link_task','smart_search','executive_summary','ask')),
  subject_type    text NOT NULL CHECK (subject_type IN ('minutes','meeting','body','task','query','portfolio')),
  subject_id      uuid,
  body_id         uuid REFERENCES bodies.bodies(id),
  requested_by    uuid NOT NULL REFERENCES iam.users(id),
  status          text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','saved','applied','discarded','expired','held')),
  content         jsonb,                       -- يُفرغ عند انتهاء الاحتفاظ لغير المحفوظ
  citations       jsonb NOT NULL DEFAULT '[]', -- [{ref, type, id, label, link}] — مراجع لسجلات حقيقية فقط
  warnings        jsonb NOT NULL DEFAULT '[]',
  provider        text NOT NULL,
  model           text NOT NULL,
  test_only       boolean NOT NULL,
  prompt_version  text NOT NULL,
  input_sha256    text NOT NULL,
  classification  text NOT NULL,
  source_count    int  NOT NULL DEFAULT 0,
  held_reason     text,
  reviewed_by     uuid REFERENCES iam.users(id),
  reviewed_at     timestamptz,
  review_note     text,
  applied_refs    jsonb NOT NULL DEFAULT '[]', -- ما أنشأه المستخدم بنفسه من المسودة عبر الواجهات الأساسية
  purged_at       timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  CHECK (status <> 'held' OR held_reason IS NOT NULL),
  CHECK (purged_at IS NULL OR status IN ('expired','discarded'))
);
CREATE INDEX outputs_subject_idx ON ai.outputs (subject_type, subject_id, created_at DESC);
CREATE INDEX outputs_requester_idx ON ai.outputs (requested_by, created_at DESC);
REVOKE DELETE, TRUNCATE ON ai.outputs FROM ccms_app;

-- ============ 7. سجل التشغيل: بيانات تشغيلية فقط، دون نصوص المدخلات أو المخرجات ============
CREATE TABLE ai.requests (
  id                 uuid PRIMARY KEY,
  function           text NOT NULL,
  user_id            uuid REFERENCES iam.users(id),
  body_id            uuid REFERENCES bodies.bodies(id),
  subject_type       text,
  subject_id         uuid,
  status             text NOT NULL CHECK (status IN ('succeeded','failed','timeout','provider_unavailable','not_configured','invalid_output',
                                                      'blocked_disabled','blocked_classification','blocked_quota','held','no_sources')),
  error_code         text,
  duration_ms        int,
  input_tokens       int,
  output_tokens      int,
  usage_estimated    boolean NOT NULL DEFAULT false,
  provider           text,
  model              text,
  prompt_version     text,
  input_sha256       text,
  source_count       int,
  excluded_sources   int NOT NULL DEFAULT 0,
  redactions         int NOT NULL DEFAULT 0,
  max_classification text,
  output_id          uuid REFERENCES ai.outputs(id),
  job_id             uuid,
  counted            boolean NOT NULL DEFAULT false,  -- يدخل في حدود الاستخدام (طلب وصل للمزوّد)
  created_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX requests_user_day_idx ON ai.requests (user_id, created_at) WHERE counted;
CREATE INDEX requests_created_idx ON ai.requests (created_at DESC);
REVOKE UPDATE, DELETE, TRUNCATE ON ai.requests FROM ccms_app;
GRANT SELECT, INSERT ON ai.requests TO ccms_app;

-- ============ 8. المهام غير المتزامنة (التفريغ الصوتي) وسياسة التعطيل ============
CREATE TABLE ai.jobs (
  id             uuid PRIMARY KEY,
  kind           text NOT NULL CHECK (kind IN ('transcription')),
  meeting_id     uuid NOT NULL REFERENCES meetings.meetings(id),
  body_id        uuid NOT NULL REFERENCES bodies.bodies(id),
  document_id    uuid NOT NULL REFERENCES docs.documents(id),
  status         text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','completed','failed','held','cancelled')),
  consent        jsonb NOT NULL,              -- {confirmed, reference, confirmedBy, confirmedAt, attendees}
  attempts       int NOT NULL DEFAULT 0,
  last_error     text,
  held_reason    text,
  output_id      uuid REFERENCES ai.outputs(id),
  requested_by   uuid NOT NULL REFERENCES iam.users(id),
  closed_by      uuid REFERENCES iam.users(id),
  close_reason   text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  CHECK ((consent->>'confirmed')::boolean IS TRUE),
  CHECK (status <> 'held' OR held_reason IS NOT NULL),
  CHECK (status <> 'cancelled' OR close_reason IS NOT NULL)
);
REVOKE DELETE, TRUNCATE ON ai.jobs FROM ccms_app;
CREATE TABLE ai.job_events (
  id          uuid PRIMARY KEY,
  job_id      uuid NOT NULL REFERENCES ai.jobs(id),
  kind        text NOT NULL,
  actor_user_id uuid REFERENCES iam.users(id),
  data        jsonb NOT NULL DEFAULT '{}',
  created_at  timestamptz NOT NULL DEFAULT now()
);
REVOKE UPDATE, DELETE, TRUNCATE ON ai.job_events FROM ccms_app;
GRANT SELECT, INSERT ON ai.job_events TO ccms_app;

-- تنبيهات الاقتراب من الحدود: مرة واحدة لكل (شهر، مقياس، عتبة)
CREATE TABLE ai.usage_alerts (
  period      text NOT NULL,      -- YYYY-MM
  metric      text NOT NULL CHECK (metric IN ('requests','tokens')),
  threshold   int  NOT NULL,
  value       bigint NOT NULL,
  limit_value bigint NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (period, metric, threshold)
);

-- ============ 9. التسجيلات الصوتية مستندات مرتبطة بالاجتماع ============
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint
           WHERE conrelid = 'docs.document_links'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%purpose%' LOOP
    EXECUTE format('ALTER TABLE docs.document_links DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
ALTER TABLE docs.document_links ADD CONSTRAINT document_links_purpose_ck CHECK (purpose IN
  ('formation_decision','agenda_memo','attachment','evidence','wet_signature','approval_evidence','minutes_pdf','signed_pdf','deliverable','assignment_letter',
   'signature_document','ai_recording'));

-- ============ 10. تطبيع النص العربي للبحث التقليدي (أساسي، يعمل دون الذكاء الاصطناعي) ============
-- يوحّد صور الألف والتاء المربوطة والألف المقصورة، ويحذف التشكيل والتطويل.
CREATE OR REPLACE FUNCTION public.ar_norm(t text) RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
  SELECT regexp_replace(translate(lower(coalesce(t, '')), 'أإآٱىة', 'اااايه'), '[ًٌٍَُِّْـ]', '', 'g')
$$;
