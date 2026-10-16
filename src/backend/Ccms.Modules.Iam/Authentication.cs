using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Iam;

public sealed class AuthOptions
{
    public const string Section = "Authentication";
    public LocalLoginOptions LocalLogin { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public EntraOptions Entra { get; set; } = new();
}

public sealed class LocalLoginOptions
{
    /// <summary>الدخول بكلمة مرور محلية. مفعّل في التطوير والاختبار؛ في الإنتاج لحساب الطوارئ فقط (FR-IAM-07).</summary>
    public bool Enabled { get; set; }
    public bool BreakGlassOnly { get; set; }
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

public sealed class SessionOptions
{
    public int IdleMinutes { get; set; } = 30;
    public int MaxHours { get; set; } = 10;
}

public sealed class EntraOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string CallbackPath { get; set; } = "/auth/sso/callback";
    /// <summary>للاختبار فقط: عنوان مزوّد هوية بديل (مثل مزوّد وهمي داخل الاختبارات). يُرفض في الإنتاج.</summary>
    public string AuthorityOverride { get; set; } = "";
    public bool AutoProvisionUsers { get; set; } = true;
    public string[] AllowedEmailDomains { get; set; } = [];

    public bool IsConfigured => TenantId.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0;

    public string Authority => AuthorityOverride.Length > 0
        ? AuthorityOverride.TrimEnd('/')
        : $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";
}

internal static class ClaimNames
{
    public const string UserId = "ccms:uid", SessionId = "ccms:sid", Name = "ccms:name";
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && Principal.FindFirst(ClaimNames.UserId) is not null;
    public Guid UserId => Guid.TryParse(Principal?.FindFirst(ClaimNames.UserId)?.Value, out var id) ? id : Guid.Empty;
    public Guid? SessionId => Guid.TryParse(Principal?.FindFirst(ClaimNames.SessionId)?.Value, out var id) ? id : null;
    public string DisplayName => Principal?.FindFirst(ClaimNames.Name)?.Value ?? "anonymous";
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string CorrelationId => accessor.HttpContext?.TraceIdentifier ?? "";
}

/// <summary>الجلسات في القاعدة (iam.sessions): الخروج من كل الأجهزة، ومهلة الخمول، والحد الأقصى، وإيقاف المستخدم المعطّل فورًا.</summary>
internal sealed class SessionService(Db db, IClock clock, IOptions<AuthOptions> options)
{
    public async Task<ClaimsPrincipal> CreateAsync(Guid userId, string displayName, string method, HttpContext http, CancellationToken ct)
    {
        var sid = Guid.CreateVersion7();
        var ua = http.Request.Headers.UserAgent.ToString();
        await db.ExecuteAsync("""
            INSERT INTO iam.sessions (id, user_id, auth_method, expires_at, ip, user_agent_hash)
            VALUES (@sid, @userId, @method, @expires, @ip, @ua);
            """, new
        {
            sid, userId, method,
            expires = clock.UtcNow.AddHours(options.Value.Session.MaxHours),
            ip = http.Connection.RemoteIpAddress?.ToString(),
            ua = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ua)))[..16],
        }, ct);
        await db.ExecuteAsync("UPDATE iam.users SET last_login_at = now() WHERE id = @userId", new { userId }, ct);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimNames.UserId, userId.ToString()),
            new Claim(ClaimNames.SessionId, sid.ToString()),
            new Claim(ClaimNames.Name, displayName),
        ], CookieAuthenticationDefaults.AuthenticationScheme, ClaimNames.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    public async Task<bool> ValidateAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var idle = options.Value.Session.IdleMinutes;
        var row = await db.QuerySingleAsync("""
            SELECT s.last_seen_at, s.expires_at, s.revoked_at, u.status
            FROM iam.sessions s JOIN iam.users u ON u.id = s.user_id
            WHERE s.id = @sessionId AND s.user_id = @userId
            """, new { sessionId, userId }, r => new
        {
            LastSeen = r.GetFieldValue<DateTime>(0),
            Expires = r.GetFieldValue<DateTime>(1),
            Revoked = r.Time("revoked_at"),
            Status = r.GetString(3),
        }, ct);
        var now = clock.UtcNow;
        if (row is null || row.Revoked is not null || row.Status != "active" || row.Expires <= now || row.LastSeen.AddMinutes(idle) <= now)
            return false;
        if (now - row.LastSeen > TimeSpan.FromMinutes(1))
            await db.ExecuteAsync("UPDATE iam.sessions SET last_seen_at = now() WHERE id = @sessionId", new { sessionId }, ct);
        return true;
    }

    public Task RevokeAsync(Guid sessionId, string reason, CancellationToken ct) =>
        db.ExecuteAsync("UPDATE iam.sessions SET revoked_at = now(), revoked_reason = @reason WHERE id = @sessionId AND revoked_at IS NULL",
            new { sessionId, reason }, ct);

    public Task<int> RevokeAllAsync(ISql sql, Guid userId, string reason, CancellationToken ct) =>
        sql.ExecuteAsync("UPDATE iam.sessions SET revoked_at = now(), revoked_reason = @reason WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId, reason }, ct);
}

