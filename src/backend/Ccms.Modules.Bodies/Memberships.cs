using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Bodies;

/// <summary>العضويات: الإضافة، والبدلاء، وتغيير الصفة، وتغيير الرئيس، والتعليق، والإنهاء، والتفويض.
/// أي تغيير ينهي سجل العضوية القائم ويُنشئ سجلًا جديدًا مرتبطًا به، فلا يُفقد التاريخ.</summary>
internal static class Memberships
{
    public static void Map(IEndpointRouteBuilder api, RouteGroupBuilder bodies)
    {
        bodies.MapGet("/{id:guid}/members", List);
        bodies.MapPost("/{id:guid}/members", Add).Validate<AddMemberRequest>();
        bodies.MapPost("/{id:guid}/members/{memberId:guid}/change", Change).Validate<ChangeMemberRequest>();
        bodies.MapPost("/{id:guid}/members/{memberId:guid}/end", End).Validate<EndMemberRequest>();
        bodies.MapPost("/{id:guid}/members/{memberId:guid}/suspend", (Guid id, Guid memberId, BodiesModule.ReasonRequest req, Db db, IAccessService a, IAuditWriter au, CancellationToken ct) =>
            SetStatus(id, memberId, "active", "suspended", req.Reason, "body.member_suspended", db, a, au, ct)).Validate<BodiesModule.ReasonRequest>();
        bodies.MapPost("/{id:guid}/members/{memberId:guid}/reactivate", (Guid id, Guid memberId, BodiesModule.ReasonRequest req, Db db, IAccessService a, IAuditWriter au, CancellationToken ct) =>
            SetStatus(id, memberId, "suspended", "active", req.Reason, "body.member_reactivated", db, a, au, ct)).Validate<BodiesModule.ReasonRequest>();
        bodies.MapPost("/{id:guid}/change-chair", ChangeChair).Validate<ChangeChairRequest>();
        bodies.MapGet("/{id:guid}/delegations", Delegations);
        bodies.MapPost("/{id:guid}/delegations", Delegate).Validate<DelegationRequest>();
        bodies.MapPost("/{id:guid}/delegations/{delegationId:guid}/revoke", Revoke);
        api.MapGet("/memberships", Global).WithTags("Bodies");
    }

