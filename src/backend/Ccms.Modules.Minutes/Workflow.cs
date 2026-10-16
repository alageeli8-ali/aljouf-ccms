using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Minutes;

/// <summary>دورة اعتماد المحضر القابلة للتهيئة لكل جهة: مسودة ← (مراجعة أمين السر) ← (مراجعة الرئيس) ← (ملاحظات الأعضاء) ← (انتظار التوقيع)
/// ← انتظار الاعتماد النهائي ← معتمد؛ ومن أي مرحلة مراجعة ← معاد للتعديل. كل انتقال يُسجل من نفذه وبأي صفة وطريقة وعلى أي بصمة.</summary>
internal static class Workflow
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/submit", Submit).Validate<SubmitRequest>();
        g.MapPost("/{id:guid}/review/approve", ApproveStep).Validate<HashRequest>();
        g.MapPost("/{id:guid}/return", Return).Validate<ReturnRequest>();
        g.MapPost("/{id:guid}/signoffs", SignOff).Validate<SignOffRequest>();
        g.MapPost("/{id:guid}/signoffs/wet", RecordWet).Validate<WetRequest>();
        g.MapPost("/{id:guid}/approve", Approve).Validate<HashRequest>();
        g.MapPost("/{id:guid}/record-approval", RecordExternal).Validate<ExternalApprovalRequest>();
        g.MapPost("/{id:guid}/amend", Amend).Validate<ReturnRequest>();
    }

    private const string HashPattern = "^[0-9a-f]{64}$";

    public sealed record SubmitRequest([property: MaxLength(1000)] string? ChangeSummary);
    public sealed record HashRequest([property: Required, RegularExpression(HashPattern, ErrorMessage = "بصمة غير صالحة")] string ContentHash, [property: MaxLength(2000)] string? Note);
    public sealed record ReturnRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(5), MaxLength(2000)] string Reason);
    public sealed record SignOffRequest([property: Required, RegularExpression(HashPattern, ErrorMessage = "بصمة غير صالحة")] string ContentHash, [property: MaxLength(2000)] string? Reservation);
    public sealed record WetRequest([property: Required] Guid? DocumentId, [property: Required, MinLength(1), MaxLength(100)] List<Guid> UserIds, [property: MaxLength(1000)] string? Note);
    public sealed record ExternalApprovalRequest(
        [property: Required, RegularExpression(HashPattern, ErrorMessage = "بصمة غير صالحة")] string ContentHash,
        [property: Required(ErrorMessage = "اسم صاحب الصلاحية مطلوب"), MaxLength(200)] string ApproverName,
        [property: MaxLength(200)] string? ApproverTitle,
        [property: Required(ErrorMessage = "مرجع الاعتماد مطلوب"), MaxLength(100)] string ReferenceNo,
        [property: Required] DateOnly? ApprovedOn,
        [property: Required(ErrorMessage = "مستند الاعتماد مطلوب")] Guid? DocumentId,
        [property: MaxLength(2000)] string? Note);

    // ---------------- مساعدات ----------------

    private static void DemandHash(VersionRow? v, string hash)
    {
        if (v is null || v.Hash != hash) throw new ConflictException("hash-mismatch", "البصمة لا تطابق الإصدار الحالي؛ أعد تحميل المحضر (BRL-06)");
    }

    internal static Task RecordAsync(ISql tx, Guid minutesId, Guid? versionId, string step, string decision, Guid actor, string? capacity = null, Guid? onBehalf = null,
        Guid? delegation = null, string? method = null, string? hash = null, string? note = null, string? extApprover = null, string? extRef = null,
        DateOnly? extDate = null, Guid? evidence = null, CancellationToken ct = default) =>
        tx.ExecuteAsync("""
            INSERT INTO minutes.minutes_approvals (id, minutes_id, minutes_version_id, step, decision, actor_user_id, acting_capacity, on_behalf_of_user_id, delegation_id,
              method, content_hash, note, external_approver, external_reference, external_date, evidence_document_id)
            VALUES (@id, @minutesId, @versionId, @step, @decision, @actor, @capacity, @onBehalf, @delegation, @method, @hash, @note, @extApprover, @extRef, @extDate, @evidence)
            """, new { id = Guid.CreateVersion7(), minutesId, versionId, step, decision, actor, capacity, onBehalf, delegation, method, hash, note, extApprover, extRef, extDate, evidence }, ct);

    /// <summary>صفة المعتمد: الرئيس، أو المفوض بصلاحياته (ويُسجل المفوِّض والتفويض)، أو أمين السر، أو غيرهم.</summary>
    internal static async Task<(string Capacity, Guid? OnBehalf, Guid? Delegation)> CapacityAsync(ISql tx, Guid bodyId, Guid me, CancellationToken ct)
    {
        var role = await tx.ScalarAsync<string>("""
            SELECT role FROM bodies.body_members WHERE body_id = @bodyId AND user_id = @me AND status = 'active' ORDER BY CASE role WHEN 'chair' THEN 0 WHEN 'secretary' THEN 1 ELSE 2 END LIMIT 1
            """, new { bodyId, me }, ct);
        if (role == "chair") return ("chair", null, null);
        var d = await tx.QuerySingleAsync("""
            SELECT id, delegator_user_id FROM iam.delegations WHERE body_id = @bodyId AND delegate_user_id = @me AND scope = 'chair_authority' AND status = 'active'
              AND valid_from <= now() AND valid_to > now() ORDER BY valid_from DESC LIMIT 1
            """, new { bodyId, me }, r => (Id: r.GetGuid(0), Delegator: r.GetGuid(1)), ct);
        if (d != default) return ("chair_delegate", d.Delegator, d.Id);
        return (role ?? "governance_officer", null, null);
    }

    internal static async Task NotifyAsync(ISql tx, INotifier notifier, IEnumerable<Guid> users, MinutesRow row, string type, string titleAr, string titleEn, string key,
        string severity = "info", CancellationToken ct = default)
    {
        foreach (var u in users.Distinct())
            await notifier.NotifyAsync(tx, new NotificationRequest(u, type, $"{titleAr} ({row.MeetingRef})", $"{titleEn} ({row.MeetingRef})", $"/minutes/{row.Id}",
                $"{key}:{row.Id}:{u}:{DateTime.UtcNow:yyyyMMddHHmmssfff}", severity), ct);
    }

    /// <summary>الانتقال إلى مرحلة: يضبط الحالة والمهلة، وينشئ طلبات التوقيع عند الدخول في مرحلته، ويُشعر أصحاب المرحلة.</summary>
    internal static async Task<string> EnterAsync(ISql tx, MinutesService svc, INotifier notifier, MinutesRow row, VersionRow version, string step, Guid by, CancellationToken ct)
    {
        await tx.ExecuteAsync("""
            UPDATE minutes.minutes SET status = @step::text, status_changed_at = now(), status_reason = NULL,
              review_deadline = CASE WHEN @step::text = 'member_review' THEN now() + make_interval(days => @days::int) ELSE NULL END
            WHERE id = @id
            """, new { id = row.Id, step, days = Math.Max(row.Workflow.MemberReviewDays, 1) }, ct);
        switch (step)
        {
            case "secretary_review":
                await NotifyAsync(tx, notifier, await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct), row, "minutes_review", "محضر بانتظار مراجعة أمين السر",
                    "Minutes awaiting secretary review", "minutes_review:secretary:" + version.No, "warning", ct);
                break;
            case "chair_review":
            case "pending_final_approval" when row.Workflow.FinalApproval == "chair":
                await NotifyAsync(tx, notifier, await MinutesService.ChairAuthoritiesAsync(tx, row.BodyId, ct), row, "minutes_review",
                    step == "chair_review" ? "محضر بانتظار مراجعتكم" : "محضر بانتظار اعتمادكم النهائي", step == "chair_review" ? "Minutes awaiting chair review" : "Minutes awaiting final approval",
                    $"minutes_review:{step}:{version.No}", "warning", ct);
                break;
            case "pending_final_approval":
                await NotifyAsync(tx, notifier, await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct), row, "minutes_review",
                    "سجّل اعتماد صاحب الصلاحية للمحضر", "Record the authority's approval", $"minutes_review:external:{version.No}", "warning", ct);
                break;
            case "member_review":
                await NotifyAsync(tx, notifier, await MinutesService.BodyUsersAsync(tx, row.BodyId, ["chair", "vice_chair", "member", "alternate"], ct), row, "minutes_review",
                    $"المحضر متاح لملاحظاتكم خلال {row.Workflow.MemberReviewDays} أيام", "Minutes open for member comments", "minutes_review:members:" + version.No, "info", ct);
                break;
            case "pending_signatures":
                if (await CreateSignoffsAsync(tx, svc, notifier, row, version, by, ct) == 0)
                {
                    await RecordAsync(tx, row.Id, version.Id, "pending_signatures", "signatures_completed", by, note: "لا يوجد موقعون مطلوبون", ct: ct);
                    var steps = row.Workflow.Steps(row.AmendmentNo > 0);
                    return await EnterAsync(tx, svc, notifier, row, version, steps[steps.ToList().IndexOf(step) + 1], by, ct);
                }
                break;
        }
        return step;
    }

    /// <summary>الموقِّعون حسب سياسة الجهة: الحاضرون من الأعضاء المصوتين (عدا الرئيس أو معه)، أو كل الأعضاء المصوتين.</summary>
    private static async Task<int> CreateSignoffsAsync(ISql tx, MinutesService svc, INotifier notifier, MinutesRow row, VersionRow version, Guid by, CancellationToken ct)
    {
        var mode = row.Workflow.Signatures;
        var circulation = row.MeetingType == "circulation";
        var signers = await tx.QueryAsync("""
            SELECT DISTINCT ON (bm.user_id) bm.user_id, bm.role
            FROM meetings.meetings m JOIN bodies.body_members bm ON bm.body_id = m.body_id
            LEFT JOIN meetings.attendance_records a ON a.meeting_id = m.id AND a.user_id = bm.user_id
            WHERE m.id = @meeting AND bm.is_voting AND bm.role IN ('chair','vice_chair','member') AND bm.status <> 'suspended'
              AND bm.start_date <= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date AND (bm.end_date IS NULL OR bm.end_date >= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date)
              AND (@mode::text = 'all_members' OR @circulation::boolean OR a.status = 'present')
              AND (@mode::text <> 'present_members' OR bm.role <> 'chair')
            ORDER BY bm.user_id, bm.start_date DESC
            """, new { meeting = row.MeetingId, mode, circulation }, r => (User: r.GetGuid(0), Role: r.GetString(1)), ct);
        var (ready, readiness) = await svc.Gateway.ReadinessAsync(ct);
        if (ready)
        {
            // وحدة التوقيع تحدد من يوقّع وبأي ترتيب ونوع وفق مسار الجهة؛ المحاضر تقدّم المرشحين من سياسة الجهة والحضور
            var chairs = await MinutesService.BodyUsersAsync(tx, row.BodyId, ["chair"], ct);
            var candidates = new SignatureCandidates(await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct),
                signers.Where(x => x.Role != "chair").Select(x => x.User).ToList(), chairs, signers.Any(x => x.Role == "chair"));
            var pdf = await svc.SignatureCopyAsync(tx, row, version.No, ct);
            var created = await svc.Gateway.CreateForMinutesAsync(tx, new MinutesSignatureSubject(row.Id, version.Id, version.No, version.Hash, row.BodyId, row.MeetingType,
                row.MeetingRef ?? "", row.Classification, pdf, $"minutes-{row.MeetingRef}-v{version.No}-for-signature.pdf", candidates), by, ct);
            if (created is not null)
            {
                foreach (var x in created.Signers)
                    await tx.ExecuteAsync("""
                        INSERT INTO minutes.minutes_signoffs (id, minutes_version_id, user_id, method, content_hash, signer_role, method_reason, signature_request_id, signature_signer_id)
                        VALUES (@sid, @vid, @uid, @method, @hash, @role, 'esign', @rid, @signer)
                        """, new { sid = Guid.CreateVersion7(), vid = version.Id, uid = x.UserId, method = created.SignatureType, hash = version.Hash, role = x.Role,
                            rid = created.RequestId, signer = x.SignerId }, ct);
                return created.Signers.Count;
            }
        }
        var method = row.Workflow.SignatureFallback == "wet_signature" ? "wet_signature" : "attestation";
        var reason = ready ? "esign_no_signers" : readiness;
        foreach (var s in signers)
            await tx.ExecuteAsync("""
                INSERT INTO minutes.minutes_signoffs (id, minutes_version_id, user_id, method, content_hash, signer_role, method_reason)
                VALUES (@sid, @vid, @uid, @method, @hash, @role, @reason)
                ON CONFLICT DO NOTHING
                """, new { sid = Guid.CreateVersion7(), vid = version.Id, uid = s.User, method, hash = version.Hash, role = s.Role == "chair" ? "chair" : "member", reason }, ct);
        var pending = await tx.QueryAsync("SELECT user_id FROM minutes.minutes_signoffs WHERE minutes_version_id = @vid AND status = 'requested'", new { vid = version.Id }, r => r.GetGuid(0), ct);
        await NotifyAsync(tx, notifier, method == "wet_signature" ? await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct) : pending, row, "signoff_requested",
            method == "wet_signature" ? "ارفع التوقيعات الورقية للمحضر" : "محضر بانتظار إقرارك", method == "wet_signature" ? "Upload wet signatures" : "Minutes awaiting your sign-off",
            "signoff:" + version.No, "warning", ct);
        return pending.Count;
    }

    internal static async Task<string> AdvanceAsync(ISql tx, MinutesService svc, INotifier notifier, MinutesRow row, VersionRow version, Guid by, CancellationToken ct)
    {
        var steps = row.Workflow.Steps(row.AmendmentNo > 0).ToList();
        var i = steps.IndexOf(row.Status);
        if (i < 0 || i + 1 >= steps.Count) throw new ConflictException("invalid-transition", "لا توجد مرحلة تالية");
        return await EnterAsync(tx, svc, notifier, row, version, steps[i + 1], by, ct);
    }

    internal static async Task<bool> SignaturesCompleteAsync(ISql tx, Guid versionId, CancellationToken ct) =>
        await tx.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_signoffs WHERE minutes_version_id = @versionId AND status = 'requested'", new { versionId }, ct) == 0;

    // ---------------- الانتقالات ----------------

    /// <summary>رفع المحضر للمراجعة: يُنشأ إصدار مجمّد ببصمته، ثم ينتقل إلى أول مرحلة مفعلة في سياسة الجهة.</summary>
    private static async Task<IResult> Submit(Guid id, SubmitRequest req, Db db, MinutesService svc, IAccessService access, IVotingQuery voting, INotifier notifier,
        IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status is not ("draft" or "returned")) throw new ConflictException("invalid-transition", "يُرفع المحضر من المسودة أو بعد إعادته للتعديل فقط");
            if (row.MeetingStatus is not ("concluded" or "closed")) throw new ConflictException("meeting-not-concluded", "يُرفع المحضر للمراجعة بعد إغلاق الاجتماع");
            if (await voting.OpenMotionsAsync(tx, row.MeetingId, ct) > 0) throw new ConflictException("motion-open", "يوجد تصويت مفتوح؛ أغلقه أولًا");
            var (version, created) = await svc.CreateVersionAsync(tx, row, req.ChangeSummary, me.UserId, ct);
            await RecordAsync(tx, id, version.Id, row.Status, "submitted", me.UserId, "editor", hash: version.Hash, note: req.ChangeSummary, ct: ct);
            var steps = row.Workflow.Steps(row.AmendmentNo > 0);
            var status = await EnterAsync(tx, svc, notifier, row with { VersionNo = version.No }, version, steps[0], me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.submitted", "minutes", id.ToString(),
                Before: new { status = row.Status }, After: new { status, versionNo = version.No, version.Hash, newVersion = created, req.ChangeSummary }), ct);
            return new { id, status, versionNo = version.No, contentHash = version.Hash, newVersion = created };
        }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ApproveStep(Guid id, HashRequest req, Db db, MinutesService svc, IAccessService access, INotifier notifier, IAuditWriter audit,
        ICurrentUser me, CancellationToken ct)
    {
        var status = await db.ScalarAsync<string>("SELECT status FROM minutes.minutes WHERE id = @id", new { id }, ct) ?? throw new NotFoundException();
        var permission = status switch
        {
            "secretary_review" => Permissions.MinutesReviewSecretary,
            "chair_review" => Permissions.MinutesApprove,
            "member_review" => Permissions.MinutesEdit,
            _ => throw new ConflictException("invalid-transition", "لا توجد مرحلة مراجعة قائمة"),
        };
        await access.DemandAsync(permission, Scope.Record("minutes", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != status) throw new ConflictException("stale-state", "تغيرت حالة المحضر؛ أعد تحميله");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            DemandHash(version, req.ContentHash);
            if (status == "member_review" && await tx.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_comments WHERE minutes_id = @id AND status = 'open'", new { id }, ct) > 0)
                throw new ConflictException("open-comments", "عالج ملاحظات الأعضاء المفتوحة (قبولًا أو رفضًا) أو أعد المحضر للتعديل");
            var (capacity, onBehalf, delegation) = await CapacityAsync(tx, row.BodyId, me.UserId, ct);
            await RecordAsync(tx, id, version!.Id, status, "step_approved", me.UserId, capacity, onBehalf, delegation, "in_system", version.Hash, req.Note, ct: ct);
            var next = await AdvanceAsync(tx, svc, notifier, row, version, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.step_approved", "minutes", id.ToString(), Before: new { status }, After: new { status = next, capacity, req.Note }), ct);
            return new { id, status = next };
        }, ct);
        return Results.Ok(result);
    }

    /// <summary>إعادة المحضر للتعديل بسبب: تُبطل الإقرارات القائمة على الإصدار ويُشعر المعد.</summary>
    private static async Task<IResult> Return(Guid id, ReturnRequest req, Db db, MinutesService svc, IAccessService access, INotifier notifier, IAuditWriter audit,
        ICurrentUser me, CancellationToken ct)
    {
        var perms = await access.GetPermissionsAsync(Scope.Record("minutes", id), ct);
        if (!perms.Contains(Permissions.MinutesView)) throw new NotFoundException();
        if (!perms.Contains(Permissions.MinutesEdit) && !perms.Contains(Permissions.MinutesApprove) && !perms.Contains(Permissions.MinutesReviewSecretary))
            throw new ForbiddenException(permission: Permissions.MinutesApprove);
        await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (!MinutesService.ReviewStates.Contains(row.Status)) throw new ConflictException("invalid-transition", "يُعاد المحضر من مراحل المراجعة فقط");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            var invalidated = await tx.ExecuteAsync("""
                UPDATE minutes.minutes_signoffs SET status = 'invalidated', invalidated_reason = @reason WHERE minutes_version_id = @vid AND status <> 'invalidated'
                """, new { vid = version!.Id, reason = "أُعيد المحضر للتعديل: " + req.Reason }, ct);
            // طلب التوقيع الجاري على هذا الإصدار يُبطل بسبب موثق (لا يُستبدل مستنده بصمت)؛ التوقيعات المكتملة تبقى في سجله
            await svc.Gateway.VoidForVersionAsync(tx, version.Id, "أُعيد المحضر للتعديل: " + req.Reason, me.UserId, ct);
            var (capacity, onBehalf, delegation) = await CapacityAsync(tx, row.BodyId, me.UserId, ct);
            await RecordAsync(tx, id, version.Id, row.Status, "returned", me.UserId, capacity, onBehalf, delegation, "in_system", version.Hash, req.Reason, ct: ct);
            await tx.ExecuteAsync("UPDATE minutes.minutes SET status = 'returned', status_reason = @Reason, status_changed_at = now(), review_deadline = NULL WHERE id = @id",
                new { id, req.Reason }, ct);
            var preparers = (await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct)).Concat(version.CreatedBy is { } c ? [c] : []);
            await NotifyAsync(tx, notifier, preparers.Where(u => u != me.UserId), row, "minutes_returned", "أُعيد المحضر للتعديل", "Minutes returned for revision",
                "minutes_returned:" + version.No + ":" + DateTime.UtcNow.Ticks, "warning", ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.returned", "minutes", id.ToString(), Before: new { status = row.Status },
                After: new { status = "returned", req.Reason, invalidatedSignoffs = invalidated, capacity }), ct);
        }, ct);
        return Results.Ok(new { id, status = "returned" });
    }

    /// <summary>الإقرار الإلكتروني داخل النظام (المسار البديل حين تكون وحدة التوقيع معطلة): على البصمة الحالية، مع تحفظ اختياري.</summary>
    private static async Task<IResult> SignOff(Guid id, SignOffRequest req, Db db, MinutesService svc, IAccessService access, INotifier notifier, IAuditWriter audit,
        ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesSign, Scope.Record("minutes", id), ct);
        var status = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != "pending_signatures") throw new ConflictException("invalid-transition", "المحضر ليس في مرحلة التوقيع");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            var s = await tx.QuerySingleAsync("SELECT id, method FROM minutes.minutes_signoffs WHERE minutes_version_id = @vid AND user_id = @me AND status = 'requested' FOR UPDATE",
                new { vid = version!.Id, me = me.UserId }, r => (Id: r.GetGuid(0), Method: r.GetString(1)), ct);
            if (s == default) throw new ConflictException("no-signoff-request", "لا يوجد طلب إقرار قائم لك على هذا الإصدار");
            DemandHash(version, req.ContentHash);
            if (s.Method is "electronic_signature" or SignatureTypes.InAppApproval or SignatureTypes.ProviderESign or SignatureTypes.PdfDigitalSignature)
                throw new ConflictException("electronic-signature-required", "هذا الإقرار يتم عبر وحدة التوقيع الإلكتروني (طلب التوقيع في صفحة المحضر)");
            if (s.Method == "wet_signature") throw new ConflictException("wet-signature-required", "هذا التوقيع ورقي يرفعه أمين السر");
            await tx.ExecuteAsync("UPDATE minutes.minutes_signoffs SET status = 'valid', signed_at = now(), reservation_text = @Reservation, recorded_by = @me WHERE id = @sid",
                new { sid = s.Id, req.Reservation, me = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signed_off", "minutes", id.ToString(), After: new { method = "attestation", req.ContentHash, reservation = req.Reservation is not null }), ct);
            if (!string.IsNullOrWhiteSpace(req.Reservation))
                await NotifyAsync(tx, notifier, await MinutesService.ChairAuthoritiesAsync(tx, row.BodyId, ct), row, "minutes_reservation", "تحفظ على المحضر عند الإقرار",
                    "Reservation recorded on minutes", "reservation:" + s.Id, "warning", ct);
            if (!await SignaturesCompleteAsync(tx, version.Id, ct)) return row.Status;
            await RecordAsync(tx, id, version.Id, "pending_signatures", "signatures_completed", me.UserId, method: "attestation", hash: version.Hash, ct: ct);
            return await AdvanceAsync(tx, svc, notifier, row, version, me.UserId, ct);
        }, ct);
        return Results.Ok(new { id, status });
    }

    /// <summary>التوقيع الورقي: يرفع أمين السر الصفحة الموقعة مستندًا مرتبطًا بالمحضر ويسجلها لموقّع أو أكثر.</summary>
    private static async Task<IResult> RecordWet(Guid id, WetRequest req, Db db, MinutesService svc, IAccessService access, IDocumentStore store, INotifier notifier,
        IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var status = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != "pending_signatures") throw new ConflictException("invalid-transition", "المحضر ليس في مرحلة التوقيع");
            if (!await store.IsLinkedAsync(tx, req.DocumentId!.Value, "minutes", id, ct)) throw AppValidationException.For("documentId", "ارفع الصفحة الموقعة مرفقًا للمحضر أولًا");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            foreach (var uid in req.UserIds.Distinct())
            {
                var n = await tx.ExecuteAsync("""
                    UPDATE minutes.minutes_signoffs SET status = 'valid', method = 'wet_signature', signed_at = now(), evidence_document_id = @doc, recorded_by = @me,
                      method_reason = COALESCE(method_reason, '') || CASE WHEN method <> 'wet_signature' THEN ' (سُجل ورقيًا)' ELSE '' END
                    WHERE minutes_version_id = @vid AND user_id = @uid AND status = 'requested'
                    """, new { vid = version!.Id, uid, doc = req.DocumentId, me = me.UserId }, ct);
                if (n == 0) throw AppValidationException.For("userIds", "أحد الموقعين ليس لديه طلب توقيع قائم");
            }
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.wet_signatures_recorded", "minutes", id.ToString(), After: new { req.DocumentId, users = req.UserIds.Count, req.Note }), ct);
            if (!await SignaturesCompleteAsync(tx, version!.Id, ct)) return row.Status;
            await RecordAsync(tx, id, version.Id, "pending_signatures", "signatures_completed", me.UserId, method: "wet_signature", hash: version.Hash, evidence: req.DocumentId, ct: ct);
            return await AdvanceAsync(tx, svc, notifier, row, version, me.UserId, ct);
        }, ct);
        return Results.Ok(new { id, status });
    }

    /// <summary>الاعتماد النهائي داخل النظام من الرئيس أو المفوض بصلاحياته. في المعاملة نفسها: تُصدر القرارات وتُرقّم، وتُفعَّل المهام، ويُغلق الاجتماع.</summary>
    private static async Task<IResult> Approve(Guid id, HashRequest req, Db db, MinutesService svc, IAccessService access, IVotingQuery voting, IResolutionIssuer issuer,
        ITaskActivator tasks, INotifier notifier, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesApprove, Scope.Record("minutes", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != "pending_final_approval") throw new ConflictException("invalid-transition", "المحضر ليس بانتظار الاعتماد النهائي");
            if (row.Workflow.FinalApproval != "chair") throw new ConflictException("external-approval-required", "سياسة الجهة تقتضي تسجيل اعتماد صاحب الصلاحية خارج النظام");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            DemandHash(version, req.ContentHash);
            if (version!.CreatedBy == me.UserId) throw new ConflictException("separation-of-duties", "لا يعتمد المحضر من أعدّ إصداره");
            var (capacity, onBehalf, delegation) = await CapacityAsync(tx, row.BodyId, me.UserId, ct);
            await RecordAsync(tx, id, version.Id, "pending_final_approval", "approved", me.UserId, capacity, onBehalf, delegation, "in_system", version.Hash, req.Note, ct: ct);
            return await FinalizeAsync(tx, row, version, voting, issuer, tasks, notifier, audit, me.UserId, capacity, "in_system", ct);
        }, ct);
        var sha = await svc.PublishApprovedPdfAsync(id, me.UserId, ct);
        return Results.Ok(new { result.id, result.status, result.versionNo, result.contentHash, result.verificationCode, result.issued, result.tasksActivated, pdfSha256 = sha });
    }

    /// <summary>مسار الاعتماد البديل الذي يحدده المكتب: يعتمد صاحب الصلاحية خارج النظام (خطاب أو توقيع ورقي)، ويسجل أمين السر من اعتمد وكيف وبأي مرجع ودليل.</summary>
    private static async Task<IResult> RecordExternal(Guid id, ExternalApprovalRequest req, Db db, MinutesService svc, IAccessService access, IVotingQuery voting,
        IResolutionIssuer issuer, ITaskActivator tasks, IDocumentStore store, INotifier notifier, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != "pending_final_approval") throw new ConflictException("invalid-transition", "المحضر ليس بانتظار الاعتماد النهائي");
            if (row.Workflow.FinalApproval != "recorded_external") throw new ConflictException("chair-approval-required", "سياسة الجهة تقتضي اعتماد الرئيس داخل النظام");
            var version = await svc.CurrentVersionAsync(tx, id, ct);
            DemandHash(version, req.ContentHash);
            if (!await store.IsLinkedAsync(tx, req.DocumentId!.Value, "minutes", id, ct)) throw AppValidationException.For("documentId", "ارفع مستند الاعتماد مرفقًا للمحضر أولًا");
            var approver = string.IsNullOrWhiteSpace(req.ApproverTitle) ? req.ApproverName : $"{req.ApproverName} — {req.ApproverTitle}";
            await RecordAsync(tx, id, version!.Id, "pending_final_approval", "approved", me.UserId, "secretary", method: "recorded_external", hash: version.Hash, note: req.Note,
                extApprover: approver, extRef: req.ReferenceNo, extDate: req.ApprovedOn, evidence: req.DocumentId, ct: ct);
            return await FinalizeAsync(tx, row, version, voting, issuer, tasks, notifier, audit, me.UserId, "recorded_external:" + approver, "recorded_external", ct);
        }, ct);
        var sha = await svc.PublishApprovedPdfAsync(id, me.UserId, ct);
        return Results.Ok(new { result.id, result.status, result.versionNo, result.contentHash, result.verificationCode, result.issued, result.tasksActivated, pdfSha256 = sha });
    }

    private sealed record FinalizeResult(Guid id, string status, int versionNo, string contentHash, string verificationCode, IReadOnlyList<string> issued, int tasksActivated);

    private static async Task<FinalizeResult> FinalizeAsync(ISql tx, MinutesRow row, VersionRow version, IVotingQuery voting, IResolutionIssuer issuer, ITaskActivator tasks,
        INotifier notifier, IAuditWriter audit, Guid by, string capacity, string method, CancellationToken ct)
    {
        if (await voting.OpenMotionsAsync(tx, row.MeetingId, ct) > 0) throw new ConflictException("motion-open", "يوجد تصويت مفتوح على قرارات هذا الاجتماع");
        var code = row.VerificationCode ?? MinutesService.NewVerificationCode();
        await tx.ExecuteAsync("""
            UPDATE minutes.minutes SET status = 'approved', approved_version_id = @vid, approved_at = now(), approved_by = @by, verification_code = @code,
              status_changed_at = now(), status_reason = NULL, review_deadline = NULL, pdf_document_id = NULL, pdf_sha256 = NULL
            WHERE id = @id
            """, new { id = row.Id, vid = version.Id, by, code }, ct);
        var issued = await issuer.IssueForMinutesAsync(tx, row.Id, version.Id, by, ct);
        var activated = await tasks.ActivateAsync(tx, issued.Issued, issued.NotIssued, by, ct);
        await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'closed' WHERE id = @m AND status = 'concluded'", new { m = row.MeetingId }, ct);
        await NotifyAsync(tx, notifier, await MinutesService.BodyUsersAsync(tx, row.BodyId, ["chair", "vice_chair", "secretary", "member", "alternate", "observer"], ct), row,
            "minutes_approved", "اعتُمد المحضر", "Minutes approved", "minutes_approved:" + version.No, "info", ct);
        await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.approved", "minutes", row.Id.ToString(),
            After: new { versionNo = version.No, version.Hash, capacity, method, issued = issued.Numbers, notIssued = issued.NotIssued.Count, tasksActivated = activated, amendmentNo = row.AmendmentNo }), ct);
        await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.minutes_approved", "meeting", row.MeetingId.ToString(), After: new { minutesId = row.Id, versionNo = version.No }), ct);
        return new FinalizeResult(row.Id, "approved", version.No, version.Hash, code, issued.Numbers, activated);
    }

    /// <summary>تعديل محضر معتمد: لا تعديل صامت؛ يُفتح تعديل بسبب موثق، فينتج إصدار جديد يعاد اعتماده وفق السياسة، ويبقى الإصدار المعتمد السابق وقراراته كما هي.</summary>
    private static async Task<IResult> Amend(Guid id, ReturnRequest req, Db db, MinutesService svc, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var row = await svc.LoadAsync(tx, id, true, ct);
            if (row.Status != "approved") throw new ConflictException("invalid-transition", "يُفتح التعديل على محضر معتمد فقط");
            var no = row.AmendmentNo + 1;
            await tx.ExecuteAsync("""
                UPDATE minutes.minutes SET status = 'draft', amendment_no = @no, amendment_reason = @Reason, status_changed_at = now(), status_reason = @Reason WHERE id = @id
                """, new { id, no, req.Reason }, ct);
            await RecordAsync(tx, id, row.ApprovedVersionId, "approved", "amendment_opened", me.UserId, "editor", note: req.Reason, ct: ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.amendment_opened", "minutes", id.ToString(), After: new { amendmentNo = no, req.Reason }), ct);
            return new { id, status = "draft", amendmentNo = no, steps = row.Workflow.Steps(true) };
        }, ct);
        return Results.Ok(result);
    }
}
