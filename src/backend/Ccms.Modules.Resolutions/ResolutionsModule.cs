using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Resolutions;

/// <summary>سجل القرارات والتوصيات: لكل قرار سجل مستقل برقم فريد يُمنح عند الإصدار فقط، ومصدره (الجهة والاجتماع والبند والمحضر وإصداره)،
/// ونصه الرسمي، ونوعه وتصنيفه، وصاحب الصلاحية، ونتيجة التصويت، والجهة المنفذة، والمهام، وحالة التنفيذ، والقرار الذي يعدّله أو يلغيه.
/// التوصية منفصلة عن القرار وقد تُرفع لجهة أعلى؛ والتكليف مهمة مستقلة مرتبطة بالقرار.</summary>
public sealed class ResolutionsModule : IModule
{
    public string Code => "RES";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRecordBodyResolver, ResolutionBodyResolver>();
        services.AddScoped<IResolutionIssuer, ResolutionIssuer>();
        services.AddScoped<IResolutionExecutionHook, ResolutionExecutionHook>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/resolutions").WithTags("Resolutions");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Edit).Validate<EditRequest>();
        g.MapPost("/{id:guid}/withdraw", Withdraw).Validate<ReasonRequest>();
        g.MapPost("/{id:guid}/transition", Transition).Validate<TransitionRequest>();
        api.MapPost("/minutes/{minutesId:guid}/resolutions", Propose).WithTags("Resolutions").Validate<ProposeRequest>().Idempotent();
    }

    public static readonly string[] Categories = ["administrative", "financial", "technical", "policy", "organizational", "procedural", "other"];

    public sealed record ProposeRequest(
        [property: Required, OneOf("decision", "recommendation")] string ResolutionType,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: Required, MaxLength(10000)] string TextAr,
        [property: MaxLength(10000)] string? TextEn,
        bool? RequiresFollowUp,
        [property: OneOf("administrative", "financial", "technical", "policy", "organizational", "procedural", "other")] string? Category = null,
        Guid? AgendaItemId = null,
        [property: MaxLength(300)] string? AuthorityText = null,
        Guid? TargetBodyId = null,
        [property: MaxLength(300)] string? TargetAuthorityText = null,
        Guid? ResponsibleOrgUnitId = null,
        Guid? ResponsibleExternalEntityId = null,
        [property: MaxLength(300)] string? ResponsibleText = null,
        Guid? SupersedesResolutionId = null,
        [property: OneOf("amends", "revokes")] string? SupersedeKind = null,
        [property: OneOf("vote", "consensus", "chair_decision")] string? AdoptionMethod = null) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (ResolutionType == "decision" && (TargetBodyId is not null || !string.IsNullOrWhiteSpace(TargetAuthorityText)))
                yield return new("الرفع لجهة أعلى للتوصيات فقط؛ القرار يصدر من الجهة نفسها", [nameof(TargetBodyId)]);
            if ((SupersedesResolutionId is null) != (SupersedeKind is null))
                yield return new("حدد القرار السابق ونوع الأثر عليه (تعديل أو إلغاء) معًا", [nameof(SupersedeKind)]);
        }
    }

    public sealed record EditRequest(
        [property: OneOf("decision", "recommendation")] string? ResolutionType,
        [property: MaxLength(300)] string? TitleAr,
        [property: MaxLength(10000)] string? TextAr,
        [property: MaxLength(10000)] string? TextEn,
        bool? RequiresFollowUp,
        [property: OneOf("administrative", "financial", "technical", "policy", "organizational", "procedural", "other")] string? Category,
        Guid? AgendaItemId,
        [property: MaxLength(300)] string? AuthorityText,
        Guid? TargetBodyId,
        [property: MaxLength(300)] string? TargetAuthorityText,
        Guid? ResponsibleOrgUnitId,
        Guid? ResponsibleExternalEntityId,
        [property: MaxLength(300)] string? ResponsibleText,
        [property: OneOf("vote", "consensus", "chair_decision")] string? AdoptionMethod,
        [property: Range(1, 500)] int? Position);

    public sealed record ReasonRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    public sealed record TransitionRequest(
        [property: Required, OneOf("start_execution", "mark_executed", "close", "refer", "record_outcome", "cancel")] string Action,
        [property: MaxLength(1000)] string? Reason,
        [property: MaxLength(200)] string? Reference,
        [property: OneOf("adopted", "rejected", "modified")] string? Outcome);

    internal const string Select = """
        SELECT r.*, b.name_ar AS body_name, b.code AS body_code, m.reference_no AS meeting_reference, m.title_ar AS meeting_title, m.starts_at AS meeting_starts_at,
               ai.title_ar AS agenda_item_title, ai.position AS agenda_item_position,
               -- القيم المرجعية باستعلامات فرعية تختصر زمن التخطيط (24 §6)
               (SELECT mv.version_no FROM minutes.minutes_versions mv WHERE mv.id = r.minutes_version_id) AS minutes_version_no,
               (SELECT mi.status FROM minutes.minutes mi WHERE mi.id = r.minutes_id) AS minutes_status,
               (SELECT tb.name_ar FROM bodies.bodies tb WHERE tb.id = r.target_body_id) AS target_body_name,
               (SELECT ou.name_ar FROM org.org_units ou WHERE ou.id = r.responsible_org_unit_id) AS responsible_org_unit_name,
               (SELECT ee.name_ar FROM org.external_entities ee WHERE ee.id = r.responsible_external_entity_id) AS responsible_entity_name,
               (SELECT sr.number FROM resolutions.resolutions sr WHERE sr.id = r.supersedes_resolution_id) AS supersedes_number,
               (SELECT sr.title_ar FROM resolutions.resolutions sr WHERE sr.id = r.supersedes_resolution_id) AS supersedes_title,
               (SELECT count(*) FROM tasks.tasks t WHERE t.resolution_id = r.id AND t.status <> 'cancelled') AS task_count,
               (SELECT count(*) FROM tasks.tasks t WHERE t.resolution_id = r.id AND t.status = 'completed') AS tasks_completed,
               (SELECT mo.result FROM deliberation.motions mo WHERE mo.resolution_id = r.id AND mo.status = 'closed' ORDER BY mo.round DESC LIMIT 1) AS vote_result,
               (SELECT mo.tally::text FROM deliberation.motions mo WHERE mo.resolution_id = r.id AND mo.status = 'closed' ORDER BY mo.round DESC LIMIT 1) AS vote_tally,
               (SELECT count(*) FROM deliberation.motions mo WHERE mo.resolution_id = r.id AND mo.status = 'open') AS open_motions
        FROM resolutions.resolutions r
        JOIN bodies.bodies b ON b.id = r.body_id
        LEFT JOIN meetings.meetings m ON m.id = r.meeting_id
        LEFT JOIN meetings.agenda_items ai ON ai.id = r.agenda_item_id
        """;

    internal static object Map(System.Data.Common.DbDataReader r)
    {
        var tally = r.Str("vote_tally") ?? r.Str("vote_summary");
        return new
        {
            id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
            minutesId = r.Uuid("minutes_id"), minutesStatus = r.Str("minutes_status"), minutesVersionNo = r.IsDBNull(r.GetOrdinal("minutes_version_no")) ? (int?)null : r.Get<int>("minutes_version_no"),
            meetingId = r.Uuid("meeting_id"), meetingReference = r.Str("meeting_reference"), meetingTitle = r.Str("meeting_title"), meetingStartsAt = r.Time("meeting_starts_at"),
            agendaItemId = r.Uuid("agenda_item_id"), agendaItemTitle = r.Str("agenda_item_title"),
            agendaItemPosition = r.IsDBNull(r.GetOrdinal("agenda_item_position")) ? (int?)null : r.Get<int>("agenda_item_position"),
            resolutionType = r.Get<string>("resolution_type"), category = r.Get<string>("category"), position = r.Get<int>("position"),
            number = r.Str("number"), titleAr = r.Get<string>("title_ar"), textAr = r.Get<string>("text_ar"), textEn = r.Str("text_en"),
            status = r.Get<string>("status"), statusReason = r.Str("status_reason"), issuedAt = r.Time("issued_at"), approvedAt = r.Time("approved_at"),
            requiresFollowUp = r.Get<bool>("requires_follow_up"), classification = r.Get<string>("classification"), source = r.Get<string>("source"),
            authorityText = r.Str("authority_text"), targetBodyId = r.Uuid("target_body_id"), targetBodyName = r.Str("target_body_name"),
            targetAuthorityText = r.Str("target_authority_text"), referralOutcome = r.Str("referral_outcome"), referralNote = r.Str("referral_note"),
            referralDecidedAt = r.Time("referral_decided_at"),
            responsibleOrgUnitId = r.Uuid("responsible_org_unit_id"), responsibleOrgUnitName = r.Str("responsible_org_unit_name"),
            responsibleExternalEntityId = r.Uuid("responsible_external_entity_id"), responsibleEntityName = r.Str("responsible_entity_name"),
            responsibleText = r.Str("responsible_text"),
            supersedesResolutionId = r.Uuid("supersedes_resolution_id"), supersedeKind = r.Str("supersede_kind"),
            supersedesNumber = r.Str("supersedes_number"), supersedesTitle = r.Str("supersedes_title"),
            adoptionMethod = r.Get<string>("adoption_method"),
            voteResult = r.Str("vote_result"), voteSummary = tally is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(tally),
            openMotions = r.Get<long>("open_motions"),
            createdAt = r.Get<DateTime>("created_at"), updatedAt = r.Get<DateTime>("updated_at"),
            taskCount = r.Get<long>("task_count"), tasksCompleted = r.Get<long>("tasks_completed"),
        };
    }

    private static async Task<IResult> List(Guid? bodyId, Guid? meetingId, Guid? minutesId, string? status, string? type, string? category, string? q,
        int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.ResolutionView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, meetingId, minutesId, status, type, category, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR r.body_id = ANY(@ids::uuid[])) AND (@bodyId::uuid IS NULL OR r.body_id = @bodyId)
              AND (@meetingId::uuid IS NULL OR r.meeting_id = @meetingId) AND (@minutesId::uuid IS NULL OR r.minutes_id = @minutesId)
              AND (@status::text IS NULL OR r.status = @status) AND (@type::text IS NULL OR r.resolution_type = @type)
              AND (@category::text IS NULL OR r.category = @category)
              AND (@q::text IS NULL OR r.title_ar ILIKE @q OR r.text_ar ILIKE @q OR r.number ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM resolutions.resolutions r " + "\n" + where, args, ct);
        var order = minutesId is not null ? " ORDER BY r.position, r.created_at" : " ORDER BY COALESCE(r.issued_at, r.created_at) DESC";
        var items = await db.QueryAsync(Select + "\n" + where + order + " LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionView, Scope.Record("resolution", id), ct);
        var resolution = await db.QuerySingleAsync(Select + " WHERE r.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var tasks = await db.QueryAsync("""
            SELECT t.id, t.reference_no, t.title_ar, t.status, t.due_date, t.progress_pct, t.assignee_type,
                   COALESCE(u.display_name_ar, o.name_ar, e.name_ar) AS assignee_name
            FROM tasks.tasks t LEFT JOIN iam.users u ON u.id = t.assignee_user_id LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id
            LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
            WHERE t.resolution_id = @id ORDER BY t.created_at
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), referenceNo = r.Str("reference_no"), titleAr = r.Get<string>("title_ar"), status = r.Get<string>("status"),
            dueDate = r.Date("due_date"), progressPct = r.Get<int>("progress_pct"), assigneeType = r.Get<string>("assignee_type"), assigneeName = r.Str("assignee_name"),
        }, ct);
        var history = await db.QueryAsync("""
            SELECT h.from_status, h.to_status, h.reason, h.reference, h.changed_at, u.display_name_ar
            FROM resolutions.resolution_status_history h LEFT JOIN iam.users u ON u.id = h.changed_by WHERE h.resolution_id = @id ORDER BY h.changed_at, h.id
            """, new { id }, r => new
        {
            fromStatus = r.Str("from_status"), toStatus = r.Get<string>("to_status"), reason = r.Str("reason"), reference = r.Str("reference"),
            changedAt = r.Get<DateTime>("changed_at"), changedBy = r.Str("display_name_ar"),
        }, ct);
        var supersededBy = await db.QueryAsync("""
            SELECT id, number, title_ar, supersede_kind, status FROM resolutions.resolutions WHERE supersedes_resolution_id = @id AND status NOT IN ('withdrawn','rejected')
            """, new { id }, r => new { id = r.GetGuid(0), number = r.Str("number"), titleAr = r.GetString(2), kind = r.GetString(3), status = r.GetString(4) }, ct);
        var reservations = await db.QueryAsync("""
            SELECT u.display_name_ar, rv.kind, rv.text, rv.created_at FROM deliberation.reservations rv JOIN iam.users u ON u.id = rv.user_id
            WHERE rv.resolution_id = @id ORDER BY rv.created_at
            """, new { id }, r => new { name = r.GetString(0), kind = r.GetString(1), text = r.GetString(2), createdAt = r.GetDateTime(3) }, ct);
        var documents = await db.ScalarAsync<long>("SELECT count(*) FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id WHERE l.owner_type = 'resolution' AND l.owner_id = @id AND d.deleted_at IS NULL", new { id }, ct);
        return Results.Ok(new
        {
            resolution, tasks, history, supersededBy, reservations, documentCount = documents,
            permissions = (await access.GetPermissionsAsync(Scope.Record("resolution", id), ct)).Order(),
        });
    }

    private static async Task<(string Status, Guid MeetingId, Guid BodyId, string Cls)> EditableMinutesAsync(ISql tx, Guid minutesId, CancellationToken ct)
    {
        var m = await tx.QuerySingleAsync("""
            SELECT mi.status, mt.id AS meeting_id, mt.body_id, mi.classification FROM minutes.minutes mi JOIN meetings.meetings mt ON mt.id = mi.meeting_id
            WHERE mi.id = @minutesId FOR UPDATE OF mi
            """, new { minutesId }, r => (Status: r.GetString(0), MeetingId: r.GetGuid(1), BodyId: r.GetGuid(2), Cls: r.GetString(3)), ct);
        if (m == default) throw new NotFoundException();
        if (m.Status is not ("draft" or "returned"))
            throw new ConflictException("minutes-locked", "تُضاف القرارات وتُعدل في مسودة المحضر أو بعد إعادته للتعديل فقط");
        return m;
    }

    private static async Task ValidateLinksAsync(ISql tx, Guid meetingId, Guid bodyId, Guid? agendaItemId, Guid? targetBodyId, Guid? supersedes, Guid? selfId, CancellationToken ct)
    {
        if (agendaItemId is { } item && await tx.ScalarAsync<Guid?>("SELECT meeting_id FROM meetings.agenda_items WHERE id = @item", new { item }, ct) != meetingId)
            throw AppValidationException.For("agendaItemId", "البند لا يتبع اجتماع هذا المحضر");
        if (targetBodyId is { } tb && (tb == bodyId || await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @tb", new { tb }, ct) is null or "archived"))
            throw AppValidationException.For("targetBodyId", "الجهة الأعلى غير صالحة");
        if (supersedes is { } s)
        {
            var prev = await tx.QuerySingleAsync("SELECT body_id, status FROM resolutions.resolutions WHERE id = @s", new { s }, r => (Body: r.GetGuid(0), Status: r.GetString(1)), ct);
            if (prev == default || prev.Body != bodyId || s == selfId) throw AppValidationException.For("supersedesResolutionId", "القرار السابق غير موجود في هذه الجهة");
            if (prev.Status is not ("issued" or "in_execution" or "executed" or "referred"))
                throw AppValidationException.For("supersedesResolutionId", "يُعدَّل أو يُلغى قرار صادر فقط");
        }
    }

    internal static Task HistoryAsync(ISql tx, Guid id, string? from, string to, string? reason, string? reference, Guid? by, CancellationToken ct) =>
        tx.ExecuteAsync("""
            INSERT INTO resolutions.resolution_status_history (id, resolution_id, from_status, to_status, reason, reference, changed_by)
            VALUES (@hid, @id, @from, @to, @reason, @reference, @by)
            """, new { hid = Guid.CreateVersion7(), id, from, to, reason, reference, by }, ct);

    /// <summary>قرار مقترح ضمن مسودة المحضر؛ لا يُرقّم ولا يُصدر إلا باعتماد المحضر (BRL-08). مفتاح Idempotency-Key يمنع تكراره عند إعادة المحاولة.</summary>
    private static async Task<IResult> Propose(Guid minutesId, ProposeRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionManage, Scope.Record("minutes", minutesId), ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var m = await EditableMinutesAsync(tx, minutesId, ct);
            await ValidateLinksAsync(tx, m.MeetingId, m.BodyId, req.AgendaItemId, req.TargetBodyId, req.SupersedesResolutionId, null, ct);
            var position = await tx.ScalarAsync<int>("SELECT COALESCE(max(position), 0) + 1 FROM resolutions.resolutions WHERE minutes_id = @minutesId", new { minutesId }, ct);
            await tx.ExecuteAsync("""
                INSERT INTO resolutions.resolutions (id, body_id, minutes_id, meeting_id, agenda_item_id, resolution_type, category, position, title_ar, text_ar, text_en,
                  requires_follow_up, classification, authority_text, target_body_id, target_authority_text, responsible_org_unit_id, responsible_external_entity_id,
                  responsible_text, supersedes_resolution_id, supersede_kind, adoption_method, created_by, updated_by)
                VALUES (@id, @bodyId, @minutesId, @meetingId, @AgendaItemId, @ResolutionType, @category, @position, @TitleAr, @TextAr, @TextEn,
                  @follow, @cls, @AuthorityText, @TargetBodyId, @TargetAuthorityText, @ResponsibleOrgUnitId, @ResponsibleExternalEntityId,
                  @ResponsibleText, @SupersedesResolutionId, @SupersedeKind, @adoption, @by, @by)
                """, new
            {
                id, bodyId = m.BodyId, minutesId, meetingId = m.MeetingId, req.AgendaItemId, req.ResolutionType, category = req.Category ?? "administrative", position,
                req.TitleAr, req.TextAr, req.TextEn, follow = req.RequiresFollowUp ?? req.ResolutionType == "decision", cls = m.Cls, req.AuthorityText, req.TargetBodyId,
                req.TargetAuthorityText, req.ResponsibleOrgUnitId, req.ResponsibleExternalEntityId, req.ResponsibleText, req.SupersedesResolutionId, req.SupersedeKind,
                adoption = req.AdoptionMethod ?? "consensus", by = me.UserId,
            }, ct);
            await HistoryAsync(tx, id, null, "proposed", null, null, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.proposed", "resolution", id.ToString(), After: req), ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.resolution_added", "minutes", minutesId.ToString(), After: new { resolutionId = id, req.TitleAr, req.ResolutionType }), ct);
        }, ct);
        return Results.Created($"/api/v1/resolutions/{id}", await db.QuerySingleAsync(Select + " WHERE r.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Edit(Guid id, EditRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionManage, Scope.Record("resolution", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var r = await tx.QuerySingleAsync("SELECT status, minutes_id, resolution_type, target_body_id, target_authority_text FROM resolutions.resolutions WHERE id = @id FOR UPDATE",
                new { id }, x => (Status: x.GetString(0), Minutes: x.Uuid("minutes_id"), Type: x.GetString(2), Target: x.Uuid("target_body_id"), TargetText: x.Str("target_authority_text")), ct);
            if (r == default) throw new NotFoundException();
            if (r.Status != "proposed") throw new ConflictException("resolution-issued", "القرار الصادر لا يُعدل؛ يُصدر قرار لاحق يعدّله أو يلغيه");
            if (r.Minutes is null) throw new ConflictException("invalid-transition", "القرار غير مرتبط بمحضر");
            var m = await EditableMinutesAsync(tx, r.Minutes.Value, ct);
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM deliberation.motions WHERE resolution_id = @id AND status = 'closed' AND result <> 'withdrawn'", new { id }, ct) > 0
                && (req.TextAr is not null || req.TitleAr is not null || req.ResolutionType is not null))
                throw new ConflictException("voted-text-locked", "صُوِّت على هذا النص؛ لتغييره اسحب القرار وأضف قرارًا جديدًا ثم صوّت عليه");
            var type = req.ResolutionType ?? r.Type;
            if (type == "decision" && (req.TargetBodyId is not null || req.TargetAuthorityText is not null || req.ResolutionType == "decision" && (r.Target is not null || r.TargetText is not null)))
                throw AppValidationException.For("targetBodyId", "الرفع لجهة أعلى للتوصيات فقط");
            await ValidateLinksAsync(tx, m.MeetingId, m.BodyId, req.AgendaItemId, req.TargetBodyId, null, id, ct);
            await tx.ExecuteAsync("""
                UPDATE resolutions.resolutions SET
                  resolution_type = COALESCE(@ResolutionType, resolution_type), title_ar = COALESCE(@TitleAr, title_ar), text_ar = COALESCE(@TextAr, text_ar),
                  text_en = COALESCE(@TextEn, text_en), requires_follow_up = COALESCE(@RequiresFollowUp, requires_follow_up), category = COALESCE(@Category, category),
                  agenda_item_id = COALESCE(@AgendaItemId, agenda_item_id), authority_text = COALESCE(@AuthorityText, authority_text),
                  target_body_id = COALESCE(@TargetBodyId, target_body_id), target_authority_text = COALESCE(@TargetAuthorityText, target_authority_text),
                  responsible_org_unit_id = COALESCE(@ResponsibleOrgUnitId, responsible_org_unit_id),
                  responsible_external_entity_id = COALESCE(@ResponsibleExternalEntityId, responsible_external_entity_id),
                  responsible_text = COALESCE(@ResponsibleText, responsible_text), adoption_method = COALESCE(@AdoptionMethod, adoption_method),
                  position = COALESCE(@Position, position), updated_by = @by
                WHERE id = @id
                """, new
            {
                id, req.ResolutionType, req.TitleAr, req.TextAr, req.TextEn, req.RequiresFollowUp, req.Category, req.AgendaItemId, req.AuthorityText, req.TargetBodyId,
                req.TargetAuthorityText, req.ResponsibleOrgUnitId, req.ResponsibleExternalEntityId, req.ResponsibleText, req.AdoptionMethod, req.Position, by = me.UserId,
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.edited", "resolution", id.ToString(), After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + " WHERE r.id = @id", new { id }, Map, ct));
    }

    /// <summary>سحب قرار مقترح من المسودة؛ يبقى السجل محفوظًا بحالة «مسحوب» ولا يُحذف.</summary>
    private static async Task<IResult> Withdraw(Guid id, ReasonRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, ITaskActivator tasks, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionManage, Scope.Record("resolution", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var r = await tx.QuerySingleAsync("SELECT status, minutes_id FROM resolutions.resolutions WHERE id = @id FOR UPDATE", new { id }, x => (Status: x.GetString(0), Minutes: x.Uuid("minutes_id")), ct);
            if (r == default) throw new NotFoundException();
            if (r.Status != "proposed") throw new ConflictException("invalid-transition", "يُسحب القرار المقترح فقط");
            if (r.Minutes is { } mid) await EditableMinutesAsync(tx, mid, ct);
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM deliberation.motions WHERE resolution_id = @id AND status = 'open'", new { id }, ct) > 0)
                throw new ConflictException("motion-open", "يوجد تصويت مفتوح على القرار");
            await tx.ExecuteAsync("UPDATE resolutions.resolutions SET status = 'withdrawn', status_reason = @Reason, updated_by = @by WHERE id = @id", new { id, req.Reason, by = me.UserId }, ct);
            await HistoryAsync(tx, id, "proposed", "withdrawn", req.Reason, null, me.UserId, ct);
            await tasks.ActivateAsync(tx, [], [id], me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.withdrawn", "resolution", id.ToString(), After: req), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>انتقالات القرار بعد إصداره (08 §3): بدء التنفيذ، والتنفيذ، والإغلاق، والرفع لجهة أعلى، وتسجيل بتّها، والإلغاء بسبب ومرجع.</summary>
    private static async Task<IResult> Transition(Guid id, TransitionRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionManage, Scope.Record("resolution", id), ct);
        var to = await db.InTransactionAsync(async tx =>
        {
            var r = await tx.QuerySingleAsync("""
                SELECT status, resolution_type, requires_follow_up, target_body_id, target_authority_text, number, title_ar FROM resolutions.resolutions WHERE id = @id FOR UPDATE
                """, new { id }, x => (Status: x.GetString(0), Type: x.GetString(1), Follow: x.GetBoolean(2), Target: x.Uuid("target_body_id"),
                TargetText: x.Str("target_authority_text"), Number: x.Str("number"), Title: x.GetString(6)), ct);
            if (r == default) throw new NotFoundException();
            string Need(string? v, string field, string msg) => string.IsNullOrWhiteSpace(v) ? throw AppValidationException.For(field, msg) : v!;
            var (target, reason) = (req.Action, r.Status) switch
            {
                ("start_execution", "issued") => ("in_execution", req.Reason),
                ("mark_executed", "issued" or "in_execution") => ("executed", Need(req.Reason, "reason", "اذكر ما يثبت التنفيذ")),
                ("close", "executed") => ("closed", req.Reason),
                ("refer", "issued") when r.Type == "recommendation" => ("referred", req.Reason),
                ("record_outcome", "referred") => ("closed", Need(req.Reason, "reason", "اذكر قرار الجهة الأعلى")),
                ("cancel", "issued" or "in_execution" or "referred") => ("cancelled", Need(req.Reason, "reason", "سبب الإلغاء مطلوب")),
                _ => throw new ConflictException("invalid-transition", "الانتقال غير مسموح من حالة القرار الحالية"),
            };
            if (req.Action == "refer" && r.Target is null && r.TargetText is null) throw AppValidationException.For("action", "حدد الجهة الأعلى في التوصية أولًا");
            if (req.Action == "record_outcome" && req.Outcome is null) throw AppValidationException.For("outcome", "حدد بتّ الجهة الأعلى: اعتماد أو رفض أو تعديل");
            if (req.Action == "cancel") Need(req.Reference, "reference", "مرجع الإلغاء (قرار لاحق أو محضر) مطلوب");
            await tx.ExecuteAsync("""
                UPDATE resolutions.resolutions SET status = @target::text, status_reason = COALESCE(@reason::text, status_reason), updated_by = @by,
                  referral_outcome = CASE WHEN @action::text = 'record_outcome' THEN @outcome::text ELSE referral_outcome END,
                  referral_note = CASE WHEN @action::text = 'record_outcome' THEN @reason::text ELSE referral_note END,
                  referral_decided_at = CASE WHEN @action::text = 'record_outcome' THEN now() ELSE referral_decided_at END
                WHERE id = @id
                """, new { id, target, reason, by = me.UserId, action = req.Action, outcome = req.Outcome }, ct);
            await HistoryAsync(tx, id, r.Status, target, reason, req.Reference, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution." + req.Action, "resolution", id.ToString(), Before: new { status = r.Status }, After: new { status = target, req.Reason, req.Reference, req.Outcome }), ct);
            if (req.Action == "refer") await NotifyTargetAsync(tx, notifier, id, r.Target, r.Number, r.Title, ct);
            return target;
        }, ct);
        return Results.Ok(new { id, status = to });
    }

    internal static async Task NotifyTargetAsync(ISql tx, INotifier notifier, Guid id, Guid? targetBody, string? number, string title, CancellationToken ct)
    {
        if (targetBody is null) return;
        var people = await tx.QueryAsync("""
            SELECT DISTINCT user_id FROM bodies.body_members WHERE body_id = @targetBody AND status = 'active' AND role IN ('chair','secretary')
            """, new { targetBody }, r => r.GetGuid(0), ct);
        foreach (var u in people)
            await notifier.NotifyAsync(tx, new NotificationRequest(u, "recommendation_referred", $"توصية مرفوعة لاعتمادكم: {number} {title}",
                $"Recommendation referred for approval: {number}", $"/resolutions/{id}", $"referred:{id}:{u}", "warning"), ct);
    }
}

/// <summary>إصدار قرارات المحضر داخل معاملة اعتماده: الترقيم بتسلسل سنوي لكل جهة، والحالة من نتيجة آخر تصويت مغلق،
/// والربط بالإصدار المعتمد. القرار المصدر سابقًا لا يُعاد إصداره، فلا يتكرر رقمه ولا سجله عند إعادة الاعتماد بعد التعديل.</summary>
internal sealed class ResolutionIssuer(IVotingQuery voting, INumberingService numbering, IClock clock, INotifier notifier, IAuditWriter audit) : IResolutionIssuer
{
    public async Task<IssueSummary> IssueForMinutesAsync(ISql tx, Guid minutesId, Guid minutesVersionId, Guid by, CancellationToken ct = default)
    {
        var rows = await tx.QueryAsync("""
            SELECT r.id, r.meeting_id, r.resolution_type, r.adoption_method, r.supersedes_resolution_id, r.supersede_kind, r.target_body_id, r.target_authority_text,
                   r.title_ar, b.code
            FROM resolutions.resolutions r JOIN bodies.bodies b ON b.id = r.body_id
            WHERE r.minutes_id = @minutesId AND r.status = 'proposed' ORDER BY r.position, r.created_at FOR UPDATE OF r
            """, new { minutesId }, r => (Id: r.GetGuid(0), Meeting: r.Uuid("meeting_id"), Type: r.GetString(2), Adoption: r.GetString(3),
            Supersedes: r.Uuid("supersedes_resolution_id"), Kind: r.Str("supersede_kind"), Target: r.Uuid("target_body_id"), TargetText: r.Str("target_authority_text"),
            Title: r.GetString(8), Code: r.GetString(9)), ct);
        var outcomes = rows.Count == 0 || rows[0].Meeting is null ? [] : (await voting.LatestOutcomesAsync(tx, rows[0].Meeting!.Value, ct)).ToDictionary(o => o.ResolutionId);
        List<Guid> issued = [], notIssued = [];
        List<string> numbers = [];
        foreach (var r in rows)
        {
            outcomes.TryGetValue(r.Id, out var o);
            var (status, reason) = o?.Result switch
            {
                null or "adopted" => ("issued", (string?)null),
                "rejected" => ("rejected", "رُفض بالتصويت"),
                "no_quorum" => ("deferred", "لم يكتمل نصاب التصويت"),
                _ => ("deferred", "تعادل الأصوات دون ترجيح"),
            };
            var adoption = o?.Method switch { null => r.Adoption, "consensus" => "consensus", "circulation" => "circulation", _ => "vote" };
            if (status != "issued")
            {
                await tx.ExecuteAsync("""
                    UPDATE resolutions.resolutions SET status = @status, status_reason = @reason, adoption_method = @adoption, vote_summary = @tally::jsonb,
                      minutes_version_id = @minutesVersionId, approved_at = now(), updated_by = @by WHERE id = @id
                    """, new { id = r.Id, status, reason, adoption, tally = o?.TallyJson, minutesVersionId, by }, ct);
                await ResolutionsModule.HistoryAsync(tx, r.Id, "proposed", status, reason, null, by, ct);
                notIssued.Add(r.Id);
                continue;
            }
            var year = clock.TodayLocal.Year;
            var seq = await numbering.NextAsync(tx, $"resolution:{r.Code}", year, ct);
            var number = $"{r.Code}/{year}/{seq:000}";
            var referred = r.Type == "recommendation" && (r.Target is not null || r.TargetText is not null);
            await tx.ExecuteAsync("""
                UPDATE resolutions.resolutions SET status = @final, number = @number, sequence_year = @year, sequence_no = @seq, issued_at = now(), approved_at = now(),
                  adoption_method = @adoption, vote_summary = @tally::jsonb, minutes_version_id = @minutesVersionId, updated_by = @by
                WHERE id = @id
                """, new { id = r.Id, final = referred ? "referred" : "issued", number, year, seq, adoption, tally = o?.TallyJson, minutesVersionId, by }, ct);
            await ResolutionsModule.HistoryAsync(tx, r.Id, "proposed", "issued", "اعتماد المحضر", null, by, ct);
            if (referred)
            {
                await ResolutionsModule.HistoryAsync(tx, r.Id, "issued", "referred", "توصية تتطلب اعتماد جهة أعلى", null, by, ct);
                await ResolutionsModule.NotifyTargetAsync(tx, notifier, r.Id, r.Target, number, r.Title, ct);
            }
            if (r.Supersedes is { } prev)
            {
                var prevStatus = await tx.ScalarAsync<string>("SELECT status FROM resolutions.resolutions WHERE id = @prev FOR UPDATE", new { prev }, ct);
                if (r.Kind == "revokes" && prevStatus is "issued" or "in_execution" or "executed" or "referred")
                {
                    await tx.ExecuteAsync("UPDATE resolutions.resolutions SET status = 'superseded', status_reason = @why, updated_by = @by WHERE id = @prev",
                        new { prev, why = $"أُلغي بالقرار {number}", by }, ct);
                    await ResolutionsModule.HistoryAsync(tx, prev, prevStatus, "superseded", "أُلغي بقرار لاحق", number, by, ct);
                }
                else
                    await ResolutionsModule.HistoryAsync(tx, prev, prevStatus, prevStatus ?? "issued", "عُدّل بقرار لاحق", number, by, ct);
                await audit.WriteAsync(tx, new AuditEntry("RES", r.Kind == "revokes" ? "resolution.revoked" : "resolution.amended", "resolution", prev.ToString(),
                    After: new { by = r.Id, number }), ct);
            }
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.issued", "resolution", r.Id.ToString(), After: new { number, adoption, minutesVersionId }), ct);
            issued.Add(r.Id);
            numbers.Add(number);
        }
        return new IssueSummary(issued, notIssued, numbers);
    }
}

internal sealed class ResolutionBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "resolution";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT body_id FROM resolutions.resolutions WHERE id = @recordId", new { recordId }, ct);
}

/// <summary>أثر المهام على القرار: بدء أول مهمة ينقل القرار الصادر إلى «قيد التنفيذ»، واكتمال كل مهامه يُشعر أمين السر لتسجيل التنفيذ
/// (لا يُسجل التنفيذ آليًا: يبقى قرارًا بشريًا بإثبات).</summary>
internal sealed class ResolutionExecutionHook(INotifier notifier, IAuditWriter audit) : IResolutionExecutionHook
{
    public async Task TaskStartedAsync(ISql tx, Guid resolutionId, Guid by, CancellationToken ct = default)
    {
        var n = await tx.ExecuteAsync("UPDATE resolutions.resolutions SET status = 'in_execution', updated_by = @by WHERE id = @resolutionId AND status = 'issued'",
            new { resolutionId, by }, ct);
        if (n == 0) return;
        await ResolutionsModule.HistoryAsync(tx, resolutionId, "issued", "in_execution", "بدء تنفيذ أول مهمة مرتبطة", null, by, ct);
        await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.start_execution", "resolution", resolutionId.ToString(), After: new { source = "task_started" }, ActorUserId: by), ct);
    }

    public async Task TasksCompletedAsync(ISql tx, Guid resolutionId, Guid by, CancellationToken ct = default)
    {
        var r = await tx.QuerySingleAsync("SELECT body_id, number, title_ar, status FROM resolutions.resolutions WHERE id = @resolutionId", new { resolutionId },
            x => (Body: x.GetGuid(0), Number: x.Str("number"), Title: x.GetString(2), Status: x.GetString(3)), ct);
        if (r == default || r.Status is not ("issued" or "in_execution")) return;
        var users = await tx.QueryAsync("""
            SELECT DISTINCT user_id FROM bodies.body_members WHERE body_id = @b AND status = 'active' AND role IN ('secretary','chair')
              AND start_date <= (now() AT TIME ZONE 'Asia/Riyadh')::date AND (end_date IS NULL OR end_date >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
            """, new { b = r.Body }, x => x.GetGuid(0), ct);
        foreach (var u in users)
            await notifier.NotifyAsync(tx, new NotificationRequest(u, "resolution_tasks_completed", $"اكتملت كل مهام القرار {r.Number}؛ سجّل تنفيذه",
                $"All tasks of resolution {r.Number} completed", $"/resolutions/{resolutionId}", $"resolution_tasks_completed:{resolutionId}:{u}"), ct);
    }
}
