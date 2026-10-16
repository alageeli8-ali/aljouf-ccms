-- V009: المرحلة 3 — المحاضر (القوالب، والإصدارات، والتعليقات، ودورة الاعتماد القابلة للتهيئة)،
-- والقرارات (السجل المستقل، والتعديل والإلغاء، والرفع لجهة أعلى)، والتصويت (المقترحات والأصوات والتحفظات).
-- لا حذف ولا تعديل صامت: الإصدارات والأصوات وقرارات الاعتماد وسجل حالات القرار للإضافة فقط.

-- ============================ سياسة المحضر لكل جهة ============================
-- دورة الاعتماد تختلف من جهة لأخرى: لا يُفرض توقيع ولا موافقة موحدة (المرحلة 3، ب).
ALTER TABLE bodies.body_policies ADD COLUMN minutes_workflow jsonb;
UPDATE bodies.body_policies SET minutes_workflow = jsonb_build_object(
  'secretaryReview', false,
  'chairReview', true,
  'memberReview', true,
  'memberReviewDays', minutes_review_days,
  'signatures', CASE WHEN COALESCE((signoff_rule->>'required')::boolean, false) THEN 'present_members' ELSE 'none' END,
  'signatureFallback', 'attestation',
  'finalApproval', 'chair',
  'amendmentReapproval', 'full',
  'templateCode', 'standard');
ALTER TABLE bodies.body_policies ALTER COLUMN minutes_workflow SET NOT NULL;

-- ============================ قوالب المحاضر ============================
CREATE TABLE minutes.templates (
  id          uuid PRIMARY KEY,
  code        text NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_-]{1,39}$'),
  name_ar     text NOT NULL,
  name_en     text,
  sections    jsonb NOT NULL,      -- [{key, titleAr, titleEn, kind: auto|text, source?, defaultBody?}]
  is_default  boolean NOT NULL DEFAULT false,
  status      text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  version     int NOT NULL DEFAULT 1,
  created_at  timestamptz NOT NULL DEFAULT now(),
  created_by  uuid REFERENCES iam.users(id),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  updated_by  uuid REFERENCES iam.users(id)
);
CREATE UNIQUE INDEX templates_one_default ON minutes.templates (is_default) WHERE is_default;
CREATE TRIGGER templates_touch BEFORE UPDATE ON minutes.templates FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

INSERT INTO minutes.templates (id, code, name_ar, name_en, is_default, sections) VALUES
 ('0192f000-0000-7000-8000-000000000001', 'standard', 'المحضر الرسمي الكامل', 'Full official minutes', true, '[
   {"key":"meeting_info","titleAr":"بيانات الاجتماع","titleEn":"Meeting details","kind":"auto","source":"meeting_info"},
   {"key":"opening","titleAr":"الافتتاح","titleEn":"Opening","kind":"text","defaultBody":"افتتح رئيس الاجتماع الجلسة بالترحيب بالحضور، ثم استعرض جدول الأعمال."},
   {"key":"attendance","titleAr":"الحضور والغياب","titleEn":"Attendance","kind":"auto","source":"attendance"},
   {"key":"quorum","titleAr":"النصاب","titleEn":"Quorum","kind":"auto","source":"quorum"},
   {"key":"agenda","titleAr":"جدول الأعمال","titleEn":"Agenda","kind":"auto","source":"agenda"},
   {"key":"discussions","titleAr":"المناقشات والتوصيات","titleEn":"Discussions and recommendations","kind":"auto","source":"discussions"},
   {"key":"decisions","titleAr":"القرارات والتوصيات","titleEn":"Decisions and recommendations","kind":"auto","source":"decisions"},
   {"key":"votes","titleAr":"نتائج التصويت","titleEn":"Voting results","kind":"auto","source":"votes"},
   {"key":"reservations","titleAr":"التحفظات","titleEn":"Reservations","kind":"auto","source":"reservations"},
   {"key":"tasks","titleAr":"المهام الناتجة","titleEn":"Resulting tasks","kind":"auto","source":"tasks"},
   {"key":"attachments","titleAr":"المرفقات","titleEn":"Attachments","kind":"auto","source":"attachments"},
   {"key":"closing","titleAr":"الختام","titleEn":"Closing","kind":"text","defaultBody":"واختُتم الاجتماع، وعلى ذلك جرى التوقيع."},
   {"key":"approval","titleAr":"بيانات المراجعة والاعتماد","titleEn":"Review and approval","kind":"auto","source":"approval"}]'),
 ('0192f000-0000-7000-8000-000000000002', 'brief', 'محضر مختصر', 'Brief minutes', false, '[
   {"key":"meeting_info","titleAr":"بيانات الاجتماع","titleEn":"Meeting details","kind":"auto","source":"meeting_info"},
   {"key":"attendance","titleAr":"الحضور والغياب","titleEn":"Attendance","kind":"auto","source":"attendance"},
   {"key":"quorum","titleAr":"النصاب","titleEn":"Quorum","kind":"auto","source":"quorum"},
   {"key":"decisions","titleAr":"القرارات والتوصيات","titleEn":"Decisions and recommendations","kind":"auto","source":"decisions"},
   {"key":"tasks","titleAr":"المهام الناتجة","titleEn":"Resulting tasks","kind":"auto","source":"tasks"},
   {"key":"notes","titleAr":"ملاحظات","titleEn":"Notes","kind":"text"},
   {"key":"approval","titleAr":"بيانات المراجعة والاعتماد","titleEn":"Review and approval","kind":"auto","source":"approval"}]');

