using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Pdf;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Documents;

/// <summary>إدارة السجلات (23 §3): إصدارات المستندات، والتصنيف السجلي، والأرشفة، والحجز القانوني، وقواعد الاحتفاظ، والإتلاف بطلب ومعتمد مختلف،
/// والإيداع في مستودع الوثائق المؤسسي. محتوى أي مستند مسجل ثابت (قاعدة البيانات تمنع تعديله)، والاستبدال بإصدار جديد فقط.</summary>
internal static class Records
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var d = api.MapGroup("/documents").WithTags("Documents");
        d.MapGet("/{id:guid}", Get);
        d.MapGet("/{id:guid}/versions", Versions);
        d.MapPost("/{id:guid}/versions", NewVersion).DisableAntiforgery();
        d.MapPost("/{id:guid}/archive", Archive).Validate<ReasonRequest>();
        d.MapPost("/{id:guid}/legal-hold", LegalHold).Validate<HoldRequest>();
        d.MapPost("/{id:guid}/repository-export", RepositoryExport);

        var r = api.MapGroup("/records").WithTags("Records");
        r.MapGet("/register", Register);
        r.MapGet("/retention-rules", Rules);
        r.MapPost("/archive-due", ArchiveDue);
        r.MapPut("/retention-rules/{code}", UpdateRule).Validate<RuleRequest>();
        r.MapGet("/destruction-requests", ListDestruction);
        r.MapPost("/destruction-requests", RequestDestruction).Validate<DestructionRequest>();
        r.MapPost("/destruction-requests/{id:guid}/decide", Decide).Validate<DecideRequest>();
        r.MapPost("/destruction-requests/{id:guid}/execute", Execute);
        r.MapGet("/destruction-requests/{id:guid}/certificate", Certificate);
    }

    public sealed record ReasonRequest([property: MaxLength(1000)] string? Reason);
    public sealed record HoldRequest(bool Hold, [property: MaxLength(1000)] string? Reason);
    public sealed record RuleRequest([property: Required, MaxLength(200)] string? NameAr, [property: Required, MaxLength(200)] string? NameEn,
        [property: Range(0, 100)] int RetentionYears, [property: Range(0, 100)] int ArchiveAfterYears, [property: Required] string? FinalAction,
        [property: MaxLength(1000)] string? LegalBasis, bool Active = true);
    public sealed record DestructionRequest([property: Required, MinLength(1), MaxLength(500)] Guid[]? DocumentIds, [property: Required, MinLength(10), MaxLength(2000)] string? Reason);
    public sealed record DecideRequest(bool Approve, [property: MaxLength(2000)] string? Note);

    private static string Root(IOptions<StorageOptions> o, IWebHostEnvironment env) =>
        Path.IsPathRooted(o.Value.Path) ? o.Value.Path : Path.Combine(env.ContentRootPath, o.Value.Path);

    private sealed record Doc(Guid Id, Guid SeriesId, int VersionNo, string FileName, string ContentType, long Size, string Sha, string Key, string Classification,
        bool IsCurrent, bool Locked, string? RecordsClass, string Lifecycle, DateOnly? RetentionUntil, bool LegalHold, string? LegalHoldReason, DateTime UploadedAt,
        string UploadedBy, string? Note, DateTime? ArchivedAt, DateTime? DestroyedAt);

    private const string DocSelect = """
        SELECT d.id, d.series_id, d.version_no, d.file_name, d.content_type, d.size_bytes, d.sha256, d.storage_key, d.classification, d.is_current, d.locked,
               d.records_class, d.lifecycle_status, d.retention_until, d.legal_hold, d.legal_hold_reason, d.uploaded_at, u.display_name_ar, d.version_note,
               d.archived_at, d.destroyed_at
        FROM docs.documents d JOIN iam.users u ON u.id = d.uploaded_by
        """;

    private static Doc ReadDoc(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
        r.GetString(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), r.GetBoolean(10), r.Str("records_class"), r.GetString(12), r.Date("retention_until"),
        r.GetBoolean(14), r.Str("legal_hold_reason"), r.GetDateTime(16), r.GetString(17), r.Str("version_note"), r.IsDBNull(19) ? null : r.GetDateTime(19),
        r.IsDBNull(20) ? null : r.GetDateTime(20));

    private static object Shape(Doc d) => new
    {
        id = d.Id, seriesId = d.SeriesId, versionNo = d.VersionNo, fileName = d.FileName, contentType = d.ContentType, sizeBytes = d.Size, sha256 = d.Sha,
        classification = d.Classification, isCurrent = d.IsCurrent, locked = d.Locked, recordsClass = d.RecordsClass, lifecycleStatus = d.Lifecycle,
        retentionUntil = d.RetentionUntil, legalHold = d.LegalHold, legalHoldReason = d.LegalHoldReason, uploadedAt = d.UploadedAt, uploadedBy = d.UploadedBy,
        versionNote = d.Note, archivedAt = d.ArchivedAt, destroyedAt = d.DestroyedAt,
    };

    private static async Task<Doc> Load(Db db, Guid id, CancellationToken ct) =>
        await db.QuerySingleAsync(DocSelect + " WHERE d.id = @id", new { id }, ReadDoc, ct) ?? throw new NotFoundException("المستند غير موجود");

    /// <summary>البيانات الوصفية: لمن يرى المستند، أو لمسؤول السجلات ومعتمد الإتلاف (الوصف دون المحتوى؛ التنزيل يبقى بصلاحية المستند).</summary>
    private static async Task DemandMetadataAsync(IAccessService access, Guid id, CancellationToken ct)
    {
        if (await access.HasAsync(Permissions.RecordsManage, Scope.System, ct) || await access.HasAsync(Permissions.RecordsDestructionApprove, Scope.System, ct)) return;
        await access.DemandAsync(Permissions.DocumentView, Scope.Document(id), ct);
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await DemandMetadataAsync(access, id, ct);
        var d = await Load(db, id, ct);
        var links = await db.QueryAsync("SELECT owner_type, owner_id, purpose FROM docs.document_links WHERE document_id = @id", new { id },
            r => new { ownerType = r.GetString(0), ownerId = r.GetGuid(1), purpose = r.GetString(2) }, ct);
        var exports = await db.QueryAsync("SELECT connector, status, remote_url, error, exported_at FROM docs.repository_exports WHERE document_id = @id ORDER BY exported_at DESC LIMIT 10",
            new { id }, r => new { connector = r.GetString(0), status = r.GetString(1), remoteUrl = r.Str("remote_url"), error = r.Str("error"), exportedAt = r.GetDateTime(4) }, ct);
        var perms = await access.GetPermissionsAsync(Scope.System, ct);
        return Results.Ok(new
        {
            document = Shape(d), links, repositoryExports = exports,
            permissions = new[] { Permissions.RecordsManage, Permissions.RecordsDestructionApprove }.Where(perms.Contains)
                .Concat(await access.HasAsync(Permissions.DocumentView, Scope.Document(id), ct) ? [Permissions.DocumentView] : []),
        });
    }

    private static async Task<IResult> Versions(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await DemandMetadataAsync(access, id, ct);
        var series = await db.ScalarAsync<Guid>("SELECT series_id FROM docs.documents WHERE id = @id", new { id }, ct);
        return Results.Ok(await db.QueryAsync(DocSelect + " WHERE d.series_id = @series ORDER BY d.version_no DESC", new { series }, r => Shape(ReadDoc(r)), ct));
    }

    /// <summary>تسجيل إصدار جديد: سجل مستند جديد بالسلسلة نفسها ورقم إصدار تالٍ، ويُعلَّم السابق «مُستبدَلًا» مع بقائه محفوظًا وقابلًا للتنزيل.
    /// المستند الموقّع أو المعتمد (مقفل) لا يُستبدل إلا بصلاحية إدارة السجلات وسبب مكتوب.</summary>
    private static async Task<IResult> NewVersion(Guid id, HttpRequest request, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        IOptions<StorageOptions> options, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!request.HasFormContentType) throw AppValidationException.For("file", "أرسل الملف بصيغة multipart/form-data");
        var prev = await Load(db, id, ct);
        var owners = await db.QueryAsync("SELECT owner_type, owner_id, purpose, external_visible FROM docs.document_links WHERE document_id = @id", new { id },
            r => (Type: r.GetString(0), Id: r.GetGuid(1), Purpose: r.GetString(2), External: r.GetBoolean(3)), ct);
        if (owners.Count == 0) throw new NotFoundException();
        var o = owners[0];
        // المقفل (موقّع أو معتمد): صلاحية إدارة السجلات وحدها؛ غيره: صلاحية الرفع على السجل المالك
        if (prev.Locked) await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        else
        {
            await access.DemandAsync(o.Type == "task" ? Permissions.TaskUpdate : Permissions.DocumentUpload,
                o.Type switch { "body" => Scope.Body(o.Id), "task" => Scope.Task(o.Id), _ => Scope.Record(o.Type, o.Id) }, ct);
            if (o.Type == "task" && await access.IsPortalOnlyAsync(o.Id, ct)) throw new ForbiddenException();
        }
        if (!prev.IsCurrent) throw new ConflictException("not-current-version", "سجّل الإصدار الجديد على الإصدار الحالي للمستند");
        if (prev.Lifecycle is "destroyed" or "destruction_pending") throw new ConflictException("document-not-active", "المستند متلف أو قيد الإتلاف");

        var form = await request.ReadFormAsync(ct);
        var note = form["note"].ToString().Trim();
        if (prev.Locked && note.Length < 10) throw AppValidationException.For("note", "المستند موقّع أو معتمد: سبب الإصدار الجديد مطلوب (10 أحرف على الأقل)");
        if (note.Length > 1000) throw AppValidationException.For("note", "السبب أطول من 1000 حرف");
        var file = form.Files.GetFile("file") ?? throw AppValidationException.For("file", "الملف مطلوب");
        var opt = options.Value;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!opt.AllowedExtensions.Contains(ext)) throw AppValidationException.For("file", $"نوع الملف غير مسموح؛ المسموح: {string.Join(" ", opt.AllowedExtensions)}");
        if (file.Length == 0 || file.Length > opt.MaxSizeMb * 1024 * 1024) throw AppValidationException.For("file", $"حجم الملف يجب ألا يتجاوز {opt.MaxSizeMb} م.ب");

        var newId = Guid.CreateVersion7();
        var key = $"{DateTime.UtcNow:yyyy/MM}/{newId:N}";
        var full = Path.Combine(Root(options, env), key);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var target = File.Create(full)) await file.CopyToAsync(target, ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(full, ct)));
        if (sha == prev.Sha) { File.Delete(full); throw new ConflictException("same-content", "الملف مطابق للإصدار الحالي؛ لا حاجة لإصدار جديد"); }
        int versionNo = 0;
        try
        {
            await db.InTransactionAsync(async tx =>
            {
                // قفل السلسلة يمنع إصدارين متزامنين بالرقم نفسه
                var cur = await tx.QuerySingleAsync("SELECT id, version_no FROM docs.documents WHERE series_id = @s AND is_current FOR UPDATE", new { s = prev.SeriesId },
                    r => (Id: r.GetGuid(0), No: r.GetInt32(1)), ct);
                if (cur.Id != prev.Id) throw new ConflictException("not-current-version", "سُجّل إصدار أحدث للتو؛ حدّث الصفحة");
                versionNo = await tx.ScalarAsync<int>("SELECT max(version_no) + 1 FROM docs.documents WHERE series_id = @s", new { s = prev.SeriesId }, ct);
                await tx.ExecuteAsync("UPDATE docs.documents SET is_current = false, superseded_at = now() WHERE id = @id", new { id = prev.Id }, ct);
                await tx.ExecuteAsync("""
                    INSERT INTO docs.documents (id, series_id, version_no, previous_version_id, file_name, content_type, size_bytes, sha256, storage_key, classification,
                                                uploaded_by, version_note, records_class, retention_until)
                    VALUES (@newId, @series, @versionNo, @prevId, @name, @type, @size, @sha, @key, @cls, @by, @note, @rc, @ru)
                    """, new
                {
                    newId, series = prev.SeriesId, versionNo, prevId = prev.Id, name = Path.GetFileName(file.FileName), type = file.ContentType, size = file.Length, sha, key,
                    cls = prev.Classification, by = me.UserId, note = note.Length == 0 ? null : note, rc = prev.RecordsClass, ru = prev.RetentionUntil,
                }, ct);
                foreach (var l in owners)
                    await tx.ExecuteAsync("INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose, external_visible) VALUES (@lid, @newId, @t, @oid, @p, @x)",
                        new { lid = Guid.CreateVersion7(), newId, t = l.Type, oid = l.Id, p = l.Purpose, x = l.External }, ct);
                await audit.WriteAsync(tx, new AuditEntry("DOC", "document.version_registered", "document", newId.ToString(),
                    Before: new { documentId = prev.Id, prev.VersionNo, sha256 = prev.Sha, prev.Locked },
                    After: new { seriesId = prev.SeriesId, versionNo, sha256 = sha, size = file.Length, note }, Classification: prev.Classification), ct);
            }, ct);
        }
        catch
        {
            File.Delete(full);
            throw;
        }
        return Results.Created($"/api/v1/documents/{newId}", new { id = newId, versionNo, sha256 = sha, previousVersionId = prev.Id });
    }

    private static async Task<IResult> Archive(Guid id, ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var d = await Load(db, id, ct);
        if (d.Lifecycle != "active") throw new ConflictException("document-not-active", "المستند ليس نشطًا");
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("UPDATE docs.documents SET lifecycle_status = 'archived', archived_at = now() WHERE series_id = @s AND lifecycle_status = 'active'", new { s = d.SeriesId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", "document.archived", "document", id.ToString(), After: new { seriesId = d.SeriesId, req.Reason }, Classification: d.Classification), ct);
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LegalHold(Guid id, HoldRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var d = await Load(db, id, ct);
        if (d.Lifecycle == "destroyed") throw new ConflictException("document-destroyed", "المستند متلف");
        if (req.Hold && string.IsNullOrWhiteSpace(req.Reason)) throw AppValidationException.For("reason", "سبب الحجز القانوني مطلوب");
        await db.InTransactionAsync(async tx =>
        {
            // الحجز يشمل كل إصدارات السلسلة
            await tx.ExecuteAsync("UPDATE docs.documents SET legal_hold = @h, legal_hold_reason = CASE WHEN @h THEN @r ELSE NULL END WHERE series_id = @s AND lifecycle_status <> 'destroyed'",
                new { h = req.Hold, r = req.Reason?.Trim(), s = d.SeriesId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", req.Hold ? "document.legal_hold_placed" : "document.legal_hold_released", "document", id.ToString(),
                After: new { seriesId = d.SeriesId, req.Reason }, Classification: d.Classification), ct);
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RepositoryExport(Guid id, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IDocumentStore store,
        IServiceProvider sp, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var d = await Load(db, id, ct);
        if (d.Lifecycle == "destroyed") throw new ConflictException("document-destroyed", "المستند متلف");
        var gateway = sp.GetService<IDocumentRepositoryGateway>() ?? throw new ConflictException("integration-unavailable", "تكامل مستودع الوثائق غير مثبت");
        var content = await store.ReadAsync(id, ct) ?? throw new ConflictException("file-missing", "ملف المستند غير موجود في التخزين");
        var folder = $"{d.UploadedAt:yyyy}/{d.RecordsClass ?? "unclassified"}";
        try
        {
            var item = await gateway.UploadAsync(folder, $"{Path.GetFileNameWithoutExtension(d.FileName)}-v{d.VersionNo}{Path.GetExtension(d.FileName)}", d.ContentType, content, ct);
            await db.ExecuteAsync("""
                INSERT INTO docs.repository_exports (id, document_id, connector, status, remote_id, remote_url, exported_by) VALUES (@x, @id, 'document_repository', 'succeeded', @rid, @url, @by)
                """, new { x = Guid.CreateVersion7(), id, rid = item.RemoteId, url = item.WebUrl, by = me.UserId }, ct);
            await audit.WriteAsync(new AuditEntry("DOC", "document.repository_exported", "document", id.ToString(), After: new { item.RemoteId, folder }, Classification: d.Classification), ct);
            return Results.Ok(new { status = "succeeded", remoteId = item.RemoteId, webUrl = item.WebUrl });
        }
        catch (IntegrationFailedException ex)
        {
            await db.ExecuteAsync("""
                INSERT INTO docs.repository_exports (id, document_id, connector, status, error, exported_by) VALUES (@x, @id, 'document_repository', 'failed', @err, @by)
                """, new { x = Guid.CreateVersion7(), id, err = $"{ex.Reason}: {ex.Message}", by = me.UserId }, ct);
            throw new ConflictException("integration-failed", ex.Message, new Dictionary<string, object?> { ["connector"] = ex.Connector, ["reason"] = ex.Reason });
        }
    }

    // ---------------- السجل وقواعد الاحتفاظ ----------------

    private static async Task DemandRecordsAsync(IAccessService access, CancellationToken ct)
    {
        if (!await access.HasAsync(Permissions.RecordsManage, Scope.System, ct) && !await access.HasAsync(Permissions.RecordsDestructionApprove, Scope.System, ct))
            throw new ForbiddenException(permission: Permissions.RecordsManage);
    }

    /// <summary>سجل المستندات لمسؤول السجلات: due=true يعرض ما انقضت مدة احتفاظه وقابل للإتلاف (ليس دائمًا ولا محجوزًا ولا مقفلًا بإجراء دائم).</summary>
    private static async Task<IResult> Register(string? status, string? recordsClass, bool? due, string? q, int? page, Db db, IAccessService access, IClock clock, CancellationToken ct)
    {
        await DemandRecordsAsync(access, ct);
        var p = Math.Max(1, page ?? 1);
        var args = new { status, rc = recordsClass, due = due ?? false, q = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%", today = clock.TodayLocal, off = (p - 1) * 50 };
        const string where = """
            WHERE d.is_current AND (@status::text IS NULL OR d.lifecycle_status = @status) AND (@rc::text IS NULL OR d.records_class = @rc)
              AND (@q::text IS NULL OR d.file_name ILIKE @q)
              AND (NOT @due::boolean OR (d.retention_until <= @today AND NOT d.legal_hold AND d.lifecycle_status IN ('active','archived')
                   AND coalesce(r.final_action, 'review') <> 'permanent'))
            """;
        var total = await db.ScalarAsync<long>($"SELECT count(*) FROM docs.documents d LEFT JOIN docs.retention_rules r ON r.code = d.records_class {where}", args, ct);
        var items = await db.QueryAsync($"""
            SELECT d.id, d.file_name, d.version_no, d.records_class, coalesce(r.name_ar, ''), coalesce(r.final_action, ''), d.lifecycle_status, d.retention_until,
                   d.legal_hold, d.locked, d.classification, d.uploaded_at, d.size_bytes,
                   (SELECT string_agg(DISTINCT l.owner_type || ':' || l.purpose, ', ') FROM docs.document_links l WHERE l.document_id = d.id)
            FROM docs.documents d LEFT JOIN docs.retention_rules r ON r.code = d.records_class {where}
            ORDER BY d.retention_until NULLS LAST, d.uploaded_at DESC LIMIT 50 OFFSET @off
            """, args, r => new
        {
            id = r.GetGuid(0), fileName = r.GetString(1), versionNo = r.GetInt32(2), recordsClass = r.Str("records_class"), recordsClassName = r.GetString(4),
            finalAction = r.GetString(5), lifecycleStatus = r.GetString(6), retentionUntil = r.Date("retention_until"), legalHold = r.GetBoolean(8), locked = r.GetBoolean(9),
            classification = r.GetString(10), uploadedAt = r.GetDateTime(11), sizeBytes = r.GetInt64(12), usage = r.IsDBNull(13) ? "" : r.GetString(13),
        }, ct);
        return Results.Ok(new { total, page = p, items });
    }

    private static async Task<IResult> Rules(Db db, IAccessService access, CancellationToken ct)
    {
        await DemandRecordsAsync(access, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT r.code, r.name_ar, r.name_en, r.purposes, r.retention_years, r.archive_after_years, r.final_action, r.legal_basis, r.active, r.version, r.updated_at,
                   (SELECT count(*) FROM docs.documents d WHERE d.records_class = r.code AND d.is_current AND d.lifecycle_status <> 'destroyed')
            FROM docs.retention_rules r ORDER BY r.retention_years = 0 DESC, r.code
            """, null, r => new
        {
            code = r.GetString(0), nameAr = r.GetString(1), nameEn = r.GetString(2), purposes = r.GetFieldValue<string[]>(3), retentionYears = r.GetInt32(4),
            archiveAfterYears = r.GetInt32(5), finalAction = r.GetString(6), legalBasis = r.Str("legal_basis"), active = r.GetBoolean(8), version = r.GetInt32(9),
            updatedAt = r.GetDateTime(10), documents = r.GetInt64(11),
        }, ct));
    }

    /// <summary>تعديل قاعدة احتفاظ بإصدار جديد للقاعدة. يُعاد حساب «تاريخ انتهاء الاحتفاظ» للمستندات غير المحجوزة وغير المتلفة في الصنف.</summary>
    private static async Task<IResult> UpdateRule(string code, RuleRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        if (req.FinalAction is not ("permanent" or "destroy" or "review")) throw AppValidationException.For("finalAction", "الإجراء النهائي: permanent أو destroy أو review");
        if (req.FinalAction != "permanent" && req.RetentionYears == 0) throw AppValidationException.For("retentionYears", "مدة الاحتفاظ صفر لا تصح إلا مع الحفظ الدائم");
        await db.InTransactionAsync(async tx =>
        {
            var before = await tx.QuerySingleAsync("SELECT retention_years, final_action, active, version FROM docs.retention_rules WHERE code = @code FOR UPDATE", new { code },
                r => new { retentionYears = r.GetInt32(0), finalAction = r.GetString(1), active = r.GetBoolean(2), version = r.GetInt32(3) }, ct) ?? throw new NotFoundException("القاعدة غير موجودة");
            await tx.ExecuteAsync("""
                UPDATE docs.retention_rules SET name_ar = @NameAr, name_en = @NameEn, retention_years = @RetentionYears, archive_after_years = @ArchiveAfterYears,
                  final_action = @FinalAction, legal_basis = @LegalBasis, active = @Active, version = version + 1, updated_by = @by, updated_at = now() WHERE code = @code
                """, new { req.NameAr, req.NameEn, req.RetentionYears, req.ArchiveAfterYears, req.FinalAction, req.LegalBasis, req.Active, by = me.UserId, code }, ct);
            var n = await tx.ExecuteAsync("""
                UPDATE docs.documents SET retention_until = CASE WHEN @FinalAction = 'permanent' THEN NULL
                    ELSE (uploaded_at AT TIME ZONE 'Asia/Riyadh')::date + make_interval(years => @RetentionYears) END
                WHERE records_class = @code AND lifecycle_status IN ('active','archived')
                """, new { req.FinalAction, req.RetentionYears, code }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", "retention_rule.updated", "retention_rule", code, Before: before,
                After: new { req.RetentionYears, req.ArchiveAfterYears, req.FinalAction, req.Active, recalculated = n }), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>تطبيق سياسة الأرشفة: أرشفة المستندات النشطة التي انقضت «مدة الأرشفة» لصنفها منذ رفعها. لا يمس المحتوى، ويبقى قابلًا للتنزيل.</summary>
    private static async Task<IResult> ArchiveDue(Db db, IAccessService access, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var n = 0;
        await db.InTransactionAsync(async tx =>
        {
            n = await tx.ExecuteAsync("""
                UPDATE docs.documents d SET lifecycle_status = 'archived', archived_at = now()
                FROM docs.retention_rules r
                WHERE r.code = d.records_class AND d.lifecycle_status = 'active'
                  AND (d.uploaded_at AT TIME ZONE 'Asia/Riyadh')::date + make_interval(years => r.archive_after_years) <= @today
                """, new { today = clock.TodayLocal }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", "records.archive_policy_applied", "retention_rule", null, After: new { archived = n }), ct);
        }, ct);
        return Results.Ok(new { archived = n });
    }

    // ---------------- الإتلاف ----------------

    private static async Task<IResult> ListDestruction(Db db, IAccessService access, CancellationToken ct)
    {
        await DemandRecordsAsync(access, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT x.id, x.reference_no, cardinality(x.document_ids), x.reason, x.status, ru.display_name_ar, x.requested_at, du.display_name_ar, x.decided_at, x.decision_note,
                   x.executed_at, x.requested_by
            FROM docs.destruction_requests x JOIN iam.users ru ON ru.id = x.requested_by LEFT JOIN iam.users du ON du.id = x.decided_by
            ORDER BY x.requested_at DESC LIMIT 200
            """, null, r => new
        {
            id = r.GetGuid(0), referenceNo = r.GetString(1), documents = r.GetInt32(2), reason = r.GetString(3), status = r.GetString(4), requestedBy = r.GetString(5),
            requestedAt = r.GetDateTime(6), decidedBy = r.IsDBNull(7) ? null : r.GetString(7), decidedAt = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8),
            decisionNote = r.Str("decision_note"), executedAt = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10), requestedById = r.GetGuid(11),
        }, ct));
    }

    private static async Task<IResult> RequestDestruction(DestructionRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        INumberingService numbering, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var ids = req.DocumentIds!.Distinct().ToArray();
        Guid id = Guid.CreateVersion7();
        string refNo = "";
        await db.InTransactionAsync(async tx =>
        {
            // كل مستند يجب أن يكون الإصدار الحالي، ومنقضي الاحتفاظ، وغير محجوز، وغير دائم الحفظ، وغير مدرج في طلب آخر
            var rows = await tx.QueryAsync("""
                SELECT d.id, d.file_name, d.is_current, d.lifecycle_status, d.retention_until, d.legal_hold, coalesce(r.final_action, 'review')
                FROM docs.documents d LEFT JOIN docs.retention_rules r ON r.code = d.records_class WHERE d.id = ANY(@ids) FOR UPDATE OF d
                """, new { ids }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Current: r.GetBoolean(2), Status: r.GetString(3), Until: r.Date("retention_until"),
                    Hold: r.GetBoolean(5), Final: r.GetString(6)), ct);
            if (rows.Count != ids.Length) throw new NotFoundException("بعض المستندات غير موجودة");
            var errors = new List<string>();
            foreach (var d in rows)
            {
                if (d.Hold) errors.Add($"{d.Name}: محجوز قانونيًا");
                else if (d.Final == "permanent") errors.Add($"{d.Name}: حفظ دائم لا يُتلف");
                else if (d.Until is null || d.Until > clock.TodayLocal) errors.Add($"{d.Name}: لم تنقضِ مدة الاحتفاظ ({d.Until:yyyy-MM-dd})");
                else if (d.Status is not ("active" or "archived")) errors.Add($"{d.Name}: الحالة {d.Status}");
                else if (!d.Current) errors.Add($"{d.Name}: ليس الإصدار الحالي — يُتلف مع سلسلته من الإصدار الحالي");
            }
            if (errors.Count > 0) throw new ConflictException("not-eligible-for-destruction", "مستندات غير مؤهلة للإتلاف", new Dictionary<string, object?> { ["documents"] = errors });
            // الإتلاف يشمل كل إصدارات السلسلة
            var all = await tx.QueryAsync("SELECT id FROM docs.documents WHERE series_id IN (SELECT series_id FROM docs.documents WHERE id = ANY(@ids)) AND lifecycle_status <> 'destroyed'",
                new { ids }, r => r.GetGuid(0), ct);
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM docs.documents WHERE id = ANY(@all) AND legal_hold", new { all = all.ToArray() }, ct) > 0)
                throw new ConflictException("not-eligible-for-destruction", "إصدار سابق في السلسلة محجوز قانونيًا");
            var year = clock.TodayLocal.Year;
            refNo = $"DST-{year}-{await numbering.NextAsync(tx, "destruction", year, ct):0000}";
            await tx.ExecuteAsync("""
                INSERT INTO docs.destruction_requests (id, reference_no, document_ids, reason, requested_by) VALUES (@id, @refNo, @all, @reason, @by)
                """, new { id, refNo, all = all.ToArray(), reason = req.Reason!.Trim(), by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE docs.documents SET lifecycle_status = 'destruction_pending' WHERE id = ANY(@all)", new { all = all.ToArray() }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", "destruction.requested", "destruction_request", id.ToString(), After: new { refNo, documents = all.Count, req.Reason }), ct);
        }, ct);
        return Results.Created($"/api/v1/records/destruction-requests/{id}", new { id, referenceNo = refNo });
    }

    private static async Task<IResult> Decide(Guid id, DecideRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsDestructionApprove, Scope.System, ct);
        await db.InTransactionAsync(async tx =>
        {
            var x = await tx.QuerySingleAsync("SELECT status, requested_by, document_ids FROM docs.destruction_requests WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), By: r.GetGuid(1), Docs: r.GetFieldValue<Guid[]>(2)), ct);
            if (x == default) throw new NotFoundException();
            if (x.Status != "pending") throw new ConflictException("already-decided", "تم البت في الطلب");
            if (x.By == me.UserId) throw new ConflictException("four-eyes", "لا يعتمد مقدّم الطلب طلبه؛ يلزم معتمد آخر");
            if (!req.Approve && string.IsNullOrWhiteSpace(req.Note)) throw AppValidationException.For("note", "سبب الرفض مطلوب");
            await tx.ExecuteAsync("UPDATE docs.destruction_requests SET status = @s, decided_by = @me, decided_at = now(), decision_note = @n WHERE id = @id",
                new { s = req.Approve ? "approved" : "rejected", me = me.UserId, n = req.Note, id }, ct);
            if (!req.Approve)
                await tx.ExecuteAsync("UPDATE docs.documents SET lifecycle_status = CASE WHEN archived_at IS NULL THEN 'active' ELSE 'archived' END WHERE id = ANY(@d) AND lifecycle_status = 'destruction_pending'",
                    new { d = x.Docs }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", req.Approve ? "destruction.approved" : "destruction.rejected", "destruction_request", id.ToString(), After: new { req.Note }), ct);
        }, ct);
        return Results.NoContent();
    }

    /// <summary>تنفيذ الإتلاف المعتمد: حذف الملف من التخزين وبقاء سجله الوصفي وبصمته وشهادة الإتلاف. ما وُضع عليه حجز بعد الاعتماد يُستثنى ويُذكر في الشهادة.</summary>
    private static async Task<IResult> Execute(Guid id, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IOptions<StorageOptions> options,
        IWebHostEnvironment env, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.RecordsManage, Scope.System, ct);
        var root = Root(options, env);
        var keys = new List<string>();
        object? certificate = null;
        await db.InTransactionAsync(async tx =>
        {
            var x = await tx.QuerySingleAsync("SELECT status, document_ids, reference_no FROM docs.destruction_requests WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Docs: r.GetFieldValue<Guid[]>(1), Ref: r.GetString(2)), ct);
            if (x == default) throw new NotFoundException();
            if (x.Status != "approved") throw new ConflictException("not-approved", "لا يُنفذ الإتلاف قبل اعتماده من معتمد مختلف");
            var docs = await tx.QueryAsync("""
                SELECT id, file_name, sha256, size_bytes, classification, records_class, version_no, legal_hold, storage_key, uploaded_at FROM docs.documents WHERE id = ANY(@d) FOR UPDATE
                """, new { d = x.Docs }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Sha: r.GetString(2), Size: r.GetInt64(3), Cls: r.GetString(4), Rc: r.Str("records_class"),
                    Ver: r.GetInt32(6), Hold: r.GetBoolean(7), Key: r.GetString(8), At: r.GetDateTime(9)), ct);
            var destroyed = docs.Where(d => !d.Hold).ToList();
            var skipped = docs.Where(d => d.Hold).ToList();
            await tx.ExecuteAsync("""
                UPDATE docs.documents SET lifecycle_status = 'destroyed', destroyed_at = now(), destroyed_by = @me, deleted_at = coalesce(deleted_at, now()) WHERE id = ANY(@ids)
                """, new { me = me.UserId, ids = destroyed.Select(d => d.Id).ToArray() }, ct);
            await tx.ExecuteAsync("UPDATE docs.documents SET lifecycle_status = CASE WHEN archived_at IS NULL THEN 'active' ELSE 'archived' END WHERE id = ANY(@ids)",
                new { ids = skipped.Select(d => d.Id).ToArray() }, ct);
            certificate = new
            {
                referenceNo = x.Ref, executedAt = DateTime.UtcNow, executedBy = me.DisplayName,
                destroyed = destroyed.Select(d => new { id = d.Id, fileName = d.Name, versionNo = d.Ver, sha256 = d.Sha, sizeBytes = d.Size, classification = d.Cls, recordsClass = d.Rc, uploadedAt = d.At }),
                skippedLegalHold = skipped.Select(d => new { id = d.Id, fileName = d.Name }),
            };
            await tx.ExecuteAsync("UPDATE docs.destruction_requests SET status = 'executed', executed_by = @me, executed_at = now(), certificate = @c::jsonb WHERE id = @id",
                new { me = me.UserId, c = Json.Serialize(certificate), id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("DOC", "destruction.executed", "destruction_request", id.ToString(),
                After: new { destroyed = destroyed.Count, skipped = skipped.Count, hashes = destroyed.Select(d => d.Sha) }), ct);
            keys.AddRange(destroyed.Select(d => d.Key));
        }, ct);
        // حذف الملفات بعد تثبيت المعاملة؛ فشل حذف ملف لا يُرجع السجل (يُكتشف في فحص التخزين الدوري)
        foreach (var k in keys)
        {
            try { File.Delete(Path.Combine(root, k)); } catch (IOException) { }
        }
        return Results.Ok(certificate);
    }

    private static async Task<IResult> Certificate(Guid id, string? format, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        await DemandRecordsAsync(access, ct);
        var x = await db.QuerySingleAsync("""
            SELECT x.reference_no, x.reason, x.certificate::text, ru.display_name_ar, du.display_name_ar, eu.display_name_ar, x.requested_at, x.decided_at, x.executed_at, x.decision_note
            FROM docs.destruction_requests x JOIN iam.users ru ON ru.id = x.requested_by LEFT JOIN iam.users du ON du.id = x.decided_by LEFT JOIN iam.users eu ON eu.id = x.executed_by
            WHERE x.id = @id AND x.status = 'executed'
            """, new { id }, r => (Ref: r.GetString(0), Reason: r.GetString(1), Cert: r.GetString(2), Req: r.GetString(3), Dec: r.GetString(4), Exe: r.GetString(5),
                ReqAt: r.GetDateTime(6), DecAt: r.GetDateTime(7), ExeAt: r.GetDateTime(8), Note: r.Str("decision_note")), ct);
        if (x == default) throw new NotFoundException("لا توجد شهادة إتلاف لطلب غير منفذ");
        using var json = System.Text.Json.JsonDocument.Parse(x.Cert);
        if (format != "pdf") return Results.Content(x.Cert, "application/json");
        var pdf = new PdfReport(new PdfReportOptions
        {
            Title = $"شهادة إتلاف {x.Ref}", Landscape = true, HeaderLine1 = "المكتب الاستراتيجي لتطوير منطقة الجوف", HeaderLine2 = "نظام إدارة المجالس واللجان — إدارة السجلات",
            FooterText = "يبقى السجل الوصفي والبصمة محفوظين؛ المحتوى أُتلف", Author = me.DisplayName,
        });
        pdf.Title($"شهادة إتلاف مستندات — {x.Ref}");
        string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        pdf.KeyValues([("سبب الإتلاف", x.Reason), ("مقدّم الطلب", $"{x.Req} — {T(x.ReqAt)}"), ("المعتمد", $"{x.Dec} — {T(x.DecAt)}{(x.Note is null ? "" : " — " + x.Note)}"),
            ("المنفذ", $"{x.Exe} — {T(x.ExeAt)}")]);
        pdf.Heading("المستندات المتلفة");
        var rows = json.RootElement.GetProperty("destroyed").EnumerateArray().Select(e => (IReadOnlyList<string?>)new string?[]
        {
            e.GetProperty("fileName").GetString(), e.GetProperty("versionNo").GetInt32().ToString(CultureInfo.InvariantCulture), e.GetProperty("recordsClass").GetString() ?? "—",
            e.GetProperty("classification").GetString(), e.GetProperty("sizeBytes").GetInt64().ToString(CultureInfo.InvariantCulture), e.GetProperty("sha256").GetString(),
        }).ToList();
        pdf.Table(["الملف", "الإصدار", "الصنف", "التصنيف", "الحجم (بايت)", "SHA-256"], [0.24, 0.07, 0.14, 0.09, 0.1, 0.36],
            rows.Count == 0 ? [["—", "", "", "", "", ""]] : rows, size: 7.5);
        var skipped = json.RootElement.GetProperty("skippedLegalHold").EnumerateArray().Select(e => e.GetProperty("fileName").GetString()).ToList();
        if (skipped.Count > 0) pdf.Paragraph("استُثني لوجود حجز قانوني بعد الاعتماد: " + string.Join("، ", skipped));
        return Results.File(pdf.Build(), "application/pdf", $"destruction-certificate-{x.Ref}.pdf");
    }
}
