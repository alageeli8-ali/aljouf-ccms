using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Tasks;

/// <summary>تهيئة دورة المهام: أسماء الحالات وألوانها وترتيبها، وإضافة حالات انتظار أو تنفيذ خاصة، وصلاحية كل انتقال ومن يُسمح له من أطراف المهمة.
/// القيود الحامية لا تُهيأ: لا اكتمال إلا بالتحقق أو بإقرار الإنجاز حين لا يُشترط تحقق، والتحقق بصلاحية التحقق أو الإدارة.</summary>
internal static class WorkflowAdmin
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/workflow", Get);
        g.MapPost("/workflow/statuses", AddStatus).Validate<StatusRequest>();
        g.MapPut("/workflow/statuses/{code}", UpdateStatus).Validate<StatusRequest>();
        g.MapPost("/workflow/transitions", AddTransition).Validate<TransitionRequest>();
        g.MapPut("/workflow/transitions/{id:guid}", UpdateTransition).Validate<TransitionRequest>();
    }

    private static readonly string[] SystemActions = ["assign", "accept", "reject", "start", "hold", "resume", "submit", "complete", "verify", "return", "reopen", "cancel"];
    private static readonly string[] AllowedPermissions = [Permissions.TaskUpdate, Permissions.TaskManage, Permissions.TaskVerify];
    private static readonly string[] Locked = ["assign", "submit", "verify", "cancel"];

    public sealed record StatusRequest(
        [property: RegularExpression("^[a-z][a-z0-9_]{1,39}$", ErrorMessage = "الرمز: أحرف لاتينية صغيرة وأرقام")] string? Code,
        [property: Required, MaxLength(100)] string NameAr,
        [property: Required, MaxLength(100)] string NameEn,
        [property: OneOf("active", "waiting")] string? Category,
        [property: MaxLength(20)] string? Color,
        [property: Range(0, 1000)] int? SortOrder,
        bool? Active,
        [property: Required(ErrorMessage = "سبب التعديل مطلوب"), MinLength(5), MaxLength(500)] string Reason);

    public sealed record TransitionRequest(
        [property: RegularExpression("^[a-z][a-z0-9_]{1,39}$")] string? Action,
        string? From,
        string? To,
        [property: Required, MaxLength(100)] string NameAr,
        [property: Required, MaxLength(100)] string NameEn,
        [property: Required] string Permission,
        [property: Required] string[] Actors,
        bool RequiresReason,
        bool Active,
        [property: Required(ErrorMessage = "سبب التعديل مطلوب"), MinLength(5), MaxLength(500)] string Reason);

    private static async Task<IResult> Get(Db db, ICurrentUser me, IAccessService access, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var statuses = await TaskData.StatusesAsync(db, ct);
        var usage = (await db.QueryAsync("SELECT status, count(*) FROM tasks.tasks GROUP BY status", null, r => (Code: r.GetString(0), Count: r.GetInt64(1)), ct))
            .ToDictionary(x => x.Code, x => x.Count);
        return Results.Ok(new
        {
            statuses = statuses.Select(s => new { s.Code, s.NameAr, s.NameEn, s.Category, s.Color, s.SortOrder, s.IsSystem, s.Active, tasks = usage.GetValueOrDefault(s.Code) }),
            transitions = await TaskData.TransitionsAsync(db, ct),
            actors = new[] { "executor", "follow_up", "verifier", "manager" },
            permissions = AllowedPermissions,
            canManage = await access.HasAsync(Permissions.TaskWorkflowManage, Scope.System, ct),
        });
    }

    private static async Task<IResult> AddStatus(StatusRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskWorkflowManage, Scope.System, ct);
        if (req.Code is null || req.Category is null) throw AppValidationException.For("code", "حدد رمز الحالة وفئتها (تنفيذ أو انتظار)");
        if (await db.ScalarAsync<long>("SELECT count(*) FROM tasks.workflow_statuses WHERE code = @Code", new { req.Code }, ct) > 0)
            throw new ConflictException("duplicate", "الرمز مستخدم");
        await db.ExecuteAsync("""
            INSERT INTO tasks.workflow_statuses (code, name_ar, name_en, category, color, sort_order, updated_by) VALUES (@Code, @NameAr, @NameEn, @Category, @color, @sort, @me)
            """, new { req.Code, req.NameAr, req.NameEn, req.Category, color = req.Color ?? "default", sort = req.SortOrder ?? 55, me = me.UserId }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.workflow_status_added", "task_workflow", req.Code, After: req), ct);
        return Results.Created("/api/v1/tasks/workflow", new { code = req.Code });
    }

    private static async Task<IResult> UpdateStatus(string code, StatusRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskWorkflowManage, Scope.System, ct);
        var s = (await TaskData.StatusesAsync(db, ct)).FirstOrDefault(x => x.Code == code) ?? throw new NotFoundException();
        if (req.Active == false)
        {
            if (s.IsSystem) throw new ConflictException("system-status", "حالات النظام لا تُعطل؛ عدّل اسمها أو لونها");
            if (await db.ScalarAsync<long>("SELECT count(*) FROM tasks.tasks WHERE status = @code", new { code }, ct) > 0)
                throw new ConflictException("status-in-use", "لا تُعطل حالة فيها مهام");
        }
        await db.ExecuteAsync("""
            UPDATE tasks.workflow_statuses SET name_ar = @NameAr, name_en = @NameEn, color = COALESCE(@Color, color), sort_order = COALESCE(@SortOrder, sort_order),
              active = COALESCE(@Active, active), updated_at = now(), updated_by = @me WHERE code = @code
            """, new { code, req.NameAr, req.NameEn, req.Color, req.SortOrder, req.Active, me = me.UserId }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.workflow_status_updated", "task_workflow", code, Before: s, After: req), ct);
        return Results.NoContent();
    }

    private static void Guard(WorkflowTransition? existing, string action, string from, string to, string permission, bool active, IReadOnlyList<WorkflowStatus> statuses)
    {
        if (!AllowedPermissions.Contains(permission)) throw AppValidationException.For("permission", "الصلاحية يجب أن تكون تحديث المهمة أو إدارتها أو التحقق منها");
        var fs = statuses.FirstOrDefault(s => s.Code == from) ?? throw AppValidationException.For("from", "حالة غير معروفة");
        var ts = statuses.FirstOrDefault(s => s.Code == to) ?? throw AppValidationException.For("to", "حالة غير معروفة");
        if (existing is null)
        {
            if (SystemActions.Contains(action)) throw AppValidationException.For("action", "اسم إجراء محجوز للنظام؛ اختر اسمًا آخر");
            // الانتقالات المضافة لا تُكمل المهمة ولا تلغيها ولا تتجاوز المراجعة
            if (ts.Category is "done" or "closed" or "review" or "new" or "derived" || fs.Category is "done" or "closed" or "new" or "derived")
                throw AppValidationException.For("to", "الانتقالات المضافة تكون بين حالات التنفيذ والانتظار فقط");
        }
        else if (existing.IsSystem)
        {
            if (Locked.Contains(existing.Action) && !active) throw new ConflictException("locked-transition", "هذا الانتقال أساسي في الدورة ولا يُعطل");
            if (existing.Action is "verify" or "return" && permission == Permissions.TaskUpdate)
                throw new ConflictException("locked-transition", "التحقق يتطلب صلاحية التحقق أو الإدارة");
            if (existing.Action == "cancel" && permission != Permissions.TaskManage)
                throw new ConflictException("locked-transition", "الإلغاء لمن يدير المهام فقط");
        }
    }

    private static async Task<IResult> AddTransition(TransitionRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskWorkflowManage, Scope.System, ct);
        if (req.Action is null || req.From is null || req.To is null) throw AppValidationException.For("action", "حدد الإجراء والحالتين");
        Actors(req.Actors);
        Guard(null, req.Action, req.From, req.To, req.Permission, req.Active, await TaskData.StatusesAsync(db, ct));
        if (await db.ScalarAsync<long>("SELECT count(*) FROM tasks.workflow_transitions WHERE from_status = @From AND action = @Action", new { req.From, req.Action }, ct) > 0)
            throw new ConflictException("duplicate", "الإجراء معرّف لهذه الحالة");
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync("""
            INSERT INTO tasks.workflow_transitions (id, action, from_status, to_status, name_ar, name_en, permission, actors, requires_reason, active, sort_order, updated_by)
            VALUES (@id, @Action, @From, @To, @NameAr, @NameEn, @Permission, @Actors, @RequiresReason, @Active, 100, @me)
            """, new { id, req.Action, req.From, req.To, req.NameAr, req.NameEn, req.Permission, req.Actors, req.RequiresReason, req.Active, me = me.UserId }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.workflow_transition_added", "task_workflow", id.ToString(), After: req), ct);
        return Results.Created("/api/v1/tasks/workflow", new { id });
    }

    private static async Task<IResult> UpdateTransition(Guid id, TransitionRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskWorkflowManage, Scope.System, ct);
        var t = (await TaskData.TransitionsAsync(db, ct)).FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException();
        Actors(req.Actors);
        Guard(t, t.Action, t.From, t.To, req.Permission, req.Active, await TaskData.StatusesAsync(db, ct));
        await db.ExecuteAsync("""
            UPDATE tasks.workflow_transitions SET name_ar = @NameAr, name_en = @NameEn, permission = @Permission, actors = @Actors, requires_reason = @RequiresReason,
              active = @Active, updated_at = now(), updated_by = @me WHERE id = @id
            """, new { id, req.NameAr, req.NameEn, req.Permission, req.Actors, req.RequiresReason, req.Active, me = me.UserId }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.workflow_transition_updated", "task_workflow", id.ToString(), Before: t, After: req), ct);
        return Results.NoContent();
    }

    private static void Actors(string[] actors)
    {
        if (actors.Any(a => a is not ("executor" or "follow_up" or "verifier" or "manager"))) throw AppValidationException.For("actors", "طرف غير معروف");
    }
}