-- ============================ المحاضر ============================
ALTER TABLE minutes.minutes DROP CONSTRAINT minutes_status_check;
UPDATE minutes.minutes SET status = CASE status
  WHEN 'internal_review' THEN 'secretary_review'
  WHEN 'pending_signoffs' THEN 'pending_signatures'
  WHEN 'pending_approval' THEN 'pending_final_approval'
  WHEN 'amended' THEN 'approved'
  ELSE status END;
ALTER TABLE minutes.minutes ADD CONSTRAINT minutes_status_check CHECK (status IN (
  'draft','secretary_review','chair_review','member_review','pending_signatures','pending_final_approval','approved','returned'));

ALTER TABLE minutes.minutes
  ADD COLUMN template_id          uuid REFERENCES minutes.templates(id),
  ADD COLUMN workflow             jsonb,                 -- لقطة دورة الاعتماد من سياسة الجهة المثبتة على الاجتماع
  ADD COLUMN approved_version_id  uuid REFERENCES minutes.minutes_versions(id),
  ADD COLUMN review_deadline      timestamptz,
  ADD COLUMN status_reason        text,
  ADD COLUMN status_changed_at    timestamptz NOT NULL DEFAULT now(),
  ADD COLUMN amendment_no         int NOT NULL DEFAULT 0,
  ADD COLUMN amendment_reason     text,
  ADD COLUMN pdf_document_id      uuid REFERENCES docs.documents(id),
  ADD COLUMN pdf_sha256           text,
  ADD COLUMN verification_code    text UNIQUE;
UPDATE minutes.minutes SET template_id = '0192f000-0000-7000-8000-000000000001';
UPDATE minutes.minutes mi SET workflow = p.minutes_workflow
FROM meetings.meetings m JOIN bodies.body_policies p ON p.id = m.policy_id WHERE m.id = mi.meeting_id;
UPDATE minutes.minutes SET workflow = '{"secretaryReview":false,"chairReview":true,"memberReview":true,"memberReviewDays":3,"signatures":"none","signatureFallback":"attestation","finalApproval":"chair","amendmentReapproval":"full","templateCode":"standard"}'
WHERE workflow IS NULL;
ALTER TABLE minutes.minutes ALTER COLUMN workflow SET NOT NULL;
ALTER TABLE minutes.minutes ALTER COLUMN template_id SET NOT NULL;

-- الإصدار: المحتوى المحرر + لقطة البيانات الآلية (الحضور، النصاب، القرارات، الأصوات، المهام…) ببصمة واحدة
ALTER TABLE minutes.minutes_versions
  ADD COLUMN snapshot        jsonb NOT NULL DEFAULT '{}',
  ADD COLUMN change_summary  text,
  ADD COLUMN amendment_no    int NOT NULL DEFAULT 0,
  ADD COLUMN created_in_status text;
REVOKE UPDATE, DELETE, TRUNCATE ON minutes.minutes_versions FROM ccms_app;
GRANT SELECT, INSERT ON minutes.minutes_versions TO ccms_app;

-- الإقرار والتوقيع لكل إصدار
ALTER TABLE minutes.minutes_signoffs
  ADD COLUMN signer_role           text NOT NULL DEFAULT 'member' CHECK (signer_role IN ('member','chair','secretary')),
  ADD COLUMN evidence_document_id  uuid REFERENCES docs.documents(id),
  ADD COLUMN invalidated_reason    text,
  ADD COLUMN method_reason         text;   -- لماذا اختيرت الطريقة: esign / esign_disabled / esign_unavailable / policy

