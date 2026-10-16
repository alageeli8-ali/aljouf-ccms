using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Tasks;

/// <summary>بوابة الجهات الخارجية (EXTERNAL_PORTAL_ENABLED): ممثل الجهة يرى تكليفات جهته فقط ببطاقة مقيدة —
/// لا محاضر ولا قرارات (إلا ما شورك من نص القرار صراحة) ولا مستندات غير مشاركة ولا ملاحظات داخلية.
/// الخاصية تُفحص على الخادم في كل طلب؛ وتعطيلها لا يمس إدارة المهام الداخلية.</summary>
internal static class Portal
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/portal").WithTags("External portal");
        g.MapGet("/me", Me);
        g.MapGet("/tasks", List);
        g.MapGet("/tasks/{id:guid}", Get);
        g.MapPost("/tasks/{id:guid}/actions/{action}", Act).Validate<ActionRequest>();
        g.MapPost("/tasks/{id:guid}/progress", Progress).Validate<TasksModule.ProgressRequest>();
        g.MapPost("/tasks/{id:guid}/comments", Comment).Validate<TasksModule.CommentRequest>().Idempotent();
        g.MapPost("/tasks/{id:guid}/extension-requests", Extension).Validate<TasksModule.ExtensionRequest>().Idempotent();
        g.MapPost("/deliverables/{did:guid}/submissions", Submit).Validate<TasksModule.SubmissionRequest>().Idempotent();
    }

    /// <summary>الإجراءات المتاحة للجهة عبر البوابة؛ ما سواها (التحقق، الإلغاء، الإيقاف...) داخلي.</summary>
    private static readonly string[] PortalActions = ["accept", "reject", "start", "submit"];

    /// <summary>يتحقق من: تفعيل البوابة، ودور ممثل الجهة، وربط الحساب بجهة، وتغيير كلمة المرور المؤقتة.</summary>
    private static async Task<Guid> EntityAsync(Db db, ICurrentUser me, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        if (!(await access.GetSystemRolesAsync(me.UserId, ct)).Contains(Roles.ExternalRepresentative))
            throw new ForbiddenException(permission: Permissions.PortalAccess);
        var u = await db.QuerySingleAsync("""
            SELECT u.external_entity_id, COALESCE(c.must_change, false), e.status FROM iam.users u LEFT JOIN iam.local_credentials c ON c.user_id = u.id
            LEFT JOIN org.external_entities e ON e.id = u.external_entity_id WHERE u.id = @id
            """, new { id = me.UserId }, r => (Entity: r.Uuid("external_entity_id"), MustChange: r.GetBoolean(1), Status: r.Str("status")), ct);
        if (u.Entity is null || u.Status != "active") throw new AccessDeniedException("no-entity", "حسابك غير مرتبط بجهة خارجية نشطة");
        if (u.MustChange) throw new AccessDeniedException("password-change-required", "غيّر كلمة المرور المؤقتة قبل الوصول إلى التكليفات");
        return u.Entity.Value;
    }

    private static async Task<TaskContext> TaskAsync(ISql sql, Guid id, Guid entity, IAccessService access, ICurrentUser me, bool forUpdate, CancellationToken ct)
    {
        var ctx = await TaskData.ContextAsync(sql, access, me, id, forUpdate, ct);
        if (ctx.Row.AssigneeEntityId != entity || ctx.Row.Status == "new") throw new NotFoundException();
        return ctx;
    }

    private static async Task<IResult> Me(Db db, ICurrentUser me, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var info = await db.QuerySingleAsync("""
            SELECT e.name_ar, e.name_en,
              count(t.id) FILTER (WHERE ws.category IN ('open','active','waiting','review')),
              count(t.id) FILTER (WHERE t.status = 'assigned'),
              count(t.id) FILTER (WHERE ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date),
              count(t.id) FILTER (WHERE ws.category = 'done')
            FROM org.external_entities e LEFT JOIN tasks.tasks t ON t.assignee_external_entity_id = e.id AND t.status <> 'new'
            LEFT JOIN tasks.workflow_statuses ws ON ws.code = t.status
            WHERE e.id = @entity GROUP BY e.name_ar, e.name_en
            """, new { entity }, r => new
        {
            entityId = entity, entityNameAr = r.GetString(0), entityNameEn = r.Str("name_en"), open = r.GetInt64(2), awaitingAcknowledgement = r.GetInt64(3),
            overdue = r.GetInt64(4), completed = r.GetInt64(5),
        }, ct);
        return Results.Ok(info);
    }

    private const string PortalSelect = """
        SELECT t.id, t.reference_no, t.title_ar, t.description, t.external_brief, t.acceptance_criteria, t.priority, t.due_date, t.original_due_date, t.status,
               ws.name_ar AS status_name_ar, ws.name_en AS status_name_en, ws.category, ws.color, t.progress_pct, t.assigned_at, t.accepted_at, t.submitted_at, t.completed_at,
               b.name_ar AS body_name, uc.display_name_ar AS follow_up_name, uc.email AS follow_up_email,
               CASE WHEN t.share_resolution_with_entity THEN r.number END AS resolution_number,
               CASE WHEN t.share_resolution_with_entity THEN r.title_ar END AS resolution_title,
               CASE WHEN t.share_resolution_with_entity THEN r.text_ar END AS resolution_text,
               (ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date) AS is_overdue,
               EXISTS (SELECT 1 FROM tasks.extension_requests x WHERE x.task_id = t.id AND x.status = 'pending') AS pending_extension,
               t.status_reason, t.correspondence_no, t.correspondence_date
        FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status JOIN bodies.bodies b ON b.id = t.body_id
        LEFT JOIN iam.users uc ON uc.id = t.accountable_user_id
        LEFT JOIN resolutions.resolutions r ON r.id = t.resolution_id
        """;

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.GetGuid(0), referenceNo = r.Str("reference_no"), titleAr = r.GetString(2), description = r.Str("description"), externalBrief = r.Str("external_brief"),
        acceptanceCriteria = r.Str("acceptance_criteria"), priority = r.GetString(6), dueDate = r.Date("due_date"), originalDueDate = r.Date("original_due_date"),
        status = r.GetString(9), statusNameAr = r.GetString(10), statusNameEn = r.GetString(11), statusCategory = r.GetString(12), statusColor = r.GetString(13),
        progressPct = r.GetInt32(14), assignedAt = r.Time("assigned_at"), acceptedAt = r.Time("accepted_at"), submittedAt = r.Time("submitted_at"),
        completedAt = r.Time("completed_at"), assigningBody = r.GetString(19), followUpName = r.Str("follow_up_name"), followUpEmail = r.Str("follow_up_email"),
        resolutionNumber = r.Str("resolution_number"), resolutionTitle = r.Str("resolution_title"), resolutionText = r.Str("resolution_text"),
        isOverdue = r.GetBoolean(25), pendingExtension = r.GetBoolean(26), statusReason = r.Str("status_reason"),
        correspondenceNo = r.Str("correspondence_no"), correspondenceDate = r.Date("correspondence_date"),
    };

    private static async Task<IResult> List(string? status, Db db, ICurrentUser me, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var items = await db.QueryAsync(PortalSelect + """
             WHERE t.assignee_external_entity_id = @entity AND t.status <> 'new' AND (@status::text IS NULL OR t.status = @status
               OR (@status = 'overdue' AND ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date))
             ORDER BY (ws.category IN ('done','closed')), t.due_date NULLS LAST
            """, new { entity, status }, Map, ct);
        return Results.Ok(new PagedResult<object>(items, 1, items.Count, items.Count));
    }

    private static async Task<IResult> Get(Guid id, Db db, ICurrentUser me, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var ctx = await TaskAsync(db, id, entity, access, me, false, ct);
        var task = await db.QuerySingleAsync(PortalSelect + " WHERE t.id = @id", new { id }, Map, ct);
        var actions = (await TaskData.TransitionsAsync(db, ct))
            .Where(t => t.From == ctx.Row.Status && PortalActions.Contains(t.Action) && ctx.Matches(t))
            .Select(t => new { action = t.Action, nameAr = t.NameAr, nameEn = t.NameEn, requiresReason = t.RequiresReason }).ToList();
        var documents = await db.QueryAsync("""
            SELECT d.id, d.file_name, d.size_bytes, d.uploaded_at, l.purpose, u.display_name_ar, u.user_type FROM docs.document_links l
            JOIN docs.documents d ON d.id = l.document_id JOIN iam.users u ON u.id = d.uploaded_by
            WHERE l.owner_type = 'task' AND l.owner_id = @id AND l.external_visible AND d.deleted_at IS NULL ORDER BY d.uploaded_at DESC
            """, new { id }, r => new
        {
            id = r.GetGuid(0), fileName = r.GetString(1), sizeBytes = r.GetInt64(2), uploadedAt = r.GetDateTime(3), purpose = r.GetString(4),
            uploadedBy = r.GetString(5), fromEntity = r.GetString(6) == "external",
        }, ct);
        var letters = await db.QueryAsync("""
            SELECT id, reference_no, subject, outgoing_no, outgoing_date, status FROM tasks.assignment_letters
            WHERE @id = ANY(task_ids) AND share_with_entity AND status IN ('dispatched_manual','dispatched_integration') ORDER BY created_at DESC
            """, new { id }, r => new { id = r.GetGuid(0), referenceNo = r.GetString(1), subject = r.GetString(2), outgoingNo = r.Str("outgoing_no"), outgoingDate = r.Date("outgoing_date") }, ct);
        return Results.Ok(new
        {
            task, actions,
            deliverables = await TaskQueries.DeliverablesAsync(db, id, true, ct),
            updates = await TaskQueries.UpdatesAsync(db, id, true, ct),
            extensions = await TaskQueries.ExtensionsAsync(db, id, ct),
            documents, letters,
            capabilities = new
            {
                canProgress = ctx.Row.Category is "open" or "active",
                canSubmitDeliverables = ctx.Row.Category is "open" or "active",
                canRequestExtension = ctx.Row.Category is not ("done" or "closed"),
                canComment = true,
            },
        });
    }

    private static async Task<IResult> Act(Guid id, string action, ActionRequest req, Db db, ICurrentUser me, IAccessService access, IFeatureService features,
        TaskEngine engine, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        if (!PortalActions.Contains(action)) throw new NotFoundException();
        var status = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskAsync(tx, id, entity, access, me, true, ct);
            return await engine.TransitionAsync(tx, ctx, action, req, me.UserId, "portal", ct);
        }, ct);
        return Results.Ok(new { id, status });
    }

    private static async Task<IResult> Progress(Guid id, TasksModule.ProgressRequest req, Db db, ICurrentUser me, IAccessService access, IFeatureService features,
        TaskEngine engine, IAuditWriter audit, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var status = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskAsync(tx, id, entity, access, me, true, ct);
            return await TasksModule.ProgressAsync(tx, ctx, req, me.UserId, "portal", engine, audit, ct);
        }, ct);
        return Results.Ok(new { id, status });
    }

    /// <summary>ردود الجهة ظاهرة لها ولمسؤول المتابعة دائمًا.</summary>
    private static async Task<IResult> Comment(Guid id, TasksModule.CommentRequest req, Db db, ICurrentUser me, IAccessService access, IFeatureService features,
        INotifier notifier, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskAsync(tx, id, entity, access, me, false, ct);
            await TasksModule.CommentAsync(tx, ctx, req.Text, true, me.UserId, "portal", notifier, features, ct);
        }, ct);
        return Results.Created($"/api/v1/portal/tasks/{id}", new { id });
    }

    private static async Task<IResult> Extension(Guid id, TasksModule.ExtensionRequest req, Db db, ICurrentUser me, IAccessService access, IFeatureService features,
        INotifier notifier, IAuditWriter audit, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var eid = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskAsync(tx, id, entity, access, me, true, ct);
            return await TasksModule.RequestExtensionAsync(tx, ctx, req, me.UserId, "portal", null, notifier, audit, ct);
        }, ct);
        return Results.Created($"/api/v1/portal/tasks/{id}", new { id = eid, status = "pending" });
    }

    private static async Task<IResult> Submit(Guid did, TasksModule.SubmissionRequest req, Db db, ICurrentUser me, IAccessService access, IFeatureService features,
        IAuditWriter audit, CancellationToken ct)
    {
        var entity = await EntityAsync(db, me, access, features, ct);
        var sid = await db.InTransactionAsync(async tx =>
        {
            var taskId = await tx.ScalarAsync<Guid?>("SELECT task_id FROM tasks.task_deliverables WHERE id = @did", new { did }, ct) ?? throw new NotFoundException();
            await TaskAsync(tx, taskId, entity, access, me, false, ct);
            return await TasksModule.SubmitDeliverableAsync(tx, access, me, did, req, "portal", audit, ct, allowPortal: true);
        }, ct);
        return Results.Created($"/api/v1/portal/deliverables/{did}", new { id = sid, deliverableStatus = "submitted" });
    }
}

