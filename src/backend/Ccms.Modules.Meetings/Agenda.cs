using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Meetings;

/// <summary>جدول الأعمال: البنود وترتيبها ومناقشتها وتوصياتها، والتأجيل إلى اجتماع لاحق أو إلى قائمة البنود المعلقة، والإحالة إلى جهة أخرى.</summary>
internal static class Agenda
{
    public static void Map(IEndpointRouteBuilder api, RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/agenda-items", Add).Validate<ItemRequest>();
        g.MapPut("/{id:guid}/agenda-items/order", Reorder).Validate<OrderRequest>();
        g.MapPost("/{id:guid}/agenda-items/import", Import).Validate<ImportRequest>();
        var a = api.MapGroup("/agenda-items").WithTags("Meetings");
        a.MapGet("/{itemId:guid}", GetItem);
        a.MapPatch("/{itemId:guid}", Edit).Validate<EditItemRequest>();
        a.MapPost("/{itemId:guid}/defer", Defer).Validate<DeferRequest>();
        a.MapPost("/{itemId:guid}/refer", Refer).Validate<ReferRequest>();
        a.MapPost("/{itemId:guid}/withdraw", Withdraw).Validate<MeetingsModule.ReasonRequest>();
        api.MapGet("/bodies/{bodyId:guid}/pending-items", PendingList).WithTags("Meetings");
        api.MapPost("/pending-items/{pid:guid}/drop", Drop).WithTags("Meetings").Validate<MeetingsModule.ReasonRequest>();
    }

    public sealed record ItemRequest(
        [property: Required, MaxLength(400)] string TitleAr,
        [property: MaxLength(400)] string? TitleEn,
        [property: MaxLength(10000)] string? DescriptionAr,
        [property: OneOf("for_decision", "for_information", "for_discussion")] string? ItemType,
        [property: MaxLength(200)] string? PresenterText,
        Guid? OwnerOrgUnitId,
        [property: MaxLength(300)] string? OwnerText,
        [property: Range(1, 480)] int? EstimatedMinutes,
        [property: MaxLength(20000)] string? MemoText);

    public sealed record EditItemRequest(
        [property: MaxLength(400)] string? TitleAr,
        [property: MaxLength(400)] string? TitleEn,
        [property: MaxLength(10000)] string? DescriptionAr,
        [property: OneOf("for_decision", "for_information", "for_discussion")] string? ItemType,
        [property: MaxLength(200)] string? PresenterText,
        Guid? OwnerOrgUnitId,
        [property: MaxLength(300)] string? OwnerText,
        [property: Range(1, 480)] int? EstimatedMinutes,
        [property: MaxLength(20000)] string? MemoText,
        [property: MaxLength(20000)] string? DiscussionSummary,
        [property: MaxLength(10000)] string? RecommendationText,
        [property: OneOf("pending", "discussed")] string? Status);

    public sealed record OrderRequest([property: Required, MinLength(1)] Guid[] ItemIds);
    public sealed record DeferRequest([property: Required(ErrorMessage = "سبب التأجيل مطلوب"), MinLength(3), MaxLength(1000)] string Reason, Guid? TargetMeetingId);
    public sealed record ReferRequest([property: Required] Guid? TargetBodyId, [property: Required(ErrorMessage = "سبب الإحالة مطلوب"), MinLength(3), MaxLength(1000)] string Reason);
    public sealed record ImportRequest([property: Required, MinLength(1)] Guid[] PendingItemIds);

    private static readonly string[] Preparing = ["draft", "scheduled", "agenda_published"];

