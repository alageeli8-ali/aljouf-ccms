using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;

namespace Ccms.Modules.Minutes;

/// <summary>منفذ المحاضر لنتائج وحدة التوقيع: المحاضر وحدها تقرر اكتمال الإقرارات وحالة المحضر (09 §2).</summary>
internal sealed class MinutesSignatureSink(MinutesService svc, INotifier notifier, IAuditWriter audit) : IMinutesSignatureSink
{
    private async Task<(MinutesRow Row, VersionRow Version)?> LoadAsync(ISql tx, Guid versionId, CancellationToken ct)
    {
        var minutesId = await tx.ScalarAsync<Guid?>("SELECT minutes_id FROM minutes.minutes_versions WHERE id = @versionId", new { versionId }, ct);
        if (minutesId is null) return null;
        var row = await svc.LoadAsync(tx, minutesId.Value, true, ct);
        var version = await svc.CurrentVersionAsync(tx, row.Id, ct);
        return version is null || version.Id != versionId || row.Status != "pending_signatures" ? null : (row, version);
    }

    public async Task<CreatedSignatureRequest?> RestartAsync(ISql tx, Guid minutesId, Guid by, CancellationToken ct = default)
    {
        var row = await svc.LoadAsync(tx, minutesId, true, ct);
        if (row.Status != "pending_signatures") throw new ConflictException("minutes-not-awaiting-signatures", "المحضر ليس في مرحلة التوقيع");
        var version = (await svc.CurrentVersionAsync(tx, minutesId, ct))!;
        var pending = await tx.QueryAsync("""
            SELECT user_id, signer_role FROM minutes.minutes_signoffs WHERE minutes_version_id = @vid AND status = 'requested'
            """, new { vid = version.Id }, r => (User: r.GetGuid(0), Role: r.GetString(1)), ct);
        if (pending.Count == 0) throw new ConflictException("no-pending-signers", "لا يوجد موقّعون متبقون على هذا الإصدار");
        var candidates = new SignatureCandidates(pending.Where(p => p.Role == "secretary").Select(p => p.User).ToList(),
            pending.Where(p => p.Role == "member").Select(p => p.User).ToList(), pending.Where(p => p.Role == "chair").Select(p => p.User).ToList(),
            pending.Any(p => p.Role == "chair"));
        var pdf = await svc.SignatureCopyAsync(tx, row, version.No, ct);
        var created = await svc.Gateway.CreateForMinutesAsync(tx, new MinutesSignatureSubject(row.Id, version.Id, version.No, version.Hash, row.BodyId, row.MeetingType,
            row.MeetingRef ?? "", row.Classification, pdf, $"minutes-{row.MeetingRef}-v{version.No}-for-signature.pdf", candidates, Restart: true), by, ct);
        if (created is null) return null;
        foreach (var x in created.Signers)
            await tx.ExecuteAsync("""
                UPDATE minutes.minutes_signoffs SET method = @method, method_reason = 'esign', signature_request_id = @rid, signature_signer_id = @signer
                WHERE minutes_version_id = @vid AND user_id = @uid AND status = 'requested'
                """, new { vid = version.Id, uid = x.UserId, method = created.SignatureType, rid = created.RequestId, signer = x.SignerId }, ct);
        await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signature_restarted", "minutes", row.Id.ToString(),
            After: new { requestId = created.RequestId, created.SignatureType, signers = created.Signers.Count }), ct);
        return created;
    }

    public async Task<bool> IsAwaitingSignaturesAsync(ISql sql, Guid minutesVersionId, CancellationToken ct = default) =>
        await sql.ScalarAsync<long>("""
            SELECT count(*) FROM minutes.minutes mi JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no
            WHERE v.id = @minutesVersionId AND mi.status = 'pending_signatures'
            """, new { minutesVersionId }, ct) > 0;

    public async Task SignerCompletedAsync(ISql tx, Guid minutesVersionId, Guid userId, Guid requestId, Guid signerId, string signatureType, Guid actedBy,
        Guid? onBehalfOf, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(tx, minutesVersionId, ct) ?? throw new ConflictException("minutes-not-awaiting-signatures", "المحضر لم يعد في مرحلة التوقيع لهذا الإصدار");
        var (row, version) = loaded;
        var n = await tx.ExecuteAsync("""
            UPDATE minutes.minutes_signoffs SET status = 'valid', signed_at = now(), recorded_by = @actedBy, on_behalf_of_user_id = @onBehalfOf
            WHERE minutes_version_id = @minutesVersionId AND user_id = @userId AND status = 'requested' AND signature_signer_id = @signerId
            """, new { minutesVersionId, userId, signerId, actedBy, onBehalfOf }, ct);
        if (n == 0) throw new ConflictException("no-signoff-request", "لا يوجد إقرار مطلوب يطابق هذا التوقيع");
        await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signed_off", "minutes", row.Id.ToString(),
            After: new { method = signatureType, requestId, versionNo = version.No, onBehalfOf }), ct);
        if (!await Workflow.SignaturesCompleteAsync(tx, version.Id, ct)) return;
        await Workflow.RecordAsync(tx, row.Id, version.Id, "pending_signatures", "signatures_completed", actedBy, method: signatureType, hash: version.Hash,
            note: "اكتملت التوقيعات عبر وحدة التوقيع", ct: ct);
        await Workflow.AdvanceAsync(tx, svc, notifier, row, version, actedBy, ct);
    }

    public async Task SignerDeclinedAsync(ISql tx, Guid minutesVersionId, Guid userId, string reason, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(tx, minutesVersionId, ct);
        if (loaded is null) return;
        var (row, version) = loaded.Value;
        var text = "رفض التوقيع وطلب التعديل: " + reason;
        // التوقيعات المكتملة على الإصدار لا تُحذف: تُبطل إقراراتها في المحضر بسبب صريح، وتبقى في سجل طلب التوقيع
        var invalidated = await tx.ExecuteAsync("""
            UPDATE minutes.minutes_signoffs SET status = 'invalidated', invalidated_reason = @text WHERE minutes_version_id = @vid AND status <> 'invalidated'
            """, new { vid = version.Id, text }, ct);
        var (capacity, onBehalf, delegation) = await Workflow.CapacityAsync(tx, row.BodyId, userId, ct);
        await Workflow.RecordAsync(tx, row.Id, version.Id, "pending_signatures", "returned", userId, capacity, onBehalf, delegation, "in_system", version.Hash, text, ct: ct);
        await tx.ExecuteAsync("UPDATE minutes.minutes SET status = 'returned', status_reason = @text, status_changed_at = now(), review_deadline = NULL WHERE id = @id",
            new { id = row.Id, text }, ct);
        var preparers = (await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct)).Concat(version.CreatedBy is { } c ? [c] : []);
        await Workflow.NotifyAsync(tx, notifier, preparers.Where(u => u != userId), row, "minutes_returned", "رُفض توقيع المحضر وطُلب تعديله", "Signature declined — revision requested",
            "signature_declined:" + version.No + ":" + DateTime.UtcNow.Ticks, "warning", ct);
        await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.returned", "minutes", row.Id.ToString(), Before: new { status = row.Status },
            After: new { status = "returned", reason = text, invalidatedSignoffs = invalidated, source = "signature_declined" }), ct);
    }

    public async Task<int> SwitchToFallbackAsync(ISql tx, Guid minutesVersionId, string reason, Guid by, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(tx, minutesVersionId, ct);
        if (loaded is null) return 0;
        var (row, version) = loaded.Value;
        var method = row.Workflow.SignatureFallback == "wet_signature" ? "wet_signature" : "attestation";
        var n = await tx.ExecuteAsync("""
            UPDATE minutes.minutes_signoffs SET method = @method, method_reason = 'esign_switched_to_fallback', signature_request_id = NULL, signature_signer_id = NULL
            WHERE minutes_version_id = @vid AND status = 'requested'
            """, new { vid = version.Id, method }, ct);
        await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signature_fallback", "minutes", row.Id.ToString(), After: new { method, reason, signoffs = n }), ct);
        var pending = await tx.QueryAsync("SELECT user_id FROM minutes.minutes_signoffs WHERE minutes_version_id = @vid AND status = 'requested'", new { vid = version.Id }, r => r.GetGuid(0), ct);
        await Workflow.NotifyAsync(tx, notifier, method == "wet_signature" ? await MinutesService.BodyUsersAsync(tx, row.BodyId, ["secretary"], ct) : pending, row, "signoff_requested",
            method == "wet_signature" ? "ارفع التوقيعات الورقية للمحضر" : "محضر بانتظار إقرارك (المسار البديل)", method == "wet_signature" ? "Upload wet signatures" : "Minutes awaiting your sign-off (fallback)",
            "signoff_fallback:" + version.No, "warning", ct);
        if (n == 0 && await Workflow.SignaturesCompleteAsync(tx, version.Id, ct))
        {
            await Workflow.RecordAsync(tx, row.Id, version.Id, "pending_signatures", "signatures_completed", by, method: method, hash: version.Hash, ct: ct);
            await Workflow.AdvanceAsync(tx, svc, notifier, row, version, by, ct);
        }
        return n;
    }
}
