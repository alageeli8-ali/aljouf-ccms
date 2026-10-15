-- V004: الجهات، والاجتماعات، والمحاضر، والقرارات، والمهام (05 §3.2–3.7).
-- هذه المرحلة تبني الهيكل والحالات؛ الانتقالات الكاملة تأتي في المراحل 2–4 (13).

CREATE TABLE bodies.bodies (
  id                     uuid PRIMARY KEY,
  code                   text NOT NULL UNIQUE CHECK (code ~ '^[A-Z][A-Z0-9-]{1,19}$'),
  body_type              text NOT NULL CHECK (body_type IN ('council','committee','sub_committee','working_team')),
  name_ar                text NOT NULL,
  name_en                text,
  parent_body_id         uuid REFERENCES bodies.bodies(id),
  mandate_ar             text,
  mandate_en             text,
  formation_decision_no  text,
  formation_decision_date date,
  term_start             date,
  term_end               date,
  classification         text NOT NULL DEFAULT 'restricted' CHECK (classification IN ('public','restricted','secret','top_secret')),
  owner_org_unit_id      uuid REFERENCES org.org_units(id),
  status                 text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','active','suspended','dissolved','archived')),
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid REFERENCES iam.users(id),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  updated_by             uuid REFERENCES iam.users(id),
  CHECK (body_type <> 'working_team' OR term_end IS NOT NULL),
  CHECK (term_end IS NULL OR term_start IS NULL OR term_end >= term_start)
);
CREATE TRIGGER bodies_touch BEFORE UPDATE ON bodies.bodies FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

CREATE TABLE bodies.body_policies (
  id                     uuid PRIMARY KEY,
  body_id                uuid NOT NULL REFERENCES bodies.bodies(id),
  version                int  NOT NULL,
  effective_from         date NOT NULL,
  quorum_rule            jsonb NOT NULL,
  voting_rule            jsonb NOT NULL,
  signoff_rule           jsonb NOT NULL,
  escalation_rule        jsonb NOT NULL,
  minutes_review_days    int  NOT NULL DEFAULT 3,
  extension_approver     text NOT NULL DEFAULT 'secretary' CHECK (extension_approver IN ('secretary','chair')),
  allow_proxy            boolean NOT NULL DEFAULT false,
  allow_remote_attendance boolean NOT NULL DEFAULT true,
  allow_circulation      boolean NOT NULL DEFAULT true,
  evidence_required      boolean NOT NULL DEFAULT false,
  created_at             timestamptz NOT NULL DEFAULT now(),
  created_by             uuid REFERENCES iam.users(id),
  UNIQUE (body_id, version)
);

CREATE TABLE bodies.body_members (
  id                    uuid PRIMARY KEY,
  body_id               uuid NOT NULL REFERENCES bodies.bodies(id),
  user_id               uuid NOT NULL REFERENCES iam.users(id),
  role                  text NOT NULL CHECK (role IN ('chair','vice_chair','secretary','member','observer')),
  is_voting             boolean NOT NULL,
  represents_entity_id  uuid REFERENCES org.external_entities(id),
  start_date            date NOT NULL DEFAULT current_date,
  end_date              date,
  appointment_ref       text,
  status                text NOT NULL DEFAULT 'active' CHECK (status IN ('active','ended')),
  created_at            timestamptz NOT NULL DEFAULT now(),
  created_by            uuid REFERENCES iam.users(id),
  CHECK (end_date IS NULL OR end_date >= start_date)
);
CREATE INDEX body_members_user_idx ON bodies.body_members (user_id, status);
CREATE UNIQUE INDEX body_members_one_active_role ON bodies.body_members (body_id, user_id) WHERE status = 'active';
CREATE UNIQUE INDEX body_members_one_chair ON bodies.body_members (body_id) WHERE status = 'active' AND role = 'chair';
CREATE UNIQUE INDEX body_members_one_vice ON bodies.body_members (body_id) WHERE status = 'active' AND role = 'vice_chair';

