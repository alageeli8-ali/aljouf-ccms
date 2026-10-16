using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;

namespace Ccms.Modules.Tasks;

/// <summary>صف المهمة اللازم للقرارات (الصلاحيات والانتقالات والإشعارات).</summary>
internal sealed record TaskRow(
    Guid Id, Guid BodyId, string BodyCode, string? ReferenceNo, string Title, string Status, string Category, string AssigneeType,
    Guid? AssigneeUserId, Guid? AssigneeOrgUnitId, Guid? AssigneeEntityId, Guid? ExecutorUserId, Guid? OrgUnitManagerId,
    Guid? AccountableUserId, Guid? VerifierUserId, bool VerificationRequired, DateOnly? DueDate, DateOnly? OriginalDueDate,
    int Progress, int EscalationLevel, Guid? ResolutionId, Guid? CreatedBy, string Priority, DateTime? SubmittedAt, Guid? ExecutingOrgUnitId,
    string? AssigneeName);

internal sealed record WorkflowStatus(string Code, string NameAr, string NameEn, string Category, string Color, int SortOrder, bool IsSystem, bool Active);

internal sealed record WorkflowTransition(Guid Id, string Action, string From, string To, string NameAr, string NameEn, string Permission,
    string[] Actors, bool RequiresReason, bool IsSystem, bool Active, int SortOrder);

/// <summary>سياق المستخدم الحالي على المهمة: صلاحياته وصفته (منفذ، متابع، متحقق، مدير).</summary>
internal sealed record TaskContext(TaskRow Row, IReadOnlySet<string> Perms, bool IsExecutor, bool IsFollowUp, bool IsVerifier, bool IsManager, bool PortalOnly)
{
    public bool Has(string permission) => Perms.Contains(permission);

    public bool Matches(WorkflowTransition t) =>
        t.Active && Perms.Contains(t.Permission) && (t.Actors.Length == 0 || t.Actors.Any(a => a switch
        {
            "executor" => IsExecutor,
            "follow_up" => IsFollowUp,
            "verifier" => IsVerifier,
            "manager" => IsManager,
            _ => false,
        }));
}

internal static class TaskData
{
    /// <summary>الفئات التي تُحتسب فيها المهمة متأخرة إن تجاوزت موعدها: مسندة أو مقبولة أو قيد التنفيذ. المتوقفة والمرفوضة وبانتظار المراجعة لا تُحتسب على المنفذ.</summary>
    public static readonly string[] OverdueCategories = ["open", "active"];
    public static readonly string[] OpenCategories = ["open", "active", "waiting", "review"];

    public const string RowSelect = """
        SELECT t.id, t.body_id, b.code AS body_code, t.reference_no, t.title_ar, t.status, ws.category, t.assignee_type, t.assignee_user_id, t.assignee_org_unit_id,
               t.assignee_external_entity_id, t.executor_user_id, o.manager_user_id, t.accountable_user_id, t.verifier_user_id, t.verification_required,
               t.due_date, t.original_due_date, t.progress_pct, t.escalation_level, t.resolution_id, t.created_by, t.priority, t.submitted_at, t.executing_org_unit_id,
               COALESCE(ua.display_name_ar, o.name_ar, e.name_ar) AS assignee_name
        FROM tasks.tasks t JOIN bodies.bodies b ON b.id = t.body_id JOIN tasks.workflow_statuses ws ON ws.code = t.status
        LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id
        LEFT JOIN iam.users ua ON ua.id = t.assignee_user_id
        LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
        """;

    public static TaskRow MapRow(System.Data.Common.DbDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Str("reference_no"), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.Uuid("assignee_user_id"), r.Uuid("assignee_org_unit_id"), r.Uuid("assignee_external_entity_id"), r.Uuid("executor_user_id"), r.Uuid("manager_user_id"),
        r.Uuid("accountable_user_id"), r.Uuid("verifier_user_id"), r.GetBoolean(15), r.Date("due_date"), r.Date("original_due_date"), r.GetInt32(18), r.GetInt32(19),
        r.Uuid("resolution_id"), r.Uuid("created_by"), r.GetString(22), r.Time("submitted_at"), r.Uuid("executing_org_unit_id"), r.Str("assignee_name"));

    public static async Task<TaskRow> LoadAsync(ISql sql, Guid id, bool forUpdate, CancellationToken ct) =>
        await sql.QuerySingleAsync(RowSelect + "\nWHERE t.id = @id" + (forUpdate ? " FOR UPDATE OF t" : ""), new { id }, MapRow, ct) ?? throw new NotFoundException();

    public static async Task<TaskContext> ContextAsync(ISql sql, IAccessService access, ICurrentUser me, Guid id, bool forUpdate, CancellationToken ct)
    {
        var perms = await access.GetPermissionsAsync(Scope.Task(id), ct);
        if (!perms.Contains(Permissions.TaskView)) throw new NotFoundException();
        var row = await LoadAsync(sql, id, forUpdate, ct);
        var portalOnly = await access.IsPortalOnlyAsync(id, ct);
        var executor = row.AssigneeUserId == me.UserId || row.ExecutorUserId == me.UserId || row.OrgUnitManagerId == me.UserId
            || (portalOnly && row.AssigneeEntityId is not null);
        return new TaskContext(row, perms, executor, row.AccountableUserId == me.UserId, row.VerifierUserId == me.UserId,
            perms.Contains(Permissions.TaskManage), portalOnly);
    }

