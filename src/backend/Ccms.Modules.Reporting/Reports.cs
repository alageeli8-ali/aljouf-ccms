using System.Globalization;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Office;
using Ccms.BuildingBlocks.Pdf;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Reporting;

/// <summary>عمود تقرير. النوع يحدد التنسيق في PDF وExcel: text, int, num, pct, date, datetime, status:&lt;kind&gt;.</summary>
public sealed record ReportColumn(string Key, string Ar, string En, double Width, string Kind = "text");

internal sealed record ReportContext(Db Db, Guid[] Bodies, ReportFilter Filter, CancellationToken Ct);

internal sealed record ReportDef(string Code, string TitleAr, string TitleEn, string DescAr, string DescEn, string Permission, string? ExportPermission,
    bool BodyScoped, ReportColumn[] Columns, Func<ReportContext, Task<List<object?[]>>> Load);

/// <summary>محرك التقارير (23 §2): تعريف واحد لكل تقرير يُخدم JSON للعرض، وPDF أفقي، وExcel. الحد الأقصى 5000 صف لكل تصدير.</summary>
internal static class Reports
{
    public const int MaxRows = 5000;
    private const string Riyadh = "Asia/Riyadh";

    private static readonly Dictionary<string, (string Ar, string En)> Labels = new()
    {
        ["meeting.draft"] = ("مسودة", "Draft"), ["meeting.scheduled"] = ("مجدول", "Scheduled"), ["meeting.agenda_published"] = ("نُشر جدول الأعمال", "Agenda published"),
        ["meeting.in_progress"] = ("منعقد الآن", "In progress"), ["meeting.concluded"] = ("منتهٍ", "Concluded"), ["meeting.closed"] = ("مغلق", "Closed"),
        ["meeting.postponed"] = ("مؤجل", "Postponed"), ["meeting.cancelled"] = ("ملغى", "Cancelled"), ["meeting.quorum_failed"] = ("لم يكتمل النصاب", "Quorum failed"),
        ["minutes.draft"] = ("مسودة", "Draft"), ["minutes.secretary_review"] = ("مراجعة أمين السر", "Secretary review"), ["minutes.chair_review"] = ("مراجعة الرئيس", "Chair review"),
        ["minutes.member_review"] = ("ملاحظات الأعضاء", "Member review"), ["minutes.pending_signatures"] = ("بانتظار التوقيع", "Pending signatures"),
        ["minutes.pending_final_approval"] = ("بانتظار الاعتماد", "Pending approval"), ["minutes.approved"] = ("معتمد", "Approved"), ["minutes.returned"] = ("معاد للتعديل", "Returned"),
        ["minutes.none"] = ("لا يوجد محضر", "No minutes"),
        ["resolution.proposed"] = ("مقترح", "Proposed"), ["resolution.withdrawn"] = ("مسحوب", "Withdrawn"), ["resolution.rejected"] = ("مرفوض", "Rejected"),
        ["resolution.deferred"] = ("مؤجل", "Deferred"), ["resolution.issued"] = ("صادر", "Issued"), ["resolution.referred"] = ("محال", "Referred"),
        ["resolution.in_execution"] = ("قيد التنفيذ", "In execution"), ["resolution.executed"] = ("منفذ", "Executed"), ["resolution.closed"] = ("مغلق", "Closed"),
        ["resolution.cancelled"] = ("ملغى", "Cancelled"), ["resolution.superseded"] = ("ملغى بقرار لاحق", "Superseded"),
        ["task.new"] = ("جديدة", "New"), ["task.assigned"] = ("مسندة", "Assigned"), ["task.accepted"] = ("مقبولة", "Accepted"), ["task.in_progress"] = ("قيد التنفيذ", "In progress"),
        ["task.on_hold"] = ("متوقفة", "On hold"), ["task.pending_review"] = ("بانتظار المراجعة", "Pending review"), ["task.completed"] = ("مكتملة", "Completed"),
        ["task.rejected"] = ("مرفوضة", "Rejected"), ["task.cancelled"] = ("ملغاة", "Cancelled"),
        ["sig.queued"] = ("في الطابور", "Queued"), ["sig.in_progress"] = ("قيد التوقيع", "In progress"), ["sig.on_hold"] = ("معلق", "On hold"), ["sig.completed"] = ("مكتمل", "Completed"),
        ["sig.declined"] = ("مرفوض", "Declined"), ["sig.voided"] = ("ملغى بعد الإرسال", "Voided"), ["sig.cancelled"] = ("ملغى", "Cancelled"), ["sig.expired"] = ("منتهي الصلاحية", "Expired"),
        ["sig.failed"] = ("فشل الإرسال", "Failed"),
        ["sigtype.in_app_approval"] = ("موافقة داخل النظام", "In-app approval"), ["sigtype.provider_esign"] = ("مزود توقيع", "Provider e-sign"),
        ["sigtype.pdf_digital_signature"] = ("توقيع رقمي PDF", "PDF digital signature"),
        ["body.active"] = ("فعالة", "Active"), ["body.draft"] = ("مسودة", "Draft"), ["body.suspended"] = ("معلقة", "Suspended"), ["body.dissolved"] = ("منحلة", "Dissolved"),
        ["bodytype.supreme_council"] = ("مجلس أعلى", "Supreme council"), ["bodytype.steering_council"] = ("مجلس توجيهي", "Steering council"),
        ["bodytype.executive_council"] = ("مجلس تنفيذي", "Executive council"), ["bodytype.permanent_committee"] = ("لجنة دائمة", "Permanent committee"),
        ["bodytype.temporary_committee"] = ("لجنة مؤقتة", "Temporary committee"), ["bodytype.working_team"] = ("فريق عمل", "Working team"),
        ["bodytype.sub_committee"] = ("لجنة فرعية", "Sub-committee"), ["bodytype.joint_committee"] = ("لجنة مشتركة", "Joint committee"),
        ["assignee.org_unit"] = ("إدارة", "Org unit"), ["assignee.external_entity"] = ("جهة خارجية", "External entity"),
        ["outcome.success"] = ("نجاح", "Success"), ["outcome.denied"] = ("مرفوض", "Denied"), ["outcome.failure"] = ("فشل", "Failure"),
        ["mtype.regular"] = ("عادي", "Regular"), ["mtype.extraordinary"] = ("استثنائي", "Extraordinary"), ["mtype.urgent"] = ("عاجل", "Urgent"), ["mtype.circulation"] = ("بالتمرير", "Circulation"),
    };

