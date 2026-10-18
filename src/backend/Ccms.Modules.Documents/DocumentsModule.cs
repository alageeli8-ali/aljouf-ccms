using System.Security.Cryptography;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Documents;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string Path { get; set; } = "storage";
    public long MaxSizeMb { get; set; } = 50;
    public string[] AllowedExtensions { get; set; } = [".pdf", ".docx", ".xlsx", ".pptx", ".png", ".jpg", ".jpeg"];
}

/// <summary>المستندات: صلاحية المستند مشتقة من السجل المالك (FR-DOC-04)، والتخزين خلف منفذ قابل للاستبدال (FR-DOC-05).</summary>
public sealed class DocumentsModule : IModule
{
    public string Code => "DOC";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddScoped<IDocumentOwnerQuery, DocumentOwnerQuery>();
        services.AddScoped<IDocumentStore, DocumentStore>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/documents").WithTags("Documents");
        g.MapGet("/", List);
        g.MapPost("/", Upload).DisableAntiforgery();
        g.MapGet("/{id:guid}/content", Download);
        g.MapPost("/{id:guid}/share", Share).Validate<ShareRequest>();
        Records.Map(api);
    }

    private static readonly string[] OwnerTypes = ["body", "meeting", "agenda_item", "minutes", "resolution", "task"];
    private static readonly string[] Purposes = ["formation_decision", "agenda_memo", "attachment", "evidence", "wet_signature", "approval_evidence", "deliverable"];

    public sealed record ShareRequest([property: System.ComponentModel.DataAnnotations.Required] Guid? TaskId, bool Share);

    private static Scope OwnerScope(string type, Guid id) => type switch
    {
        "body" => Scope.Body(id),
        "task" => Scope.Task(id),
        _ => Scope.Record(type, id),
    };

    private static string ViewPermission(string type) => type == "task" ? Permissions.TaskView : Permissions.DocumentView;

    private static async Task<IResult> List(string ownerType, Guid ownerId, Db db, IAccessService access, CancellationToken ct)
    {
        if (!OwnerTypes.Contains(ownerType)) throw AppValidationException.For("ownerType", "نوع مالك غير معروف");
        await access.DemandAsync(ViewPermission(ownerType), OwnerScope(ownerType, ownerId), ct);
        var portalOnly = ownerType == "task" && await access.IsPortalOnlyAsync(ownerId, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT d.id, d.file_name, d.content_type, d.size_bytes, d.sha256, d.classification, d.scan_status, d.uploaded_at, u.display_name_ar, l.purpose, l.external_visible,
                   d.version_no, d.locked, d.lifecycle_status, d.records_class, d.legal_hold
            FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id JOIN iam.users u ON u.id = d.uploaded_by
            WHERE l.owner_type = @ownerType AND l.owner_id = @ownerId AND d.deleted_at IS NULL AND d.is_current AND (NOT @portalOnly::boolean OR l.external_visible)
            ORDER BY d.uploaded_at DESC
            """, new { ownerType, ownerId, portalOnly }, r => new
        {
            id = r.GetGuid(0), fileName = r.GetString(1), contentType = r.GetString(2), sizeBytes = r.GetInt64(3), sha256 = r.GetString(4),
            classification = r.GetString(5), scanStatus = r.GetString(6), uploadedAt = r.GetDateTime(7), uploadedBy = r.GetString(8), purpose = r.GetString(9),
            sharedWithEntity = r.GetBoolean(10), versionNo = r.GetInt32(11), locked = r.GetBoolean(12), lifecycleStatus = r.GetString(13),
            recordsClass = r.Str("records_class"), legalHold = r.GetBoolean(15),
        }, ct));
    }

    /// <summary>مشاركة مستند مهمة مع الجهة الخارجية المكلفة أو إلغاء مشاركته؛ لمن يدير المهمة أو يتابعها من الداخل فقط.</summary>
    private static async Task<IResult> Share(Guid id, ShareRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var taskId = req.TaskId!.Value;
        await access.DemandAsync(Permissions.TaskUpdate, Scope.Task(taskId), ct);
        if (await access.IsPortalOnlyAsync(taskId, ct)) throw new ForbiddenException();
        var perms = await access.GetPermissionsAsync(Scope.Task(taskId), ct);
        if (!perms.Contains(Permissions.TaskManage) && !perms.Contains(Permissions.TaskRecordExternal)) throw new ForbiddenException(permission: Permissions.TaskManage);
        var n = await db.ExecuteAsync("UPDATE docs.document_links SET external_visible = @Share WHERE document_id = @id AND owner_type = 'task' AND owner_id = @taskId",
            new { id, taskId, req.Share }, ct);
        if (n == 0) throw new NotFoundException();
        await audit.WriteAsync(new AuditEntry("DOC", req.Share ? "document.shared_with_entity" : "document.unshared", "document", id.ToString(), After: new { taskId }), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Upload(HttpRequest request, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        IOptions<StorageOptions> options, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!request.HasFormContentType) throw AppValidationException.For("file", "أرسل الملف بصيغة multipart/form-data");
        var form = await request.ReadFormAsync(ct);
        var ownerType = form["ownerType"].ToString();
        var purpose = form["purpose"].ToString() is { Length: > 0 } pp ? pp : "attachment";
        if (!OwnerTypes.Contains(ownerType)) throw AppValidationException.For("ownerType", "نوع مالك غير معروف");
        if (!Guid.TryParse(form["ownerId"], out var ownerId)) throw AppValidationException.For("ownerId", "معرّف المالك غير صالح");
        if (!Purposes.Contains(purpose)) throw AppValidationException.For("purpose", "غرض غير مسموح");
        await access.DemandAsync(ownerType == "task" ? Permissions.TaskUpdate : Permissions.DocumentUpload, OwnerScope(ownerType, ownerId), ct);
        // ما ترفعه الجهة الخارجية عبر البوابة مشارك معها تلقائيًا؛ وما يرفعه الداخل لا يُشارك إلا صراحة
        var externalVisible = ownerType == "task" && (await access.IsPortalOnlyAsync(ownerId, ct) || form["shareWithEntity"].ToString() == "true");

        var file = form.Files.GetFile("file") ?? throw AppValidationException.For("file", "الملف مطلوب");
        var o = options.Value;
        var ext = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!o.AllowedExtensions.Contains(ext)) throw AppValidationException.For("file", $"نوع الملف غير مسموح؛ المسموح: {string.Join(" ", o.AllowedExtensions)}");
        if (file.Length == 0 || file.Length > o.MaxSizeMb * 1024 * 1024) throw AppValidationException.For("file", $"حجم الملف يجب ألا يتجاوز {o.MaxSizeMb} م.ب");

        var id = Guid.CreateVersion7();
        var root = System.IO.Path.IsPathRooted(o.Path) ? o.Path : System.IO.Path.Combine(env.ContentRootPath, o.Path);
        var key = $"{DateTime.UtcNow:yyyy/MM}/{id:N}";
        var full = System.IO.Path.Combine(root, key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string sha;
        await using (var target = File.Create(full))
        await using (var source = file.OpenReadStream())
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            sha = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        try
        {
            await db.InTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("""
                    INSERT INTO docs.documents (id, file_name, content_type, size_bytes, sha256, storage_key, uploaded_by)
                    VALUES (@id, @name, @type, @size, @sha, @key, @by)
                    """, new { id, name = System.IO.Path.GetFileName(file.FileName), type = file.ContentType, size = file.Length, sha, key, by = me.UserId }, ct);
                await tx.ExecuteAsync("""
                    INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose, external_visible) VALUES (@lid, @id, @ownerType, @ownerId, @purpose, @externalVisible)
                    """, new { lid = Guid.CreateVersion7(), id, ownerType, ownerId, purpose, externalVisible }, ct);
                await audit.WriteAsync(tx, new AuditEntry("DOC", "document.uploaded", "document", id.ToString(), After: new { ownerType, ownerId, purpose, sha, size = file.Length, externalVisible }), ct);
            }, ct);
        }
        catch
        {
            File.Delete(full);
            throw;
        }
        return Results.Created($"/api/v1/documents/{id}/content", new { id, sha256 = sha, scanStatus = "not_scanned", sharedWithEntity = externalVisible });
    }

    private static async Task<IResult> Download(Guid id, Db db, IAccessService access, IAuditWriter audit, IOptions<StorageOptions> options,
        IWebHostEnvironment env, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.DocumentView, Scope.Document(id), ct);
        var d = await db.QuerySingleAsync("SELECT file_name, content_type, storage_key, scan_status, classification, lifecycle_status FROM docs.documents WHERE id = @id",
            new { id }, r => (Name: r.GetString(0), Type: r.GetString(1), Key: r.GetString(2), Scan: r.GetString(3), Cls: r.GetString(4), Life: r.GetString(5)), ct);
        if (d == default) throw new NotFoundException();
        if (d.Life == "destroyed") throw new ConflictException("document-destroyed", "أُتلف محتوى هذا المستند وفق قواعد الاحتفاظ؛ سجله الوصفي وشهادة إتلافه محفوظان");
        if (d.Scan == "infected") throw new ConflictException("file-infected", "الملف محجوب لاكتشاف برمجية ضارة");
        var root = System.IO.Path.IsPathRooted(options.Value.Path) ? options.Value.Path : System.IO.Path.Combine(env.ContentRootPath, options.Value.Path);
        await audit.WriteAsync(new AuditEntry("DOC", "document.downloaded", "document", id.ToString(), Classification: d.Cls), ct);
        return Results.File(System.IO.Path.Combine(root, d.Key), d.Type, d.Name);
    }
}

internal sealed class DocumentOwnerQuery(Db db) : IDocumentOwnerQuery
{
    public async Task<IReadOnlyList<DocumentOwner>> GetOwnersAsync(Guid documentId, CancellationToken ct = default) =>
        await db.QueryAsync("SELECT owner_type, owner_id, external_visible FROM docs.document_links WHERE document_id = @documentId", new { documentId },
            r => new DocumentOwner(r.GetString(0), r.GetGuid(1), r.GetBoolean(2)), ct);
}

/// <summary>حفظ المستندات التي يولّدها النظام (PDF المحضر المعتمد) بالمنفذ نفسه وببصمة SHA-256.</summary>
internal sealed class DocumentStore(Db db, IOptions<StorageOptions> options, IWebHostEnvironment env, IAuditWriter audit) : IDocumentStore
{
    private string Root => System.IO.Path.IsPathRooted(options.Value.Path) ? options.Value.Path : System.IO.Path.Combine(env.ContentRootPath, options.Value.Path);

    public async Task<StoredDocument> SaveAsync(ISql tx, string fileName, string contentType, byte[] content, string ownerType, Guid ownerId,
        string purpose, string classification, Guid by, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var key = $"{DateTime.UtcNow:yyyy/MM}/{id:N}";
        var full = System.IO.Path.Combine(Root, key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        await tx.ExecuteAsync("""
            INSERT INTO docs.documents (id, file_name, content_type, size_bytes, sha256, storage_key, classification, uploaded_by)
            VALUES (@id, @fileName, @contentType, @size, @sha, @key, @classification, @by)
            """, new { id, fileName, contentType, size = (long)content.Length, sha, key, classification, by }, ct);
        await tx.ExecuteAsync("INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose) VALUES (@lid, @id, @ownerType, @ownerId, @purpose)",
            new { lid = Guid.CreateVersion7(), id, ownerType, ownerId, purpose }, ct);
        await audit.WriteAsync(tx, new AuditEntry("DOC", "document.generated", "document", id.ToString(), After: new { ownerType, ownerId, purpose, sha, size = content.Length }), ct);
        return new StoredDocument(id, sha, content.Length);
    }

    public async Task<byte[]?> ReadAsync(Guid documentId, CancellationToken ct = default)
    {
        var key = await db.ScalarAsync<string>("SELECT storage_key FROM docs.documents WHERE id = @documentId AND deleted_at IS NULL", new { documentId }, ct);
        if (key is null) return null;
        var full = System.IO.Path.Combine(Root, key);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : null;
    }

    public async Task<bool> IsLinkedAsync(ISql sql, Guid documentId, string ownerType, Guid ownerId, CancellationToken ct = default) =>
        await sql.ScalarAsync<long>("""
            SELECT count(*) FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id
            WHERE l.document_id = @documentId AND l.owner_type = @ownerType AND l.owner_id = @ownerId AND d.deleted_at IS NULL
            """, new { documentId, ownerType, ownerId }, ct) > 0;
}
