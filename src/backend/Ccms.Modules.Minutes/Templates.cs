using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Minutes;

/// <summary>قوالب المحاضر القابلة للتهيئة: ترتيب الأقسام، والنصية منها بنص افتراضي، والآلية بمصدر بياناتها.</summary>
internal static class Templates
{
    public sealed record SectionRequest(
        [property: Required, RegularExpression("^[a-z][a-z0-9_]{1,39}$", ErrorMessage = "مفتاح القسم: أحرف لاتينية صغيرة وأرقام")] string Key,
        [property: Required, MaxLength(200)] string TitleAr,
        [property: MaxLength(200)] string? TitleEn,
        [property: Required, OneOf("auto", "text")] string Kind,
        [property: OneOf("meeting_info", "attendance", "quorum", "agenda", "discussions", "decisions", "votes", "reservations", "tasks", "attachments", "approval")] string? Source,
        [property: MaxLength(5000)] string? DefaultBody);

    public sealed record TemplateRequest(
        [property: Required, RegularExpression("^[a-z][a-z0-9_-]{1,39}$", ErrorMessage = "الرمز: أحرف لاتينية صغيرة وأرقام")] string Code,
        [property: Required, MaxLength(200)] string NameAr,
        [property: MaxLength(200)] string? NameEn,
        [property: Required, MinLength(1), MaxLength(40)] List<SectionRequest> Sections,
        bool IsDefault,
        [property: OneOf("active", "inactive")] string? Status) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            foreach (var (s, i) in Sections.Select((s, i) => (s, i)))
            {
                foreach (var r in ValidationHelper.Validate(s))
                    yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"sections[{i}].{m}"));
                if (s.Kind == "auto" && s.Source is null) yield return new("القسم الآلي يحتاج مصدر بيانات", [$"sections[{i}].source"]);
            }
            if (Sections.Select(s => s.Key).Distinct().Count() != Sections.Count) yield return new("مفاتيح الأقسام مكررة", [nameof(Sections)]);
            if (!Sections.Any(s => s.Source == "approval")) yield return new("يجب أن يتضمن القالب قسم بيانات المراجعة والاعتماد", [nameof(Sections)]);
            if (IsDefault && Status == "inactive") yield return new("القالب الافتراضي لا يكون معطلًا", [nameof(IsDefault)]);
        }
    }

    public static async Task<List<TemplateSection>> GetSectionsAsync(ISql sql, Guid templateId, CancellationToken ct)
    {
        var json = await sql.ScalarAsync<string>("SELECT sections::text FROM minutes.templates WHERE id = @templateId", new { templateId }, ct) ?? "[]";
        return JsonSerializer.Deserialize<List<TemplateSection>>(json, MinutesContent.Options) ?? [];
    }

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.GetGuid(0), code = r.GetString(1), nameAr = r.GetString(2), nameEn = r.Str("name_en"),
        sections = JsonSerializer.Deserialize<JsonElement>(r.GetString(4)), isDefault = r.GetBoolean(5), status = r.GetString(6), version = r.GetInt32(7),
        updatedAt = r.GetDateTime(8), usage = r.GetInt64(9),
    };

    private const string Select = """
        SELECT t.id, t.code, t.name_ar, t.name_en, t.sections::text, t.is_default, t.status, t.version, t.updated_at,
               (SELECT count(*) FROM minutes.minutes mi WHERE mi.template_id = t.id) AS usage
        FROM minutes.templates t
        """;

    public static async Task<IResult> List(Db db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        return Results.Ok(await db.QueryAsync(Select + " ORDER BY t.is_default DESC, t.name_ar", null, Map, ct));
    }

    private static string SectionsJson(TemplateRequest req) => JsonSerializer.Serialize(
        req.Sections.Select(s => new TemplateSection(s.Key, s.TitleAr, s.TitleEn, s.Kind, s.Kind == "auto" ? s.Source : null, s.Kind == "text" ? s.DefaultBody : null)),
        MinutesContent.Options);

    public static async Task<IResult> Create(TemplateRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesTemplates, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            if (req.IsDefault) await tx.ExecuteAsync("UPDATE minutes.templates SET is_default = false WHERE is_default", null, ct);
            await tx.ExecuteAsync("""
                INSERT INTO minutes.templates (id, code, name_ar, name_en, sections, is_default, status, created_by, updated_by)
                VALUES (@id, @Code, @NameAr, @NameEn, @sections::jsonb, @IsDefault, @status, @me, @me)
                """, new { id, req.Code, req.NameAr, req.NameEn, sections = SectionsJson(req), req.IsDefault, status = req.Status ?? "active", me = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.template_created", "minutes_template", id.ToString(), After: new { req.Code, req.NameAr, sections = req.Sections.Count }), ct);
        }, ct);
        return Results.Created($"/api/v1/minutes/templates/{id}", await db.QuerySingleAsync(Select + " WHERE t.id = @id", new { id }, Map, ct));
    }

    /// <summary>تعديل القالب يرفع رقم إصداره؛ المحاضر المعتمدة لا تتأثر لأن محتواها ولقطتها مجمّدان في إصداراتها.</summary>
    public static async Task<IResult> Update(Guid templateId, TemplateRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesTemplates, Scope.System, ct);
        await db.InTransactionAsync(async tx =>
        {
            var current = await tx.QuerySingleAsync("SELECT is_default, sections::text FROM minutes.templates WHERE id = @templateId FOR UPDATE", new { templateId },
                r => (Default: r.GetBoolean(0), Sections: r.GetString(1)), ct);
            if (current == default) throw new NotFoundException();
            if (current.Default && !req.IsDefault) throw new ConflictException("default-required", "عيّن قالبًا افتراضيًا آخر بدل إلغاء الافتراضي");
            if (req.IsDefault) await tx.ExecuteAsync("UPDATE minutes.templates SET is_default = false WHERE is_default AND id <> @templateId", new { templateId }, ct);
            await tx.ExecuteAsync("""
                UPDATE minutes.templates SET code = @Code, name_ar = @NameAr, name_en = @NameEn, sections = @sections::jsonb, is_default = @IsDefault,
                  status = @status, version = version + 1, updated_by = @me WHERE id = @templateId
                """, new { templateId, req.Code, req.NameAr, req.NameEn, sections = SectionsJson(req), req.IsDefault, status = req.Status ?? "active", me = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.template_updated", "minutes_template", templateId.ToString(),
                Before: new { sections = current.Sections }, After: new { req.Code, req.NameAr, sections = req.Sections.Count, req.IsDefault, req.Status }), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + " WHERE t.id = @templateId", new { templateId }, Map, ct));
    }
}
