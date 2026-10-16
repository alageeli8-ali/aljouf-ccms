-- V008: المرحلة 2 — الجهات بأنواعها وسجلها، والعضويات والبدلاء والتفويض، والاجتماعات والدعوات والحضور والنصاب وجدول الأعمال.
-- لا حذف لأي بيانات رسمية: التغيير ينهي السجل القديم وينشئ سجلًا جديدًا، والأحداث تُحفظ في سجل التدقيق.

-- ============================ الجهات ============================

ALTER TABLE bodies.bodies DROP CONSTRAINT bodies_body_type_check;
UPDATE bodies.bodies SET body_type = CASE body_type
  WHEN 'council' THEN 'executive_council'
  WHEN 'committee' THEN 'permanent_committee'
  ELSE body_type END;
ALTER TABLE bodies.bodies ADD CONSTRAINT bodies_body_type_check CHECK (body_type IN (
  'supreme_council', 'steering_council', 'executive_council',
  'permanent_committee', 'temporary_committee', 'working_team', 'sub_committee', 'joint_committee'));

ALTER TABLE bodies.bodies
  ADD COLUMN reference_no      text,
  ADD COLUMN objective_ar      text,
  ADD COLUMN objective_en      text,
  ADD COLUMN authority_ref     text,           -- المرجعية: الجهة أو النظام الذي تتبع له
  ADD COLUMN meeting_frequency text NOT NULL DEFAULT 'monthly'
    CHECK (meeting_frequency IN ('weekly','biweekly','monthly','quarterly','semiannual','annual','as_needed')),
  ADD COLUMN status_reason     text;

-- رقم مرجعي فريد لكل جهة: BOD-{السنة}-{تسلسل}
WITH numbered AS (
  SELECT id, extract(year FROM created_at)::int AS y,
         row_number() OVER (PARTITION BY extract(year FROM created_at) ORDER BY created_at, id) AS n
  FROM bodies.bodies)
UPDATE bodies.bodies b SET reference_no = 'BOD-' || numbered.y || '-' || lpad(numbered.n::text, 4, '0')
FROM numbered WHERE numbered.id = b.id;
INSERT INTO config.numbering_sequences (scope_key, year, last_value)
SELECT 'body', extract(year FROM created_at)::int, count(*) FROM bodies.bodies GROUP BY 2
ON CONFLICT (scope_key, year) DO UPDATE SET last_value = GREATEST(config.numbering_sequences.last_value, EXCLUDED.last_value);
ALTER TABLE bodies.bodies ALTER COLUMN reference_no SET NOT NULL;
ALTER TABLE bodies.bodies ADD CONSTRAINT bodies_reference_no_key UNIQUE (reference_no);
ALTER TABLE bodies.bodies ADD CONSTRAINT bodies_temporary_term_check
  CHECK (body_type NOT IN ('temporary_committee','working_team') OR term_end IS NOT NULL);
ALTER TABLE bodies.bodies ADD CONSTRAINT bodies_sub_parent_check
  CHECK (body_type <> 'sub_committee' OR parent_body_id IS NOT NULL);
ALTER TABLE bodies.bodies ADD CONSTRAINT bodies_not_own_parent CHECK (parent_body_id IS NULL OR parent_body_id <> id);

-- الجهات الخارجية الشريكة في اللجان المشتركة
CREATE TABLE bodies.body_partner_entities (
  body_id             uuid NOT NULL REFERENCES bodies.bodies(id),
  external_entity_id  uuid NOT NULL REFERENCES org.external_entities(id),
  added_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (body_id, external_entity_id)
);

