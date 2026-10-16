using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Bodies;

/// <summary>المجالس واللجان وفرق العمل: الأنواع، والرقم المرجعي، والمدة وتجديدها وتمديدها، والإنهاء والأرشفة دون حذف (BRL-12).</summary>
public sealed class BodiesModule : IModule
{
    public string Code => "BOD";

    public static readonly string[] Types =
        ["supreme_council", "steering_council", "executive_council", "permanent_committee", "temporary_committee", "working_team", "sub_committee", "joint_committee"];
    public static readonly string[] Frequencies = ["weekly", "biweekly", "monthly", "quarterly", "semiannual", "annual", "as_needed"];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IBodyMembershipQuery, BodyMembershipQuery>();
        services.AddScoped<IBodyPolicyQuery, BodyPolicyQuery>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/bodies").WithTags("Bodies");
        g.MapGet("/", List);
        g.MapPost("/", Create).Validate<CreateBodyRequest>();
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update).Validate<UpdateBodyRequest>();
        g.MapPut("/{id:guid}/partners", SetPartners).Validate<PartnersRequest>();
        g.MapPost("/{id:guid}/activate", Activate);
        g.MapPost("/{id:guid}/suspend", (Guid id, ReasonRequest req, Db db, IAccessService a, IAuditWriter au, CancellationToken ct) =>
            Transition(id, req, ["active"], "suspended", "body.suspended", db, a, au, ct)).Validate<ReasonRequest>();
        g.MapPost("/{id:guid}/resume", (Guid id, ReasonRequest req, Db db, IAccessService a, IAuditWriter au, CancellationToken ct) =>
            Transition(id, req, ["suspended"], "active", "body.resumed", db, a, au, ct)).Validate<ReasonRequest>();
        g.MapPost("/{id:guid}/renew", Renew).Validate<TermRequest>();
        g.MapPost("/{id:guid}/extend", Extend).Validate<TermRequest>();
        g.MapPost("/{id:guid}/terminate", Terminate).Validate<TerminateRequest>();
        g.MapPost("/{id:guid}/archive", (Guid id, ReasonRequest req, Db db, IAccessService a, IAuditWriter au, CancellationToken ct) =>
            Transition(id, req, ["dissolved"], "archived", "body.archived", db, a, au, ct)).Validate<ReasonRequest>();
        g.MapGet("/{id:guid}/policy", PolicyEndpoints.Get);
        g.MapPut("/{id:guid}/policy", PolicyEndpoints.Update).Validate<PolicyRequest>();
        g.MapGet("/{id:guid}/activity", Activity);
        Memberships.Map(api, g);
    }

    // ---------------- الطلبات ----------------

    public sealed record CreateBodyRequest(
        [property: Required, RegularExpression("^[A-Z][A-Z0-9-]{1,19}$", ErrorMessage = "الرمز 2–20 حرفًا لاتينيًا كبيرًا يبدأ بحرف")] string Code,
        [property: Required, OneOf("supreme_council", "steering_council", "executive_council", "permanent_committee", "temporary_committee", "working_team", "sub_committee", "joint_committee")] string BodyType,
        [property: Required, MaxLength(300)] string NameAr,
        [property: Required(ErrorMessage = "الاسم بالإنجليزية مطلوب"), MaxLength(300)] string NameEn,
        Guid? ParentBodyId,
        [property: MaxLength(3000)] string? ObjectiveAr,
        [property: MaxLength(10000)] string? MandateAr,
        [property: MaxLength(500)] string? AuthorityRef,
        [property: MaxLength(100)] string? FormationDecisionNo,
        DateOnly? FormationDecisionDate,
        DateOnly? TermStart,
        DateOnly? TermEnd,
        [property: OneOf("weekly", "biweekly", "monthly", "quarterly", "semiannual", "annual", "as_needed")] string? MeetingFrequency,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Classification,
        Guid? OwnerOrgUnitId,
        Guid[]? PartnerEntityIds) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (BodyType is "temporary_committee" or "working_team" && TermEnd is null)
                yield return new("اللجنة المؤقتة وفريق العمل يتطلبان تاريخ انتهاء", [nameof(TermEnd)]);
            if (BodyType == "sub_committee" && ParentBodyId is null)
                yield return new("اللجنة الفرعية تتطلب تحديد المجلس أو اللجنة الرئيسية", [nameof(ParentBodyId)]);
            if (BodyType == "joint_committee" && (PartnerEntityIds is null || PartnerEntityIds.Length == 0))
                yield return new("اللجنة المشتركة تتطلب جهة خارجية شريكة واحدة على الأقل", [nameof(PartnerEntityIds)]);
            if (TermStart is { } s && TermEnd is { } e && e < s)
                yield return new("تاريخ الانتهاء قبل تاريخ البدء", [nameof(TermEnd)]);
        }
    }

    public sealed record UpdateBodyRequest(
        [property: MaxLength(300)] string? NameAr,
        [property: MaxLength(300)] string? NameEn,
        [property: MaxLength(3000)] string? ObjectiveAr,
        [property: MaxLength(10000)] string? MandateAr,
        [property: MaxLength(500)] string? AuthorityRef,
        [property: MaxLength(100)] string? FormationDecisionNo,
        DateOnly? FormationDecisionDate,
        [property: OneOf("weekly", "biweekly", "monthly", "quarterly", "semiannual", "annual", "as_needed")] string? MeetingFrequency,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Classification);

    public sealed record PartnersRequest([property: Required] Guid[] EntityIds);
    public sealed record ReasonRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record TermRequest(
        DateOnly? TermStart,
        [property: Required] DateOnly? TermEnd,
        [property: MaxLength(100)] string? DecisionNo,
        DateOnly? DecisionDate,
        [property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record TerminateRequest(
        DateOnly? EffectiveDate,
        [property: MaxLength(100)] string? DecisionNo,
        [property: Required(ErrorMessage = "سبب الإنهاء مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    // ---------------- القراءة ----------------

    internal const string Select = """
        SELECT b.*, p.name_ar AS parent_name, o.name_ar AS owner_org_unit_name,
          (SELECT u.display_name_ar FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id WHERE m.body_id = b.id AND m.status = 'active' AND m.role = 'chair' LIMIT 1) AS chair_name,
          (SELECT u.display_name_ar FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id WHERE m.body_id = b.id AND m.status = 'active' AND m.role = 'vice_chair' LIMIT 1) AS vice_chair_name,
          (SELECT u.display_name_ar FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id WHERE m.body_id = b.id AND m.status = 'active' AND m.role = 'secretary' LIMIT 1) AS secretary_name,
          (SELECT count(*) FROM bodies.body_members m WHERE m.body_id = b.id AND m.status = 'active') AS member_count,
          (SELECT max(mt.starts_at) FROM meetings.meetings mt WHERE mt.body_id = b.id AND mt.status IN ('concluded','closed')) AS last_meeting_at,
          (SELECT min(mt.starts_at) FROM meetings.meetings mt WHERE mt.body_id = b.id AND mt.status IN ('scheduled','agenda_published') AND mt.starts_at >= now()) AS next_meeting_at
        FROM bodies.bodies b
        LEFT JOIN bodies.bodies p ON p.id = b.parent_body_id
        LEFT JOIN org.org_units o ON o.id = b.owner_org_unit_id
        """;

    internal static object Map(DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), referenceNo = r.Get<string>("reference_no"), code = r.Get<string>("code"), bodyType = r.Get<string>("body_type"),
        nameAr = r.Get<string>("name_ar"), nameEn = r.Str("name_en"), parentBodyId = r.Uuid("parent_body_id"), parentName = r.Str("parent_name"),
        objectiveAr = r.Str("objective_ar"), mandateAr = r.Str("mandate_ar"), authorityRef = r.Str("authority_ref"),
        formationDecisionNo = r.Str("formation_decision_no"), formationDecisionDate = r.Date("formation_decision_date"),
        termStart = r.Date("term_start"), termEnd = r.Date("term_end"), meetingFrequency = r.Get<string>("meeting_frequency"),
        classification = r.Get<string>("classification"), ownerOrgUnitId = r.Uuid("owner_org_unit_id"), ownerOrgUnitName = r.Str("owner_org_unit_name"),
        status = r.Get<string>("status"), statusReason = r.Str("status_reason"), createdAt = r.Get<DateTime>("created_at"),
        chairName = r.Str("chair_name"), viceChairName = r.Str("vice_chair_name"), secretaryName = r.Str("secretary_name"),
        memberCount = r.Get<long>("member_count"), lastMeetingAt = r.Time("last_meeting_at"), nextMeetingAt = r.Time("next_meeting_at"),
    };

    private static async Task<IResult> List(string? q, string? type, string? status, Guid? parentId, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, q = SqlText.Like(q), type, status, parentId, limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR b.id = ANY(@ids::uuid[]))
              AND (@q::text IS NULL OR b.name_ar ILIKE @q OR b.name_en ILIKE @q OR b.code ILIKE @q OR b.reference_no ILIKE @q)
              AND (@type::text IS NULL OR b.body_type = @type)
              AND (@status::text IS NULL OR b.status = @status)
              AND (@parentId::uuid IS NULL OR b.parent_body_id = @parentId)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM bodies.bodies b\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY b.status = 'archived', b.name_ar LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        var body = await db.QuerySingleAsync(Select + "\nWHERE b.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var partners = await db.QueryAsync("""
            SELECT e.id, e.name_ar, e.name_en FROM bodies.body_partner_entities p JOIN org.external_entities e ON e.id = p.external_entity_id
            WHERE p.body_id = @id ORDER BY e.name_ar
            """, new { id }, r => new { id = r.GetGuid(0), nameAr = r.GetString(1), nameEn = r.Str("name_en") }, ct);
        var filter = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var children = await db.QueryAsync("""
            SELECT id, reference_no, name_ar, body_type, status FROM bodies.bodies
            WHERE parent_body_id = @id AND (@all::boolean OR id = ANY(@ids::uuid[])) ORDER BY name_ar
            """, new { id, all = filter.All, ids = filter.BodyIds }, r => new
        {
            id = r.GetGuid(0), referenceNo = r.GetString(1), nameAr = r.GetString(2), bodyType = r.GetString(3), status = r.GetString(4),
        }, ct);
        var terms = await db.QueryAsync("""
            SELECT t.change_type, t.term_start, t.term_end, t.decision_no, t.decision_date, t.reason, t.created_at, u.display_name_ar
            FROM bodies.body_terms t LEFT JOIN iam.users u ON u.id = t.created_by WHERE t.body_id = @id ORDER BY t.created_at DESC
            """, new { id }, r => new
        {
            changeType = r.GetString(0), termStart = r.Date("term_start"), termEnd = r.Date("term_end"), decisionNo = r.Str("decision_no"),
            decisionDate = r.Date("decision_date"), reason = r.Str("reason"), createdAt = r.GetDateTime(6), createdBy = r.Str("display_name_ar"),
        }, ct);
        return Results.Ok(new { body, partners, children, terms, permissions = (await access.GetPermissionsAsync(Scope.Body(id), ct)).Order() });
    }

    private static async Task<IResult> Activity(Guid id, IAccessService access, IActivityQuery activity, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        return Results.Ok(await activity.ForEntityAsync("body", id, 300, ct));
    }

    // ---------------- الإنشاء والتعديل ----------------

    private static async Task<IResult> Create(CreateBodyRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IClock clock,
        INumberingService numbering, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodiesCreate, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            if (req.ParentBodyId is { } parent)
            {
                var ps = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @parent", new { parent }, ct)
                    ?? throw AppValidationException.For("parentBodyId", "الجهة الرئيسية غير موجودة");
                if (ps is "dissolved" or "archived") throw AppValidationException.For("parentBodyId", "الجهة الرئيسية منتهية");
            }
            var year = clock.TodayLocal.Year;
            var seq = await numbering.NextAsync(tx, "body", year, ct);
            var refNo = $"BOD-{year}-{seq:0000}";
            await tx.ExecuteAsync("""
                INSERT INTO bodies.bodies (id, reference_no, code, body_type, name_ar, name_en, parent_body_id, objective_ar, mandate_ar, authority_ref,
                  formation_decision_no, formation_decision_date, term_start, term_end, meeting_frequency, classification, owner_org_unit_id, created_by, updated_by)
                VALUES (@id, @refNo, @Code, @BodyType, @NameAr, @NameEn, @ParentBodyId, @ObjectiveAr, @MandateAr, @AuthorityRef,
                  @FormationDecisionNo, @FormationDecisionDate, @TermStart, @TermEnd, @freq, @cls, @OwnerOrgUnitId, @by, @by)
                """, new
            {
                id, refNo, req.Code, req.BodyType, req.NameAr, req.NameEn, req.ParentBodyId, req.ObjectiveAr, req.MandateAr, req.AuthorityRef,
                req.FormationDecisionNo, req.FormationDecisionDate, req.TermStart, req.TermEnd, freq = req.MeetingFrequency ?? "monthly",
                cls = req.Classification ?? "restricted", req.OwnerOrgUnitId, by = me.UserId,
            }, ct);
            foreach (var e in (req.PartnerEntityIds ?? []).Distinct())
                await tx.ExecuteAsync("INSERT INTO bodies.body_partner_entities (body_id, external_entity_id) VALUES (@id, @e)", new { id, e }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_terms (id, body_id, change_type, term_start, term_end, decision_no, decision_date, created_by)
                VALUES (@tid, @id, 'initial', @TermStart, @TermEnd, @FormationDecisionNo, @FormationDecisionDate, @by)
                """, new { tid = Guid.CreateVersion7(), id, req.TermStart, req.TermEnd, req.FormationDecisionNo, req.FormationDecisionDate, by = me.UserId }, ct);
            await PolicyEndpoints.InsertDefaultAsync(tx, id, req.TermStart ?? clock.TodayLocal, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.created", "body", id.ToString(), After: new { refNo, req.Code, req.BodyType, req.NameAr }, Classification: req.Classification), ct);
        }, ct);
        return Results.Created($"/api/v1/bodies/{id}", await db.QuerySingleAsync(Select + "\nWHERE b.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Update(Guid id, UpdateBodyRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyUpdate, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var before = await tx.QuerySingleAsync("SELECT name_ar, objective_ar, mandate_ar, meeting_frequency, classification, status FROM bodies.bodies WHERE id = @id FOR UPDATE",
                new { id }, r => new { nameAr = r.GetString(0), objectiveAr = r.Str("objective_ar"), mandateAr = r.Str("mandate_ar"), frequency = r.GetString(3), classification = r.GetString(4), status = r.GetString(5) }, ct)
                ?? throw new NotFoundException();
            if (before.status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "لا تُعدل جهة منتهية أو مؤرشفة");
            await tx.ExecuteAsync("""
                UPDATE bodies.bodies SET name_ar = COALESCE(@NameAr, name_ar), name_en = COALESCE(@NameEn, name_en), objective_ar = COALESCE(@ObjectiveAr, objective_ar),
                  mandate_ar = COALESCE(@MandateAr, mandate_ar), authority_ref = COALESCE(@AuthorityRef, authority_ref),
                  formation_decision_no = COALESCE(@FormationDecisionNo, formation_decision_no), formation_decision_date = COALESCE(@FormationDecisionDate, formation_decision_date),
                  meeting_frequency = COALESCE(@MeetingFrequency, meeting_frequency), classification = COALESCE(@Classification, classification), updated_by = @by
                WHERE id = @id
                """, new { id, req.NameAr, req.NameEn, req.ObjectiveAr, req.MandateAr, req.AuthorityRef, req.FormationDecisionNo, req.FormationDecisionDate, req.MeetingFrequency, req.Classification, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.updated", "body", id.ToString(), Before: before, After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + "\nWHERE b.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> SetPartners(Guid id, PartnersRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var type = await tx.ScalarAsync<string>("SELECT body_type FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (type == "joint_committee" && req.EntityIds.Length == 0) throw AppValidationException.For("entityIds", "اللجنة المشتركة تتطلب جهة شريكة واحدة على الأقل");
            var before = await tx.QueryAsync("SELECT external_entity_id FROM bodies.body_partner_entities WHERE body_id = @id", new { id }, r => r.GetGuid(0), ct);
            await tx.ExecuteAsync("DELETE FROM bodies.body_partner_entities WHERE body_id = @id AND NOT (external_entity_id = ANY(@ids::uuid[]))", new { id, ids = req.EntityIds }, ct);
            foreach (var e in req.EntityIds.Distinct())
                await tx.ExecuteAsync("INSERT INTO bodies.body_partner_entities (body_id, external_entity_id) VALUES (@id, @e) ON CONFLICT DO NOTHING", new { id, e }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.partners_changed", "body", id.ToString(), Before: new { partners = before }, After: new { partners = req.EntityIds }), ct);
        }, ct);
        return Results.NoContent();
    }

    // ---------------- دورة الحياة ----------------

    private static async Task<IResult> Activate(Guid id, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var b = await tx.QuerySingleAsync("""
                SELECT b.status, b.body_type, p.status AS parent_status,
                  (SELECT count(*) FROM bodies.body_partner_entities x WHERE x.body_id = b.id) AS partners
                FROM bodies.bodies b LEFT JOIN bodies.bodies p ON p.id = b.parent_body_id WHERE b.id = @id FOR UPDATE OF b
                """, new { id }, r => (Status: r.GetString(0), Type: r.GetString(1), Parent: r.Str("parent_status"), Partners: r.GetInt64(3)), ct);
            if (b == default) throw new NotFoundException();
            if (b.Status != "draft") throw new ConflictException("invalid-transition", "لا تُفعّل إلا جهة في حالة المسودة");
            var counts = await tx.QuerySingleAsync("""
                SELECT count(*) FILTER (WHERE role = 'chair'), count(*) FILTER (WHERE role = 'secretary')
                FROM bodies.body_members WHERE body_id = @id AND status = 'active'
                """, new { id }, r => (Chairs: r.GetInt64(0), Secretaries: r.GetInt64(1)), ct);
            var problems = new Dictionary<string, object?>();
            if (counts.Chairs != 1 || counts.Secretaries < 1) problems["formation"] = "رئيس واحد وأمين سر واحد على الأقل";
            if (b.Type == "joint_committee" && b.Partners == 0) problems["partners"] = "جهة خارجية شريكة واحدة على الأقل";
            if (b.Type == "sub_committee" && b.Parent != "active") problems["parent"] = "الجهة الرئيسية يجب أن تكون نشطة";
            if (problems.Count > 0)
                throw new ConflictException("formation-incomplete", "تفعيل الجهة يتطلب استكمال التشكيل (FR-BOD-04)", new Dictionary<string, object?>
                { ["chairs"] = counts.Chairs, ["secretaries"] = counts.Secretaries, ["problems"] = problems });
            await tx.ExecuteAsync("UPDATE bodies.bodies SET status = 'active', status_reason = NULL WHERE id = @id", new { id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.activated", "body", id.ToString(), Before: new { status = b.Status }, After: new { status = "active" }), ct);
        }, ct);
        return Results.Ok(new { id, status = "active" });
    }

    private static async Task<IResult> Transition(Guid id, ReasonRequest req, string[] from, string to, string action, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (!from.Contains(status)) throw new ConflictException("invalid-transition", $"لا يمكن الانتقال من الحالة الحالية ({status})");
            await tx.ExecuteAsync("UPDATE bodies.bodies SET status = @to, status_reason = @Reason WHERE id = @id", new { id, to, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", action, "body", id.ToString(), Before: new { status }, After: new { status = to, req.Reason }), ct);
        }, ct);
        return Results.Ok(new { id, status = to });
    }

    /// <summary>تجديد: مدة جديدة بقرار جديد، وتبقى المدة السابقة في سجل المدد.</summary>
    private static async Task<IResult> Renew(Guid id, TermRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        if (req.TermStart is null) throw AppValidationException.For("termStart", "تاريخ بداية المدة الجديدة مطلوب");
        if (req.TermEnd < req.TermStart) throw AppValidationException.For("termEnd", "نهاية المدة قبل بدايتها");
        await ChangeTermAsync(id, "renewal", req, db, me, audit, ct, current =>
        {
            if (current.End is { } e && req.TermStart <= e) throw AppValidationException.For("termStart", "المدة الجديدة يجب أن تبدأ بعد نهاية المدة الحالية");
        });
        return Results.Ok(new { id, req.TermStart, req.TermEnd });
    }

    /// <summary>تمديد: تأخير تاريخ الانتهاء للمدة الحالية.</summary>
    private static async Task<IResult> Extend(Guid id, TermRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        await ChangeTermAsync(id, "extension", req with { TermStart = null }, db, me, audit, ct, current =>
        {
            if (current.End is { } e && req.TermEnd <= e) throw AppValidationException.For("termEnd", "تاريخ الانتهاء الجديد يجب أن يكون بعد الحالي");
        });
        return Results.Ok(new { id, req.TermEnd });
    }

    private static async Task ChangeTermAsync(Guid id, string type, TermRequest req, Db db, ICurrentUser me, IAuditWriter audit, CancellationToken ct,
        Action<(DateOnly? Start, DateOnly? End)> validate)
    {
        await db.InTransactionAsync(async tx =>
        {
            var b = await tx.QuerySingleAsync("SELECT status, term_start, term_end FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Start: r.Date("term_start"), End: r.Date("term_end")), ct);
            if (b == default) throw new NotFoundException();
            if (b.Status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "لا تُجدد ولا تُمدد جهة منتهية");
            validate((b.Start, b.End));
            await tx.ExecuteAsync("UPDATE bodies.bodies SET term_start = COALESCE(@TermStart::date, term_start), term_end = @TermEnd::date, updated_by = @by WHERE id = @id",
                new { id, req.TermStart, req.TermEnd, by = me.UserId }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_terms (id, body_id, change_type, term_start, term_end, decision_no, decision_date, reason, created_by)
                VALUES (@tid, @id, @type, COALESCE(@TermStart::date, @oldStart::date), @TermEnd::date, @DecisionNo, @DecisionDate::date, @Reason, @by)
                """, new { tid = Guid.CreateVersion7(), id, type, req.TermStart, oldStart = b.Start, req.TermEnd, req.DecisionNo, req.DecisionDate, req.Reason, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", type == "renewal" ? "body.renewed" : "body.extended", "body", id.ToString(),
                Before: new { termStart = b.Start, termEnd = b.End }, After: new { req.TermStart, req.TermEnd, req.DecisionNo, req.Reason }), ct);
        }, ct);
    }

    /// <summary>إنهاء الجهة: تُنهى العضويات النشطة بسبب موحد، وتُلغى الاجتماعات المستقبلية غير المنعقدة، دون حذف أي سجل.</summary>
    private static async Task<IResult> Terminate(Guid id, TerminateRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyLifecycle, Scope.Body(id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "الجهة منتهية مسبقًا");
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM meetings.meetings WHERE body_id = @id AND status = 'in_progress'", new { id }, ct) > 0)
                throw new ConflictException("meeting-in-progress", "يوجد اجتماع منعقد حاليًا؛ أغلقه قبل إنهاء الجهة");
            var effective = req.EffectiveDate ?? clock.TodayLocal;
            var reason = "إنهاء الجهة: " + req.Reason;
            var ended = await tx.ExecuteAsync("""
                UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective), end_reason = @reason, ended_by = @by
                WHERE body_id = @id AND status <> 'ended'
                """, new { id, effective, reason, by = me.UserId }, ct);
            var cancelled = await tx.ExecuteAsync("""
                UPDATE meetings.meetings SET status = 'cancelled', status_reason = @reason
                WHERE body_id = @id AND status IN ('draft','scheduled','agenda_published')
                """, new { id, reason }, ct);
            await tx.ExecuteAsync("UPDATE iam.delegations SET status = 'revoked', revoked_by = @by, revoked_at = now() WHERE body_id = @id AND status = 'active'", new { id, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE bodies.bodies SET status = 'dissolved', status_reason = @Reason, term_end = CASE WHEN term_start IS NOT NULL AND @effective::date < term_start THEN term_end ELSE LEAST(COALESCE(term_end, @effective::date), @effective::date) END WHERE id = @id",
                new { id, req.Reason, effective }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.terminated", "body", id.ToString(), Before: new { status },
                After: new { status = "dissolved", req.Reason, req.DecisionNo, effective, endedMemberships = ended, cancelledMeetings = cancelled }), ct);
            return new { id, status = "dissolved", endedMemberships = ended, cancelledMeetings = cancelled };
        }, ct);
        return Results.Ok(result);
    }
}

/// <summary>العضويات السارية في تاريخ معيّن، مضافًا إليها تفويض صلاحية الرئاسة الساري.</summary>
internal sealed class BodyMembershipQuery(Db db) : IBodyMembershipQuery
{
    public async Task<IReadOnlyList<BodyRole>> GetActiveRolesAsync(Guid userId, DateOnly on, CancellationToken ct = default) =>
        await db.QueryAsync("""
            SELECT m.body_id, m.role FROM bodies.body_members m JOIN bodies.bodies b ON b.id = m.body_id
            WHERE m.user_id = @userId AND m.status = 'active' AND m.start_date <= @on AND (m.end_date IS NULL OR m.end_date >= @on)
              AND b.status <> 'archived'
            UNION
            SELECT d.body_id, 'chair' FROM iam.delegations d JOIN bodies.bodies b ON b.id = d.body_id
            WHERE d.delegate_user_id = @userId AND d.scope = 'chair_authority' AND d.status = 'active'
              AND d.valid_from <= now() AND d.valid_to > now() AND b.status = 'active'
            """, new { userId, on }, r => new BodyRole(r.GetGuid(0), r.GetString(1)), ct);
}