internal sealed class SessionValidationEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var p = context.Principal;
        if (p is null
            || !Guid.TryParse(p.FindFirst(ClaimNames.SessionId)?.Value, out var sid)
            || !Guid.TryParse(p.FindFirst(ClaimNames.UserId)?.Value, out var uid)
            || !await context.HttpContext.RequestServices.GetRequiredService<SessionService>().ValidateAsync(sid, uid, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    // واجهة برمجية: 401/403 بصيغة ProblemDetails بدل إعادة التوجيه
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

public sealed record ChangePasswordRequest(
    [property: Required(ErrorMessage = "كلمة المرور الحالية مطلوبة"), MaxLength(200)] string CurrentPassword,
    [property: Required(ErrorMessage = "كلمة المرور الجديدة مطلوبة"), MinLength(10, ErrorMessage = "عشرة أحرف على الأقل"), MaxLength(200)] string NewPassword) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!NewPassword.Any(char.IsLetter) || !NewPassword.Any(char.IsDigit) || NewPassword.All(char.IsLetterOrDigit))
            yield return new("كلمة المرور يجب أن تجمع حروفًا وأرقامًا ورمزًا", [nameof(NewPassword)]);
        if (NewPassword == CurrentPassword) yield return new("اختر كلمة مرور مختلفة عن الحالية", [nameof(NewPassword)]);
    }
}

public sealed record LoginRequest(
    [property: Required(ErrorMessage = "البريد مطلوب"), EmailAddress(ErrorMessage = "بريد غير صالح"), MaxLength(200)] string Email,
    [property: Required(ErrorMessage = "كلمة المرور مطلوبة"), MaxLength(200)] string Password);

internal sealed class LocalLoginService(Db db, IClock clock, IOptions<AuthOptions> options, IAuditWriter audit, IFeatureService features)
{
    internal static readonly PasswordHasher<object> Hasher = new();

    public static string Hash(string password) => Hasher.HashPassword(null!, password);