-- سجل مدة العمل: التجديد والتمديد بسند، دون الكتابة فوق القيم السابقة
CREATE TABLE bodies.body_terms (
  id             uuid PRIMARY KEY,
  body_id        uuid NOT NULL REFERENCES bodies.bodies(id),
  change_type    text NOT NULL CHECK (change_type IN ('initial','renewal','extension')),
  term_start     date,
  term_end       date,
  decision_no    text,
  decision_date  date,
  reason         text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     uuid REFERENCES iam.users(id)
);
INSERT INTO bodies.body_terms (id, body_id, change_type, term_start, term_end, decision_no, decision_date, created_at, created_by)
SELECT gen_random_uuid(), id, 'initial', term_start, term_end, formation_decision_no, formation_decision_date, created_at, created_by FROM bodies.bodies;
REVOKE UPDATE, DELETE, TRUNCATE ON bodies.body_terms FROM ccms_app;
GRANT SELECT, INSERT ON bodies.body_terms TO ccms_app;

-- ============================ العضويات ============================

ALTER TABLE bodies.body_members DROP CONSTRAINT body_members_role_check;
ALTER TABLE bodies.body_members ADD CONSTRAINT body_members_role_check
  CHECK (role IN ('chair','vice_chair','secretary','member','alternate','observer'));
ALTER TABLE bodies.body_members DROP CONSTRAINT body_members_status_check;
ALTER TABLE bodies.body_members ADD CONSTRAINT body_members_status_check CHECK (status IN ('active','suspended','ended'));
ALTER TABLE bodies.body_members
  ADD COLUMN alternate_for_member_id uuid REFERENCES bodies.body_members(id),
  ADD COLUMN end_reason              text,
  ADD COLUMN replaced_by_member_id   uuid REFERENCES bodies.body_members(id),
  ADD COLUMN ended_by                uuid REFERENCES iam.users(id);
ALTER TABLE bodies.body_members ADD CONSTRAINT body_members_alternate_check
  CHECK ((role = 'alternate') = (alternate_for_member_id IS NOT NULL));
-- عضوية واحدة غير منتهية لكل مستخدم في الجهة (النشطة أو الموقوفة)
DROP INDEX bodies.body_members_one_active_role;
CREATE UNIQUE INDEX body_members_one_open ON bodies.body_members (body_id, user_id) WHERE status <> 'ended';

-- وسائل التواصل المؤسسية فقط (لا بيانات شخصية غير لازمة)
ALTER TABLE iam.users ADD COLUMN work_phone text;

-- التفويض والإنابة (06 §6)
CREATE TABLE iam.delegations (
  id                 uuid PRIMARY KEY,
  body_id            uuid NOT NULL REFERENCES bodies.bodies(id),
  delegator_user_id  uuid NOT NULL REFERENCES iam.users(id),
  delegate_user_id   uuid NOT NULL REFERENCES iam.users(id),
  scope              text NOT NULL CHECK (scope IN ('chair_authority','attendance_proxy')),
  valid_from         timestamptz NOT NULL,
  valid_to           timestamptz NOT NULL,
  reason             text NOT NULL,
  status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active','revoked')),
  created_by         uuid REFERENCES iam.users(id),
  created_at         timestamptz NOT NULL DEFAULT now(),
  revoked_by         uuid REFERENCES iam.users(id),
  revoked_at         timestamptz,
  CHECK (delegator_user_id <> delegate_user_id),
  CHECK (valid_to > valid_from)
);
CREATE INDEX delegations_delegate_idx ON iam.delegations (delegate_user_id, status);
CREATE INDEX delegations_body_idx ON iam.delegations (body_id, status);

-- ============================ الاجتماعات ============================

ALTER TABLE meetings.meetings DROP CONSTRAINT meetings_meeting_type_check;
ALTER TABLE meetings.meetings ADD CONSTRAINT meetings_meeting_type_check
  CHECK (meeting_type IN ('regular','extraordinary','urgent','circulation'));
ALTER TABLE meetings.meetings
  ADD COLUMN previous_meeting_id         uuid REFERENCES meetings.meetings(id),
  ADD COLUMN rescheduled_from_meeting_id uuid REFERENCES meetings.meetings(id),
  ADD COLUMN status_reason               text,
  ADD COLUMN actual_started_at           timestamptz,
  ADD COLUMN actual_ended_at             timestamptz,
  ADD COLUMN circulation_deadline        timestamptz,
  ADD COLUMN invitations_sent_at         timestamptz,
  ADD COLUMN chair_user_id               uuid REFERENCES iam.users(id),
  ADD COLUMN secretary_user_id           uuid REFERENCES iam.users(id);