/// <summary>أثر تعطيل البوابة: ما ينتظر الجهات من تأكيد استلام أو طلبات وردت عبر البوابة ينتقل إلى التسجيل اليدوي لدى مسؤول المتابعة.</summary>
internal sealed class PortalImpact(Db db) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys => [Contracts.FeatureKeys.ExternalPortalEnabled];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        if (newValue != "false") return [];
        var r = await db.QuerySingleAsync("""
            SELECT (SELECT count(*) FROM tasks.tasks t WHERE t.assignee_external_entity_id IS NOT NULL AND t.status = 'assigned'),
                   (SELECT count(*) FROM tasks.extension_requests x WHERE x.channel = 'portal' AND x.status = 'pending')
            """, null, x => (Waiting: x.GetInt64(0), Pending: x.GetInt64(1)), ct);
        return
        [
            new FeatureImpactItem("external_tasks_awaiting_ack", "تكليفات لجهات خارجية تنتظر تأكيد الاستلام؛ يسجله مسؤول المتابعة عند ورود الرد رسميًا",
                "External assignments awaiting acknowledgement; follow-up officers record replies received officially", (int)r.Waiting),
            new FeatureImpactItem("portal_extension_requests", "طلبات تمديد وردت عبر البوابة ولم يُبت فيها (تبقى قابلة للبت داخليًا)",
                "Portal extension requests pending decision (still decidable internally)", (int)r.Pending),
        ];
    }
}
