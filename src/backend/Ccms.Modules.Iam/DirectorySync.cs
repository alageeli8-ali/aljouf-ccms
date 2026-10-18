using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Iam;

/// <summary>مزامنة الدليل المؤسسي (23 §4.3): معاينة ثم تطبيق صريح. تُحدّث الأسماء والمسميات وربط معرّف Entra للحسابات المطابقة بالبريد،
/// وتُعطّل الحسابات الداخلية المعطلة في الدليل. لا تُنشأ حسابات ولا تُمنح أدوار آليًا (الإنشاء عند أول دخول موحد أو يدويًا)، ولا تُمس الحسابات المحلية الطارئة والخارجية.</summary>
internal static class DirectorySync
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/admin/directory-sync").WithTags("Users");
        g.MapGet("/preview", Preview);
        g.MapPost("/apply", Apply);
    }

    private sealed record Change(Guid UserId, string Email, string Kind, string? DisplayNameBefore, string? DisplayNameAfter, string? JobTitleBefore, string? JobTitleAfter,
        string ExternalId);

    private static async Task<(List<Change> Changes, int DirectoryCount, List<string> NotProvisioned)> PlanAsync(Db db, IDirectoryGateway gateway, CancellationToken ct)
    {
        var users = await gateway.ListUsersAsync(5000, ct);
        var local = (await db.QueryAsync("""
            SELECT id, lower(email), display_name_ar, job_title, entra_object_id, status, user_type FROM iam.users WHERE user_type IN ('internal','guest')
            """, null, r => (Id: r.GetGuid(0), Email: r.GetString(1), Name: r.GetString(2), Title: r.Str("job_title"), Oid: r.Str("entra_object_id"), Status: r.GetString(5)), ct))
            .ToDictionary(x => x.Email);
        var changes = new List<Change>();
        var notProvisioned = new List<string>();
        foreach (var u in users)
        {
            if (!local.TryGetValue(u.Email.ToLowerInvariant(), out var l)) { if (u.Enabled) notProvisioned.Add(u.Email); continue; }
            if (!u.Enabled && l.Status == "active") { changes.Add(new(l.Id, u.Email, "disable", null, null, null, null, u.ExternalId)); continue; }
            if (!u.Enabled) continue;
            var nameChanged = !string.IsNullOrWhiteSpace(u.DisplayName) && u.DisplayName != l.Name;
            var titleChanged = (u.JobTitle ?? "") != (l.Title ?? "");
            var link = l.Oid != u.ExternalId;
            if (nameChanged || titleChanged || link)
                changes.Add(new(l.Id, u.Email, link && !nameChanged && !titleChanged ? "link" : "update", l.Name, nameChanged ? u.DisplayName : l.Name, l.Title,
                    titleChanged ? u.JobTitle : l.Title, u.ExternalId));
        }
        return (changes, users.Count, notProvisioned);
    }

    private static IDirectoryGateway Gateway(IServiceProvider sp) =>
        sp.GetService<IDirectoryGateway>() ?? throw new ConflictException("integration-unavailable", "وحدة التكاملات غير مثبتة");

    private static async Task<IResult> Preview(Db db, IAccessService access, IServiceProvider sp, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        try
        {
            var (changes, count, notProvisioned) = await PlanAsync(db, Gateway(sp), ct);
            return Results.Ok(new { directoryUsers = count, changes, notProvisioned = notProvisioned.Take(200), notProvisionedCount = notProvisioned.Count });
        }
        catch (IntegrationFailedException e)
        {
            throw new ConflictException("integration-failed", e.Message, new Dictionary<string, object?> { ["connector"] = e.Connector, ["reason"] = e.Reason });
        }
    }

    private static async Task<IResult> Apply(Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, IServiceProvider sp, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        List<Change> changes;
        try { (changes, _, _) = await PlanAsync(db, Gateway(sp), ct); }
        catch (IntegrationFailedException e)
        {
            throw new ConflictException("integration-failed", e.Message, new Dictionary<string, object?> { ["connector"] = e.Connector, ["reason"] = e.Reason });
        }
        await db.InTransactionAsync(async tx =>
        {
            foreach (var c in changes)
            {
                if (c.Kind == "disable")
                    await tx.ExecuteAsync("UPDATE iam.users SET status = 'disabled', updated_by = @me WHERE id = @id AND user_type IN ('internal','guest')", new { id = c.UserId, me = me.UserId }, ct);
                else
                    await tx.ExecuteAsync("""
                        UPDATE iam.users SET display_name_ar = @name, job_title = @title, entra_object_id = @oid, updated_by = @me WHERE id = @id
                        """, new { id = c.UserId, name = c.DisplayNameAfter, title = c.JobTitleAfter, oid = c.ExternalId, me = me.UserId }, ct);
                await audit.WriteAsync(tx, new AuditEntry("IAM", "user.directory_synced", "user", c.UserId.ToString(),
                    Before: new { c.DisplayNameBefore, c.JobTitleBefore }, After: new { c.Kind, c.DisplayNameAfter, c.JobTitleAfter }), ct);
            }
            // إبطال جلسات من عُطّل
            var disabled = changes.Where(c => c.Kind == "disable").Select(c => c.UserId).ToArray();
            if (disabled.Length > 0)
                await tx.ExecuteAsync("UPDATE iam.sessions SET revoked_at = now(), revoked_reason = 'directory_disabled' WHERE user_id = ANY(@disabled) AND revoked_at IS NULL", new { disabled }, ct);
        }, ct);
        return Results.Ok(new { applied = changes.Count, disabled = changes.Count(c => c.Kind == "disable"), updated = changes.Count(c => c.Kind != "disable") });
    }
}