    /// <summary>تغيير كلمة المرور ذاتيًا (إلزامي لحسابات الجهات الخارجية بعد كلمة المرور المؤقتة).</summary>
    public async Task ChangeAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct)
    {
        var hash = await db.ScalarAsync<string>("SELECT password_hash FROM iam.local_credentials WHERE user_id = @userId", new { userId }, ct)
            ?? throw new ConflictException("no-local-password", "الحساب يدخل بالدخول الموحد؛ لا كلمة مرور محلية");
        if (Hasher.VerifyHashedPassword(null!, hash, req.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            await audit.WriteAsync(new AuditEntry("IAM", "auth.password_change_failed", "user", userId.ToString(), Outcome: "denied"), ct);
            throw AppValidationException.For("currentPassword", "كلمة المرور الحالية غير صحيحة");
        }
        await db.ExecuteAsync("UPDATE iam.local_credentials SET password_hash = @h, must_change = false, password_set_at = now() WHERE user_id = @userId",
            new { userId, h = Hash(req.NewPassword) }, ct);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.password_changed", "user", userId.ToString()), ct);
    }

    /// <summary>يعيد المستخدم عند النجاح؛ ويطبق القفل بعد المحاولات الفاشلة. الرسالة موحدة حتى لا تكشف وجود الحساب.</summary>
    public async Task<(Guid Id, string Name)> AuthenticateAsync(LoginRequest req, CancellationToken ct)
    {
        var o = options.Value.LocalLogin;
        if (!o.Enabled) throw new FeatureDisabledException("Authentication:LocalLogin");
        var row = await db.QuerySingleAsync("""
            SELECT u.id, u.display_name_ar, u.status, u.user_type, c.password_hash, c.failed_attempts, c.locked_until
            FROM iam.users u JOIN iam.local_credentials c ON c.user_id = u.id
            WHERE lower(u.email) = lower(@email)
            """, new { email = req.Email }, r => new
        {
            Id = r.GetGuid(0), Name = r.GetString(1), Status = r.GetString(2), Type = r.GetString(3),
            Hash = r.GetString(4), Failed = r.GetInt32(5), LockedUntil = r.Time("locked_until"),
        }, ct);

        var invalid = new UnauthenticatedException("بيانات الدخول غير صحيحة");
        if (row is null)
        {
            Hasher.VerifyHashedPassword(null!, Hash("timing-equalizer"), req.Password); // توحيد زمن الاستجابة
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", null, After: new { reason = "unknown_account" }, Outcome: "denied"), ct);
            throw invalid;
        }
        // حسابات الجهات الخارجية ليست في دليل المكتب: تدخل محليًا عبر البوابة حين تكون مفعلة فقط
        var portalEnabled = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        var externalPortalUser = row.Type == "external" && await db.ScalarAsync<long>("""
            SELECT count(*) FROM bodies.body_members WHERE user_id = @id AND status = 'active' AND (end_date IS NULL OR end_date >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
            """, new { id = row.Id }, ct) == 0;
        if (o.BreakGlassOnly && row.Type != "local_breakglass" && !(externalPortalUser && portalEnabled))
        {
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", row.Id.ToString(), After: new { reason = "local_login_restricted" }, Outcome: "denied"), ct);
            throw invalid;
        }
        if (row.LockedUntil is { } until && until > clock.UtcNow)
        {
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", row.Id.ToString(), After: new { reason = "locked" }, Outcome: "denied"), ct);
            throw new ConflictException("account-locked", "الحساب مقفل مؤقتًا بسبب محاولات دخول فاشلة متكررة",
                new Dictionary<string, object?> { ["lockedUntil"] = until });
        }
        var ok = Hasher.VerifyHashedPassword(null!, row.Hash, req.Password) != PasswordVerificationResult.Failed;
        if (!ok || row.Status != "active")
        {
            var failed = row.Failed + 1;
            DateTime? lockUntil = failed >= o.MaxFailedAttempts ? clock.UtcNow.AddMinutes(o.LockoutMinutes) : null;
            await db.ExecuteAsync("UPDATE iam.local_credentials SET failed_attempts = @failed, locked_until = @lockUntil WHERE user_id = @id",
                new { failed = lockUntil is null ? failed : 0, lockUntil, id = row.Id }, ct);
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", row.Id.ToString(),
                After: new { reason = ok ? "disabled" : "bad_password", locked = lockUntil is not null }, Outcome: "denied"), ct);
            throw invalid;
        }
        if (externalPortalUser && !portalEnabled)
        {
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", row.Id.ToString(), After: new { reason = "external_portal_disabled" }, Outcome: "denied"), ct);
            throw new AccessDeniedException("portal-disabled", "بوابة الجهات الخارجية غير متاحة حاليًا؛ تواصل مع مسؤول المتابعة في المكتب عبر القنوات الرسمية");
        }
        await db.ExecuteAsync("UPDATE iam.local_credentials SET failed_attempts = 0, locked_until = NULL WHERE user_id = @id", new { id = row.Id }, ct);
        return (row.Id, row.Name);
    }
}

/// <summary>إعادة التحقق من الهوية قبل التوقيع: كلمة المرور المحلية، وتُحسب المحاولة الفاشلة ضمن قفل الحساب.</summary>
internal sealed class Reauthenticator(Db db, IAuditWriter audit) : IReauthenticator
{
    public async Task<bool?> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var row = await db.QuerySingleAsync("SELECT password_hash, locked_until FROM iam.local_credentials WHERE user_id = @userId", new { userId },
            r => (Hash: r.GetString(0), LockedUntil: r.IsDBNull(1) ? (DateTime?)null : r.GetDateTime(1)), ct);
        if (row == default) return null;
        if (row.LockedUntil is { } until && until > DateTime.UtcNow) return false;
        var ok = LocalLoginService.Hasher.VerifyHashedPassword(null!, row.Hash, password) != PasswordVerificationResult.Failed;
        if (ok) return true;
        await db.ExecuteAsync("""
            UPDATE iam.local_credentials SET failed_attempts = failed_attempts + 1,
              locked_until = CASE WHEN failed_attempts + 1 >= 5 THEN now() + interval '15 minutes' ELSE locked_until END
            WHERE user_id = @userId
            """, new { userId }, ct);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.reauth_failed", "user", userId.ToString(), Outcome: "denied"), ct);
        return false;
    }
}
