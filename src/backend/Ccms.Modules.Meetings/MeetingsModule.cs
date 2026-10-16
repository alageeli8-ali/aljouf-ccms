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

namespace Ccms.Modules.Meetings;

/// <summary>الاجتماعات: الدورية والاستثنائية والعاجلة وبالتمرير، والتقويم، والجدولة، والإلغاء، وإعادة الجدولة مع ربط الاجتماعات، والافتتاح والإغلاق.</summary>
public sealed class MeetingsModule : IModule
{
    public string Code => "MTG";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRecordBodyResolver, MeetingBodyResolver>();
        services.AddScoped<IRecordBodyResolver, AgendaItemBodyResolver>();
        services.AddScoped<IRecordRelationProvider, MeetingInviteeRelation>();
        services.AddScoped<QuorumService>();
        if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<MeetingReminderWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/meetings").WithTags("Meetings");
        g.MapGet("/", List);
        g.MapGet("/calendar", Calendar);
        g.MapPost("/", Create).Validate<MeetingRequest>();
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update).Validate<UpdateMeetingRequest>();
        g.MapPost("/{id:guid}/schedule", Schedule);
        g.MapPost("/{id:guid}/cancel", Cancel).Validate<ReasonRequest>();
        g.MapPost("/{id:guid}/reschedule", Reschedule).Validate<RescheduleRequest>();
        g.MapPost("/{id:guid}/open", Open).Validate<OpenRequest>();
        g.MapPost("/{id:guid}/close", Close);
        g.MapPost("/{id:guid}/mark-quorum-failed", QuorumFailed).Validate<ReasonRequest>();
        g.MapGet("/{id:guid}/activity", Activity);
        Attendance.Map(g);
        Agenda.Map(api, g);
    }

    public sealed record MeetingRequest(
        [property: Required] Guid? BodyId,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: MaxLength(300)] string? TitleEn,
        [property: OneOf("regular", "extraordinary", "urgent", "circulation")] string? MeetingType,
        [property: OneOf("in_person", "remote", "hybrid")] string? Mode,
        [property: Required] DateTime? StartsAt,
        [property: Range(15, 720)] int? DurationMinutes,
        [property: MaxLength(300)] string? Location,
        [property: Url(ErrorMessage = "رابط غير صالح"), MaxLength(500)] string? OnlineLink,
        DateTime? CirculationDeadline,
        Guid? PreviousMeetingId) : IValidatableObject
    {
        public string Type => MeetingType ?? "regular";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (MeetingType == "circulation" && CirculationDeadline is null) yield return new("حدد مهلة الردود للاجتماع بالتمرير", [nameof(CirculationDeadline)]);
            if (MeetingType == "circulation" && CirculationDeadline <= StartsAt) yield return new("مهلة الردود بعد تاريخ الإرسال", [nameof(CirculationDeadline)]);
            if (Mode is "remote" or "hybrid" && string.IsNullOrWhiteSpace(OnlineLink) && MeetingType != "circulation") yield return new("رابط الاجتماع المرئي مطلوب للاجتماع عن بعد أو المختلط", [nameof(OnlineLink)]);
            if (Mode is "in_person" or "hybrid" or null && string.IsNullOrWhiteSpace(Location) && MeetingType != "circulation") yield return new("المكان مطلوب للاجتماع الحضوري", [nameof(Location)]);
        }
    }

    public sealed record UpdateMeetingRequest(
        [property: MaxLength(300)] string? TitleAr,
        [property: MaxLength(300)] string? TitleEn,
        DateTime? StartsAt,
        [property: Range(15, 720)] int? DurationMinutes,
        [property: OneOf("in_person", "remote", "hybrid")] string? Mode,
        [property: MaxLength(300)] string? Location,
        [property: Url(ErrorMessage = "رابط غير صالح"), MaxLength(500)] string? OnlineLink,
        DateTime? CirculationDeadline);

    public sealed record ReasonRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);
    public sealed record RescheduleRequest([property: Required] DateTime? StartsAt, [property: Range(15, 720)] int? DurationMinutes,
        [property: MaxLength(300)] string? Location, [property: Required(ErrorMessage = "سبب إعادة الجدولة مطلوب"), MinLength(3), MaxLength(1000)] string Reason);
    public sealed record OpenRequest([property: MaxLength(1000)] string? OverrideReason);

    internal const string Select = """
        SELECT m.*, b.name_ar AS body_name, b.code AS body_code, b.status AS body_status,
               cu.display_name_ar AS chair_name, su.display_name_ar AS secretary_name,
               (SELECT count(*) FROM meetings.agenda_items a WHERE a.meeting_id = m.id) AS agenda_count,
               (SELECT count(*) FROM meetings.invitations i WHERE i.meeting_id = m.id) AS invited_count,
               (SELECT count(*) FROM meetings.invitations i WHERE i.meeting_id = m.id AND i.response = 'accepted') AS accepted_count,
               (SELECT mi.id FROM minutes.minutes mi WHERE mi.meeting_id = m.id) AS minutes_id,
               pm.reference_no AS previous_reference, pm.title_ar AS previous_title,
               rm.reference_no AS rescheduled_from_reference,
               (SELECT n.id FROM meetings.meetings n WHERE n.rescheduled_from_meeting_id = m.id LIMIT 1) AS rescheduled_to_id,
               (SELECT n.id FROM meetings.meetings n WHERE n.previous_meeting_id = m.id ORDER BY n.starts_at LIMIT 1) AS next_meeting_id
        FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
        LEFT JOIN iam.users cu ON cu.id = m.chair_user_id LEFT JOIN iam.users su ON su.id = m.secretary_user_id
        LEFT JOIN meetings.meetings pm ON pm.id = m.previous_meeting_id
        LEFT JOIN meetings.meetings rm ON rm.id = m.rescheduled_from_meeting_id
        """;

    internal static object Map(DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
        referenceNo = r.Str("reference_no"), titleAr = r.Get<string>("title_ar"), titleEn = r.Str("title_en"), meetingType = r.Get<string>("meeting_type"),
        mode = r.Get<string>("mode"), startsAt = r.Get<DateTime>("starts_at"), plannedEndAt = r.Time("planned_end_at"), location = r.Str("location"),
        onlineLink = r.Str("online_link"), status = r.Get<string>("status"), statusReason = r.Str("status_reason"), circulationDeadline = r.Time("circulation_deadline"),
        invitationsSentAt = r.Time("invitations_sent_at"), actualStartedAt = r.Time("actual_started_at"), actualEndedAt = r.Time("actual_ended_at"),
        chairName = r.Str("chair_name"), secretaryName = r.Str("secretary_name"), agendaCount = r.Get<long>("agenda_count"),
        invitedCount = r.Get<long>("invited_count"), acceptedCount = r.Get<long>("accepted_count"), minutesId = r.Uuid("minutes_id"),
        previousMeetingId = r.Uuid("previous_meeting_id"), previousReference = r.Str("previous_reference"), previousTitle = r.Str("previous_title"),
        rescheduledFromMeetingId = r.Uuid("rescheduled_from_meeting_id"), rescheduledFromReference = r.Str("rescheduled_from_reference"),
        rescheduledToId = r.Uuid("rescheduled_to_id"), nextMeetingId = r.Uuid("next_meeting_id"), classification = r.Get<string>("classification"),
    };

    private static async Task<IResult> List(Guid? bodyId, string? status, string? type, DateTime? from, DateTime? to, string? q, int? page, int? pageSize,
        Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.MeetingView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, me = me.UserId, bodyId, status, type, from, to, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR m.body_id = ANY(@ids::uuid[]) OR EXISTS (SELECT 1 FROM meetings.invitations gi WHERE gi.meeting_id = m.id AND gi.user_id = @me AND gi.sent_at IS NOT NULL))
              AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId) AND (@status::text IS NULL OR m.status = @status) AND (@type::text IS NULL OR m.meeting_type = @type)
              AND (@from::timestamptz IS NULL OR m.starts_at >= @from) AND (@to::timestamptz IS NULL OR m.starts_at < @to)
              AND (@q::text IS NULL OR m.title_ar ILIKE @q OR m.reference_no ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM meetings.meetings m\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY m.starts_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    /// <summary>التقويم الموحد: كل الاجتماعات ضمن الفترة في الجهات المسموحة، والاجتماعات التي دُعي إليها المستخدم ضيفًا.</summary>
    private static async Task<IResult> Calendar(DateTime from, DateTime to, Guid? bodyId, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(100)) throw AppValidationException.For("to", "الفترة يجب ألا تتجاوز 100 يوم");
        var filter = await access.GetBodyFilterAsync(Permissions.MeetingView, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT m.id, m.body_id, b.name_ar AS body_name, b.code AS body_code, m.reference_no, m.title_ar, m.meeting_type, m.mode, m.starts_at,
                   m.planned_end_at, m.circulation_deadline, m.location, m.status,
                   (SELECT i.response FROM meetings.invitations i WHERE i.meeting_id = m.id AND i.user_id = @me) AS my_response
            FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
            WHERE m.starts_at < @to AND COALESCE(m.circulation_deadline, m.planned_end_at, m.starts_at) >= @from
              AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId)
              AND (@all::boolean OR m.body_id = ANY(@ids::uuid[]) OR EXISTS (SELECT 1 FROM meetings.invitations gi WHERE gi.meeting_id = m.id AND gi.user_id = @me AND gi.sent_at IS NOT NULL))
            ORDER BY m.starts_at
            """, new { from, to, bodyId, all = filter.All, ids = filter.BodyIds, me = me.UserId }, r => new
        {
            id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
            referenceNo = r.Str("reference_no"), titleAr = r.Get<string>("title_ar"), meetingType = r.Get<string>("meeting_type"), mode = r.Get<string>("mode"),
            startsAt = r.Get<DateTime>("starts_at"), plannedEndAt = r.Time("planned_end_at"), circulationDeadline = r.Time("circulation_deadline"),
            location = r.Str("location"), status = r.Get<string>("status"), myResponse = r.Str("my_response"),
        }, ct));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var meeting = await db.QuerySingleAsync(Select + "\nWHERE m.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var perms = await access.GetPermissionsAsync(Scope.Record("meeting", id), ct);
        return Results.Ok(new { meeting, agenda = await Agenda.LoadAsync(db, id, ct), permissions = perms.Order() });
    }

    private static async Task<IResult> Activity(Guid id, IAccessService access, IActivityQuery activity, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        return Results.Ok(await activity.ForEntityAsync("meeting", id, 300, ct));
    }

    private static async Task<(string Status, BodyPolicy Policy)> BodyForMeetingAsync(ISql tx, IBodyPolicyQuery policies, Guid bodyId, DateTime startsAt, CancellationToken ct)
    {
        var status = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @bodyId", new { bodyId }, ct) ?? throw new NotFoundException();
        if (status != "active") throw new ConflictException("body-not-active", "لا تُجدول اجتماعات لجهة غير نشطة (FR-BOD-02)");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startsAt.ToUniversalTime(), Riyadh));
        var policy = await policies.GetEffectiveAsync(bodyId, date, ct) ?? throw new ConflictException("no-policy", "لا توجد سياسة سارية للجهة في تاريخ الاجتماع");
        return (status, policy);
    }

    internal static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private static void CheckPolicy(BodyPolicy policy, string type, string mode)
    {
        if (type == "circulation" && !policy.AllowCirculation) throw new ConflictException("circulation-not-allowed", "سياسة الجهة لا تجيز الاجتماع بالتمرير");
        if (mode is "remote" or "hybrid" && !policy.AllowRemoteAttendance) throw new ConflictException("remote-not-allowed", "سياسة الجهة لا تجيز الحضور عن بعد");
    }

    private static async Task<IResult> Create(MeetingRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IBodyPolicyQuery policies, IClock clock, CancellationToken ct)
    {
        var bodyId = req.BodyId!.Value;
        await access.DemandAsync(Permissions.MeetingManage, Scope.Body(bodyId), ct);
        var starts = req.StartsAt!.Value.ToUniversalTime();
        if (starts <= clock.UtcNow && req.Type != "urgent") throw AppValidationException.For("startsAt", "موعد الاجتماع يجب أن يكون مستقبليًا");
        var mode = req.Type == "circulation" ? "remote" : req.Mode ?? "in_person";
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var (_, policy) = await BodyForMeetingAsync(tx, policies, bodyId, starts, ct);
            CheckPolicy(policy, req.Type, mode);
            var previous = req.PreviousMeetingId ?? await tx.ScalarAsync<Guid?>("""
                SELECT id FROM meetings.meetings WHERE body_id = @bodyId AND starts_at < @starts AND status NOT IN ('cancelled','postponed') ORDER BY starts_at DESC LIMIT 1
                """, new { bodyId, starts }, ct);
            if (req.PreviousMeetingId is { } pm && await tx.ScalarAsync<Guid?>("SELECT body_id FROM meetings.meetings WHERE id = @pm", new { pm }, ct) != bodyId)
                throw AppValidationException.For("previousMeetingId", "الاجتماع السابق ليس لهذه الجهة");
            await tx.ExecuteAsync("""
                INSERT INTO meetings.meetings (id, body_id, title_ar, title_en, meeting_type, mode, starts_at, planned_end_at, location, online_link, circulation_deadline,
                  previous_meeting_id, classification, chair_user_id, secretary_user_id, created_by)
                SELECT @id, b.id, @TitleAr, @TitleEn, @MeetingType, @mode, @starts, @ends, @Location, @OnlineLink, @CirculationDeadline, @previous, b.classification,
                  (SELECT user_id FROM bodies.body_members WHERE body_id = b.id AND role = 'chair' AND status = 'active' LIMIT 1),
                  (SELECT user_id FROM bodies.body_members WHERE body_id = b.id AND role = 'secretary' AND status = 'active' ORDER BY start_date LIMIT 1), @by
                FROM bodies.bodies b WHERE b.id = @bodyId
                """, new
            {
                id, bodyId, req.TitleAr, req.TitleEn, MeetingType = req.Type, mode, starts, ends = req.Type == "circulation" ? (DateTime?)null : starts.AddMinutes(req.DurationMinutes ?? 60),
                req.Location, req.OnlineLink, CirculationDeadline = req.CirculationDeadline?.ToUniversalTime(), previous, by = me.UserId,
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.created", "meeting", id.ToString(), After: new { req.TitleAr, meetingType = req.Type, mode, starts, previous }), ct);
        }, ct);
        return Results.Created($"/api/v1/meetings/{id}", await db.QuerySingleAsync(Select + "\nWHERE m.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Update(Guid id, UpdateMeetingRequest req, Db db, IAccessService access, IAuditWriter audit, IBodyPolicyQuery policies, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, body_id, meeting_type, mode, invitations_sent_at, starts_at FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Body: r.GetGuid(1), Type: r.GetString(2), Mode: r.GetString(3), Sent: r.Time("invitations_sent_at"), Starts: r.GetDateTime(5)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is not ("draft" or "scheduled" or "agenda_published")) throw new ConflictException("invalid-transition", "لا يُعدّل اجتماع بعد انعقاده أو إلغائه");
            if (m.Sent is not null && req.StartsAt is { } s && s.ToUniversalTime() != m.Starts)
                throw new ConflictException("use-reschedule", "أُرسلت الدعوات؛ تغيير الموعد يتم عبر «إعادة الجدولة» مع ذكر السبب");
            var mode = req.Mode ?? m.Mode;
            if (req.Mode is not null)
            {
                var (_, policy) = await BodyForMeetingAsync(tx, policies, m.Body, req.StartsAt ?? m.Starts, ct);
                CheckPolicy(policy, m.Type, mode);
            }
            await tx.ExecuteAsync("""
                UPDATE meetings.meetings SET title_ar = COALESCE(@TitleAr, title_ar), title_en = COALESCE(@TitleEn, title_en),
                  starts_at = COALESCE(@starts, starts_at),
                  planned_end_at = CASE WHEN meeting_type = 'circulation' THEN NULL ELSE COALESCE(@starts, starts_at) + make_interval(mins => COALESCE(@DurationMinutes, (EXTRACT(EPOCH FROM (planned_end_at - starts_at)) / 60)::int, 60)) END,
                  mode = @mode, location = COALESCE(@Location, location), online_link = COALESCE(@OnlineLink, online_link),
                  circulation_deadline = CASE WHEN meeting_type = 'circulation' THEN COALESCE(@deadline, circulation_deadline) ELSE NULL END
                WHERE id = @id
                """, new { id, req.TitleAr, req.TitleEn, starts = req.StartsAt?.ToUniversalTime(), req.DurationMinutes, mode, req.Location, req.OnlineLink, deadline = req.CirculationDeadline?.ToUniversalTime() }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.updated", "meeting", id.ToString(), After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + "\nWHERE m.id = @id", new { id }, Map, ct));
    }

    /// <summary>تثبيت الموعد: ترقيم الاجتماع وتثبيت إصدار السياسة الساري في تاريخه (BRL-05). الإشعار يتم عند إرسال الدعوات.</summary>
    internal static async Task<string> ScheduleAsync(ISql tx, Guid id, IBodyPolicyQuery policies, INumberingService numbering, IClock clock, CancellationToken ct)
    {
        var m = await tx.QuerySingleAsync("""
            SELECT m.status, m.starts_at, m.body_id, b.code, m.meeting_type FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id WHERE m.id = @id FOR UPDATE OF m
            """, new { id }, r => (Status: r.GetString(0), Starts: r.GetDateTime(1), Body: r.GetGuid(2), Code: r.GetString(3), Type: r.GetString(4)), ct);
        if (m == default) throw new NotFoundException();
        if (m.Status != "draft") throw new ConflictException("invalid-transition", "الاجتماع مجدول مسبقًا");
        if (m.Starts <= clock.UtcNow && m.Type != "urgent") throw AppValidationException.For("startsAt", "موعد الاجتماع يجب أن يكون مستقبليًا");
        var (_, policy) = await BodyForMeetingAsync(tx, policies, m.Body, m.Starts, ct);
        var year = TimeZoneInfo.ConvertTimeFromUtc(m.Starts, Riyadh).Year;
        var seq = await numbering.NextAsync(tx, $"meeting:{m.Code}", year, ct);
        var refNo = $"{m.Code}-{year}-{seq:000}";
        await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'scheduled', sequence_year = @year, sequence_no = @seq, reference_no = @refNo, policy_id = @pid WHERE id = @id",
            new { id, year, seq, refNo, pid = policy.Id }, ct);
        return refNo;
    }

    private static async Task<IResult> Schedule(Guid id, Db db, IAccessService access, IBodyPolicyQuery policies, INumberingService numbering, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var refNo = await db.InTransactionAsync(async tx =>
        {
            var r = await ScheduleAsync(tx, id, policies, numbering, clock, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.scheduled", "meeting", id.ToString(), After: new { referenceNo = r }), ct);
            return r;
        }, ct);
        return Results.Ok(new { id, status = "scheduled", referenceNo = refNo });
    }

    internal static Task NotifyInviteesAsync(ISql tx, INotifier notifier, Guid meetingId, string eventType, string titleAr, string titleEn, string dedupe, string severity, CancellationToken ct) =>
        NotifyInviteesCoreAsync(tx, notifier, meetingId, eventType, titleAr, titleEn, dedupe, severity, ct);

    private static async Task NotifyInviteesCoreAsync(ISql tx, INotifier notifier, Guid meetingId, string eventType, string titleAr, string titleEn, string dedupe, string severity, CancellationToken ct)
    {
        var users = await tx.QueryAsync("SELECT user_id FROM meetings.invitations WHERE meeting_id = @meetingId AND sent_at IS NOT NULL", new { meetingId }, r => r.GetGuid(0), ct);
        foreach (var u in users)
            await notifier.NotifyAsync(tx, new NotificationRequest(u, eventType, titleAr, titleEn, $"/meetings/{meetingId}", $"{dedupe}:{u}", severity), ct);
    }

    private static async Task<IResult> Cancel(Guid id, ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, title_ar, reference_no FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Title: r.GetString(1), Ref: r.Str("reference_no")), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is not ("draft" or "scheduled" or "agenda_published")) throw new ConflictException("invalid-transition", "لا يُلغى الاجتماع من حالته الحالية");
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'cancelled', status_reason = @Reason, cancellation_reason = @Reason WHERE id = @id", new { id, req.Reason }, ct);
            await NotifyInviteesCoreAsync(tx, notifier, id, "meeting_cancelled", $"أُلغي الاجتماع: {m.Title} — {req.Reason}", $"Meeting cancelled: {m.Ref}", $"meeting_cancelled:{id}", "warning", ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.cancelled", "meeting", id.ToString(), Before: new { status = m.Status }, After: new { status = "cancelled", req.Reason }), ct);
        }, ct);
        return Results.Ok(new { id, status = "cancelled" });
    }

    /// <summary>إعادة الجدولة: اجتماع جديد مرتبط بالأصل يرث بنوده غير المنتهية، والأصل يصبح «مؤجلًا» بسبب مسجّل.</summary>
    private static async Task<IResult> Reschedule(Guid id, RescheduleRequest req, Db db, IAccessService access, IAuditWriter audit, INotifier notifier,
        IBodyPolicyQuery policies, INumberingService numbering, IClock clock, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var starts = req.StartsAt!.Value.ToUniversalTime();
        if (starts <= clock.UtcNow) throw AppValidationException.For("startsAt", "الموعد الجديد يجب أن يكون مستقبليًا");
        var newId = Guid.CreateVersion7();
        var refNo = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, title_ar, reference_no, body_id, meeting_type FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Title: r.GetString(1), Ref: r.Str("reference_no"), Body: r.GetGuid(3), Type: r.GetString(4)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is not ("scheduled" or "agenda_published" or "quorum_failed")) throw new ConflictException("invalid-transition", "لا يُعاد جدولة الاجتماع من حالته الحالية");
            await BodyForMeetingAsync(tx, policies, m.Body, starts, ct);
            await tx.ExecuteAsync("""
                INSERT INTO meetings.meetings (id, body_id, title_ar, title_en, meeting_type, mode, starts_at, planned_end_at, location, online_link, circulation_deadline,
                  previous_meeting_id, rescheduled_from_meeting_id, classification, chair_user_id, secretary_user_id, created_by, status_reason)
                SELECT @newId, body_id, title_ar, title_en, meeting_type, mode, @starts,
                  CASE WHEN meeting_type = 'circulation' THEN NULL ELSE @starts::timestamptz + make_interval(mins => COALESCE(@DurationMinutes, (EXTRACT(EPOCH FROM (planned_end_at - starts_at)) / 60)::int, 60)) END,
                  COALESCE(@Location, location), online_link,
                  CASE WHEN meeting_type = 'circulation' THEN @starts::timestamptz + (circulation_deadline - starts_at) END,
                  previous_meeting_id, id, classification, chair_user_id, secretary_user_id, @by, @Reason
                FROM meetings.meetings WHERE id = @id
                """, new { newId, id, starts, req.DurationMinutes, req.Location, req.Reason, by = me.UserId }, ct);
            var carried = await tx.ExecuteAsync("""
                INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, title_en, item_type, presenter_text, estimated_minutes, classification, memo_text,
                  description_ar, owner_org_unit_id, owner_text, carried_from_item_id)
                SELECT gen_random_uuid(), @newId, row_number() OVER (ORDER BY position), title_ar, title_en, item_type, presenter_text, estimated_minutes, classification, memo_text,
                  description_ar, owner_org_unit_id, owner_text, id
                FROM meetings.agenda_items WHERE meeting_id = @id AND status = 'pending'
                """, new { newId, id }, ct);
            await tx.ExecuteAsync("UPDATE meetings.agenda_items SET status = 'deferred', status_reason = @reason WHERE meeting_id = @id AND status = 'pending'",
                new { id, reason = "إعادة جدولة الاجتماع: " + req.Reason }, ct);
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'postponed', status_reason = @Reason WHERE id = @id", new { id, req.Reason }, ct);
            var newRef = await ScheduleAsync(tx, newId, policies, numbering, clock, ct);
            await NotifyInviteesCoreAsync(tx, notifier, id, "meeting_rescheduled", $"أُعيدت جدولة الاجتماع: {m.Title} — {req.Reason}", $"Meeting rescheduled: {m.Ref} → {newRef}",
                $"meeting_rescheduled:{id}", "warning", ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.rescheduled", "meeting", id.ToString(), Before: new { status = m.Status },
                After: new { status = "postponed", req.Reason, newMeetingId = newId, newReference = newRef, starts, carriedItems = carried }), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.created_by_reschedule", "meeting", newId.ToString(), After: new { fromMeetingId = id, fromReference = m.Ref, req.Reason, carriedItems = carried }), ct);
            return newRef;
        }, ct);
        return Results.Ok(new { id = newId, referenceNo = refNo, previousId = id });
    }

    /// <summary>الافتتاح: فحص نصاب الافتتاح وفق لقطة السياسة؛ لا افتتاح دون نصاب إلا بتجاوز موثق تجيزه السياسة.</summary>
    private static async Task<IResult> Open(Guid id, OpenRequest req, Db db, IAccessService access, IAuditWriter audit, QuorumService quorum, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("SELECT 1 FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct);
            var c = await quorum.LoadContextAsync(tx, id, ct);
            if (c.Status != "agenda_published") throw new ConflictException("invalid-transition", "يُفتتح الاجتماع بعد إرسال الدعوات وقبل انعقاده");
            if (c.MeetingType == "circulation") throw new ConflictException("invalid-transition", "الاجتماع بالتمرير لا يُفتتح؛ يُغلق بعد انتهاء مهلة الردود");
            var q = await quorum.ComputeAsync(tx, c, "actual", ct);
            string? overrideReason = null;
            if (!q.IsMet)
            {
                if (string.IsNullOrWhiteSpace(req.OverrideReason) || !c.Policy.Quorum.AllowOverride)
                {
                    await quorum.RecordAsync(tx, c, "opening", q, null, me.UserId, ct);
                    return (Opened: false, Q: q);
                }
                overrideReason = req.OverrideReason;
            }
            await quorum.RecordAsync(tx, c, "opening", q, overrideReason, me.UserId, ct);
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'in_progress', actual_started_at = now() WHERE id = @id", new { id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.opened", "meeting", id.ToString(), After: new { q.Eligible, q.Counted, q.Required, q.IsMet, overrideReason }), ct);
            return (Opened: true, Q: q);
        }, ct);
        if (!result.Opened)
            throw new ConflictException("quorum-not-met", $"لم يكتمل النصاب: الحاضرون المحتسبون {result.Q.Counted} من {result.Q.Required} مطلوبين",
                new Dictionary<string, object?> { ["quorum"] = result.Q });
        return Results.Ok(new { id, status = "in_progress", quorum = result.Q });
    }

    /// <summary>الإغلاق: البنود التي لم تُناقش تُؤجل آليًا إلى قائمة البنود المعلقة للجهة، والاجتماع بالتمرير يُغلق بفحص نصاب الردود.</summary>
    private static async Task<IResult> Close(Guid id, Db db, IAccessService access, IAuditWriter audit, QuorumService quorum, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        var deferred = await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("SELECT 1 FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct);
            var c = await quorum.LoadContextAsync(tx, id, ct);
            if (c.MeetingType == "circulation")
            {
                if (c.Status != "agenda_published") throw new ConflictException("invalid-transition", "لا يُغلق التمرير من حالته الحالية");
                var q = await quorum.ComputeAsync(tx, c, "circulation", ct);
                await quorum.RecordAsync(tx, c, "circulation", q, null, me.UserId, ct);
                if (!q.IsMet)
                {
                    await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'quorum_failed', status_reason = 'لم يكتمل نصاب الردود في التمرير' WHERE id = @id", new { id }, ct);
                    await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.quorum_failed", "meeting", id.ToString(), After: new { q.Counted, q.Required, circulation = true }), ct);
                    return -1;
                }
            }
            else if (c.Status != "in_progress") throw new ConflictException("invalid-transition", "لا يُغلق إلا اجتماع منعقد");
            var moved = await Agenda.DeferRemainingAsync(tx, id, c.BodyId, me.UserId, "لم يُناقش في الاجتماع", ct);
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'concluded', actual_ended_at = now(), actual_started_at = COALESCE(actual_started_at, starts_at) WHERE id = @id", new { id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.closed", "meeting", id.ToString(), After: new { deferredItems = moved }), ct);
            return moved;
        }, ct);
        if (deferred < 0) throw new ConflictException("quorum-not-met", "لم يكتمل نصاب الردود؛ سُجل الاجتماع «لم يكتمل النصاب»");
        return Results.Ok(new { id, status = "concluded", deferredItems = deferred });
    }

    private static async Task<IResult> QuorumFailed(Guid id, ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, QuorumService quorum, INotifier notifier, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("SELECT 1 FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct);
            var c = await quorum.LoadContextAsync(tx, id, ct);
            if (c.Status != "agenda_published") throw new ConflictException("invalid-transition", "يُسجل عدم اكتمال النصاب قبل الافتتاح فقط");
            var q = await quorum.ComputeAsync(tx, c, "actual", ct);
            if (q.IsMet) throw new ConflictException("quorum-met", "النصاب مكتمل؛ لا يُسجل الاجتماع غير مكتمل النصاب");
            await quorum.RecordAsync(tx, c, "opening", q, null, me.UserId, ct);
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'quorum_failed', status_reason = @Reason WHERE id = @id", new { id, req.Reason }, ct);
            await NotifyInviteesCoreAsync(tx, notifier, id, "meeting_quorum_failed", "لم يكتمل نصاب الاجتماع", "Meeting quorum not met", $"quorum_failed:{id}", "warning", ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.quorum_failed", "meeting", id.ToString(), After: new { q.Counted, q.Required, req.Reason }), ct);
        }, ct);
        return Results.Ok(new { id, status = "quorum_failed" });
    }
}

internal sealed class MeetingBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "meeting";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT body_id FROM meetings.meetings WHERE id = @recordId", new { recordId }, ct);
}

internal sealed class AgendaItemBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "agenda_item";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT m.body_id FROM meetings.agenda_items a JOIN meetings.meetings m ON m.id = a.meeting_id WHERE a.id = @recordId", new { recordId }, ct);
}

/// <summary>الضيف المدعو (بعد إرسال دعوته) يطّلع على الاجتماع؛ وضيف البند يطّلع على مرفقات بنده فقط.</summary>
internal sealed class MeetingInviteeRelation(Db db) : IRecordRelationProvider
{
    public string RecordType => "meeting";
    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var invited = await db.ScalarAsync<long>("SELECT count(*) FROM meetings.invitations WHERE meeting_id = @recordId AND user_id = @userId AND sent_at IS NOT NULL",
            new { recordId, userId }, ct);
        return invited > 0 ? new HashSet<string> { Permissions.MeetingView } : new HashSet<string>();
    }
}
