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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ccms.Modules.Minutes;

/// <summary>المحاضر: الإنشاء من الاجتماع بقالب قابل للتهيئة، والمحرر (أقسام نصية + ملخص كل بند)، واللقطة الآلية (الحضور، النصاب، القرارات، التصويت،
/// التحفظات، المهام، المرفقات)، والإصدارات غير القابلة للتعديل ببصمة، والتعليقات، ودورة الاعتماد القابلة للتهيئة لكل جهة، وتصدير PDF.</summary>
public sealed class MinutesModule : IModule
{
    public string Code => "MIN";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRecordBodyResolver, MinutesBodyResolver>();
        services.AddScoped<IFeatureImpactProvider, SignatureImpact>();
        // وحدة التوقيع (المرحلة 6) تسجل بوابتها الفعلية؛ حتى ذلك يعمل المسار البديل المحدد في سياسة الجهة
        services.TryAddScoped<ISignatureGateway, PendingSignatureGateway>();
        services.AddScoped<MinutesService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/minutes").WithTags("Minutes");
        g.MapGet("/", List);
        g.MapGet("/templates", Templates.List);
        g.MapPost("/templates", Templates.Create).Validate<Templates.TemplateRequest>();
        g.MapPut("/templates/{templateId:guid}", Templates.Update).Validate<Templates.TemplateRequest>();
        g.MapGet("/verify/{code}", Verify);
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}/draft", SaveDraft).Validate<DraftRequest>();
        g.MapGet("/{id:guid}/versions", Versions);
        g.MapGet("/{id:guid}/versions/{no:int}", Version);
        g.MapGet("/{id:guid}/pdf", Pdf);
        g.MapGet("/{id:guid}/activity", Activity);
        g.MapGet("/{id:guid}/comments", Comments);
        g.MapPost("/{id:guid}/comments", AddComment).Validate<CommentRequest>().Idempotent();
        g.MapPost("/comments/{commentId:guid}/resolve", ResolveComment).Validate<ResolveCommentRequest>();
        Workflow.Map(g);
        api.MapPost("/meetings/{meetingId:guid}/minutes", Create).WithTags("Minutes").Idempotent();
    }

    // ---------------- الطلبات ----------------

    public sealed record Section(
        [property: Required, MaxLength(100)] string Key,
        [property: Required, MaxLength(300)] string Title,
        [property: MaxLength(50000)] string? Body);

    public sealed record ItemText([property: Required] Guid? AgendaItemId, [property: MaxLength(20000)] string? Discussion, [property: MaxLength(5000)] string? Recommendation);

    public sealed record DraftRequest([property: Required, MaxLength(50)] List<Section> Sections, [property: MaxLength(200)] List<ItemText>? Items = null) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            foreach (var (s, i) in Sections.Select((s, i) => (s, i)))
                foreach (var r in ValidationHelper.Validate(s))
                    yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"sections[{i}].{m}"));
            if (Sections.Select(s => s.Key).Distinct().Count() != Sections.Count) yield return new("مفاتيح الأقسام مكررة", [nameof(Sections)]);
            foreach (var (s, i) in (Items ?? []).Select((s, i) => (s, i)))
                foreach (var r in ValidationHelper.Validate(s))
                    yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"items[{i}].{m}"));
        }
    }

    public sealed record CreateRequest(Guid? TemplateId);

    public sealed record CommentRequest([property: Required, MaxLength(100)] string Anchor, [property: Required, MinLength(2), MaxLength(4000)] string Text);

    public sealed record ResolveCommentRequest([property: Required, OneOf("accepted", "rejected")] string Status, [property: MaxLength(2000)] string? Note);

    // ---------------- القراءة ----------------

    internal const string Select = """
        SELECT mi.id, mi.meeting_id, mi.status, mi.current_version_no, mi.draft_content::text AS draft_content, mi.updated_at, mi.approved_at,
               mi.workflow::text AS workflow, mi.template_id, mi.amendment_no, mi.amendment_reason, mi.review_deadline, mi.status_reason, mi.status_changed_at,
               mi.verification_code, mi.pdf_document_id, mi.pdf_sha256, mi.classification, mi.approved_version_id,
               av.version_no AS approved_version_no, ua.display_name_ar AS approved_by_name,
               m.reference_no, m.title_ar AS meeting_title, m.starts_at, m.status AS meeting_status, m.meeting_type, m.body_id, b.name_ar AS body_name, b.code AS body_code,
               (SELECT v.content_hash FROM minutes.minutes_versions v WHERE v.minutes_id = mi.id ORDER BY v.version_no DESC LIMIT 1) AS current_hash,
               (SELECT count(*) FROM resolutions.resolutions r WHERE r.minutes_id = mi.id AND r.status <> 'withdrawn') AS resolution_count,
               (SELECT count(*) FROM minutes.minutes_comments c WHERE c.minutes_id = mi.id AND c.status = 'open') AS open_comments
        FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
        LEFT JOIN minutes.minutes_versions av ON av.id = mi.approved_version_id
        LEFT JOIN iam.users ua ON ua.id = mi.approved_by
        """;

    internal static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), meetingId = r.Get<Guid>("meeting_id"), status = r.Get<string>("status"), versionNo = r.Get<int>("current_version_no"),
        content = JsonSerializer.Deserialize<JsonElement>(r.Get<string>("draft_content")), updatedAt = r.Get<DateTime>("updated_at"),
        approvedAt = r.Time("approved_at"), approvedByName = r.Str("approved_by_name"),
        approvedVersionNo = r.IsDBNull(r.GetOrdinal("approved_version_no")) ? (int?)null : r.Get<int>("approved_version_no"),
        workflow = JsonSerializer.Deserialize<JsonElement>(r.Get<string>("workflow")), templateId = r.Get<Guid>("template_id"),
        amendmentNo = r.Get<int>("amendment_no"), amendmentReason = r.Str("amendment_reason"), reviewDeadline = r.Time("review_deadline"),
        statusReason = r.Str("status_reason"), statusChangedAt = r.Get<DateTime>("status_changed_at"), verificationCode = r.Str("verification_code"),
        pdfDocumentId = r.Uuid("pdf_document_id"), pdfSha256 = r.Str("pdf_sha256"), classification = r.Get<string>("classification"),
        meetingReference = r.Str("reference_no"), meetingTitle = r.Get<string>("meeting_title"), meetingStartsAt = r.Get<DateTime>("starts_at"),
        meetingStatus = r.Get<string>("meeting_status"), meetingType = r.Get<string>("meeting_type"),
        bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), bodyCode = r.Get<string>("body_code"),
        contentHash = r.Str("current_hash"), resolutionCount = r.Get<long>("resolution_count"), openComments = r.Get<long>("open_comments"),
    };

    private static async Task<IResult> List(Guid? bodyId, string? status, string? q, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.MinutesView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, status, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR m.body_id = ANY(@ids::uuid[])) AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId)
              AND (@status::text IS NULL OR mi.status = @status)
              AND (@q::text IS NULL OR m.title_ar ILIKE @q OR m.reference_no ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id " + "\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY m.starts_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, MinutesService svc, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var minutes = await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var row = await svc.LoadAsync(db, id, false, ct);
        var template = await Templates.GetSectionsAsync(db, row.TemplateId, ct);
        var current = await svc.CurrentVersionAsync(db, id, ct);
        var editable = row.Status is "draft" or "returned";
        // اللقطة: حية أثناء التحرير، ومجمدة من الإصدار الحالي أثناء المراجعة والاعتماد
        MinutesSnapshot snapshot = editable || current is null
            ? await SnapshotBuilder.BuildAsync(db, id, MinutesContent.Parse(row.ContentJson), ct)
            : MinutesSnapshot.Parse(await db.ScalarAsync<string>("SELECT snapshot::text FROM minutes.minutes_versions WHERE id = @vid", new { vid = current.Id }, ct))!;
        var signoffs = await svc.SignoffsAsync(db, id, current?.Id, ct);
        var approvals = await svc.ApprovalsAsync(db, id, ct);
        var permissions = await access.GetPermissionsAsync(Scope.Record("minutes", id), ct);
        var actions = await svc.ActionsAsync(db, row, current, permissions, me.UserId, ct);
        var gateway = await svc.SignatureMethodAsync(row.Workflow, ct);
        return Results.Ok(new
        {
            minutes, template, snapshot, signoffs, approvals, actions,
            steps = row.Workflow.Steps(row.AmendmentNo > 0),
            currentVersion = current is null ? null : new { no = current.No, hash = current.Hash, createdAt = current.CreatedAt, createdBy = current.CreatedByName, current.ChangeSummary },
            signatureMethod = new { method = gateway.Method, reason = gateway.Reason },
            permissions = permissions.Order(),
        });
    }

    private static async Task<IResult> Create(Guid meetingId, HttpRequest http, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("meeting", meetingId), ct);
        Guid? templateId = null;
        if (http.ContentLength is > 0 && http.HasJsonContentType())
            templateId = (await http.ReadFromJsonAsync<CreateRequest>(Json.Options, ct))?.TemplateId;
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("""
                SELECT m.status, m.classification, COALESCE(p.minutes_workflow, ep.minutes_workflow)::text AS workflow
                FROM meetings.meetings m
                LEFT JOIN bodies.body_policies p ON p.id = m.policy_id
                LEFT JOIN LATERAL (SELECT minutes_workflow FROM bodies.body_policies x WHERE x.body_id = m.body_id ORDER BY x.effective_from DESC, x.version DESC LIMIT 1) ep ON true
                WHERE m.id = @meetingId FOR UPDATE OF m
                """, new { meetingId }, r => (Status: r.GetString(0), Cls: r.GetString(1), Workflow: r.Str("workflow")), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is "draft" or "cancelled" or "postponed") throw new ConflictException("invalid-transition", "لا يُنشأ محضر لاجتماع غير مجدول أو ملغى");
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM minutes.minutes WHERE meeting_id = @meetingId", new { meetingId }, ct) > 0)
                throw new ConflictException("duplicate", "للاجتماع محضر مسبقًا");
            var workflow = MinutesWorkflow.Parse(m.Workflow);
            var template = templateId is { } tid
                ? await tx.ScalarAsync<Guid?>("SELECT id FROM minutes.templates WHERE id = @tid AND status = 'active'", new { tid }, ct) ?? throw AppValidationException.For("templateId", "القالب غير متاح")
                : await tx.ScalarAsync<Guid?>("SELECT id FROM minutes.templates WHERE code = @code AND status = 'active'", new { code = workflow.TemplateCode }, ct)
                  ?? await tx.ScalarAsync<Guid>("SELECT id FROM minutes.templates WHERE is_default", null, ct);
            var sections = await Templates.GetSectionsAsync(tx, template, ct);
            var items = await tx.QueryAsync("SELECT id, discussion_summary, recommendation_text FROM meetings.agenda_items WHERE meeting_id = @meetingId ORDER BY position",
                new { meetingId }, r => new ContentItem(r.GetGuid(0), r.Str("discussion_summary"), r.Str("recommendation_text")), ct);
            var content = new MinutesContent(sections.Where(s => s.Kind == "text").Select(s => new ContentSection(s.Key, s.TitleAr, s.DefaultBody ?? "")).ToList(), items);
            await tx.ExecuteAsync("""
                INSERT INTO minutes.minutes (id, meeting_id, draft_content, classification, created_by, template_id, workflow)
                VALUES (@id, @meetingId, @content::jsonb, @cls, @by, @template, @workflow::jsonb)
                """, new { id, meetingId, content = content.ToJson(), cls = m.Cls, by = me.UserId, template, workflow = workflow.ToJson() }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.created", "minutes", id.ToString(), After: new { meetingId, template, workflow }), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.minutes_created", "meeting", meetingId.ToString(), After: new { minutesId = id }), ct);
        }, ct);
        return Results.Created($"/api/v1/minutes/{id}", await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct));
    }

    /// <summary>حفظ المسودة: في حالتي المسودة والمعاد للتعديل فقط؛ المحضر قيد المراجعة أو المعتمد لا يُعدل بصمت (BRL-07).</summary>
    private static async Task<IResult> SaveDraft(Guid id, DraftRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var row = await tx.QuerySingleAsync("SELECT status, meeting_id, draft_content::text FROM minutes.minutes WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Meeting: r.GetGuid(1), Content: r.GetString(2)), ct);
            if (row == default) throw new NotFoundException();
            if (row.Status == "approved") throw new ConflictException("minutes-approved", "المحضر المعتمد لا يُعدل؛ افتح تعديلًا بسبب موثق لينتج إصدارًا جديدًا يُعاد اعتماده");
            if (row.Status is not ("draft" or "returned")) throw new ConflictException("minutes-in-review", "المحضر قيد المراجعة؛ لا يُعدل إلا بعد إعادته للتعديل");
            var previous = MinutesContent.Parse(row.Content);
            var validItems = (await tx.QueryAsync("SELECT id FROM meetings.agenda_items WHERE meeting_id = @m", new { m = row.Meeting }, r => r.GetGuid(0), ct)).ToHashSet();
            if (req.Items is { } its && its.Any(i => !validItems.Contains(i.AgendaItemId!.Value)))
                throw AppValidationException.For("items", "بند لا يتبع اجتماع هذا المحضر");
            var items = req.Items is null ? previous.Items : req.Items.Select(i => new ContentItem(i.AgendaItemId!.Value, i.Discussion, i.Recommendation)).ToList();
            var content = new MinutesContent(req.Sections.Select(s => new ContentSection(s.Key, s.Title, s.Body)).ToList(), items);
            await tx.ExecuteAsync("UPDATE minutes.minutes SET draft_content = @content::jsonb WHERE id = @id", new { id, content = content.ToJson() }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.draft_saved", "minutes", id.ToString(), After: new { sections = req.Sections.Count, items = items.Count, status = row.Status }), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Versions(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var approvedId = await db.ScalarAsync<Guid?>("SELECT approved_version_id FROM minutes.minutes WHERE id = @id", new { id }, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT v.id, v.version_no, v.content_hash, v.created_at, u.display_name_ar, v.change_summary, v.amendment_no, v.created_in_status, v.source,
                   (SELECT count(*) FROM resolutions.resolutions r WHERE r.minutes_version_id = v.id) AS issued_here,
                   EXISTS (SELECT 1 FROM minutes.minutes_approvals a WHERE a.minutes_version_id = v.id AND a.decision = 'approved') AS approved
            FROM minutes.minutes_versions v LEFT JOIN iam.users u ON u.id = v.created_by WHERE v.minutes_id = @id ORDER BY v.version_no DESC
            """, new { id }, r => new
        {
            id = r.GetGuid(0), versionNo = r.GetInt32(1), contentHash = r.GetString(2), createdAt = r.GetDateTime(3), createdBy = r.Str("display_name_ar"),
            changeSummary = r.Str("change_summary"), amendmentNo = r.GetInt32(6), createdInStatus = r.Str("created_in_status"), source = r.GetString(8),
            resolutionsIssued = r.GetInt64(9), approved = r.GetBoolean(10), current = r.GetGuid(0) == approvedId,
        }, ct));
    }

    private static async Task<IResult> Version(Guid id, int no, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var v = await db.QuerySingleAsync("""
            SELECT v.id, v.version_no, v.content::text, v.snapshot::text, v.content_hash, v.created_at, u.display_name_ar, v.change_summary, v.amendment_no
            FROM minutes.minutes_versions v LEFT JOIN iam.users u ON u.id = v.created_by WHERE v.minutes_id = @id AND v.version_no = @no
            """, new { id, no }, r => new
        {
            id = r.GetGuid(0), versionNo = r.GetInt32(1), content = JsonSerializer.Deserialize<JsonElement>(r.GetString(2)),
            snapshot = JsonSerializer.Deserialize<JsonElement>(r.GetString(3)), contentHash = r.GetString(4), createdAt = r.GetDateTime(5),
            createdBy = r.Str("display_name_ar"), changeSummary = r.Str("change_summary"), amendmentNo = r.GetInt32(8),
        }, ct) ?? throw new NotFoundException();
        var resolutions = await db.QueryAsync("SELECT id, number, title_ar, status FROM resolutions.resolutions WHERE minutes_version_id = @vid ORDER BY number",
            new { vid = v.id }, r => new { id = r.GetGuid(0), number = r.Str("number"), titleAr = r.GetString(2), status = r.GetString(3) }, ct);
        return Results.Ok(new { version = v, issuedResolutions = resolutions });
    }

    /// <summary>تصدير PDF: الإصدار المعتمد يُعاد من المستند المحفوظ ببصمته؛ وغيره يُولّد عند الطلب بعلامة «مسودة».</summary>
    private static async Task<IResult> Pdf(Guid id, int? versionNo, Db db, IAccessService access, MinutesService svc, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var (bytes, name) = await svc.PdfAsync(id, versionNo, ct);
        await audit.WriteAsync(new AuditEntry("MIN", "minutes.pdf_exported", "minutes", id.ToString(), After: new { versionNo, size = bytes.Length }), ct);
        return Results.File(bytes, "application/pdf", name);
    }

    private static async Task<IResult> Verify(string code, Db db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var r = await db.QuerySingleAsync("""
            SELECT m.reference_no, b.name_ar, v.version_no, v.content_hash, mi.pdf_sha256, mi.approved_at, mi.status, mi.amendment_no
            FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
            LEFT JOIN minutes.minutes_versions v ON v.id = mi.approved_version_id WHERE mi.verification_code = @code
            """, new { code = code.ToUpperInvariant() }, x => new
        {
            meetingReference = x.Str("reference_no"), bodyName = x.GetString(1), approvedVersionNo = x.IsDBNull(2) ? (int?)null : x.GetInt32(2),
            contentHash = x.Str("content_hash"), pdfSha256 = x.Str("pdf_sha256"), approvedAt = x.Time("approved_at"), status = x.GetString(6), amendmentNo = x.GetInt32(7),
        }, ct) ?? throw new NotFoundException("رمز التحقق غير صحيح");
        return Results.Ok(r);
    }

    private static async Task<IResult> Activity(Guid id, IAccessService access, IActivityQuery activity, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        return Results.Ok(await activity.ForEntityAsync("minutes", id, 300, ct));
    }

    // ---------------- التعليقات ----------------

    private static async Task<IResult> Comments(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT c.id, c.anchor, c.text, c.status, c.resolution_note, c.created_at, c.resolved_at, ua.display_name_ar AS author, ur.display_name_ar AS resolver,
                   v.version_no, c.author_user_id
            FROM minutes.minutes_comments c JOIN iam.users ua ON ua.id = c.author_user_id LEFT JOIN iam.users ur ON ur.id = c.resolved_by
            LEFT JOIN minutes.minutes_versions v ON v.id = c.minutes_version_id
            WHERE c.minutes_id = @id ORDER BY c.created_at
            """, new { id }, r => new
        {
            id = r.GetGuid(0), anchor = r.GetString(1), text = r.GetString(2), status = r.GetString(3), resolutionNote = r.Str("resolution_note"),
            createdAt = r.GetDateTime(5), resolvedAt = r.Time("resolved_at"), author = r.GetString(7), resolver = r.Str("resolver"),
            versionNo = r.IsDBNull(9) ? (int?)null : r.GetInt32(9), authorUserId = r.GetGuid(10),
        }, ct));
    }

    private static async Task<IResult> AddComment(Guid id, CommentRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesComment, Scope.Record("minutes", id), ct);
        var cid = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var row = await tx.QuerySingleAsync("""
                SELECT mi.status, (SELECT v.id FROM minutes.minutes_versions v WHERE v.minutes_id = mi.id ORDER BY v.version_no DESC LIMIT 1), m.reference_no, m.body_id
                FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.id = @id
                """, new { id }, r => (Status: r.GetString(0), Version: r.Uuid("id"), Ref: r.Str("reference_no"), Body: r.GetGuid(3)), ct);
            if (row == default) throw new NotFoundException();
            if (row.Status == "approved") throw new ConflictException("minutes-approved", "لا تُضاف ملاحظات على محضر معتمد");
            await tx.ExecuteAsync("""
                INSERT INTO minutes.minutes_comments (id, minutes_id, minutes_version_id, author_user_id, anchor, text) VALUES (@cid, @id, @Version, @me, @Anchor, @Text)
                """, new { cid, id, row.Version, me = me.UserId, req.Anchor, req.Text }, ct);
            foreach (var u in await MinutesService.BodyUsersAsync(tx, row.Body, ["secretary"], ct))
                if (u != me.UserId)
                    await notifier.NotifyAsync(tx, new NotificationRequest(u, "minutes_comment", $"ملاحظة جديدة على المحضر {row.Ref}", $"New comment on minutes {row.Ref}",
                        $"/minutes/{id}?tab=comments", $"minutes_comment:{cid}:{u}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.comment_added", "minutes", id.ToString(), After: new { commentId = cid, req.Anchor }), ct);
        }, ct);
        return Results.Created($"/api/v1/minutes/{id}/comments", new { id = cid });
    }

    private static async Task<IResult> ResolveComment(Guid commentId, ResolveCommentRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var minutesId = await db.ScalarAsync<Guid?>("SELECT minutes_id FROM minutes.minutes_comments WHERE id = @commentId", new { commentId }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", minutesId), ct);
        var n = await db.ExecuteAsync("""
            UPDATE minutes.minutes_comments SET status = @Status, resolution_note = @Note, resolved_by = @me, resolved_at = now() WHERE id = @commentId AND status = 'open'
            """, new { commentId, req.Status, req.Note, me = me.UserId }, ct);
        if (n == 0) throw new ConflictException("comment-resolved", "عولجت الملاحظة مسبقًا");
        await audit.WriteAsync(new AuditEntry("MIN", "minutes.comment_resolved", "minutes", minutesId.ToString(), After: new { commentId, req.Status, req.Note }), ct);
        return Results.NoContent();
    }
}