CREATE TABLE meetings.meetings (
  id               uuid PRIMARY KEY,
  body_id          uuid NOT NULL REFERENCES bodies.bodies(id),
  sequence_year    int,
  sequence_no      int,
  reference_no     text UNIQUE,
  title_ar         text NOT NULL,
  title_en         text,
  meeting_type     text NOT NULL DEFAULT 'regular' CHECK (meeting_type IN ('regular','extraordinary')),
  mode             text NOT NULL DEFAULT 'in_person' CHECK (mode IN ('in_person','remote','hybrid')),
  starts_at        timestamptz NOT NULL,
  planned_end_at   timestamptz,
  location         text,
  online_link      text,
  status           text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','scheduled','agenda_published','in_progress','concluded','closed','postponed','cancelled','quorum_failed')),
  policy_id        uuid REFERENCES bodies.body_policies(id),
  classification   text NOT NULL DEFAULT 'restricted',
  cancellation_reason text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid REFERENCES iam.users(id),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  CHECK (planned_end_at IS NULL OR planned_end_at > starts_at)
);
CREATE INDEX meetings_body_idx ON meetings.meetings (body_id, starts_at);
CREATE TRIGGER meetings_touch BEFORE UPDATE ON meetings.meetings FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

CREATE TABLE meetings.agenda_items (
  id                 uuid PRIMARY KEY,
  meeting_id         uuid NOT NULL REFERENCES meetings.meetings(id),
  position           int  NOT NULL,
  title_ar           text NOT NULL,
  title_en           text,
  item_type          text NOT NULL DEFAULT 'for_decision' CHECK (item_type IN ('for_decision','for_information','for_discussion')),
  presenter_text     text,
  estimated_minutes  int CHECK (estimated_minutes IS NULL OR estimated_minutes > 0),
  classification     text NOT NULL DEFAULT 'restricted',
  status             text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','discussed','deferred','withdrawn')),
  memo_text          text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  UNIQUE (meeting_id, position)
);

