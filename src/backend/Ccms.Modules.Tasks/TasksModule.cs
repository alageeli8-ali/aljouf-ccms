using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Tasks;

public sealed class TasksModule : IModule
{
    public string Code => "TSK";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<ITaskRelationQuery, TaskRelationQuery>();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/tasks").WithTags("Tasks");
        g.MapGet("/", List);
        g.MapPost("/", Create).Validate<CreateTaskRequest>();
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/{id:guid}/updates", AddUpdate).Validate<TaskUpdateRequest>();
        api.MapGet("/my/tasks", My).WithTags("Tasks");
        api.MapGet("/portal/tasks", Portal).WithTags("External portal");
    }

    public sealed record CreateTaskRequest(
        [property: Required] Guid? BodyId,
        Guid? ResolutionId,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: MaxLength(5000)] string? Description,
        [property: Required, OneOf("user", "org_unit", "external_entity")] string AssigneeType,
        Guid? AssigneeUserId,
        Guid? AssigneeOrgUnitId,
        Guid? AssigneeExternalEntityId,
        Guid? AccountableUserId,
        Guid? VerifierUserId,
        bool? VerificationRequired,
        [property: OneOf("low", "normal", "high", "critical")] string? Priority,
        [property: Required] DateOnly? DueDate) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (AssigneeType == "user" && AssigneeUserId is null) yield return new("حدد الموظف المكلف", [nameof(AssigneeUserId)]);
            if (AssigneeType == "org_unit" && AssigneeOrgUnitId is null) yield return new("حدد الإدارة المكلفة", [nameof(AssigneeOrgUnitId)]);
            if (AssigneeType == "external_entity" && AssigneeExternalEntityId is null) yield return new("حدد الجهة الخارجية", [nameof(AssigneeExternalEntityId)]);
        }
    }

    public sealed record TaskUpdateRequest(
        [property: Range(0, 100)] int? ProgressPct,
        [property: Required(ErrorMessage = "ملاحظة التحديث مطلوبة"), MaxLength(4000)] string Note);

    private const string Select = """
        SELECT t.*, b.name_ar AS body_name, b.code AS body_code, r.number AS resolution_number, r.title_ar AS resolution_title,
               ua.display_name_ar AS assignee_user_name, o.name_ar AS assignee_org_unit_name, e.name_ar AS assignee_entity_name,
               uc.display_name_ar AS accountable_name,
               (t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date AND t.status IN ('assigned','in_progress','returned')) AS is_overdue
        FROM tasks.tasks t
        JOIN bodies.bodies b ON b.id = t.body_id
        LEFT JOIN resolutions.resolutions r ON r.id = t.resolution_id
        LEFT JOIN iam.users ua ON ua.id = t.assignee_user_id
        LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id
        LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
        LEFT JOIN iam.users uc ON uc.id = t.accountable_user_id
        """;

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), referenceNo = r.Str("reference_no"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"),
        resolutionId = r.Uuid("resolution_id"), resolutionNumber = r.Str("resolution_number"), resolutionTitle = r.Str("resolution_title"),
        titleAr = r.Get<string>("title_ar"), description = r.Str("description"), assigneeType = r.Get<string>("assignee_type"),
        assigneeUserId = r.Uuid("assignee_user_id"), assigneeName = r.Str("assignee_user_name") ?? r.Str("assignee_org_unit_name") ?? r.Str("assignee_entity_name"),
        assigneeOrgUnitId = r.Uuid("assignee_org_unit_id"), assigneeExternalEntityId = r.Uuid("assignee_external_entity_id"),
        accountableUserId = r.Uuid("accountable_user_id"), accountableName = r.Str("accountable_name"),
        priority = r.Get<string>("priority"), dueDate = r.Date("due_date"), originalDueDate = r.Date("original_due_date"),
        status = r.Get<string>("status"), progressPct = r.Get<int>("progress_pct"), escalationLevel = r.Get<int>("escalation_level"),
        isOverdue = r.Get<bool>("is_overdue"), createdAt = r.Get<DateTime>("created_at"), updatedAt = r.Get<DateTime>("updated_at"),
    };

    /// <summary>المهام الظاهرة للمستخدم: مهام جهاته (task.view) + ما أُسند إليه أو يتابعه + مهام جهته الخارجية إن فُعّلت البوابة.</summary>
    private sealed record Visibility(string Where, bool All, Guid[] Ids, Guid Me, Guid? Entity);

    private static async Task<Visibility> VisibilityAsync(IAccessService access, ICurrentUser me, IFeatureService features, Db db, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.TaskView, ct);
        var roles = await access.GetSystemRolesAsync(me.UserId, ct);
        Guid? entity = null;
        if (roles.Contains(Roles.ExternalRepresentative) && await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct))
            entity = await db.ScalarAsync<Guid?>("SELECT external_entity_id FROM iam.users WHERE id = @id", new { id = me.UserId }, ct);
        const string where = """
            (@all::boolean OR t.body_id = ANY(@ids::uuid[]) OR t.assignee_user_id = @me OR t.accountable_user_id = @me
             OR t.assignee_org_unit_id IN (SELECT id FROM org.org_units WHERE manager_user_id = @me)
             OR (@entity::uuid IS NOT NULL AND t.assignee_external_entity_id = @entity))
            """;
        return new Visibility(where, filter.All, filter.BodyIds, me.UserId, entity);
    }

    private static async Task<IResult> List(Guid? bodyId, string? status, bool? overdue, string? q, Guid? assigneeUserId, int? page, int? pageSize,
        Db db, IAccessService access, ICurrentUser me, IFeatureService features, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var v = await VisibilityAsync(access, me, features, db, ct);
        var vis = v.Where;
        var p = PageQuery.From(page, pageSize);
        var args = new Dictionary<string, object?>
        {
            ["all"] = v.All, ["ids"] = v.Ids, ["me"] = v.Me, ["entity"] = v.Entity, ["bodyId"] = bodyId, ["status"] = status,
            ["overdue"] = overdue ?? false, ["q"] = SqlText.Like(q), ["assignee"] = assigneeUserId, ["limit"] = p.PageSize, ["offset"] = p.Offset,
        };
        var where = $"""
            WHERE {vis}
              AND (@bodyId::uuid IS NULL OR t.body_id = @bodyId) AND (@status::text IS NULL OR t.status = @status)
              AND (@assignee::uuid IS NULL OR t.assignee_user_id = @assignee)
              AND (NOT @overdue::boolean OR (t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date AND t.status IN ('assigned','in_progress','returned')))
              AND (@q::text IS NULL OR t.title_ar ILIKE @q OR t.reference_no ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM tasks.tasks t " + "\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY t.due_date NULLS LAST, t.created_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> My(Db db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var items = await db.QueryAsync(Select + "\n" + """
            WHERE (t.assignee_user_id = @me OR t.accountable_user_id = @me OR t.assignee_org_unit_id IN (SELECT id FROM org.org_units WHERE manager_user_id = @me))
              AND t.status NOT IN ('draft','completed','cancelled')
            ORDER BY t.due_date NULLS LAST
            """, new { me = me.UserId }, Map, ct);
        return Results.Ok(items);
    }

    /// <summary>بوابة الجهات الخارجية: محكومة بـ EXTERNAL_PORTAL_ENABLED على الخادم وبدور ممثل الجهة.</summary>
    private static async Task<IResult> Portal(Db db, ICurrentUser me, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        if (!(await access.GetSystemRolesAsync(me.UserId, ct)).Contains(Roles.ExternalRepresentative))
            throw new ForbiddenException(permission: Permissions.PortalAccess);
        var entity = await db.ScalarAsync<Guid?>("SELECT external_entity_id FROM iam.users WHERE id = @id", new { id = me.UserId }, ct)
            ?? throw new ForbiddenException("حسابك غير مرتبط بجهة خارجية");
        var items = await db.QueryAsync(Select + " WHERE t.assignee_external_entity_id = @entity AND t.status NOT IN ('draft','cancelled') ORDER BY t.due_date",
            new { entity }, Map, ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> Create(CreateTaskRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INotifier notifier, INumberingService numbering, IClock clock, CancellationToken ct)
    {
        var bodyId = req.BodyId!.Value;
        await access.DemandAsync(Permissions.TaskManage, Scope.Body(bodyId), ct);
        var id = Guid.CreateVersion7();
        var created = await db.InTransactionAsync(async tx =>
        {
            var code = await tx.ScalarAsync<string>("SELECT code FROM bodies.bodies WHERE id = @bodyId AND status = 'active'", new { bodyId }, ct)
                ?? throw new ConflictException("body-not-active", "الجهة غير نشطة");
            if (req.ResolutionId is { } rid && await tx.ScalarAsync<Guid?>("SELECT body_id FROM resolutions.resolutions WHERE id = @rid", new { rid }, ct) != bodyId)
                throw AppValidationException.For("resolutionId", "القرار لا يتبع هذه الجهة");
            // المسؤول المتابع: إلزامي لغير الموظف؛ افتراضيًا مدير الإدارة أو المتابع المعيّن للجهة الخارجية (BRL-09)
            var accountable = req.AccountableUserId ?? req.AssigneeType switch
            {
                "org_unit" => await tx.ScalarAsync<Guid?>("SELECT manager_user_id FROM org.org_units WHERE id = @u", new { u = req.AssigneeOrgUnitId }, ct),
                "external_entity" => await tx.ScalarAsync<Guid?>("SELECT liaison_user_id FROM org.external_entities WHERE id = @e", new { e = req.AssigneeExternalEntityId }, ct),
                _ => null,
            };
            if (req.AssigneeType != "user" && accountable is null)
                throw AppValidationException.For("accountableUserId", "المسؤول المتابع الداخلي إلزامي للمهام المسندة لإدارة أو جهة خارجية");
            var year = clock.TodayLocal.Year;
            var seq = await numbering.NextAsync(tx, $"task:{code}", year, ct);
            var refNo = $"{code}-T-{year}-{seq:0000}";
            await tx.ExecuteAsync("""
                INSERT INTO tasks.tasks (id, body_id, resolution_id, reference_no, title_ar, description, assignee_type, assignee_user_id, assignee_org_unit_id,
                  assignee_external_entity_id, accountable_user_id, verifier_user_id, verification_required, priority, original_due_date, due_date, status, created_by)
                VALUES (@id, @bodyId, @ResolutionId, @refNo, @TitleAr, @Description, @AssigneeType,
                  CASE WHEN @AssigneeType = 'user' THEN @AssigneeUserId::uuid END,
                  CASE WHEN @AssigneeType = 'org_unit' THEN @AssigneeOrgUnitId::uuid END,
                  CASE WHEN @AssigneeType = 'external_entity' THEN @AssigneeExternalEntityId::uuid END,
                  @accountable, @VerifierUserId, @verify, @priority, @DueDate, @DueDate, 'assigned', @by)
                """, new
            {
                id, bodyId, req.ResolutionId, refNo, req.TitleAr, req.Description, req.AssigneeType, req.AssigneeUserId, req.AssigneeOrgUnitId,
                req.AssigneeExternalEntityId, accountable, req.VerifierUserId, verify = req.VerificationRequired ?? true,
                priority = req.Priority ?? "normal", req.DueDate, by = me.UserId,
            }, ct);
            foreach (var uid in new[] { req.AssigneeType == "user" ? req.AssigneeUserId : null, accountable }.OfType<Guid>().Distinct())
                await notifier.NotifyAsync(tx, new NotificationRequest(uid, "task_assigned", $"أُسندت إليك مهمة: {req.TitleAr} ({refNo})",
                    $"Task assigned: {refNo}", $"/tasks/{id}", $"task_assigned:{id}:{uid}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.assigned", "task", id.ToString(), After: new { refNo, req.AssigneeType, accountable, req.DueDate }), ct);
            return refNo;
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id, referenceNo = created });
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskView, Scope.Task(id), ct);
        var task = await db.QuerySingleAsync(Select + " WHERE t.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var updates = await db.QueryAsync("""
            SELECT tu.id, tu.update_type, tu.progress_pct, tu.note, tu.created_at, u.display_name_ar
            FROM tasks.task_updates tu JOIN iam.users u ON u.id = tu.author_user_id WHERE tu.task_id = @id ORDER BY tu.created_at DESC
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), updateType = r.Get<string>("update_type"), progressPct = r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
            note = r.Str("note"), createdAt = r.Get<DateTime>("created_at"), author = r.Get<string>("display_name_ar"),
        }, ct);
        return Results.Ok(new { task, updates, permissions = (await access.GetPermissionsAsync(Scope.Task(id), ct)).Order() });
    }

    private static async Task<IResult> AddUpdate(Guid id, TaskUpdateRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskUpdate, Scope.Task(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM tasks.tasks WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is not ("assigned" or "in_progress" or "returned")) throw new ConflictException("invalid-transition", "لا تُحدَّث مهمة في حالتها الحالية");
            await tx.ExecuteAsync("""
                INSERT INTO tasks.task_updates (id, task_id, author_user_id, update_type, progress_pct, note, old_value, new_value)
                VALUES (@uid, @id, @by, 'progress', @ProgressPct, @Note, jsonb_build_object('status', @status::text), jsonb_build_object('status', 'in_progress'))
                """, new { uid = Guid.CreateVersion7(), id, by = me.UserId, req.ProgressPct, req.Note, status }, ct);
            await tx.ExecuteAsync("UPDATE tasks.tasks SET status = 'in_progress', progress_pct = COALESCE(@ProgressPct, progress_pct) WHERE id = @id", new { id, req.ProgressPct }, ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.progress_updated", "task", id.ToString(), Before: new { status }, After: new { req.ProgressPct }), ct);
        }, ct);
        return Results.NoContent();
    }
}

internal sealed class TaskRelationQuery(Db db) : ITaskRelationQuery
{
    public Task<TaskRelation?> GetAsync(Guid taskId, CancellationToken ct = default) =>
        db.QuerySingleAsync<TaskRelation?>("""
            SELECT t.id, t.body_id, t.assignee_user_id, t.accountable_user_id, o.manager_user_id, t.assignee_external_entity_id
            FROM tasks.tasks t LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id WHERE t.id = @taskId
            """, new { taskId }, r => new TaskRelation(r.GetGuid(0), r.GetGuid(1), r.Uuid("assignee_user_id"), r.Uuid("accountable_user_id"),
            r.Uuid("manager_user_id"), r.Uuid("assignee_external_entity_id")), ct);
}
