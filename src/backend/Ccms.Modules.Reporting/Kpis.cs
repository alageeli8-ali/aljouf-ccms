using System.Globalization;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Reporting;

/// <summary>مرشحات التقارير واللوحة. الفترة بتواريخ الرياض شاملة الطرفين.</summary>
public sealed record ReportFilter(DateOnly From, DateOnly To, Guid? BodyId, string? BodyKind, Guid? OrgUnitId, Guid? EntityId, string? Status)
{
    public static readonly string[] Councils = ["supreme_council", "steering_council", "executive_council"];
    public static readonly string[] Committees = ["permanent_committee", "temporary_committee", "working_team", "sub_committee", "joint_committee"];

    public static ReportFilter Parse(HttpRequest r, DateOnly today)
    {
        DateOnly? D(string k) => DateOnly.TryParseExact(r.Query[k].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        Guid? G(string k) => Guid.TryParse(r.Query[k].ToString(), out var g) ? g : null;
        string? S(string k) => r.Query[k].ToString() is { Length: > 0 } s ? s : null;
        var to = D("to") ?? today;
        var from = D("from") ?? new DateOnly(to.Year, 1, 1);
        if (from > to) throw AppValidationException.For("from", "بداية الفترة بعد نهايتها");
        if (to.DayNumber - from.DayNumber > 3660) throw AppValidationException.For("from", "الفترة أطول من عشر سنوات");
        var kind = S("bodyKind");
        if (kind is not null and not ("council" or "committee")) throw AppValidationException.For("bodyKind", "النوع: council أو committee");
        return new ReportFilter(from, to, G("bodyId"), kind, G("orgUnitId"), G("entityId"), S("status"));
    }

    public string[]? Kinds => BodyKind switch { "council" => Councils, "committee" => Committees, _ => null };
}

/// <summary>نطاق الجهات المسموح بها للمستخدم بعد تطبيق مرشحات الجهة والنوع — يُحسب مرة ويُمرر لكل استعلام (كل رقم مقيد بصلاحية المستخدم).</summary>
internal static class ReportScope
{
    public static async Task<Guid[]> BodiesAsync(Db db, IAccessService access, ReportFilter f, CancellationToken ct, string permission = Permissions.ReportsBody)
    {
        var scope = await access.GetBodyFilterAsync(permission, ct);
        if (!scope.All && scope.BodyIds.Length == 0) throw new ForbiddenException(permission: permission);
        return (await db.QueryAsync("""
            SELECT id FROM bodies.bodies
            WHERE (@all::boolean OR id = ANY(@ids::uuid[])) AND (@body::uuid IS NULL OR id = @body) AND (@kinds::text[] IS NULL OR body_type = ANY(@kinds::text[]))
            """, new { all = scope.All, ids = scope.BodyIds, body = f.BodyId, kinds = f.Kinds }, r => r.GetGuid(0), ct)).ToArray();
    }

    public static object Args(Guid[] ids, ReportFilter f) => new
    {
        ids, from = f.From, to = f.To, unit = f.OrgUnitId, entity = f.EntityId, status = f.Status,
    };

