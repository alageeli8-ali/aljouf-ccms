-- بيانات حجم واقعي لاختبار الأداء (5 سنوات تشغيل تقريبًا). تُولَّد في قاعدة منفصلة ccms_perf فقط.
\set ON_ERROR_STOP on
SET session_replication_role = replica; -- تعطيل المشغلات أثناء التوليد الكتلي (عدا سلسلة التدقيق تُبنى بعدها)
CREATE TEMP TABLE pu AS SELECT gen_random_uuid() id, g FROM generate_series(1,1500) g;
INSERT INTO iam.users (id, email, display_name_ar, user_type, status) SELECT id, 'perf'||g||'@perf.jsdo.test', 'موظف أداء '||g, 'internal', 'active' FROM pu;
CREATE TEMP TABLE pb AS SELECT gen_random_uuid() id, g, (ARRAY['executive_council','permanent_committee','steering_council','joint_committee'])[1 + g % 4] t FROM generate_series(1,60) g;
INSERT INTO bodies.bodies (id, code, body_type, name_ar, name_en, reference_no, status) SELECT id, 'PF-'||g, t, 'جهة أداء '||g, 'Perf body '||g, 'PF-REF-'||g, 'active' FROM pb;
INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, status)
  SELECT gen_random_uuid(), pb.id, pu.id, CASE WHEN pu.g % 10 = 0 THEN 'chair' WHEN pu.g % 10 = 1 THEN 'secretary' ELSE 'member' END, true, 'active'
  FROM pb JOIN pu ON pu.g BETWEEN pb.g * 10 AND pb.g * 10 + 9;
CREATE TEMP TABLE pm AS SELECT gen_random_uuid() id, pb.id body_id, pb.g bg, s,
  now() - make_interval(days => (s * 3) % 1800) - make_interval(hours => s % 9) starts FROM pb, generate_series(1,100) s;
INSERT INTO meetings.meetings (id, body_id, title_ar, starts_at, status, reference_no, meeting_type, mode, actual_started_at, actual_ended_at)
  SELECT id, body_id, 'اجتماع أداء '||bg||'-'||s||' حول خطة التحول الرقمي والمشاريع', starts, CASE WHEN starts < now() THEN 'closed' ELSE 'scheduled' END, 'PF-'||bg||'-'||s,
         'regular', 'in_person', CASE WHEN starts < now() THEN starts END, CASE WHEN starts < now() THEN starts + interval '2 hours' END FROM pm;
INSERT INTO meetings.attendance_records (id, meeting_id, user_id, body_member_id, status, attendance_mode)
  SELECT gen_random_uuid(), pm.id, bm.user_id, bm.id, CASE WHEN (pm.s + abs(hashtext(bm.id::text))) % 7 = 0 THEN 'absent' ELSE 'present' END,
         CASE WHEN (pm.s + abs(hashtext(bm.id::text))) % 7 = 0 THEN NULL ELSE 'in_person' END
  FROM pm JOIN bodies.body_members bm ON bm.body_id = pm.body_id WHERE pm.starts < now();
INSERT INTO minutes.minutes (id, meeting_id, template_id, workflow, status, approved_at, current_version_no)
  SELECT gen_random_uuid(), pm.id, (SELECT template_id FROM minutes.minutes LIMIT 1), (SELECT workflow FROM minutes.minutes LIMIT 1),
         CASE WHEN pm.s % 11 = 0 THEN 'chair_review' ELSE 'approved' END, CASE WHEN pm.s % 11 = 0 THEN NULL ELSE pm.starts + interval '3 days' END, 1
  FROM pm WHERE pm.starts < now();
