using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Bodies;

public sealed class BodiesModule : IModule
{
    public string Code => "BOD";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<IBodyMembershipQuery, BodyMembershipQuery>();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/bodies").WithTags("Bodies");
        g.MapGet("/", List);
        g.MapPost("/", Create).Validate<CreateBodyRequest>();
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update).Validate<UpdateBodyRequest>();
        g.MapPost("/{id:guid}/activate", Activate);
        g.MapGet("/{id:guid}/members", Members);
        g.MapPost("/{id:guid}/members", AddMember).Validate<AddMemberRequest>();
        g.MapPost("/{id:guid}/members/{memberId:guid}/end", EndMember).Validate<EndMemberRequest>();
        g.MapGet("/{id:guid}/my-permissions", MyPermissions);
    }

    public sealed record CreateBodyRequest(
        [property: Required, RegularExpression("^[A-Z][A-Z0-9-]{1,19}$", ErrorMessage = "الرمز 2–20 حرفًا لاتينيًا كبيرًا يبدأ بحرف")] string Code,
        [property: Required, OneOf("council", "committee", "sub_committee", "working_team")] string BodyType,
        [property: Required, MaxLength(300)] string NameAr,
        [property: MaxLength(300)] string? NameEn,
        Guid? ParentBodyId,
        [property: MaxLength(5000)] string? MandateAr,
        [property: MaxLength(100)] string? FormationDecisionNo,
        DateOnly? FormationDecisionDate,
        DateOnly? TermStart,
        DateOnly? TermEnd,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Classification,
        Guid? OwnerOrgUnitId) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (BodyType == "working_team" && TermEnd is null)
                yield return new ValidationResult("فريق العمل يتطلب تاريخ انتهاء", [nameof(TermEnd)]);
            if (TermStart is { } s && TermEnd is { } e && e < s)
                yield return new ValidationResult("تاريخ الانتهاء قبل تاريخ البدء", [nameof(TermEnd)]);
        }
    }

    public sealed record UpdateBodyRequest(
        [property: MaxLength(300)] string? NameAr,
        [property: MaxLength(300)] string? NameEn,
        [property: MaxLength(5000)] string? MandateAr,
        DateOnly? TermEnd,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Classification);

    public sealed record AddMemberRequest(
        [property: Required] Guid? UserId,
        [property: Required, OneOf("chair", "vice_chair", "secretary", "member", "observer")] string Role,
        bool? IsVoting,
        Guid? RepresentsEntityId,
        DateOnly? StartDate,
        DateOnly? EndDate,
        [property: MaxLength(200)] string? AppointmentRef);

    public sealed record EndMemberRequest(DateOnly? EndDate, [property: Required, MinLength(3), MaxLength(500)] string Reason);

    private static object MapBody(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), code = r.Get<string>("code"), bodyType = r.Get<string>("body_type"), nameAr = r.Get<string>("name_ar"),
        nameEn = r.Str("name_en"), parentBodyId = r.Uuid("parent_body_id"), mandateAr = r.Str("mandate_ar"),
        formationDecisionNo = r.Str("formation_decision_no"), formationDecisionDate = r.Date("formation_decision_date"),
        termStart = r.Date("term_start"), termEnd = r.Date("term_end"), classification = r.Get<string>("classification"),
        ownerOrgUnitId = r.Uuid("owner_org_unit_id"), status = r.Get<string>("status"), createdAt = r.Get<DateTime>("created_at"),
        chairName = r.Str("chair_name"), secretaryName = r.Str("secretary_name"), memberCount = r.Get<long>("member_count"),
    };

    private const string BodySelect = """
        SELECT b.*,
          (SELECT u.display_name_ar FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id WHERE m.body_id = b.id AND m.status = 'active' AND m.role = 'chair' LIMIT 1) AS chair_name,
          (SELECT u.display_name_ar FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id WHERE m.body_id = b.id AND m.status = 'active' AND m.role = 'secretary' LIMIT 1) AS secretary_name,
          (SELECT count(*) FROM bodies.body_members m WHERE m.body_id = b.id AND m.status = 'active') AS member_count
        FROM bodies.bodies b
        """;

    private static async Task<IResult> List(string? q, string? type, string? status, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, q = SqlText.Like(q), type, status, limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR b.id = ANY(@ids::uuid[]))
              AND (@q::text IS NULL OR b.name_ar ILIKE @q OR b.name_en ILIKE @q OR b.code ILIKE @q)
              AND (@type::text IS NULL OR b.body_type = @type)
              AND (@status::text IS NULL OR b.status = @status)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM bodies.bodies b " + "\n" + where, args, ct);
        var items = await db.QueryAsync(BodySelect + "\n" + where + " ORDER BY b.name_ar LIMIT @limit OFFSET @offset", args, MapBody, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static readonly object DefaultQuorum = new { basis = "voting_members", type = "majority", chairRequired = false, countRemote = true, countProxies = false, conflictReducesBase = true, allowOverride = false };
    private static readonly object DefaultVoting = new { threshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = true };
    private static readonly object DefaultSignoff = new { required = false, signers = "present_voting_members_except_chair", methods = new[] { "attestation", "wet_signature_evidence" }, chairEsignOnApproval = false };
    private static readonly object DefaultEscalation = new
    {
        workingDays = true,
        levels = new object[]
        {
            new { level = 0, trigger = "due_soon", offsetDays = -3, notify = new[] { "assignee", "accountable" } },
            new { level = 1, trigger = "overdue", offsetDays = 0, notify = new[] { "assignee", "accountable", "secretary" } },
            new { level = 2, trigger = "overdue_l2", offsetDays = 7, notify = new[] { "assignee", "accountable", "secretary", "assignee_org_unit_manager" } },
            new { level = 3, trigger = "overdue_l3", offsetDays = 14, notify = new[] { "secretary", "chair" } },
        },
        repeatOverdueEveryDays = 7,
    };

    private static async Task<IResult> Create(CreateBodyRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodiesCreate, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                INSERT INTO bodies.bodies (id, code, body_type, name_ar, name_en, parent_body_id, mandate_ar, formation_decision_no, formation_decision_date,
                  term_start, term_end, classification, owner_org_unit_id, created_by, updated_by)
                VALUES (@id, @Code, @BodyType, @NameAr, @NameEn, @ParentBodyId, @MandateAr, @FormationDecisionNo, @FormationDecisionDate,
                  @TermStart, @TermEnd, @classification, @OwnerOrgUnitId, @by, @by)
                """, new
            {
                id, req.Code, req.BodyType, req.NameAr, req.NameEn, req.ParentBodyId, req.MandateAr, req.FormationDecisionNo, req.FormationDecisionDate,
                req.TermStart, req.TermEnd, classification = req.Classification ?? "restricted", req.OwnerOrgUnitId, by = me.UserId,
            }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_policies (id, body_id, version, effective_from, quorum_rule, voting_rule, signoff_rule, escalation_rule, created_by)
                VALUES (@pid, @id, 1, @from, @q::jsonb, @v::jsonb, @s::jsonb, @e::jsonb, @by)
                """, new
            {
                pid = Guid.CreateVersion7(), id, from = req.TermStart ?? clock.TodayLocal, q = Json.Serialize(DefaultQuorum), v = Json.Serialize(DefaultVoting),
                s = Json.Serialize(DefaultSignoff), e = Json.Serialize(DefaultEscalation), by = me.UserId,
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.created", "body", id.ToString(), After: req, Classification: req.Classification), ct);
        }, ct);
        return Results.Created($"/api/v1/bodies/{id}", await db.QuerySingleAsync(BodySelect + " WHERE b.id = @id", new { id }, MapBody, ct));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        var body = await db.QuerySingleAsync(BodySelect + " WHERE b.id = @id", new { id }, MapBody, ct) ?? throw new NotFoundException();
        return Results.Ok(new { body, permissions = (await access.GetPermissionsAsync(Scope.Body(id), ct)).Order() });
    }

    private static async Task<IResult> Update(Guid id, UpdateBodyRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyUpdate, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var before = await tx.QuerySingleAsync("SELECT name_ar, mandate_ar, term_end, classification, status FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id },
                r => new { nameAr = r.GetString(0), mandateAr = r.Str("mandate_ar"), termEnd = r.Date("term_end"), classification = r.GetString(3), status = r.GetString(4) }, ct)
                ?? throw new NotFoundException();
            if (before.status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "لا يمكن تعديل جهة منحلة أو مؤرشفة");
            await tx.ExecuteAsync("""
                UPDATE bodies.bodies SET name_ar = COALESCE(@NameAr, name_ar), name_en = COALESCE(@NameEn, name_en),
                  mandate_ar = COALESCE(@MandateAr, mandate_ar), term_end = COALESCE(@TermEnd, term_end),
                  classification = COALESCE(@Classification, classification), updated_by = @by
                WHERE id = @id
                """, new { id, req.NameAr, req.NameEn, req.MandateAr, req.TermEnd, req.Classification, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.updated", "body", id.ToString(), Before: before, After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(BodySelect + " WHERE b.id = @id", new { id }, MapBody, ct));
    }

    private static async Task<IResult> Activate(Guid id, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is not ("draft" or "suspended")) throw new ConflictException("invalid-transition", "لا يمكن تفعيل الجهة من حالتها الحالية");
            var counts = await tx.QuerySingleAsync("""
                SELECT count(*) FILTER (WHERE role = 'chair'), count(*) FILTER (WHERE role = 'secretary')
                FROM bodies.body_members WHERE body_id = @id AND status = 'active'
                """, new { id }, r => (Chairs: r.GetInt64(0), Secretaries: r.GetInt64(1)), ct);
            if (counts.Chairs != 1 || counts.Secretaries < 1)
                throw new ConflictException("formation-incomplete", "تفعيل الجهة يتطلب رئيسًا واحدًا وأمين سر واحدًا على الأقل (FR-BOD-04)",
                    new Dictionary<string, object?> { ["chairs"] = counts.Chairs, ["secretaries"] = counts.Secretaries });
            await tx.ExecuteAsync("UPDATE bodies.bodies SET status = 'active' WHERE id = @id", new { id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.activated", "body", id.ToString(), Before: new { status }, After: new { status = "active" }), ct);
        }, ct);
        return Results.Ok(new { id, status = "active" });
    }

    private static async Task<IResult> Members(Guid id, bool? includeEnded, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT m.id, m.user_id, u.display_name_ar, u.display_name_en, u.email, m.role, m.is_voting, m.represents_entity_id, e.name_ar AS entity_name,
                   m.start_date, m.end_date, m.status
            FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id LEFT JOIN org.external_entities e ON e.id = m.represents_entity_id
            WHERE m.body_id = @id AND (@all::boolean OR m.status = 'active')
            ORDER BY array_position(ARRAY['chair','vice_chair','secretary','member','observer'], m.role), u.display_name_ar
            """, new { id, all = includeEnded ?? false }, r => new
        {
            id = r.Get<Guid>("id"), userId = r.Get<Guid>("user_id"), displayNameAr = r.Get<string>("display_name_ar"), displayNameEn = r.Str("display_name_en"),
            email = r.Get<string>("email"), role = r.Get<string>("role"), isVoting = r.Get<bool>("is_voting"), representsEntityId = r.Uuid("represents_entity_id"),
            entityName = r.Str("entity_name"), startDate = r.Date("start_date"), endDate = r.Date("end_date"), status = r.Get<string>("status"),
        }, ct));
    }

    private static async Task<IResult> AddMember(Guid id, AddMemberRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INotifier notifier, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var memberId = Guid.CreateVersion7();
        var isVoting = req.IsVoting ?? req.Role is "chair" or "vice_chair" or "member";
        if (req.Role is "secretary" or "observer" && isVoting)
            throw AppValidationException.For("isVoting", "أمين السر والمدعو/المراقب لا يصوتون (06 §2.2)");
        await db.InTransactionAsync(async tx =>
        {
            var body = await tx.QuerySingleAsync("SELECT status, name_ar FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Name: r.GetString(1)), ct);
            if (body == default) throw new NotFoundException();
            if (body.Status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "الجهة منحلة أو مؤرشفة");
            var userOk = await tx.ScalarAsync<int>("SELECT count(*) FROM iam.users WHERE id = @uid AND status = 'active'", new { uid = req.UserId }, ct);
            if (userOk == 0) throw AppValidationException.For("userId", "المستخدم غير موجود أو معطل");
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, represents_entity_id, start_date, end_date, appointment_ref, created_by)
                VALUES (@memberId, @id, @UserId, @Role, @isVoting, @RepresentsEntityId, @start, @EndDate, @AppointmentRef, @by)
                """, new { memberId, id, req.UserId, req.Role, isVoting, req.RepresentsEntityId, start = req.StartDate ?? clock.TodayLocal, req.EndDate, req.AppointmentRef, by = me.UserId }, ct);
            await notifier.NotifyAsync(tx, new NotificationRequest(req.UserId!.Value, "membership_added",
                $"أُضفت إلى {body.Name}", $"You were added to {body.Name}", $"/bodies/{id}", $"membership:{memberId}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.member_added", "body", id.ToString(), After: new { memberId, req.UserId, req.Role, isVoting }), ct);
        }, ct);
        access.Invalidate(req.UserId!.Value);
        return Results.Created($"/api/v1/bodies/{id}/members/{memberId}", new { id = memberId });
    }

    private static async Task<IResult> EndMember(Guid id, Guid memberId, EndMemberRequest req, Db db, IAccessService access, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var end = req.EndDate ?? clock.TodayLocal;
        var n = await db.InTransactionAsync(async tx =>
        {
            var updated = await tx.ExecuteAsync("""
                UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @end)
                WHERE id = @memberId AND body_id = @id AND status = 'active'
                """, new { memberId, id, end }, ct);
            if (updated > 0)
                await audit.WriteAsync(tx, new AuditEntry("BOD", "body.member_ended", "body", id.ToString(), After: new { memberId, end, req.Reason }), ct);
            return updated;
        }, ct);
        return n == 0 ? throw new NotFoundException() : Results.NoContent();
    }

    private static async Task<IResult> MyPermissions(Guid id, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        return Results.Ok((await access.GetPermissionsAsync(Scope.Body(id), ct)).Order());
    }
}

internal sealed class BodyMembershipQuery(Db db) : IBodyMembershipQuery
{
    public async Task<IReadOnlyList<BodyRole>> GetActiveRolesAsync(Guid userId, DateOnly on, CancellationToken ct = default) =>
        await db.QueryAsync("""
            SELECT m.body_id, m.role FROM bodies.body_members m JOIN bodies.bodies b ON b.id = m.body_id
            WHERE m.user_id = @userId AND m.status = 'active' AND m.start_date <= @on AND (m.end_date IS NULL OR m.end_date >= @on)
              AND b.status <> 'archived'
            """, new { userId, on }, r => new BodyRole(r.GetGuid(0), r.GetString(1)), ct);
}
