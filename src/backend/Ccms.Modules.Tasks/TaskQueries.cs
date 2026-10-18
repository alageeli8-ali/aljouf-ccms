using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Tasks;

/// <summary>قوائم المهام ولوحاتها وبطاقة المهمة. كل الأرقام محسوبة من القاعدة ومقيدة بما يحق للمستخدم الاطلاع عليه.</summary>
internal static class TaskQueries
{
    internal const string Select = """
        SELECT t.*, b.name_ar AS body_name, b.code AS body_code, ws.category, ws.name_ar AS status_name_ar, ws.name_en AS status_name_en, ws.color AS status_color,
               r.number AS resolution_number, r.title_ar AS resolution_title, r.resolution_type, r.status AS resolution_status,
               m.reference_no AS meeting_reference, ai.title_ar AS agenda_item_title, ai.position AS agenda_item_position,
               -- أسماء العرض باستعلامات فرعية لا بربط: 15 ربطًا كانت تكلف المخطط ~150 مث لكل طلب (24 §6)
               (SELECT x.display_name_ar FROM iam.users x WHERE x.id = t.assignee_user_id) AS assignee_user_name,
               (SELECT x.name_ar FROM org.org_units x WHERE x.id = t.assignee_org_unit_id) AS assignee_org_unit_name,
               (SELECT x.name_ar FROM org.external_entities x WHERE x.id = t.assignee_external_entity_id) AS assignee_entity_name,
               (SELECT x.display_name_ar FROM iam.users x WHERE x.id = t.executor_user_id) AS executor_name,
               (SELECT x.display_name_ar FROM org.org_units o JOIN iam.users x ON x.id = o.manager_user_id WHERE o.id = t.assignee_org_unit_id) AS unit_manager_name,
               (SELECT x.name_ar FROM org.org_units x WHERE x.id = t.executing_org_unit_id) AS executing_unit_name,
               (SELECT x.display_name_ar FROM iam.users x WHERE x.id = t.accountable_user_id) AS accountable_name,
               (SELECT x.display_name_ar FROM iam.users x WHERE x.id = t.verifier_user_id) AS verifier_name,
               (SELECT x.display_name_ar FROM iam.users x WHERE x.id = t.verified_by) AS verified_by_name,
               (ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date) AS is_overdue,
               CASE WHEN ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date
                    THEN (now() AT TIME ZONE 'Asia/Riyadh')::date - t.due_date END AS days_overdue,
               EXISTS (SELECT 1 FROM tasks.extension_requests x WHERE x.task_id = t.id AND x.status = 'pending') AS pending_extension,
               (SELECT count(*) FROM tasks.task_deliverables d WHERE d.task_id = t.id AND d.status <> 'removed') AS deliverables_total,
               (SELECT count(*) FROM tasks.task_deliverables d WHERE d.task_id = t.id AND d.status = 'accepted') AS deliverables_accepted
        FROM tasks.tasks t
        JOIN bodies.bodies b ON b.id = t.body_id
        JOIN tasks.workflow_statuses ws ON ws.code = t.status
        LEFT JOIN resolutions.resolutions r ON r.id = t.resolution_id
        LEFT JOIN meetings.meetings m ON m.id = t.meeting_id
        LEFT JOIN meetings.agenda_items ai ON ai.id = t.agenda_item_id
        """;