    public const string Day = "(now() AT TIME ZONE 'Asia/Riyadh')::date";
    public static string InPeriod(string ts) => $"({ts} AT TIME ZONE 'Asia/Riyadh')::date BETWEEN @from AND @to";
    public const string OpenTask = "ws.category IN ('open','active','waiting','review')";
    public const string TaskUnit = "(@unit::uuid IS NULL OR t.assignee_org_unit_id = @unit OR t.executing_org_unit_id = @unit) AND (@entity::uuid IS NULL OR t.assignee_external_entity_id = @entity)";
    public const string ResolutionUnit = """
        (@unit::uuid IS NULL OR r.responsible_org_unit_id = @unit OR EXISTS (SELECT 1 FROM tasks.tasks tu WHERE tu.resolution_id = r.id AND (tu.assignee_org_unit_id = @unit OR tu.executing_org_unit_id = @unit)))
        AND (@entity::uuid IS NULL OR r.responsible_external_entity_id = @entity OR EXISTS (SELECT 1 FROM tasks.tasks te WHERE te.resolution_id = r.id AND te.assignee_external_entity_id = @entity))
        """;
}

/// <summary>تعريف المؤشر وطريقة احتسابه (23 §1). تعرضه الواجهة بجانب كل رقم، ويُطابق النص الموثق.</summary>
public sealed record KpiDefinition(string Code, string TitleAr, string TitleEn, string FormulaAr, string FormulaEn, string Basis);

public static class KpiCatalog
{
    public static readonly KpiDefinition[] All =
    [
        new("bodies_active", "المجالس واللجان الفعالة", "Active councils & committees",
            "عدد الجهات بحالة «فعالة» ضمن نطاق المستخدم والمرشحات، الآن.", "Bodies with status ‘active’ within the user’s scope and filters, now.", "snapshot"),
        new("meetings_planned", "الاجتماعات المخططة", "Planned meetings",
            "الاجتماعات التي يقع موعدها في الفترة وحالتها ليست مسودة ولا ملغاة (تشمل المنعقدة والقادمة والمؤجلة).",
            "Meetings dated within the period whose status is neither draft nor cancelled (held, upcoming and postponed).", "period"),
        new("meetings_held", "الاجتماعات المنعقدة", "Meetings held",
            "الاجتماعات التي يقع موعدها في الفترة وحالتها «منتهٍ» أو «مغلق».", "Meetings dated within the period with status concluded or closed.", "period"),
        new("attendance_rate", "نسبة الحضور", "Attendance rate",
            "(سجلات حضور الأعضاء بحالة حاضر أو بالإنابة) ÷ (كل سجلات حضور الأعضاء) في الاجتماعات المنعقدة في الفترة؛ الضيوف مستبعدون والمعتذر غير حاضر.",
            "(Member attendance records present or by proxy) ÷ (all member attendance records) in meetings held in the period; guests excluded, excused counts as absent.", "period"),
        new("resolutions_issued", "القرارات الصادرة", "Resolutions issued",
            "القرارات والتوصيات التي يقع تاريخ صدورها في الفترة.", "Resolutions whose issue date falls within the period.", "period"),
        new("resolutions_executed", "القرارات المنفذة", "Resolutions executed",
            "القرارات التي انتقلت إلى «منفذ» خلال الفترة (من سجل حالات القرار).", "Resolutions that moved to ‘executed’ during the period (from the status history).", "period"),
        new("resolutions_overdue", "القرارات المتأخرة", "Overdue resolutions",
            "القرارات الصادرة أو قيد التنفيذ التي لها مهمة مفتوحة تجاوزت استحقاقها، الآن (القرار لا يحمل موعدًا مستقلًا).",
            "Issued or in-execution resolutions having an open task past its due date, now (resolutions carry no due date of their own).", "snapshot"),
        new("tasks_open", "المهام المفتوحة", "Open tasks",
            "المهام في حالة مفتوحة أو نشطة أو انتظار أو مراجعة، الآن.", "Tasks in an open, active, waiting or review status, now.", "snapshot"),
        new("tasks_completed", "المهام المكتملة", "Tasks completed",
            "المهام التي يقع تاريخ إكمالها في الفترة.", "Tasks whose completion date falls within the period.", "period"),
        new("tasks_overdue", "المهام المتأخرة", "Overdue tasks",
            "المهام المفتوحة أو النشطة التي تجاوزت تاريخ استحقاقها، الآن.", "Open or active tasks past their due date, now.", "snapshot"),
        new("task_avg_days", "متوسط مدة تنفيذ المهام (يوم)", "Average task duration (days)",
            "متوسط (تاريخ الإكمال − تاريخ الإسناد، أو الإنشاء إن لم تُسند) للمهام المكتملة في الفترة.",
            "Average of (completion − assignment, or creation if never assigned) for tasks completed in the period.", "period"),
        new("task_on_time_rate", "نسبة الإنجاز في الموعد", "On-time completion rate",
            "المهام المكتملة في الفترة بتاريخ إكمال لا يتجاوز الاستحقاق ÷ كل المهام المكتملة في الفترة.",
            "Tasks completed in the period on or before their due date ÷ all tasks completed in the period.", "period"),
        new("minutes_pending_review", "محاضر بانتظار المراجعة", "Minutes awaiting review",
            "المحاضر بحالة مراجعة أمين السر أو الرئيس أو ملاحظات الأعضاء أو معادة للتعديل، الآن.", "Minutes in secretary, chair or member review, or returned, now.", "snapshot"),
        new("minutes_pending_signatures", "محاضر بانتظار التوقيع", "Minutes awaiting signatures",
            "المحاضر بحالة «بانتظار التوقيع»، الآن.", "Minutes in ‘pending signatures’, now.", "snapshot"),
        new("minutes_pending_approval", "محاضر بانتظار الاعتماد", "Minutes awaiting approval",
            "المحاضر بحالة «بانتظار الاعتماد النهائي»، الآن.", "Minutes in ‘pending final approval’, now.", "snapshot"),
        new("minutes_avg_approval_days", "متوسط زمن اعتماد المحاضر (يوم)", "Average minutes approval time (days)",
            "متوسط (تاريخ الاعتماد − نهاية الاجتماع الفعلية، أو موعده إن لم تُسجل) للمحاضر المعتمدة في الفترة.",
            "Average of (approval − actual meeting end, or scheduled start if none) for minutes approved in the period.", "period"),
        new("compliance_index", "مؤشر الالتزام بالتكليفات", "Assignment compliance index",
            "لكل إدارة أو جهة خارجية: المكتمل في الموعد خلال الفترة ÷ (المكتمل خلال الفترة + المفتوح المتأخر الآن).",
            "Per unit or external entity: completed on time in the period ÷ (completed in the period + open overdue now).", "mixed"),
    ];
}

internal static class Kpis
{
    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);
    private static double? Pct(long num, long den) => den == 0 ? null : Math.Round(100.0 * num / den, 1);

