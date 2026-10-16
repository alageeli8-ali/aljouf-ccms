using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Signatures;

/// <summary>مسارات التوقيع القابلة للتهيئة لكل جهة أو نوع اجتماع: الأعضاء ثم الرئيس، أو الرئيس فقط، أو أمين السر والأعضاء والرئيس… بإصدارات وسبب لكل تغيير.</summary>
internal static class Routes
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/routes", List);
        g.MapPost("/routes", Create).Validate<RouteRequest>();
        g.MapPut("/routes/{id:guid}", Update).Validate<RouteRequest>();
        g.MapPost("/routes/{id:guid}/deactivate", Deactivate).Validate<SignaturesModule.ReasonBody>();
    }

    public static readonly string[] Roles = ["secretary", "members", "chair"];
    public static readonly string[] MeetingTypes = ["regular", "extraordinary", "urgent", "circulation"];

    public sealed record StepRequest(int Order, [property: Required] string Role, bool AllowDelegation);

    public sealed record RouteRequest(
        Guid? BodyId,
        string? MeetingType,
        [property: Required(ErrorMessage = "الاسم مطلوب"), MaxLength(200)] string NameAr,
        [property: MaxLength(200)] string? NameEn,
        [property: Required, MinLength(1), MaxLength(3)] List<StepRequest> Steps,
        bool? Sequential,
        string? SignatureType,
        [property: Range(1, 90)] int ExpiryDays,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(5), MaxLength(1000)] string Reason) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (MeetingType is not null && !MeetingTypes.Contains(MeetingType)) yield return new("نوع الاجتماع غير معروف", [nameof(MeetingType)]);
            if (SignatureType is not null && !SignatureTypes.All.Contains(SignatureType)) yield return new("نوع التوقيع غير معروف", [nameof(SignatureType)]);
            if (Steps.Any(s => !Roles.Contains(s.Role))) yield return new("دور المرحلة غير معروف", [nameof(Steps)]);
            if (Steps.Select(s => s.Role).Distinct().Count() != Steps.Count) yield return new("لا يتكرر الدور في أكثر من مرحلة", [nameof(Steps)]);
            if (Steps.Any(s => s.AllowDelegation && s.Role != "chair")) yield return new("التوقيع بالنيابة متاح لمرحلة الرئيس فقط (بتفويض صلاحيات الرئيس الموثق)", [nameof(Steps)]);
        }

        public string StepsJson() => JsonSerializer.Serialize(Steps.OrderBy(s => s.Order).Select((s, i) => new { order = i + 1, role = s.Role, allowDelegation = s.AllowDelegation }));
    }

    private static async Task<IResult> List(Db db, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ESignRoutesManage, Scope.System, ct);
        var s = await SigSettings.LoadAsync(features, ct);
        var rows = await db.QueryAsync("""
            SELECT r.id, r.body_id, b.name_ar, r.meeting_type, r.name_ar, r.name_en, r.steps::text, r.sequential, r.signature_type, r.expiry_days, r.active, r.version,
                   u.display_name_ar, r.updated_at
            FROM sig.routes r LEFT JOIN bodies.bodies b ON b.id = r.body_id LEFT JOIN iam.users u ON u.id = COALESCE(r.updated_by, r.created_by)
            ORDER BY r.active DESC, r.body_id NULLS FIRST, r.meeting_type NULLS FIRST, r.updated_at DESC
            """, null, r => new
        {
            id = r.GetGuid(0), bodyId = r.Uuid("body_id"), bodyName = r.IsDBNull(2) ? null : r.GetString(2), meetingType = r.Str("meeting_type"), nameAr = r.GetString(4),
            nameEn = r.Str("name_en"), steps = ResolvedRoute.ParseSteps(r.GetString(6)), sequential = r.IsDBNull(7) ? (bool?)null : r.GetBoolean(7),
            signatureType = r.Str("signature_type"), expiryDays = r.GetInt32(9), active = r.GetBoolean(10), version = r.GetInt32(11),
            updatedBy = r.IsDBNull(12) ? null : r.GetString(12), updatedAt = r.GetDateTime(13),
        }, ct);
        var defaults = await ESignatureGateway.ResolveRouteAsync(new NoRoutes(), s, Guid.Empty, "regular", true, ct);
        return Results.Ok(new { routes = rows, defaultRoute = defaults.ToJson() });
    }

    /// <summary>لاستخراج المسار الافتراضي من الإعدادات دون مسارات مخزنة.</summary>
    private sealed class NoRoutes : ISql
    {
        public Task<List<T>> QueryAsync<T>(string sql, object? args, Func<System.Data.Common.DbDataReader, T> map, CancellationToken ct = default) => Task.FromResult(new List<T>());
        public Task<T?> QuerySingleAsync<T>(string sql, object? args, Func<System.Data.Common.DbDataReader, T> map, CancellationToken ct = default) => Task.FromResult(default(T));
        public Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default) => Task.FromResult(default(T));
    }

    private static async Task<IResult> Create(RouteRequest req, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ESignRoutesManage, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var exists = await tx.ScalarAsync<long>("""
                SELECT count(*) FROM sig.routes WHERE active AND body_id IS NOT DISTINCT FROM @BodyId AND meeting_type IS NOT DISTINCT FROM @MeetingType
                """, new { req.BodyId, req.MeetingType }, ct);
            if (exists > 0) throw new ConflictException("route-exists", "يوجد مسار فعّال لهذه الجهة ونوع الاجتماع؛ عدّله بدل إنشاء آخر");
            await tx.ExecuteAsync("""
                INSERT INTO sig.routes (id, body_id, meeting_type, name_ar, name_en, steps, sequential, signature_type, expiry_days, created_by, updated_by)
                VALUES (@id, @BodyId, @MeetingType, @NameAr, @NameEn, @steps::jsonb, @Sequential, @SignatureType, @ExpiryDays, @me, @me)
                """, new { id, req.BodyId, req.MeetingType, req.NameAr, req.NameEn, steps = req.StepsJson(), req.Sequential, req.SignatureType, req.ExpiryDays, me = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.route_created", "esign_route", id.ToString(),
                After: new { req.BodyId, req.MeetingType, req.NameAr, steps = req.Steps, req.Sequential, req.SignatureType, req.ExpiryDays, req.Reason }), ct);
        }, ct);
        return Results.Created($"/api/v1/esign/routes/{id}", new { id });
    }

    private static async Task<IResult> Update(Guid id, RouteRequest req, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ESignRoutesManage, Scope.System, ct);
        await db.InTransactionAsync(async tx =>
        {
            var before = await tx.QuerySingleAsync("SELECT steps::text, sequential, signature_type, expiry_days, name_ar, version FROM sig.routes WHERE id = @id AND active FOR UPDATE",
                new { id }, r => new { steps = r.GetString(0), sequential = r.IsDBNull(1) ? (bool?)null : r.GetBoolean(1), type = r.Str("signature_type"), expiry = r.GetInt32(3),
                    name = r.GetString(4), version = r.GetInt32(5) }, ct) ?? throw new NotFoundException("المسار غير موجود أو غير فعّال");
            await tx.ExecuteAsync("""
                UPDATE sig.routes SET name_ar = @NameAr, name_en = @NameEn, steps = @steps::jsonb, sequential = @Sequential, signature_type = @SignatureType,
                  expiry_days = @ExpiryDays, version = version + 1, updated_by = @me, updated_at = now() WHERE id = @id
                """, new { id, req.NameAr, req.NameEn, steps = req.StepsJson(), req.Sequential, req.SignatureType, req.ExpiryDays, me = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.route_updated", "esign_route", id.ToString(), Before: before,
                After: new { req.NameAr, steps = req.Steps, req.Sequential, req.SignatureType, req.ExpiryDays, req.Reason, version = before.version + 1 }), ct);
        }, ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> Deactivate(Guid id, SignaturesModule.ReasonBody req, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ESignRoutesManage, Scope.System, ct);
        var n = await db.ExecuteAsync("UPDATE sig.routes SET active = false, updated_by = @me, updated_at = now() WHERE id = @id AND active", new { id, me = me.UserId }, ct);
        if (n == 0) throw new NotFoundException();
        await audit.WriteAsync(new AuditEntry("SIG", "esign.route_deactivated", "esign_route", id.ToString(), After: new { req.Reason }), ct);
        return Results.Ok(new { id, active = false });
    }
}
