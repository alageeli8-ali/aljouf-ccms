using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Org;

public sealed class OrgModule : IModule
{
    public string Code => "ORG";

    public void AddServices(IServiceCollection services, IConfiguration configuration) { }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("").WithTags("Organization");
        g.MapGet("/org-units", ListUnits);
        g.MapPost("/org-units", CreateUnit).Validate<OrgUnitRequest>();
        g.MapGet("/external-entities", ListEntities);
        g.MapPost("/external-entities", CreateEntity).Validate<ExternalEntityRequest>();
    }

    public sealed record OrgUnitRequest(
        [property: Required, RegularExpression("^[A-Z0-9-]{2,20}$", ErrorMessage = "رمز من 2–20 حرفًا لاتينيًا كبيرًا أو أرقام")] string Code,
        [property: Required, MaxLength(200)] string NameAr,
        [property: MaxLength(200)] string? NameEn,
        Guid? ParentId,
        Guid? ManagerUserId);

    public sealed record ExternalEntityRequest(
        [property: Required, MaxLength(200)] string NameAr,
        [property: MaxLength(200)] string? NameEn,
        [property: OneOf("government", "municipality", "company", "university", "other")] string? EntityType,
        [property: MaxLength(200)] string? ContactName,
        [property: EmailAddress, MaxLength(200)] string? ContactEmail,
        [property: MaxLength(50)] string? ContactPhone,
        [property: Required(ErrorMessage = "المسؤول المتابع الداخلي إلزامي")] Guid? LiaisonUserId);

    private static async Task<IResult> ListUnits(Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.UsersDirectory, Scope.System, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT o.id, o.code, o.name_ar, o.name_en, o.parent_id, o.manager_user_id, u.display_name_ar AS manager_name, o.status
            FROM org.org_units o LEFT JOIN iam.users u ON u.id = o.manager_user_id ORDER BY o.name_ar
            """, null, r => new
        {
            id = r.Get<Guid>("id"), code = r.Get<string>("code"), nameAr = r.Get<string>("name_ar"), nameEn = r.Str("name_en"),
            parentId = r.Uuid("parent_id"), managerUserId = r.Uuid("manager_user_id"), managerName = r.Str("manager_name"), status = r.Get<string>("status"),
        }, ct));
    }

    private static async Task<IResult> CreateUnit(OrgUnitRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.OrgManage, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("INSERT INTO org.org_units (id, code, name_ar, name_en, parent_id, manager_user_id) VALUES (@id, @Code, @NameAr, @NameEn, @ParentId, @ManagerUserId)",
                new { id, req.Code, req.NameAr, req.NameEn, req.ParentId, req.ManagerUserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("ORG", "org_unit.created", "org_unit", id.ToString(), After: req), ct);
        }, ct);
        return Results.Created($"/api/v1/org-units/{id}", new { id });
    }

    private static async Task<IResult> ListEntities(Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.UsersDirectory, Scope.System, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT e.id, e.name_ar, e.name_en, e.entity_type, e.contact_name, e.contact_email, e.liaison_user_id, u.display_name_ar AS liaison_name, e.status
            FROM org.external_entities e JOIN iam.users u ON u.id = e.liaison_user_id ORDER BY e.name_ar
            """, null, r => new
        {
            id = r.Get<Guid>("id"), nameAr = r.Get<string>("name_ar"), nameEn = r.Str("name_en"), entityType = r.Get<string>("entity_type"),
            contactName = r.Str("contact_name"), contactEmail = r.Str("contact_email"), liaisonUserId = r.Get<Guid>("liaison_user_id"),
            liaisonName = r.Get<string>("liaison_name"), status = r.Get<string>("status"),
        }, ct));
    }

    private static async Task<IResult> CreateEntity(ExternalEntityRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.OrgManage, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                INSERT INTO org.external_entities (id, name_ar, name_en, entity_type, contact_name, contact_email, contact_phone, liaison_user_id)
                VALUES (@id, @NameAr, @NameEn, @type, @ContactName, @ContactEmail, @ContactPhone, @LiaisonUserId)
                """, new { id, req.NameAr, req.NameEn, type = req.EntityType ?? "government", req.ContactName, req.ContactEmail, req.ContactPhone, req.LiaisonUserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("ORG", "external_entity.created", "external_entity", id.ToString(), After: req), ct);
        }, ct);
        return Results.Created($"/api/v1/external-entities/{id}", new { id });
    }
}