-- التعليقات والملاحظات على الإصدار
CREATE TABLE minutes.minutes_comments (
  id                  uuid PRIMARY KEY,
  minutes_id          uuid NOT NULL REFERENCES minutes.minutes(id),
  minutes_version_id  uuid REFERENCES minutes.minutes_versions(id),
  author_user_id      uuid NOT NULL REFERENCES iam.users(id),
  anchor              text NOT NULL,             -- مفتاح القسم أو معرّف البند أو القرار
  text                text NOT NULL,
  status              text NOT NULL DEFAULT 'open' CHECK (status IN ('open','accepted','rejected')),
  resolution_note     text,
  resolved_by         uuid REFERENCES iam.users(id),
  resolved_at         timestamptz,
  created_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX minutes_comments_idx ON minutes.minutes_comments (minutes_id, status);

-- كل قرار في دورة الاعتماد: من، وبأي صفة، وبأي طريقة، وعلى أي بصمة (للإضافة فقط)
CREATE TABLE minutes.minutes_approvals (
  id                    uuid PRIMARY KEY,
  minutes_id            uuid NOT NULL REFERENCES minutes.minutes(id),
  minutes_version_id    uuid REFERENCES minutes.minutes_versions(id),
  step                  text NOT NULL CHECK (step IN ('draft','secretary_review','chair_review','member_review','pending_signatures','pending_final_approval','approved','returned')),
  decision              text NOT NULL CHECK (decision IN ('submitted','step_approved','returned','approved','amendment_opened','signatures_completed')),
  actor_user_id         uuid NOT NULL REFERENCES iam.users(id),
  acting_capacity       text,                    -- secretary / chair / chair_delegate / editor / governance_officer
  on_behalf_of_user_id  uuid REFERENCES iam.users(id),
  delegation_id         uuid REFERENCES iam.delegations(id),
  method                text CHECK (method IN ('in_system','recorded_external','electronic_signature','attestation','wet_signature')),
  content_hash          text,
  note                  text,
  external_approver     text,                    -- مسار الاعتماد البديل: اسم المعتمد خارج النظام وصفته
  external_reference    text,
  external_date         date,
  evidence_document_id  uuid REFERENCES docs.documents(id),
  created_at            timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX minutes_approvals_idx ON minutes.minutes_approvals (minutes_id, created_at);
REVOKE UPDATE, DELETE, TRUNCATE ON minutes.minutes_approvals FROM ccms_app;
GRANT SELECT, INSERT ON minutes.minutes_approvals TO ccms_app;

-- ============================ القرارات ============================
ALTER TABLE resolutions.resolutions DROP CONSTRAINT resolutions_status_check;
ALTER TABLE resolutions.resolutions ADD CONSTRAINT resolutions_status_check CHECK (status IN (
  'proposed','withdrawn','rejected','deferred','issued','referred','in_execution','executed','closed','cancelled','superseded'));

ALTER TABLE resolutions.resolutions
  ADD COLUMN category                       text NOT NULL DEFAULT 'administrative'
    CHECK (category IN ('administrative','financial','technical','policy','organizational','procedural','other')),
  ADD COLUMN position                       int NOT NULL DEFAULT 0,
  ADD COLUMN minutes_version_id             uuid REFERENCES minutes.minutes_versions(id),
  ADD COLUMN approved_at                    timestamptz,
  ADD COLUMN authority_text                 text,
  ADD COLUMN target_body_id                 uuid REFERENCES bodies.bodies(id),
  ADD COLUMN target_authority_text          text,
  ADD COLUMN referral_outcome               text CHECK (referral_outcome IN ('adopted','rejected','modified')),
  ADD COLUMN referral_note                  text,
  ADD COLUMN referral_decided_at            timestamptz,
  ADD COLUMN responsible_org_unit_id        uuid REFERENCES org.org_units(id),
  ADD COLUMN responsible_external_entity_id uuid REFERENCES org.external_entities(id),
  ADD COLUMN responsible_text               text,
  ADD COLUMN supersedes_resolution_id       uuid REFERENCES resolutions.resolutions(id),
  ADD COLUMN supersede_kind                 text CHECK (supersede_kind IN ('amends','revokes')),
  ADD COLUMN adoption_method                text NOT NULL DEFAULT 'consensus' CHECK (adoption_method IN ('vote','consensus','circulation','chair_decision')),
  ADD COLUMN vote_summary                   jsonb,
  ADD COLUMN status_reason                  text,
  ADD COLUMN updated_by                     uuid REFERENCES iam.users(id),
  ADD CONSTRAINT resolutions_supersede_pair CHECK ((supersedes_resolution_id IS NULL) = (supersede_kind IS NULL)),
  ADD CONSTRAINT resolutions_not_self CHECK (supersedes_resolution_id IS NULL OR supersedes_resolution_id <> id),
  ADD CONSTRAINT resolutions_number_issued CHECK (number IS NULL OR status NOT IN ('proposed','withdrawn','rejected','deferred'));
CREATE INDEX resolutions_minutes_idx ON resolutions.resolutions (minutes_id, position);
CREATE INDEX resolutions_meeting_idx ON resolutions.resolutions (meeting_id);

-- سجل حالات القرار (للإضافة فقط)
CREATE TABLE resolutions.resolution_status_history (
  id             uuid PRIMARY KEY,
  resolution_id  uuid NOT NULL REFERENCES resolutions.resolutions(id),
  from_status    text,
  to_status      text NOT NULL,
  reason         text,
  reference      text,
  changed_by     uuid REFERENCES iam.users(id),
  changed_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX resolution_history_idx ON resolutions.resolution_status_history (resolution_id, changed_at);
REVOKE UPDATE, DELETE, TRUNCATE ON resolutions.resolution_status_history FROM ccms_app;
GRANT SELECT, INSERT ON resolutions.resolution_status_history TO ccms_app;
INSERT INTO resolutions.resolution_status_history (id, resolution_id, from_status, to_status, changed_by, changed_at)
SELECT gen_random_uuid(), id, NULL, status, created_by, created_at FROM resolutions.resolutions;

-- المهام الناتجة: مسودة مرتبطة بقرار مقترح تُفعَّل عند اعتماد المحضر (08 §2.4)
ALTER TABLE tasks.tasks ALTER COLUMN reference_no DROP NOT NULL;

-- ============================ التصويت ============================
CREATE SCHEMA IF NOT EXISTS deliberation;
GRANT USAGE ON SCHEMA deliberation TO ccms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA deliberation GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ccms_app;

CREATE TABLE deliberation.motions (
  id               uuid PRIMARY KEY,
  meeting_id       uuid NOT NULL REFERENCES meetings.meetings(id),
  agenda_item_id   uuid REFERENCES meetings.agenda_items(id),
  resolution_id    uuid NOT NULL REFERENCES resolutions.resolutions(id),
  round            int NOT NULL DEFAULT 1,
  method           text NOT NULL CHECK (method IN ('consensus','show_of_hands','electronic','secret','circulation')),
  status           text NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed','withdrawn')),
  opened_at        timestamptz NOT NULL DEFAULT now(),
  opened_by        uuid NOT NULL REFERENCES iam.users(id),
  deadline_at      timestamptz,
  closed_at        timestamptz,
  closed_by        uuid REFERENCES iam.users(id),
  close_reason     text,
  result           text CHECK (result IN ('adopted','rejected','tied_unresolved','no_quorum','withdrawn')),
  tally            jsonb,
  rule_snapshot    jsonb NOT NULL,
  eligible         jsonb NOT NULL,     -- [{principalUserId, name, role, castBy, capacity}] لقطة الأهلية عند الفتح
  quorum_snapshot  jsonb,
  UNIQUE (resolution_id, round),
  CHECK ((status = 'open') = (result IS NULL))
);
CREATE UNIQUE INDEX motions_one_open ON deliberation.motions (resolution_id) WHERE status = 'open';
CREATE INDEX motions_meeting_idx ON deliberation.motions (meeting_id, opened_at);

-- الصوت الفردي؛ في السري يُحذف الأصيل والمدلي ويبقى الاختيار فقط
CREATE TABLE deliberation.votes (
  id                 uuid PRIMARY KEY,
  motion_id          uuid NOT NULL REFERENCES deliberation.motions(id),
  principal_user_id  uuid REFERENCES iam.users(id),
  cast_by_user_id    uuid REFERENCES iam.users(id),
  capacity           text CHECK (capacity IN ('self','proxy','alternate','recorded')),
  choice             text NOT NULL CHECK (choice IN ('for','against','abstain')),
  cast_at            timestamptz NOT NULL,
  recorded_by        uuid REFERENCES iam.users(id),
  UNIQUE (motion_id, principal_user_id)
);
-- المشاركة تمنع التصويت المكرر حتى في السري دون كشف الاختيار
CREATE TABLE deliberation.vote_participation (
  motion_id          uuid NOT NULL REFERENCES deliberation.motions(id),
  principal_user_id  uuid NOT NULL REFERENCES iam.users(id),
  cast_by_user_id    uuid NOT NULL REFERENCES iam.users(id),
  cast_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (motion_id, principal_user_id)
);
CREATE TABLE deliberation.reservations (
  id             uuid PRIMARY KEY,
  motion_id      uuid REFERENCES deliberation.motions(id),
  resolution_id  uuid NOT NULL REFERENCES resolutions.resolutions(id),
  user_id        uuid NOT NULL REFERENCES iam.users(id),
  kind           text NOT NULL DEFAULT 'reservation' CHECK (kind IN ('reservation','dissent')),
  text           text NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX reservations_resolution_idx ON deliberation.reservations (resolution_id);
GRANT SELECT, INSERT, UPDATE ON deliberation.motions TO ccms_app;
GRANT SELECT, INSERT ON deliberation.votes, deliberation.vote_participation, deliberation.reservations TO ccms_app;
REVOKE UPDATE, DELETE, TRUNCATE ON deliberation.votes, deliberation.vote_participation, deliberation.reservations FROM ccms_app;
REVOKE DELETE, TRUNCATE ON deliberation.motions FROM ccms_app;

-- المقترح المغلق لا تتغير نتيجته
CREATE OR REPLACE FUNCTION deliberation.motions_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'open' THEN
    RAISE EXCEPTION 'المقترح % مغلق ولا يُعدل', OLD.id USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER motions_guard BEFORE UPDATE ON deliberation.motions FOR EACH ROW EXECUTE FUNCTION deliberation.motions_guard();

ALTER TABLE meetings.quorum_checks DROP CONSTRAINT quorum_checks_context_check;
ALTER TABLE meetings.quorum_checks ADD CONSTRAINT quorum_checks_context_check
  CHECK (context IN ('forecast','opening','departure','manual','circulation','vote'));

-- ============================ منع التكرار عند إعادة المحاولة ============================
-- كل طلب إنشاء يحمل Idempotency-Key؛ إعادة الطلب نفسه تُرجع النتيجة الأولى دون إنشاء سجل ثانٍ.
CREATE TABLE config.idempotency_keys (
  user_id      uuid NOT NULL REFERENCES iam.users(id),
  key          text NOT NULL CHECK (length(key) BETWEEN 8 AND 100),
  route        text NOT NULL,
  request_hash text NOT NULL,
  status       text NOT NULL DEFAULT 'in_progress' CHECK (status IN ('in_progress','completed')),
  status_code  int,
  location     text,
  response     jsonb,
  created_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, key)
);
CREATE INDEX idempotency_created_idx ON config.idempotency_keys (created_at);

-- ============================ المستندات ============================
ALTER TABLE docs.document_links DROP CONSTRAINT document_links_purpose_check;
ALTER TABLE docs.document_links ADD CONSTRAINT document_links_purpose_check
  CHECK (purpose IN ('formation_decision','agenda_memo','attachment','evidence','minutes_pdf','wet_signature','signed_pdf','approval_evidence'));

-- ============================ الصلاحيات ============================
INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('minutes.comment',          'body',   'إبداء الملاحظات على المحضر أثناء المراجعة'),
  ('minutes.review_secretary', 'body',   'مراجعة أمين السر للمحضر'),
  ('minutes.templates.manage', 'system', 'إدارة قوالب المحاضر'),
  ('vote.cast',                'body',   'الإدلاء بالصوت'),
  ('vote.manage',              'body',   'فتح التصويت وإغلاقه وتسجيل الأصوات برفع الأيدي'),
  ('vote.view_individual',     'body',   'الاطلاع على الأصوات الفردية في التصويت العلني');

INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('system_admin', 'minutes.templates.manage'), ('governance_officer', 'minutes.templates.manage'),
  ('governance_officer', 'minutes.view'), ('governance_officer', 'vote.view_individual'),
  ('auditor', 'vote.view_individual'),
  ('chair', 'minutes.comment'), ('chair', 'minutes.sign'), ('chair', 'vote.cast'), ('chair', 'vote.manage'), ('chair', 'vote.view_individual'),
  ('vice_chair', 'minutes.comment'), ('vice_chair', 'vote.cast'),
  ('secretary', 'minutes.comment'), ('secretary', 'minutes.review_secretary'), ('secretary', 'vote.manage'), ('secretary', 'vote.view_individual'),
  ('member', 'minutes.comment'), ('member', 'vote.cast'),
  ('alternate', 'minutes.comment'), ('alternate', 'vote.cast')
ON CONFLICT DO NOTHING;
