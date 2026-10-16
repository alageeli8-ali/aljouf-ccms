using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Iam;

/// <summary>تقييم الصلاحيات النطاقية في الخادم لكل طلب (06-rbac §1).
/// المصادر: الأدوار النظامية، والعضويات السارية، والعلاقة المباشرة بالمهمة، وتمثيل الجهة الخارجية.</summary>
internal sealed class AccessService(
    Db db,
    ICurrentUser user,
    IMemoryCache cache,
    IServiceProvider services,
    IFeatureService features,
    IClock clock) : IAccessService
{
    private static readonly string[] VisibilityPermissions =
        [Permissions.BodyView, Permissions.MeetingView, Permissions.MinutesView, Permissions.ResolutionView, Permissions.TaskView, Permissions.DocumentView];

    // تخزين مؤقت داخل الطلب
    private readonly Dictionary<Scope, IReadOnlySet<string>> _perScope = [];
    private IReadOnlyList<string>? _systemRoles;
    private IReadOnlyList<BodyRole>? _bodyRoles;
    private Guid? _externalEntityId;
    private bool _externalLoaded;

    private async Task<Dictionary<string, HashSet<string>>> RolePermissionsAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("iam:role-permissions", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var rows = await db.QueryAsync("SELECT role_code, permission_code FROM iam.role_permissions", null,
                r => (Role: r.GetString(0), Perm: r.GetString(1)), ct);
            return rows.GroupBy(x => x.Role).ToDictionary(g => g.Key, g => g.Select(x => x.Perm).ToHashSet());
        }))!;

    public async Task<IReadOnlyList<string>> GetSystemRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var roles = await db.QueryAsync("""
            SELECT DISTINCT role_code FROM iam.system_role_assignments
            WHERE user_id = @userId AND valid_from <= @today AND (valid_to IS NULL OR valid_to >= @today)
            """, new { userId, today = clock.TodayLocal }, r => r.GetString(0), ct);
        roles.Add(Roles.Employee);
        return roles.Distinct().ToList();
    }

    private async Task<IReadOnlyList<string>> MySystemRolesAsync(CancellationToken ct) =>
        _systemRoles ??= await GetSystemRolesAsync(user.UserId, ct);

    private async Task<IReadOnlyList<BodyRole>> MyBodyRolesAsync(CancellationToken ct) =>
        _bodyRoles ??= await services.GetRequiredService<IBodyMembershipQuery>().GetActiveRolesAsync(user.UserId, clock.TodayLocal, ct);

    private async Task<Guid?> MyExternalEntityAsync(CancellationToken ct)
    {
        if (_externalLoaded) return _externalEntityId;
        _externalEntityId = await db.ScalarAsync<Guid?>("SELECT external_entity_id FROM iam.users WHERE id = @id", new { id = user.UserId }, ct);
        _externalLoaded = true;
        return _externalEntityId;
    }

    /// <summary>صلاحيات تمنحها الأدوار النظامية في كل الجهات. ممثل الجهة الخارجية مستثنى: صلاحياته محصورة في مهام جهته.</summary>
    private async Task<HashSet<string>> GlobalGrantsAsync(CancellationToken ct)
    {
        var map = await RolePermissionsAsync(ct);
        var set = new HashSet<string>();
        foreach (var role in await MySystemRolesAsync(ct))
            if (role != Roles.ExternalRepresentative && map.TryGetValue(role, out var perms)) set.UnionWith(perms);
        return set;
    }

    private static IEnumerable<string> RolePerms(Dictionary<string, HashSet<string>> map, string role) =>
        map.TryGetValue(role, out var p) ? p : [];

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Scope scope, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated) return new HashSet<string>();
        if (_perScope.TryGetValue(scope, out var cached)) return cached;
        var result = scope.Type switch
        {
            ScopeType.System => await GlobalGrantsAsync(ct),
            ScopeType.Body => await BodyPermissionsAsync(scope.Id!.Value, ct),
            ScopeType.Task => await TaskPermissionsAsync(scope.Id!.Value, ct),
            ScopeType.Record => await RecordPermissionsAsync(scope.RecordType!, scope.Id!.Value, ct),
            ScopeType.Document => await DocumentPermissionsAsync(scope.Id!.Value, ct),
            _ => [],
        };
        _perScope[scope] = result;
        return result;
    }

    private async Task<HashSet<string>> BodyPermissionsAsync(Guid bodyId, CancellationToken ct)
    {
        var map = await RolePermissionsAsync(ct);
        var set = await GlobalGrantsAsync(ct);
        foreach (var br in (await MyBodyRolesAsync(ct)).Where(r => r.BodyId == bodyId))
            set.UnionWith(RolePerms(map, br.Role));
        return set;
    }

    private async Task<HashSet<string>> TaskPermissionsAsync(Guid taskId, CancellationToken ct)
    {
        var rel = await services.GetRequiredService<ITaskRelationQuery>().GetAsync(taskId, ct);
        if (rel is null) return [];
        var map = await RolePermissionsAsync(ct);
        var set = await BodyPermissionsAsync(rel.BodyId, ct);
        if (rel.AssigneeUserId == user.UserId) set.UnionWith(RolePerms(map, Roles.TaskExecutor));
        if (rel.AccountableUserId == user.UserId || rel.AssigneeOrgUnitManagerId == user.UserId)
            set.UnionWith(RolePerms(map, Roles.TaskFollowUp));
        if (rel.AssigneeExternalEntityId is { } entity
            && (await MySystemRolesAsync(ct)).Contains(Roles.ExternalRepresentative)
            && await MyExternalEntityAsync(ct) == entity
            && await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct))
            set.UnionWith(RolePerms(map, Roles.ExternalRepresentative));
        return set;
    }

    private async Task<HashSet<string>> RecordPermissionsAsync(string type, Guid id, CancellationToken ct)
    {
        var resolver = services.GetServices<IRecordBodyResolver>().FirstOrDefault(r => r.RecordType == type)
            ?? throw new InvalidOperationException($"لا يوجد محلل لنوع السجل {type}");
        var bodyId = await resolver.ResolveBodyAsync(id, ct);
        HashSet<string> set = bodyId is null ? [] : await BodyPermissionsAsync(bodyId.Value, ct);
        // صلاحيات العلاقة المباشرة بالسجل، مثل الضيف المدعو إلى اجتماع
        foreach (var rel in services.GetServices<IRecordRelationProvider>().Where(r => r.RecordType == type))
            set.UnionWith(await rel.GetPermissionsAsync(id, user.UserId, ct));
        return set;
    }

    private async Task<HashSet<string>> DocumentPermissionsAsync(Guid documentId, CancellationToken ct)
    {
        var owners = await services.GetRequiredService<IDocumentOwnerQuery>().GetOwnersAsync(documentId, ct);
        var set = new HashSet<string>();
        foreach (var o in owners)
        {
            var ownerPerms = o.OwnerType switch
            {
                "body" => await BodyPermissionsAsync(o.OwnerId, ct),
                "task" => await TaskPermissionsAsync(o.OwnerId, ct),
                _ => await RecordPermissionsAsync(o.OwnerType, o.OwnerId, ct),
            };
            // صلاحيات المستند تُشتق من السجل المالك (FR-DOC-04)
            if (ownerPerms.Contains(Permissions.DocumentView)) set.Add(Permissions.DocumentView);
            if (ownerPerms.Contains(Permissions.DocumentUpload)) set.Add(Permissions.DocumentUpload);
        }
        return set;
    }

    public async Task<bool> HasAsync(string permission, Scope scope, CancellationToken ct = default) =>
        (await GetPermissionsAsync(scope, ct)).Contains(permission);

    public async Task DemandAsync(string permission, Scope scope, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated) throw new UnauthenticatedException();
        var perms = await GetPermissionsAsync(scope, ct);
        if (perms.Contains(permission)) return;
        if (scope.Type == ScopeType.System || VisibilityPermissions.Any(perms.Contains))
            throw new ForbiddenException(permission: permission);
        throw new NotFoundException();
    }

    public async Task<BodyFilter> GetBodyFilterAsync(string permission, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated) return BodyFilter.None;
        if ((await GlobalGrantsAsync(ct)).Contains(permission)) return new BodyFilter(true, []);
        var map = await RolePermissionsAsync(ct);
        var ids = (await MyBodyRolesAsync(ct))
            .Where(br => RolePerms(map, br.Role).Contains(permission))
            .Select(br => br.BodyId).Distinct().ToArray();
        return new BodyFilter(false, ids);
    }

    public void Invalidate(Guid userId)
    {
        _perScope.Clear();
        _systemRoles = null;
        _bodyRoles = null;
        _externalLoaded = false;
    }
}