    public static async Task<Dictionary<string, object?>> ComputeAsync(Db db, Guid[] ids, ReportFilter f, CancellationToken ct)
    {
        var a = ReportScope.Args(ids, f);
        // مسح واحد لكل جدول بتجميع FILTER بدل استعلام فرعي لكل مؤشر (24 §6)
        var k = await db.QuerySingleAsync($"""
            WITH mt AS (
              SELECT count(*) FILTER (WHERE m.status NOT IN ('draft','cancelled')) AS planned, count(*) FILTER (WHERE m.status IN ('concluded','closed')) AS held
              FROM meetings.meetings m WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")}
            ), att AS (
              SELECT count(*) FILTER (WHERE ar.status IN ('present','proxy')) AS present, count(*) AS expected
              FROM meetings.attendance_records ar JOIN meetings.meetings m ON m.id = ar.meeting_id
              WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")} AND m.status IN ('concluded','closed') AND ar.body_member_id IS NOT NULL
            ), rs AS (
              SELECT count(*) FILTER (WHERE r.issued_at IS NOT NULL AND {ReportScope.InPeriod("r.issued_at")}) AS issued,
                     count(*) FILTER (WHERE r.status IN ('issued','in_execution') AND EXISTS (SELECT 1 FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
                                      WHERE t.resolution_id = r.id AND {ReportScope.OpenTask} AND t.due_date < {ReportScope.Day})) AS overdue
              FROM resolutions.resolutions r WHERE r.body_id = ANY(@ids) AND {ReportScope.ResolutionUnit}
            ), ex AS (
              SELECT count(DISTINCT h.resolution_id) AS executed FROM resolutions.resolution_status_history h JOIN resolutions.resolutions r ON r.id = h.resolution_id
              WHERE r.body_id = ANY(@ids) AND h.to_status = 'executed' AND {ReportScope.InPeriod("h.changed_at")} AND {ReportScope.ResolutionUnit}
            ), tk AS (
              SELECT count(*) FILTER (WHERE {ReportScope.OpenTask}) AS open,
                     count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")}) AS completed,
                     count(*) FILTER (WHERE ws.category IN ('open','active') AND t.due_date < {ReportScope.Day}) AS overdue,
                     avg(extract(epoch FROM (t.completed_at - coalesce(t.assigned_at, t.created_at))) / 86400.0)
                       FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")})::float8 AS avg_days,
                     count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")}
                                      AND t.due_date IS NOT NULL AND (t.completed_at AT TIME ZONE 'Asia/Riyadh')::date <= t.due_date) AS on_time
              FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.body_id = ANY(@ids) AND {ReportScope.TaskUnit}
            ), mi AS (
              SELECT count(*) FILTER (WHERE mi.status IN ('secretary_review','chair_review','member_review','returned')) AS review,
                     count(*) FILTER (WHERE mi.status = 'pending_signatures') AS signatures,
                     count(*) FILTER (WHERE mi.status = 'pending_final_approval') AS approval,
                     avg(extract(epoch FROM (mi.approved_at - coalesce(m.actual_ended_at, m.starts_at))) / 86400.0)
                       FILTER (WHERE mi.approved_at IS NOT NULL AND {ReportScope.InPeriod("mi.approved_at")})::float8 AS avg_approval
              FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE m.body_id = ANY(@ids)
            )
            SELECT (SELECT count(*) FROM bodies.bodies b WHERE b.id = ANY(@ids) AND b.status = 'active'),
                   mt.planned, mt.held, att.present, att.expected, rs.issued, ex.executed, rs.overdue, tk.open, tk.completed, tk.overdue, tk.avg_days, tk.on_time,
                   mi.review, mi.signatures, mi.approval, mi.avg_approval
            FROM mt, att, rs, ex, tk, mi
            """, a, r => new
        {
            Bodies = r.GetInt64(0), Planned = r.GetInt64(1), Held = r.GetInt64(2), Present = r.GetInt64(3), Expected = r.GetInt64(4), Issued = r.GetInt64(5),
            Executed = r.GetInt64(6), ResOverdue = r.GetInt64(7), Open = r.GetInt64(8), Completed = r.GetInt64(9), TaskOverdue = r.GetInt64(10),
            AvgDays = r.IsDBNull(11) ? (double?)null : r.GetDouble(11), OnTime = r.GetInt64(12), PendReview = r.GetInt64(13), PendSign = r.GetInt64(14),
            PendApproval = r.GetInt64(15), AvgApproval = r.IsDBNull(16) ? (double?)null : r.GetDouble(16),
        }, ct);
        return new Dictionary<string, object?>
        {
            ["bodies_active"] = k!.Bodies, ["meetings_planned"] = k.Planned, ["meetings_held"] = k.Held,
            ["attendance_rate"] = Pct(k.Present, k.Expected), ["attendance_present"] = k.Present, ["attendance_expected"] = k.Expected,
            ["resolutions_issued"] = k.Issued, ["resolutions_executed"] = k.Executed, ["resolutions_overdue"] = k.ResOverdue,
            ["tasks_open"] = k.Open, ["tasks_completed"] = k.Completed, ["tasks_overdue"] = k.TaskOverdue,
            ["task_avg_days"] = Round(k.AvgDays), ["task_on_time_rate"] = Pct(k.OnTime, k.Completed), ["tasks_on_time"] = k.OnTime,
            ["minutes_pending_review"] = k.PendReview, ["minutes_pending_signatures"] = k.PendSign, ["minutes_pending_approval"] = k.PendApproval,
            ["minutes_avg_approval_days"] = Round(k.AvgApproval),
        };
    }

