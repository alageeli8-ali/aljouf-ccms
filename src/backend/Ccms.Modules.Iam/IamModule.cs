using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Iam;

public sealed class IamModule : IModule
{
    public string Code => "IAM";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IAccessService, AccessService>();
        services.AddScoped<IUserQuery, UserQuery>();
        services.AddScoped<SessionService>();
        services.AddScoped<LocalLoginService>();
        services.AddScoped<IReauthenticator, Reauthenticator>();
        services.AddSingleton<EntraSsoConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<EntraSsoConnector>());
        services.AddHttpClient("sso", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<SessionValidationEvents>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "ccms.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.SlidingExpiration = true;
                o.ExpireTimeSpan = TimeSpan.FromHours(10);
                o.EventsType = typeof(SessionValidationEvents);
            });
        services.AddAuthorization();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");
        auth.MapGet("/providers", Providers).AllowAnonymous();
        auth.MapPost("/login", Login).AllowAnonymous().Validate<LoginRequest>().RequireRateLimiting("login");
        auth.MapPost("/logout", Logout);
        auth.MapPost("/logout-all", LogoutAll);
        auth.MapPost("/password", ChangePassword).Validate<ChangePasswordRequest>();

        api.MapGet("/me", Me).WithTags("Auth");

        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/", ListUsers);
        users.MapGet("/{id:guid}", GetUser);
        users.MapPost("/", CreateUser).Validate<CreateUserRequest>();
        users.MapPatch("/{id:guid}", UpdateUser).Validate<UpdateUserRequest>();
        users.MapPut("/{id:guid}/system-roles", SetRoles).Validate<SetRolesRequest>();
        users.MapPut("/{id:guid}/password", SetPassword).Validate<SetPasswordRequest>();
        api.MapGet("/roles", ListRoles).WithTags("Users");
        api.MapGet("/directory", Directory).WithTags("Users");
    }

    // ---------------- الدخول ----------------

    internal static async Task<IResult> Providers(IOptions<AuthOptions> options, IFeatureService features, IConnectorStatusStore status, CancellationToken ct)
    {
        var ssoEnabled = await features.IsEnabledAsync(FeatureKeys.SsoEnabled, ct);
        var health = await status.GetAsync(EntraSsoConnector.ConnectorCode, ct);
        return Results.Ok(new
        {
            local = new { enabled = options.Value.LocalLogin.Enabled, breakGlassOnly = options.Value.LocalLogin.BreakGlassOnly },
            sso = new
            {
                featureEnabled = ssoEnabled,
                configured = options.Value.Entra.IsConfigured,
                connectionStatus = health?.StateCode ?? "not_configured",
                available = ssoEnabled && options.Value.Entra.IsConfigured && health?.State == ConnectorState.Healthy,
                loginUrl = "/auth/sso/login",
            },
        });
    }

    internal static async Task<IResult> Login(LoginRequest req, HttpContext http, LocalLoginService local, SessionService sessions, IAuditWriter audit, CancellationToken ct)
    {
        var (id, name) = await local.AuthenticateAsync(req, ct);
        var principal = await sessions.CreateAsync(id, name, "local", http, ct);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.login", "user", id.ToString(), After: new { method = "local" }, ActorUserId: id), ct);
        return Results.Ok(new { userId = id, displayName = name });
    }

    internal static async Task<IResult> ChangePassword(ChangePasswordRequest req, ICurrentUser user, LocalLoginService local, IAccessService access, CancellationToken ct)
    {
        if (!user.IsAuthenticated) throw new UnauthenticatedException();
        await local.ChangeAsync(user.UserId, req, ct);
        access.Invalidate(user.UserId);
        return Results.NoContent();
    }

    internal static async Task<IResult> Logout(HttpContext http, ICurrentUser user, SessionService sessions, IAuditWriter audit, CancellationToken ct)
    {
        if (user.SessionId is { } sid) await sessions.RevokeAsync(sid, "logout", ct);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.logout", "user", user.UserId.ToString()), ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    internal static async Task<IResult> LogoutAll(HttpContext http, ICurrentUser user, SessionService sessions, Db db, IAuditWriter audit, CancellationToken ct)
    {
        var n = await sessions.RevokeAllAsync(db, user.UserId, "logout_all", ct);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.logout_all", "user", user.UserId.ToString(), After: new { sessions = n }), ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new { revokedSessions = n });
    }

    internal static async Task<IResult> Me(ICurrentUser user, Db db, IAccessService access, IBodyMembershipQuery memberships, IClock clock, IFeatureService features, CancellationToken ct)
    {
        var u = await db.QuerySingleAsync("""
            SELECT u.id, u.email, u.display_name_ar, u.display_name_en, u.job_title, u.preferred_language, u.preferred_calendar,
                   u.external_entity_id, e.name_ar AS entity_name, u.user_type, COALESCE(c.must_change, false) AS must_change
            FROM iam.users u LEFT JOIN org.external_entities e ON e.id = u.external_entity_id LEFT JOIN iam.local_credentials c ON c.user_id = u.id
            WHERE u.id = @id
            """, new { id = user.UserId }, r => new
        {
            id = r.GetGuid(0), email = r.GetString(1), displayNameAr = r.GetString(2), displayNameEn = r.Str("display_name_en"),
            jobTitle = r.Str("job_title"), language = r.GetString(5), calendar = r.GetString(6),
            externalEntityId = r.Uuid("external_entity_id"), externalEntityName = r.Str("entity_name"),
            userType = r.GetString(9), mustChangePassword = r.GetBoolean(10),
        }, ct) ?? throw new UnauthenticatedException();
        var bodyRoles = await memberships.GetActiveRolesAsync(user.UserId, clock.TodayLocal, ct);
        var systemRoles = await access.GetSystemRolesAsync(user.UserId, ct);
        return Results.Ok(new
        {
            user = u,
            systemRoles,
            bodyRoles,
            // مستخدم البوابة: ممثل جهة خارجية بلا عضوية داخلية؛ تُعرض له واجهة البوابة فقط
            portalUser = systemRoles.Contains(Roles.ExternalRepresentative) && u.userType == "external" && bodyRoles.Count == 0,
            portalEnabled = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct),
            permissions = (await access.GetPermissionsAsync(Scope.System, ct)).Order(),
        });
    }

    // ---------------- إدارة المستخدمين ----------------

    public sealed record CreateUserRequest(
        [property: Required, EmailAddress, MaxLength(200)] string Email,
        [property: Required, MaxLength(200)] string DisplayNameAr,
        [property: MaxLength(200)] string? DisplayNameEn,
        [property: MaxLength(200)] string? JobTitle,
        [property: OneOf("internal", "guest", "external", "local_breakglass")] string? UserType,
        Guid? OrgUnitId,
        Guid? ExternalEntityId,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Clearance,
        [property: OneOf(Roles.SystemAdmin, Roles.GovernanceOfficer, Roles.Executive, Roles.Auditor, Roles.ExternalRepresentative)] string[]? SystemRoles,
        [property: MinLength(12, ErrorMessage = "كلمة المرور 12 حرفًا على الأقل"), MaxLength(200)] string? InitialPassword) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if ((SystemRoles ?? []).Contains(Roles.ExternalRepresentative) && ExternalEntityId is null)
                yield return new ValidationResult("ممثل الجهة الخارجية يتطلب تحديد الجهة", [nameof(ExternalEntityId)]);
        }
    }

    public sealed record UpdateUserRequest(
        [property: MaxLength(200)] string? DisplayNameAr,
        [property: MaxLength(200)] string? DisplayNameEn,
        [property: MaxLength(200)] string? JobTitle,
        Guid? OrgUnitId,
        Guid? ExternalEntityId,
        [property: OneOf("public", "restricted", "secret", "top_secret")] string? Clearance,
        [property: OneOf("active", "disabled")] string? Status);

    public sealed record SetRolesRequest(
        [property: Required, OneOf(Roles.SystemAdmin, Roles.GovernanceOfficer, Roles.Executive, Roles.Auditor, Roles.ExternalRepresentative)] string[] Roles,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(5), MaxLength(500)] string Reason);

    public sealed record SetPasswordRequest(
        [property: Required, MinLength(12, ErrorMessage = "كلمة المرور 12 حرفًا على الأقل"), MaxLength(200)] string Password);

    private static object MapUser(System.Data.Common.DbDataReader r) => new
    {
        id = r.GetGuid(r.GetOrdinal("id")),
        email = r.Get<string>("email"),
        displayNameAr = r.Get<string>("display_name_ar"),
        displayNameEn = r.Str("display_name_en"),
        jobTitle = r.Str("job_title"),
        userType = r.Get<string>("user_type"),
        orgUnitId = r.Uuid("org_unit_id"),
        orgUnitName = r.Str("org_unit_name"),
        externalEntityId = r.Uuid("external_entity_id"),
        externalEntityName = r.Str("entity_name"),
        clearance = r.Get<string>("clearance"),
        status = r.Get<string>("status"),
        lastLoginAt = r.Time("last_login_at"),
        hasLocalPassword = r.Get<bool>("has_local_password"),
        systemRoles = r.Get<string[]>("system_roles"),
    };

    private const string UserSelect = """
        SELECT u.*, o.name_ar AS org_unit_name, e.name_ar AS entity_name,
               EXISTS (SELECT 1 FROM iam.local_credentials c WHERE c.user_id = u.id) AS has_local_password,
               COALESCE((SELECT array_agg(DISTINCT a.role_code ORDER BY a.role_code) FROM iam.system_role_assignments a
                         WHERE a.user_id = u.id AND a.valid_from <= (now() AT TIME ZONE 'Asia/Riyadh')::date AND (a.valid_to IS NULL OR a.valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date)), '{}') AS system_roles
        FROM iam.users u
        LEFT JOIN org.org_units o ON o.id = u.org_unit_id
        LEFT JOIN org.external_entities e ON e.id = u.external_entity_id
        """;

    internal static async Task<IResult> ListUsers(string? q, string? role, string? status, int? page, int? pageSize,
        Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { q = SqlText.Like(q), role, status, limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@q::text IS NULL OR u.email ILIKE @q OR u.display_name_ar ILIKE @q OR u.display_name_en ILIKE @q)
              AND (@status::text IS NULL OR u.status = @status)
              AND (@role::text IS NULL OR EXISTS (SELECT 1 FROM iam.system_role_assignments a WHERE a.user_id = u.id AND a.role_code = @role
                     AND (a.valid_to IS NULL OR a.valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date)))
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM iam.users u " + "\n" + where, args, ct);
        var items = await db.QueryAsync(UserSelect + "\n" + where + " ORDER BY u.display_name_ar LIMIT @limit OFFSET @offset", args, MapUser, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    internal static async Task<IResult> GetUser(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        return Results.Ok(await db.QuerySingleAsync(UserSelect + " WHERE u.id = @id", new { id }, MapUser, ct) ?? throw new NotFoundException());
    }

    internal static async Task<IResult> CreateUser(CreateUserRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("""
                INSERT INTO iam.users (id, email, display_name_ar, display_name_en, job_title, user_type, org_unit_id, external_entity_id, clearance, created_by)
                VALUES (@id, lower(@Email), @DisplayNameAr, @DisplayNameEn, @JobTitle, @type, @OrgUnitId, @ExternalEntityId, @clearance, @by)
                """, new
            {
                id, req.Email, req.DisplayNameAr, req.DisplayNameEn, req.JobTitle, type = req.UserType ?? "internal",
                req.OrgUnitId, req.ExternalEntityId, clearance = req.Clearance ?? "restricted", by = me.UserId,
            }, ct);
            foreach (var role in (req.SystemRoles ?? []).Distinct())
                await tx.ExecuteAsync("INSERT INTO iam.system_role_assignments (id, user_id, role_code, granted_by, reason) VALUES (@rid, @id, @role, @by, 'إنشاء المستخدم')",
                    new { rid = Guid.CreateVersion7(), id, role, by = me.UserId }, ct);
            if (req.InitialPassword is { } pw)
                await tx.ExecuteAsync("INSERT INTO iam.local_credentials (user_id, password_hash, must_change) VALUES (@id, @hash, true)",
                    new { id, hash = LocalLoginService.Hash(pw) }, ct);
            await audit.WriteAsync(tx, new AuditEntry("IAM", "user.created", "user", id.ToString(),
                After: new { req.Email, roles = req.SystemRoles, req.Clearance, req.UserType, localPassword = req.InitialPassword is not null }), ct);
        }, ct);
        return Results.Created($"/api/v1/users/{id}", await db.QuerySingleAsync(UserSelect + " WHERE u.id = @id", new { id }, MapUser, ct));
    }

    internal static async Task<IResult> UpdateUser(Guid id, UpdateUserRequest req, Db db, IAccessService access, ICurrentUser me,
        IAuditWriter audit, SessionService sessions, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        if (id == me.UserId && req.Status == "disabled") throw AppValidationException.For("status", "لا يمكنك تعطيل حسابك");
        await db.InTransactionAsync(async tx =>
        {
            var before = await tx.QuerySingleAsync("SELECT status, clearance, external_entity_id FROM iam.users WHERE id = @id FOR UPDATE",
                new { id }, r => new { status = r.GetString(0), clearance = r.GetString(1), externalEntityId = r.Uuid("external_entity_id") }, ct)
                ?? throw new NotFoundException();
            await tx.ExecuteAsync("""
                UPDATE iam.users SET
                  display_name_ar = COALESCE(@DisplayNameAr, display_name_ar),
                  display_name_en = COALESCE(@DisplayNameEn, display_name_en),
                  job_title = COALESCE(@JobTitle, job_title),
                  org_unit_id = COALESCE(@OrgUnitId, org_unit_id),
                  external_entity_id = COALESCE(@ExternalEntityId, external_entity_id),
                  clearance = COALESCE(@Clearance, clearance),
                  status = COALESCE(@Status, status),
                  updated_by = @by
                WHERE id = @id
                """, new { id, req.DisplayNameAr, req.DisplayNameEn, req.JobTitle, req.OrgUnitId, req.ExternalEntityId, req.Clearance, req.Status, by = me.UserId }, ct);
            if (req.Status == "disabled") await sessions.RevokeAllAsync(tx, id, "user_disabled", ct);
            await audit.WriteAsync(tx, new AuditEntry("IAM", "user.updated", "user", id.ToString(), Before: before, After: req), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(UserSelect + " WHERE u.id = @id", new { id }, MapUser, ct));
    }

    internal static async Task<IResult> SetRoles(Guid id, SetRolesRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        if (id == me.UserId && !req.Roles.Contains(Roles.SystemAdmin))
            throw AppValidationException.For("roles", "لا يمكنك إزالة دور مدير النظام عن نفسك");
        await db.InTransactionAsync(async tx =>
        {
            var current = await tx.QueryAsync("""
                SELECT role_code FROM iam.system_role_assignments
                WHERE user_id = @id AND (valid_to IS NULL OR valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date) FOR UPDATE
                """, new { id }, r => r.GetString(0), ct);
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM iam.users WHERE id = @id", new { id }, ct) == 0) throw new NotFoundException();
            if (req.Roles.Contains(Roles.ExternalRepresentative)
                && await tx.ScalarAsync<Guid?>("SELECT external_entity_id FROM iam.users WHERE id = @id", new { id }, ct) is null)
                throw AppValidationException.For("roles", "ممثل الجهة الخارجية يتطلب ربط المستخدم بجهة خارجية أولًا");
            var remove = current.Except(req.Roles).ToArray();
            var add = req.Roles.Except(current).Distinct().ToArray();
            if (remove.Length > 0)
                await tx.ExecuteAsync("""
                    UPDATE iam.system_role_assignments SET valid_to = (now() AT TIME ZONE 'Asia/Riyadh')::date - 1
                    WHERE user_id = @id AND role_code = ANY(@remove::text[]) AND (valid_to IS NULL OR valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
                    """, new { id, remove }, ct);
            foreach (var role in add)
                await tx.ExecuteAsync("INSERT INTO iam.system_role_assignments (id, user_id, role_code, granted_by, reason) VALUES (@rid, @id, @role, @by, @reason)",
                    new { rid = Guid.CreateVersion7(), id, role, by = me.UserId, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("IAM", "user.roles_changed", "user", id.ToString(),
                Before: new { roles = current }, After: new { roles = req.Roles, req.Reason }), ct);
        }, ct);
        access.Invalidate(id);
        return Results.Ok(await db.QuerySingleAsync(UserSelect + " WHERE u.id = @id", new { id }, MapUser, ct));
    }

    internal static async Task<IResult> SetPassword(Guid id, SetPasswordRequest req, Db db, IAccessService access, IAuditWriter audit,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminUsersManage, Scope.System, ct);
        if (!options.Value.LocalLogin.Enabled) throw new FeatureDisabledException("Authentication:LocalLogin");
        await db.InTransactionAsync(async tx =>
        {
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM iam.users WHERE id = @id", new { id }, ct) == 0) throw new NotFoundException();
            await tx.ExecuteAsync("""
                INSERT INTO iam.local_credentials (user_id, password_hash, must_change) VALUES (@id, @hash, true)
                ON CONFLICT (user_id) DO UPDATE SET password_hash = EXCLUDED.password_hash, failed_attempts = 0, locked_until = NULL,
                  password_set_at = now(), must_change = true
                """, new { id, hash = LocalLoginService.Hash(req.Password) }, ct);
            await audit.WriteAsync(tx, new AuditEntry("IAM", "user.password_set", "user", id.ToString()), ct);
        }, ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> ListRoles(Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.UsersDirectory, Scope.System, ct);
        var perms = await db.QueryAsync("SELECT role_code, permission_code FROM iam.role_permissions ORDER BY 1, 2", null,
            r => (Role: r.GetString(0), Perm: r.GetString(1)), ct);
        var roles = await db.QueryAsync("SELECT code, scope, name_ar, name_en FROM iam.roles ORDER BY sort_order", null, r => new
        {
            code = r.GetString(0), scope = r.GetString(1), nameAr = r.GetString(2), nameEn = r.GetString(3),
            permissions = perms.Where(p => p.Role == r.GetString(0)).Select(p => p.Perm).ToArray(),
        }, ct);
        return Results.Ok(roles);
    }

    internal static async Task<IResult> Directory(string? q, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.UsersDirectory, Scope.System, ct);
        var items = await db.QueryAsync("""
            SELECT id, email, display_name_ar, display_name_en, job_title FROM iam.users
            WHERE status = 'active' AND (@q::text IS NULL OR email ILIKE @q OR display_name_ar ILIKE @q OR display_name_en ILIKE @q)
            ORDER BY display_name_ar LIMIT 30
            """, new { q = SqlText.Like(q) }, r => new
        {
            id = r.GetGuid(0), email = r.GetString(1), displayNameAr = r.GetString(2), displayNameEn = r.Str("display_name_en"), jobTitle = r.Str("job_title"),
        }, ct);
        return Results.Ok(items);
    }
}

internal sealed class UserQuery(Db db) : IUserQuery
{
    public Task<UserSummary?> GetAsync(Guid userId, CancellationToken ct = default) =>
        db.QuerySingleAsync("SELECT id, email, display_name_ar, display_name_en, external_entity_id, status FROM iam.users WHERE id = @userId",
            new { userId }, r => new UserSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str("display_name_en"), r.Uuid("external_entity_id"), r.GetString(5)), ct);
}