UPDATE meetings.meetings SET status_reason = cancellation_reason WHERE cancellation_reason IS NOT NULL;
ALTER TABLE meetings.meetings ADD CONSTRAINT meetings_circulation_check
  CHECK ((meeting_type = 'circulation') = (circulation_deadline IS NOT NULL));
CREATE INDEX meetings_starts_idx ON meetings.meetings (starts_at);

CREATE TABLE meetings.invitations (
  id              uuid PRIMARY KEY,
  meeting_id      uuid NOT NULL REFERENCES meetings.meetings(id),
  user_id         uuid NOT NULL REFERENCES iam.users(id),
  invitee_kind    text NOT NULL CHECK (invitee_kind IN ('member','guest')),
  body_member_id  uuid REFERENCES bodies.body_members(id),
  agenda_item_id  uuid,                         -- الضيف المدعو لبند بعينه
  sent_at         timestamptz,
  response        text NOT NULL DEFAULT 'pending' CHECK (response IN ('pending','accepted','declined','tentative')),
  response_note   text,
  responded_at    timestamptz,
  last_reminder_at timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (meeting_id, user_id)
);

CREATE TABLE meetings.attendance_records (
  id                     uuid PRIMARY KEY,
  meeting_id             uuid NOT NULL REFERENCES meetings.meetings(id),
  user_id                uuid NOT NULL REFERENCES iam.users(id),
  body_member_id         uuid REFERENCES bodies.body_members(id),
  status                 text NOT NULL CHECK (status IN ('present','absent','excused','proxy')),
  attendance_mode        text CHECK (attendance_mode IN ('in_person','remote')),
  proxy_holder_user_id   uuid REFERENCES iam.users(id),
  note                   text,
  arrived_at             timestamptz,
  left_at                timestamptz,
  recorded_by            uuid REFERENCES iam.users(id),
  recorded_at            timestamptz NOT NULL DEFAULT now(),
  UNIQUE (meeting_id, user_id),
  CHECK ((status = 'proxy') = (proxy_holder_user_id IS NOT NULL)),
  CHECK (status <> 'present' OR attendance_mode IS NOT NULL)
);

