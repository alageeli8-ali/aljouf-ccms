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
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/documents").WithTags("Documents");
        g.MapGet("/", List);
        g.MapPost("/", Upload).DisableAntiforgery();
        g.MapGet("/{id:guid}/content", Download);
    }

    private static readonly string[] OwnerTypes = ["body", "meeting", "minutes", "resolution", "task"];
    private static readonly string[] Purposes = ["formation_decision", "agenda_memo", "attachment", "evidence"];

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
        return Results.Ok(await db.QueryAsync("""
            SELECT d.id, d.file_name, d.content_type, d.size_bytes, d.sha256, d.classification, d.scan_status, d.uploaded_at, u.display_name_ar, l.purpose
            FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id JOIN iam.users u ON u.id = d.uploaded_by
            WHERE l.owner_type = @ownerType AND l.owner_id = @ownerId AND d.deleted_at IS NULL ORDER BY d.uploaded_at DESC
            """, new { ownerType, ownerId }, r => new
        {
            id = r.GetGuid(0), fileName = r.GetString(1), contentType = r.GetString(2), sizeBytes = r.GetInt64(3), sha256 = r.GetString(4),
            classification = r.GetString(5), scanStatus = r.GetString(6), uploadedAt = r.GetDateTime(7), uploadedBy = r.GetString(8), purpose = r.GetString(9),
        }, ct));
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
                await tx.ExecuteAsync("INSERT INTO docs.document_links (id, document_id, owner_type, owner_id, purpose) VALUES (@lid, @id, @ownerType, @ownerId, @purpose)",
                    new { lid = Guid.CreateVersion7(), id, ownerType, ownerId, purpose }, ct);
                await audit.WriteAsync(tx, new AuditEntry("DOC", "document.uploaded", "document", id.ToString(), After: new { ownerType, ownerId, purpose, sha, size = file.Length }), ct);
            }, ct);
        }
        catch
        {
            File.Delete(full);
            throw;
        }
        return Results.Created($"/api/v1/documents/{id}/content", new { id, sha256 = sha, scanStatus = "not_scanned" });
    }

    private static async Task<IResult> Download(Guid id, Db db, IAccessService access, IAuditWriter audit, IOptions<StorageOptions> options,
        IWebHostEnvironment env, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.DocumentView, Scope.Document(id), ct);
        var d = await db.QuerySingleAsync("SELECT file_name, content_type, storage_key, scan_status, classification FROM docs.documents WHERE id = @id AND deleted_at IS NULL",
            new { id }, r => (Name: r.GetString(0), Type: r.GetString(1), Key: r.GetString(2), Scan: r.GetString(3), Cls: r.GetString(4)), ct);
        if (d == default) throw new NotFoundException();
        if (d.Scan == "infected") throw new ConflictException("file-infected", "الملف محجوب لاكتشاف برمجية ضارة");
        var root = System.IO.Path.IsPathRooted(options.Value.Path) ? options.Value.Path : System.IO.Path.Combine(env.ContentRootPath, options.Value.Path);
        await audit.WriteAsync(new AuditEntry("DOC", "document.downloaded", "document", id.ToString(), Classification: d.Cls), ct);
        return Results.File(System.IO.Path.Combine(root, d.Key), d.Type, d.Name);
    }
}

internal sealed class DocumentOwnerQuery(Db db) : IDocumentOwnerQuery
{
    public async Task<IReadOnlyList<DocumentOwner>> GetOwnersAsync(Guid documentId, CancellationToken ct = default) =>
        await db.QueryAsync("SELECT owner_type, owner_id FROM docs.document_links WHERE document_id = @documentId", new { documentId },
            r => new DocumentOwner(r.GetString(0), r.GetGuid(1)), ct);
}