    public static Task<List<WorkflowStatus>> StatusesAsync(ISql sql, CancellationToken ct) =>
        sql.QueryAsync("SELECT code, name_ar, name_en, category, color, sort_order, is_system, active FROM tasks.workflow_statuses ORDER BY sort_order, code", null,
            r => new WorkflowStatus(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetBoolean(6), r.GetBoolean(7)), ct);

    public static Task<List<WorkflowTransition>> TransitionsAsync(ISql sql, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT id, action, from_status, to_status, name_ar, name_en, permission, actors, requires_reason, is_system, active, sort_order
            FROM tasks.workflow_transitions ORDER BY sort_order, action
            """, null, r => new WorkflowTransition(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
            r.GetFieldValue<string[]>(7), r.GetBoolean(8), r.GetBoolean(9), r.GetBoolean(10), r.GetInt32(11)), ct);

    /// <summary>منفذو المهمة الذين يُشعرون: المكلَّف، والمنفذ المسمى، ومدير الإدارة المكلفة، وممثلو الجهة الخارجية حين تكون البوابة مفعلة.</summary>
    public static async Task<List<Guid>> ExecutorUsersAsync(ISql sql, TaskRow t, bool portalEnabled, CancellationToken ct)
    {
        var list = new List<Guid>();
        foreach (var u in new[] { t.AssigneeUserId, t.ExecutorUserId, t.OrgUnitManagerId }) if (u is { } x) list.Add(x);
        if (t.AssigneeEntityId is { } entity && portalEnabled)
            list.AddRange(await sql.QueryAsync("""
                SELECT u.id FROM iam.users u JOIN iam.system_role_assignments a ON a.user_id = u.id AND a.role_code = 'external_representative'
                  AND a.valid_from <= (now() AT TIME ZONE 'Asia/Riyadh')::date AND (a.valid_to IS NULL OR a.valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
                WHERE u.external_entity_id = @entity AND u.status = 'active'
                """, new { entity }, r => r.GetGuid(0), ct));
        return [.. list.Distinct()];
    }

    public static Task LogAsync(ISql tx, Guid taskId, Guid? by, string type, string? note, object? oldValue = null, object? newValue = null, int? progress = null,
        string channel = "system", string visibility = "internal", string? officialChannel = null, string? reference = null, DateOnly? receivedOn = null,
        Guid? recordedForEntity = null, string? delayReason = null, Guid[]? documentIds = null, CancellationToken ct = default) =>
        tx.ExecuteAsync("""
            INSERT INTO tasks.task_updates (id, task_id, author_user_id, update_type, note, old_value, new_value, progress_pct, channel, visibility, official_channel,
              reference, received_on, recorded_for_entity_id, delay_reason, document_ids)
            VALUES (@uid, @taskId, @by, @type, @note, @old::jsonb, @new::jsonb, @progress, @channel, @visibility, @officialChannel, @reference, @receivedOn,
              @recordedForEntity, @delayReason, @documentIds)
            """, new
        {
            uid = Guid.CreateVersion7(), taskId, by, type, note, old = oldValue is null ? null : Json.Serialize(oldValue), @new = newValue is null ? null : Json.Serialize(newValue),
            progress, channel, visibility, officialChannel, reference, receivedOn, recordedForEntity, delayReason, documentIds = documentIds ?? [],
        }, ct);

    public static async Task NotifyAsync(ISql tx, INotifier notifier, IEnumerable<Guid?> users, TaskRow t, string type, string titleAr, string titleEn, string key,
        string severity = "info", Guid? except = null, string? link = null, CancellationToken ct = default)
    {
        foreach (var u in users.OfType<Guid>().Distinct())
        {
            if (u == except) continue;
            await notifier.NotifyAsync(tx, new NotificationRequest(u, type, $"{titleAr}: {t.Title} ({t.ReferenceNo})", $"{titleEn}: {t.ReferenceNo}",
                link ?? $"/tasks/{t.Id}", $"{key}:{t.Id}:{u}", severity), ct);
        }
    }

    public static Task<List<Guid>> BodyRoleUsersAsync(ISql sql, Guid bodyId, string[] roles, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT DISTINCT user_id FROM bodies.body_members WHERE body_id = @bodyId AND status = 'active' AND role = ANY(@roles::text[])
              AND start_date <= (now() AT TIME ZONE 'Asia/Riyadh')::date AND (end_date IS NULL OR end_date >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
            """, new { bodyId, roles }, r => r.GetGuid(0), ct);

    /// <summary>إضافة أيام عمل (الجمعة والسبت عطلة) أو أيام تقويمية حسب قاعدة الجهة.</summary>
    public static DateOnly AddDays(DateOnly d, int days, bool workingDays)
    {
        if (!workingDays || days == 0) return d.AddDays(days);
        var step = Math.Sign(days);
        var left = Math.Abs(days);
        while (left > 0)
        {
            d = d.AddDays(step);
            if (d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday)) left--;
        }
        return d;
    }

    /// <summary>عدد أيام التأخير بعد الاستحقاق (أيام عمل أو تقويمية).</summary>
    public static int DaysBetween(DateOnly from, DateOnly to, bool workingDays)
    {
        if (to <= from) return 0;
        if (!workingDays) return to.DayNumber - from.DayNumber;
        var n = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1)) if (d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday)) n++;
        return n;
    }
}
