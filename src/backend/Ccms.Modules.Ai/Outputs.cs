using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Ai;

/// <summary>المخرجات المحفوظة: تبقى قابلة للاطلاع والمراجعة والوحدة معطلة (لا تُحذف ولا تُولّد)، ولا ينشأ مخرج إلا من استدعاء مزوّد ناجح في الخادم.
/// «طُبّق» يسجل ما أنشأه المستخدم بنفسه عبر الواجهات الأساسية وصلاحياته؛ الوحدة لا تنشئ قرارًا ولا مهمة ولا تعدّل محضرًا.</summary>
internal static class Outputs
{
    public sealed record SaveRequest([property: MaxLength(1000)] string? Note);
    public sealed record DiscardRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);
    public sealed record AppliedRef([property: Required] string RecordType, Guid RecordId, int? Index);
    public sealed record AppliedRequest([property: Required, MinLength(1)] List<AppliedRef> Refs, [property: MaxLength(1000)] string? Note);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/outputs", List);
        g.MapGet("/outputs/{id:guid}", Get);
        g.MapPost("/outputs/{id:guid}/save", Save);
        g.MapPost("/outputs/{id:guid}/discard", Discard).Validate<DiscardRequest>();
        g.MapPost("/outputs/{id:guid}/applied", Applied).Validate<AppliedRequest>();
        g.MapPost("/outputs/{id:guid}/release", Release);
    }

    private sealed record Row(Guid Id, string Function, string SubjectType, Guid? SubjectId, Guid? BodyId, Guid RequestedBy, string RequestedByName, string Status,
        string? Content, string Citations, string Warnings, string Provider, string Model, bool TestOnly, string PromptVersion, string Classification, int SourceCount,
        string? HeldReason, string? ReviewedBy, DateTime? ReviewedAt, string? ReviewNote, string AppliedRefs, DateTime? PurgedAt, DateTime CreatedAt);

    private const string Select = """
        SELECT o.id, o.function, o.subject_type, o.subject_id, o.body_id, o.requested_by, u.display_name_ar, o.status, o.content::text, o.citations::text, o.warnings::text,
               o.provider, o.model, o.test_only, o.prompt_version, o.classification, o.source_count, o.held_reason, rv.display_name_ar AS reviewer, o.reviewed_at, o.review_note,
               o.applied_refs::text, o.purged_at, o.created_at
        FROM ai.outputs o JOIN iam.users u ON u.id = o.requested_by LEFT JOIN iam.users rv ON rv.id = o.reviewed_by
        """;

    private static Row Map(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Uuid("subject_id"), r.Uuid("body_id"), r.GetGuid(5),
        r.GetString(6), r.GetString(7), r.Str("content"), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetBoolean(13), r.GetString(14), r.GetString(15),
        r.GetInt32(16), r.Str("held_reason"), r.Str("reviewer"), r.Time("reviewed_at"), r.Str("review_note"), r.GetString(21), r.Time("purged_at"), r.GetDateTime(23));

    private static object View(Row o, ICurrentUser me, bool canManage) => new
    {
        o.Id, o.Function, o.SubjectType, o.SubjectId, o.BodyId, o.RequestedBy, o.RequestedByName, o.Status, generated = true, o.TestOnly, o.Provider, o.Model, o.PromptVersion,
        o.Classification, o.SourceCount, o.HeldReason, o.ReviewedBy, o.ReviewedAt, o.ReviewNote, o.PurgedAt, o.CreatedAt,
        // المحجوب لا يُعرض محتواه حتى الإفراج عنه بعد إعادة التفعيل
        content = o.Status == "held" || o.Content is null ? null : JsonNode.Parse(o.Content),
        citations = o.Status == "held" ? null : JsonNode.Parse(o.Citations),
        warnings = JsonNode.Parse(o.Warnings), appliedRefs = JsonNode.Parse(o.AppliedRefs),
        can = new
        {
            save = canManage && o.Status == "draft",
            discard = canManage && o.Status is "draft" or "held" or "saved",
            applied = canManage && o.Status is "draft" or "saved" or "applied",
            release = canManage && o.Status == "held",
        },
        mine = o.RequestedBy == me.UserId,
    };

    /// <summary>من يطّلع: صاحب الطلب، أو من يطّلع على السجل موضوع المخرج. ومن يدير: صاحب الطلب أو من يملك تحرير السجل.</summary>
    private static async Task<(bool View, bool Manage)> AccessAsync(Row o, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var mine = o.RequestedBy == me.UserId;
        if (o.SubjectId is not { } sid || o.SubjectType is "query" or "portfolio") return (mine, mine);
        var (scope, viewPerm, editPerm) = o.SubjectType switch
        {
            "minutes" => (Scope.Record("minutes", sid), Permissions.MinutesView, Permissions.MinutesEdit),
            "meeting" => (Scope.Record("meeting", sid), Permissions.MeetingView, Permissions.MeetingManage),
            "task" => (Scope.Task(sid), Permissions.TaskView, Permissions.TaskManage),
            _ => (Scope.Body(sid), Permissions.BodyView, Permissions.MeetingManage),
        };
        var perms = await access.GetPermissionsAsync(scope, ct);
        var view = perms.Contains(viewPerm) && (mine || perms.Contains(Permissions.AiUse));
        return (view, view && (mine || perms.Contains(editPerm)));
    }

    private static async Task<Row> LoadAsync(Db db, Guid id, IAccessService access, ICurrentUser me, bool manage, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var o = await db.QuerySingleAsync(Select + " WHERE o.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var (view, canManage) = await AccessAsync(o, access, me, ct);
        if (!view) throw new NotFoundException();
        if (manage && !canManage) throw new ForbiddenException("لا تملك إدارة هذا المخرج");
        return o;
    }

    private static async Task<IResult> List(string? subjectType, Guid? subjectId, string? function, bool? mine, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var rows = await db.QueryAsync(Select + """
             WHERE (@st::text IS NULL OR o.subject_type = @st) AND (@sid::uuid IS NULL OR o.subject_id = @sid) AND (@fn::text IS NULL OR o.function = @fn)
               AND (NOT @mine::boolean OR o.requested_by = @me) AND (@sid::uuid IS NOT NULL OR o.requested_by = @me)
               AND o.status <> 'expired'
             ORDER BY o.created_at DESC LIMIT 50
            """, new { st = subjectType, sid = subjectId, fn = function, mine = mine ?? false, me = me.UserId }, Map, ct);
        var list = new List<object>();
        foreach (var o in rows)
        {
            var (view, manage) = await AccessAsync(o, access, me, ct);
            if (view) list.Add(View(o, me, manage));
        }
        return Results.Ok(list);
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var o = await LoadAsync(db, id, access, me, false, ct);
        return Results.Ok(View(o, me, (await AccessAsync(o, access, me, ct)).Manage));
    }

    private static async Task<IResult> Transition(Db db, IAuditWriter audit, ICurrentUser me, IAccessService access, Guid id, string[] from, string to, string action, string? note,
        string? appliedJson, CancellationToken ct)
    {
        await LoadAsync(db, id, access, me, true, ct);
        await db.InTransactionAsync(async tx =>
        {
            var current = await tx.ScalarAsync<string>("SELECT status FROM ai.outputs WHERE id = @id FOR UPDATE", new { id }, ct);
            if (!from.Contains(current)) throw new ConflictException("invalid-state", $"لا يمكن تنفيذ الإجراء والمخرج بحالة {current}");
            await tx.ExecuteAsync("""
                UPDATE ai.outputs SET status = @to, reviewed_by = @me, reviewed_at = now(), review_note = coalesce(@note, review_note), updated_at = now(),
                       held_reason = CASE WHEN @to = 'held' THEN held_reason ELSE NULL END,
                       applied_refs = CASE WHEN @applied::jsonb IS NULL THEN applied_refs ELSE applied_refs || @applied::jsonb END
                WHERE id = @id
                """, new { id, to, me = me.UserId, note, applied = appliedJson }, ct);
            await audit.WriteAsync(tx, new AuditEntry("AI", action, "ai_output", id.ToString(), Before: new { status = current }, After: new { status = to, note }), ct);
        }, ct);
        return Results.Ok(new { id, status = to });
    }

    private static Task<IResult> Save(Guid id, SaveRequest? req, Db db, IAuditWriter audit, ICurrentUser me, IAccessService access, CancellationToken ct) =>
        Transition(db, audit, me, access, id, ["draft"], "saved", "ai.output_saved", req?.Note, null, ct);

    private static Task<IResult> Discard(Guid id, DiscardRequest req, Db db, IAuditWriter audit, ICurrentUser me, IAccessService access, CancellationToken ct) =>
        Transition(db, audit, me, access, id, ["draft", "held", "saved"], "discarded", "ai.output_discarded", req.Reason, null, ct);

    /// <summary>يسجل أن المستخدم أنشأ بنفسه سجلات من المسودة؛ يُتحقق أن كل سجل موجود ويطّلع عليه المستخدم.</summary>
    private static async Task<IResult> Applied(Guid id, AppliedRequest req, Db db, IAuditWriter audit, ICurrentUser me, IAccessService access, CancellationToken ct)
    {
        foreach (var r in req.Refs)
        {
            var ok = r.RecordType switch
            {
                "resolution" => await access.HasAsync(Permissions.ResolutionView, Scope.Record("resolution", r.RecordId), ct),
                "task" => await access.HasAsync(Permissions.TaskView, Scope.Task(r.RecordId), ct),
                "minutes" => await access.HasAsync(Permissions.MinutesView, Scope.Record("minutes", r.RecordId), ct),
                "agenda_item" => await access.HasAsync(Permissions.MeetingView, Scope.Record("agenda_item", r.RecordId), ct),
                _ => false,
            };
            if (!ok) throw AppValidationException.For("refs", $"سجل غير موجود أو خارج صلاحيتك: {r.RecordType} {r.RecordId}");
        }
        var json = Json.Serialize(req.Refs.Select(r => new { r.RecordType, r.RecordId, r.Index, by = me.UserId, at = DateTime.UtcNow }));
        return await Transition(db, audit, me, access, id, ["draft", "saved", "applied"], "applied", "ai.output_applied", req.Note, json, ct);
    }

    /// <summary>الإفراج عن نتيجة حُجزت لتعطيل الخدمة أثناء معالجتها: يتطلب أن تكون الوظيفة مفعلة الآن.</summary>
    private static async Task<IResult> Release(Guid id, Db db, IAuditWriter audit, ICurrentUser me, IAccessService access, AiService svc, CancellationToken ct)
    {
        var o = await LoadAsync(db, id, access, me, true, ct);
        await svc.DemandFunctionAsync(o.Function, o.SubjectType, o.SubjectId, ct);
        return await Transition(db, audit, me, access, id, ["held"], "draft", "ai.output_released", "أُفرج عنها بعد إعادة التفعيل", null, ct);
    }
}
