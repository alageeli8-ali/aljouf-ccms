using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Ccms.Modules.Signatures.Pdf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Signatures;

internal sealed class SigUnavailableException(string message) : AppException("esign-provider-unavailable", 503, message);

/// <summary>منطق الطلبات: التوقيع بأنواعه، والرفض، وجلسة المزوّد، والإرسال وإعادة المحاولة، والإجراءات الصريحة عند التعطيل، والاستدعاءات الراجعة، والصيانة.</summary>
internal sealed class SigningService(Db db, IFeatureService features, IAccessService access, ICurrentUser me, ProviderRegistry providers, IDocumentStore store,
    IMinutesSignatureSink minutes, VerificationService verification, INotifier notifier, IAuditWriter audit, IReauthenticator reauth,
    IOptions<SignatureOptions> options, IHttpContextAccessor http, ILogger<SigningService> log)
{
    private async Task<SigRequest> DemandAsync(Guid id, string permission, CancellationToken ct)
    {
        var r = await SigData.RequestAsync(db, id, false, ct) ?? throw new NotFoundException("طلب التوقيع غير موجود");
        await access.DemandAsync(permission, Scope.Record("minutes", r.MinutesId), ct);
        return r;
    }

    private async Task DemandModuleEnabledAsync(CancellationToken ct)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct))
            throw new ConflictException("esign-disabled", "وحدة التوقيع الإلكتروني معطلة: لا تُنفذ إجراءات توقيع جديدة. الطلبات الجارية محفوظة وتنتظر إجراءً صريحًا من أمين السر.");
    }

    // ---------------- إعادة التحقق من الهوية ----------------

    public async Task<object> ReauthAsync(string password, CancellationToken ct)
    {
        var ok = await reauth.VerifyPasswordAsync(me.UserId, password, ct);
        if (ok is null) throw new ConflictException("reauth-sso-required", "الحساب يدخل بالدخول الموحد: أعد الدخول عبر Microsoft قبل التوقيع (غير متاح في هذه البيئة)");
        if (ok == false) throw new AccessDeniedException("reauth-failed", "كلمة المرور غير صحيحة");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = DateTime.UtcNow.AddMinutes(Math.Clamp(options.Value.ReauthMinutes, 1, 15));
        await db.ExecuteAsync("INSERT INTO sig.reauth_grants (id, user_id, token_hash, method, expires_at) VALUES (@id, @user, @hash, 'password', @expires)",
            new { id = Guid.CreateVersion7(), user = me.UserId, hash = Hash(token), expires }, ct);
        await audit.WriteAsync(new AuditEntry("SIG", "esign.reauthenticated", "user", me.UserId.ToString(), After: new { method = "password", expires }), ct);
        return new { reauthToken = token, expiresAt = expires, method = "password" };
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<JsonObject?> ConsumeReauthAsync(ISql tx, SigRequest r, string? token, CancellationToken ct)
    {
        if (!r.ReauthRequired) return null;
        if (string.IsNullOrEmpty(token)) throw new AccessDeniedException("reauth-required", "يلزم إعادة التحقق من الهوية قبل التوقيع");
        var n = await tx.ExecuteAsync("""
            UPDATE sig.reauth_grants SET used_at = now(), used_for = @req WHERE token_hash = @hash AND user_id = @user AND used_at IS NULL AND expires_at > now()
            """, new { req = r.Id, hash = Hash(token), user = me.UserId }, ct);
        if (n == 0) throw new AccessDeniedException("reauth-required", "انتهت صلاحية إعادة التحقق أو استُخدمت؛ أعد التحقق من هويتك");
        return new JsonObject { ["method"] = "password", ["at"] = DateTime.UtcNow };
    }

    // ---------------- الموقّع الفعلي (بالأصالة أو بالنيابة) ----------------

    private async Task<(SigSigner Signer, Guid? Delegation)> ActingSignerAsync(ISql tx, SigRequest r, Guid? onBehalfOf, CancellationToken ct)
    {
        var signers = await SigData.SignersAsync(tx, r.Id, ct);
        if (onBehalfOf is null)
        {
            var mine = signers.FirstOrDefault(s => s.UserId == me.UserId) ?? throw new AccessDeniedException("not-a-signer", "لست من الموقّعين على هذا الطلب");
            return (mine, null);
        }
        var target = signers.FirstOrDefault(s => s.UserId == onBehalfOf) ?? throw AppValidationException.For("onBehalfOf", "الموقّع الأصيل غير موجود في الطلب");
        if (!target.AllowDelegation || target.Role != "chair")
            throw new AccessDeniedException("delegation-not-allowed", "مسار التوقيع لا يسمح بالتوقيع بالنيابة لهذا الموقّع");
        var delegation = await tx.ScalarAsync<Guid?>("""
            SELECT id FROM iam.delegations WHERE body_id = @body AND delegator_user_id = @delegator AND delegate_user_id = @me AND scope = 'chair_authority'
              AND status = 'active' AND valid_from <= now() AND valid_to > now() ORDER BY valid_from DESC LIMIT 1
            """, new { body = r.BodyId, delegator = target.UserId, me = me.UserId }, ct)
            ?? throw new AccessDeniedException("no-valid-delegation", "لا يوجد تفويض ساري موثق بصلاحيات الرئيس لك");
        return (target, delegation);
    }

    private static void DemandTurn(SigSigner s)
    {
        switch (s.Status)
        {
            case "signed": throw new ConflictException("already-signed", "سبق التوقيع");
            case "declined" or "cancelled": throw new ConflictException("signer-closed", "لم يعد هذا الموقّع مطلوبًا في الطلب");
            case "waiting": throw new ConflictException("not-your-turn", "التوقيع بالتسلسل: لم يحن دورك بعد");
        }
    }

    private async Task DemandLiveAsync(ISql tx, SigRequest r, CancellationToken ct)
    {
        if (r.Status == "on_hold") throw new ConflictException("request-on-hold", "الطلب معلّق بإجراء صريح؛ يستأنفه أمين السر");
        if (r.Status != "in_progress" && r.Status != "queued") throw new ConflictException("request-closed", "طلب التوقيع لم يعد قائمًا");
        if (!await minutes.IsAwaitingSignaturesAsync(tx, r.VersionId, ct))
            throw new ConflictException("version-not-awaiting", "الإصدار المرتبط بالطلب لم يعد في مرحلة التوقيع");
        if (r.ExpiresAt < DateTime.UtcNow) throw new ConflictException("request-expired", "انتهت صلاحية طلب التوقيع");
    }

    private async Task<byte[]> CurrentDocumentAsync(SigRequest r, CancellationToken ct)
    {
        var id = r.CurrentDocumentId ?? r.DocumentId;
        var bytes = await store.ReadAsync(id, ct) ?? throw new ConflictException("document-missing", "نسخة التوقيع غير موجودة في المخزن");
        if (ESignatureGateway.Sha256(bytes) != (r.CurrentDocumentSha256 ?? r.DocumentSha256))
            throw new ConflictException("document-integrity-failed", "بصمة النسخة الجاري توقيعها لا تطابق المسجل؛ أُوقف التوقيع ولا تُستبدل النسخة بصمت");
        return bytes;
    }

    // ---------------- التوقيع ----------------

    public sealed record SignRequest(string? ReauthToken, Guid? OnBehalfOf, string? Note);

    public async Task<object> SignAsync(Guid id, SignRequest req, CancellationToken ct)
    {
        var r0 = await DemandAsync(id, Permissions.MinutesSign, ct);
        await DemandModuleEnabledAsync(ct);
        if (r0.Type == SignatureTypes.ProviderESign) throw new ConflictException("sign-at-provider", "هذا الطلب يوقَّع لدى المزوّد؛ افتح جلسة التوقيع عبره");
        byte[]? docBefore = r0.Type == SignatureTypes.PdfDigitalSignature ? await CurrentDocumentAsync(r0, ct) : null;
        if (r0.Type == SignatureTypes.InAppApproval) await CurrentDocumentAsync(r0, ct);   // سلامة النسخة قبل الموافقة عليها
        bool completed;
        try
        {
            completed = await db.InTransactionAsync(async tx =>
            {
                var r = (await SigData.RequestAsync(tx, id, true, ct))!;
                await DemandLiveAsync(tx, r, ct);
                if ((r.CurrentDocumentSha256 ?? r.DocumentSha256) != (r0.CurrentDocumentSha256 ?? r0.DocumentSha256))
                    throw new ConflictException("stale-document", "وقّع موقّع آخر للتو؛ أعد المحاولة");
                var (signer, delegation) = await ActingSignerAsync(tx, r, req.OnBehalfOf, ct);
                DemandTurn(signer);
                var reauthEvidence = await ConsumeReauthAsync(tx, r, req.ReauthToken, ct);
                var evidence = new JsonObject
                {
                    ["type"] = r.Type, ["reauth"] = reauthEvidence, ["contentHash"] = r.ContentHash, ["versionNo"] = r.VersionNo, ["note"] = req.Note,
                    ["client"] = new JsonObject
                    {
                        ["ipSha256"] = http.HttpContext?.Connection.RemoteIpAddress is { } ip ? Hash(ip.ToString()) : null,
                        ["userAgent"] = http.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 200)] : null,
                    },
                };
                if (delegation is { } d) evidence["delegationId"] = d.ToString();
                var signedDocSha = r.CurrentDocumentSha256 ?? r.DocumentSha256;
                if (r.Type == SignatureTypes.PdfDigitalSignature)
                {
                    var actorName = await tx.ScalarAsync<string>("SELECT display_name_ar FROM iam.users WHERE id = @id", new { id = me.UserId }, ct) ?? "";
                    var now = DateTime.UtcNow;
                    var reason = delegation is null ? $"توقيع المحضر — {r.Reference} — الإصدار {r.VersionNo}" : $"توقيع بالنيابة عن {signer.Name} — {r.Reference}";
                    var prepared = PdfSigner.Prepare(docBefore!, "Sig" + signer.Position, actorName, reason, now);
                    var provider = providers.Get(r.Provider);
                    var remote = await WithRetryAsync(() => provider.SignDigestAsync(new RemoteSignRequest(me.UserId, actorName, prepared.Digest(), reason, now, r.Id), ct), ct);
                    var signed = PdfSigner.Embed(prepared, remote.Cms);
                    var check = await verification.VerifyPdfAsync(signed, ct);
                    var last = check.Signatures.LastOrDefault();
                    if (last is null || !last.CryptographicallyValid || !last.CoversWholeDocument)
                        throw new ConflictException("signature-verification-failed", "فشل التحقق من التوقيع الرقمي بعد إنشائه؛ لم يُحفظ: " + (last?.Error ?? "غير معروف"));
                    var doc = await store.SaveAsync(tx, $"{r.Reference}-rev{signer.Position}.pdf", "application/pdf", signed, "minutes", r.MinutesId, "signature_document",
                        "restricted", me.UserId, ct);
                    await tx.ExecuteAsync("UPDATE sig.requests SET current_document_id = @doc, current_document_sha256 = @sha, updated_at = now() WHERE id = @id",
                        new { id, doc = doc.Id, sha = doc.Sha256 }, ct);
                    evidence["certificate"] = new JsonObject
                    {
                        ["subject"] = remote.Certificate.Subject, ["issuer"] = remote.Certificate.Issuer, ["serial"] = remote.Certificate.SerialNumber,
                        ["thumbprint"] = remote.Certificate.Thumbprint, ["notAfter"] = remote.Certificate.NotAfter.ToUniversalTime(), ["chain"] = last.Chain,
                    };
                    evidence["revisionSha256"] = doc.Sha256;
                    evidence["signatureIndex"] = check.Signatures.Count;
                    evidence["provider"] = r.Provider;
                }
                await tx.ExecuteAsync("""
                    UPDATE sig.signers SET status = 'signed', signed_at = now(), acted_by_user_id = @me, delegation_id = @delegation, signed_document_sha256 = @sha,
                      evidence = @evidence::jsonb, updated_at = now()
                    WHERE id = @sid
                    """, new { sid = signer.Id, me = me.UserId, delegation, sha = signedDocSha, evidence = evidence.ToJsonString() }, ct);
                await SigData.EventAsync(tx, id, signer.Id, "signed", "user", me.UserId, new { type = r.Type, onBehalf = delegation is not null, signedDocumentSha256 = signedDocSha }, ct);
                await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.signed", "minutes", r.MinutesId.ToString(),
                    After: new { requestId = id, r.Reference, type = r.Type, signer = signer.UserId, onBehalf = delegation is not null, delegation }), ct);
                await minutes.SignerCompletedAsync(tx, r.VersionId, signer.UserId, r.Id, signer.Id, r.Type, me.UserId, delegation is null ? null : signer.UserId, ct);
                await SignerFlow.ActivateAsync(tx, notifier, id, r.MinutesId, r.Reference, ct);
                return await CompleteIfDoneAsync(tx, r, ct);
            }, ct);
        }
        catch (SignatureProviderUnavailableException e)
        {
            await SigData.EventAsync(db, id, null, "provider_unavailable", "system", me.UserId, new { operation = "sign_digest", error = e.Message }, ct);
            throw new SigUnavailableException("تعذر الوصول إلى مزوّد التوقيع بعد إعادة المحاولة؛ لم يُحفظ أي توقيع. أعد المحاولة لاحقًا.");
        }
        if (completed) await FinalizeAsync(id, ct);
        return await RequestStatusAsync(id, ct);
    }

    private async Task<object> RequestStatusAsync(Guid id, CancellationToken ct)
    {
        var r = (await SigData.RequestAsync(db, id, false, ct))!;
        var status = await db.ScalarAsync<string>("SELECT status FROM minutes.minutes WHERE id = @m", new { m = r.MinutesId }, ct);
        return new { id, status = r.Status, minutesStatus = status };
    }

    private async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await call(); }
            catch (SignatureProviderUnavailableException e) when (attempt < 3)
            {
                log.LogWarning("انقطاع مؤقت في مزوّد التوقيع (المحاولة {Attempt}): {Error}", attempt, e.Message);
                await Task.Delay(150 * attempt, ct);
            }
        }
    }

    /// <summary>عند اكتمال كل التوقيعات: يكتمل الطلب (التقرير والنسخة النهائية في <see cref="FinalizeAsync"/> بعد الالتزام).</summary>
    private static async Task<bool> CompleteIfDoneAsync(ISql tx, SigRequest r, CancellationToken ct)
    {
        if (r.Type == SignatureTypes.ProviderESign) return false;   // يكتمل بنسخة المزوّد النهائية
        var open = await tx.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE request_id = @id AND status <> 'signed'", new { id = r.Id }, ct);
        if (open > 0) return false;
        await tx.ExecuteAsync("""
            UPDATE sig.requests SET status = 'completed', completed_at = now(), updated_at = now(),
              signed_document_id = CASE WHEN signature_type = 'pdf_digital_signature' THEN current_document_id END,
              signed_document_sha256 = CASE WHEN signature_type = 'pdf_digital_signature' THEN current_document_sha256 END
            WHERE id = @id
            """, new { id = r.Id }, ct);
        await SigData.EventAsync(tx, r.Id, null, "completed", "system", null, null, ct);
        return true;
    }

    /// <summary>بعد الالتزام: تقرير التحقق المحفوظ، والنسخة النهائية مرفقة بالمحضر إن فُعّل إصدار PDF الموقّع، والإشعار.</summary>
    public async Task FinalizeAsync(Guid id, CancellationToken ct)
    {
        var r = (await SigData.RequestAsync(db, id, false, ct))!;
        if (r.Status != "completed") return;
        var signers = await SigData.SignersAsync(db, id, ct);
        var report = await verification.ReportAsync(r, signers, ct);
        await db.InTransactionAsync(async tx =>
        {
            Guid? output = null;
            if (r.PdfOutput && r.SignedDocumentId is { } sd && await store.ReadAsync(sd, ct) is { } bytes)
                output = (await store.SaveAsync(tx, $"{r.Reference}-signed.pdf", "application/pdf", bytes, "minutes", r.MinutesId, "signed_pdf", "restricted",
                    r.CreatedBy, ct)).Id;
            await tx.ExecuteAsync("UPDATE sig.requests SET verification = @v::jsonb, updated_at = now() WHERE id = @id",
                new { id, v = report.ToJsonString() }, ct);
            await SigData.EventAsync(tx, id, null, "verified", "system", null, new { overall = report["overall"]?.GetValue<string>(), outputDocument = output }, ct);
            foreach (var u in (await SecretariesAsync(tx, r.BodyId, ct)).Append(r.CreatedBy).Distinct())
                await notifier.NotifyAsync(tx, new NotificationRequest(u, "signature_completed", $"اكتمل التوقيع ({r.Reference})", $"Signing completed ({r.Reference})",
                    $"/minutes/{r.MinutesId}?tab=signatures", $"esign-done:{id}:{u}", "info"), ct);
        }, ct);
    }

    private static Task<List<Guid>> SecretariesAsync(ISql sql, Guid bodyId, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT DISTINCT user_id FROM bodies.body_members WHERE body_id = @bodyId AND role = 'secretary' AND status = 'active'
              AND start_date <= (now() AT TIME ZONE 'Asia/Riyadh')::date AND (end_date IS NULL OR end_date >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
            """, new { bodyId }, r => r.GetGuid(0), ct);

    // ---------------- الرفض وطلب التعديل ----------------

    public sealed record DeclineRequest(string Reason, Guid? OnBehalfOf);

    public async Task<object> DeclineAsync(Guid id, DeclineRequest req, CancellationToken ct)
    {
        var r0 = await DemandAsync(id, Permissions.MinutesSign, ct);
        await DemandModuleEnabledAsync(ct);
        await db.InTransactionAsync(async tx =>
        {
            var r = (await SigData.RequestAsync(tx, id, true, ct))!;
            await DemandLiveAsync(tx, r, ct);
            var (signer, delegation) = await ActingSignerAsync(tx, r, req.OnBehalfOf, ct);
            if (signer.Status is not ("pending" or "waiting")) throw new ConflictException("signer-closed", "لا يمكنك رفض التوقيع في حالته الحالية");
            await DeclineCoreAsync(tx, r, signer, req.Reason, me.UserId, "user", ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.declined", "minutes", r.MinutesId.ToString(),
                After: new { requestId = id, r0.Reference, signer = signer.UserId, req.Reason, delegation }), ct);
        }, ct);
        return await RequestStatusAsync(id, ct);
    }

    private async Task DeclineCoreAsync(ISql tx, SigRequest r, SigSigner signer, string reason, Guid? actor, string source, CancellationToken ct)
    {
        await tx.ExecuteAsync("UPDATE sig.signers SET status = 'declined', decline_reason = @reason, acted_by_user_id = @actor, updated_at = now() WHERE id = @sid",
            new { sid = signer.Id, reason, actor = actor ?? signer.UserId }, ct);
        await tx.ExecuteAsync("UPDATE sig.signers SET status = 'cancelled', updated_at = now() WHERE request_id = @id AND status IN ('waiting','pending')", new { id = r.Id }, ct);
        await tx.ExecuteAsync("""
            UPDATE sig.requests SET status = 'declined', status_reason = @text, closed_by = @actor, closed_at = now(), updated_at = now(),
              pending_provider_cancel = (provider_ref IS NOT NULL AND signature_type = 'provider_esign' AND @source <> 'provider')
            WHERE id = @id
            """, new { id = r.Id, text = $"رفض {signer.Name} التوقيع: {reason}", actor = actor ?? signer.UserId, source }, ct);
        await SigData.EventAsync(tx, r.Id, signer.Id, "declined", source, actor, new { reason }, ct);
        await minutes.SignerDeclinedAsync(tx, r.VersionId, signer.UserId, reason, ct);
    }

    // ---------------- التوقيع عبر المزوّد ----------------

    public sealed record SessionRequest(string? ReauthToken);

    public async Task<object> ProviderSessionAsync(Guid id, SessionRequest req, CancellationToken ct)
    {
        var r = await DemandAsync(id, Permissions.MinutesSign, ct);
        await DemandModuleEnabledAsync(ct);
        if (r.Type != SignatureTypes.ProviderESign) throw new ConflictException("not-provider-request", "هذا الطلب لا يوقَّع لدى مزوّد خارجي");
        await db.InTransactionAsync(async tx =>
        {
            var cur = (await SigData.RequestAsync(tx, id, true, ct))!;
            await DemandLiveAsync(tx, cur, ct);
            var (signer, _) = await ActingSignerAsync(tx, cur, null, ct);
            DemandTurn(signer);
            await ConsumeReauthAsync(tx, cur, req.ReauthToken, ct);
        }, ct);
        if (r.Status == "queued") await DispatchAsync(id, "user", ct);
        r = (await SigData.RequestAsync(db, id, false, ct))!;
        if (r.Status == "queued" || r.ProviderRef is null)
            throw new SigUnavailableException($"لم يُرسل المستند إلى المزوّد بعد (المحاولة {r.Attempts}): {r.LastError}. ستُعاد المحاولة آليًا.");
        var signer2 = (await SigData.SignersAsync(db, id, ct)).First(s => s.UserId == me.UserId);
        try
        {
            var url = await WithRetryAsync(() => providers.Get(r.Provider).SigningUrlAsync(r.ProviderRef!, signer2.ProviderSignerRef!, ct), ct);
            await SigData.EventAsync(db, id, signer2.Id, "provider_session_opened", "user", me.UserId, null, ct);
            return new { url, provider = r.Provider, productionGrade = providers.Get(r.Provider).Readiness().ProductionGrade };
        }
        catch (SignatureProviderUnavailableException e)
        {
            await SigData.EventAsync(db, id, signer2.Id, "provider_unavailable", "system", me.UserId, new { operation = "signing_url", error = e.Message }, ct);
            throw new SigUnavailableException("تعذر الوصول إلى مزوّد التوقيع؛ أعد المحاولة لاحقًا.");
        }
    }

    /// <summary>إرسال المستند للمزوّد مع إعادة المحاولة بتراجع أسّي؛ بعد الحد الأقصى يفشل الطلب صراحة ويُشعر أمين السر.</summary>
    public async Task DispatchAsync(Guid id, string source, CancellationToken ct, DateTime? asOf = null)
    {
        var r = await SigData.RequestAsync(db, id, false, ct);
        if (r is null || r.Status != "queued" || !await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct)) return;
        var provider = providers.Get(r.Provider);
        var signers = await SigData.SignersAsync(db, id, ct);
        var now = asOf ?? DateTime.UtcNow;
        try
        {
            var bytes = await CurrentDocumentAsync(r, ct);
            var envelope = await provider.CreateEnvelopeAsync(new EnvelopeRequest(r.Id, r.Reference, $"توقيع محضر — {r.Reference}", bytes, r.DocumentSha256,
                signers.Select(s => new EnvelopeSigner(s.Id.ToString(), s.UserId, s.Name, null, s.Position)).ToList(), r.Sequential, r.ExpiresAt,
                $"توقيع المحضر — الإصدار {r.VersionNo}"), ct);
            await db.InTransactionAsync(async tx =>
            {
                var n = await tx.ExecuteAsync("""
                    UPDATE sig.requests SET provider_ref = @ref, status = 'in_progress', dispatch_attempts = dispatch_attempts + 1, next_attempt_at = NULL, last_error = NULL,
                      updated_at = now() WHERE id = @id AND status = 'queued'
                    """, new { id, @ref = envelope.ProviderRef }, ct);
                if (n == 0) return;
                foreach (var (sid, sref) in envelope.SignerRefs)
                    await tx.ExecuteAsync("UPDATE sig.signers SET provider_signer_ref = @sref WHERE id = @sid", new { sid = Guid.Parse(sid), sref }, ct);
                await SigData.EventAsync(tx, id, null, "dispatched", source, null, new { providerRef = envelope.ProviderRef, attempt = r.Attempts + 1 }, ct);
            }, ct);
        }
        catch (Exception e) when (e is SignatureProviderUnavailableException or SignatureProviderNotConfiguredException)
        {
            var attempts = r.Attempts + 1;
            var final = e is SignatureProviderNotConfiguredException || attempts >= Math.Max(1, options.Value.RetryMaxAttempts);
            var next = now.AddMinutes(Math.Pow(2, attempts));
            await db.InTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("""
                    UPDATE sig.requests SET dispatch_attempts = @attempts, last_error = @error, next_attempt_at = @next,
                      status = CASE WHEN @final THEN 'failed' ELSE status END,
                      status_reason = CASE WHEN @final THEN @reason ELSE status_reason END,
                      closed_at = CASE WHEN @final THEN now() ELSE closed_at END, updated_at = now()
                    WHERE id = @id AND status = 'queued'
                    """, new { id, attempts, error = e.Message, next = final ? (DateTime?)null : next, final,
                        reason = $"تعذر إرسال المستند للمزوّد بعد {attempts} محاولات: {e.Message}" }, ct);
                await SigData.EventAsync(tx, id, null, final ? "dispatch_failed_final" : "dispatch_failed", source, null,
                    new { attempt = attempts, error = e.Message, nextAttemptAt = final ? (DateTime?)null : next }, ct);
                if (final)
                    foreach (var u in await SecretariesAsync(tx, r.BodyId, ct))
                        await notifier.NotifyAsync(tx, new NotificationRequest(u, "signature_failed", $"تعذر إرسال طلب التوقيع ({r.Reference})", $"Signature dispatch failed ({r.Reference})",
                            $"/minutes/{r.MinutesId}?tab=signatures", $"esign-failed:{id}:{u}", "critical"), ct);
            }, ct);
        }
    }

    // ---------------- إجراءات صريحة على الطلبات الجارية ----------------

    public sealed record ReasonRequest(string Reason);

    public async Task<object> FallbackAsync(Guid id, ReasonRequest req, CancellationToken ct)
    {
        var r0 = await DemandAsync(id, Permissions.ESignManage, ct);
        var switched = await db.InTransactionAsync(async tx =>
        {
            var r = (await SigData.RequestAsync(tx, id, true, ct))!;
            if (!r.Live) throw new ConflictException("request-closed", "الطلب ليس جاريًا");
            var text = "تحويل صريح للمسار البديل: " + req.Reason;
            await tx.ExecuteAsync("""
                UPDATE sig.requests SET status = 'cancelled', status_reason = @text, closed_by = @me, closed_at = now(), updated_at = now(),
                  pending_provider_cancel = (provider_ref IS NOT NULL AND signature_type = 'provider_esign')
                WHERE id = @id
                """, new { id, text, me = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE sig.signers SET status = 'cancelled', updated_at = now() WHERE request_id = @id AND status IN ('waiting','pending')", new { id }, ct);
            var signed = await tx.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE request_id = @id AND status = 'signed'", new { id }, ct);
            var n = await minutes.SwitchToFallbackAsync(tx, r.VersionId, req.Reason, me.UserId, ct);
            await SigData.EventAsync(tx, id, null, "switched_to_fallback", "admin", me.UserId, new { req.Reason, keptSignatures = signed, fallbackSignoffs = n }, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.switched_to_fallback", "minutes", r.MinutesId.ToString(),
                After: new { requestId = id, r0.Reference, req.Reason, keptSignatures = signed, fallbackSignoffs = n }), ct);
            return n;
        }, ct);
        return new { id, status = "cancelled", fallbackSignoffs = switched };
    }

    public async Task<object> HoldAsync(Guid id, ReasonRequest req, CancellationToken ct)
    {
        var r0 = await DemandAsync(id, Permissions.ESignManage, ct);
        await db.InTransactionAsync(async tx =>
        {
            var r = (await SigData.RequestAsync(tx, id, true, ct))!;
            if (r.Status is not ("in_progress" or "queued")) throw new ConflictException("invalid-state", "يُعلّق الطلب الجاري فقط");
            await tx.ExecuteAsync("UPDATE sig.requests SET status = 'on_hold', status_reason = @reason, updated_at = now() WHERE id = @id",
                new { id, reason = "معلّق بإجراء صريح: " + req.Reason }, ct);
            await SigData.EventAsync(tx, id, null, "held", "admin", me.UserId, new { req.Reason, previous = r.Status }, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.request_held", "minutes", r.MinutesId.ToString(), After: new { requestId = id, r0.Reference, req.Reason }), ct);
        }, ct);
        return new { id, status = "on_hold" };
    }

    public async Task<object> ResumeAsync(Guid id, CancellationToken ct)
    {
        var r0 = await DemandAsync(id, Permissions.ESignManage, ct);
        await DemandModuleEnabledAsync(ct);
        string status = "";
        await db.InTransactionAsync(async tx =>
        {
            var r = (await SigData.RequestAsync(tx, id, true, ct))!;
            if (r.Status != "on_hold") throw new ConflictException("invalid-state", "الطلب غير معلّق");
            status = r.Type == SignatureTypes.ProviderESign && r.ProviderRef is null ? "queued" : "in_progress";
            await tx.ExecuteAsync("UPDATE sig.requests SET status = @status, status_reason = NULL, next_attempt_at = CASE WHEN @status = 'queued' THEN now() END, updated_at = now() WHERE id = @id",
                new { id, status }, ct);
            await SigData.EventAsync(tx, id, null, "resumed", "admin", me.UserId, null, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.request_resumed", "minutes", r.MinutesId.ToString(), After: new { requestId = id, r0.Reference }), ct);
        }, ct);
        await ApplyDeferredAsync(id, ct);
        return new { id, status };
    }

    public async Task<object> RestartAsync(Guid minutesId, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ESignRequest, Scope.Record("minutes", minutesId), ct);
        await features.DemandEnabledAsync(FeatureKeys.ESignatureEnabled, ct);
        var created = await db.InTransactionAsync(async tx =>
        {
            var live = await tx.ScalarAsync<long>("""
                SELECT count(*) FROM sig.requests r JOIN minutes.minutes mi ON mi.id = r.minutes_id
                JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no
                WHERE r.minutes_id = @minutesId AND r.minutes_version_id = v.id AND r.status IN ('queued','in_progress','on_hold')
                """, new { minutesId }, ct);
            if (live > 0) throw new ConflictException("request-live", "يوجد طلب توقيع جارٍ على الإصدار الحالي");
            return await minutes.RestartAsync(tx, minutesId, me.UserId, ct)
                ?? throw new ConflictException("esign-not-ready", "لا يمكن إنشاء طلب توقيع: النوع المحدد غير مهيأ أو لا يوجد موقّعون");
        }, ct);
        return new { requestId = created.RequestId, created.SignatureType, signers = created.Signers.Count };
    }

    // ---------------- الاستدعاءات الراجعة ----------------

    public async Task<IResult> CallbackAsync(string providerCode, HttpRequest request, CancellationToken ct)
    {
        var provider = providers.Find(providerCode);
        if (provider is null) return Results.NotFound();
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        return await ProcessCallbackAsync(provider, request.Headers, ms.ToArray(), ct);
    }

    public async Task<IResult> ProcessCallbackAsync(ISignatureProvider provider, IHeaderDictionary headers, byte[] body, CancellationToken ct)
    {
        var payloadSha = Convert.ToHexStringLower(SHA256.HashData(body));
        var cb = provider.VerifyCallback(headers, body);
        if (cb is null)
        {
            await db.ExecuteAsync("""
                INSERT INTO sig.provider_callbacks (id, provider, provider_event_id, signature_valid, payload_sha256, outcome, detail)
                VALUES (@id, @p, @eid, false, @sha, 'rejected_signature', 'توقيع الرسالة غير صحيح؛ لم يُقرأ محتواها')
                """, new { id = Guid.CreateVersion7(), p = provider.Code, eid = "unverified-" + Guid.NewGuid().ToString("N"), sha = payloadSha }, ct);
            log.LogWarning("استدعاء راجع بتوقيع غير صحيح من المزوّد {Provider}", provider.Code);
            return Results.Problem(statusCode: 401, title: "توقيع الرسالة غير صحيح", extensions: new Dictionary<string, object?> { ["code"] = "callback-signature-invalid" });
        }
        var payload = Json.Sanitize(JsonNode.Parse(body))!.ToJsonString();
        var (outcome, completedId) = await db.InTransactionAsync(async tx =>
        {
            var cid = Guid.CreateVersion7();
            var inserted = await tx.ExecuteAsync("""
                INSERT INTO sig.provider_callbacks (id, provider, provider_event_id, provider_ref, signature_valid, payload_sha256, payload, outcome)
                VALUES (@cid, @p, @eid, @ref, true, @sha, @payload::jsonb, 'ignored')
                ON CONFLICT (provider, provider_event_id) WHERE signature_valid DO NOTHING
                """, new { cid, p = provider.Code, eid = cb.EventId, @ref = cb.ProviderRef, sha = payloadSha, payload }, ct);
            if (inserted == 0) return ("duplicate", (Guid?)null);
            async Task<(string, Guid?)> Done(string o, string? detail = null, Guid? requestId = null, Guid? completed = null)
            {
                await tx.ExecuteAsync("UPDATE sig.provider_callbacks SET outcome = @o, detail = @detail, applied_at = CASE WHEN @o = 'applied' THEN now() END WHERE id = @cid",
                    new { cid, o, detail }, ct);
                if (requestId is not null) await tx.ExecuteAsync("UPDATE sig.provider_callbacks SET request_id = @requestId WHERE id = @cid", new { cid, requestId }, ct);
                return (o, completed);
            }
            var rid = await tx.ScalarAsync<Guid?>("SELECT id FROM sig.requests WHERE provider = @p AND provider_ref = @ref", new { p = provider.Code, @ref = cb.ProviderRef }, ct);
            if (rid is null) return await Done("rejected_unknown", "لا يوجد طلب بهذا المرجع لدى المزوّد");
            var r = (await SigData.RequestAsync(tx, rid.Value, true, ct))!;
            if (cb.CorrelationId != r.Id || (cb.DocumentSha256 is not null && cb.DocumentSha256 != r.DocumentSha256))
            {
                await SigData.EventAsync(tx, r.Id, null, "callback_rejected", "provider", null, new { cb.EventId, cb.Event, reason = "mismatch" }, ct);
                return await Done("rejected_mismatch", "معرّف الطلب أو بصمة المستند لا يطابقان الطلب", r.Id);
            }
            if (!await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct))
            {
                await SigData.EventAsync(tx, r.Id, null, "callback_deferred", "provider", null, new { cb.EventId, cb.Event, reason = "module_disabled" }, ct);
                return await Done("deferred_disabled", "الوحدة معطلة: حُفظ الحدث دون تطبيق", r.Id);
            }
            if (r.Status == "on_hold")
            {
                await SigData.EventAsync(tx, r.Id, null, "callback_deferred", "provider", null, new { cb.EventId, cb.Event, reason = "on_hold" }, ct);
                return await Done("deferred_on_hold", "الطلب معلّق: حُفظ الحدث دون تطبيق", r.Id);
            }
            var applied = await ApplyVerifiedAsync(tx, r, provider, cb, ct);
            return await Done(applied.Outcome, applied.Detail, r.Id, applied.Completed ? r.Id : null);
        }, ct);
        if (completedId is { } done) await FinalizeAsync(done, ct);
        return outcome switch
        {
            "rejected_mismatch" => Results.Problem(statusCode: 409, title: "الاستدعاء لا يطابق الطلب", extensions: new Dictionary<string, object?> { ["code"] = "callback-mismatch" }),
            "rejected_unknown" => Results.Problem(statusCode: 404, title: "طلب غير معروف", extensions: new Dictionary<string, object?> { ["code"] = "callback-unknown-request" }),
            "deferred_disabled" or "deferred_on_hold" => Results.Json(new { outcome }, statusCode: 202),
            _ => Results.Ok(new { outcome }),
        };
    }

    private async Task<(string Outcome, string? Detail, bool Completed)> ApplyVerifiedAsync(ISql tx, SigRequest r, ISignatureProvider provider, ProviderCallback cb, CancellationToken ct)
    {
        if (!r.Live) return ("ignored", "الطلب لم يعد جاريًا", false);
        var signers = await SigData.SignersAsync(tx, r.Id, ct);
        switch (cb.Event)
        {
            case "signer_signed":
            {
                var s = signers.FirstOrDefault(x => x.ProviderSignerRef == cb.SignerRef);
                if (s is null) return ("rejected_mismatch", "موقّع غير معروف في الطلب", false);
                if (s.Status == "signed") return ("ignored", "سبق تسجيل التوقيع", false);
                var evidence = new JsonObject { ["type"] = r.Type, ["provider"] = provider.Code, ["providerSignerRef"] = cb.SignerRef, ["providerEventId"] = cb.EventId, ["occurredAt"] = cb.OccurredAt };
                await tx.ExecuteAsync("""
                    UPDATE sig.signers SET status = 'signed', signed_at = now(), acted_by_user_id = user_id, signed_document_sha256 = @sha, evidence = @e::jsonb, updated_at = now() WHERE id = @sid
                    """, new { sid = s.Id, sha = r.DocumentSha256, e = evidence.ToJsonString() }, ct);
                await SigData.EventAsync(tx, r.Id, s.Id, "signed", "provider", null, new { cb.EventId }, ct);
                await minutes.SignerCompletedAsync(tx, r.VersionId, s.UserId, r.Id, s.Id, r.Type, s.UserId, null, ct);
                await SignerFlow.ActivateAsync(tx, notifier, r.Id, r.MinutesId, r.Reference, ct);
                return ("applied", null, false);
            }
            case "signer_declined":
            {
                var s = signers.FirstOrDefault(x => x.ProviderSignerRef == cb.SignerRef);
                if (s is null) return ("rejected_mismatch", "موقّع غير معروف في الطلب", false);
                await DeclineCoreAsync(tx, r, s, cb.Reason ?? "رفض لدى المزوّد", null, "provider", ct);
                return ("applied", null, false);
            }
            case "completed":
            {
                if (signers.Any(x => x.Status != "signed")) return ("ignored", "لم يكتمل كل الموقّعين في سجلنا", false);
                ProviderSignedDocument signed;
                try { signed = await WithRetryAsync(() => provider.DownloadSignedAsync(r.ProviderRef!, ct), ct); }
                catch (SignatureProviderUnavailableException e)
                {
                    await SigData.EventAsync(tx, r.Id, null, "provider_unavailable", "system", null, new { operation = "download_signed", error = e.Message }, ct);
                    return ("ignored", "تعذر تنزيل النسخة النهائية؛ تُعاد المحاولة في الصيانة", false);
                }
                var original = await store.ReadAsync(r.DocumentId, ct);
                var preserved = original is not null && signed.Pdf.Length > original.Length && signed.Pdf.AsSpan(0, original.Length).SequenceEqual(original);
                var check = await verification.VerifyPdfAsync(signed.Pdf, ct);
                if (!preserved || check.Overall is "invalid" or "no_signatures")
                {
                    await SigData.SetStatusAsync(tx, r.Id, "failed", "النسخة النهائية من المزوّد لم تجتز التحقق (النسخة الأصلية غير محفوظة أو التوقيع غير صالح)", null, ct);
                    await SigData.EventAsync(tx, r.Id, null, "signed_copy_rejected", "system", null, new { preserved, overall = check.Overall }, ct);
                    return ("applied", "رُفضت النسخة النهائية", false);
                }
                var doc = await store.SaveAsync(tx, $"{r.Reference}-provider-signed.pdf", "application/pdf", signed.Pdf, "minutes", r.MinutesId, "signature_document",
                    "restricted", r.CreatedBy, ct);
                await tx.ExecuteAsync("""
                    UPDATE sig.requests SET status = 'completed', completed_at = now(), current_document_id = @doc, current_document_sha256 = @sha,
                      signed_document_id = @doc, signed_document_sha256 = @sha, updated_at = now() WHERE id = @id
                    """, new { id = r.Id, doc = doc.Id, sha = doc.Sha256 }, ct);
                await SigData.EventAsync(tx, r.Id, null, "completed", "provider", null, new { signedSha256 = doc.Sha256, overall = check.Overall,
                    providerVerification = Json.Sanitize(JsonNode.Parse(signed.VerificationJson)) }, ct);
                return ("applied", null, true);
            }
            case "expired":
                await SigData.SetStatusAsync(tx, r.Id, "expired", "انتهت صلاحية الطلب لدى المزوّد", null, ct);
                await tx.ExecuteAsync("UPDATE sig.signers SET status = 'cancelled', updated_at = now() WHERE request_id = @id AND status IN ('waiting','pending')", new { id = r.Id }, ct);
                await SigData.EventAsync(tx, r.Id, null, "expired", "provider", null, null, ct);
                return ("applied", null, false);
            default:
                return ("ignored", "حدث غير معروف", false);
        }
    }

    /// <summary>تطبيق الأحداث المؤجلة (وردت والوحدة معطلة أو الطلب معلّق) بعد الاستئناف.</summary>
    public async Task<int> ApplyDeferredAsync(Guid? requestId, CancellationToken ct)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct)) return 0;
        var pending = await db.QueryAsync("""
            SELECT c.id, c.provider, c.payload::text, c.request_id FROM sig.provider_callbacks c JOIN sig.requests r ON r.id = c.request_id
            WHERE c.outcome IN ('deferred_disabled','deferred_on_hold') AND r.status IN ('in_progress','queued') AND (@requestId::uuid IS NULL OR c.request_id = @requestId)
            ORDER BY c.received_at
            """, new { requestId }, r => (Id: r.GetGuid(0), Provider: r.GetString(1), Payload: r.GetString(2), Request: r.GetGuid(3)), ct);
        var applied = 0;
        foreach (var p in pending)
        {
            var provider = providers.Find(p.Provider);
            if (provider is null) continue;
            var j = JsonNode.Parse(p.Payload)!;
            var cb = new ProviderCallback(j["eventId"]!.GetValue<string>(), j["providerRef"]!.GetValue<string>(), Guid.Parse(j["correlationId"]!.GetValue<string>()),
                j["event"]!.GetValue<string>(), j["signerRef"]?.GetValue<string>(), j["documentSha256"]?.GetValue<string>(), j["reason"]?.GetValue<string>(),
                j["occurredAt"]?.GetValue<DateTime>() ?? DateTime.UtcNow);
            var completed = await db.InTransactionAsync(async tx =>
            {
                var r = (await SigData.RequestAsync(tx, p.Request, true, ct))!;
                var res = await ApplyVerifiedAsync(tx, r, provider, cb, ct);
                await tx.ExecuteAsync("UPDATE sig.provider_callbacks SET outcome = @o, detail = @d, applied_at = CASE WHEN @o = 'applied' THEN now() END WHERE id = @id",
                    new { id = p.Id, o = res.Outcome, d = "طُبّق بعد الاستئناف: " + (res.Detail ?? "") }, ct);
                await SigData.EventAsync(tx, r.Id, null, "callback_applied_deferred", "worker", null, new { cb.EventId, cb.Event, res.Outcome }, ct);
                return res.Completed;
            }, ct);
            applied++;
            if (completed) await FinalizeAsync(p.Request, ct);
        }
        return applied;
    }

    // ---------------- الصيانة الدورية ----------------

    public async Task<object> MaintenanceAsync(DateTime asOf, CancellationToken ct)
    {
        var enabled = await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct);
        var dispatched = 0;
        if (enabled)
            foreach (var id in await db.QueryAsync("SELECT id FROM sig.requests WHERE status = 'queued' AND (next_attempt_at IS NULL OR next_attempt_at <= @asOf) ORDER BY created_at",
                new { asOf }, r => r.GetGuid(0), ct))
            {
                await DispatchAsync(id, "worker", ct, asOf);
                dispatched++;
            }
        // انتهاء الصلاحية: تغيير صريح للحالة مع إشعار؛ لا يُنفذ والوحدة معطلة (الطلبات تبقى كما هي حتى إجراء صريح)
        var expired = 0;
        if (enabled)
            foreach (var r in await SigData.RequestsWhereAsync(db, "r.status IN ('queued','in_progress') AND r.expires_at < @asOf", new { asOf }, ct))
            {
                await db.InTransactionAsync(async tx =>
                {
                    await SigData.SetStatusAsync(tx, r.Id, "expired", "انتهت صلاحية طلب التوقيع قبل اكتماله", null, ct);
                    await tx.ExecuteAsync("UPDATE sig.signers SET status = 'cancelled', updated_at = now() WHERE request_id = @id AND status IN ('waiting','pending')", new { id = r.Id }, ct);
                    await tx.ExecuteAsync("UPDATE sig.requests SET pending_provider_cancel = (provider_ref IS NOT NULL AND signature_type = 'provider_esign') WHERE id = @id", new { id = r.Id }, ct);
                    await SigData.EventAsync(tx, r.Id, null, "expired", "worker", null, new { asOf }, ct);
                    foreach (var u in await SecretariesAsync(tx, r.BodyId, ct))
                        await notifier.NotifyAsync(tx, new NotificationRequest(u, "signature_expired", $"انتهت صلاحية طلب التوقيع ({r.Reference})", $"Signature request expired ({r.Reference})",
                            $"/minutes/{r.MinutesId}?tab=signatures", $"esign-expired:{r.Id}:{u}", "warning"), ct);
                }, ct);
                expired++;
            }
        var cancelled = 0;
        foreach (var r in await SigData.RequestsWhereAsync(db, "r.pending_provider_cancel AND r.provider_ref IS NOT NULL", new { }, ct))
        {
            try
            {
                await providers.Get(r.Provider).CancelAsync(r.ProviderRef!, r.StatusReason ?? "إغلاق الطلب", ct);
                await db.ExecuteAsync("UPDATE sig.requests SET pending_provider_cancel = false, updated_at = now() WHERE id = @id", new { id = r.Id }, ct);
                await SigData.EventAsync(db, r.Id, null, "provider_cancelled", "worker", null, null, ct);
                cancelled++;
            }
            catch (Exception e) when (e is SignatureProviderUnavailableException or SignatureProviderNotConfiguredException)
            {
                await SigData.EventAsync(db, r.Id, null, "provider_cancel_failed", "worker", null, new { error = e.Message }, ct);
            }
        }
        var deferred = await ApplyDeferredAsync(null, ct);
        return new { asOf, enabled, dispatched, expired, providerCancelled = cancelled, deferredApplied = deferred };
    }
}
