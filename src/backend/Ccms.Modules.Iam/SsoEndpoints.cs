using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Iam;

/// <summary>نقاط الدخول الموحد خارج /api (إعادة توجيه المتصفح). محكومة بـ SSO_ENABLED على الخادم،
/// ومستقلة عن جاهزية المزوّد: المفتاح المفعّل مع مزوّد غير مضبوط يعطي 503 لا دخولًا وهميًا.</summary>
public static class SsoEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth/sso").WithTags("Auth").AllowAnonymous();
        g.MapGet("/login", Login);
        g.MapGet("/callback", Callback);
    }

    private static string RedirectUri(HttpContext http, EntraOptions o) => $"{http.Request.Scheme}://{http.Request.Host}{o.CallbackPath}";

    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    private static async Task<IResult> Login(string? returnUrl, HttpContext http, IFeatureService features, IOptions<AuthOptions> options,
        EntraSsoConnector sso, Db db, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.SsoEnabled, ct);
        if (!options.Value.Entra.IsConfigured) throw new ProviderUnavailableException(EntraSsoConnector.ConnectorCode, "not_configured");
        try
        {
            return Results.Redirect(await sso.BuildAuthorizeUrlAsync(db, RedirectUri(http, options.Value.Entra), SafeReturnUrl(returnUrl), ct));
        }
        catch (HttpRequestException e)
        {
            throw new ProviderUnavailableException(EntraSsoConnector.ConnectorCode, "failing: " + e.Message);
        }
    }

    private static async Task<IResult> Callback(string? code, string? state, string? error, HttpContext http, IFeatureService features,
        IOptions<AuthOptions> options, EntraSsoConnector sso, Db db, SessionService sessions, IAuditWriter audit, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.SsoEnabled, ct);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Results.Redirect("/login?error=sso_denied");

        var identity = await sso.CompleteAsync(db, code, state, RedirectUri(http, options.Value.Entra), ct);
        var user = await db.QuerySingleAsync("""
            SELECT id, display_name_ar, status FROM iam.users
            WHERE entra_object_id = @oid OR (entra_object_id IS NULL AND lower(email) = lower(@email))
            ORDER BY (entra_object_id = @oid) DESC NULLS LAST LIMIT 1
            """, new { oid = identity.ObjectId, email = identity.Email }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Status: r.GetString(2)), ct);

        if (user == default)
        {
            if (!options.Value.Entra.AutoProvisionUsers)
            {
                await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", null, After: new { method = "sso", reason = "not_provisioned", identity.Email }, Outcome: "denied"), ct);
                return Results.Redirect("/login?error=sso_not_provisioned");
            }
            // إنشاء تلقائي بلا أي صلاحية حتى تُسند له عضوية أو دور (FR-IAM-02)
            var id = Guid.CreateVersion7();
            await db.ExecuteAsync("INSERT INTO iam.users (id, entra_object_id, email, display_name_ar) VALUES (@id, @oid, lower(@email), @name)",
                new { id, oid = identity.ObjectId, email = identity.Email, name = identity.Name }, ct);
            user = (id, identity.Name, "active");
        }
        else
        {
            await db.ExecuteAsync("UPDATE iam.users SET entra_object_id = @oid WHERE id = @id AND entra_object_id IS NULL", new { oid = identity.ObjectId, id = user.Id }, ct);
        }
        if (user.Status != "active")
        {
            await audit.WriteAsync(new AuditEntry("IAM", "auth.login_failed", "user", user.Id.ToString(), After: new { method = "sso", reason = "disabled" }, Outcome: "denied"), ct);
            return Results.Redirect("/login?error=account_disabled");
        }
        var principal = await sessions.CreateAsync(user.Id, user.Name, "sso", http, ct);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await audit.WriteAsync(new AuditEntry("IAM", "auth.login", "user", user.Id.ToString(), After: new { method = "sso" }, ActorUserId: user.Id), ct);
        return Results.Redirect(SafeReturnUrl(identity.ReturnUrl));
    }
}