    private static ReportColumn C(string key, string ar, string en, double w, string kind = "text") => new(key, ar, en, w, kind);

    public static readonly ReportDef[] All =
    [
        new("meetings", "تقرير الاجتماعات", "Meetings report",
            "الاجتماعات التي يقع موعدها في الفترة مع الحضور وحالة المحضر.", "Meetings dated within the period with attendance and minutes status.",
            Permissions.ReportsBody, null, true,
            [C("referenceNo", "المرجع", "Reference", 1.2), C("title", "العنوان", "Title", 2.4), C("body", "الجهة", "Body", 1.8), C("type", "النوع", "Type", 0.9, "status:mtype"),
             C("startsAt", "الموعد", "Date", 1.2, "datetime"), C("status", "الحالة", "Status", 1.1, "status:meeting"), C("present", "حضر", "Present", 0.6, "int"),
             C("expected", "الأعضاء", "Members", 0.7, "int"), C("attendance", "الحضور %", "Attendance %", 0.8, "pct"), C("minutes", "المحضر", "Minutes", 1.2, "status:minutes")],
            async c => await c.Db.QueryAsync($"""
                SELECT m.reference_no, m.title_ar, b.name_ar, m.meeting_type, m.starts_at, m.status,
                  (SELECT count(*) FROM meetings.attendance_records ar WHERE ar.meeting_id = m.id AND ar.body_member_id IS NOT NULL AND ar.status IN ('present','proxy')),
                  (SELECT count(*) FROM meetings.attendance_records ar WHERE ar.meeting_id = m.id AND ar.body_member_id IS NOT NULL),
                  coalesce((SELECT mi.status FROM minutes.minutes mi WHERE mi.meeting_id = m.id), 'none')
                FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
                WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")} AND (@status::text IS NULL OR m.status = @status)
                ORDER BY m.starts_at DESC LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r =>
            {
                long p = r.GetInt64(6), e = r.GetInt64(7);
                return new object?[] { r.Str("reference_no"), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDateTime(4), r.GetString(5), p, e,
                    e == 0 ? null : Math.Round(100.0 * p / e, 1), r.GetString(8) };
            }, c.Ct)),

        new("minutes", "تقرير المحاضر وحالات اعتمادها", "Minutes & approval status report",
            "محاضر اجتماعات الفترة: الحالة والإصدار وتاريخ الاعتماد والمدة والتوقيعات المعلقة.", "Minutes of meetings in the period: status, version, approval date, duration and pending sign-offs.",
            Permissions.ReportsBody, null, true,
            [C("referenceNo", "مرجع الاجتماع", "Meeting ref.", 1.2), C("title", "الاجتماع", "Meeting", 2.4), C("body", "الجهة", "Body", 1.8), C("meetingDate", "تاريخ الاجتماع", "Meeting date", 1, "date"),
             C("status", "حالة المحضر", "Minutes status", 1.3, "status:minutes"), C("version", "الإصدار", "Version", 0.6, "int"), C("approvedAt", "تاريخ الاعتماد", "Approved", 1, "date"),
             C("approvalDays", "أيام الاعتماد", "Days to approve", 0.8, "num"), C("pendingSignoffs", "توقيعات معلقة", "Pending sign-offs", 0.8, "int")],
            async c => await c.Db.QueryAsync($"""
                SELECT m.reference_no, m.title_ar, b.name_ar, m.starts_at, mi.status, mi.current_version_no, mi.approved_at,
                  CASE WHEN mi.approved_at IS NULL THEN NULL ELSE round((extract(epoch FROM (mi.approved_at - coalesce(m.actual_ended_at, m.starts_at))) / 86400.0)::numeric, 1)::float8 END,
                  (SELECT count(*) FROM minutes.minutes_signoffs s JOIN minutes.minutes_versions v ON v.id = s.minutes_version_id WHERE v.minutes_id = mi.id AND s.status = 'requested')
                  + (SELECT count(*) FROM sig.signers sg JOIN sig.requests rq ON rq.id = sg.request_id WHERE rq.minutes_id = mi.id AND rq.status IN ('queued','in_progress','on_hold') AND sg.status IN ('waiting','pending'))
                FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
                WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")} AND (@status::text IS NULL OR mi.status = @status)
                ORDER BY m.starts_at DESC LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r => new object?[]
            {
                r.Str("reference_no"), r.GetString(1), r.GetString(2), Local(r.GetDateTime(3)), r.GetString(4), r.IsDBNull(5) ? null : (long)r.GetInt32(5),
                r.IsDBNull(6) ? null : Local(r.GetDateTime(6)), r.IsDBNull(7) ? null : r.GetDouble(7), r.GetInt64(8),
            }, c.Ct)),

        new("resolutions", "تقرير القرارات", "Resolutions report",
            "القرارات الصادرة في الفترة (أو المنشأة إن لم تصدر) مع الجهة المسؤولة وحالة مهام التنفيذ.", "Resolutions issued in the period (or created, if not issued) with the responsible party and execution tasks.",
            Permissions.ReportsBody, null, true,
            [C("number", "الرقم", "Number", 1.1), C("title", "العنوان", "Title", 2.6), C("body", "الجهة", "Body", 1.6), C("status", "الحالة", "Status", 1, "status:resolution"),
             C("issuedAt", "تاريخ الصدور", "Issued", 1, "date"), C("responsible", "المسؤول عن التنفيذ", "Responsible", 1.6), C("tasks", "المهام", "Tasks", 0.6, "int"),
             C("tasksDone", "المكتملة", "Done", 0.6, "int"), C("tasksOverdue", "المتأخرة", "Overdue", 0.6, "int"), C("nextDue", "أقرب استحقاق", "Next due", 1, "date")],
            async c => await c.Db.QueryAsync($"""
                SELECT r.number, r.title_ar, b.name_ar, r.status, r.issued_at, coalesce(e.name_ar, o.name_ar, ''),
                  (SELECT count(*) FROM tasks.tasks t WHERE t.resolution_id = r.id AND t.status <> 'cancelled'),
                  (SELECT count(*) FROM tasks.tasks t WHERE t.resolution_id = r.id AND t.status = 'completed'),
                  (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.resolution_id = r.id AND {ReportScope.OpenTask} AND t.due_date < {ReportScope.Day}),
                  (SELECT min(t.due_date) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.resolution_id = r.id AND {ReportScope.OpenTask})
                FROM resolutions.resolutions r JOIN bodies.bodies b ON b.id = r.body_id
                LEFT JOIN org.org_units o ON o.id = r.responsible_org_unit_id LEFT JOIN org.external_entities e ON e.id = r.responsible_external_entity_id
                WHERE r.body_id = ANY(@ids) AND {ReportScope.InPeriod("coalesce(r.issued_at, r.created_at)")} AND (@status::text IS NULL OR r.status = @status) AND {ReportScope.ResolutionUnit}
                ORDER BY coalesce(r.issued_at, r.created_at) DESC LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r => new object?[]
            {
                r.Str("number"), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : Local(r.GetDateTime(4)), r.GetString(5),
                r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.Date("min"),
            }, c.Ct)),

        new("overdue_tasks", "تقرير المهام المتأخرة", "Overdue tasks report",
            "لقطة الآن: المهام المفتوحة أو النشطة التي تجاوزت استحقاقها، مرتبة بالأقدم تأخرًا. الفترة لا تنطبق.",
            "Snapshot now: open or active tasks past their due date, oldest first. The period does not apply.",
            Permissions.ReportsBody, null, true,
            [C("referenceNo", "المرجع", "Reference", 1.1), C("title", "المهمة", "Task", 2.6), C("body", "الجهة", "Body", 1.5), C("resolution", "القرار", "Resolution", 1),
             C("assignee", "المكلف", "Assignee", 1.6), C("unit", "الإدارة/الجهة", "Unit / entity", 1.5), C("dueDate", "الاستحقاق", "Due", 0.9, "date"),
             C("daysOverdue", "أيام التأخر", "Days overdue", 0.7, "int"), C("status", "الحالة", "Status", 0.9, "status:task")],
            async c => await c.Db.QueryAsync($"""
                SELECT t.reference_no, t.title_ar, b.name_ar, r.number, coalesce(u.display_name_ar, ''), coalesce(e.name_ar, o.name_ar, ''), t.due_date,
                  ({ReportScope.Day} - t.due_date)::bigint, t.status
                FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status JOIN bodies.bodies b ON b.id = t.body_id
                LEFT JOIN resolutions.resolutions r ON r.id = t.resolution_id LEFT JOIN iam.users u ON u.id = t.assignee_user_id
                LEFT JOIN org.org_units o ON o.id = coalesce(t.assignee_org_unit_id, t.executing_org_unit_id) LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
                WHERE t.body_id = ANY(@ids) AND ws.category IN ('open','active') AND t.due_date < {ReportScope.Day} AND {ReportScope.TaskUnit}
                  AND (@status::text IS NULL OR t.status = @status)
                ORDER BY t.due_date, t.reference_no LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r => new object?[]
            {
                r.Str("reference_no"), r.GetString(1), r.GetString(2), r.Str("number"), r.GetString(4), r.GetString(5), r.Date("due_date"), r.GetInt64(7), r.GetString(8),
            }, c.Ct)),

        new("body_performance", "تقرير أداء المجالس واللجان", "Councils & committees performance report",
            "لكل جهة: الاجتماعات المنعقدة والحضور والقرارات والمهام والالتزام وزمن اعتماد المحاضر خلال الفترة (التعاريف في 23 §1).",
            "Per body: meetings held, attendance, resolutions, tasks, compliance and minutes approval time in the period (definitions in doc 23 §1).",
            Permissions.ReportsBody, null, true,
            [C("code", "الرمز", "Code", 0.9), C("body", "الجهة", "Body", 2.2), C("type", "النوع", "Type", 1.2, "status:bodytype"), C("held", "اجتماعات منعقدة", "Meetings held", 0.8, "int"),
             C("attendance", "الحضور %", "Attendance %", 0.8, "pct"), C("issued", "قرارات صادرة", "Resolutions", 0.8, "int"), C("resOverdue", "قرارات متأخرة", "Overdue res.", 0.8, "int"),
             C("completed", "مهام مكتملة", "Tasks done", 0.8, "int"), C("overdue", "مهام متأخرة", "Overdue tasks", 0.8, "int"), C("onTime", "في الموعد %", "On time %", 0.8, "pct"),
             C("approvalDays", "أيام اعتماد المحضر", "Minutes days", 0.9, "num"), C("minutesPending", "محاضر قيد الاعتماد", "Minutes pending", 0.8, "int")],
            async c => (await Kpis.BodiesAsync(c.Db, c.Bodies, c.Filter, c.Ct)).Select(b => new object?[]
            {
                b.Code, b.Name, b.BodyType, b.MeetingsHeld, b.AttendanceRate, b.ResolutionsIssued, b.ResolutionsOverdue, b.TasksCompleted, b.TasksOverdue, b.OnTimeRate,
                b.AvgApprovalDays, b.MinutesPending,
            }).ToList()),

        new("signatures", "تقرير التوقيعات وحالاتها", "Signatures & status report",
            "طلبات توقيع المحاضر المنشأة في الفترة: النوع والمزود والحالة وعدد الموقعين ومن وقّع.", "Minutes signature requests created in the period: type, provider, status and signers.",
            Permissions.ReportsBody, null, true,
            [C("referenceNo", "مرجع الطلب", "Request ref.", 1.2), C("meeting", "الاجتماع", "Meeting", 2.2), C("body", "الجهة", "Body", 1.5), C("version", "الإصدار", "Version", 0.6, "int"),
             C("type", "نوع التوقيع", "Signature type", 1.3, "status:sigtype"), C("provider", "المزود", "Provider", 0.9), C("status", "الحالة", "Status", 1, "status:sig"),
             C("signed", "وقّع", "Signed", 0.6, "int"), C("signers", "الموقعون", "Signers", 0.7, "int"), C("createdAt", "الإنشاء", "Created", 1.1, "datetime"),
             C("completedAt", "الاكتمال", "Completed", 1.1, "datetime"), C("expiresAt", "الانتهاء", "Expires", 1, "date")],
            async c => await c.Db.QueryAsync($"""
                SELECT rq.reference_no, m.title_ar, b.name_ar, rq.version_no, rq.signature_type, rq.provider, rq.status,
                  (SELECT count(*) FROM sig.signers s WHERE s.request_id = rq.id AND s.status = 'signed'),
                  (SELECT count(*) FROM sig.signers s WHERE s.request_id = rq.id AND s.status <> 'cancelled'),
                  rq.created_at, rq.completed_at, rq.expires_at
                FROM sig.requests rq JOIN minutes.minutes mi ON mi.id = rq.minutes_id JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = rq.body_id
                WHERE rq.body_id = ANY(@ids) AND {ReportScope.InPeriod("rq.created_at")} AND (@status::text IS NULL OR rq.status = @status)
                ORDER BY rq.created_at DESC LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r => new object?[]
            {
                r.GetString(0), r.GetString(1), r.GetString(2), (long)r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetInt64(8),
                r.GetDateTime(9), r.IsDBNull(10) ? null : r.GetDateTime(10), Local(r.GetDateTime(11)),
            }, c.Ct)),

        new("audit", "تقرير التغييرات والتدقيق", "Changes & audit report",
            "أحداث سجل التدقيق في الفترة: المنفذ والوحدة والإجراء والكيان والنتيجة والحقول المتغيرة (دون القيم). الحالة = النتيجة.",
            "Audit trail events in the period: actor, module, action, entity, outcome and changed field names (values omitted). Status = outcome.",
            Permissions.AuditRead, Permissions.AuditExport, false,
            [C("occurredAt", "الوقت", "Time", 1.2, "datetime"), C("actor", "المنفذ", "Actor", 1.6), C("module", "الوحدة", "Module", 0.7), C("action", "الإجراء", "Action", 1.6),
             C("entityType", "نوع الكيان", "Entity type", 1.1), C("entityId", "المعرف", "Entity id", 1.9), C("outcome", "النتيجة", "Outcome", 0.7, "status:outcome"),
             C("fields", "الحقول المتغيرة", "Changed fields", 2.2)],
            async c => await c.Db.QueryAsync($"""
                SELECT a.occurred_at, coalesce(u.display_name_ar, CASE WHEN a.actor_user_id IS NULL THEN 'النظام' ELSE a.actor_user_id::text END), a.module, a.action, a.entity_type,
                  coalesce(a.entity_id, ''), a.outcome,
                  coalesce((SELECT string_agg(k, '، ' ORDER BY k) FROM jsonb_object_keys(CASE WHEN jsonb_typeof(a.after) = 'object' THEN a.after ELSE jsonb_build_object() END) k), '')
                FROM audit.audit_events a LEFT JOIN iam.users u ON u.id = a.actor_user_id
                WHERE {ReportScope.InPeriod("a.occurred_at")} AND (@status::text IS NULL OR a.outcome = @status)
                ORDER BY a.occurred_at DESC, a.id DESC LIMIT {MaxRows + 1}
                """, ReportScope.Args(c.Bodies, c.Filter), r => new object?[]
            {
                r.GetDateTime(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
            }, c.Ct)),

        new("compliance", "تقرير الالتزام بمواعيد التنفيذ", "Due-date compliance report",
            "لكل إدارة أو جهة خارجية: المسند في الفترة، والمكتمل، والمكتمل في الموعد، والمتأخر المفتوح الآن، ونسبة الالتزام، ومتوسط التأخر.",
            "Per unit or external entity: assigned in period, completed, completed on time, open overdue now, compliance index and average delay.",
            Permissions.ReportsBody, null, true,
            [C("type", "النوع", "Type", 0.9, "status:assignee"), C("name", "الإدارة / الجهة", "Unit / entity", 2.6), C("assigned", "مسند في الفترة", "Assigned", 0.9, "int"),
             C("completed", "مكتمل", "Completed", 0.8, "int"), C("onTime", "في الموعد", "On time", 0.8, "int"), C("overdue", "متأخر مفتوح", "Open overdue", 0.9, "int"),
             C("onTimeRate", "الإنجاز في الموعد %", "On-time %", 0.9, "pct"), C("index", "مؤشر الالتزام %", "Compliance %", 0.9, "pct"),
             C("avgDelay", "متوسط التأخر (يوم)", "Avg delay (days)", 0.9, "num")],
            async c => (await Kpis.ComplianceAsync(c.Db, c.Bodies, c.Filter, c.Ct)).Select(x => new object?[]
            {
                x.AssigneeType, x.Name, x.Assigned, x.Completed, x.CompletedOnTime, x.OpenOverdue, x.OnTimeRate, x.ComplianceIndex, x.AvgDelayDays,
            }).ToList()),
    ];

    private static DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz));
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById(Riyadh);

    public static string Label(string kind, object? v, bool en)
    {
        if (v is not string s) return "";
        return Labels.TryGetValue(kind + "." + s, out var l) ? (en ? l.En : l.Ar) : s;
    }

    /// <summary>القيمة للعرض النصي (PDF) أو الخلية (Excel). التواريخ بتوقيت الرياض.</summary>
    private static object? Cell(ReportColumn c, object? v, bool en, bool forExcel)
    {
        if (v is null) return forExcel ? null : "—";
        if (c.Kind.StartsWith("status:")) return Label(c.Kind[7..], v, en);
        return v switch
        {
            DateTime dt when c.Kind == "datetime" => forExcel ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), Tz)
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), Tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateTime dt => forExcel ? Local(dt) : Local(dt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly d => forExcel ? d : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            double n when c.Kind == "pct" => forExcel ? n : n.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            double n => forExcel ? n : n.ToString("0.#", CultureInfo.InvariantCulture),
            long n => forExcel ? n : n.ToString(CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
    }

    public static object Describe(ReportDef d) => new
    {
        code = d.Code, titleAr = d.TitleAr, titleEn = d.TitleEn, descriptionAr = d.DescAr, descriptionEn = d.DescEn, permission = d.Permission,
        exportPermission = d.ExportPermission ?? d.Permission, bodyScoped = d.BodyScoped,
        columns = d.Columns.Select(c => new { key = c.Key, ar = c.Ar, en = c.En, kind = c.Kind }),
    };

    public static async Task<IResult> CatalogEndpoint(IAccessService access, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var d in All)
        {
            var can = d.BodyScoped ? (await access.GetBodyFilterAsync(d.Permission, ct)) is { } f && (f.All || f.BodyIds.Length > 0) : await access.HasAsync(d.Permission, Scope.System, ct);
            if (!can) continue;
            var canExport = d.ExportPermission is null || await access.HasAsync(d.ExportPermission, Scope.System, ct);
            list.Add(new { report = Describe(d), canExport });
        }
        return Results.Ok(new { items = list, maxRows = MaxRows, labels = Labels.ToDictionary(l => l.Key, l => new { ar = l.Value.Ar, en = l.Value.En }) });
    }

    public static async Task<IResult> RunEndpoint(string code, string? format, string? lang, HttpRequest request, Db db, IAccessService access, IClock clock,
        ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var d = All.FirstOrDefault(x => x.Code == code) ?? throw new NotFoundException("التقرير غير موجود");
        var fmt = (format ?? "json").ToLowerInvariant();
        if (fmt is not ("json" or "pdf" or "xlsx")) throw AppValidationException.For("format", "الصيغ: json أو pdf أو xlsx");
        var f = ReportFilter.Parse(request, clock.TodayLocal);
        Guid[] ids = [];
        if (d.BodyScoped) ids = await ReportScope.BodiesAsync(db, access, f, ct, d.Permission);
        else await access.DemandAsync(d.Permission, Scope.System, ct);
        // التصدير قد يتطلب صلاحية أعلى من العرض (سجل التدقيق: audit.read للعرض، audit.export للتصدير)
        if (fmt != "json" && d.ExportPermission is not null) await access.DemandAsync(d.ExportPermission, Scope.System, ct);

        var rows = await d.Load(new ReportContext(db, ids, f, ct));
        var truncated = rows.Count > MaxRows;
        if (truncated) rows = rows.Take(MaxRows).ToList();
        if (fmt == "json")
            return Results.Ok(new { report = Describe(d), filter = f, generatedAt = DateTime.UtcNow, rowCount = rows.Count, truncated, rows = rows.Select(r => ToJson(d, r)) });

        var en = lang == "en";
        var title = en ? d.TitleEn : d.TitleAr;
        var stamp = clock.UtcNow;
        var names = await db.QuerySingleAsync("""
            SELECT (SELECT coalesce(CASE WHEN @en THEN name_en END, name_ar) FROM bodies.bodies WHERE id = @b),
                   (SELECT coalesce(CASE WHEN @en THEN name_en END, name_ar) FROM org.org_units WHERE id = @u),
                   (SELECT coalesce(CASE WHEN @en THEN name_en END, name_ar) FROM org.external_entities WHERE id = @e)
            """, new { en, b = f.BodyId, u = f.OrgUnitId, e = f.EntityId }, r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ct);
        var filterText = FilterText(f, en, names);
        byte[] bytes;
        string contentType, ext;
        if (fmt == "pdf")
        {
            var pdf = new PdfReport(new PdfReportOptions
            {
                Title = title, Landscape = true, Rtl = !en, Author = me.DisplayName ?? "",
                HeaderLine1 = en ? "Strategic Office for Al-Jouf Region Development" : "المكتب الاستراتيجي لتطوير منطقة الجوف",
                HeaderLine2 = en ? "Councils & Committees Management System" : "نظام إدارة المجالس واللجان",
                FooterText = (en ? "Generated " : "أُنشئ ") + stamp.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            });
            pdf.Title(title);
            pdf.KeyValues([
                (en ? "Period / filters" : "الفترة والمرشحات", filterText),
                (en ? "Rows" : "عدد الصفوف", rows.Count.ToString(CultureInfo.InvariantCulture) + (truncated ? (en ? $" (first {MaxRows})" : $" (أول {MaxRows})") : "")),
                (en ? "Generated by" : "أنشأه", me.DisplayName),
                (en ? "Scope" : "النطاق", en ? d.DescEn : d.DescAr),
            ]);
            pdf.Spacer();
            var total = d.Columns.Sum(c => c.Width);
            pdf.Table(d.Columns.Select(c => en ? c.En : c.Ar).ToList(), d.Columns.Select(c => c.Width / total).ToList(),
                rows.Count == 0 ? [[en ? "No data for these filters." : "لا توجد بيانات لهذه المرشحات.", .. Enumerable.Repeat<string?>("", d.Columns.Length - 1)]]
                    : rows.Select(r => (IReadOnlyList<string?>)d.Columns.Select((c, i) => (string?)Cell(c, r[i], en, false)).ToList()).ToList(), size: 8);
            bytes = pdf.Build();
            (contentType, ext) = ("application/pdf", "pdf");
        }
        else
        {
            var x = new XlsxWriter { Creator = me.DisplayName ?? "CCMS" };
            x.AddSheet(en ? "Data" : "البيانات", d.Columns.Select(c => en ? c.En : c.Ar).ToList(),
                rows.Select(r => (IReadOnlyList<object?>)d.Columns.Select((c, i) => Cell(c, r[i], en, true)).ToList()).ToList(),
                d.Columns.Select(c => Math.Max(10, c.Width * 12)).ToList(), rtl: !en);
            x.AddSheet(en ? "About" : "عن التقرير", [en ? "Item" : "البند", en ? "Value" : "القيمة"],
            [
                [en ? "Report" : "التقرير", title], [en ? "Description" : "الوصف", en ? d.DescEn : d.DescAr], [en ? "Filters" : "المرشحات", filterText],
                [en ? "Rows" : "عدد الصفوف", (long)rows.Count], [en ? "Truncated" : "مقتطع", truncated ? (en ? "Yes" : "نعم") : (en ? "No" : "لا")],
                [en ? "Generated at (UTC)" : "وقت الإنشاء (UTC)", stamp], [en ? "Generated by" : "أنشأه", me.DisplayName],
                [en ? "Source" : "المصدر", en ? "Live database query; KPI definitions in doc 23 §1" : "استعلام مباشر من قاعدة البيانات؛ تعاريف المؤشرات في الوثيقة 23 §1"],
            ], [28, 90], rtl: !en);
            bytes = x.Build();
            (contentType, ext) = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
        }
        await audit.WriteAsync(new AuditEntry("RPT", "report.export", "report", d.Code, After: new
        {
            format = fmt, f.From, f.To, f.BodyId, f.BodyKind, f.OrgUnitId, f.EntityId, f.Status, rows = rows.Count, truncated, bytes = bytes.Length,
        }), ct);
        return Results.File(bytes, contentType, $"{d.Code}-{f.From:yyyyMMdd}-{f.To:yyyyMMdd}.{ext}");
    }

    private static Dictionary<string, object?> ToJson(ReportDef d, object?[] row)
    {
        var o = new Dictionary<string, object?>();
        for (var i = 0; i < d.Columns.Length; i++) o[d.Columns[i].Key] = row[i];
        return o;
    }

    private static string FilterText(ReportFilter f, bool en, (string? Body, string? Unit, string? Entity) n)
    {
        var parts = new List<string> { $"{f.From:yyyy-MM-dd} {(en ? "to" : "إلى")} {f.To:yyyy-MM-dd}" };
        if (n.Body is not null) parts.Add(n.Body);
        if (f.BodyKind is not null) parts.Add(f.BodyKind == "council" ? (en ? "Councils" : "المجالس") : (en ? "Committees" : "اللجان"));
        if (f.OrgUnitId is not null) parts.Add(n.Unit ?? f.OrgUnitId.ToString()!);
        if (f.EntityId is not null) parts.Add(n.Entity ?? f.EntityId.ToString()!);
        if (f.Status is not null) parts.Add((en ? "Status " : "الحالة ") + f.Status);
        return string.Join(" · ", parts);
    }
}