    internal static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), referenceNo = r.Str("reference_no"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
        resolutionId = r.Uuid("resolution_id"), resolutionNumber = r.Str("resolution_number"), resolutionTitle = r.Str("resolution_title"),
        resolutionType = r.Str("resolution_type"), resolutionStatus = r.Str("resolution_status"),
        meetingId = r.Uuid("meeting_id"), meetingReference = r.Str("meeting_reference"),
        agendaItemId = r.Uuid("agenda_item_id"), agendaItemTitle = r.Str("agenda_item_title"),
        sourceType = r.Uuid("resolution_id") is not null ? (r.Str("resolution_type") ?? "decision") : r.Uuid("agenda_item_id") is not null ? "agenda_item" : "manual",
        titleAr = r.Get<string>("title_ar"), description = r.Str("description"), assigneeType = r.Get<string>("assignee_type"),
        assigneeUserId = r.Uuid("assignee_user_id"), assigneeOrgUnitId = r.Uuid("assignee_org_unit_id"), assigneeExternalEntityId = r.Uuid("assignee_external_entity_id"),
        assigneeName = r.Str("assignee_user_name") ?? r.Str("assignee_org_unit_name") ?? r.Str("assignee_entity_name"),
        executorUserId = r.Uuid("executor_user_id"), executorName = r.Str("executor_name") ?? r.Str("unit_manager_name") ?? r.Str("executor_contact") ?? r.Str("assignee_user_name"),
        executorContact = r.Str("executor_contact"), executingOrgUnitId = r.Uuid("executing_org_unit_id"),
        executingUnitName = r.Str("assignee_entity_name") ?? r.Str("executing_unit_name") ?? r.Str("assignee_org_unit_name"),
        accountableUserId = r.Uuid("accountable_user_id"), accountableName = r.Str("accountable_name"),
        verifierUserId = r.Uuid("verifier_user_id"), verifierName = r.Str("verifier_name"), verificationRequired = r.Get<bool>("verification_required"),
        priority = r.Get<string>("priority"), dueDate = r.Date("due_date"), originalDueDate = r.Date("original_due_date"),
        status = r.Get<string>("status"), statusCategory = r.Get<string>("category"), statusNameAr = r.Get<string>("status_name_ar"),
        statusNameEn = r.Get<string>("status_name_en"), statusColor = r.Get<string>("status_color"), statusReason = r.Str("status_reason"),
        progressPct = r.Get<int>("progress_pct"), escalationLevel = r.Get<int>("escalation_level"),
        isOverdue = r.Get<bool>("is_overdue"), daysOverdue = r.IsDBNull(r.GetOrdinal("days_overdue")) ? (int?)null : r.Get<int>("days_overdue"),
        pendingExtension = r.Get<bool>("pending_extension"), deliverablesTotal = r.Get<long>("deliverables_total"), deliverablesAccepted = r.Get<long>("deliverables_accepted"),
        acceptanceCriteria = r.Str("acceptance_criteria"), externalBrief = r.Str("external_brief"), shareResolutionWithEntity = r.Get<bool>("share_resolution_with_entity"),
        delayReason = r.Str("delay_reason"), correspondenceNo = r.Str("correspondence_no"), correspondenceDate = r.Date("correspondence_date"),
        assignedAt = r.Time("assigned_at"), acceptedAt = r.Time("accepted_at"), startedAt = r.Time("started_at"), submittedAt = r.Time("submitted_at"),
        completedAt = r.Time("completed_at"), verifiedAt = r.Time("verified_at"), verifiedByName = r.Str("verified_by_name"), completionMethod = r.Str("completion_method"),
        cancelledAt = r.Time("cancelled_at"), createdAt = r.Get<DateTime>("created_at"), updatedAt = r.Get<DateTime>("updated_at"),
    };

    /// <summary>مرشحات القوائم واللوحات. «متأخرة» ليست حالة مخزنة: مسندة أو مقبولة أو قيد التنفيذ تجاوزت موعدها.</summary>
    internal sealed record Filters(Guid? BodyId, string? Status, string? Priority, string? AssigneeType, Guid? AssigneeUserId, Guid? OrgUnitId, Guid? ExternalEntityId,
        Guid? AccountableUserId, Guid? ResolutionId, DateOnly? DueFrom, DateOnly? DueTo, string? Q, bool? Mine, bool? Escalated);

    private static async Task<(string Where, Dictionary<string, object?> Args)> WhereAsync(Filters f, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var filter = await access.GetBodyFilterAsync(Permissions.TaskView, ct);
        var args = new Dictionary<string, object?>
        {
            ["all"] = filter.All, ["ids"] = filter.BodyIds, ["me"] = me.UserId, ["bodyId"] = f.BodyId, ["status"] = f.Status is "overdue" ? null : f.Status,
            ["overdue"] = f.Status == "overdue", ["priority"] = f.Priority, ["atype"] = f.AssigneeType == "user" ? null : f.AssigneeType,
            ["auser"] = f.AssigneeUserId, ["unit"] = f.OrgUnitId, ["entity"] = f.ExternalEntityId, ["acc"] = f.AccountableUserId, ["rid"] = f.ResolutionId,
            ["dueFrom"] = f.DueFrom, ["dueTo"] = f.DueTo, ["q"] = SqlText.Like(f.Q), ["mine"] = f.Mine ?? false, ["escalated"] = f.Escalated ?? false,
        };
        const string where = """
            WHERE (@all::boolean OR t.body_id = ANY(@ids::uuid[]) OR t.assignee_user_id = @me OR t.executor_user_id = @me OR t.accountable_user_id = @me
                   OR t.verifier_user_id = @me OR t.assignee_org_unit_id IN (SELECT id FROM org.org_units WHERE manager_user_id = @me)
                   OR t.executing_org_unit_id IN (SELECT id FROM org.org_units WHERE manager_user_id = @me))
              AND (@bodyId::uuid IS NULL OR t.body_id = @bodyId)
              AND (@status::text IS NULL OR t.status = @status)
              AND (NOT @overdue::boolean OR (ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date))
              AND (@priority::text IS NULL OR t.priority = @priority)
              AND (@atype::text IS NULL OR t.assignee_type = @atype)
              AND (@auser::uuid IS NULL OR t.assignee_user_id = @auser OR t.executor_user_id = @auser)
              AND (@unit::uuid IS NULL OR t.assignee_org_unit_id = @unit OR t.executing_org_unit_id = @unit)
              AND (@entity::uuid IS NULL OR t.assignee_external_entity_id = @entity)
              AND (@acc::uuid IS NULL OR t.accountable_user_id = @acc)
              AND (@rid::uuid IS NULL OR t.resolution_id = @rid)
              AND (@dueFrom::date IS NULL OR t.due_date >= @dueFrom) AND (@dueTo::date IS NULL OR t.due_date <= @dueTo)
              AND (@q::text IS NULL OR t.title_ar ILIKE @q OR t.reference_no ILIKE @q)
              AND (NOT @mine::boolean OR t.assignee_user_id = @me OR t.executor_user_id = @me OR t.accountable_user_id = @me OR t.verifier_user_id = @me
                   OR t.assignee_org_unit_id IN (SELECT id FROM org.org_units WHERE manager_user_id = @me))
              AND (NOT @escalated::boolean OR (t.escalation_level > 0 AND ws.category IN ('open','active','waiting','review')))
            """;
        return (where, args);
    }

    private static Filters F(Guid? bodyId, string? status, string? priority, string? assigneeType, Guid? assigneeUserId, Guid? orgUnitId, Guid? externalEntityId,
        Guid? accountableUserId, Guid? resolutionId, DateOnly? dueFrom, DateOnly? dueTo, string? q, bool? mine, bool? escalated) =>
        new(bodyId, status, priority, assigneeType, assigneeUserId, orgUnitId, externalEntityId, accountableUserId, resolutionId, dueFrom, dueTo, q, mine, escalated);

    public static async Task<IResult> List(Guid? bodyId, string? status, string? priority, string? assigneeType, Guid? assigneeUserId, Guid? orgUnitId,
        Guid? externalEntityId, Guid? accountableUserId, Guid? resolutionId, DateOnly? dueFrom, DateOnly? dueTo, string? q, bool? mine, bool? escalated, bool? overdue,
        string? sort, int? page, int? pageSize, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var (where, args) = await WhereAsync(F(bodyId, overdue == true ? "overdue" : status, priority, assigneeType, assigneeUserId, orgUnitId, externalEntityId,
            accountableUserId, resolutionId, dueFrom, dueTo, q, mine, escalated), access, me, ct);
        var p = PageQuery.From(page, pageSize);
        args["limit"] = p.PageSize;
        args["offset"] = p.Offset;
        var order = sort switch
        {
            "priority" => "CASE t.priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END, t.due_date NULLS LAST",
            "updated" => "t.updated_at DESC",
            "progress" => "t.progress_pct, t.due_date NULLS LAST",
            _ => "t.due_date NULLS LAST, t.created_at DESC",
        };
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + $" ORDER BY {order} LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    public static async Task<IResult> My(Db db, ICurrentUser me, IAccessService access, CancellationToken ct)
    {
        var (where, args) = await WhereAsync(F(null, null, null, null, null, null, null, null, null, null, null, null, true, null), access, me, ct);
        return Results.Ok(await db.QueryAsync(Select + "\n" + where + " AND ws.category IN ('open','active','waiting','review') ORDER BY t.due_date NULLS LAST", args, Map, ct));
    }

    /// <summary>لوحة كانبان: المهام مجمعة حسب الحالة بترتيب الحالات المهيأة.</summary>
    public static async Task<IResult> Board(Guid? bodyId, string? priority, string? assigneeType, Guid? orgUnitId, Guid? externalEntityId, bool? mine,
        Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var (where, args) = await WhereAsync(F(bodyId, null, priority, assigneeType, null, orgUnitId, externalEntityId, null, null, null, null, null, mine, null), access, me, ct);
        var statuses = await TaskData.StatusesAsync(db, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " AND (ws.category NOT IN ('done','closed') OR t.updated_at > now() - interval '30 days') ORDER BY t.due_date NULLS LAST LIMIT 500",
            args, r => (Status: r.Get<string>("status"), Item: Map(r)), ct);
        return Results.Ok(statuses.Where(s => s.Active && s.Category != "derived").Select(s => new
        {
            status = s.Code, nameAr = s.NameAr, nameEn = s.NameEn, category = s.Category, color = s.Color,
            items = items.Where(i => i.Status == s.Code).Select(i => i.Item).ToList(),
        }));
    }

    /// <summary>لوحة المهام: المؤشرات والتوزيعات حسب الحالة والأولوية والمجلس والجهة المنفذة والمنفذ وموعد الاستحقاق — كلها من البيانات الفعلية.</summary>
    public static async Task<IResult> Dashboard(Guid? bodyId, string? priority, string? assigneeType, Guid? orgUnitId, Guid? externalEntityId, Guid? assigneeUserId,
        DateOnly? dueFrom, DateOnly? dueTo, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var (where, args) = await WhereAsync(F(bodyId, null, priority, assigneeType, assigneeUserId, orgUnitId, externalEntityId, null, null, dueFrom, dueTo, null, null, null),
            access, me, ct);
        const string today = "(now() AT TIME ZONE 'Asia/Riyadh')::date";
        var baseCte = $"""
            WITH v AS (
              SELECT t.*, ws.category, ws.name_ar AS status_name_ar, ws.name_en AS status_name_en, ws.color, ws.sort_order,
                     (ws.category IN ('open','active') AND t.due_date < {today}) AS overdue, b.name_ar AS body_name,
                     CASE t.assignee_type WHEN 'external_entity' THEN e.name_ar WHEN 'org_unit' THEN o.name_ar ELSE ua.display_name_ar END AS executor_name,
                     COALESCE(t.assignee_external_entity_id, t.assignee_org_unit_id, t.assignee_user_id) AS executor_id
              FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status JOIN bodies.bodies b ON b.id = t.body_id
              LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
              LEFT JOIN iam.users ua ON ua.id = t.assignee_user_id
              {where} AND t.status <> 'new')
            """;
        var kq = await db.QuerySingleAsync(baseCte + $"""
            SELECT count(*) FILTER (WHERE category <> 'closed') AS total,
                   count(*) FILTER (WHERE category IN ('open','active','waiting','review')) AS open,
                   count(*) FILTER (WHERE category = 'done') AS completed,
                   count(*) FILTER (WHERE category = 'closed') AS cancelled,
                   count(*) FILTER (WHERE overdue) AS overdue,
                   count(*) FILTER (WHERE category IN ('open','active') AND due_date BETWEEN {today} AND {today} + 7) AS due_soon,
                   count(*) FILTER (WHERE category = 'review') AS pending_review,
                   count(*) FILTER (WHERE status = 'on_hold') AS on_hold,
                   count(*) FILTER (WHERE status = 'rejected') AS rejected,
                   count(*) FILTER (WHERE category = 'done' AND (due_date IS NULL OR (completed_at AT TIME ZONE 'Asia/Riyadh')::date <= due_date)) AS on_time,
                   COALESCE(round(avg(progress_pct) FILTER (WHERE category IN ('open','active','waiting','review'))), 0)::int AS avg_progress,
                   count(*) FILTER (WHERE escalation_level > 0 AND category IN ('open','active','waiting','review')) AS escalated,
                   (SELECT count(*) FROM tasks.extension_requests x WHERE x.status = 'pending' AND x.task_id IN (SELECT id FROM v)) AS pending_extensions
            FROM v
            """, args, r => new
        {
            total = r.GetInt64(0), open = r.GetInt64(1), completed = r.GetInt64(2), cancelled = r.GetInt64(3), overdue = r.GetInt64(4), dueSoon = r.GetInt64(5),
            pendingReview = r.GetInt64(6), onHold = r.GetInt64(7), rejected = r.GetInt64(8), completedOnTime = r.GetInt64(9), avgProgress = r.GetInt32(10),
            escalated = r.GetInt64(11), pendingExtensions = r.GetInt64(12),
        }, ct);
        var k = kq!;
        var completionRate = k.total == 0 ? 0 : Math.Round(100.0 * k.completed / k.total, 1);
        var onTimeRate = k.completed == 0 ? 0 : Math.Round(100.0 * k.completedOnTime / k.completed, 1);
        var overdueRate = k.open == 0 ? 0 : Math.Round(100.0 * k.overdue / k.open, 1);

        var byStatus = await db.QueryAsync(baseCte + "SELECT status, status_name_ar, status_name_en, color, category, count(*) FROM v GROUP BY 1,2,3,4,5, sort_order ORDER BY sort_order",
            args, r => new { status = r.GetString(0), nameAr = r.GetString(1), nameEn = r.GetString(2), color = r.GetString(3), category = r.GetString(4), count = r.GetInt64(5) }, ct);
        var overdueTile = new { status = "overdue", nameAr = "متأخرة", nameEn = "Overdue", color = "red", category = "derived", count = k.overdue };
        const string groupCols = """
            count(*) FILTER (WHERE category <> 'closed') AS total, count(*) FILTER (WHERE category IN ('open','active','waiting','review')) AS open,
            count(*) FILTER (WHERE category = 'done') AS completed, count(*) FILTER (WHERE overdue) AS overdue,
            COALESCE(round(avg(progress_pct) FILTER (WHERE category IN ('open','active','waiting','review'))), 0)::int AS avg_progress
            """;
        object Group(System.Data.Common.DbDataReader r, int keyCount)
        {
            var total = r.GetInt64(keyCount);
            var completed = r.GetInt64(keyCount + 2);
            return new
            {
                key = r.IsDBNull(0) ? null : r.GetValue(0).ToString(), name = keyCount > 1 && !r.IsDBNull(1) ? r.GetString(1) : null,
                kind = keyCount > 2 && !r.IsDBNull(2) ? r.GetString(2) : null,
                total, open = r.GetInt64(keyCount + 1), completed, overdue = r.GetInt64(keyCount + 3), avgProgress = r.GetInt32(keyCount + 4),
                completionRate = total == 0 ? 0 : Math.Round(100.0 * completed / total, 1),
            };
        }
        var byPriority = await db.QueryAsync(baseCte + $"SELECT priority, {groupCols} FROM v GROUP BY priority ORDER BY CASE priority WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END",
            args, r => Group(r, 1), ct);
        var byBody = await db.QueryAsync(baseCte + $"SELECT body_id, body_name, {groupCols} FROM v GROUP BY body_id, body_name ORDER BY count(*) DESC", args, r => Group(r, 2), ct);
        var byExecutor = await db.QueryAsync(baseCte + $"SELECT executor_id, executor_name, assignee_type, {groupCols} FROM v GROUP BY 1,2,3 ORDER BY count(*) FILTER (WHERE overdue) DESC, count(*) DESC LIMIT 30",
            args, r => Group(r, 3), ct);
        var byDue = await db.QuerySingleAsync(baseCte + $"""
            SELECT count(*) FILTER (WHERE overdue),
                   count(*) FILTER (WHERE category IN ('open','active','waiting','review') AND due_date BETWEEN {today} AND {today} + 7 AND NOT overdue),
                   count(*) FILTER (WHERE category IN ('open','active','waiting','review') AND due_date BETWEEN {today} + 8 AND {today} + 30),
                   count(*) FILTER (WHERE category IN ('open','active','waiting','review') AND due_date > {today} + 30),
                   count(*) FILTER (WHERE category IN ('open','active','waiting','review') AND due_date IS NULL),
                   count(*) FILTER (WHERE category IN ('waiting','review') AND due_date < {today})
            FROM v
            """, args, r => new[]
        {
            new { bucket = "overdue", count = r.GetInt64(0) }, new { bucket = "next_7_days", count = r.GetInt64(1) }, new { bucket = "next_30_days", count = r.GetInt64(2) },
            new { bucket = "later", count = r.GetInt64(3) }, new { bucket = "no_due_date", count = r.GetInt64(4) }, new { bucket = "past_due_waiting", count = r.GetInt64(5) },
        }, ct);
        var topOverdue = await db.QueryAsync(baseCte + $"""
            SELECT id, reference_no, title_ar, executor_name, due_date, {today} - due_date AS days, escalation_level, body_name, priority FROM v
            WHERE overdue ORDER BY due_date, priority LIMIT 10
            """, args, r => new
        {
            id = r.GetGuid(0), referenceNo = r.Str("reference_no"), titleAr = r.GetString(2), executorName = r.Str("executor_name"), dueDate = r.Date("due_date"),
            daysOverdue = r.GetInt32(5), escalationLevel = r.GetInt32(6), bodyName = r.GetString(7), priority = r.GetString(8),
        }, ct);
        // قائمة «تصعيد للإدارة العليا» (08 §4.3): المستوى الثالث فأعلى
        var executive = await db.QueryAsync(baseCte + """
            SELECT v.id, v.reference_no, v.title_ar, v.executor_name, v.due_date, v.escalation_level, v.body_name,
                   (SELECT max(created_at) FROM tasks.escalations x WHERE x.task_id = v.id) AS last_escalated
            FROM v WHERE v.escalation_level >= 3 AND v.category IN ('open','active','waiting','review') ORDER BY v.escalation_level DESC, v.due_date
            """, args, r => new
        {
            id = r.GetGuid(0), referenceNo = r.Str("reference_no"), titleAr = r.GetString(2), executorName = r.Str("executor_name"), dueDate = r.Date("due_date"),
            escalationLevel = r.GetInt32(5), bodyName = r.GetString(6), lastEscalated = r.Time("last_escalated"),
        }, ct);
        return Results.Ok(new
        {
            kpis = new
            {
                k.total, k.open, k.completed, k.cancelled, k.overdue, k.dueSoon, k.pendingReview, k.onHold, k.rejected, k.completedOnTime, k.avgProgress,
                k.escalated, k.pendingExtensions, completionRate, onTimeRate, overdueRate,
            },
            byStatus = byStatus.Cast<object>().Append(overdueTile), byPriority, byBody, byExecutor, byDue, topOverdue, executive,
            generatedAt = DateTime.UtcNow,
        });
    }

    // ---------------- بطاقة المهمة ----------------

    public static async Task<IResult> Get(Guid id, Db db, IAccessService access, ICurrentUser me, IFeatureService features, CancellationToken ct)
    {
        var ctx = await TaskData.ContextAsync(db, access, me, id, false, ct);
        // ممثل الجهة الخارجية يرى البطاقة المقيدة في البوابة فقط
        if (ctx.PortalOnly) throw new NotFoundException();
        var task = await db.QuerySingleAsync(Select + " WHERE t.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var transitions = await TaskData.TransitionsAsync(db, ct);
        var actions = transitions.Where(t => t.From == ctx.Row.Status && ctx.Matches(t))
            .Where(t => t.Action is not ("verify" or "return") || !ctx.IsExecutor)
            .Where(t => t.Action != "complete" || !ctx.Row.VerificationRequired)
            .Select(t => new { action = t.Action, to = t.To, nameAr = t.NameAr, nameEn = t.NameEn, requiresReason = t.RequiresReason }).ToList();
        var portal = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        return Results.Ok(new
        {
            task,
            deliverables = await DeliverablesAsync(db, id, false, ct),
            updates = await UpdatesAsync(db, id, false, ct),
            extensions = await ExtensionsAsync(db, id, ct),
            escalations = await db.QueryAsync("""
                SELECT x.id, x.kind, x.level, x.trigger, x.reason, x.recipients::text, x.created_at, x.days_overdue, u.display_name_ar, x.acknowledged_at, ua.display_name_ar, x.ack_note
                FROM tasks.escalations x LEFT JOIN iam.users u ON u.id = x.created_by LEFT JOIN iam.users ua ON ua.id = x.acknowledged_by
                WHERE x.task_id = @id ORDER BY x.created_at DESC
                """, new { id }, r => new
            {
                id = r.GetGuid(0), kind = r.GetString(1), level = r.GetInt32(2), trigger = r.GetString(3), reason = r.Str("reason"),
                recipients = JsonSerializer.Deserialize<JsonElement>(r.GetString(5)), createdAt = r.GetDateTime(6),
                daysOverdue = r.IsDBNull(7) ? (int?)null : r.GetInt32(7), createdBy = r.IsDBNull(8) ? null : r.GetString(8),
                acknowledgedAt = r.Time("acknowledged_at"), acknowledgedBy = r.IsDBNull(10) ? null : r.GetString(10), ackNote = r.Str("ack_note"),
                canAcknowledge = r.Time("acknowledged_at") is null && r.GetString(5).Contains(me.UserId.ToString(), StringComparison.OrdinalIgnoreCase),
            }, ct),
            reviews = await db.QueryAsync("""
                SELECT rv.id, rv.decision, rv.note, rv.deliverable_decisions::text, u.display_name_ar, rv.created_at FROM tasks.task_reviews rv
                JOIN iam.users u ON u.id = rv.reviewer_user_id WHERE rv.task_id = @id ORDER BY rv.created_at DESC
                """, new { id }, r => new
            {
                id = r.GetGuid(0), decision = r.GetString(1), note = r.Str("note"), deliverableDecisions = JsonSerializer.Deserialize<JsonElement>(r.GetString(3)),
                reviewer = r.GetString(4), createdAt = r.GetDateTime(5),
            }, ct),
            letters = await db.QueryAsync("""
                SELECT id, reference_no, subject, status, outgoing_no, outgoing_date, created_at FROM tasks.assignment_letters WHERE @id = ANY(task_ids) ORDER BY created_at DESC
                """, new { id }, r => new
            {
                id = r.GetGuid(0), referenceNo = r.GetString(1), subject = r.GetString(2), status = r.GetString(3), outgoingNo = r.Str("outgoing_no"),
                outgoingDate = r.Date("outgoing_date"), createdAt = r.GetDateTime(6),
            }, ct),
            actions,
            capabilities = new
            {
                canManage = ctx.IsManager,
                canProgress = ctx.Has(Permissions.TaskUpdate) && (ctx.IsExecutor || ctx.IsFollowUp || ctx.IsManager) && ctx.Row.Category is "open" or "active",
                canSubmitDeliverables = ctx.Has(Permissions.TaskUpdate) && (ctx.IsExecutor || ctx.IsFollowUp) && ctx.Row.Category is "open" or "active",
                canComment = ctx.Has(Permissions.TaskComment),
                canRequestExtension = ctx.Has(Permissions.TaskUpdate) && (ctx.IsExecutor || ctx.IsFollowUp) && ctx.Row.Category is not ("done" or "closed" or "new"),
                canDecideExtension = ctx.Has(Permissions.TaskExtensionApprove),
                canRecordExternal = ctx.Row.AssigneeEntityId is not null && ctx.Has(Permissions.TaskRecordExternal) && (ctx.IsFollowUp || ctx.IsManager),
                canEscalate = ctx.Has(Permissions.TaskEscalate) && ctx.Row.Category is not ("done" or "closed"),
                canLetters = ctx.Has(Permissions.TaskLettersManage),
                canShareDocuments = ctx.Row.AssigneeEntityId is not null && (ctx.IsManager || ctx.Has(Permissions.TaskRecordExternal)),
                isExecutor = ctx.IsExecutor, isFollowUp = ctx.IsFollowUp, isVerifier = ctx.IsVerifier,
                portalEnabled = portal,
            },
            permissions = ctx.Perms.Order(),
        });
    }

    internal static async Task<List<object>> DeliverablesAsync(ISql db, Guid taskId, bool portal, CancellationToken ct)
    {
        var subs = await db.QueryAsync("""
            SELECT s.id, s.deliverable_id, s.document_ids, s.note, s.channel, s.created_at, u.display_name_ar FROM tasks.deliverable_submissions s
            JOIN iam.users u ON u.id = s.submitted_by WHERE s.task_id = @taskId ORDER BY s.created_at DESC
            """, new { taskId }, r => (Deliverable: r.GetGuid(1), Item: new
        {
            id = r.GetGuid(0), documentIds = r.GetFieldValue<Guid[]>(2), note = r.Str("note"), channel = r.GetString(4), createdAt = r.GetDateTime(5), submittedBy = r.GetString(6),
        }), ct);
        var docIds = subs.SelectMany(s => s.Item.documentIds).Distinct().ToArray();
        var docs = (await db.QueryAsync("SELECT id, file_name, sha256 FROM docs.documents WHERE id = ANY(@docIds::uuid[])", new { docIds },
            r => new { id = r.GetGuid(0), fileName = r.GetString(1), sha256 = r.GetString(2) }, ct)).ToDictionary(d => d.id);
        return (await db.QueryAsync("""
            SELECT d.id, d.position, d.title, d.description, d.acceptance_criteria, d.required, d.status, d.review_note, d.reviewed_at, u.display_name_ar
            FROM tasks.task_deliverables d LEFT JOIN iam.users u ON u.id = d.reviewed_by WHERE d.task_id = @taskId AND d.status <> 'removed' ORDER BY d.position
            """, new { taskId }, r => (Id: r.GetGuid(0), Row: (Position: r.GetInt32(1), Title: r.GetString(2), Description: r.Str("description"),
            Criteria: r.Str("acceptance_criteria"), Required: r.GetBoolean(5), Status: r.GetString(6), ReviewNote: r.Str("review_note"),
            ReviewedAt: r.Time("reviewed_at"), ReviewedBy: r.IsDBNull(9) ? null : r.GetString(9))), ct))
            .Select(d => (object)new
            {
                id = d.Id, position = d.Row.Position, title = d.Row.Title, description = d.Row.Description, acceptanceCriteria = d.Row.Criteria,
                required = d.Row.Required, status = d.Row.Status, reviewNote = d.Row.ReviewNote, reviewedAt = d.Row.ReviewedAt, reviewedBy = portal ? null : d.Row.ReviewedBy,
                submissions = subs.Where(s => s.Deliverable == d.Id).Select(s => new
                {
                    s.Item.id, s.Item.note, s.Item.channel, s.Item.createdAt, s.Item.submittedBy,
                    documents = s.Item.documentIds.Where(docs.ContainsKey).Select(x => docs[x]).ToList(),
                }).ToList(),
            }).ToList();
    }

    internal static Task<List<object>> UpdatesAsync(ISql db, Guid taskId, bool sharedOnly, CancellationToken ct) =>
        db.QueryAsync("""
            SELECT tu.id, tu.update_type, tu.progress_pct, tu.note, tu.created_at, u.display_name_ar, tu.channel, tu.visibility, tu.official_channel, tu.reference,
                   tu.received_on, tu.new_value::text, tu.old_value::text, tu.delay_reason, e.name_ar AS entity_name, tu.document_ids, u.user_type
            FROM tasks.task_updates tu LEFT JOIN iam.users u ON u.id = tu.author_user_id LEFT JOIN org.external_entities e ON e.id = tu.recorded_for_entity_id
            WHERE tu.task_id = @taskId AND (NOT @sharedOnly::boolean OR tu.visibility = 'shared') ORDER BY tu.created_at DESC, tu.id DESC
            """, new { taskId, sharedOnly }, r => (object)new
        {
            id = r.GetGuid(0), updateType = r.GetString(1), progressPct = r.IsDBNull(2) ? (int?)null : r.GetInt32(2), note = r.Str("note"),
            createdAt = r.GetDateTime(4), author = r.Str("display_name_ar"), authorIsExternal = r.Str("user_type") == "external",
            channel = r.GetString(6), visibility = r.GetString(7), officialChannel = r.Str("official_channel"), reference = r.Str("reference"), receivedOn = r.Date("received_on"),
            newValue = r.IsDBNull(11) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.GetString(11)),
            oldValue = r.IsDBNull(12) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.GetString(12)),
            delayReason = r.Str("delay_reason"), recordedForEntity = r.Str("entity_name"), documentIds = r.GetFieldValue<Guid[]>(15),
        }, ct);

    internal static Task<List<object>> ExtensionsAsync(ISql db, Guid taskId, CancellationToken ct) =>
        db.QueryAsync("""
            SELECT x.id, x.status, x.current_due_date, x.requested_due_date, x.approved_due_date, x.delay_cause, x.reason, x.channel, x.reference, x.created_at,
                   ur.display_name_ar, x.decided_at, ud.display_name_ar, x.decision_note
            FROM tasks.extension_requests x JOIN iam.users ur ON ur.id = x.requested_by LEFT JOIN iam.users ud ON ud.id = x.decided_by
            WHERE x.task_id = @taskId ORDER BY x.created_at DESC
            """, new { taskId }, r => (object)new
        {
            id = r.GetGuid(0), status = r.GetString(1), currentDueDate = r.Date("current_due_date"), requestedDueDate = r.Date("requested_due_date"),
            approvedDueDate = r.Date("approved_due_date"), delayCause = r.GetString(5), reason = r.GetString(6), channel = r.GetString(7), reference = r.Str("reference"),
            createdAt = r.GetDateTime(9), requestedBy = r.GetString(10), decidedAt = r.Time("decided_at"), decidedBy = r.IsDBNull(12) ? null : r.GetString(12),
            decisionNote = r.Str("decision_note"),
        }, ct);
}