internal static class ValidationHelper
{
    public static IEnumerable<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, true);
        return results;
    }
}

internal sealed class MinutesBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "minutes";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT m.body_id FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.id = @recordId", new { recordId }, ct);
}

/// <summary>قبل تنفيذ وحدة التوقيع (المرحلة 6) لا توجد بوابة جاهزة؛ يُوثَّق السبب ويعمل المسار البديل.</summary>
internal sealed class PendingSignatureGateway(IFeatureService features) : ISignatureGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default) =>
        await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct)
            ? (false, "esign_not_integrated")
            : (false, "esign_disabled");
}

/// <summary>أثر تغيير إعدادات التوقيع على المحاضر التي تنتظر توقيعات (المرحلة 1، البند 5).</summary>
internal sealed class SignatureImpact(Db db) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys =>
        [Contracts.FeatureKeys.ESignatureEnabled, Contracts.FeatureKeys.ESignatureProvider, Contracts.FeatureKeys.ESignatureMethod, Contracts.FeatureKeys.ESignatureMemberRequired];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        if (key == Contracts.FeatureKeys.ESignatureEnabled && newValue == "true") return [];
        if (key == Contracts.FeatureKeys.ESignatureMemberRequired && newValue == "true") return [];
        var pending = await db.QuerySingleAsync("""
            SELECT count(DISTINCT mi.id), count(s.id)
            FROM minutes.minutes mi JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no
            JOIN minutes.minutes_signoffs s ON s.minutes_version_id = v.id
            WHERE mi.status = 'pending_signatures' AND s.status = 'requested'
            """, null, r => (Minutes: r.GetInt64(0), Signoffs: r.GetInt64(1)), ct);
        return
        [
            new FeatureImpactItem("minutes_pending_signoffs", "محاضر تنتظر توقيعات أو إقرارات", "Minutes awaiting signatures", (int)pending.Minutes),
            new FeatureImpactItem("pending_signoff_requests", "طلبات توقيع لم تكتمل", "Incomplete signature requests", (int)pending.Signoffs),
        ];
    }
}
