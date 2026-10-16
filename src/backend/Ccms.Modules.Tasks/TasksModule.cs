using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ccms.Modules.Tasks;

/// <summary>المهام والتكليفات: بطاقة المهمة، ودورة الحالات القابلة للتهيئة، والمخرجات ومعايير قبولها، والتحقق والإغلاق،
/// والتمديد، والتنبيه والتصعيد، وبوابة الجهات الخارجية، وخطابات التكليف، واللوحات.</summary>
public sealed class TasksModule : IModule
{
    public string Code => "TSK";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITaskRelationQuery, TaskRelationQuery>();
        services.AddScoped<ITaskActivator, TaskActivator>();
        services.AddScoped<TaskEngine>();
        services.AddScoped<EscalationService>();
        services.AddScoped<IFeatureImpactProvider, PortalImpact>();
        // وحدة التكاملات تسجل بوابتها الفعلية؛ دونها لا يتاح الإرسال عبر نظام المراسلات
        services.TryAddScoped<ICorrespondenceGateway, UnavailableCorrespondenceGateway>();
        if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<EscalationWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/tasks").WithTags("Tasks");
        g.MapGet("/", TaskQueries.List);
        g.MapGet("/dashboard", TaskQueries.Dashboard);
        g.MapGet("/board", TaskQueries.Board);
        g.MapPost("/", Create).Validate<CreateTaskRequest>().Idempotent();
        g.MapGet("/{id:guid}", TaskQueries.Get);
        g.MapPatch("/{id:guid}", Edit).Validate<EditTaskRequest>();
        g.MapPost("/{id:guid}/reassign", Reassign).Validate<ReassignRequest>();
        g.MapPost("/{id:guid}/actions/{action}", Act).Validate<ActionRequest>();
        g.MapPost("/{id:guid}/updates", Progress).Validate<ProgressRequest>();
        g.MapPost("/{id:guid}/comments", Comment).Validate<CommentRequest>().Idempotent();
        g.MapPost("/{id:guid}/deliverables", AddDeliverable).Validate<DeliverableRequest>();
        g.MapPatch("/deliverables/{did:guid}", EditDeliverable).Validate<DeliverableRequest>();
        g.MapPost("/deliverables/{did:guid}/submissions", SubmitDeliverable).Validate<SubmissionRequest>().Idempotent();
        g.MapPost("/{id:guid}/extension-requests", RequestExtension).Validate<ExtensionRequest>().Idempotent();
        g.MapPost("/extension-requests/{eid:guid}/decision", DecideExtension).Validate<ExtensionDecision>();
        g.MapPost("/extension-requests/{eid:guid}/withdraw", WithdrawExtension);
        g.MapPost("/{id:guid}/external-updates", RecordExternal).Validate<ExternalUpdateRequest>().Idempotent();
        g.MapPost("/{id:guid}/escalate", Escalations.Manual).Validate<Escalations.ManualRequest>();
        g.MapPost("/escalations/{eid:guid}/acknowledge", Escalations.Acknowledge).Validate<Escalations.AckRequest>();
        g.MapPost("/escalations/run", Escalations.Run).Validate<Escalations.RunRequest>();
        WorkflowAdmin.Map(g);
        Letters.Map(g);
        api.MapGet("/my/tasks", TaskQueries.My).WithTags("Tasks");
        Portal.Map(api);
    }

    // ---------------- الطلبات ----------------

    public sealed record DeliverableItem([property: Required, MaxLength(300)] string Title, [property: MaxLength(2000)] string? Description,
        [property: MaxLength(2000)] string? AcceptanceCriteria, bool? Required);

    public sealed record CreateTaskRequest(
        [property: Required] Guid? BodyId,
        Guid? ResolutionId,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: MaxLength(5000)] string? Description,
        [property: Required, OneOf("member", "employee", "user", "org_unit", "external_entity")] string AssigneeType,
        Guid? AssigneeUserId,
        Guid? AssigneeOrgUnitId,
        Guid? AssigneeExternalEntityId,
        Guid? AccountableUserId,
        Guid? VerifierUserId,
        bool? VerificationRequired,
        [property: OneOf("low", "normal", "high", "critical")] string? Priority,
        [property: Required] DateOnly? DueDate,
        Guid? AgendaItemId = null,
        Guid? ExecutorUserId = null,
        [property: MaxLength(300)] string? ExecutorContact = null,
        Guid? ExecutingOrgUnitId = null,
        [property: MaxLength(4000)] string? AcceptanceCriteria = null,
        [property: MaxLength(4000)] string? ExternalBrief = null,
        bool? ShareResolutionWithEntity = null,
        [property: MaxLength(20)] List<DeliverableItem>? Deliverables = null,
        bool? SaveAsNew = null) : IValidatableObject
    {
        public string Kind => AssigneeType == "user" ? "employee" : AssigneeType;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Kind is "member" or "employee" && AssigneeUserId is null) yield return new("حدد الشخص المكلف", [nameof(AssigneeUserId)]);
            if (Kind == "org_unit" && AssigneeOrgUnitId is null) yield return new("حدد الإدارة المكلفة", [nameof(AssigneeOrgUnitId)]);
            if (Kind == "external_entity" && AssigneeExternalEntityId is null) yield return new("حدد الجهة الخارجية", [nameof(AssigneeExternalEntityId)]);
            if (AccountableUserId is not null && (AccountableUserId == AssigneeUserId || AccountableUserId == ExecutorUserId))
                yield return new("مسؤول المتابعة يجب أن يكون غير المنفذ", [nameof(AccountableUserId)]);
            foreach (var (d, i) in (Deliverables ?? []).Select((d, i) => (d, i)))
                if (string.IsNullOrWhiteSpace(d.Title)) yield return new("عنوان المخرج مطلوب", [$"deliverables[{i}].title"]);
        }
    }

    public sealed record EditTaskRequest(
        [property: MaxLength(300)] string? TitleAr,
        [property: MaxLength(5000)] string? Description,
        [property: OneOf("low", "normal", "high", "critical")] string? Priority,
        Guid? AccountableUserId,
        Guid? VerifierUserId,
        bool? VerificationRequired,
        Guid? ExecutorUserId,
        [property: MaxLength(300)] string? ExecutorContact,
        [property: MaxLength(4000)] string? AcceptanceCriteria,
        [property: MaxLength(4000)] string? ExternalBrief,
        bool? ShareResolutionWithEntity,
        [property: MaxLength(1000)] string? Reason);

    public sealed record ReassignRequest(
        [property: Required, OneOf("member", "employee", "user", "org_unit", "external_entity")] string AssigneeType,
        Guid? AssigneeUserId, Guid? AssigneeOrgUnitId, Guid? AssigneeExternalEntityId, Guid? ExecutorUserId, Guid? AccountableUserId,
        [property: Required(ErrorMessage = "سبب إعادة الإسناد مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record ProgressRequest(
        [property: Range(0, 100)] int? ProgressPct,
        [property: Required(ErrorMessage = "ملاحظة التحديث مطلوبة"), MaxLength(4000)] string Note,
        [property: MaxLength(2000)] string? DelayReason = null);

    public sealed record CommentRequest([property: Required, MinLength(2), MaxLength(4000)] string Text, bool? ShareWithEntity);

    public sealed record DeliverableRequest([property: MaxLength(300)] string? Title, [property: MaxLength(2000)] string? Description,
        [property: MaxLength(2000)] string? AcceptanceCriteria, bool? Required, bool? Remove);

    public sealed record SubmissionRequest([property: Required, MinLength(1), MaxLength(20)] List<Guid> DocumentIds, [property: MaxLength(2000)] string? Note);

    public sealed record ExtensionRequest(
        [property: Required] DateOnly? RequestedDueDate,
        [property: Required, OneOf("dependency", "resources", "scope_change", "external_party", "data_availability", "other")] string DelayCause,
        [property: Required(ErrorMessage = "سبب التأخير مطلوب"), MinLength(5), MaxLength(2000)] string Reason);

    public sealed record ExtensionDecision(
        [property: Required, OneOf("approved", "rejected")] string Decision,
        DateOnly? ApprovedDueDate,
        [property: MaxLength(2000)] string? Note);

    /// <summary>رد الجهة الخارجية الوارد بالقنوات الرسمية (خطاب، بريد، اتصال، اجتماع) يسجله مسؤول المتابعة؛ يعمل والبوابة معطلة أو مفعلة.</summary>
    public sealed record ExternalUpdateRequest(
        [property: Required, OneOf("acknowledged", "declined", "progress", "deliverable", "extension_request", "comment")] string Kind,
        [property: Required, OneOf("letter", "email", "phone", "meeting", "correspondence_system", "other")] string OfficialChannel,
        [property: MaxLength(100)] string? Reference,
        [property: Required] DateOnly? ReceivedOn,
        [property: Required, MinLength(3), MaxLength(4000)] string Note,
        [property: Range(0, 100)] int? ProgressPct,
        Guid? DeliverableId,
        List<Guid>? DocumentIds,
        DateOnly? RequestedDueDate,
        [property: OneOf("dependency", "resources", "scope_change", "external_party", "data_availability", "other")] string? DelayCause);

    // ---------------- الإنشاء ----------------

    private static async Task<IResult> Create(CreateTaskRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, TaskEngine engine,
        IClock clock, CancellationToken ct)
    {
        var bodyId = req.BodyId!.Value;
        await access.DemandAsync(Permissions.TaskManage, Scope.Body(bodyId), ct);
        if (req.DueDate < clock.TodayLocal) throw AppValidationException.For("dueDate", "موعد الاستحقاق لا يكون في الماضي");
        var id = Guid.CreateVersion7();
        var result = await db.InTransactionAsync(async tx =>
        {
            if (await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @bodyId", new { bodyId }, ct) != "active")
                throw new ConflictException("body-not-active", "الجهة غير نشطة");
            // المصدر: قرار أو توصية، أو بند اجتماع (FR-TSK-01)
            var draft = req.SaveAsNew == true;
            Guid? meetingId = null, agendaItemId = req.AgendaItemId;
            if (req.ResolutionId is { } rid)
            {
                var res = await tx.QuerySingleAsync("SELECT body_id, status, meeting_id, agenda_item_id FROM resolutions.resolutions WHERE id = @rid", new { rid },
                    r => (Body: r.GetGuid(0), Status: r.GetString(1), Meeting: r.Uuid("meeting_id"), Item: r.Uuid("agenda_item_id")), ct);
                if (res == default || res.Body != bodyId) throw AppValidationException.For("resolutionId", "القرار لا يتبع هذه الجهة");
                if (res.Status is not ("proposed" or "issued" or "in_execution" or "referred"))
                    throw new ConflictException("invalid-transition", "لا تُسند مهام لقرار غير صادر أو منتهٍ");
                // مهمة قرار مقترح في مسودة المحضر تبقى «جديدة» بلا رقم ولا إشعار حتى يُعتمد المحضر (08 §2.4)
                draft |= res.Status == "proposed";
                meetingId = res.Meeting;
                agendaItemId ??= res.Item;
            }
            else if (agendaItemId is { } aid)
            {
                meetingId = await tx.ScalarAsync<Guid?>("""
                    SELECT m.id FROM meetings.agenda_items ai JOIN meetings.meetings m ON m.id = ai.meeting_id WHERE ai.id = @aid AND m.body_id = @bodyId
                    """, new { aid, bodyId }, ct) ?? throw AppValidationException.For("agendaItemId", "البند لا يتبع اجتماعات هذه الجهة");
            }
            var a = await ResolveAssigneeAsync(tx, bodyId, req.Kind, req.AssigneeUserId, req.AssigneeOrgUnitId, req.AssigneeExternalEntityId, req.ExecutorUserId, ct);
            // مسؤول المتابعة داخل المكتب: إلزامي ومنفصل عن المنفذ؛ الافتراضي ضابط الاتصال للجهة الخارجية أو منشئ المهمة
            var accountable = req.AccountableUserId
                ?? (req.Kind == "external_entity" ? a.Liaison : null)
                ?? (me.UserId != req.AssigneeUserId && me.UserId != a.Executor ? me.UserId : (Guid?)null)
                ?? throw AppValidationException.For("accountableUserId", "حدد مسؤول متابعة غير المنفذ");
            if (accountable == req.AssigneeUserId || accountable == a.Executor)
                throw AppValidationException.For("accountableUserId", "مسؤول المتابعة يجب أن يكون غير المنفذ");
            await RequireInternalUserAsync(tx, accountable, "accountableUserId", ct);
            if (req.VerifierUserId is { } v)
            {
                await RequireInternalUserAsync(tx, v, "verifierUserId", ct);
                if (v == req.AssigneeUserId || v == a.Executor) throw AppValidationException.For("verifierUserId", "لا يتحقق من المهمة منفذها");
            }
            // التحقق إلزامي لمهام الجهات الخارجية وللمهام ذات المخرجات المطلوبة
            var verify = req.VerificationRequired ?? true;
            if (req.Kind == "external_entity" || (req.Deliverables?.Count ?? 0) > 0) verify = true;
            await tx.ExecuteAsync("""
                INSERT INTO tasks.tasks (id, body_id, resolution_id, meeting_id, agenda_item_id, title_ar, description, assignee_type, assignee_user_id, assignee_org_unit_id,
                  assignee_external_entity_id, executor_user_id, executor_contact, executing_org_unit_id, accountable_user_id, verifier_user_id, verification_required,
                  priority, original_due_date, due_date, status, acceptance_criteria, external_brief, share_resolution_with_entity, created_by, updated_by)
                VALUES (@id, @bodyId, @ResolutionId, @meetingId, @agendaItemId, @TitleAr, @Description, @kind,
                  CASE WHEN @kind IN ('member','employee') THEN @AssigneeUserId::uuid END,
                  CASE WHEN @kind = 'org_unit' THEN @AssigneeOrgUnitId::uuid END,
                  CASE WHEN @kind = 'external_entity' THEN @AssigneeExternalEntityId::uuid END,
                  @executor, @ExecutorContact, @executingUnit, @accountable, @VerifierUserId, @verify, @priority, @DueDate, @DueDate, 'new',
                  @AcceptanceCriteria, @ExternalBrief, @share, @by, @by)
                """, new
            {
                id, bodyId, req.ResolutionId, meetingId, agendaItemId, req.TitleAr, req.Description, kind = req.Kind, req.AssigneeUserId, req.AssigneeOrgUnitId,
                req.AssigneeExternalEntityId, executor = a.Executor, ExecutorContact = req.ExecutorContact ?? a.Contact, executingUnit = req.ExecutingOrgUnitId ?? a.Unit,
                accountable, req.VerifierUserId, verify, priority = req.Priority ?? "normal", req.DueDate, req.AcceptanceCriteria, req.ExternalBrief,
                share = req.ShareResolutionWithEntity ?? false, by = me.UserId,
            }, ct);
            var pos = 0;
            foreach (var d in req.Deliverables ?? [])
                await tx.ExecuteAsync("""
                    INSERT INTO tasks.task_deliverables (id, task_id, position, title, description, acceptance_criteria, required, created_by)
                    VALUES (@did, @id, @pos, @Title, @Description, @AcceptanceCriteria, @req, @by)
                    """, new { did = Guid.CreateVersion7(), id, pos = ++pos, d.Title, d.Description, d.AcceptanceCriteria, req = d.Required ?? true, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.created", "task", id.ToString(),
                After: new { req.TitleAr, kind = req.Kind, req.ResolutionId, agendaItemId, accountable, verify, req.DueDate, deliverables = pos }), ct);
            if (draft) return (Ref: (string?)null, Status: "new");
            // المهمة لم تُلتزم بعد؛ المنشئ يملك إدارة المهام في الجهة، فيُسندها بهذه الصفة
            var row = await TaskData.LoadAsync(tx, id, true, ct);
            var ctx = new TaskContext(row, new HashSet<string> { Permissions.TaskView, Permissions.TaskManage }, false, false, false, true, false);
            var status = await engine.TransitionAsync(tx, ctx, "assign", new ActionRequest(null, null, null), me.UserId, "system", ct);
            var refNo = await tx.ScalarAsync<string>("SELECT reference_no FROM tasks.tasks WHERE id = @id", new { id }, ct);
            return (Ref: refNo, Status: status);
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id, referenceNo = result.Ref, status = result.Status });
    }

    internal sealed record Assignee(Guid? Executor, Guid? Unit, Guid? Liaison, string? Contact);

    /// <summary>يتحقق من المكلَّف: العضو عضو ساري في الجهة، والموظف حساب داخلي نشط، والإدارة نشطة (ومنفذها الافتراضي مديرها)، والجهة الخارجية نشطة.</summary>
    internal static async Task<Assignee> ResolveAssigneeAsync(ISql tx, Guid bodyId, string kind, Guid? userId, Guid? unitId, Guid? entityId, Guid? executor, CancellationToken ct)
    {
        switch (kind)
        {
            case "member":
                if (await tx.ScalarAsync<long>("""
                    SELECT count(*) FROM bodies.body_members WHERE body_id = @bodyId AND user_id = @userId AND status = 'active'
                      AND start_date <= current_date AND (end_date IS NULL OR end_date >= current_date)
                    """, new { bodyId, userId }, ct) == 0)
                    throw AppValidationException.For("assigneeUserId", "المكلف ليس عضوًا ساريًا في هذه الجهة");
                return new(null, await tx.ScalarAsync<Guid?>("SELECT org_unit_id FROM iam.users WHERE id = @userId", new { userId }, ct), null, null);
            case "employee":
                var u = await tx.QuerySingleAsync("SELECT user_type, status, org_unit_id FROM iam.users WHERE id = @userId", new { userId },
                    r => (Type: r.GetString(0), Status: r.GetString(1), Unit: r.Uuid("org_unit_id")), ct);
                if (u == default || u.Status != "active" || u.Type is "external" or "guest")
                    throw AppValidationException.For("assigneeUserId", "الموظف المكلف غير موجود أو غير نشط أو ليس حسابًا داخليًا");
                return new(null, u.Unit, null, null);
            case "org_unit":
                var o = await tx.QuerySingleAsync("SELECT status, manager_user_id FROM org.org_units WHERE id = @unitId", new { unitId },
                    r => (Status: r.GetString(0), Manager: r.Uuid("manager_user_id")), ct);
                if (o == default || o.Status != "active") throw AppValidationException.For("assigneeOrgUnitId", "الإدارة غير موجودة أو غير نشطة");
                if (executor is { } ex && await tx.ScalarAsync<Guid?>("SELECT org_unit_id FROM iam.users WHERE id = @ex AND status = 'active'", new { ex }, ct) != unitId)
                    throw AppValidationException.For("executorUserId", "المنفذ المسؤول يجب أن يكون من موظفي الإدارة المكلفة");
                return new(executor, unitId, null, null);
            default:
                var e = await tx.QuerySingleAsync("SELECT status, liaison_user_id, contact_name FROM org.external_entities WHERE id = @entityId", new { entityId },
                    r => (Status: r.GetString(0), Liaison: r.Uuid("liaison_user_id"), Contact: r.Str("contact_name")), ct);
                if (e == default || e.Status != "active") throw AppValidationException.For("assigneeExternalEntityId", "الجهة الخارجية غير موجودة أو غير نشطة");
                return new(null, null, e.Liaison, e.Contact);
        }
    }

    private static async Task RequireInternalUserAsync(ISql tx, Guid userId, string field, CancellationToken ct)
    {
        var type = await tx.ScalarAsync<string>("SELECT user_type FROM iam.users WHERE id = @userId AND status = 'active'", new { userId }, ct);
        if (type is null or "external" or "guest") throw AppValidationException.For(field, "يجب أن يكون موظفًا داخليًا نشطًا في المكتب");
    }

    private static async Task<IResult> Edit(Guid id, EditTaskRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskManage, Scope.Task(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var t = await TaskData.LoadAsync(tx, id, true, ct);
            if (t.Category is "done" or "closed") throw new ConflictException("task-closed", "لا تُعدل مهمة مكتملة أو ملغاة");
            var executor = req.ExecutorUserId ?? t.ExecutorUserId;
            var accountable = req.AccountableUserId ?? t.AccountableUserId;
            if (accountable is not null && (accountable == t.AssigneeUserId || accountable == executor))
                throw AppValidationException.For("accountableUserId", "مسؤول المتابعة يجب أن يكون غير المنفذ");
            if (req.AccountableUserId is { } acc) await RequireInternalUserAsync(tx, acc, "accountableUserId", ct);
            if (req.VerifierUserId is { } v && (v == t.AssigneeUserId || v == executor)) throw AppValidationException.For("verifierUserId", "لا يتحقق من المهمة منفذها");
            if (req.VerificationRequired == false && (t.AssigneeType == "external_entity"
                || await tx.ScalarAsync<long>("SELECT count(*) FROM tasks.task_deliverables WHERE task_id = @id AND status <> 'removed'", new { id }, ct) > 0))
                throw new ConflictException("verification-required", "التحقق إلزامي لمهام الجهات الخارجية وللمهام ذات المخرجات");
            await tx.ExecuteAsync("""
                UPDATE tasks.tasks SET title_ar = COALESCE(@TitleAr, title_ar), description = COALESCE(@Description, description), priority = COALESCE(@Priority, priority),
                  accountable_user_id = COALESCE(@AccountableUserId, accountable_user_id), verifier_user_id = COALESCE(@VerifierUserId, verifier_user_id),
                  verification_required = COALESCE(@VerificationRequired, verification_required), executor_user_id = COALESCE(@ExecutorUserId, executor_user_id),
                  executor_contact = COALESCE(@ExecutorContact, executor_contact), acceptance_criteria = COALESCE(@AcceptanceCriteria, acceptance_criteria),
                  external_brief = COALESCE(@ExternalBrief, external_brief), share_resolution_with_entity = COALESCE(@ShareResolutionWithEntity, share_resolution_with_entity),
                  updated_by = @by
                WHERE id = @id
                """, new
            {
                id, req.TitleAr, req.Description, req.Priority, req.AccountableUserId, req.VerifierUserId, req.VerificationRequired, req.ExecutorUserId,
                req.ExecutorContact, req.AcceptanceCriteria, req.ExternalBrief, req.ShareResolutionWithEntity, by = me.UserId,
            }, ct);
            await TaskData.LogAsync(tx, id, me.UserId, "edit", req.Reason, null, req, ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.edited", "task", id.ToString(), After: req), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>إعادة الإسناد (بعد رفض المكلف أو لتغيير المنفذ): يُسجل السبب، وتعود المهمة «مسندة» إن كانت مرفوضة، ويُشعر المنفذ الجديد.</summary>
    private static async Task<IResult> Reassign(Guid id, ReassignRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INotifier notifier, IFeatureService features, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskManage, Scope.Task(id), ct);
        var kind = req.AssigneeType == "user" ? "employee" : req.AssigneeType;
        await db.InTransactionAsync(async tx =>
        {
            var t = await TaskData.LoadAsync(tx, id, true, ct);
            if (t.Category is "done" or "closed" or "review") throw new ConflictException("invalid-transition", "لا يُعاد إسناد مهمة مكتملة أو ملغاة أو قيد المراجعة");
            var a = await ResolveAssigneeAsync(tx, t.BodyId, kind, req.AssigneeUserId, req.AssigneeOrgUnitId, req.AssigneeExternalEntityId, req.ExecutorUserId, ct);
            var accountable = req.AccountableUserId ?? t.AccountableUserId;
            if (accountable == req.AssigneeUserId || accountable == a.Executor) throw AppValidationException.For("accountableUserId", "مسؤول المتابعة يجب أن يكون غير المنفذ");
            var status = t.Status == "rejected" ? "assigned" : t.Status;
            await tx.ExecuteAsync("""
                UPDATE tasks.tasks SET assignee_type = @kind,
                  assignee_user_id = CASE WHEN @kind IN ('member','employee') THEN @AssigneeUserId::uuid END,
                  assignee_org_unit_id = CASE WHEN @kind = 'org_unit' THEN @AssigneeOrgUnitId::uuid END,
                  assignee_external_entity_id = CASE WHEN @kind = 'external_entity' THEN @AssigneeExternalEntityId::uuid END,
                  executor_user_id = @executor, executing_org_unit_id = @unit, accountable_user_id = @accountable, status = @status,
                  status_changed_at = CASE WHEN @status <> status THEN now() ELSE status_changed_at END, accepted_at = NULL, assigned_at = now(),
                  verification_required = verification_required OR @kind = 'external_entity', escalation_level = 0, updated_by = @by
                WHERE id = @id
                """, new { id, kind, req.AssigneeUserId, req.AssigneeOrgUnitId, req.AssigneeExternalEntityId, executor = a.Executor, unit = a.Unit, accountable, status, by = me.UserId }, ct);
            await TaskData.LogAsync(tx, id, me.UserId, "reassignment", req.Reason, new { t.AssigneeType, t.AssigneeName }, new { kind, req.AssigneeUserId, req.AssigneeOrgUnitId, req.AssigneeExternalEntityId },
                visibility: "internal", ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.reassigned", "task", id.ToString(), Before: new { t.AssigneeType, t.Status }, After: new { kind, status, req.Reason }), ct);
            var nt = await TaskData.LoadAsync(tx, id, false, ct);
            var executors = await TaskData.ExecutorUsersAsync(tx, nt, await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct), ct);
            await TaskData.NotifyAsync(tx, notifier, [.. executors.Cast<Guid?>(), accountable], nt, "task_assigned", "أُسندت إليك مهمة", "Task assigned", "task_reassigned:" + Guid.NewGuid(), except: me.UserId, ct: ct);
        }, ct);
        return Results.NoContent();
    }

    // ---------------- الإجراءات ----------------

    private static async Task<IResult> Act(Guid id, string action, ActionRequest req, Db db, IAccessService access, ICurrentUser me, TaskEngine engine, CancellationToken ct)
    {
        var status = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskData.ContextAsync(tx, access, me, id, true, ct);
            if (ctx.PortalOnly) throw new NotFoundException();
            return await engine.TransitionAsync(tx, ctx, action, req, me.UserId, "system", ct);
        }, ct);
        return Results.Ok(new { id, status });
    }

    /// <summary>تحديث نسبة الإنجاز وسبب التأخير. أول تحديث على مهمة مسندة أو مقبولة يبدأ التنفيذ.</summary>
    internal static async Task<string> ProgressAsync(ISql tx, TaskContext ctx, ProgressRequest req, Guid by, string channel, TaskEngine engine, IAuditWriter audit, CancellationToken ct)
    {
        var t = ctx.Row;
        if (!ctx.Has(Permissions.TaskUpdate) || !(ctx.IsExecutor || ctx.IsFollowUp || ctx.IsManager)) throw new ForbiddenException(permission: Permissions.TaskUpdate);
        var status = t.Status;
        if (t.Category == "open")
            status = await engine.TransitionAsync(tx, ctx, "start", new ActionRequest(null, "بدء التنفيذ مع أول تحديث", null), by, channel, ct);
        else if (t.Category != "active")
            throw new ConflictException("invalid-transition", "لا يُحدّث تقدم مهمة في حالتها الحالية");
        if (req.ProgressPct is 100) throw AppValidationException.For("progressPct", "نسبة 100٪ تُسجل بالتحقق أو إقرار الإنجاز لا بالتحديث");
        await tx.ExecuteAsync("""
            UPDATE tasks.tasks SET progress_pct = COALESCE(@ProgressPct, progress_pct), delay_reason = COALESCE(@DelayReason, delay_reason), updated_by = @by WHERE id = @id
            """, new { id = t.Id, req.ProgressPct, req.DelayReason, by }, ct);
        await TaskData.LogAsync(tx, t.Id, by, "progress", req.Note, new { progress = t.Progress }, new { progress = req.ProgressPct }, req.ProgressPct,
            channel, "shared", delayReason: req.DelayReason, ct: ct);
        await audit.WriteAsync(tx, new AuditEntry("TSK", "task.progress_updated", "task", t.Id.ToString(), Before: new { t.Progress }, After: new { req.ProgressPct, req.DelayReason, channel }), ct);
        return status;
    }

    private static async Task<IResult> Progress(Guid id, ProgressRequest req, Db db, IAccessService access, ICurrentUser me, TaskEngine engine, IAuditWriter audit, CancellationToken ct)
    {
        await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskData.ContextAsync(tx, access, me, id, true, ct);
            if (ctx.PortalOnly) throw new NotFoundException();
            await ProgressAsync(tx, ctx, req, me.UserId, "system", engine, audit, ct);
        }, ct);
        return Results.NoContent();
    }

    internal static async Task CommentAsync(ISql tx, TaskContext ctx, string text, bool shared, Guid by, string channel, INotifier notifier, IFeatureService features, CancellationToken ct)
    {
        if (!ctx.Has(Permissions.TaskComment)) throw new ForbiddenException(permission: Permissions.TaskComment);
        var t = ctx.Row;
        await TaskData.LogAsync(tx, t.Id, by, "comment", text, channel: channel, visibility: shared ? "shared" : "internal", ct: ct);
        var users = new List<Guid?> { t.AccountableUserId };
        if (shared) users.AddRange((await TaskData.ExecutorUsersAsync(tx, t, await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct), ct)).Cast<Guid?>());
        await TaskData.NotifyAsync(tx, notifier, users, t, "task_comment", "تعليق جديد على المهمة", "New task comment", "task_comment:" + Guid.NewGuid(), except: by, ct: ct);
    }

    private static async Task<IResult> Comment(Guid id, CommentRequest req, Db db, IAccessService access, ICurrentUser me, INotifier notifier, IFeatureService features, CancellationToken ct)
    {
        await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskData.ContextAsync(tx, access, me, id, false, ct);
            if (ctx.PortalOnly) throw new NotFoundException();
            await CommentAsync(tx, ctx, req.Text, req.ShareWithEntity == true, me.UserId, "system", notifier, features, ct);
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id });
    }

    // ---------------- المخرجات ----------------

    private static async Task<IResult> AddDeliverable(Guid id, DeliverableRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskManage, Scope.Task(id), ct);
        if (string.IsNullOrWhiteSpace(req.Title)) throw AppValidationException.For("title", "عنوان المخرج مطلوب");
        var did = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var t = await TaskData.LoadAsync(tx, id, true, ct);
            if (t.Category is "done" or "closed") throw new ConflictException("task-closed", "لا تُضاف مخرجات لمهمة مكتملة أو ملغاة");
            var pos = await tx.ScalarAsync<int>("SELECT COALESCE(max(position), 0) + 1 FROM tasks.task_deliverables WHERE task_id = @id", new { id }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO tasks.task_deliverables (id, task_id, position, title, description, acceptance_criteria, required, created_by)
                VALUES (@did, @id, @pos, @Title, @Description, @AcceptanceCriteria, @required, @by)
                """, new { did, id, pos, req.Title, req.Description, req.AcceptanceCriteria, required = req.Required ?? true, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE tasks.tasks SET verification_required = true WHERE id = @id", new { id }, ct);
            await TaskData.LogAsync(tx, id, me.UserId, "edit", $"مخرج مطلوب: {req.Title}", visibility: "shared", ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.deliverable_added", "task", id.ToString(), After: new { did, req.Title, req.AcceptanceCriteria }), ct);
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id = did });
    }

    private static async Task<IResult> EditDeliverable(Guid did, DeliverableRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var taskId = await db.ScalarAsync<Guid?>("SELECT task_id FROM tasks.task_deliverables WHERE id = @did", new { did }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.TaskManage, Scope.Task(taskId), ct);
        await db.InTransactionAsync(async tx =>
        {
            var d = await tx.QuerySingleAsync("SELECT status, (SELECT count(*) FROM tasks.deliverable_submissions s WHERE s.deliverable_id = d.id) FROM tasks.task_deliverables d WHERE id = @did FOR UPDATE",
                new { did }, r => (Status: r.GetString(0), Subs: r.GetInt64(1)), ct);
            if (d.Status == "accepted") throw new ConflictException("deliverable-accepted", "المخرج المقبول لا يُعدل");
            if (req.Remove == true && d.Subs > 0) throw new ConflictException("deliverable-submitted", "لا يُحذف مخرج قُدمت له ملفات؛ اجعله غير إلزامي");
            await tx.ExecuteAsync("""
                UPDATE tasks.task_deliverables SET title = COALESCE(@Title, title), description = COALESCE(@Description, description),
                  acceptance_criteria = COALESCE(@AcceptanceCriteria, acceptance_criteria), required = COALESCE(@Required, required),
                  status = CASE WHEN @remove THEN 'removed' ELSE status END
                WHERE id = @did
                """, new { did, req.Title, req.Description, req.AcceptanceCriteria, req.Required, remove = req.Remove == true }, ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.deliverable_edited", "task", taskId.ToString(), After: new { did, req }), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>تقديم ملفات لمخرج: يُسجل التقديم ويصبح المخرج «مقدمًا» فقط؛ لا تتغير حالة المهمة ولا يُعد ذلك قبولًا.</summary>
    internal static async Task<Guid> SubmitDeliverableAsync(ISql tx, IAccessService access, ICurrentUser me, Guid did, SubmissionRequest req, string channel,
        IAuditWriter audit, CancellationToken ct, bool allowPortal = false)
    {
        var taskId = await tx.ScalarAsync<Guid?>("SELECT task_id FROM tasks.task_deliverables WHERE id = @did AND status <> 'removed'", new { did }, ct) ?? throw new NotFoundException();
        var ctx = await TaskData.ContextAsync(tx, access, me, taskId, true, ct);
        if (ctx.PortalOnly && !allowPortal) throw new NotFoundException();
        if (!ctx.Has(Permissions.TaskUpdate) || !(ctx.IsExecutor || ctx.IsFollowUp)) throw new ForbiddenException(permission: Permissions.TaskUpdate);
        if (ctx.Row.Category is not ("open" or "active")) throw new ConflictException("invalid-transition", "تُقدَّم المخرجات أثناء التنفيذ");
        var status = await tx.ScalarAsync<string>("SELECT status FROM tasks.task_deliverables WHERE id = @did FOR UPDATE", new { did }, ct);
        if (status == "accepted") throw new ConflictException("deliverable-accepted", "قُبل هذا المخرج مسبقًا");
        var linked = await tx.ScalarAsync<long>("""
            SELECT count(DISTINCT l.document_id) FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id
            WHERE l.owner_type = 'task' AND l.owner_id = @taskId AND l.document_id = ANY(@ids::uuid[]) AND d.deleted_at IS NULL
            """, new { taskId, ids = req.DocumentIds.Distinct().ToArray() }, ct);
        if (linked != req.DocumentIds.Distinct().Count()) throw AppValidationException.For("documentIds", "ارفع الملفات مرفقات للمهمة أولًا");
        var sid = Guid.CreateVersion7();
        await tx.ExecuteAsync("""
            INSERT INTO tasks.deliverable_submissions (id, deliverable_id, task_id, document_ids, note, channel, submitted_by)
            VALUES (@sid, @did, @taskId, @ids, @Note, @channel, @by)
            """, new { sid, did, taskId, ids = req.DocumentIds.Distinct().ToArray(), req.Note, channel, by = me.UserId }, ct);
        await tx.ExecuteAsync("UPDATE tasks.task_deliverables SET status = 'submitted', review_note = NULL WHERE id = @did", new { did }, ct);
        // ملفات المخرج تُشارك مع الجهة المكلفة لتتابع ما قدمته
        if (ctx.Row.AssigneeEntityId is not null)
            await tx.ExecuteAsync("UPDATE docs.document_links SET external_visible = true, purpose = CASE WHEN purpose = 'attachment' THEN 'deliverable' ELSE purpose END WHERE owner_type = 'task' AND owner_id = @taskId AND document_id = ANY(@ids::uuid[])",
                new { taskId, ids = req.DocumentIds.ToArray() }, ct);
        await TaskData.LogAsync(tx, taskId, me.UserId, "deliverable", req.Note, null, new { deliverableId = did, documents = req.DocumentIds.Count }, channel: channel,
            visibility: "shared", documentIds: [.. req.DocumentIds], ct: ct);
        await audit.WriteAsync(tx, new AuditEntry("TSK", "task.deliverable_submitted", "task", taskId.ToString(), After: new { did, documents = req.DocumentIds, channel }), ct);
        return sid;
    }

    private static async Task<IResult> SubmitDeliverable(Guid did, SubmissionRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var sid = await db.InTransactionAsync(tx => SubmitDeliverableAsync(tx, access, me, did, req, "system", audit, ct), ct);
        return Results.Created($"/api/v1/tasks/deliverables/{did}", new { id = sid, deliverableStatus = "submitted" });
    }

    // ---------------- التمديد ----------------

    internal static async Task<Guid> RequestExtensionAsync(ISql tx, TaskContext ctx, ExtensionRequest req, Guid by, string channel, string? reference, INotifier notifier,
        IAuditWriter audit, CancellationToken ct)
    {
        var t = ctx.Row;
        if (!(ctx.IsExecutor || ctx.IsFollowUp) || !ctx.Has(Permissions.TaskUpdate)) throw new ForbiddenException(permission: Permissions.TaskUpdate);
        if (t.Category is "done" or "closed" or "new") throw new ConflictException("invalid-transition", "لا يُطلب تمديد لمهمة في حالتها الحالية");
        if (t.DueDate is null || req.RequestedDueDate <= t.DueDate) throw AppValidationException.For("requestedDueDate", "الموعد المطلوب يجب أن يكون بعد الموعد الحالي");
        if (await tx.ScalarAsync<long>("SELECT count(*) FROM tasks.extension_requests WHERE task_id = @id AND status = 'pending'", new { id = t.Id }, ct) > 0)
            throw new ConflictException("extension-pending", "يوجد طلب تمديد قيد البت لهذه المهمة");
        var eid = Guid.CreateVersion7();
        await tx.ExecuteAsync("""
            INSERT INTO tasks.extension_requests (id, task_id, requested_by, channel, reference, current_due_date, requested_due_date, delay_cause, reason)
            VALUES (@eid, @taskId, @by, @channel, @reference, @due, @RequestedDueDate, @DelayCause, @Reason)
            """, new { eid, taskId = t.Id, by, channel, reference, due = t.DueDate, req.RequestedDueDate, req.DelayCause, req.Reason }, ct);
        await tx.ExecuteAsync("UPDATE tasks.tasks SET delay_reason = @Reason WHERE id = @id", new { id = t.Id, req.Reason }, ct);
        await TaskData.LogAsync(tx, t.Id, by, "extension_request", req.Reason, new { dueDate = t.DueDate }, new { requested = req.RequestedDueDate, cause = req.DelayCause },
            channel: channel, visibility: "shared", reference: reference, delayReason: req.Reason, ct: ct);
        // تنبيه عند طلب التمديد: أصحاب البت حسب سياسة الجهة، ومسؤول المتابعة
        var approvers = await ApproversAsync(tx, t.BodyId, ct);
        await TaskData.NotifyAsync(tx, notifier, [.. approvers.Cast<Guid?>(), t.AccountableUserId], t, "extension_requested", "طلب تمديد مهمة", "Extension requested",
            "extension_requested:" + eid, "warning", by, ct: ct);
        await audit.WriteAsync(tx, new AuditEntry("TSK", "task.extension_requested", "task", t.Id.ToString(), After: new { eid, req.RequestedDueDate, req.DelayCause, channel }), ct);
        return eid;
    }

    /// <summary>أصحاب البت في التمديد حسب سياسة الجهة: أمين السر (والرئيس) أو الرئيس فقط، مع المفوض بصلاحيات الرئيس.</summary>
    private static async Task<List<Guid>> ApproversAsync(ISql tx, Guid bodyId, CancellationToken ct)
    {
        var approver = await tx.ScalarAsync<string>("""
            SELECT extension_approver FROM bodies.body_policies WHERE body_id = @bodyId AND effective_from <= current_date ORDER BY effective_from DESC, version DESC LIMIT 1
            """, new { bodyId }, ct) ?? "secretary";
        var list = await TaskData.BodyRoleUsersAsync(tx, bodyId, approver == "chair" ? ["chair"] : ["chair", "secretary"], ct);
        list.AddRange(await tx.QueryAsync("""
            SELECT delegate_user_id FROM iam.delegations WHERE body_id = @bodyId AND scope = 'chair_authority' AND status = 'active' AND valid_from <= now() AND valid_to > now()
            """, new { bodyId }, r => r.GetGuid(0), ct));
        return [.. list.Distinct()];
    }

    private static async Task<IResult> RequestExtension(Guid id, ExtensionRequest req, Db db, IAccessService access, ICurrentUser me, INotifier notifier, IAuditWriter audit, CancellationToken ct)
    {
        var eid = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskData.ContextAsync(tx, access, me, id, true, ct);
            if (ctx.PortalOnly) throw new NotFoundException();
            return await RequestExtensionAsync(tx, ctx, req, me.UserId, "system", null, notifier, audit, ct);
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id = eid, status = "pending" });
    }

    /// <summary>البت في التمديد: الموافقة تغير الموعد وتحفظ الموعد الأصلي وتعيد مستوى التصعيد إلى الصفر (08 §4.2).</summary>
    private static async Task<IResult> DecideExtension(Guid eid, ExtensionDecision req, Db db, IAccessService access, ICurrentUser me, INotifier notifier,
        IAuditWriter audit, IFeatureService features, CancellationToken ct)
    {
        var taskId = await db.ScalarAsync<Guid?>("SELECT task_id FROM tasks.extension_requests WHERE id = @eid", new { eid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.TaskExtensionApprove, Scope.Task(taskId), ct);
        var due = await db.InTransactionAsync(async tx =>
        {
            var e = await tx.QuerySingleAsync("SELECT status, requested_by, requested_due_date, current_due_date FROM tasks.extension_requests WHERE id = @eid FOR UPDATE",
                new { eid }, r => (Status: r.GetString(0), By: r.GetGuid(1), Requested: r.Get<DateOnly>("requested_due_date"), Current: r.Get<DateOnly>("current_due_date")), ct);
            if (e.Status != "pending") throw new ConflictException("extension-decided", "بُت في الطلب مسبقًا");
            if (e.By == me.UserId) throw new AccessDeniedException("separation-of-duties", "لا يبت في طلب التمديد مقدمه");
            var t = await TaskData.LoadAsync(tx, taskId, true, ct);
            if (!(await ApproversAsync(tx, t.BodyId, ct)).Contains(me.UserId))
                throw new AccessDeniedException("not-extension-approver", "البت في التمديد لصاحب الصلاحية المحدد في سياسة الجهة");
            if (req.Decision == "rejected" && string.IsNullOrWhiteSpace(req.Note)) throw AppValidationException.For("note", "اذكر سبب رفض التمديد");
            var newDue = req.Decision == "approved" ? req.ApprovedDueDate ?? e.Requested : (DateOnly?)null;
            if (newDue is { } nd && nd <= e.Current) throw AppValidationException.For("approvedDueDate", "الموعد الجديد يجب أن يكون بعد الموعد الحالي");
            await tx.ExecuteAsync("""
                UPDATE tasks.extension_requests SET status = @Decision, approved_due_date = @newDue, decided_by = @by, decided_at = now(), decision_note = @Note WHERE id = @eid
                """, new { eid, req.Decision, newDue, by = me.UserId, req.Note }, ct);
            if (newDue is not null)
            {
                await tx.ExecuteAsync("""
                    UPDATE tasks.tasks SET due_date = @newDue, original_due_date = COALESCE(original_due_date, due_date), escalation_level = 0, updated_by = @by WHERE id = @taskId
                    """, new { taskId, newDue, by = me.UserId }, ct);
                await TaskData.LogAsync(tx, taskId, me.UserId, "due_date_change", req.Note, new { dueDate = t.DueDate }, new { dueDate = newDue, extensionId = eid }, visibility: "shared", ct: ct);
            }
            await TaskData.LogAsync(tx, taskId, me.UserId, "extension_decision", req.Note, null, new { decision = req.Decision, dueDate = newDue }, visibility: "shared", ct: ct);
            var executors = await TaskData.ExecutorUsersAsync(tx, t, await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct), ct);
            await TaskData.NotifyAsync(tx, notifier, [e.By, t.AccountableUserId, .. executors.Cast<Guid?>()], t, "extension_decided",
                req.Decision == "approved" ? "وُوفق على تمديد المهمة" : "رُفض طلب تمديد المهمة", req.Decision == "approved" ? "Extension approved" : "Extension rejected",
                "extension_decided:" + eid, except: me.UserId, ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.extension_" + req.Decision, "task", taskId.ToString(), Before: new { dueDate = t.DueDate }, After: new { eid, newDue, req.Note }), ct);
            return newDue;
        }, ct);
        return Results.Ok(new { id = eid, status = req.Decision, dueDate = due });
    }

    private static async Task<IResult> WithdrawExtension(Guid eid, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var n = await db.ExecuteAsync("UPDATE tasks.extension_requests SET status = 'withdrawn', decided_at = now() WHERE id = @eid AND status = 'pending' AND requested_by = @me",
            new { eid, me = me.UserId }, ct);
        if (n == 0) throw new NotFoundException();
        await audit.WriteAsync(new AuditEntry("TSK", "task.extension_withdrawn", "extension_request", eid.ToString()), ct);
        return Results.NoContent();
    }

    // ---------------- ردود الجهة عبر القنوات الرسمية ----------------

    private static async Task<IResult> RecordExternal(Guid id, ExternalUpdateRequest req, Db db, IAccessService access, ICurrentUser me, TaskEngine engine,
        INotifier notifier, IAuditWriter audit, CancellationToken ct)
    {
        var result = await db.InTransactionAsync(async tx =>
        {
            var ctx = await TaskData.ContextAsync(tx, access, me, id, true, ct);
            if (ctx.PortalOnly) throw new NotFoundException();
            if (!ctx.Has(Permissions.TaskRecordExternal) || !(ctx.IsFollowUp || ctx.IsManager)) throw new ForbiddenException(permission: Permissions.TaskRecordExternal);
            var t = ctx.Row;
            if (t.AssigneeEntityId is not { } entity) throw new ConflictException("not-external", "التسجيل نيابة عن جهة خارجية لمهامها فقط");
            // المسجل يتصرف نيابة عن الجهة: يُعامل منفذًا لغرض هذا الإجراء، ويُوثق المصدر والمرجع
            var asExecutor = ctx with { IsExecutor = true };
            string status = t.Status;
            switch (req.Kind)
            {
                case "acknowledged":
                    status = await engine.TransitionAsync(tx, asExecutor, "accept", new ActionRequest(null, req.Note, null), me.UserId, "recorded", ct);
                    break;
                case "declined":
                    status = await engine.TransitionAsync(tx, asExecutor, "reject", new ActionRequest(req.Note, null, null), me.UserId, "recorded", ct);
                    break;
                case "progress":
                    status = await ProgressAsync(tx, asExecutor, new ProgressRequest(req.ProgressPct, req.Note), me.UserId, "recorded", engine, audit, ct);
                    break;
                case "deliverable":
                    if (req.DeliverableId is not { } did || req.DocumentIds is not { Count: > 0 } docs)
                        throw AppValidationException.For("deliverableId", "حدد المخرج والملفات الواردة من الجهة");
                    await SubmitDeliverableAsync(tx, access, me, did, new SubmissionRequest(docs, req.Note), "recorded", audit, ct);
                    break;
                case "extension_request":
                    if (req.RequestedDueDate is null || req.DelayCause is null) throw AppValidationException.For("requestedDueDate", "حدد الموعد المطلوب وسبب التأخير");
                    await RequestExtensionAsync(tx, asExecutor, new ExtensionRequest(req.RequestedDueDate, req.DelayCause, req.Note), me.UserId, "recorded", req.Reference, notifier, audit, ct);
                    break;
            }
            await TaskData.LogAsync(tx, id, me.UserId, "external_update", req.Note, null, new { req.Kind, req.ProgressPct }, req.ProgressPct, "recorded", "shared",
                req.OfficialChannel, req.Reference, req.ReceivedOn, entity, documentIds: req.DocumentIds?.ToArray(), ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.external_update_recorded", "task", id.ToString(),
                After: new { req.Kind, req.OfficialChannel, req.Reference, req.ReceivedOn, entity }), ct);
            return status;
        }, ct);
        return Results.Created($"/api/v1/tasks/{id}", new { id, status = result, channel = "recorded" });
    }
}

internal sealed class TaskRelationQuery(Db db) : ITaskRelationQuery
{
    public Task<TaskRelation?> GetAsync(Guid taskId, CancellationToken ct = default) =>
        db.QuerySingleAsync<TaskRelation?>("""
            SELECT t.id, t.body_id, t.assignee_user_id, t.accountable_user_id, o.manager_user_id, t.assignee_external_entity_id, t.executor_user_id, t.verifier_user_id,
                   xo.manager_user_id AS executing_manager
            FROM tasks.tasks t LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id LEFT JOIN org.org_units xo ON xo.id = t.executing_org_unit_id
            WHERE t.id = @taskId
            """, new { taskId }, r => new TaskRelation(r.GetGuid(0), r.GetGuid(1), r.Uuid("assignee_user_id"), r.Uuid("accountable_user_id"),
            r.Uuid("manager_user_id"), r.Uuid("assignee_external_entity_id"), r.Uuid("executor_user_id"), r.Uuid("verifier_user_id"), r.Uuid("executing_manager")), ct);
}

/// <summary>عند اعتماد المحضر: تُسند المهام «الجديدة» لقراراته الصادرة (ترقيم وإشعار)؛ ومهام القرارات غير الصادرة تُلغى بسببها.</summary>
internal sealed class TaskActivator(TaskEngine engine, IAuditWriter audit) : ITaskActivator
{
    public async Task<int> ActivateAsync(ISql tx, IReadOnlyList<Guid> issuedResolutions, IReadOnlyList<Guid> notIssuedResolutions, Guid by, CancellationToken ct = default)
    {
        var drafts = await tx.QueryAsync("""
            SELECT t.id FROM tasks.tasks t WHERE t.status = 'new' AND t.resolution_id = ANY(@ids::uuid[]) ORDER BY t.created_at FOR UPDATE OF t
            """, new { ids = issuedResolutions.ToArray() }, r => r.GetGuid(0), ct);
        foreach (var id in drafts)
        {
            // الإسناد الآلي بصلاحية النظام عند الاعتماد: سياق مدير يتجاوز صفة المعتمد
            var row = await TaskData.LoadAsync(tx, id, true, ct);
            var ctx = new TaskContext(row, new HashSet<string> { Permissions.TaskView, Permissions.TaskManage }, false, false, false, true, false);
            await engine.TransitionAsync(tx, ctx, "assign", new ActionRequest(null, "إسناد آلي عند اعتماد المحضر", null), by, "system", ct);
        }
        var cancelled = await tx.ExecuteAsync("""
            UPDATE tasks.tasks SET status = 'cancelled', cancelled_at = now(), status_reason = 'القرار لم يصدر' WHERE status = 'new' AND resolution_id = ANY(@ids::uuid[])
            """, new { ids = notIssuedResolutions.ToArray() }, ct);
        if (cancelled > 0)
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.drafts_cancelled", "minutes", null, After: new { cancelled, reason = "القرار لم يصدر" }, ActorUserId: by), ct);
        return drafts.Count;
    }
}