    public sealed record BodyRow(Guid BodyId, string Code, string Name, string BodyType, string Status, long MeetingsHeld, double? AttendanceRate, long ResolutionsIssued,
        long ResolutionsOverdue, long TasksCompleted, long TasksOverdue, double? OnTimeRate, double? AvgApprovalDays, long MinutesPending);

    /// <summary>أداء الجهات بتجميع واحد لكل جدول (لا استعلامات فرعية لكل جهة) — يبقى ثابت الزمن تقريبًا مع زيادة عدد الجهات (24 §6).</summary>
    public static async Task<List<BodyRow>> BodiesAsync(Db db, Guid[] ids, ReportFilter f, CancellationToken ct) =>
        await db.QueryAsync($"""
            WITH mt AS (
              SELECT m.body_id, count(*) AS held FROM meetings.meetings m
              WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")} AND m.status IN ('concluded','closed') GROUP BY m.body_id
            ), att AS (
              SELECT m.body_id, count(*) FILTER (WHERE ar.status IN ('present','proxy')) AS present, count(*) AS expected
              FROM meetings.attendance_records ar JOIN meetings.meetings m ON m.id = ar.meeting_id
              WHERE m.body_id = ANY(@ids) AND {ReportScope.InPeriod("m.starts_at")} AND m.status IN ('concluded','closed') AND ar.body_member_id IS NOT NULL GROUP BY m.body_id
            ), rs AS (
              SELECT r.body_id,
                count(*) FILTER (WHERE r.issued_at IS NOT NULL AND {ReportScope.InPeriod("r.issued_at")}) AS issued,
                count(*) FILTER (WHERE r.status IN ('issued','in_execution') AND EXISTS (SELECT 1 FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
                                 WHERE t.resolution_id = r.id AND {ReportScope.OpenTask} AND t.due_date < {ReportScope.Day})) AS overdue
              FROM resolutions.resolutions r WHERE r.body_id = ANY(@ids) AND {ReportScope.ResolutionUnit} GROUP BY r.body_id
            ), tk AS (
              SELECT t.body_id,
                count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")}) AS completed,
                count(*) FILTER (WHERE ws.category IN ('open','active') AND t.due_date < {ReportScope.Day}) AS overdue,
                count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")}
                                 AND t.due_date IS NOT NULL AND (t.completed_at AT TIME ZONE 'Asia/Riyadh')::date <= t.due_date) AS on_time
              FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.body_id = ANY(@ids) AND {ReportScope.TaskUnit} GROUP BY t.body_id
            ), mi AS (
              SELECT m.body_id,
                avg(extract(epoch FROM (mi.approved_at - coalesce(m.actual_ended_at, m.starts_at))) / 86400.0)
                  FILTER (WHERE mi.approved_at IS NOT NULL AND {ReportScope.InPeriod("mi.approved_at")})::float8 AS approval_days,
                count(*) FILTER (WHERE mi.status NOT IN ('draft','approved')) AS pending
              FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE m.body_id = ANY(@ids) GROUP BY m.body_id
            )
            SELECT b.id, b.code, b.name_ar, b.body_type, b.status, coalesce(mt.held, 0), coalesce(att.present, 0), coalesce(att.expected, 0), coalesce(rs.issued, 0),
                   coalesce(rs.overdue, 0), coalesce(tk.completed, 0), coalesce(tk.overdue, 0), coalesce(tk.on_time, 0), mi.approval_days, coalesce(mi.pending, 0)
            FROM bodies.bodies b LEFT JOIN mt ON mt.body_id = b.id LEFT JOIN att ON att.body_id = b.id LEFT JOIN rs ON rs.body_id = b.id
              LEFT JOIN tk ON tk.body_id = b.id LEFT JOIN mi ON mi.body_id = b.id
            WHERE b.id = ANY(@ids) AND b.status <> 'archived' AND (@status::text IS NULL OR b.status = @status)
            ORDER BY b.name_ar
            """, ReportScope.Args(ids, f), r =>
        {
            var completed = r.GetInt64(10);
            return new BodyRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), Pct(r.GetInt64(6), r.GetInt64(7)), r.GetInt64(8),
                r.GetInt64(9), completed, r.GetInt64(11), Pct(r.GetInt64(12), completed), Round(r.IsDBNull(13) ? null : r.GetDouble(13)), r.GetInt64(14));
        }, ct);

    public sealed record ComplianceRow(string AssigneeType, Guid AssigneeId, string Name, long Assigned, long Completed, long CompletedOnTime, long OpenOverdue,
        double? OnTimeRate, double? ComplianceIndex, double? AvgDelayDays);

    /// <summary>التزام الإدارات والجهات الخارجية بالتكليفات المسندة إليها.</summary>
    public static async Task<List<ComplianceRow>> ComplianceAsync(Db db, Guid[] ids, ReportFilter f, CancellationToken ct) =>
        await db.QueryAsync($"""
            WITH t AS (
              SELECT t.*, ws.category,
                     CASE WHEN t.assignee_external_entity_id IS NOT NULL THEN 'external_entity' ELSE 'org_unit' END AS kind,
                     coalesce(t.assignee_external_entity_id, t.assignee_org_unit_id, t.executing_org_unit_id) AS who
              FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
              WHERE t.body_id = ANY(@ids) AND t.status <> 'cancelled'
                AND coalesce(t.assignee_external_entity_id, t.assignee_org_unit_id, t.executing_org_unit_id) IS NOT NULL AND {ReportScope.TaskUnit}
            )
            SELECT kind, who, coalesce(e.name_ar, o.name_ar, '—'),
              count(*) FILTER (WHERE t.assigned_at IS NOT NULL AND {ReportScope.InPeriod("t.assigned_at")}),
              count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")}),
              count(*) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL AND {ReportScope.InPeriod("t.completed_at")} AND t.due_date IS NOT NULL
                               AND (t.completed_at AT TIME ZONE 'Asia/Riyadh')::date <= t.due_date),
              count(*) FILTER (WHERE t.category IN ('open','active') AND t.due_date < {ReportScope.Day}),
              avg(((t.completed_at AT TIME ZONE 'Asia/Riyadh')::date - t.due_date)::float8) FILTER (WHERE t.status = 'completed' AND t.completed_at IS NOT NULL
                  AND {ReportScope.InPeriod("t.completed_at")} AND t.due_date IS NOT NULL AND (t.completed_at AT TIME ZONE 'Asia/Riyadh')::date > t.due_date)
            FROM t LEFT JOIN org.external_entities e ON e.id = t.who AND t.kind = 'external_entity' LEFT JOIN org.org_units o ON o.id = t.who AND t.kind = 'org_unit'
            GROUP BY kind, who, coalesce(e.name_ar, o.name_ar, '—')
            ORDER BY 3
            """, ReportScope.Args(ids, f), r =>
        {
            var completed = r.GetInt64(4);
            var onTime = r.GetInt64(5);
            var overdue = r.GetInt64(6);
            return new ComplianceRow(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetInt64(3), completed, onTime, overdue, Pct(onTime, completed),
                Pct(onTime, completed + overdue), Round(r.IsDBNull(7) ? null : r.GetDouble(7)));
        }, ct);

    public static async Task<IResult> ExecutiveEndpoint(HttpRequest request, Db db, IAccessService access, IClock clock, CancellationToken ct)
    {
        var f = ReportFilter.Parse(request, clock.TodayLocal);
        var ids = await ReportScope.BodiesAsync(db, access, f, ct);
        return Results.Ok(new
        {
            filter = f, asOf = clock.TodayLocal, generatedAt = DateTime.UtcNow, kpis = await ComputeAsync(db, ids, f, ct),
            bodies = await BodiesAsync(db, ids, f, ct), compliance = await ComplianceAsync(db, ids, f, ct), definitions = KpiCatalog.All,
            scopeBodies = ids.Length,
        });
    }
}