CREATE TEMP TABLE pr AS SELECT gen_random_uuid() id, pm.body_id, pm.id meeting_id, pm.bg, pm.s, r, pm.starts FROM pm, generate_series(1,4) r WHERE pm.starts < now();
INSERT INTO resolutions.resolutions (id, body_id, resolution_type, title_ar, text_ar, status, number, issued_at)
  SELECT id, body_id, 'decision', 'قرار أداء '||bg||'-'||s||'-'||r||' باعتماد المشروع', 'قرر المجلس اعتماد المشروع رقم '||s||' ضمن خطة التحول وتكليف الإدارات بالتنفيذ.',
         (ARRAY['issued','in_execution','executed','closed'])[1 + (s + r) % 4], 'PF-'||bg||'/'||s||'/'||r, starts + interval '3 days' FROM pr;
INSERT INTO tasks.tasks (id, body_id, resolution_id, title_ar, assignee_type, assignee_user_id, accountable_user_id, status, due_date, assigned_at, completed_at, created_at, reference_no)
  SELECT gen_random_uuid(), pr.body_id, pr.id, 'مهمة تنفيذ '||pr.bg||'-'||pr.s||'-'||pr.r||'-'||k, 'employee', (SELECT id FROM pu WHERE g = 1 + (pr.s * 7 + k) % 1500),
         (SELECT id FROM pu WHERE g = 1 + (pr.s * 13 + k) % 1500),
         CASE WHEN (pr.s + k) % 5 = 0 THEN 'in_progress' WHEN (pr.s + k) % 7 = 0 THEN 'assigned' ELSE 'completed' END,
         (pr.starts + interval '30 days')::date, pr.starts + interval '4 days',
         CASE WHEN (pr.s + k) % 5 = 0 OR (pr.s + k) % 7 = 0 THEN NULL ELSE pr.starts + make_interval(days => 10 + (pr.s + k) % 40) END, pr.starts + interval '4 days',
         'PF-T-'||pr.bg||'-'||pr.s||'-'||pr.r||'-'||k
  FROM pr, generate_series(1,3) k;
INSERT INTO docs.documents (id, series_id, file_name, content_type, size_bytes, sha256, storage_key, uploaded_by, records_class, lifecycle_status)
  SELECT d, d, 'مرفق-أداء-'||g||'.pdf', 'application/pdf', 120000, md5(g::text)||md5(g::text), 'perf/'||g, (SELECT id FROM pu WHERE g = 1 + x.g % 1500), 'working_papers', 'active'
  FROM (SELECT gen_random_uuid() d, g FROM generate_series(1, 20000) g) x;
CREATE TEMP TABLE pmn AS SELECT id, row_number() OVER (ORDER BY id) - 1 n FROM pm;
INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose)
  SELECT gen_random_uuid(), d.id, 'meeting', pmn.id, 'attachment'
  FROM (SELECT id, row_number() OVER (ORDER BY id) n FROM docs.documents WHERE storage_key LIKE 'perf/%') d JOIN pmn ON pmn.n = d.n % 6000;
SET session_replication_role = origin;
-- سجل تدقيق كبير بالمشغل الحقيقي (سلسلة البصمات) — 200 ألف حدث
INSERT INTO audit.audit_events (actor_user_id, module, action, entity_type, entity_id, outcome, after)
  SELECT (SELECT id FROM pu WHERE g = 1 + s % 1500), 'TSK', 'task.updated', 'task', gen_random_uuid()::text, 'success', jsonb_build_object('progressPct', s % 100)
  FROM generate_series(1, 200000) s;
ANALYZE;
SELECT 'users', count(*) FROM iam.users UNION ALL SELECT 'bodies', count(*) FROM bodies.bodies UNION ALL SELECT 'meetings', count(*) FROM meetings.meetings
UNION ALL SELECT 'attendance', count(*) FROM meetings.attendance_records UNION ALL SELECT 'minutes', count(*) FROM minutes.minutes
UNION ALL SELECT 'resolutions', count(*) FROM resolutions.resolutions UNION ALL SELECT 'tasks', count(*) FROM tasks.tasks
UNION ALL SELECT 'documents', count(*) FROM docs.documents UNION ALL SELECT 'audit', count(*) FROM audit.audit_events;
