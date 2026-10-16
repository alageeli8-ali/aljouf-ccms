-- V010: المرحلة 4 — دورة المهام والتكليفات القابلة للتهيئة، والمخرجات ومعايير القبول، والتحقق، والتمديد، والتصعيد،
-- وبوابة الجهات الخارجية، وخطابات التكليف. (08 §4، 20-phase4-delivery)

-- ============ 1. حالات المهمة وانتقالاتها (قابلة للتهيئة) ============
CREATE TABLE tasks.workflow_statuses (
  code         text PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9_]{1,39}$'),
  name_ar      text NOT NULL,
  name_en      text NOT NULL,
  -- الفئة تحدد دلالة الحالة في الحسابات: مفتوحة/نشطة/منتظرة/مراجعة/منجزة/مغلقة، والمتأخرة «مشتقة» لا تُخزن
  category     text NOT NULL CHECK (category IN ('new','open','active','waiting','review','done','closed','derived')),
  color        text NOT NULL DEFAULT 'default',
  sort_order   int  NOT NULL DEFAULT 0,
  is_system    boolean NOT NULL DEFAULT false,
  active       boolean NOT NULL DEFAULT true,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   uuid REFERENCES iam.users(id)
);

INSERT INTO tasks.workflow_statuses (code, name_ar, name_en, category, color, sort_order, is_system) VALUES
  ('new',            'جديدة',            'New',             'new',     'default',   10, true),
  ('assigned',       'مسندة',            'Assigned',        'open',    'blue',      20, true),
  ('accepted',       'مقبولة',           'Accepted',        'open',    'geekblue',  30, true),
  ('in_progress',    'قيد التنفيذ',      'In progress',     'active',  'processing',40, true),
  ('on_hold',        'متوقفة',           'On hold',         'waiting', 'orange',    50, true),
  ('pending_review', 'بانتظار المراجعة', 'Pending review',  'review',  'purple',    60, true),
  ('completed',      'مكتملة',           'Completed',       'done',    'green',     70, true),
  ('rejected',       'مرفوضة',           'Rejected',        'waiting', 'volcano',   80, true),
  ('cancelled',      'ملغاة',            'Cancelled',       'closed',  'default',   90, true),
  ('overdue',        'متأخرة',           'Overdue',         'derived', 'red',       45, true);

CREATE TABLE tasks.workflow_transitions (
  id             uuid PRIMARY KEY,
  action         text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_]{1,39}$'),
  from_status    text NOT NULL REFERENCES tasks.workflow_statuses(code),
  to_status      text NOT NULL REFERENCES tasks.workflow_statuses(code),
  name_ar        text NOT NULL,
  name_en        text NOT NULL,
  -- الصلاحية المطلوبة في نطاق المهمة، ومن يُسمح له من أطرافها (فارغة = كل من يملك الصلاحية)
  permission     text NOT NULL REFERENCES iam.permissions(code),
  actors         text[] NOT NULL DEFAULT '{}' CHECK (actors <@ ARRAY['executor','follow_up','verifier','manager']::text[]),
  requires_reason boolean NOT NULL DEFAULT false,
  is_system      boolean NOT NULL DEFAULT false,
  active         boolean NOT NULL DEFAULT true,
  sort_order     int NOT NULL DEFAULT 0,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid REFERENCES iam.users(id),
  UNIQUE (from_status, action),
  CHECK (from_status <> to_status),
  CHECK (from_status <> 'overdue' AND to_status <> 'overdue')
);

-- ============ 2. صلاحيات المرحلة 4 ============
INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('task.comment',           'task',   'التعليق على المهمة'),
  ('task.extension.approve', 'body',   'البت في طلبات تمديد المهام'),
  ('task.escalate',          'body',   'التصعيد اليدوي للمهام'),
  ('task.record_external',   'task',   'تسجيل ردود الجهة الخارجية الواردة بالقنوات الرسمية'),
  ('task.letters.manage',    'body',   'إعداد خطابات التكليف واعتمادها وتسجيل صدورها'),
  ('task.workflow.manage',   'system', 'تهيئة حالات المهام وانتقالاتها وصلاحياتها');

INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('chair','task.comment'), ('vice_chair','task.comment'), ('secretary','task.comment'), ('member','task.comment'),
  ('task_executor','task.comment'), ('task_follow_up','task.comment'), ('external_representative','task.comment'), ('governance_officer','task.comment'),
  ('chair','task.extension.approve'), ('secretary','task.extension.approve'),
  ('chair','task.escalate'), ('secretary','task.escalate'), ('task_follow_up','task.escalate'),
  ('secretary','task.record_external'), ('task_follow_up','task.record_external'),
  ('chair','task.letters.manage'), ('secretary','task.letters.manage'), ('governance_officer','task.letters.manage'),
  ('system_admin','task.workflow.manage'), ('governance_officer','task.workflow.manage'),
  ('task_follow_up','task.verify'),
  ('governance_officer','task.update')
ON CONFLICT DO NOTHING;

INSERT INTO tasks.workflow_transitions (id, action, from_status, to_status, name_ar, name_en, permission, actors, requires_reason, is_system, sort_order) VALUES
  ('0192f004-0000-7000-8000-000000000001', 'assign',   'new',            'assigned',       'إسناد',                  'Assign',            'task.manage', '{}',                    false, true, 10),
  ('0192f004-0000-7000-8000-000000000002', 'accept',   'assigned',       'accepted',       'تأكيد الاستلام',         'Acknowledge',       'task.update', '{executor}',            false, true, 20),
  ('0192f004-0000-7000-8000-000000000003', 'reject',   'assigned',       'rejected',       'رفض التكليف',            'Decline',           'task.update', '{executor}',            true,  true, 30),
  ('0192f004-0000-7000-8000-000000000004', 'start',    'assigned',       'in_progress',    'بدء التنفيذ',            'Start',             'task.update', '{executor,follow_up}',  false, true, 40),
  ('0192f004-0000-7000-8000-000000000005', 'start',    'accepted',       'in_progress',    'بدء التنفيذ',            'Start',             'task.update', '{executor,follow_up}',  false, true, 41),
  ('0192f004-0000-7000-8000-000000000006', 'hold',     'in_progress',    'on_hold',        'إيقاف مؤقت',             'Put on hold',       'task.update', '{follow_up,manager}',   true,  true, 50),
  ('0192f004-0000-7000-8000-000000000007', 'hold',     'accepted',       'on_hold',        'إيقاف مؤقت',             'Put on hold',       'task.update', '{follow_up,manager}',   true,  true, 51),
  ('0192f004-0000-7000-8000-000000000008', 'resume',   'on_hold',        'in_progress',    'استئناف',                'Resume',            'task.update', '{follow_up,manager}',   false, true, 52),
  ('0192f004-0000-7000-8000-000000000009', 'submit',   'in_progress',    'pending_review', 'رفع المخرج للمراجعة',     'Submit for review', 'task.update', '{executor,follow_up}',  false, true, 60),
  ('0192f004-0000-7000-8000-000000000010', 'complete', 'in_progress',    'completed',      'إقرار الإنجاز',           'Declare completed', 'task.update', '{executor,follow_up}',  false, true, 61),
  ('0192f004-0000-7000-8000-000000000011', 'verify',   'pending_review', 'completed',      'التحقق والإغلاق',         'Verify and close',  'task.verify', '{}',                    false, true, 70),
  ('0192f004-0000-7000-8000-000000000012', 'return',   'pending_review', 'in_progress',    'إعادة للاستكمال',         'Return',            'task.verify', '{}',                    true,  true, 71),
  ('0192f004-0000-7000-8000-000000000013', 'reopen',   'completed',      'in_progress',    'إعادة فتح',              'Reopen',            'task.manage', '{}',                    true,  true, 80),
  ('0192f004-0000-7000-8000-000000000014', 'cancel',   'new',            'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 90),
  ('0192f004-0000-7000-8000-000000000015', 'cancel',   'assigned',       'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 91),
  ('0192f004-0000-7000-8000-000000000016', 'cancel',   'accepted',       'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 92),
  ('0192f004-0000-7000-8000-000000000017', 'cancel',   'in_progress',    'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 93),
  ('0192f004-0000-7000-8000-000000000018', 'cancel',   'on_hold',        'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 94),
  ('0192f004-0000-7000-8000-000000000019', 'cancel',   'rejected',       'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 95),
  ('0192f004-0000-7000-8000-000000000020', 'cancel',   'pending_review', 'cancelled',      'إلغاء',                  'Cancel',            'task.manage', '{}',                    true,  true, 96);

-- ============ 3. بطاقة المهمة ============
-- إزالة قيود الفحص القديمة (غير المسماة) ثم إعادة بنائها
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'tasks.tasks'::regclass AND contype = 'c' LOOP
    EXECUTE format('ALTER TABLE tasks.tasks DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;

UPDATE tasks.tasks SET status = CASE status
  WHEN 'draft' THEN 'new' WHEN 'pending_verification' THEN 'pending_review' WHEN 'returned' THEN 'in_progress' ELSE status END;
ALTER TABLE tasks.tasks ALTER COLUMN status SET DEFAULT 'new';
ALTER TABLE tasks.tasks ADD CONSTRAINT tasks_status_fk FOREIGN KEY (status) REFERENCES tasks.workflow_statuses(code);
ALTER TABLE tasks.tasks ADD CONSTRAINT tasks_status_not_derived CHECK (status <> 'overdue');

-- المكلَّف: عضو، أو موظف، أو إدارة، أو جهة خارجية (FR-TSK-02)
UPDATE tasks.tasks t SET assignee_type = CASE WHEN EXISTS (
    SELECT 1 FROM bodies.body_members bm WHERE bm.body_id = t.body_id AND bm.user_id = t.assignee_user_id) THEN 'member' ELSE 'employee' END
  WHERE assignee_type = 'user';

ALTER TABLE tasks.tasks
  ADD COLUMN executor_user_id       uuid REFERENCES iam.users(id),
  ADD COLUMN executor_contact       text,
  ADD COLUMN executing_org_unit_id  uuid REFERENCES org.org_units(id),
  ADD COLUMN acceptance_criteria    text,
  ADD COLUMN external_brief         text,
  ADD COLUMN share_resolution_with_entity boolean NOT NULL DEFAULT false,
  ADD COLUMN delay_reason           text,
  ADD COLUMN assigned_at            timestamptz,
  ADD COLUMN accepted_at            timestamptz,
  ADD COLUMN started_at             timestamptz,
  ADD COLUMN submitted_at           timestamptz,
  ADD COLUMN verified_at            timestamptz,
  ADD COLUMN verified_by            uuid REFERENCES iam.users(id),
  ADD COLUMN completion_method      text CHECK (completion_method IN ('verified','self_declared')),
  ADD COLUMN cancelled_at           timestamptz,
  ADD COLUMN last_escalated_at      timestamptz,
  ADD COLUMN status_changed_at      timestamptz NOT NULL DEFAULT now(),
  ADD COLUMN updated_by             uuid REFERENCES iam.users(id);

UPDATE tasks.tasks SET assigned_at = created_at WHERE status <> 'new';
UPDATE tasks.tasks SET accountable_user_id = created_by WHERE accountable_user_id IS NULL AND created_by IS NOT NULL AND created_by IS DISTINCT FROM assignee_user_id;
UPDATE tasks.tasks t SET executing_org_unit_id = COALESCE(t.assignee_org_unit_id, (SELECT u.org_unit_id FROM iam.users u WHERE u.id = t.assignee_user_id))
  WHERE t.assignee_type <> 'external_entity';

ALTER TABLE tasks.tasks
  ADD CONSTRAINT tasks_assignee_type_ck CHECK (assignee_type IN ('member','employee','org_unit','external_entity')),
  ADD CONSTRAINT tasks_assignee_ck CHECK (
       (assignee_type IN ('member','employee') AND assignee_user_id IS NOT NULL AND assignee_org_unit_id IS NULL AND assignee_external_entity_id IS NULL)
    OR (assignee_type = 'org_unit' AND assignee_org_unit_id IS NOT NULL AND assignee_user_id IS NULL AND assignee_external_entity_id IS NULL)
    OR (assignee_type = 'external_entity' AND assignee_external_entity_id IS NOT NULL AND assignee_user_id IS NULL AND assignee_org_unit_id IS NULL)),
  ADD CONSTRAINT tasks_priority_ck CHECK (priority IN ('low','normal','high','critical')),
  ADD CONSTRAINT tasks_progress_ck CHECK (progress_pct BETWEEN 0 AND 100),
  ADD CONSTRAINT tasks_escalation_ck CHECK (escalation_level BETWEEN 0 AND 10),
  ADD CONSTRAINT tasks_source_ck CHECK (source IN ('manual','ai_assisted')),
  -- الفصل بين المنفذ ومسؤول المتابعة (FR-TSK-02): متابع داخلي إلزامي لكل مهمة مسندة، ولا يكون هو المنفذ نفسه
  ADD CONSTRAINT tasks_follow_up_required CHECK (status = 'new' OR accountable_user_id IS NOT NULL) NOT VALID,
  ADD CONSTRAINT tasks_follow_up_separate CHECK (accountable_user_id IS DISTINCT FROM assignee_user_id AND accountable_user_id IS DISTINCT FROM executor_user_id) NOT VALID;

CREATE INDEX tasks_executing_unit_idx ON tasks.tasks (executing_org_unit_id, status);
CREATE INDEX tasks_resolution_idx ON tasks.tasks (resolution_id);

-- ============ 4. المخرجات المطلوبة ومعايير قبولها ============
CREATE TABLE tasks.task_deliverables (
  id                   uuid PRIMARY KEY,
  task_id              uuid NOT NULL REFERENCES tasks.tasks(id),
  position             int  NOT NULL,
  title                text NOT NULL,
  description          text,
  acceptance_criteria  text,
  required             boolean NOT NULL DEFAULT true,
  status               text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','submitted','accepted','rejected','removed')),
  review_note          text,
  reviewed_by          uuid REFERENCES iam.users(id),
  reviewed_at          timestamptz,
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           uuid REFERENCES iam.users(id),
  updated_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX task_deliverables_task_idx ON tasks.task_deliverables (task_id, position);
CREATE TRIGGER task_deliverables_touch BEFORE UPDATE ON tasks.task_deliverables FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();
REVOKE DELETE, TRUNCATE ON tasks.task_deliverables FROM ccms_app;

-- تقديمات المخرج: إلحاقية؛ رفع ملف لا يغير حالة المهمة ولا يقبل المخرج
CREATE TABLE tasks.deliverable_submissions (
  id               uuid PRIMARY KEY,
  deliverable_id   uuid NOT NULL REFERENCES tasks.task_deliverables(id),
  task_id          uuid NOT NULL REFERENCES tasks.tasks(id),
  document_ids     uuid[] NOT NULL DEFAULT '{}',
  note             text,
  channel          text NOT NULL DEFAULT 'system' CHECK (channel IN ('system','portal','recorded')),
  submitted_by     uuid NOT NULL REFERENCES iam.users(id),
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX deliverable_submissions_idx ON tasks.deliverable_submissions (deliverable_id, created_at);
REVOKE UPDATE, DELETE, TRUNCATE ON tasks.deliverable_submissions FROM ccms_app;
GRANT SELECT, INSERT ON tasks.deliverable_submissions TO ccms_app;

-- قرارات التحقق: إلحاقية
CREATE TABLE tasks.task_reviews (
  id                     uuid PRIMARY KEY,
  task_id                uuid NOT NULL REFERENCES tasks.tasks(id),
  decision               text NOT NULL CHECK (decision IN ('accepted','returned')),
  note                   text,
  deliverable_decisions  jsonb NOT NULL DEFAULT '[]',
  reviewer_user_id       uuid NOT NULL REFERENCES iam.users(id),
  acting_capacity        text,
  created_at             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX task_reviews_task_idx ON tasks.task_reviews (task_id, created_at);
REVOKE UPDATE, DELETE, TRUNCATE ON tasks.task_reviews FROM ccms_app;
GRANT SELECT, INSERT ON tasks.task_reviews TO ccms_app;

-- ============ 5. طلبات التمديد وأسباب التأخير ============
CREATE TABLE tasks.extension_requests (
  id                  uuid PRIMARY KEY,
  task_id             uuid NOT NULL REFERENCES tasks.tasks(id),
  requested_by        uuid NOT NULL REFERENCES iam.users(id),
  channel             text NOT NULL DEFAULT 'system' CHECK (channel IN ('system','portal','recorded')),
  reference           text,
  current_due_date    date NOT NULL,
  requested_due_date  date NOT NULL,
  delay_cause         text NOT NULL CHECK (delay_cause IN ('dependency','resources','scope_change','external_party','data_availability','other')),
  reason              text NOT NULL,
  status              text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','rejected','withdrawn')),
  approved_due_date   date,
  decided_by          uuid REFERENCES iam.users(id),
  decided_at          timestamptz,
  decision_note       text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  CHECK (requested_due_date > current_date - 365),
  CHECK (status <> 'approved' OR approved_due_date IS NOT NULL)
);
CREATE UNIQUE INDEX extension_one_pending ON tasks.extension_requests (task_id) WHERE status = 'pending';
REVOKE DELETE, TRUNCATE ON tasks.extension_requests FROM ccms_app;

-- ============ 6. التنبيهات والتصعيد ============
CREATE TABLE tasks.escalations (
  id               uuid PRIMARY KEY,
  task_id          uuid NOT NULL REFERENCES tasks.tasks(id),
  kind             text NOT NULL CHECK (kind IN ('reminder','escalation','manual','review_overdue')),
  level            int  NOT NULL CHECK (level BETWEEN 0 AND 10),
  trigger          text NOT NULL,
  period_key       text NOT NULL,
  due_date         date,
  days_overdue     int,
  reason           text,
  recipient_roles  text[] NOT NULL DEFAULT '{}',
  recipients       jsonb NOT NULL DEFAULT '[]',
  created_by       uuid REFERENCES iam.users(id),
  created_at       timestamptz NOT NULL DEFAULT now(),
  acknowledged_by  uuid REFERENCES iam.users(id),
  acknowledged_at  timestamptz,
  ack_note         text,
  UNIQUE (task_id, kind, level, period_key)
);
CREATE INDEX escalations_task_idx ON tasks.escalations (task_id, created_at);
-- سجل إلحاقي: يُسمح فقط بتسجيل إقرار المتلقي
REVOKE UPDATE, DELETE, TRUNCATE ON tasks.escalations FROM ccms_app;
GRANT SELECT, INSERT ON tasks.escalations TO ccms_app;
GRANT UPDATE (acknowledged_by, acknowledged_at, ack_note) ON tasks.escalations TO ccms_app;

-- ============ 7. سجل التحديثات والتعليقات ============
ALTER TABLE tasks.task_updates DROP CONSTRAINT IF EXISTS task_updates_update_type_check;
-- التنبيهات والتصعيد الآلي يسجلها النظام بلا مستخدم
ALTER TABLE tasks.task_updates ALTER COLUMN author_user_id DROP NOT NULL;
ALTER TABLE tasks.task_updates
  ADD CONSTRAINT task_updates_type_ck CHECK (update_type IN ('progress','status_change','comment','evidence','reassignment','due_date_change',
    'deliverable','review','extension_request','extension_decision','escalation','external_update','letter','edit')),
  ADD COLUMN channel                text NOT NULL DEFAULT 'system' CHECK (channel IN ('system','portal','recorded')),
  ADD COLUMN official_channel       text CHECK (official_channel IN ('letter','email','phone','meeting','correspondence_system','other')),
  ADD COLUMN reference              text,
  ADD COLUMN received_on            date,
  ADD COLUMN recorded_for_entity_id uuid REFERENCES org.external_entities(id),
  ADD COLUMN visibility             text NOT NULL DEFAULT 'internal' CHECK (visibility IN ('internal','shared')),
  ADD COLUMN delay_reason           text,
  ADD COLUMN document_ids           uuid[] NOT NULL DEFAULT '{}';

-- ============ 8. خطابات التكليف ============
CREATE TABLE tasks.assignment_letters (
  id                            uuid PRIMARY KEY,
  reference_no                  text NOT NULL UNIQUE,
  body_id                       uuid NOT NULL REFERENCES bodies.bodies(id),
  resolution_id                 uuid REFERENCES resolutions.resolutions(id),
  recipient_type                text NOT NULL CHECK (recipient_type IN ('org_unit','external_entity','user')),
  recipient_org_unit_id         uuid REFERENCES org.org_units(id),
  recipient_external_entity_id  uuid REFERENCES org.external_entities(id),
  recipient_user_id             uuid REFERENCES iam.users(id),
  recipient_name                text NOT NULL,
  recipient_attention           text,
  subject                       text NOT NULL,
  body_text                     text NOT NULL,
  due_date                      date,
  task_ids                      uuid[] NOT NULL DEFAULT '{}',
  attachment_ids                uuid[] NOT NULL DEFAULT '{}',
  share_with_entity             boolean NOT NULL DEFAULT true,
  status                        text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','approved','dispatched_manual','dispatched_integration','cancelled')),
  approved_by                   uuid REFERENCES iam.users(id),
  approved_at                   timestamptz,
  dispatch_channel              text CHECK (dispatch_channel IN ('correspondence_system_manual','hand_delivery','official_email','other','integration')),
  outgoing_no                   text,
  outgoing_date                 date,
  integration_ref               text,
  dispatched_by                 uuid REFERENCES iam.users(id),
  dispatched_at                 timestamptz,
  cancel_reason                 text,
  pdf_document_id               uuid REFERENCES docs.documents(id),
  created_by                    uuid NOT NULL REFERENCES iam.users(id),
  created_at                    timestamptz NOT NULL DEFAULT now(),
  updated_at                    timestamptz NOT NULL DEFAULT now(),
  CHECK ((recipient_type = 'org_unit' AND recipient_org_unit_id IS NOT NULL)
      OR (recipient_type = 'external_entity' AND recipient_external_entity_id IS NOT NULL)
      OR (recipient_type = 'user' AND recipient_user_id IS NOT NULL)),
  -- لا يُدّعى صدور رسمي دون رقم صادر، ولا إرسال عبر التكامل دون مرجع منه
  CHECK (status <> 'dispatched_manual' OR (outgoing_no IS NOT NULL AND outgoing_date IS NOT NULL)),
  CHECK (status <> 'dispatched_integration' OR integration_ref IS NOT NULL)
);
CREATE INDEX letters_resolution_idx ON tasks.assignment_letters (resolution_id);
CREATE TRIGGER letters_touch BEFORE UPDATE ON tasks.assignment_letters FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();
REVOKE DELETE, TRUNCATE ON tasks.assignment_letters FROM ccms_app;

-- ============ 9. المستندات المشاركة مع الجهة الخارجية ============
ALTER TABLE docs.document_links ADD COLUMN external_visible boolean NOT NULL DEFAULT false;
DO $$
DECLARE c record;
BEGIN
  FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'docs.document_links'::regclass AND contype = 'c'
           AND pg_get_constraintdef(oid) LIKE '%purpose%' LOOP
    EXECUTE format('ALTER TABLE docs.document_links DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
ALTER TABLE docs.document_links ADD CONSTRAINT document_links_purpose_ck CHECK (purpose IN
  ('formation_decision','agenda_memo','attachment','evidence','wet_signature','approval_evidence','minutes_pdf','signed_pdf','deliverable','assignment_letter'));