    private const string ItemSelect = """
        SELECT a.*, ou.name_ar AS owner_org_unit_name,
               src.meeting_id AS carried_from_meeting_id, srcm.reference_no AS carried_from_reference,
               (SELECT count(*) FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id WHERE l.owner_type = 'agenda_item' AND l.owner_id = a.id AND d.deleted_at IS NULL) AS attachment_count,
               (SELECT count(*) FROM resolutions.resolutions r WHERE r.agenda_item_id = a.id) AS resolution_count,
               (SELECT count(*) FROM tasks.tasks t WHERE t.agenda_item_id = a.id) AS task_count,
               fwd.target_item_id, fwd.target_meeting_id, fwd.target_reference, fwd.pending_body_name, fwd.pending_status
        FROM meetings.agenda_items a
        LEFT JOIN org.org_units ou ON ou.id = a.owner_org_unit_id
        LEFT JOIN meetings.agenda_items src ON src.id = a.carried_from_item_id
        LEFT JOIN meetings.meetings srcm ON srcm.id = src.meeting_id
        LEFT JOIN LATERAL (
          SELECT p.scheduled_item_id AS target_item_id, ti.meeting_id AS target_meeting_id, tm.reference_no AS target_reference, b.name_ar AS pending_body_name, p.status AS pending_status
          FROM meetings.pending_items p JOIN bodies.bodies b ON b.id = p.body_id
          LEFT JOIN meetings.agenda_items ti ON ti.id = p.scheduled_item_id LEFT JOIN meetings.meetings tm ON tm.id = ti.meeting_id
          WHERE p.source_item_id = a.id ORDER BY p.created_at DESC LIMIT 1) fwd ON true
        """;

