using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Iam;

/// <summary>موصل الدخول الموحد عبر Microsoft Entra ID (OIDC Authorization Code + PKCE) بنمط BFF (ADR-004).
/// لا يُعد الدخول الموحد «فعليًا» إلا بعد ضبط بيانات تسجيل التطبيق ونجاح فحص الموصل؛ حتى ذلك الحين حالته not_configured.</summary>
internal sealed class EntraSsoConnector(IOptions<AuthOptions> options, IHttpClientFactory http, IMemoryCache cache) : IConnector
{
    public const string ConnectorCode = "sso";
    public string Code => ConnectorCode;
    public string NameAr => "Microsoft Entra ID / Active Directory";
    public string NameEn => "Microsoft Entra ID / Active Directory";

    private EntraOptions O => options.Value.Entra;

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        if (!O.IsConfigured)
            return new ConnectorHealth(ConnectorState.NotConfigured, "بيانات تسجيل التطبيق في Entra ID غير مضبوطة (TenantId, ClientId, ClientSecret)");
        var sw = Stopwatch.StartNew();
        try
        {
            cache.Remove(DiscoveryCacheKey);
            var d = await GetDiscoveryAsync(ct);
            return new ConnectorHealth(ConnectorState.Healthy, $"المصدر: {d.Issuer}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            return new ConnectorHealth(ConnectorState.Failing, $"تعذر الوصول إلى مزوّد الهوية: {e.Message}", (int)sw.ElapsedMilliseconds);
        }
    }

    public ConnectorDescriptor Describe() => new(Code, NameAr, NameEn, FeatureKeys.SsoEnabled, "oidc_authorization_code",
    [
        new("Authentication:Entra:TenantId", "معرّف المستأجر", "Tenant id", O.TenantId.Length > 0),
        new("Authentication:Entra:ClientId", "معرّف تطبيق تسجيل الدخول", "Sign-in app client id", O.ClientId.Length > 0),
        new("Authentication:Entra:ClientSecret", "سر تطبيق تسجيل الدخول (من بيئة الخادم)", "Sign-in app secret (server environment)", O.ClientSecret.Length > 0, true),
        new("Authentication:Entra:AllowedEmailDomains", "نطاقات البريد المسموح لها", "Allowed email domains", O.AllowedEmailDomains.Length > 0),
    ],
    "تسجيل الدخول الموحد (OIDC). مزامنة الدليل موصل مستقل (directory) بمفتاح مستقل؛ تعطيل SSO لا يمس الحسابات المحلية الطارئة.",
    "Single sign-on (OIDC). Directory sync is a separate connector (directory) with its own switch; disabling SSO leaves local break-glass accounts intact.");

    private string DiscoveryCacheKey => "sso:discovery:" + O.Authority;