    public sealed record AddMemberRequest(
        [property: Required] Guid? UserId,
        [property: Required, OneOf("chair", "vice_chair", "secretary", "member", "alternate", "observer")] string Role,
        bool? IsVoting,
        Guid? AlternateForMemberId,
        Guid? RepresentsEntityId,
        DateOnly? StartDate,
        DateOnly? EndDate,
        [property: MaxLength(200)] string? AppointmentRef) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Role == "alternate" && AlternateForMemberId is null) yield return new("حدد العضو الأصيل الذي ينوب عنه البديل", [nameof(AlternateForMemberId)]);
            if (StartDate is { } s && EndDate is { } e && e < s) yield return new("تاريخ الانتهاء قبل تاريخ البدء", [nameof(EndDate)]);
        }
    }

    public sealed record ChangeMemberRequest(
        [property: Required, OneOf("vice_chair", "secretary", "member", "alternate", "observer")] string Role,
        bool? IsVoting,
        Guid? AlternateForMemberId,
        DateOnly? EffectiveDate,
        [property: MaxLength(200)] string? AppointmentRef,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record EndMemberRequest(DateOnly? EndDate, [property: Required(ErrorMessage = "سبب الإنهاء مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record ChangeChairRequest(
        [property: Required] Guid? UserId,
        DateOnly? EffectiveDate,
        [property: MaxLength(200)] string? DecisionNo,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record DelegationRequest(
        Guid? DelegatorUserId,
        [property: Required] Guid? DelegateUserId,
        [property: Required, OneOf("chair_authority", "attendance_proxy")] string Scope,
        [property: Required] DateTime? ValidFrom,
        [property: Required] DateTime? ValidTo,
        [property: Required(ErrorMessage = "سبب التفويض مطلوب"), MinLength(3), MaxLength(1000)] string Reason) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (ValidFrom is { } f && ValidTo is { } t && t <= f) yield return new("نهاية التفويض قبل بدايته", [nameof(ValidTo)]);
        }
    }

    private const string MemberSelect = """
        SELECT m.id, m.body_id, m.user_id, u.display_name_ar, u.display_name_en, u.email, u.work_phone, u.job_title, ou.name_ar AS org_unit_name,
               COALESCE(re.name_ar, ue.name_ar) AS entity_name, (u.entra_object_id IS NOT NULL) AS verified_identity,
               m.role, m.is_voting, m.alternate_for_member_id, pu.display_name_ar AS alternate_for_name,
               m.start_date, m.end_date, m.status, m.end_reason, m.appointment_ref, m.replaced_by_member_id, m.created_at
        FROM bodies.body_members m
        JOIN iam.users u ON u.id = m.user_id
        LEFT JOIN org.org_units ou ON ou.id = u.org_unit_id
        LEFT JOIN org.external_entities re ON re.id = m.represents_entity_id
        LEFT JOIN org.external_entities ue ON ue.id = u.external_entity_id
        LEFT JOIN bodies.body_members pm ON pm.id = m.alternate_for_member_id
        LEFT JOIN iam.users pu ON pu.id = pm.user_id
        """;

    private static object MapMember(DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), userId = r.Get<Guid>("user_id"), displayNameAr = r.Get<string>("display_name_ar"),
        displayNameEn = r.Str("display_name_en"), email = r.Get<string>("email"), workPhone = r.Str("work_phone"), jobTitle = r.Str("job_title"),
        orgUnitName = r.Str("org_unit_name"), entityName = r.Str("entity_name"), verifiedIdentity = r.Get<bool>("verified_identity"),
        role = r.Get<string>("role"), isVoting = r.Get<bool>("is_voting"), alternateForMemberId = r.Uuid("alternate_for_member_id"),
        alternateForName = r.Str("alternate_for_name"), startDate = r.Date("start_date"), endDate = r.Date("end_date"), status = r.Get<string>("status"),
        endReason = r.Str("end_reason"), appointmentRef = r.Str("appointment_ref"), replacedByMemberId = r.Uuid("replaced_by_member_id"),
        createdAt = r.Get<DateTime>("created_at"),
    };

    private static async Task<IResult> List(Guid id, bool? includeEnded, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        return Results.Ok(await db.QueryAsync(MemberSelect + """

            WHERE m.body_id = @id AND (@all::boolean OR m.status <> 'ended')
            ORDER BY m.status = 'ended', array_position(ARRAY['chair','vice_chair','secretary','member','alternate','observer'], m.role), u.display_name_ar, m.start_date DESC
            """, new { id, all = includeEnded ?? false }, MapMember, ct));
    }

    private static async Task<IResult> Global(Guid? bodyId, Guid? userId, string? role, string? status, string? q, int? page, int? pageSize,
        Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, userId, role, status, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR m.body_id = ANY(@ids::uuid[])) AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId)
              AND (@userId::uuid IS NULL OR m.user_id = @userId) AND (@role::text IS NULL OR m.role = @role) AND (@status::text IS NULL OR m.status = @status)
              AND (@q::text IS NULL OR u.display_name_ar ILIKE @q OR u.email ILIKE @q OR b.name_ar ILIKE @q)
            """;
        const string from = "FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id JOIN bodies.bodies b ON b.id = m.body_id\n";
        var total = await db.ScalarAsync<long>("SELECT count(*) " + from + where, args, ct);
        var items = await db.QueryAsync("""
            SELECT m.id, m.body_id, b.name_ar AS body_name, b.reference_no AS body_reference, m.user_id, u.display_name_ar, u.email, u.job_title,
                   m.role, m.is_voting, m.start_date, m.end_date, m.status, m.end_reason
            """ + "\n" + from + where + " ORDER BY m.status = 'ended', b.name_ar, u.display_name_ar LIMIT @limit OFFSET @offset", args, r => new
        {
            id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyReference = r.Get<string>("body_reference"),
            userId = r.Get<Guid>("user_id"), displayNameAr = r.Get<string>("display_name_ar"), email = r.Get<string>("email"), jobTitle = r.Str("job_title"),
            role = r.Get<string>("role"), isVoting = r.Get<bool>("is_voting"), startDate = r.Date("start_date"), endDate = r.Date("end_date"),
            status = r.Get<string>("status"), endReason = r.Str("end_reason"),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<string> RequireOpenBodyAsync(ISql tx, Guid id, CancellationToken ct)
    {
        var b = await tx.QuerySingleAsync("SELECT status, name_ar FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, r => (Status: r.GetString(0), Name: r.GetString(1)), ct);
        if (b == default) throw new NotFoundException();
        if (b.Status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "الجهة منتهية أو مؤرشفة");
        return b.Name;
    }

    private static async Task ValidateAlternateAsync(ISql tx, Guid bodyId, Guid principalId, CancellationToken ct)
    {
        var p = await tx.QuerySingleAsync("SELECT role, is_voting, status FROM bodies.body_members WHERE id = @principalId AND body_id = @bodyId",
            new { principalId, bodyId }, r => (Role: r.GetString(0), Voting: r.GetBoolean(1), Status: r.GetString(2)), ct);
        if (p == default || p.Status == "ended") throw AppValidationException.For("alternateForMemberId", "العضو الأصيل غير موجود أو انتهت عضويته");
        if (p.Role is "alternate" or "observer" or "secretary" || !p.Voting)
            throw AppValidationException.For("alternateForMemberId", "البديل ينوب عن عضو مصوّت أصيل فقط");
    }

    private static bool DefaultVoting(string role) => role is "chair" or "vice_chair" or "member";

    private static void CheckVoting(string role, bool isVoting)
    {
        if (role is "secretary" or "observer" or "alternate" && isVoting)
            throw AppValidationException.For("isVoting", "أمين السر والمدعو والبديل لا يُحتسبون أعضاء مصوتين (البديل يحل محل الأصيل عند غيابه)");
    }

    private static async Task<IResult> Add(Guid id, AddMemberRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, INotifier notifier, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var isVoting = req.IsVoting ?? DefaultVoting(req.Role);
        CheckVoting(req.Role, isVoting);
        var memberId = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var name = await RequireOpenBodyAsync(tx, id, ct);
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM iam.users WHERE id = @uid AND status = 'active'", new { uid = req.UserId }, ct) == 0)
                throw AppValidationException.For("userId", "المستخدم غير موجود أو معطل");
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM bodies.body_members WHERE body_id = @id AND user_id = @uid AND status <> 'ended'", new { id, uid = req.UserId }, ct) > 0)
                throw new ConflictException("duplicate", "للمستخدم عضوية قائمة في هذه الجهة؛ استخدم «تغيير الصفة»");
            if (req.Role == "chair" && await tx.ScalarAsync<long>("SELECT count(*) FROM bodies.body_members WHERE body_id = @id AND role = 'chair' AND status = 'active'", new { id }, ct) > 0)
                throw new ConflictException("chair-exists", "للجهة رئيس قائم؛ استخدم «تغيير الرئيس»");
            if (req.Role == "alternate") await ValidateAlternateAsync(tx, id, req.AlternateForMemberId!.Value, ct);
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, alternate_for_member_id, represents_entity_id, start_date, end_date, appointment_ref, created_by)
                VALUES (@memberId, @id, @UserId, @Role, @isVoting, @alt, @RepresentsEntityId, @start, @EndDate, @AppointmentRef, @by)
                """, new
            {
                memberId, id, req.UserId, req.Role, isVoting, alt = req.Role == "alternate" ? req.AlternateForMemberId : null,
                req.RepresentsEntityId, start = req.StartDate ?? clock.TodayLocal, req.EndDate, req.AppointmentRef, by = me.UserId,
            }, ct);
            await notifier.NotifyAsync(tx, new NotificationRequest(req.UserId!.Value, "membership_added", $"أُضفت إلى {name}", $"You were added to {name}", $"/bodies/{id}", $"membership:{memberId}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.member_added", "body", id.ToString(), After: new { memberId, req.UserId, req.Role, isVoting, req.AlternateForMemberId, req.StartDate }), ct);
        }, ct);
        access.Invalidate(req.UserId!.Value);
        return Results.Created($"/api/v1/bodies/{id}/members/{memberId}", new { id = memberId });
    }

    /// <summary>تغيير الصفة أو حق التصويت: إنهاء السجل القائم وإنشاء سجل جديد يبدأ من تاريخ النفاذ، والربط بينهما.</summary>
    private static async Task<IResult> Change(Guid id, Guid memberId, ChangeMemberRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var isVoting = req.IsVoting ?? DefaultVoting(req.Role);
        CheckVoting(req.Role, isVoting);
        var effective = req.EffectiveDate ?? clock.TodayLocal;
        var newId = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            await RequireOpenBodyAsync(tx, id, ct);
            var old = await tx.QuerySingleAsync("""
                SELECT user_id, role, is_voting, status, start_date, represents_entity_id, alternate_for_member_id FROM bodies.body_members
                WHERE id = @memberId AND body_id = @id FOR UPDATE
                """, new { memberId, id }, r => (User: r.GetGuid(0), Role: r.GetString(1), Voting: r.GetBoolean(2), Status: r.GetString(3),
                Start: r.GetFieldValue<DateOnly>(4), Entity: r.Uuid("represents_entity_id"), Alt: r.Uuid("alternate_for_member_id")), ct);
            if (old == default) throw new NotFoundException();
            if (old.Status == "ended") throw new ConflictException("invalid-transition", "العضوية منتهية");
            if (old.Role == "chair") throw new ConflictException("use-change-chair", "تغيير صفة الرئيس يتم عبر «تغيير الرئيس»");
            if (old.Role == req.Role && old.Voting == isVoting && old.Alt == req.AlternateForMemberId) throw AppValidationException.For("role", "لا يوجد تغيير");
            if (effective < old.Start) throw AppValidationException.For("effectiveDate", "تاريخ النفاذ قبل بداية العضوية الحالية");
            if (req.Role == "alternate")
            {
                if (req.AlternateForMemberId is null) throw AppValidationException.For("alternateForMemberId", "حدد العضو الأصيل");
                if (req.AlternateForMemberId == memberId) throw AppValidationException.For("alternateForMemberId", "لا ينوب العضو عن نفسه");
                await ValidateAlternateAsync(tx, id, req.AlternateForMemberId.Value, ct);
            }
            await tx.ExecuteAsync("""
                UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective::date - 1), end_reason = @reason, ended_by = @by
                WHERE id = @memberId
                """, new { memberId, effective, reason = "تغيير الصفة: " + req.Reason, by = me.UserId }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, alternate_for_member_id, represents_entity_id, start_date, appointment_ref, created_by)
                VALUES (@newId, @id, @user, @Role, @isVoting, @alt, @entity, @effective, @AppointmentRef, @by)
                """, new { newId, id, user = old.User, req.Role, isVoting, alt = req.Role == "alternate" ? req.AlternateForMemberId : null, entity = old.Entity, effective, req.AppointmentRef, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE bodies.body_members SET replaced_by_member_id = @newId WHERE id = @memberId", new { newId, memberId }, ct);
            // البدلاء المرتبطون بالأصيل ينتقلون إلى سجله الجديد إن بقي عضوًا مصوتًا، وإلا تنتهي إنابتهم
            if (isVoting && req.Role is "member" or "vice_chair")
                await tx.ExecuteAsync("UPDATE bodies.body_members SET alternate_for_member_id = @newId WHERE alternate_for_member_id = @memberId AND status <> 'ended'", new { newId, memberId }, ct);
            else
                await tx.ExecuteAsync("""
                    UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective::date - 1), end_reason = 'انتهت صفة العضو الأصيل', ended_by = @by
                    WHERE alternate_for_member_id = @memberId AND status <> 'ended'
                    """, new { memberId, effective, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.member_changed", "body", id.ToString(),
                Before: new { memberId, role = old.Role, isVoting = old.Voting }, After: new { memberId = newId, req.Role, isVoting, effective, req.Reason }), ct);
            access.Invalidate(old.User);
        }, ct);
        return Results.Ok(new { id = newId, previousId = memberId });
    }

    private static async Task<IResult> End(Guid id, Guid memberId, EndMemberRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var end = req.EndDate ?? clock.TodayLocal;
        await db.InTransactionAsync(async tx =>
        {
            await RequireOpenBodyAsync(tx, id, ct);
            var m = await tx.QuerySingleAsync("SELECT role, status, user_id FROM bodies.body_members WHERE id = @memberId AND body_id = @id FOR UPDATE",
                new { memberId, id }, r => (Role: r.GetString(0), Status: r.GetString(1), User: r.GetGuid(2)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status == "ended") throw new ConflictException("invalid-transition", "العضوية منتهية مسبقًا");
            var bodyStatus = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @id", new { id }, ct);
            if (bodyStatus == "active" && m.Role == "chair")
                throw new ConflictException("use-change-chair", "لا تُنهى عضوية رئيس جهة نشطة إلا بتعيين رئيس جديد عبر «تغيير الرئيس»");
            await tx.ExecuteAsync("""
                UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @end), end_reason = @Reason, ended_by = @by WHERE id = @memberId
                """, new { memberId, end, req.Reason, by = me.UserId }, ct);
            var alternates = await tx.ExecuteAsync("""
                UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @end), end_reason = 'انتهت عضوية العضو الأصيل', ended_by = @by
                WHERE alternate_for_member_id = @memberId AND status <> 'ended'
                """, new { memberId, end, by = me.UserId }, ct);
            await tx.ExecuteAsync("""
                UPDATE iam.delegations SET status = 'revoked', revoked_by = @by, revoked_at = now()
                WHERE body_id = @id AND status = 'active' AND (delegator_user_id = @user OR delegate_user_id = @user)
                """, new { id, user = m.User, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.member_ended", "body", id.ToString(), After: new { memberId, role = m.Role, end, req.Reason, endedAlternates = alternates }), ct);
            access.Invalidate(m.User);
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetStatus(Guid id, Guid memberId, string from, string to, string reason, string action, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            await RequireOpenBodyAsync(tx, id, ct);
            var m = await tx.QuerySingleAsync("SELECT role, status FROM bodies.body_members WHERE id = @memberId AND body_id = @id FOR UPDATE",
                new { memberId, id }, r => (Role: r.GetString(0), Status: r.GetString(1)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status != from) throw new ConflictException("invalid-transition", "حالة العضوية لا تسمح بهذا الإجراء");
            if (m.Role == "chair" && to == "suspended") throw new ConflictException("use-change-chair", "لا يُعلّق الرئيس؛ فوّض صلاحيات الرئاسة أو غيّر الرئيس");
            await tx.ExecuteAsync("UPDATE bodies.body_members SET status = @to WHERE id = @memberId", new { memberId, to }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", action, "body", id.ToString(), After: new { memberId, status = to, reason }), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>تغيير الرئيس: إنهاء رئاسة القائم، وإنهاء العضوية الحالية للرئيس الجديد إن وجدت، ثم إنشاء عضوية الرئاسة الجديدة.</summary>
    private static async Task<IResult> ChangeChair(Guid id, ChangeChairRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, INotifier notifier, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyMembersManage, Scope.Body(id), ct);
        var effective = req.EffectiveDate ?? clock.TodayLocal;
        var newId = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var name = await RequireOpenBodyAsync(tx, id, ct);
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM iam.users WHERE id = @uid AND status = 'active'", new { uid = req.UserId }, ct) == 0)
                throw AppValidationException.For("userId", "المستخدم غير موجود أو معطل");
            var current = await tx.QuerySingleAsync("SELECT id, user_id FROM bodies.body_members WHERE body_id = @id AND role = 'chair' AND status <> 'ended' FOR UPDATE",
                new { id }, r => (Id: r.GetGuid(0), User: r.GetGuid(1)), ct);
            if (current != default && current.User == req.UserId) throw AppValidationException.For("userId", "المستخدم هو الرئيس الحالي");
            var reason = "تغيير الرئيس: " + req.Reason;
            if (current != default)
            {
                await tx.ExecuteAsync("UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective::date - 1), end_reason = @reason, ended_by = @by WHERE id = @cid",
                    new { cid = current.Id, effective, reason, by = me.UserId }, ct);
                await tx.ExecuteAsync("UPDATE iam.delegations SET status = 'revoked', revoked_by = @by, revoked_at = now() WHERE body_id = @id AND status = 'active' AND delegator_user_id = @u AND scope = 'chair_authority'",
                    new { id, u = current.User, by = me.UserId }, ct);
            }
            var existing = await tx.QuerySingleAsync("SELECT id FROM bodies.body_members WHERE body_id = @id AND user_id = @uid AND status <> 'ended' FOR UPDATE",
                new { id, uid = req.UserId }, r => r.GetGuid(0), ct);
            if (existing != Guid.Empty)
            {
                await tx.ExecuteAsync("UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective::date - 1), end_reason = @reason, ended_by = @by WHERE id = @existing",
                    new { existing, effective, reason = "تعيين رئيسًا للجهة", by = me.UserId }, ct);
                await tx.ExecuteAsync("UPDATE bodies.body_members SET status = 'ended', end_date = GREATEST(start_date, @effective::date - 1), end_reason = 'انتهت صفة العضو الأصيل', ended_by = @by WHERE alternate_for_member_id = @existing AND status <> 'ended'",
                    new { existing, effective, by = me.UserId }, ct);
            }
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, start_date, appointment_ref, created_by)
                VALUES (@newId, @id, @UserId, 'chair', true, @effective, @DecisionNo, @by)
                """, new { newId, id, req.UserId, effective, req.DecisionNo, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE bodies.body_members SET replaced_by_member_id = @newId WHERE id = ANY(@ids::uuid[])",
                new { newId, ids = new[] { current.Id, existing }.Where(x => x != Guid.Empty).ToArray() }, ct);
            await notifier.NotifyAsync(tx, new NotificationRequest(req.UserId!.Value, "chair_assigned", $"عُيّنت رئيسًا لـ {name}", $"You were appointed chair of {name}", $"/bodies/{id}", $"chair:{newId}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.chair_changed", "body", id.ToString(),
                Before: current == default ? null : new { chairUserId = current.User, memberId = current.Id }, After: new { chairUserId = req.UserId, memberId = newId, effective, req.DecisionNo, req.Reason }), ct);
            if (current != default) access.Invalidate(current.User);
        }, ct);
        return Results.Ok(new { id = newId });
    }

    // ---------------- التفويض ----------------

    private static async Task<IResult> Delegations(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT d.id, d.scope, d.delegator_user_id, a.display_name_ar AS delegator_name, d.delegate_user_id, b.display_name_ar AS delegate_name,
                   d.valid_from, d.valid_to, d.reason, d.status, (d.status = 'active' AND d.valid_from <= now() AND d.valid_to > now()) AS effective, d.created_at
            FROM iam.delegations d JOIN iam.users a ON a.id = d.delegator_user_id JOIN iam.users b ON b.id = d.delegate_user_id
            WHERE d.body_id = @id ORDER BY d.created_at DESC
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), scope = r.Get<string>("scope"), delegatorUserId = r.Get<Guid>("delegator_user_id"), delegatorName = r.Get<string>("delegator_name"),
            delegateUserId = r.Get<Guid>("delegate_user_id"), delegateName = r.Get<string>("delegate_name"), validFrom = r.Get<DateTime>("valid_from"),
            validTo = r.Get<DateTime>("valid_to"), reason = r.Get<string>("reason"), status = r.Get<string>("status"), effective = r.Get<bool>("effective"),
            createdAt = r.Get<DateTime>("created_at"),
        }, ct));
    }

    /// <summary>تفويض صلاحية الرئاسة (من الرئيس لنائبه أو عضو مصوت)، أو الإنابة في الحضور (من عضو مصوت لعضو مصوت) إن أجازتها السياسة.</summary>
    private static async Task<IResult> Delegate(Guid id, DelegationRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INotifier notifier, IBodyPolicyQuery policies, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyDelegate, Scope.Body(id), ct);
        var delegator = req.DelegatorUserId ?? me.UserId;
        var onBehalf = delegator != me.UserId;
        if (onBehalf && !await access.HasAsync(Permissions.BodyMembersManage, Scope.Body(id), ct))
            throw new ForbiddenException("التفويض نيابة عن غيرك لمسؤول المجالس فقط", Permissions.BodyMembersManage);
        var did = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var name = await RequireOpenBodyAsync(tx, id, ct);
            async Task<(string Role, bool Voting)> RoleOf(Guid u) => await tx.QuerySingleAsync(
                "SELECT role, is_voting FROM bodies.body_members WHERE body_id = @id AND user_id = @u AND status = 'active'", new { id, u },
                r => (r.GetString(0), r.GetBoolean(1)), ct);
            var from = await RoleOf(delegator);
            var to = await RoleOf(req.DelegateUserId!.Value);
            if (to == default) throw AppValidationException.For("delegateUserId", "المفوَّض ليس عضوًا نشطًا في الجهة");
            if (req.Scope == "chair_authority")
            {
                if (from.Role != "chair") throw AppValidationException.For("scope", "تفويض صلاحية الرئاسة يصدر من الرئيس فقط");
                if (!to.Voting || to.Role is not ("vice_chair" or "member")) throw AppValidationException.For("delegateUserId", "تُفوَّض الرئاسة لنائب الرئيس أو عضو مصوت");
            }
            else
            {
                var policy = await policies.GetEffectiveAsync(id, clock.TodayLocal, ct);
                if (policy is null || !policy.AllowProxy) throw new ConflictException("proxy-not-allowed", "سياسة الجهة لا تجيز الإنابة في الحضور");
                if (from == default || !from.Voting) throw AppValidationException.For("delegatorUserId", "الإنابة تصدر من عضو مصوت");
                if (!to.Voting) throw AppValidationException.For("delegateUserId", "يُناب عضو مصوت فقط");
            }
            await tx.ExecuteAsync("""
                INSERT INTO iam.delegations (id, body_id, delegator_user_id, delegate_user_id, scope, valid_from, valid_to, reason, created_by)
                VALUES (@did, @id, @delegator, @DelegateUserId, @Scope, @ValidFrom, @ValidTo, @Reason, @by)
                """, new { did, id, delegator, req.DelegateUserId, req.Scope, req.ValidFrom, req.ValidTo, req.Reason, by = me.UserId }, ct);
            await notifier.NotifyAsync(tx, new NotificationRequest(req.DelegateUserId!.Value, "delegation_granted",
                req.Scope == "chair_authority" ? $"فُوّضت صلاحيات الرئاسة في {name}" : $"أُنبت في الحضور عن عضو في {name}",
                req.Scope == "chair_authority" ? $"Chair authority delegated to you in {name}" : $"You hold an attendance proxy in {name}", $"/bodies/{id}", $"delegation:{did}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.delegation_granted", "body", id.ToString(),
                After: new { delegationId = did, delegator, req.DelegateUserId, req.Scope, req.ValidFrom, req.ValidTo, req.Reason, onBehalf }), ct);
        }, ct);
        access.Invalidate(req.DelegateUserId!.Value);
        return Results.Created($"/api/v1/bodies/{id}/delegations", new { id = did });
    }

    private static async Task<IResult> Revoke(Guid id, Guid delegationId, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var d = await tx.QuerySingleAsync("SELECT delegator_user_id, delegate_user_id, status FROM iam.delegations WHERE id = @delegationId AND body_id = @id FOR UPDATE",
                new { delegationId, id }, r => (From: r.GetGuid(0), To: r.GetGuid(1), Status: r.GetString(2)), ct);
            if (d == default) throw new NotFoundException();
            if (d.From != me.UserId && !await access.HasAsync(Permissions.BodyMembersManage, Scope.Body(id), ct))
                throw new ForbiddenException("يلغي التفويض صاحبه أو مسؤول المجالس", Permissions.BodyMembersManage);
            if (d.Status != "active") throw new ConflictException("invalid-transition", "التفويض ملغى مسبقًا");
            await tx.ExecuteAsync("UPDATE iam.delegations SET status = 'revoked', revoked_by = @by, revoked_at = now() WHERE id = @delegationId", new { delegationId, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.delegation_revoked", "body", id.ToString(), After: new { delegationId }), ct);
            access.Invalidate(d.To);
        }, ct);
        return Results.NoContent();
    }
}
