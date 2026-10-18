using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Ai;

/// <summary>التفريغ الصوتي (الوظيفة 4): تسجيل مصرح به فقط — موافقة موثقة من الحضور بمرجع — وخدمة تفريغ متاحة ومعتمدة.
/// المعالجة غير متزامنة؛ وعند التعطيل تُحجز المهام (held) بمدخلاتها ولا تُرسل، ولا تُستأنف إلا بإجراء صريح.</summary>
internal static class Transcription
{
    public sealed record ReasonRequest([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(3), MaxLength(1000)] string Reason);

    private static readonly string[] AudioTypes = ["audio/wav", "audio/x-wav", "audio/wave", "audio/mpeg", "audio/mp3", "audio/mp4", "audio/x-m4a", "audio/m4a", "audio/ogg", "audio/webm", "audio/aac"];

    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/meetings/{id:guid}/transcriptions", Upload).DisableAntiforgery();
        g.MapGet("/meetings/{id:guid}/transcriptions", List);
        g.MapPost("/jobs/{id:guid}/resume", Resume);
        g.MapPost("/jobs/{id:guid}/cancel", Cancel).Validate<ReasonRequest>();
    }

    private static async Task<IResult> Upload(Guid id, HttpRequest request, AiService svc, Db db, IAccessService access, IDocumentStore docs, ICurrentUser me,
        IAuditWriter audit, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.Transcription, "meeting", id, ct);
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        var m = await db.QuerySingleAsync("""
            SELECT m.body_id, m.classification, b.classification FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id WHERE m.id = @id
            """, new { id }, r => (Body: r.GetGuid(0), Cls: r.GetString(1), BodyCls: r.GetString(2)), ct);
        if (!await access.HasAsync(Permissions.AiUse, Scope.Body(m.Body), ct)) throw new ForbiddenException(permission: Permissions.AiUse);
        var (_, s) = await svc.DemandProviderAsync(AiFunctions.Transcription, true, "meeting", id, ct);

        if (!request.HasFormContentType) throw AppValidationException.For("file", "أرفق التسجيل الصوتي");
        var form = await request.ReadFormAsync(ct);
        // التسجيل المصرح به فقط: موافقة الحضور موثقة بمرجع
        if (form["consentConfirmed"] != "true") throw AppValidationException.For("consentConfirmed", "يلزم تأكيد موافقة الحضور الموثقة على التسجيل والتفريغ");
        var reference = form["consentReference"].ToString().Trim();
        if (reference.Length < 3) throw AppValidationException.For("consentReference", "اذكر مرجع توثيق الموافقة (محضر أو بند أو إقرار)");
        var file = form.Files["file"] ?? throw AppValidationException.For("file", "أرفق التسجيل الصوتي");
        if (file.Length is 0 or > 50 * 1024 * 1024) throw AppValidationException.For("file", "حجم التسجيل غير مقبول (حتى 50 م.ب)");
        var type = (file.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (!AudioTypes.Contains(type)) throw AppValidationException.For("file", "نوع الملف ليس تسجيلًا صوتيًا مدعومًا");

        var cls = AiData.Max(m.Cls, m.BodyCls);
        if (DataPolicy.Rank(cls) > DataPolicy.AllowedRank(s))
        {
            await svc.LogAsync(new AiService.LogRow(AiFunctions.Transcription, "blocked_classification") { SubjectType = "meeting", SubjectId = id, BodyId = m.Body, ErrorCode = "classification_" + cls, Provider = s.Provider }, ct);
            throw new AiClassificationBlockedException(cls, DataPolicy.AllowedLabel(s), 1);
        }
        await svc.DemandQuotaAsync(AiFunctions.Transcription, s, "meeting", id, ct);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        // المحتوى يطابق توقيع ملف صوتي بامتداده (لا يُقبل ملف آخر بنوع محتوى مزوّر)
        if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".wav" or ".mp3" or ".m4a")
            || !FileSafety.MatchesSignature(file.FileName, ms.GetBuffer().AsSpan(0, (int)Math.Min(16, ms.Length))))
            throw AppValidationException.For("file", "الملف ليس تسجيلًا صوتيًا صالحًا (wav أو mp3 أو m4a)");
        type = FileSafety.ContentTypeFor(file.FileName);
        var jobId = Guid.CreateVersion7();
        var consent = new JsonObject
        {
            ["confirmed"] = true, ["reference"] = reference, ["note"] = form["consentNote"].ToString(), ["confirmedBy"] = me.UserId.ToString(),
            ["confirmedByName"] = me.DisplayName, ["confirmedAt"] = DateTime.UtcNow.ToString("O"),
        };
        await db.InTransactionAsync(async tx =>
        {
            var doc = await docs.SaveAsync(tx, FileSafety.SafeName(file.FileName), type, ms.ToArray(), "meeting", id, "ai_recording", cls, me.UserId, ct);
            await tx.ExecuteAsync("""
                INSERT INTO ai.jobs (id, kind, meeting_id, body_id, document_id, status, consent, requested_by) VALUES (@jobId, 'transcription', @id, @body, @doc, 'queued', @consent::jsonb, @me)
                """, new { jobId, id, body = m.Body, doc = doc.Id, consent = consent.ToJsonString(), me = me.UserId }, ct);
            await Event(tx, jobId, "queued", me.UserId, new { reference }, ct);
            await audit.WriteAsync(tx, new AuditEntry("AI", "ai.transcription_queued", "meeting", id.ToString(), After: new { jobId, consentReference = reference, document = doc.Id }), ct);
        }, ct);
        return Results.Ok(new { jobId, status = "queued" });
    }

    private static Task Event(ISql sql, Guid jobId, string kind, Guid? actor, object data, CancellationToken ct) =>
        sql.ExecuteAsync("INSERT INTO ai.job_events (id, job_id, kind, actor_user_id, data) VALUES (@id, @jobId, @kind, @actor, @data::jsonb)",
            new { id = Guid.CreateVersion7(), jobId, kind, actor, data = Json.Serialize(data) }, ct);

    private static async Task<IResult> List(Guid id, Db db, IAccessService access, IFeatureService features, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var perms = await access.GetPermissionsAsync(Scope.Record("meeting", id), ct);
        var enabled = await features.IsEnabledAsync(FeatureKeys.AiAudioTranscription, ct);
        var manage = perms.Contains(Permissions.MeetingManage);
        var jobs = await db.QueryAsync("""
            SELECT j.id, j.status, j.attempts, j.last_error, j.held_reason, j.output_id, j.consent::text, j.created_at, j.updated_at, d.file_name, j.document_id, u.display_name_ar, j.close_reason
            FROM ai.jobs j JOIN docs.documents d ON d.id = j.document_id JOIN iam.users u ON u.id = j.requested_by WHERE j.meeting_id = @id ORDER BY j.created_at DESC
            """, new { id }, r => new
        {
            id = r.GetGuid(0), status = r.GetString(1), attempts = r.GetInt32(2), lastError = r.Str("last_error"), heldReason = r.Str("held_reason"), outputId = r.Uuid("output_id"),
            consent = JsonNode.Parse(r.GetString(6)), createdAt = r.GetDateTime(7), updatedAt = r.GetDateTime(8), fileName = r.GetString(9), documentId = r.GetGuid(10),
            requestedBy = r.GetString(11), closeReason = r.Str("close_reason"),
            can = new { resume = manage && enabled && r.GetString(1) is "held" or "failed", cancel = manage && r.GetString(1) is "queued" or "held" or "failed" },
        }, ct);
        return Results.Ok(new { enabled, canUpload = enabled && manage, jobs });
    }

    private static async Task<(Guid Meeting, string Status)> JobAsync(Db db, Guid id, IAccessService access, CancellationToken ct)
    {
        var j = await db.QuerySingleAsync("SELECT meeting_id, status FROM ai.jobs WHERE id = @id", new { id }, r => (r.GetGuid(0), r.GetString(1)), ct);
        if (j == default) throw new NotFoundException();
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", j.Item1), ct);
        return j;
    }

    private static async Task<IResult> Resume(Guid id, Db db, IAccessService access, AiService svc, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var (meeting, status) = await JobAsync(db, id, access, ct);
        await svc.DemandFunctionAsync(AiFunctions.Transcription, "meeting", meeting, ct);
        if (status is not ("held" or "failed")) throw new ConflictException("invalid-state", "لا يُستأنف إلا المحجوز أو الفاشل");
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("UPDATE ai.jobs SET status = 'queued', held_reason = NULL, attempts = 0, updated_at = now() WHERE id = @id", new { id }, ct);
            await Event(tx, id, "resumed", me.UserId, new { from = status }, ct);
            await audit.WriteAsync(tx, new AuditEntry("AI", "ai.transcription_resumed", "meeting", meeting.ToString(), After: new { jobId = id }), ct);
        }, ct);
        return Results.Ok(new { id, status = "queued" });
    }

    private static async Task<IResult> Cancel(Guid id, ReasonRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var (meeting, status) = await JobAsync(db, id, access, ct);
        if (status is not ("queued" or "held" or "failed")) throw new ConflictException("invalid-state", "لا يُلغى إلا المنتظر أو المحجوز أو الفاشل");
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("UPDATE ai.jobs SET status = 'cancelled', close_reason = @reason, closed_by = @me, updated_at = now() WHERE id = @id",
                new { id, reason = req.Reason, me = me.UserId }, ct);
            await Event(tx, id, "cancelled", me.UserId, new { req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("AI", "ai.transcription_cancelled", "meeting", meeting.ToString(), After: new { jobId = id, req.Reason }), ct);
        }, ct);
        return Results.Ok(new { id, status = "cancelled" });
    }

    /// <summary>معالجة المهام المنتظرة: إن عُطلت الوظيفة تُحجز (لا تُرسل ولا تُحذف)؛ وإلا تُفرّغ وتُحفظ مسودة نص مرتبطة بالاجتماع.</summary>
    public static async Task<object> ProcessAsync(Db db, IFeatureService features, AiProviderRegistry providers, IDocumentStore docs, AiService svc, CancellationToken ct)
    {
        var enabled = await features.IsEnabledAsync(FeatureKeys.AiAudioTranscription, ct);
        int held = 0, done = 0, failed = 0, retried = 0;
        var queued = await db.QueryAsync("SELECT id, meeting_id, body_id, document_id, requested_by, attempts FROM ai.jobs WHERE status = 'queued' ORDER BY created_at LIMIT 10", null,
            r => (Id: r.GetGuid(0), Meeting: r.GetGuid(1), Body: r.GetGuid(2), Doc: r.GetGuid(3), By: r.GetGuid(4), Attempts: r.GetInt32(5)), ct);
        if (!enabled)
        {
            foreach (var j in queued)
            {
                await db.InTransactionAsync(async tx =>
                {
                    var n = await tx.ExecuteAsync("UPDATE ai.jobs SET status = 'held', held_reason = 'ai_disabled', updated_at = now() WHERE id = @id AND status = 'queued'", new { id = j.Id }, ct);
                    if (n > 0) await Event(tx, j.Id, "held", null, new { reason = "ai_disabled" }, ct);
                }, ct);
                held++;
            }
            return new { held, done, failed, retried };
        }
        var s = await svc.SettingsAsync(ct);
        var (provider, ready) = await providers.ResolveAsync(s, true, ct);
        foreach (var j in queued)
        {
            var claimed = await db.ExecuteAsync("UPDATE ai.jobs SET status = 'running', attempts = attempts + 1, updated_at = now() WHERE id = @id AND status = 'queued'", new { id = j.Id }, ct);
            if (claimed == 0) continue;
            var sw = Stopwatch.StartNew();
            try
            {
                if (provider is null || !ready.Ready) throw new AiProviderException("unavailable", OpenAiCompatibleProvider.Describe(ready.Reason));
                var file = await db.QuerySingleAsync("SELECT file_name, content_type FROM docs.documents WHERE id = @id", new { id = j.Doc }, r => (r.GetString(0), r.GetString(1)), ct);
                var audio = await docs.ReadAsync(j.Doc, ct) ?? throw new AiProviderException("error", "ملف التسجيل غير موجود");
                var t = await provider.TranscribeAsync(s, audio, file.Item1, file.Item2, ct);
                var stillOn = await features.IsEnabledAsync(FeatureKeys.AiAudioTranscription, ct);
                var outputId = Guid.CreateVersion7();
                await db.InTransactionAsync(async tx =>
                {
                    await tx.ExecuteAsync("""
                        INSERT INTO ai.outputs (id, function, subject_type, subject_id, body_id, requested_by, status, content, citations, warnings, provider, model, test_only,
                                                prompt_version, input_sha256, classification, source_count, held_reason)
                        SELECT @oid, 'transcription', 'meeting', @meeting, @body, @by, @status, @content::jsonb, @cit::jsonb, @warn::jsonb, @provider, @model, @test, 'transcription',
                               d.sha256, d.classification, 1, @held
                        FROM docs.documents d WHERE d.id = @doc
                        """, new
                    {
                        oid = outputId, meeting = j.Meeting, body = j.Body, by = j.By, status = stillOn ? "draft" : "held",
                        content = new JsonObject { ["text"] = t.Text, ["language"] = "ar", ["documentId"] = j.Doc.ToString() }.ToJsonString(),
                        cit = Json.Serialize(new[] { new Citation("D1", "document", j.Doc, file.Item1, $"/api/v1/documents/{j.Doc}/content") }),
                        warn = Json.Serialize(new[] { "نص مفرّغ آليًا: راجع دقته قبل استخدامه في المحضر" }), provider = provider.Code, model = t.Model, test = provider.TestOnly,
                        doc = j.Doc, held = stillOn ? null : "disabled_during_processing",
                    }, ct);
                    await tx.ExecuteAsync("UPDATE ai.jobs SET status = 'completed', output_id = @oid, last_error = NULL, updated_at = now() WHERE id = @id", new { id = j.Id, oid = outputId }, ct);
                    await Event(tx, j.Id, "completed", null, new { outputId }, ct);
                }, ct);
                await svc.LogAsync(new AiService.LogRow(AiFunctions.Transcription, stillOn ? "succeeded" : "held")
                {
                    UserId = j.By, SubjectType = "meeting", SubjectId = j.Meeting, BodyId = j.Body, DurationMs = (int)sw.ElapsedMilliseconds, InputTokens = t.InputTokens,
                    Estimated = t.UsageEstimated, Provider = provider.Code, Model = t.Model, OutputId = outputId, JobId = j.Id, Counted = true, SourceCount = 1,
                }, ct);
                done++;
            }
            catch (AiProviderException e)
            {
                var final = !e.Transient || j.Attempts + 1 >= 3;
                await db.InTransactionAsync(async tx =>
                {
                    await tx.ExecuteAsync("UPDATE ai.jobs SET status = @status, last_error = @err, updated_at = now() WHERE id = @id",
                        new { id = j.Id, status = final ? "failed" : "queued", err = e.Message }, ct);
                    await Event(tx, j.Id, final ? "failed" : "retry", null, new { kind = e.Kind }, ct);
                }, ct);
                await svc.LogAsync(new AiService.LogRow(AiFunctions.Transcription, e.Kind == "timeout" ? "timeout" : e.Transient ? "provider_unavailable" : "failed")
                {
                    UserId = j.By, SubjectType = "meeting", SubjectId = j.Meeting, BodyId = j.Body, ErrorCode = e.Kind, DurationMs = (int)sw.ElapsedMilliseconds,
                    Provider = provider?.Code, JobId = j.Id, Counted = provider is not null && ready.Ready,
                }, ct);
                if (final) failed++; else retried++;
            }
        }
        return new { held, done, failed, retried };
    }
}
