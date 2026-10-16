using System.Security.Cryptography;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Minutes;

internal sealed record MinutesRow(Guid Id, Guid MeetingId, Guid BodyId, string Status, int VersionNo, MinutesWorkflow Workflow, int AmendmentNo,
    Guid? ApprovedVersionId, Guid TemplateId, string MeetingStatus, string MeetingType, string? MeetingRef, string MeetingTitle, string ContentJson,
    string Classification, string? VerificationCode, Guid? PdfDocumentId, string? AmendmentReason);

internal sealed record VersionRow(Guid Id, int No, string Hash, DateTime CreatedAt, Guid? CreatedBy, string? CreatedByName, string? ChangeSummary);

/// <summary>خدمات مشتركة للمحضر: التحميل، والإصدارات وبصمتها، والإقرارات، وسجل الاعتماد، والإجراءات المتاحة، وPDF.</summary>
internal sealed class MinutesService(Db db, ISignatureGateway gateway, IDocumentStore store, ILogger<MinutesService> log)
{
    public static readonly string[] ReviewStates = ["secretary_review", "chair_review", "member_review", "pending_signatures", "pending_final_approval"];

    public async Task<MinutesRow> LoadAsync(ISql sql, Guid id, bool forUpdate, CancellationToken ct)
    {
        var row = await sql.QuerySingleAsync($"""
            SELECT mi.id, mi.meeting_id, m.body_id, mi.status, mi.current_version_no, mi.workflow::text, mi.amendment_no, mi.approved_version_id, mi.template_id,
                   m.status AS meeting_status, m.meeting_type, m.reference_no, m.title_ar, mi.draft_content::text, mi.classification, mi.verification_code,
                   mi.pdf_document_id, mi.amendment_reason
            FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.id = @id {(forUpdate ? "FOR UPDATE OF mi" : "")}
            """, new { id }, r => new MinutesRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), MinutesWorkflow.Parse(r.GetString(5)),
            r.GetInt32(6), r.Uuid("approved_version_id"), r.GetGuid(8), r.GetString(9), r.GetString(10), r.Str("reference_no"), r.GetString(12), r.GetString(13),
            r.GetString(14), r.Str("verification_code"), r.Uuid("pdf_document_id"), r.Str("amendment_reason")), ct);
        return row ?? throw new NotFoundException();
    }

    public Task<VersionRow?> CurrentVersionAsync(ISql sql, Guid id, CancellationToken ct) =>
        sql.QuerySingleAsync<VersionRow?>("""
            SELECT v.id, v.version_no, v.content_hash, v.created_at, v.created_by, u.display_name_ar, v.change_summary
            FROM minutes.minutes_versions v LEFT JOIN iam.users u ON u.id = v.created_by WHERE v.minutes_id = @id ORDER BY v.version_no DESC LIMIT 1
            """, new { id }, r => new VersionRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetDateTime(3), r.Uuid("created_by"), r.Str("display_name_ar"), r.Str("change_summary")), ct);

    /// <summary>ينشئ إصدارًا غير قابل للتعديل من المحتوى واللقطة إن تغيّرا عن آخر إصدار، وإلا يُعاد استخدام الأخير.
    /// البصمة SHA-256 تُحسب في القاعدة على التمثيل القانوني لـ jsonb (08 §2.3).</summary>
    public async Task<(VersionRow Version, bool Created)> CreateVersionAsync(ISql tx, MinutesRow row, string? changeSummary, Guid by, CancellationToken ct)
    {
        var content = MinutesContent.Parse(row.ContentJson);
        var snapshot = (await SnapshotBuilder.BuildAsync(tx, row.Id, content, ct)).ToJson();
        var contentJson = content.ToJson();
        var last = await tx.QuerySingleAsync("""
            SELECT id, (content = @content::jsonb AND snapshot = @snapshot::jsonb) AS same FROM minutes.minutes_versions WHERE minutes_id = @id ORDER BY version_no DESC LIMIT 1
            """, new { id = row.Id, content = contentJson, snapshot }, r => (Id: r.GetGuid(0), Same: r.GetBoolean(1)), ct);
        if (last != default && last.Same) return ((await CurrentVersionAsync(tx, row.Id, ct))!, false);
        var no = row.VersionNo + 1;
        var vid = Guid.CreateVersion7();
        await tx.ExecuteAsync("""
            INSERT INTO minutes.minutes_versions (id, minutes_id, version_no, content, snapshot, content_hash, created_by, change_summary, amendment_no, created_in_status)
            VALUES (@vid, @id, @no, @content::jsonb, @snapshot::jsonb,
                    encode(sha256(convert_to(jsonb_build_object('minutesId', @idText::text, 'versionNo', @no::int, 'content', @content::jsonb, 'snapshot', @snapshot::jsonb)::text, 'UTF8')), 'hex'),
                    @by, @changeSummary, @amendment, @status)
            """, new { vid, id = row.Id, idText = row.Id.ToString(), no, content = contentJson, snapshot, by, changeSummary, amendment = row.AmendmentNo, status = row.Status }, ct);
        await tx.ExecuteAsync("UPDATE minutes.minutes SET current_version_no = @no WHERE id = @id", new { id = row.Id, no }, ct);
        return ((await CurrentVersionAsync(tx, row.Id, ct))!, true);
    }

    public async Task<List<SignoffView>> SignoffsAsync(ISql sql, Guid id, Guid? versionId, CancellationToken ct) =>
        versionId is null ? [] : await sql.QueryAsync("""
            SELECT s.id, s.user_id, u.display_name_ar, s.signer_role, s.method, s.status, s.reservation_text, s.requested_at, s.signed_at, s.method_reason,
                   s.evidence_document_id, rb.display_name_ar AS recorded_by, s.invalidated_reason, v.version_no
            FROM minutes.minutes_signoffs s JOIN iam.users u ON u.id = s.user_id JOIN minutes.minutes_versions v ON v.id = s.minutes_version_id
            LEFT JOIN iam.users rb ON rb.id = s.recorded_by
            WHERE v.minutes_id = @id ORDER BY v.version_no DESC, s.requested_at, u.display_name_ar
            """, new { id }, r => new SignoffView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.Str("reservation_text"),
            r.GetDateTime(7), r.Time("signed_at"), r.Str("method_reason"), r.Uuid("evidence_document_id"), r.Str("recorded_by"), r.Str("invalidated_reason"), r.GetInt32(13)), ct);

    public sealed record SignoffView(Guid Id, Guid UserId, string Name, string SignerRole, string Method, string Status, string? Reservation,
        DateTime RequestedAt, DateTime? SignedAt, string? MethodReason, Guid? EvidenceDocumentId, string? RecordedBy, string? InvalidatedReason, int VersionNo);

    public sealed record ApprovalView(Guid Id, string Step, string Decision, string Actor, string? Capacity, string? OnBehalfOf, string? Method, string? Note,
        string? ExternalApprover, string? ExternalReference, DateOnly? ExternalDate, Guid? EvidenceDocumentId, string? ContentHash, int? VersionNo, DateTime CreatedAt);

    public Task<List<ApprovalView>> ApprovalsAsync(ISql sql, Guid id, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT a.id, a.step, a.decision, u.display_name_ar, a.acting_capacity, ob.display_name_ar AS on_behalf, a.method, a.note, a.external_approver,
                   a.external_reference, a.external_date, a.evidence_document_id, a.content_hash, v.version_no, a.created_at
            FROM minutes.minutes_approvals a JOIN iam.users u ON u.id = a.actor_user_id LEFT JOIN iam.users ob ON ob.id = a.on_behalf_of_user_id
            LEFT JOIN minutes.minutes_versions v ON v.id = a.minutes_version_id
            WHERE a.minutes_id = @id ORDER BY a.created_at, a.id
            """, new { id }, r => new ApprovalView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Str("acting_capacity"), r.Str("on_behalf"),
            r.Str("method"), r.Str("note"), r.Str("external_approver"), r.Str("external_reference"), r.Date("external_date"), r.Uuid("evidence_document_id"),
            r.Str("content_hash"), r.IsDBNull(13) ? null : r.GetInt32(13), r.GetDateTime(14)), ct);

    /// <summary>الطريقة الفعلية للتوقيع: إلكتروني إن كانت البوابة جاهزة، وإلا البديل المحدد في سياسة الجهة مع سببه.</summary>
    public async Task<(string Method, string Reason)> SignatureMethodAsync(MinutesWorkflow wf, CancellationToken ct)
    {
        var (ready, reason) = await gateway.ReadinessAsync(ct);
        return ready ? ("electronic_signature", "esign") : (wf.SignatureFallback == "wet_signature" ? "wet_signature" : "attestation", reason);
    }

    public static async Task<List<Guid>> BodyUsersAsync(ISql sql, Guid bodyId, string[] roles, CancellationToken ct) =>
        await sql.QueryAsync("""
            SELECT DISTINCT user_id FROM bodies.body_members WHERE body_id = @bodyId AND status = 'active' AND role = ANY(@roles::text[])
              AND start_date <= current_date AND (end_date IS NULL OR end_date >= current_date)
            """, new { bodyId, roles }, r => r.GetGuid(0), ct);

    public static async Task<List<Guid>> ChairAuthoritiesAsync(ISql sql, Guid bodyId, CancellationToken ct)
    {
        var list = await BodyUsersAsync(sql, bodyId, ["chair"], ct);
        list.AddRange(await sql.QueryAsync("""
            SELECT delegate_user_id FROM iam.delegations WHERE body_id = @bodyId AND scope = 'chair_authority' AND status = 'active' AND valid_from <= now() AND valid_to > now()
            """, new { bodyId }, r => r.GetGuid(0), ct));
        return [.. list.Distinct()];
    }

    public async Task<object> ActionsAsync(ISql sql, MinutesRow row, VersionRow? current, IReadOnlySet<string> perms, Guid me, CancellationToken ct)
    {
        var editable = row.Status is "draft" or "returned";
        var meetingDone = row.MeetingStatus is "concluded" or "closed";
        var openMotions = await sql.ScalarAsync<long>("SELECT count(*) FROM deliberation.motions WHERE meeting_id = @m AND status = 'open'", new { m = row.MeetingId }, ct);
        var mySignoff = current is null ? null : await sql.ScalarAsync<string>("""
            SELECT method FROM minutes.minutes_signoffs WHERE minutes_version_id = @v AND user_id = @me AND status = 'requested'
            """, new { v = current.Id, me }, ct);
        bool P(string p) => perms.Contains(p);
        return new
        {
            canEdit = editable && P(Permissions.MinutesEdit),
            canManageResolutions = editable && P(Permissions.ResolutionManage),
            canSubmit = editable && P(Permissions.MinutesEdit) && meetingDone && openMotions == 0,
            submitBlockedReason = !editable ? null : !meetingDone ? "meeting-not-concluded" : openMotions > 0 ? "motion-open" : null,
            canApproveStep = row.Status switch
            {
                "secretary_review" => P(Permissions.MinutesReviewSecretary),
                "chair_review" => P(Permissions.MinutesApprove),
                "member_review" => P(Permissions.MinutesEdit),
                _ => false,
            },
            canReturn = ReviewStates.Contains(row.Status) && (P(Permissions.MinutesEdit) || P(Permissions.MinutesApprove) || P(Permissions.MinutesReviewSecretary)),
            canSign = row.Status == "pending_signatures" && mySignoff == "attestation" && P(Permissions.MinutesSign),
            mySignoffMethod = mySignoff,
            canRecordWet = row.Status == "pending_signatures" && P(Permissions.MinutesEdit),
            canApprove = row.Status == "pending_final_approval" && row.Workflow.FinalApproval == "chair" && P(Permissions.MinutesApprove) && current?.CreatedBy != me,
            canRecordExternal = row.Status == "pending_final_approval" && row.Workflow.FinalApproval == "recorded_external" && P(Permissions.MinutesEdit),
            canAmend = row.Status == "approved" && P(Permissions.MinutesEdit),
            canComment = row.Status != "approved" && P(Permissions.MinutesComment),
            canResolveComments = P(Permissions.MinutesEdit),
        };
    }

    // ---------------- PDF ----------------

    private async Task<MinutesPdfModel> ModelAsync(MinutesRow row, int? versionNo, CancellationToken ct)
    {
        var template = await Templates.GetSectionsAsync(db, row.TemplateId, ct);
        var approvals = (await ApprovalsAsync(db, row.Id, ct)).Select(a => new ApprovalLine(a.Step, a.Decision, a.Actor, a.Capacity, a.OnBehalfOf, a.Method, a.Note,
            a.ExternalApprover, a.ExternalReference, a.ExternalDate, a.CreatedAt, a.VersionNo)).ToList();
        if (versionNo is null && row.Status is "draft" or "returned")
        {
            var content = MinutesContent.Parse(row.ContentJson);
            var live = await SnapshotBuilder.BuildAsync(db, row.Id, content, ct);
            return new MinutesPdfModel(live, content, template, row.VersionNo + 1, null, row.Status, null, row.AmendmentNo, row.AmendmentReason, approvals, [], true);
        }
        var v = await db.QuerySingleAsync("""
            SELECT id, version_no, content::text, snapshot::text, content_hash, amendment_no FROM minutes.minutes_versions
            WHERE minutes_id = @id AND (@no::int IS NULL OR version_no = @no) ORDER BY version_no DESC LIMIT 1
            """, new { id = row.Id, no = versionNo }, r => (Id: r.GetGuid(0), No: r.GetInt32(1), Content: r.GetString(2), Snapshot: r.GetString(3), Hash: r.GetString(4),
            Amendment: r.GetInt32(5)), ct);
        if (v == default) throw new NotFoundException("الإصدار غير موجود");
        var snapshot = MinutesSnapshot.Parse(v.Snapshot)!;
        var approved = row.Status == "approved" && v.Id == row.ApprovedVersionId
            || await db.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_approvals WHERE minutes_version_id = @vid AND decision = 'approved'", new { vid = v.Id }, ct) > 0;
        if (approved)
        {
            // الأرقام تُمنح عند الاعتماد: تُعرض أرقام القرارات والمهام الصادرة مع هذا الإصدار، دون تغيير محتواه المبصوم
            var numbers = (await db.QueryAsync("SELECT id, number, status FROM resolutions.resolutions WHERE minutes_id = @id", new { id = row.Id },
                r => (Id: r.GetGuid(0), Number: r.Str("number"), Status: r.GetString(2)), ct)).ToDictionary(x => x.Id);
            var taskRefs = (await db.QueryAsync("SELECT t.id, t.reference_no, t.status FROM tasks.tasks t JOIN resolutions.resolutions r ON r.id = t.resolution_id WHERE r.minutes_id = @id",
                new { id = row.Id }, r => (Id: r.GetGuid(0), Ref: r.Str("reference_no"), Status: r.GetString(2)), ct)).ToDictionary(x => x.Id);
            snapshot = snapshot with
            {
                Resolutions = snapshot.Resolutions.Select(r => numbers.TryGetValue(r.Id, out var n) ? r with { Number = n.Number, Status = n.Status } : r).ToList(),
                Tasks = snapshot.Tasks.Select(t => taskRefs.TryGetValue(t.Id, out var x) ? t with { ReferenceNo = x.Ref, Status = x.Status } : t).ToList(),
            };
        }
        var signoffs = (await SignoffsAsync(db, row.Id, v.Id, ct)).Where(s => s.VersionNo == v.No && s.Status != "invalidated")
            .Select(s => new SignoffLine(s.Name, s.SignerRole, s.Method, s.Status, s.Reservation, s.SignedAt)).ToList();
        return new MinutesPdfModel(snapshot, MinutesContent.Parse(v.Content), template, v.No, v.Hash, row.Status, approved ? row.VerificationCode : null,
            v.Amendment, row.AmendmentReason, approvals.Where(a => a.VersionNo is null || a.VersionNo <= v.No).ToList(), signoffs, !approved);
    }

    public async Task<(byte[] Bytes, string Name)> PdfAsync(Guid id, int? versionNo, CancellationToken ct)
    {
        var row = await LoadAsync(db, id, false, ct);
        var approvedNo = row.ApprovedVersionId is { } av ? await db.ScalarAsync<int?>("SELECT version_no FROM minutes.minutes_versions WHERE id = @av", new { av }, ct) : null;
        var wantsApproved = row.Status == "approved" && (versionNo is null || versionNo == approvedNo);
        if (wantsApproved && row.PdfDocumentId is { } doc && await store.ReadAsync(doc, ct) is { } stored)
            return (stored, $"minutes-{row.MeetingRef}-v{approvedNo}.pdf");
        var model = await ModelAsync(row, wantsApproved ? approvedNo : versionNo, ct);
        return (MinutesPdf.Render(model), $"minutes-{row.MeetingRef}-v{model.VersionNo}{(model.Draft ? "-draft" : "")}.pdf");
    }

    /// <summary>يولّد PDF الإصدار المعتمد ويحفظه ببصمته بعد الاعتماد. فشله لا يلغي الاعتماد؛ يُعاد توليده عند أول طلب.</summary>
    public async Task<string?> PublishApprovedPdfAsync(Guid id, Guid by, CancellationToken ct)
    {
        try
        {
            var row = await LoadAsync(db, id, false, ct);
            if (row.Status != "approved" || row.ApprovedVersionId is null) return null;
            var approvedNo = await db.ScalarAsync<int>("SELECT version_no FROM minutes.minutes_versions WHERE id = @v", new { v = row.ApprovedVersionId }, ct);
            var bytes = MinutesPdf.Render(await ModelAsync(row, approvedNo, ct));
            return await db.InTransactionAsync(async tx =>
            {
                var doc = await store.SaveAsync(tx, $"minutes-{row.MeetingRef}-v{approvedNo}.pdf", "application/pdf", bytes, "minutes", id, "minutes_pdf", row.Classification, by, ct);
                await tx.ExecuteAsync("UPDATE minutes.minutes SET pdf_document_id = @doc, pdf_sha256 = @sha WHERE id = @id AND approved_version_id = @v",
                    new { id, doc = doc.Id, sha = doc.Sha256, v = row.ApprovedVersionId }, ct);
                return doc.Sha256;
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "تعذر توليد PDF المحضر المعتمد {Id}", id);
            return null;
        }
    }

    public static string NewVerificationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> b = stackalloc byte[10];
        RandomNumberGenerator.Fill(b);
        var chars = new char[11];
        for (int i = 0, j = 0; i < 10; i++, j++)
        {
            if (i == 5) chars[j++] = '-';
            chars[j] = alphabet[b[i] % alphabet.Length];
        }
        return new string(chars);
    }
}