    internal sealed record Discovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);

    internal async Task<Discovery> GetDiscoveryAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(DiscoveryCacheKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var client = http.CreateClient("sso");
            using var doc = JsonDocument.Parse(await client.GetStringAsync(O.Authority + "/.well-known/openid-configuration", ct));
            var root = doc.RootElement;
            return new Discovery(
                root.GetProperty("issuer").GetString()!,
                root.GetProperty("authorization_endpoint").GetString()!,
                root.GetProperty("token_endpoint").GetString()!,
                root.GetProperty("jwks_uri").GetString()!);
        }))!;

    private async Task<JsonElement> GetJwksAsync(string uri, bool refresh, CancellationToken ct)
    {
        var key = "sso:jwks:" + uri;
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            var json = await http.CreateClient("sso").GetStringAsync(uri, ct);
            return JsonDocument.Parse(json).RootElement.Clone();
        }));
    }

    public async Task<string> BuildAuthorizeUrlAsync(Db db, string redirectUri, string returnUrl, CancellationToken ct)
    {
        var d = await GetDiscoveryAsync(ct);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        await db.ExecuteAsync("DELETE FROM iam.sso_requests WHERE created_at < now() - interval '15 minutes'", null, ct);
        await db.ExecuteAsync("INSERT INTO iam.sso_requests (state, nonce, code_verifier, return_url) VALUES (@state, @nonce, @verifier, @returnUrl)",
            new { state, nonce, verifier, returnUrl }, ct);
        var q = new Dictionary<string, string>
        {
            ["client_id"] = O.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return d.AuthorizationEndpoint + "?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    internal sealed record SsoIdentity(string ObjectId, string Email, string Name, string ReturnUrl);

    public async Task<SsoIdentity> CompleteAsync(Db db, string code, string state, string redirectUri, CancellationToken ct)
    {
        var req = await db.QuerySingleAsync("DELETE FROM iam.sso_requests WHERE state = @state AND created_at > now() - interval '15 minutes' RETURNING nonce, code_verifier, return_url",
            new { state }, r => (Nonce: r.GetString(0), Verifier: r.GetString(1), ReturnUrl: r.GetString(2)), ct);
        if (req == default) throw new UnauthenticatedException("طلب الدخول الموحد منتهٍ أو غير معروف");

        var d = await GetDiscoveryAsync(ct);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = O.ClientId,
            ["client_secret"] = O.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = req.Verifier,
        });
        var resp = await http.CreateClient("sso").PostAsync(d.TokenEndpoint, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new UnauthenticatedException("رفض مزوّد الهوية تبادل الرمز");
        using var tokenDoc = JsonDocument.Parse(body);
        var idToken = tokenDoc.RootElement.GetProperty("id_token").GetString()!;
        var claims = await ValidateIdTokenAsync(idToken, d, req.Nonce, ct);

        string Claim(string name) => claims.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var oid = Claim("oid") is { Length: > 0 } o ? o : Claim("sub");
        var email = Claim("email") is { Length: > 0 } e ? e : Claim("preferred_username");
        if (O.AllowedEmailDomains.Length > 0 && !O.AllowedEmailDomains.Any(dom => email.EndsWith("@" + dom, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthenticatedException("نطاق البريد غير مسموح");
        return new SsoIdentity(oid, email, Claim("name") is { Length: > 0 } n ? n : email, req.ReturnUrl);
    }

    /// <summary>التحقق من رمز الهوية: الخوارزمية RS256، والتوقيع بمفتاح JWKS، والمصدر، والجمهور، والصلاحية الزمنية، والـ nonce.</summary>
    internal async Task<JsonElement> ValidateIdTokenAsync(string jwt, Discovery d, string nonce, CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new UnauthenticatedException("رمز هوية غير صالح");
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new UnauthenticatedException("خوارزمية توقيع غير مسموحة");
        var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;

        JsonElement? FindKey(JsonElement jwks) => jwks.GetProperty("keys").EnumerateArray()
            .Where(x => x.GetProperty("kty").GetString() == "RSA" && (kid is null || (x.TryGetProperty("kid", out var xk) && xk.GetString() == kid)))
            .Select(x => (JsonElement?)x).FirstOrDefault();

        var key = FindKey(await GetJwksAsync(d.JwksUri, false, ct)) ?? FindKey(await GetJwksAsync(d.JwksUri, true, ct))
            ?? throw new UnauthenticatedException("مفتاح توقيع غير معروف");
        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = FromBase64Url(key.GetProperty("n").GetString()!),
            Exponent = FromBase64Url(key.GetProperty("e").GetString()!),
        });
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new UnauthenticatedException("توقيع رمز الهوية غير صالح");

        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement.Clone();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const int skew = 300;
        var tid = payload.TryGetProperty("tid", out var t) ? t.GetString() : null;
        var expectedIssuer = d.Issuer.Replace("{tenantid}", tid ?? "");
        if (payload.GetProperty("iss").GetString() != expectedIssuer) throw new UnauthenticatedException("مصدر رمز الهوية غير مطابق");
        var aud = payload.GetProperty("aud");
        var audOk = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Any(a => a.GetString() == O.ClientId) : aud.GetString() == O.ClientId;
        if (!audOk) throw new UnauthenticatedException("جمهور رمز الهوية غير مطابق");
        if (payload.GetProperty("exp").GetInt64() + skew < now) throw new UnauthenticatedException("رمز الهوية منتهي الصلاحية");
        if (payload.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() - skew > now) throw new UnauthenticatedException("رمز الهوية غير ساري بعد");
        if (!payload.TryGetProperty("nonce", out var n) || n.GetString() != nonce) throw new UnauthenticatedException("nonce غير مطابق");
        return payload;
    }

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