    private static object MapItem(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), meetingId = r.Get<Guid>("meeting_id"), position = r.Get<int>("position"), titleAr = r.Get<string>("title_ar"), titleEn = r.Str("title_en"),
        descriptionAr = r.Str("description_ar"), itemType = r.Get<string>("item_type"), presenterText = r.Str("presenter_text"),
        ownerOrgUnitId = r.Uuid("owner_org_unit_id"), ownerOrgUnitName = r.Str("owner_org_unit_name"), ownerText = r.Str("owner_text"),
        estimatedMinutes = r.IsDBNull(r.GetOrdinal("estimated_minutes")) ? (int?)null : r.Get<int>("estimated_minutes"),
        status = r.Get<string>("status"), statusReason = r.Str("status_reason"), memoText = r.Str("memo_text"),
        discussionSummary = r.Str("discussion_summary"), recommendationText = r.Str("recommendation_text"),
        carriedFromItemId = r.Uuid("carried_from_item_id"), carriedFromMeetingId = r.Uuid("carried_from_meeting_id"), carriedFromReference = r.Str("carried_from_reference"),
        attachmentCount = r.Get<long>("attachment_count"), resolutionCount = r.Get<long>("resolution_count"), taskCount = r.Get<long>("task_count"),
        forwardedToItemId = r.Uuid("target_item_id"), forwardedToMeetingId = r.Uuid("target_meeting_id"), forwardedToReference = r.Str("target_reference"),
        pendingBodyName = r.Str("pending_body_name"), pendingStatus = r.Str("pending_status"),
    };

    public static Task<List<object>> LoadAsync(ISql db, Guid meetingId, CancellationToken ct) =>
        db.QueryAsync(ItemSelect + "\nWHERE a.meeting_id = @meetingId ORDER BY a.position", new { meetingId }, MapItem, ct);

    private static async Task<(Guid Meeting, Guid Body, string MeetingStatus, string Status, string Title, string? Desc)> LockItemAsync(ISql tx, Guid itemId, CancellationToken ct)
    {
        var x = await tx.QuerySingleAsync("""
            SELECT a.meeting_id, m.body_id, m.status, a.status, a.title_ar, a.description_ar
            FROM meetings.agenda_items a JOIN meetings.meetings m ON m.id = a.meeting_id WHERE a.id = @itemId FOR UPDATE OF a, m
            """, new { itemId }, r => (r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Str("description_ar")), ct);
        if (x == default) throw new NotFoundException();
        return x;
    }

    private static async Task<IResult> GetItem(Guid itemId, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("agenda_item", itemId), ct);
        var item = await db.QuerySingleAsync(ItemSelect + "\nWHERE a.id = @itemId", new { itemId }, MapItem, ct) ?? throw new NotFoundException();
        var resolutions = await db.QueryAsync("SELECT id, number, title_ar, status FROM resolutions.resolutions WHERE agenda_item_id = @itemId ORDER BY created_at",
            new { itemId }, r => new { id = r.Get<Guid>("id"), number = r.Str("number"), titleAr = r.Get<string>("title_ar"), status = r.Get<string>("status") }, ct);
        var tasks = await db.QueryAsync("SELECT id, reference_no, title_ar, status, due_date FROM tasks.tasks WHERE agenda_item_id = @itemId ORDER BY created_at",
            new { itemId }, r => new { id = r.Get<Guid>("id"), referenceNo = r.Str("reference_no"), titleAr = r.Get<string>("title_ar"), status = r.Get<string>("status"),
                dueDate = r.IsDBNull(r.GetOrdinal("due_date")) ? (DateOnly?)null : r.Get<DateOnly>("due_date") }, ct);
        return Results.Ok(new { item, resolutions, tasks });
    }

    private static async Task<IResult> Add(Guid id, ItemRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var itemId = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (!Preparing.Contains(status)) throw new ConflictException("invalid-transition", "لا يُعدل جدول الأعمال بعد انعقاد الاجتماع أو إلغائه");
            await InsertItemAsync(tx, itemId, id, req, null, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_added", "meeting", id.ToString(), After: new { itemId, req.TitleAr, req.ItemType, req.PresenterText }), ct);
        }, ct);
        return Results.Created($"/api/v1/agenda-items/{itemId}", await db.QuerySingleAsync(ItemSelect + "\nWHERE a.id = @itemId", new { itemId }, MapItem, ct));
    }

    private static Task InsertItemAsync(ISql tx, Guid itemId, Guid meetingId, ItemRequest req, Guid? carriedFrom, CancellationToken ct) =>
        tx.ExecuteAsync("""
            INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, title_en, description_ar, item_type, presenter_text, owner_org_unit_id, owner_text,
              estimated_minutes, memo_text, carried_from_item_id, classification)
            SELECT @itemId, @meetingId, COALESCE((SELECT max(position) FROM meetings.agenda_items WHERE meeting_id = @meetingId), 0) + 1,
              @TitleAr, @TitleEn, @DescriptionAr, @type, @PresenterText, @OwnerOrgUnitId, @OwnerText, @EstimatedMinutes, @MemoText, @carriedFrom, m.classification
            FROM meetings.meetings m WHERE m.id = @meetingId
            """, new
        {
            itemId, meetingId, req.TitleAr, req.TitleEn, req.DescriptionAr, type = req.ItemType ?? "for_decision", req.PresenterText, req.OwnerOrgUnitId,
            req.OwnerText, req.EstimatedMinutes, req.MemoText, carriedFrom,
        }, ct);

    /// <summary>تعديل محتوى البند قبل الانعقاد، وتسجيل ملخص النقاش والتوصية وحالة «نوقش» أثناء الاجتماع أو بعده.</summary>
    private static async Task<IResult> Edit(Guid itemId, EditItemRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("agenda_item", itemId), ct);
        await db.InTransactionAsync(async tx =>
        {
            var x = await LockItemAsync(tx, itemId, ct);
            var contentChange = req.TitleAr is not null || req.TitleEn is not null || req.DescriptionAr is not null || req.ItemType is not null || req.PresenterText is not null
                || req.OwnerOrgUnitId is not null || req.OwnerText is not null || req.EstimatedMinutes is not null || req.MemoText is not null;
            var discussionChange = req.DiscussionSummary is not null || req.RecommendationText is not null || req.Status is not null;
            if (contentChange && !Preparing.Contains(x.MeetingStatus) && x.MeetingStatus != "in_progress")
                throw new ConflictException("invalid-transition", "لا يُعدل محتوى البند بعد انتهاء الاجتماع");
            if (discussionChange && x.MeetingStatus is not ("in_progress" or "concluded"))
                throw new ConflictException("invalid-transition", "ملخص النقاش والتوصية يُسجلان أثناء الاجتماع أو بعد انتهائه");
            if (x.Status is "deferred" or "referred" or "withdrawn") throw new ConflictException("invalid-transition", "البند مؤجل أو محال أو مسحوب ولا يُعدل");
            await tx.ExecuteAsync("""
                UPDATE meetings.agenda_items SET title_ar = COALESCE(@TitleAr, title_ar), title_en = COALESCE(@TitleEn, title_en),
                  description_ar = COALESCE(@DescriptionAr, description_ar), item_type = COALESCE(@ItemType, item_type), presenter_text = COALESCE(@PresenterText, presenter_text),
                  owner_org_unit_id = COALESCE(@OwnerOrgUnitId, owner_org_unit_id), owner_text = COALESCE(@OwnerText, owner_text),
                  estimated_minutes = COALESCE(@EstimatedMinutes, estimated_minutes), memo_text = COALESCE(@MemoText, memo_text),
                  discussion_summary = COALESCE(@DiscussionSummary, discussion_summary), recommendation_text = COALESCE(@RecommendationText, recommendation_text),
                  status = COALESCE(@Status, status)
                WHERE id = @itemId
                """, new
            {
                itemId, req.TitleAr, req.TitleEn, req.DescriptionAr, req.ItemType, req.PresenterText, req.OwnerOrgUnitId, req.OwnerText, req.EstimatedMinutes,
                req.MemoText, req.DiscussionSummary, req.RecommendationText, req.Status,
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", discussionChange ? "agenda.item_discussed" : "agenda.item_updated", "meeting", x.Meeting.ToString(),
                After: new { itemId, title = req.TitleAr ?? x.Title, req.Status, req.RecommendationText, summaryRecorded = req.DiscussionSummary is not null }), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(ItemSelect + "\nWHERE a.id = @itemId", new { itemId }, MapItem, ct));
    }

    private static async Task<IResult> Reorder(Guid id, OrderRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (!Preparing.Contains(status) && status != "in_progress") throw new ConflictException("invalid-transition", "لا يُعاد ترتيب جدول أعمال اجتماع منتهٍ");
            var existing = (await tx.QueryAsync("SELECT id FROM meetings.agenda_items WHERE meeting_id = @id", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
            if (req.ItemIds.Length != existing.Count || req.ItemIds.Distinct().Count() != existing.Count || !req.ItemIds.All(existing.Contains))
                throw AppValidationException.For("itemIds", "القائمة يجب أن تتضمن كل بنود الاجتماع مرة واحدة");
            for (var i = 0; i < req.ItemIds.Length; i++)
                await tx.ExecuteAsync("UPDATE meetings.agenda_items SET position = @pos WHERE id = @item", new { pos = i + 1, item = req.ItemIds[i] }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.reordered", "meeting", id.ToString(), After: new { order = req.ItemIds }), ct);
        }, ct);
        return Results.Ok(await LoadAsync(db, id, ct));
    }

    /// <summary>تأجيل البند: إلى اجتماع لاحق محدد للجهة نفسها (يُدرج فيه مباشرة) أو إلى قائمة البنود المعلقة لإدراجه لاحقًا. البند الأصلي يبقى بحالته «مؤجل» للأثر.</summary>
    private static async Task<IResult> Defer(Guid itemId, DeferRequest req, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("agenda_item", itemId), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var x = await LockItemAsync(tx, itemId, ct);
            if (x.Status != "pending") throw new ConflictException("invalid-transition", "يُؤجل البند غير المنظور فقط");
            if (x.MeetingStatus is "cancelled" or "closed" or "postponed") throw new ConflictException("invalid-transition", "الاجتماع في حالة لا تسمح بالتأجيل");
            var pid = Guid.CreateVersion7();
            Guid? newItem = null;
            if (req.TargetMeetingId is { } target)
            {
                var t = await tx.QuerySingleAsync("SELECT body_id, status, starts_at, (SELECT starts_at FROM meetings.meetings WHERE id = @src) AS src_starts FROM meetings.meetings WHERE id = @target FOR UPDATE",
                    new { target, src = x.Meeting }, r => (Body: r.GetGuid(0), Status: r.GetString(1), Starts: r.GetDateTime(2), Src: r.GetDateTime(3)), ct);
                if (t == default) throw AppValidationException.For("targetMeetingId", "الاجتماع غير موجود");
                if (t.Body != x.Body) throw AppValidationException.For("targetMeetingId", "التأجيل لاجتماع للجهة نفسها؛ لجهة أخرى استخدم الإحالة");
                if (!Preparing.Contains(t.Status)) throw AppValidationException.For("targetMeetingId", "الاجتماع المستهدف ليس قيد الإعداد");
                if (t.Starts <= t.Src || target == x.Meeting) throw AppValidationException.For("targetMeetingId", "الاجتماع المستهدف يجب أن يكون لاحقًا");
                newItem = Guid.CreateVersion7();
                await tx.ExecuteAsync("""
                    INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, title_en, description_ar, item_type, presenter_text, owner_org_unit_id, owner_text,
                      estimated_minutes, memo_text, carried_from_item_id, classification)
                    SELECT @newItem, @target, COALESCE((SELECT max(position) FROM meetings.agenda_items WHERE meeting_id = @target), 0) + 1,
                      title_ar, title_en, description_ar, item_type, presenter_text, owner_org_unit_id, owner_text, estimated_minutes, memo_text, id, classification
                    FROM meetings.agenda_items WHERE id = @itemId
                    """, new { newItem, target, itemId }, ct);
                await CopyAttachmentsAsync(tx, itemId, newItem.Value, ct);
            }
            await tx.ExecuteAsync("""
                INSERT INTO meetings.pending_items (id, body_id, source_item_id, source_body_id, kind, title_ar, description_ar, reason, status, scheduled_item_id, created_by, scheduled_at)
                VALUES (@pid, @body, @itemId, @body, 'deferred', @title, @desc, @Reason, @st, @newItem, @by, CASE WHEN @newItem::uuid IS NULL THEN NULL ELSE now() END)
                """, new { pid, body = x.Body, itemId, title = x.Title, desc = x.Desc, req.Reason, st = newItem is null ? "pending" : "scheduled", newItem, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE meetings.agenda_items SET status = 'deferred', status_reason = @Reason WHERE id = @itemId", new { itemId, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_deferred", "meeting", x.Meeting.ToString(),
                After: new { itemId, title = x.Title, req.Reason, targetMeetingId = req.TargetMeetingId, newItemId = newItem, pendingItemId = pid }), ct);
            if (req.TargetMeetingId is { } tm)
                await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_carried_in", "meeting", tm.ToString(), After: new { itemId = newItem, fromItemId = itemId, fromMeetingId = x.Meeting, title = x.Title, req.Reason }), ct);
            return new { pendingItemId = pid, newItemId = newItem, targetMeetingId = req.TargetMeetingId };
        }, ct);
        return Results.Ok(result);
    }

    private static Task CopyAttachmentsAsync(ISql tx, Guid fromItem, Guid toItem, CancellationToken ct) =>
        tx.ExecuteAsync("""
            INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose)
            SELECT gen_random_uuid(), document_id, 'agenda_item', @toItem, purpose FROM docs.document_links WHERE owner_type = 'agenda_item' AND owner_id = @fromItem
            """, new { fromItem, toItem }, ct);

    /// <summary>إحالة البند إلى جهة أخرى نشطة: يدخل قائمة البنود المعلقة لتلك الجهة، ويُشعر أمين سرها.</summary>
    private static async Task<IResult> Refer(Guid itemId, ReferRequest req, Db db, IAccessService access, IAuditWriter audit, INotifier notifier, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("agenda_item", itemId), ct);
        var target = req.TargetBodyId!.Value;
        var pid = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var x = await LockItemAsync(tx, itemId, ct);
            if (x.Status is not ("pending" or "discussed")) throw new ConflictException("invalid-transition", "البند مؤجل أو محال مسبقًا");
            if (target == x.Body) throw AppValidationException.For("targetBodyId", "الإحالة لجهة أخرى؛ للجهة نفسها استخدم التأجيل");
            var t = await tx.QuerySingleAsync("SELECT status, name_ar FROM bodies.bodies WHERE id = @target", new { target }, r => (Status: r.GetString(0), Name: r.GetString(1)), ct);
            if (t == default || t.Status != "active") throw AppValidationException.For("targetBodyId", "الجهة المحال إليها غير موجودة أو غير نشطة");
            await tx.ExecuteAsync("""
                INSERT INTO meetings.pending_items (id, body_id, source_item_id, source_body_id, kind, title_ar, description_ar, reason, created_by)
                VALUES (@pid, @target, @itemId, @src, 'referred', @title, @desc, @Reason, @by)
                """, new { pid, target, itemId, src = x.Body, title = x.Title, desc = x.Desc, req.Reason, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE meetings.agenda_items SET status = 'referred', status_reason = @Reason WHERE id = @itemId", new { itemId, req.Reason }, ct);
            var secretaries = await tx.QueryAsync("SELECT user_id FROM bodies.body_members WHERE body_id = @target AND role IN ('secretary','chair') AND status = 'active'",
                new { target }, r => r.GetGuid(0), ct);
            foreach (var s in secretaries)
                await notifier.NotifyAsync(tx, new NotificationRequest(s, "agenda_item_referred", $"بند محال إلى {t.Name}: {x.Title}", $"Agenda item referred: {x.Title}",
                    $"/bodies/{target}?tab=pending", $"agenda_referred:{pid}:{s}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_referred", "meeting", x.Meeting.ToString(), After: new { itemId, title = x.Title, targetBodyId = target, targetBody = t.Name, req.Reason, pendingItemId = pid }), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "body.item_received", "body", target.ToString(), After: new { pendingItemId = pid, fromBodyId = x.Body, title = x.Title, req.Reason }), ct);
        }, ct);
        return Results.Ok(new { pendingItemId = pid });
    }

    private static async Task<IResult> Withdraw(Guid itemId, MeetingsModule.ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("agenda_item", itemId), ct);
        await db.InTransactionAsync(async tx =>
        {
            var x = await LockItemAsync(tx, itemId, ct);
            if (x.Status != "pending") throw new ConflictException("invalid-transition", "يُسحب البند غير المنظور فقط");
            if (!Preparing.Contains(x.MeetingStatus) && x.MeetingStatus != "in_progress") throw new ConflictException("invalid-transition", "لا يُسحب بند بعد انتهاء الاجتماع");
            await tx.ExecuteAsync("UPDATE meetings.agenda_items SET status = 'withdrawn', status_reason = @Reason WHERE id = @itemId", new { itemId, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.item_withdrawn", "meeting", x.Meeting.ToString(), After: new { itemId, title = x.Title, req.Reason }), ct);
        }, ct);
        return Results.Ok(new { itemId, status = "withdrawn" });
    }

    private static async Task<IResult> PendingList(Guid bodyId, string? status, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Body(bodyId), ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT p.*, sb.name_ar AS source_body_name, sm.id AS source_meeting_id, sm.reference_no AS source_meeting_reference, sm.title_ar AS source_meeting_title,
                   ti.meeting_id AS scheduled_meeting_id, tm.reference_no AS scheduled_meeting_reference, u.display_name_ar AS created_by_name
            FROM meetings.pending_items p JOIN bodies.bodies sb ON sb.id = p.source_body_id
            JOIN meetings.agenda_items si ON si.id = p.source_item_id JOIN meetings.meetings sm ON sm.id = si.meeting_id
            LEFT JOIN meetings.agenda_items ti ON ti.id = p.scheduled_item_id LEFT JOIN meetings.meetings tm ON tm.id = ti.meeting_id
            LEFT JOIN iam.users u ON u.id = p.created_by
            WHERE p.body_id = @bodyId AND (@status::text IS NULL OR p.status = @status) ORDER BY p.created_at DESC
            """, new { bodyId, status }, r => new
        {
            id = r.Get<Guid>("id"), kind = r.Get<string>("kind"), titleAr = r.Get<string>("title_ar"), descriptionAr = r.Str("description_ar"), reason = r.Get<string>("reason"),
            status = r.Get<string>("status"), sourceItemId = r.Get<Guid>("source_item_id"), sourceBodyId = r.Get<Guid>("source_body_id"), sourceBodyName = r.Get<string>("source_body_name"),
            sourceMeetingId = r.Get<Guid>("source_meeting_id"), sourceMeetingReference = r.Str("source_meeting_reference"), sourceMeetingTitle = r.Get<string>("source_meeting_title"),
            scheduledItemId = r.Uuid("scheduled_item_id"), scheduledMeetingId = r.Uuid("scheduled_meeting_id"), scheduledMeetingReference = r.Str("scheduled_meeting_reference"),
            createdAt = r.Get<DateTime>("created_at"), createdByName = r.Str("created_by_name"),
        }, ct));
    }

    /// <summary>إدراج بنود معلقة (مؤجلة أو محالة) في اجتماع لاحق للجهة.</summary>
    private static async Task<IResult> Import(Guid id, ImportRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var created = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, body_id FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, r => (Status: r.GetString(0), Body: r.GetGuid(1)), ct);
            if (m == default) throw new NotFoundException();
            if (!Preparing.Contains(m.Status)) throw new ConflictException("invalid-transition", "يُدرج في اجتماع قيد الإعداد فقط");
            var list = new List<object>();
            foreach (var pid in req.PendingItemIds.Distinct())
            {
                var p = await tx.QuerySingleAsync("SELECT body_id, status, source_item_id, kind FROM meetings.pending_items WHERE id = @pid FOR UPDATE", new { pid },
                    r => (Body: r.GetGuid(0), Status: r.GetString(1), Source: r.GetGuid(2), Kind: r.GetString(3)), ct);
                if (p == default || p.Body != m.Body) throw AppValidationException.For("pendingItemIds", "بند معلق غير موجود لهذه الجهة");
                if (p.Status != "pending") throw new ConflictException("invalid-transition", "البند المعلق مُدرج مسبقًا أو مستبعد");
                var newItem = Guid.CreateVersion7();
                await tx.ExecuteAsync("""
                    INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, title_en, description_ar, item_type, presenter_text, owner_org_unit_id, owner_text,
                      estimated_minutes, memo_text, carried_from_item_id, classification)
                    SELECT @newItem, @id, COALESCE((SELECT max(position) FROM meetings.agenda_items WHERE meeting_id = @id), 0) + 1,
                      p.title_ar, s.title_en, p.description_ar, s.item_type, s.presenter_text, s.owner_org_unit_id, s.owner_text, s.estimated_minutes, s.memo_text, s.id, s.classification
                    FROM meetings.pending_items p JOIN meetings.agenda_items s ON s.id = p.source_item_id WHERE p.id = @pid
                    """, new { newItem, id, pid }, ct);
                await CopyAttachmentsAsync(tx, p.Source, newItem, ct);
                await tx.ExecuteAsync("UPDATE meetings.pending_items SET status = 'scheduled', scheduled_item_id = @newItem, scheduled_at = now() WHERE id = @pid", new { pid, newItem }, ct);
                list.Add(new { pendingItemId = pid, itemId = newItem, p.Kind });
            }
            await audit.WriteAsync(tx, new AuditEntry("MTG", "agenda.items_imported", "meeting", id.ToString(), After: list), ct);
            return list;
        }, ct);
        return Results.Ok(new { imported = created, agenda = await LoadAsync(db, id, ct) });
    }

    private static async Task<IResult> Drop(Guid pid, MeetingsModule.ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        var body = await db.ScalarAsync<Guid?>("SELECT body_id FROM meetings.pending_items WHERE id = @pid", new { pid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.MeetingManage, Scope.Body(body), ct);
        var n = await db.InTransactionAsync(async tx =>
        {
            var c = await tx.ExecuteAsync("UPDATE meetings.pending_items SET status = 'dropped', reason = reason || ' | استبعاد: ' || @Reason WHERE id = @pid AND status = 'pending'", new { pid, req.Reason }, ct);
            if (c > 0) await audit.WriteAsync(tx, new AuditEntry("MTG", "pending_item.dropped", "body", body.ToString(), After: new { pendingItemId = pid, req.Reason }), ct);
            return c;
        }, ct);
        if (n == 0) throw new ConflictException("invalid-transition", "البند ليس معلقًا");
        return Results.Ok(new { id = pid, status = "dropped" });
    }

    /// <summary>عند إغلاق الاجتماع: البنود غير المنظورة تُؤجل آليًا إلى قائمة البنود المعلقة للجهة.</summary>
    public static async Task<int> DeferRemainingAsync(ISql tx, Guid meetingId, Guid bodyId, Guid by, string reason, CancellationToken ct)
    {
        var n = await tx.ExecuteAsync("""
            INSERT INTO meetings.pending_items (id, body_id, source_item_id, source_body_id, kind, title_ar, description_ar, reason, created_by)
            SELECT gen_random_uuid(), @bodyId, id, @bodyId, 'deferred', title_ar, description_ar, @reason, @by
            FROM meetings.agenda_items WHERE meeting_id = @meetingId AND status = 'pending'
            """, new { meetingId, bodyId, reason, by }, ct);
        await tx.ExecuteAsync("UPDATE meetings.agenda_items SET status = 'deferred', status_reason = @reason WHERE meeting_id = @meetingId AND status = 'pending'", new { meetingId, reason }, ct);
        return n;
    }
}