CREATE TABLE meetings.quorum_checks (
  id               uuid PRIMARY KEY,
  meeting_id       uuid NOT NULL REFERENCES meetings.meetings(id),
  context          text NOT NULL CHECK (context IN ('forecast','opening','departure','manual','circulation')),
  eligible_count   int NOT NULL,
  counted_present  int NOT NULL,
  required_count   int NOT NULL,
  chair_present    boolean NOT NULL,
  is_met           boolean NOT NULL,
  override_reason  text,
  rule_snapshot    jsonb NOT NULL,
  breakdown        jsonb NOT NULL,
  checked_by       uuid REFERENCES iam.users(id),
  checked_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX quorum_checks_meeting_idx ON meetings.quorum_checks (meeting_id, checked_at);
REVOKE UPDATE, DELETE, TRUNCATE ON meetings.quorum_checks FROM ccms_app;
GRANT SELECT, INSERT ON meetings.quorum_checks TO ccms_app;

-- ============================ جدول الأعمال ============================

ALTER TABLE meetings.agenda_items DROP CONSTRAINT agenda_items_status_check;
ALTER TABLE meetings.agenda_items ADD CONSTRAINT agenda_items_status_check
  CHECK (status IN ('pending','discussed','deferred','referred','withdrawn'));
ALTER TABLE meetings.agenda_items
  ADD COLUMN description_ar        text,
  ADD COLUMN owner_org_unit_id     uuid REFERENCES org.org_units(id),
  ADD COLUMN owner_text            text,
  ADD COLUMN discussion_summary    text,
  ADD COLUMN recommendation_text   text,
  ADD COLUMN status_reason         text,
  ADD COLUMN carried_from_item_id  uuid REFERENCES meetings.agenda_items(id),
  ADD COLUMN updated_at            timestamptz NOT NULL DEFAULT now();
CREATE TRIGGER agenda_items_touch BEFORE UPDATE ON meetings.agenda_items FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();
-- إعادة الترتيب تُحدّث المواقع داخل المعاملة؛ القيد يُفحص عند نهايتها
ALTER TABLE meetings.agenda_items DROP CONSTRAINT agenda_items_meeting_id_position_key;
ALTER TABLE meetings.agenda_items ADD CONSTRAINT agenda_items_meeting_position_key UNIQUE (meeting_id, position) DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE meetings.invitations ADD CONSTRAINT invitations_agenda_item_fk FOREIGN KEY (agenda_item_id) REFERENCES meetings.agenda_items(id);

-- البنود المؤجلة أو المحالة بانتظار إدراجها في اجتماع لاحق (للجهة نفسها أو لجهة أخرى)
CREATE TABLE meetings.pending_items (
  id                  uuid PRIMARY KEY,
  body_id             uuid NOT NULL REFERENCES bodies.bodies(id),
  source_item_id      uuid NOT NULL REFERENCES meetings.agenda_items(id),
  source_body_id      uuid NOT NULL REFERENCES bodies.bodies(id),
  kind                text NOT NULL CHECK (kind IN ('deferred','referred')),
  title_ar            text NOT NULL,
  description_ar      text,
  reason              text NOT NULL,
  status              text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','scheduled','dropped')),
  scheduled_item_id   uuid REFERENCES meetings.agenda_items(id),
  created_by          uuid REFERENCES iam.users(id),
  created_at          timestamptz NOT NULL DEFAULT now(),
  scheduled_at        timestamptz
);
CREATE INDEX pending_items_body_idx ON meetings.pending_items (body_id, status);

-- ربط القرارات والمهام ببند جدول الأعمال
ALTER TABLE resolutions.resolutions ADD COLUMN agenda_item_id uuid REFERENCES meetings.agenda_items(id);
ALTER TABLE tasks.tasks ADD COLUMN agenda_item_id uuid REFERENCES meetings.agenda_items(id);

-- مرفقات البنود
ALTER TABLE docs.document_links DROP CONSTRAINT document_links_owner_type_check;
ALTER TABLE docs.document_links ADD CONSTRAINT document_links_owner_type_check
  CHECK (owner_type IN ('body','meeting','agenda_item','minutes','resolution','task'));

-- ============================ الصلاحيات ============================

INSERT INTO iam.roles (code, scope, name_ar, name_en, sort_order) VALUES
  ('alternate', 'body', 'عضو بديل', 'Alternate member', 15);

INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('body.lifecycle.manage', 'body', 'تجديد الجهة وتمديدها وإنهاؤها وأرشفتها'),
  ('body.policy.manage',    'body', 'تعديل سياسة النصاب والتصويت والاجتماعات'),
  ('body.delegate',         'body', 'تفويض صلاحيات الرئاسة أو الإنابة في الحضور'),
  ('meeting.attendance',    'body', 'تسجيل الحضور والتحقق من النصاب وافتتاح الاجتماع وإغلاقه'),
  ('meeting.invite',        'body', 'إرسال الدعوات والتذكيرات ودعوة الضيوف');

INSERT INTO iam.role_permissions (role_code, permission_code) VALUES
  ('governance_officer', 'body.lifecycle.manage'), ('governance_officer', 'body.policy.manage'), ('governance_officer', 'body.delegate'),
  ('chair', 'body.delegate'), ('chair', 'meeting.attendance'), ('chair', 'meeting.invite'),
  ('vice_chair', 'meeting.attendance'),
  ('secretary', 'meeting.attendance'), ('secretary', 'meeting.invite'),
  ('member', 'body.delegate'),
  ('alternate', 'body.view'), ('alternate', 'meeting.view'), ('alternate', 'minutes.view'), ('alternate', 'resolution.view'), ('alternate', 'document.view')
ON CONFLICT DO NOTHING;