CREATE TABLE minutes.minutes (
  id                   uuid PRIMARY KEY,
  meeting_id           uuid NOT NULL UNIQUE REFERENCES meetings.meetings(id),
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','internal_review','member_review','pending_signoffs','pending_approval','approved','amended')),
  current_version_no   int NOT NULL DEFAULT 0,
  draft_content        jsonb NOT NULL DEFAULT '{"sections":[]}',
  approved_at          timestamptz,
  approved_by          uuid REFERENCES iam.users(id),
  classification       text NOT NULL DEFAULT 'restricted',
  created_at           timestamptz NOT NULL DEFAULT now(),
  created_by           uuid REFERENCES iam.users(id),
  updated_at           timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER minutes_touch BEFORE UPDATE ON minutes.minutes FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

CREATE TABLE minutes.minutes_versions (
  id            uuid PRIMARY KEY,
  minutes_id    uuid NOT NULL REFERENCES minutes.minutes(id),
  version_no    int  NOT NULL,
  content       jsonb NOT NULL,
  content_hash  text NOT NULL,
  source        text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','ai_assisted')),
  created_by    uuid REFERENCES iam.users(id),
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (minutes_id, version_no)
);

CREATE TABLE minutes.minutes_signoffs (
  id                   uuid PRIMARY KEY,
  minutes_version_id   uuid NOT NULL REFERENCES minutes.minutes_versions(id),
  user_id              uuid NOT NULL REFERENCES iam.users(id),
  method               text NOT NULL CHECK (method IN ('attestation','electronic_signature','wet_signature_evidence')),
  content_hash         text NOT NULL,
  reservation_text     text,
  status               text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested','valid','invalidated')),
  requested_at         timestamptz NOT NULL DEFAULT now(),
  signed_at            timestamptz,
  recorded_by          uuid REFERENCES iam.users(id)
);
CREATE UNIQUE INDEX signoffs_one_live ON minutes.minutes_signoffs (minutes_version_id, user_id) WHERE status <> 'invalidated';

CREATE TABLE resolutions.resolutions (
  id                uuid PRIMARY KEY,
  body_id           uuid NOT NULL REFERENCES bodies.bodies(id),
  minutes_id        uuid REFERENCES minutes.minutes(id),
  meeting_id        uuid REFERENCES meetings.meetings(id),
  resolution_type   text NOT NULL CHECK (resolution_type IN ('decision','recommendation')),
  sequence_year     int,
  sequence_no       int,
  number            text UNIQUE,
  title_ar          text NOT NULL,
  text_ar           text NOT NULL,
  text_en           text,
  status            text NOT NULL DEFAULT 'proposed' CHECK (status IN ('proposed','rejected','deferred','issued','referred','in_execution','executed','closed','cancelled','superseded')),
  issued_at         timestamptz,
  requires_follow_up boolean NOT NULL DEFAULT true,
  classification    text NOT NULL DEFAULT 'restricted',
  source            text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','ai_assisted')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        uuid REFERENCES iam.users(id),
  updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX resolutions_body_idx ON resolutions.resolutions (body_id, status);
CREATE TRIGGER resolutions_touch BEFORE UPDATE ON resolutions.resolutions FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

CREATE TABLE tasks.tasks (
  id                           uuid PRIMARY KEY,
  body_id                      uuid NOT NULL REFERENCES bodies.bodies(id),
  resolution_id                uuid REFERENCES resolutions.resolutions(id),
  meeting_id                   uuid REFERENCES meetings.meetings(id),
  parent_task_id               uuid REFERENCES tasks.tasks(id),
  reference_no                 text UNIQUE,
  title_ar                     text NOT NULL,
  description                  text,
  assignee_type                text NOT NULL CHECK (assignee_type IN ('user','org_unit','external_entity')),
  assignee_user_id             uuid REFERENCES iam.users(id),
  assignee_org_unit_id         uuid REFERENCES org.org_units(id),
  assignee_external_entity_id  uuid REFERENCES org.external_entities(id),
  accountable_user_id          uuid REFERENCES iam.users(id),
  verifier_user_id             uuid REFERENCES iam.users(id),
  verification_required        boolean NOT NULL DEFAULT true,
  priority                     text NOT NULL DEFAULT 'normal' CHECK (priority IN ('low','normal','high','critical')),
  original_due_date            date,
  due_date                     date,
  status                       text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','assigned','in_progress','pending_verification','completed','returned','on_hold','cancelled')),
  progress_pct                 int NOT NULL DEFAULT 0 CHECK (progress_pct BETWEEN 0 AND 100),
  escalation_level             int NOT NULL DEFAULT 0 CHECK (escalation_level BETWEEN 0 AND 4),
  correspondence_no            text,
  correspondence_date          date,
  source                       text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','ai_assisted')),
  status_reason                text,
  completed_at                 timestamptz,
  created_at                   timestamptz NOT NULL DEFAULT now(),
  created_by                   uuid REFERENCES iam.users(id),
  updated_at                   timestamptz NOT NULL DEFAULT now(),
  CHECK ((assignee_type = 'user'            AND assignee_user_id IS NOT NULL)
      OR (assignee_type = 'org_unit'        AND assignee_org_unit_id IS NOT NULL)
      OR (assignee_type = 'external_entity' AND assignee_external_entity_id IS NOT NULL)),
  CHECK (assignee_type = 'user' OR accountable_user_id IS NOT NULL)
);
CREATE INDEX tasks_status_due_idx ON tasks.tasks (status, due_date);
CREATE INDEX tasks_assignee_idx ON tasks.tasks (assignee_user_id, status);
CREATE INDEX tasks_accountable_idx ON tasks.tasks (accountable_user_id, status);
CREATE INDEX tasks_entity_idx ON tasks.tasks (assignee_external_entity_id, status);
CREATE INDEX tasks_body_idx ON tasks.tasks (body_id, status);
CREATE TRIGGER tasks_touch BEFORE UPDATE ON tasks.tasks FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();

CREATE TABLE tasks.task_updates (
  id                    uuid PRIMARY KEY,
  task_id               uuid NOT NULL REFERENCES tasks.tasks(id),
  author_user_id        uuid NOT NULL REFERENCES iam.users(id),
  update_type           text NOT NULL CHECK (update_type IN ('progress','status_change','comment','evidence','reassignment','due_date_change')),
  old_value             jsonb,
  new_value             jsonb,
  progress_pct          int CHECK (progress_pct IS NULL OR progress_pct BETWEEN 0 AND 100),
  note                  text,
  created_at            timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX task_updates_task_idx ON tasks.task_updates (task_id, created_at);
REVOKE UPDATE, DELETE, TRUNCATE ON tasks.task_updates FROM ccms_app;
GRANT SELECT, INSERT ON tasks.task_updates TO ccms_app;
