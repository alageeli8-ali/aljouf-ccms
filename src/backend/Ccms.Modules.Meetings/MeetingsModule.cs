using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Meetings;

public sealed class MeetingsModule : IModule
{
    public string Code => "MTG";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<IRecordBodyResolver, MeetingBodyResolver>();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/meetings").WithTags("Meetings");
        g.MapGet("/", List);
        g.MapPost("/", Create).Validate<CreateMeetingRequest>();
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update).Validate<UpdateMeetingRequest>();
        g.MapPost("/{id:guid}/schedule", Schedule);
        g.MapPost("/{id:guid}/cancel", Cancel).Validate<CancelMeetingRequest>();
        g.MapPost("/{id:guid}/agenda-items", AddAgendaItem).Validate<AgendaItemRequest>();
    }

    public sealed record CreateMeetingRequest(
        [property: Required] Guid? BodyId,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: MaxLength(300)] string? TitleEn,
        [property: OneOf("regular", "extraordinary")] string? MeetingType,
        [property: OneOf("in_person", "remote", "hybrid")] string? Mode,
        [property: Required] DateTime? StartsAt,
        [property: Range(15, 720)] int? DurationMinutes,
        [property: MaxLength(300)] string? Location,
        [property: Url, MaxLength(500)] string? OnlineLink);

    public sealed record UpdateMeetingRequest(
        [property: MaxLength(300)] string? TitleAr,
        DateTime? StartsAt,
        [property: Range(15, 720)] int? DurationMinutes,
        [property: MaxLength(300)] string? Location,
        [property: OneOf("in_person", "remote", "hybrid")] string? Mode,
        [property: Url, MaxLength(500)] string? OnlineLink);

    public sealed record CancelMeetingRequest([property: Required, MinLength(5), MaxLength(500)] string Reason);

    public sealed record AgendaItemRequest(
        [property: Required, MaxLength(400)] string TitleAr,
        [property: MaxLength(400)] string? TitleEn,
        [property: OneOf("for_decision", "for_information", "for_discussion")] string? ItemType,
        [property: MaxLength(200)] string? PresenterText,
        [property: Range(1, 480)] int? EstimatedMinutes,
        [property: MaxLength(20000)] string? MemoText);

    private const string MeetingSelect = """
        SELECT m.*, b.name_ar AS body_name, b.code AS body_code,
               (SELECT count(*) FROM meetings.agenda_items a WHERE a.meeting_id = m.id) AS agenda_count,
               (SELECT mi.id FROM minutes.minutes mi WHERE mi.meeting_id = m.id) AS minutes_id
        FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
        """;

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
        referenceNo = r.Str("reference_no"), titleAr = r.Get<string>("title_ar"), titleEn = r.Str("title_en"),
        meetingType = r.Get<string>("meeting_type"), mode = r.Get<string>("mode"), startsAt = r.Get<DateTime>("starts_at"),
        plannedEndAt = r.Time("planned_end_at"), location = r.Str("location"), onlineLink = r.Str("online_link"), status = r.Get<string>("status"),
        agendaCount = r.Get<long>("agenda_count"), minutesId = r.Uuid("minutes_id"), cancellationReason = r.Str("cancellation_reason"),
    };

    private static async Task<IResult> List(Guid? bodyId, string? status, DateTime? from, DateTime? to, string? q, int? page, int? pageSize,
        Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.MeetingView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, status, from, to, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR m.body_id = ANY(@ids::uuid[]))
              AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId)
              AND (@status::text IS NULL OR m.status = @status)
              AND (@from::timestamptz IS NULL OR m.starts_at >= @from)
              AND (@to::timestamptz IS NULL OR m.starts_at < @to)
              AND (@q::text IS NULL OR m.title_ar ILIKE @q OR m.reference_no ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM meetings.meetings m " + "\n" + where, args, ct);
        var items = await db.QueryAsync(MeetingSelect + "\n" + where + " ORDER BY m.starts_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Create(CreateMeetingRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var bodyId = req.BodyId!.Value;
        await access.DemandAsync(Permissions.MeetingManage, Scope.Body(bodyId), ct);
        var id = Guid.CreateVersion7();
        var starts = req.StartsAt!.Value.ToUniversalTime();
        await db.InTransactionAsync(async tx =>
        {
            var b = await tx.QuerySingleAsync("SELECT status, classification FROM bodies.bodies WHERE id = @bodyId", new { bodyId },
                r => (Status: r.GetString(0), Classification: r.GetString(1)), ct);
            if (b.Status != "active") throw new ConflictException("body-not-active", "لا تُجدول اجتماعات لجهة غير نشطة (FR-BOD-02)");
            await tx.ExecuteAsync("""
                INSERT INTO meetings.meetings (id, body_id, title_ar, title_en, meeting_type, mode, starts_at, planned_end_at, location, online_link, classification, created_by)
                VALUES (@id, @bodyId, @TitleAr, @TitleEn, @type, @mode, @starts, @ends, @Location, @OnlineLink, @cls, @by)
                """, new
            {
                id, bodyId, req.TitleAr, req.TitleEn, type = req.MeetingType ?? "regular", mode = req.Mode ?? "in_person", starts,
                ends = starts.AddMinutes(req.DurationMinutes ?? 60), req.Location, req.OnlineLink, cls = b.Classification, by = me.UserId,
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.created", "meeting", id.ToString(), After: req), ct);
        }, ct);
        return Results.Created($"/api/v1/meetings/{id}", await db.QuerySingleAsync(MeetingSelect + " WHERE m.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var meeting = await db.QuerySingleAsync(MeetingSelect + " WHERE m.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var agenda = await db.QueryAsync("""
            SELECT id, position, title_ar, title_en, item_type, presenter_text, estimated_minutes, status, memo_text
            FROM meetings.agenda_items WHERE meeting_id = @id ORDER BY position
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), position = r.Get<int>("position"), titleAr = r.Get<string>("title_ar"), titleEn = r.Str("title_en"),
            itemType = r.Get<string>("item_type"), presenterText = r.Str("presenter_text"),
            estimatedMinutes = r.IsDBNull(r.GetOrdinal("estimated_minutes")) ? (int?)null : r.Get<int>("estimated_minutes"),
            status = r.Get<string>("status"), memoText = r.Str("memo_text"),
        }, ct);
        return Results.Ok(new { meeting, agenda, permissions = (await access.GetPermissionsAsync(Scope.Record("meeting", id), ct)).Order() });
    }

    private static async Task<IResult> Update(Guid id, UpdateMeetingRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is not ("draft" or "scheduled" or "agenda_published"))
                throw new ConflictException("invalid-transition", "لا يمكن تعديل اجتماع بعد انعقاده أو إلغائه");
            await tx.ExecuteAsync("""
                UPDATE meetings.meetings SET title_ar = COALESCE(@TitleAr, title_ar),
                  starts_at = COALESCE(@starts, starts_at),
                  planned_end_at = COALESCE(@starts, starts_at) + make_interval(mins => COALESCE(@DurationMinutes, (EXTRACT(EPOCH FROM (planned_end_at - starts_at)) / 60)::int, 60)),
                  location = COALESCE(@Location, location), mode = COALESCE(@Mode, mode), online_link = COALESCE(@OnlineLink, online_link)
                WHERE id = @id
                """, new { id, req.TitleAr, starts = req.StartsAt?.ToUniversalTime(), req.DurationMinutes, req.Location, req.Mode, req.OnlineLink }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.updated", "meeting", id.ToString(), After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(MeetingSelect + " WHERE m.id = @id", new { id }, Map, ct));
    }

    /// <summary>تثبيت الموعد: ترقيم بلا تكرار، وتثبيت إصدار السياسة الساري، وإشعار الأعضاء (08 §1).</summary>
    private static async Task<IResult> Schedule(Guid id, Db db, IAccessService access, INumberingService numbering, INotifier notifier,
        IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var reference = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("""
                SELECT m.status, m.starts_at, m.title_ar, m.body_id, b.code FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
                WHERE m.id = @id FOR UPDATE OF m
                """, new { id }, r => (Status: r.GetString(0), Starts: r.GetDateTime(1), Title: r.GetString(2), BodyId: r.GetGuid(3), Code: r.GetString(4)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status != "draft") throw new ConflictException("invalid-transition", "الاجتماع مجدول مسبقًا");
            if (m.Starts <= clock.UtcNow) throw AppValidationException.For("startsAt", "موعد الاجتماع يجب أن يكون مستقبليًا");
            var year = m.Starts.Year;
            var seq = await numbering.NextAsync(tx, $"meeting:{m.Code}", year, ct);
            var refNo = $"{m.Code}-{year}-{seq:000}";
            await tx.ExecuteAsync("""
                UPDATE meetings.meetings SET status = 'scheduled', sequence_year = @year, sequence_no = @seq, reference_no = @refNo,
                  policy_id = (SELECT p.id FROM bodies.body_policies p WHERE p.body_id = @bodyId AND p.effective_from <= (@starts::timestamptz AT TIME ZONE 'Asia/Riyadh')::date
                               ORDER BY p.version DESC LIMIT 1)
                WHERE id = @id
                """, new { id, year, seq, refNo, bodyId = m.BodyId, starts = m.Starts }, ct);
            var members = await tx.QueryAsync("""
                SELECT user_id FROM bodies.body_members WHERE body_id = @bodyId AND status = 'active'
                  AND start_date <= (@starts::timestamptz AT TIME ZONE 'Asia/Riyadh')::date
                  AND (end_date IS NULL OR end_date >= (@starts::timestamptz AT TIME ZONE 'Asia/Riyadh')::date)
                """, new { bodyId = m.BodyId, starts = m.Starts }, r => r.GetGuid(0), ct);
            foreach (var uid in members)
                await notifier.NotifyAsync(tx, new NotificationRequest(uid, "meeting_scheduled", $"اجتماع مجدول: {m.Title} ({refNo})",
                    $"Meeting scheduled: {refNo}", $"/meetings/{id}", $"meeting_scheduled:{id}:{uid}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.scheduled", "meeting", id.ToString(), After: new { refNo, invited = members.Count }), ct);
            return refNo;
        }, ct);
        return Results.Ok(new { id, status = "scheduled", referenceNo = reference });
    }

    private static async Task<IResult> Cancel(Guid id, CancelMeetingRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is not ("draft" or "scheduled" or "agenda_published")) throw new ConflictException("invalid-transition", "لا يمكن إلغاء الاجتماع من حالته الحالية");
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'cancelled', cancellation_reason = @Reason WHERE id = @id", new { id, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.cancelled", "meeting", id.ToString(), Before: new { status }, After: req), ct);
        }, ct);
        return Results.Ok(new { id, status = "cancelled" });
    }

    private static async Task<IResult> AddAgendaItem(Guid id, AgendaItemRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var itemId = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is not ("draft" or "scheduled" or "agenda_published")) throw new ConflictException("invalid-transition", "لا تُضاف بنود بعد انعقاد الاجتماع");
            await tx.ExecuteAsync("""
                INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, title_en, item_type, presenter_text, estimated_minutes, memo_text)
                VALUES (@itemId, @id, (SELECT COALESCE(max(position), 0) + 1 FROM meetings.agenda_items WHERE meeting_id = @id),
                        @TitleAr, @TitleEn, @type, @PresenterText, @EstimatedMinutes, @MemoText)
                """, new { itemId, id, req.TitleAr, req.TitleEn, type = req.ItemType ?? "for_decision", req.PresenterText, req.EstimatedMinutes, req.MemoText }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_added", "meeting", id.ToString(), After: new { itemId, req.TitleAr }), ct);
        }, ct);
        return Results.Created($"/api/v1/meetings/{id}", new { id = itemId });
    }
}

internal sealed class MeetingBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "meeting";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT body_id FROM meetings.meetings WHERE id = @recordId", new { recordId }, ct);
}
