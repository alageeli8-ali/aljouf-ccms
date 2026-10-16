using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Pdf;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Tasks;

/// <summary>خطابات التكليف: مسودة مرتبطة بالقرار والمهام برقم مرجعي داخلي، وبيانات الجهة، والمطلوب، وموعد الاستحقاق، والمرفقات.
/// الإرسال عبر نظام المراسلات لا يتم إلا بتكامل فعلي جاهز؛ وإلا يُسجل رقم الصادر يدويًا بعد إرساله رسميًا، ولا يُدّعى الإرسال.</summary>
internal static class Letters
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/letters", List);
        g.MapPost("/letters", Create).Validate<CreateRequest>().Idempotent();
        g.MapGet("/letters/{id:guid}", Get);
        g.MapPatch("/letters/{id:guid}", Edit).Validate<EditRequest>();
        g.MapGet("/letters/{id:guid}/pdf", Pdf);
        g.MapPost("/letters/{id:guid}/approve", Approve);
        g.MapPost("/letters/{id:guid}/send", Send);
        g.MapPost("/letters/{id:guid}/record-dispatch", RecordDispatch).Validate<DispatchRequest>();
        g.MapPost("/letters/{id:guid}/cancel", Cancel).Validate<CancelRequest>();
    }

    public sealed record CreateRequest(
        [property: Required, MinLength(1), MaxLength(20)] List<Guid> TaskIds,
        [property: MaxLength(300)] string? Subject,
        [property: MaxLength(300)] string? RecipientAttention,
        [property: MaxLength(10000)] string? BodyText,
        List<Guid>? AttachmentIds,
        bool? ShareWithEntity);

    public sealed record EditRequest([property: MaxLength(300)] string? Subject, [property: MaxLength(300)] string? RecipientAttention,
        [property: MaxLength(10000)] string? BodyText, List<Guid>? AttachmentIds, bool? ShareWithEntity);

    public sealed record DispatchRequest(
        [property: Required, OneOf("correspondence_system_manual", "hand_delivery", "official_email", "other")] string Channel,
        [property: Required(ErrorMessage = "رقم الصادر مطلوب"), MaxLength(100)] string OutgoingNo,
        [property: Required] DateOnly? OutgoingDate,
        [property: MaxLength(1000)] string? Note);

    public sealed record CancelRequest([property: Required, MinLength(5), MaxLength(1000)] string Reason);

    private sealed record LetterRow(Guid Id, string ReferenceNo, Guid BodyId, Guid? ResolutionId, string RecipientType, string RecipientName, string? Attention,
        string Subject, string BodyText, DateOnly? DueDate, Guid[] TaskIds, Guid[] AttachmentIds, bool Share, string Status, Guid CreatedBy, string? OutgoingNo,
        DateOnly? OutgoingDate, Guid? PdfDocumentId, DateTime CreatedAt, DateTime? ApprovedAt, string? ApprovedByName, string? CreatedByName, string? DispatchChannel,
        DateTime? DispatchedAt, string? CancelReason, string? IntegrationRef);

    private const string Select = """
        SELECT l.id, l.reference_no, l.body_id, l.resolution_id, l.recipient_type, l.recipient_name, l.recipient_attention, l.subject, l.body_text, l.due_date,
               l.task_ids, l.attachment_ids, l.share_with_entity, l.status, l.created_by, l.outgoing_no, l.outgoing_date, l.pdf_document_id, l.created_at, l.approved_at,
               ua.display_name_ar AS approved_by_name, uc.display_name_ar AS created_by_name, l.dispatch_channel, l.dispatched_at, l.cancel_reason, l.integration_ref
        FROM tasks.assignment_letters l LEFT JOIN iam.users ua ON ua.id = l.approved_by LEFT JOIN iam.users uc ON uc.id = l.created_by
        """;

    private static LetterRow MapRow(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Uuid("resolution_id"), r.GetString(4),
        r.GetString(5), r.Str("recipient_attention"), r.GetString(7), r.GetString(8), r.Date("due_date"), r.GetFieldValue<Guid[]>(10), r.GetFieldValue<Guid[]>(11),
        r.GetBoolean(12), r.GetString(13), r.GetGuid(14), r.Str("outgoing_no"), r.Date("outgoing_date"), r.Uuid("pdf_document_id"), r.GetDateTime(18), r.Time("approved_at"),
        r.Str("approved_by_name"), r.Str("created_by_name"), r.Str("dispatch_channel"), r.Time("dispatched_at"), r.Str("cancel_reason"), r.Str("integration_ref"));

    private static async Task<LetterRow> LoadAsync(ISql sql, Guid id, CancellationToken ct) =>
        await sql.QuerySingleAsync(Select + " WHERE l.id = @id", new { id }, MapRow, ct) ?? throw new NotFoundException();

    private static async Task<IResult> List(Guid? resolutionId, Guid? taskId, string? status, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        var f = await access.GetBodyFilterAsync(Permissions.TaskView, ct);
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var items = await db.QueryAsync(Select + """
             WHERE (@all::boolean OR l.body_id = ANY(@ids::uuid[])) AND (@resolutionId::uuid IS NULL OR l.resolution_id = @resolutionId)
               AND (@taskId::uuid IS NULL OR @taskId = ANY(l.task_ids)) AND (@status::text IS NULL OR l.status = @status)
             ORDER BY l.created_at DESC LIMIT 200
            """, new { all = f.All, ids = f.BodyIds, resolutionId, taskId, status }, MapRow, ct);
        return Results.Ok(items.Select(View));
    }

    private static object View(LetterRow l) => new
    {
        id = l.Id, referenceNo = l.ReferenceNo, bodyId = l.BodyId, resolutionId = l.ResolutionId, recipientType = l.RecipientType, recipientName = l.RecipientName,
        recipientAttention = l.Attention, subject = l.Subject, bodyText = l.BodyText, dueDate = l.DueDate, taskIds = l.TaskIds, attachmentIds = l.AttachmentIds,
        shareWithEntity = l.Share, status = l.Status, outgoingNo = l.OutgoingNo, outgoingDate = l.OutgoingDate, pdfDocumentId = l.PdfDocumentId, createdAt = l.CreatedAt,
        createdByName = l.CreatedByName, approvedAt = l.ApprovedAt, approvedByName = l.ApprovedByName, dispatchChannel = l.DispatchChannel, dispatchedAt = l.DispatchedAt,
        cancelReason = l.CancelReason, integrationRef = l.IntegrationRef,
        // لا يُعرض «مُرسل» إلا بمرجع فعلي: رقم صادر مسجل يدويًا أو مرجع من التكامل
        officiallySent = l.Status is "dispatched_manual" or "dispatched_integration",
    };

    private sealed record LetterTask(Guid Id, string? ReferenceNo, string Title, DateOnly? DueDate, string AssigneeType, Guid? UserId, Guid? UnitId, Guid? EntityId,
        string RecipientName, Guid BodyId, Guid? ResolutionId, string Category, string? AcceptanceCriteria, string? Brief);

    private static Task<List<LetterTask>> TasksAsync(ISql sql, Guid[] ids, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT t.id, t.reference_no, t.title_ar, t.due_date, t.assignee_type, t.assignee_user_id, t.assignee_org_unit_id, t.assignee_external_entity_id,
                   COALESCE(e.name_ar, o.name_ar, u.display_name_ar), t.body_id, t.resolution_id, ws.category, t.acceptance_criteria, t.external_brief
            FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
            LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id LEFT JOIN iam.users u ON u.id = t.assignee_user_id
            WHERE t.id = ANY(@ids::uuid[]) ORDER BY t.reference_no
            """, new { ids }, r => new LetterTask(r.GetGuid(0), r.Str("reference_no"), r.GetString(2), r.Date("due_date"), r.GetString(4), r.Uuid("assignee_user_id"),
            r.Uuid("assignee_org_unit_id"), r.Uuid("assignee_external_entity_id"), r.GetString(8), r.GetGuid(9), r.Uuid("resolution_id"), r.GetString(11),
            r.Str("acceptance_criteria"), r.Str("external_brief")), ct);

    private static async Task<IResult> Create(CreateRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, INumberingService numbering,
        IClock clock, CancellationToken ct)
    {
        var ids = req.TaskIds.Distinct().ToArray();
        var tasks = await TasksAsync(db, ids, ct);
        if (tasks.Count != ids.Length) throw new NotFoundException();
        var first = tasks[0];
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(first.BodyId), ct);
        if (tasks.Any(t => t.BodyId != first.BodyId || t.AssigneeType != first.AssigneeType || t.UserId != first.UserId || t.UnitId != first.UnitId || t.EntityId != first.EntityId))
            throw AppValidationException.For("taskIds", "خطاب التكليف الواحد لمهام جهة مكلفة واحدة في مجلس واحد");
        if (tasks.Any(t => t.Category is "new" or "done" or "closed")) throw AppValidationException.For("taskIds", "يُعد خطاب التكليف لمهام مسندة جارية");
        var resolutionId = tasks.Select(t => t.ResolutionId).Distinct().Count() == 1 ? first.ResolutionId : null;
        var id = Guid.CreateVersion7();
        var refNo = await db.InTransactionAsync(async tx =>
        {
            var body = await tx.QuerySingleAsync("SELECT code, name_ar FROM bodies.bodies WHERE id = @b", new { b = first.BodyId }, r => (Code: r.GetString(0), Name: r.GetString(1)), ct);
            var res = resolutionId is { } rid
                ? await tx.QuerySingleAsync("SELECT number, title_ar, issued_at FROM resolutions.resolutions WHERE id = @rid", new { rid },
                    r => (Number: r.Str("number"), Title: r.GetString(1), Issued: r.Time("issued_at")), ct)
                : default;
            var year = clock.TodayLocal.Year;
            var no = $"{body.Code}-L-{year}-{await numbering.NextAsync(tx, $"letter:{body.Code}", year, ct):0000}";
            var (kind, recipient) = first.AssigneeType switch
            {
                "external_entity" => ("external_entity", first.RecipientName),
                "org_unit" => ("org_unit", first.RecipientName),
                _ => ("user", first.RecipientName),
            };
            var subject = req.Subject ?? (res.Title is { } rt ? $"تكليف بتنفيذ قرار {body.Name}: {rt}" : $"تكليف من {body.Name}");
            var due = tasks.Where(t => t.DueDate is not null).Select(t => t.DueDate).Min();
            var text = req.BodyText ?? DefaultText(body.Name, recipient, kind, res.Number, res.Issued, tasks, due);
            if (req.AttachmentIds is { Count: > 0 } att
                && await tx.ScalarAsync<long>("SELECT count(DISTINCT document_id) FROM docs.document_links WHERE document_id = ANY(@att::uuid[]) AND owner_type = 'task' AND owner_id = ANY(@ids::uuid[])",
                    new { att = att.ToArray(), ids }, ct) != att.Distinct().Count())
                throw AppValidationException.For("attachmentIds", "المرفقات يجب أن تكون من مرفقات المهام المشمولة");
            await tx.ExecuteAsync("""
                INSERT INTO tasks.assignment_letters (id, reference_no, body_id, resolution_id, recipient_type, recipient_org_unit_id, recipient_external_entity_id,
                  recipient_user_id, recipient_name, recipient_attention, subject, body_text, due_date, task_ids, attachment_ids, share_with_entity, created_by)
                VALUES (@id, @no, @bodyId, @resolutionId, @kind, @unit, @entity, @user, @recipient, @RecipientAttention, @subject, @text, @due, @ids, @att, @share, @by)
                """, new
            {
                id, no, bodyId = first.BodyId, resolutionId, kind, unit = first.UnitId, entity = first.EntityId, user = first.UserId, recipient, req.RecipientAttention,
                subject, text, due, ids, att = (req.AttachmentIds ?? []).Distinct().ToArray(), share = req.ShareWithEntity ?? true, by = me.UserId,
            }, ct);
            foreach (var t in tasks)
                await TaskData.LogAsync(tx, t.Id, me.UserId, "letter", $"أُعدت مسودة خطاب تكليف {no}", null, new { letterId = id, status = "draft" }, ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.letter_drafted", "assignment_letter", id.ToString(), After: new { no, ids, recipient, resolutionId }), ct);
            return no;
        }, ct);
        return Results.Created($"/api/v1/tasks/letters/{id}", View(await LoadAsync(db, id, ct)));
    }

    private static string DefaultText(string bodyName, string recipient, string kind, string? resNumber, DateTime? resIssued, List<LetterTask> tasks, DateOnly? due)
    {
        var sb = new StringBuilder();
        sb.AppendLine(kind == "external_entity" ? $"سعادة/ {recipient}        المحترم" : $"المكرم/ {recipient}        المحترم");
        sb.AppendLine("السلام عليكم ورحمة الله وبركاته، وبعد:");
        sb.Append($"فإشارةً إلى ما قرره {bodyName}");
        if (resNumber is not null) sb.Append($" بالقرار رقم ({resNumber})" + (resIssued is { } d ? $" وتاريخ {d.AddHours(3):yyyy/MM/dd}م" : ""));
        sb.AppendLine("، نأمل التكرم بتنفيذ ما يلي:");
        var i = 0;
        foreach (var t in tasks)
        {
            sb.AppendLine($"{++i}. {t.Title}" + (t.ReferenceNo is null ? "" : $" (مرجع المهمة {t.ReferenceNo})") + (t.DueDate is { } dd ? $" — موعد الاستحقاق {dd:yyyy/MM/dd}م" : ""));
            if (!string.IsNullOrWhiteSpace(t.Brief)) sb.AppendLine("   " + t.Brief);
            if (!string.IsNullOrWhiteSpace(t.AcceptanceCriteria)) sb.AppendLine("   معايير القبول: " + t.AcceptanceCriteria);
        }
        sb.AppendLine(due is { } d2 ? $"وتزويدنا بما يفيد التنفيذ والمخرجات المطلوبة في موعد أقصاه {d2:yyyy/MM/dd}م." : "وتزويدنا بما يفيد التنفيذ والمخرجات المطلوبة.");
        sb.AppendLine("شاكرين ومقدرين حسن تعاونكم، وتقبلوا وافر التحية والتقدير.");
        return sb.ToString().TrimEnd();
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskView, Scope.Body(l.BodyId), ct);
        var tasks = await TasksAsync(db, l.TaskIds, ct);
        return Results.Ok(new
        {
            letter = View(l),
            tasks = tasks.Select(t => new { t.Id, t.ReferenceNo, t.Title, t.DueDate }),
            canManage = await access.HasAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct),
            correspondenceIntegrationEnabled = await features.IsEnabledAsync(FeatureKeys.CorrespondenceIntegrationEnabled, ct),
        });
    }

    private static async Task<IResult> Edit(Guid id, EditRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct);
        if (l.Status != "draft") throw new ConflictException("letter-locked", "يُعدل الخطاب في المسودة فقط");
        await db.ExecuteAsync("""
            UPDATE tasks.assignment_letters SET subject = COALESCE(@Subject, subject), recipient_attention = COALESCE(@RecipientAttention, recipient_attention),
              body_text = COALESCE(@BodyText, body_text), attachment_ids = COALESCE(@att, attachment_ids), share_with_entity = COALESCE(@ShareWithEntity, share_with_entity)
            WHERE id = @id
            """, new { id, req.Subject, req.RecipientAttention, req.BodyText, att = req.AttachmentIds?.Distinct().ToArray(), req.ShareWithEntity }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.letter_edited", "assignment_letter", id.ToString(), After: req), ct);
        return Results.Ok(View(await LoadAsync(db, id, ct)));
    }

    private static byte[] Render(LetterRow l, List<LetterTask> tasks, string bodyName, IReadOnlyList<string> attachments, bool draft)
    {
        var pdf = new PdfReport(new PdfReportOptions
        {
            Title = $"خطاب تكليف {l.ReferenceNo}",
            HeaderLine1 = "المكتب الاستراتيجي لتطوير منطقة الجوف",
            HeaderLine2 = bodyName,
            FooterText = draft
                ? $"مسودة خطاب تكليف — المرجع الداخلي {l.ReferenceNo} — ليست خطابًا رسميًا صادرًا"
                : $"المرجع الداخلي {l.ReferenceNo} — يُستكمل رقم الصادر وتاريخه من نظام المراسلات عند الإرسال الرسمي",
            Watermark = draft ? "مسودة" : null,
            Author = bodyName,
        });
        pdf.KeyValues([
            ("المرجع الداخلي", l.ReferenceNo),
            ("رقم الصادر", l.OutgoingNo ?? "يُستكمل من نظام المراسلات"),
            ("التاريخ", (l.OutgoingDate ?? DateOnly.FromDateTime(l.CreatedAt.AddHours(3))).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "م"),
            ("إلى", l.RecipientName + (l.Attention is { } a ? $" — عناية: {a}" : "")),
            ("الموضوع", l.Subject),
        ], 0.22);
        pdf.Spacer(6);
        foreach (var para in l.BodyText.Split('\n')) pdf.Paragraph(para.TrimEnd('\r'), 11);
        pdf.Heading("المهام المكلف بها");
        pdf.Table(["المرجع", "المهمة", "الاستحقاق"], [0.22, 0.58, 0.2],
            tasks.Select(t => (IReadOnlyList<string?>)[t.ReferenceNo, t.Title, t.DueDate?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)]).ToList());
        if (attachments.Count > 0)
        {
            pdf.Heading("المرفقات");
            pdf.Table(["#", "الملف"], [0.1, 0.9], attachments.Select((x, i) => (IReadOnlyList<string?>)[(i + 1).ToString(CultureInfo.InvariantCulture), x]).ToList());
        }
        pdf.Spacer(16);
        pdf.Paragraph(draft ? "اعتماد: (لم يُعتمد بعد)" : $"اعتمده: {l.ApprovedByName} — {l.ApprovedAt?.AddHours(3):yyyy/MM/dd}", 10, true);
        return pdf.Build();
    }

    private static async Task<(byte[] Bytes, string Name)> PdfAsync(Db db, LetterRow l, bool draft, CancellationToken ct)
    {
        var tasks = await TasksAsync(db, l.TaskIds, ct);
        var bodyName = await db.ScalarAsync<string>("SELECT name_ar FROM bodies.bodies WHERE id = @b", new { b = l.BodyId }, ct) ?? "";
        var attachments = await db.QueryAsync("SELECT file_name FROM docs.documents WHERE id = ANY(@ids::uuid[]) ORDER BY uploaded_at", new { ids = l.AttachmentIds }, r => r.GetString(0), ct);
        return (Render(l, tasks, bodyName, attachments, draft), $"assignment-letter-{l.ReferenceNo}{(draft ? "-draft" : "")}.pdf");
    }

    private static async Task<IResult> Pdf(Guid id, Db db, IAccessService access, IDocumentStore store, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskView, Scope.Body(l.BodyId), ct);
        if (l.Status != "draft" && l.PdfDocumentId is { } doc && await store.ReadAsync(doc, ct) is { } stored)
            return Results.File(stored, "application/pdf", $"assignment-letter-{l.ReferenceNo}.pdf");
        var (bytes, name) = await PdfAsync(db, l, l.Status == "draft", ct);
        return Results.File(bytes, "application/pdf", name);
    }

    /// <summary>اعتماد المسودة: من غير معدها، ويُحفظ PDF المعتمد مستندًا مرتبطًا بالمهام (غير مشارك مع الجهة حتى يُسجل صدوره رسميًا).</summary>
    private static async Task<IResult> Approve(Guid id, Db db, IAccessService access, ICurrentUser me, IDocumentStore store, IAuditWriter audit, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct);
        if (l.Status != "draft") throw new ConflictException("invalid-transition", "يُعتمد الخطاب من المسودة فقط");
        if (l.CreatedBy == me.UserId) throw new AccessDeniedException("separation-of-duties", "يعتمد الخطاب غير من أعده");
        var approverName = await db.ScalarAsync<string>("SELECT display_name_ar FROM iam.users WHERE id = @me", new { me = me.UserId }, ct);
        var approved = l with { Status = "approved", ApprovedAt = DateTime.UtcNow, ApprovedByName = approverName };
        var (bytes, name) = await PdfAsync(db, approved, false, ct);
        await db.InTransactionAsync(async tx =>
        {
            var doc = await store.SaveAsync(tx, name, "application/pdf", bytes, "task", l.TaskIds[0], "assignment_letter", "restricted", me.UserId, ct);
            foreach (var t in l.TaskIds.Skip(1))
                await tx.ExecuteAsync("INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose) VALUES (@lid, @doc, 'task', @t, 'assignment_letter')",
                    new { lid = Guid.CreateVersion7(), doc = doc.Id, t }, ct);
            var n = await tx.ExecuteAsync("UPDATE tasks.assignment_letters SET status = 'approved', approved_by = @me, approved_at = now(), pdf_document_id = @doc WHERE id = @id AND status = 'draft'",
                new { id, me = me.UserId, doc = doc.Id }, ct);
            if (n == 0) throw new ConflictException("invalid-transition", "تغيرت حالة الخطاب");
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.letter_approved", "assignment_letter", id.ToString(), After: new { l.ReferenceNo, doc.Sha256 }), ct);
        }, ct);
        return Results.Ok(View(await LoadAsync(db, id, ct)));
    }

    /// <summary>الإرسال عبر نظام المراسلات: يتطلب تفعيل التكامل وجاهزيته الفعلية؛ وإلا يُرفض بسبب صريح ولا تتغير حالة الخطاب.</summary>
    private static async Task<IResult> Send(Guid id, Db db, IAccessService access, ICurrentUser me, IFeatureService features, ICorrespondenceGateway gateway,
        IDocumentStore store, IAuditWriter audit, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.CorrespondenceIntegrationEnabled, ct);
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct);
        if (l.Status != "approved") throw new ConflictException("invalid-transition", "يُرسل الخطاب المعتمد فقط");
        var (ready, reason) = await gateway.ReadinessAsync(ct);
        if (!ready)
        {
            await audit.WriteAsync(new AuditEntry("TSK", "task.letter_send_unavailable", "assignment_letter", id.ToString(), After: new { reason }, Outcome: "denied"), ct);
            throw new ConflictException("correspondence-unavailable",
                "التكامل مع نظام المراسلات غير جاهز؛ لم يُرسل الخطاب. أرسله رسميًا عبر نظام المراسلات ثم سجّل رقم الصادر",
                new Dictionary<string, object?> { ["reason"] = reason });
        }
        var pdf = l.PdfDocumentId is { } doc ? await store.ReadAsync(doc, ct) : null;
        var outgoing = await gateway.SubmitAsync(new CorrespondenceLetter(l.Id, l.ReferenceNo, l.RecipientName, l.Subject, l.BodyText, pdf ?? []), ct);
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                UPDATE tasks.assignment_letters SET status = 'dispatched_integration', dispatch_channel = 'integration', integration_ref = @outgoing, outgoing_no = @outgoing,
                  outgoing_date = (now() AT TIME ZONE 'Asia/Riyadh')::date, dispatched_by = @me, dispatched_at = now() WHERE id = @id
                """, new { id, outgoing, me = me.UserId }, ct);
            await AfterDispatchAsync(tx, l, outgoing, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), me.UserId, "integration", ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.letter_sent_integration", "assignment_letter", id.ToString(), After: new { outgoing }), ct);
        }, ct);
        return Results.Ok(View(await LoadAsync(db, id, ct)));
    }

    /// <summary>تسجيل الصدور الرسمي يدويًا: بعد أن يرسل الموظف الخطاب عبر القنوات الرسمية، يسجل رقم الصادر وتاريخه وقناته.</summary>
    private static async Task<IResult> RecordDispatch(Guid id, DispatchRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct);
        if (l.Status != "approved") throw new ConflictException("invalid-transition", "يُسجل صدور الخطاب المعتمد فقط");
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                UPDATE tasks.assignment_letters SET status = 'dispatched_manual', dispatch_channel = @Channel, outgoing_no = @OutgoingNo, outgoing_date = @OutgoingDate,
                  dispatched_by = @me, dispatched_at = now() WHERE id = @id
                """, new { id, req.Channel, req.OutgoingNo, req.OutgoingDate, me = me.UserId }, ct);
            await AfterDispatchAsync(tx, l, req.OutgoingNo, req.OutgoingDate!.Value, me.UserId, req.Channel, ct);
            await audit.WriteAsync(tx, new AuditEntry("TSK", "task.letter_dispatch_recorded", "assignment_letter", id.ToString(), After: req), ct);
        }, ct);
        return Results.Ok(View(await LoadAsync(db, id, ct)));
    }

    private static async Task AfterDispatchAsync(ISql tx, LetterRow l, string outgoing, DateOnly date, Guid by, string channel, CancellationToken ct)
    {
        await tx.ExecuteAsync("UPDATE tasks.tasks SET correspondence_no = @outgoing, correspondence_date = @date WHERE id = ANY(@ids::uuid[])",
            new { outgoing, date, ids = l.TaskIds }, ct);
        if (l.Share && l.RecipientType == "external_entity")
            await tx.ExecuteAsync("""
                UPDATE docs.document_links SET external_visible = true WHERE owner_type = 'task' AND owner_id = ANY(@ids::uuid[])
                  AND (document_id = @pdf OR document_id = ANY(@att::uuid[]))
                """, new { ids = l.TaskIds, pdf = l.PdfDocumentId, att = l.AttachmentIds }, ct);
        foreach (var t in l.TaskIds)
            await TaskData.LogAsync(tx, t, by, "letter", $"صدر خطاب التكليف {l.ReferenceNo} برقم الصادر {outgoing}", null,
                new { letterId = l.Id, outgoing, date, channel }, channel: "system", visibility: "shared", reference: outgoing, ct: ct);
    }

    private static async Task<IResult> Cancel(Guid id, CancelRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        var l = await LoadAsync(db, id, ct);
        await access.DemandAsync(Permissions.TaskLettersManage, Scope.Body(l.BodyId), ct);
        if (l.Status is not ("draft" or "approved")) throw new ConflictException("invalid-transition", "لا يُلغى خطاب صدر رسميًا؛ يُصدر خطاب لاحق");
        await db.ExecuteAsync("UPDATE tasks.assignment_letters SET status = 'cancelled', cancel_reason = @Reason WHERE id = @id", new { id, req.Reason }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.letter_cancelled", "assignment_letter", id.ToString(), After: req), ct);
        return Results.NoContent();
    }
}

/// <summary>البديل حين لا تسجل وحدة التكاملات بوابتها: التكامل غير متاح، فلا يُدّعى إرسال.</summary>
internal sealed class UnavailableCorrespondenceGateway : ICorrespondenceGateway
{
    public Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default) => Task.FromResult((false, "integration_module_unavailable"));
    public Task<string> SubmitAsync(CorrespondenceLetter letter, CancellationToken ct = default) => throw new InvalidOperationException("التكامل غير متاح");
}
