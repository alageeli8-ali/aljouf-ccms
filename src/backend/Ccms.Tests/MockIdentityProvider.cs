using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ccms.Tests;

/// <summary>مزوّد هوية OIDC وهمي داخل الاختبارات فقط، يحاكي Entra ID (discovery، authorize، token، jwks)
/// بتوقيع RS256 حقيقي. يثبت سلامة الموصل دون ادعاء دخول موحد فعلي مع المكتب.</summary>
public sealed class MockIdentityProvider : IAsyncDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _rogueKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, (string Nonce, string Challenge)> _codes = new();
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";
    public string ClientId { get; } = "ccms-test-client";
    public string ClientSecret { get; } = "ccms-test-secret";
    public string TenantId { get; } = "00000000-0000-0000-0000-00000000test";

    /// <summary>الهوية التي يعيدها المزوّد عند الدخول التالي.</summary>
    public (string Oid, string Email, string Name) NextUser { get; set; } = ("oid-1", "sso.user@jsdo.gov.sa", "مستخدم الدخول الموحد");
    public bool SignWithRogueKey { get; set; }
    public bool TamperNonce { get; set; }

    public async Task StartAsync()
    {
        var port = AppInstance.FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls(BaseUrl);
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = BaseUrl,
            ["authorization_endpoint"] = BaseUrl + "/authorize",
            ["token_endpoint"] = BaseUrl + "/token",
            ["jwks_uri"] = BaseUrl + "/jwks",
        }));
        _app.MapGet("/jwks", () =>
        {
            var p = _key.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", kid = "k1", use = "sig", n = B64(p.Modulus!), e = B64(p.Exponent!) } } });
        });
        _app.MapGet("/authorize", (HttpRequest r) =>
        {
            var q = r.Query;
            if (q["client_id"] != ClientId || q["code_challenge_method"] != "S256") return Results.BadRequest();
            var code = Guid.NewGuid().ToString("N");
            _codes[code] = (q["nonce"]!, q["code_challenge"]!);
            return Results.Redirect($"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"]!)}");
        });
        _app.MapPost("/token", async (HttpRequest r) =>
        {
            var f = await r.ReadFormAsync();
            if (f["client_id"] != ClientId || f["client_secret"] != ClientSecret) return Results.Unauthorized();
            if (!_codes.TryRemove(f["code"]!, out var c)) return Results.BadRequest(new { error = "invalid_grant" });
            if (B64(SHA256.HashData(Encoding.ASCII.GetBytes(f["code_verifier"]!))) != c.Challenge) return Results.BadRequest(new { error = "invalid_grant", detail = "pkce" });
            return Results.Json(new { id_token = IdToken(TamperNonce ? "wrong-nonce" : c.Nonce), token_type = "Bearer" });
        });
        await _app.StartAsync();
    }

    private string IdToken(string nonce)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = "k1" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = BaseUrl, ["aud"] = ClientId, ["exp"] = now + 600, ["iat"] = now, ["nbf"] = now, ["nonce"] = nonce,
            ["oid"] = NextUser.Oid, ["sub"] = NextUser.Oid, ["email"] = NextUser.Email, ["name"] = NextUser.Name, ["tid"] = TenantId,
        }));
        var key = SignWithRogueKey ? _rogueKey : _key;
        var sig = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(sig)}";
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _key.Dispose();
        _rogueKey.Dispose();
    }
}
