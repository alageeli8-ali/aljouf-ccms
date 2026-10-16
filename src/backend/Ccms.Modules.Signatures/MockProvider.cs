using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;
using Ccms.Modules.Signatures.Crypto;
using Ccms.Modules.Signatures.Pdf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Signatures;

public sealed class SignatureOptions
{
    public const string Section = "Signatures";
    /// <summary>سر توقيع الاستدعاءات الراجعة للمزوّد التجريبي. لا يُكتب في أي سجل.</summary>
    public string MockCallbackSecret { get; set; } = "";
    public int RetryMaxAttempts { get; set; } = 5;
    public int ReauthMinutes { get; set; } = 5;
    public int WorkerIntervalSeconds { get; set; } = 30;
    /// <summary>ملفات PEM لجهات الثقة المعتمدة للتحقق من الشهادات (تُضبط عند التعاقد مع مزوّد معتمد).</summary>
    public string[] TrustedRootPemFiles { get; set; } = [];
}

/// <summary>أعطال المزوّد التجريبي المحقونة لاختبار الانقطاع وإعادة المحاولة (غير متاحة في الإنتاج).</summary>
public sealed class MockFaults
{
    private int _failNext;
    public volatile bool Unavailable;
    public int FailNext { get => Volatile.Read(ref _failNext); set => Volatile.Write(ref _failNext, value); }
    public void ThrowIfDown()
    {
        if (Unavailable) throw new SignatureProviderUnavailableException("المزوّد التجريبي غير متاح (انقطاع محاكى)");
        while (true)
        {
            var n = Volatile.Read(ref _failNext);
            if (n <= 0) return;
            if (Interlocked.CompareExchange(ref _failNext, n - 1, n) == n) throw new SignatureProviderUnavailableException("انقطع الاتصال بالمزوّد التجريبي (محاكاة)");
        }
    }
}

/// <summary>مزوّد تجريبي للاختبار فقط — ليس خدمة توقيع إنتاجية. يحاكي ما يفعله مزوّد خارجي: مغلفات بموقّعين وتسلسل، وصفحة توقيع،
/// واستدعاءات راجعة موقعة بـ HMAC، ونسخة نهائية موقعة رقميًا بشهادات اختبارية، وتوقيع بصمات عن بعد. ممنوع في الإنتاج.</summary>
internal sealed class MockSignatureProvider(Db db, MockFaults faults, IOptions<SignatureOptions> options, IHostEnvironment env) : ISignatureProvider
{
    private static X509Certificate2? _root;
    private static readonly SemaphoreSlim RootLock = new(1, 1);

    public string Code => "mock";
    public string NameAr => "مزوّد تجريبي (للاختبار فقط — ليس خدمة توقيع إنتاجية)";
    public bool Supports(string signatureType) => signatureType is SignatureTypes.ProviderESign or SignatureTypes.PdfDigitalSignature;

    public ProviderReadiness Readiness() => env.IsProduction()
        ? new(true, false, false, "esign_mock_blocked_in_production")
        : new(true, true, false, "mock_test_only");

    private void Guard()
    {
        if (env.IsProduction()) throw new SignatureProviderNotConfiguredException("المزوّد التجريبي ممنوع في بيئة الإنتاج");
        faults.ThrowIfDown();
    }

    // ---------------- الشهادات الاختبارية ----------------

    public async Task<X509Certificate2> RootAsync(CancellationToken ct)
    {
        if (_root is not null) return _root;
        await RootLock.WaitAsync(ct);
        try
        {
            if (_root is not null) return _root;
            var stored = await db.ScalarAsync<string>("SELECT pfx_base64 FROM sig.mock_keys WHERE name = 'root'", null, ct);
            if (stored is null)
            {
                var created = TestCertificates.CreateRoot();
                await db.ExecuteAsync("INSERT INTO sig.mock_keys (name, pfx_base64) VALUES ('root', @pfx) ON CONFLICT DO NOTHING",
                    new { pfx = Convert.ToBase64String(created.Export(X509ContentType.Pfx)) }, ct);
                stored = await db.ScalarAsync<string>("SELECT pfx_base64 FROM sig.mock_keys WHERE name = 'root'", null, ct);
            }
            return _root = TestCertificates.Reload(Convert.FromBase64String(stored!));
        }
        finally { RootLock.Release(); }
    }

    public async Task<X509Certificate2> SignerCertificateAsync(Guid userId, string name, CancellationToken ct)
    {
        var key = "signer:" + userId;
        var stored = await db.ScalarAsync<string>("SELECT pfx_base64 FROM sig.mock_keys WHERE name = @key", new { key }, ct);
        if (stored is null)
        {
            var cert = TestCertificates.IssueSigner(await RootAsync(ct), name, userId.ToString());
            await db.ExecuteAsync("INSERT INTO sig.mock_keys (name, pfx_base64) VALUES (@key, @pfx) ON CONFLICT DO NOTHING",
                new { key, pfx = Convert.ToBase64String(cert.Export(X509ContentType.Pfx)) }, ct);
            stored = await db.ScalarAsync<string>("SELECT pfx_base64 FROM sig.mock_keys WHERE name = @key", new { key }, ct);
        }
        return TestCertificates.Reload(Convert.FromBase64String(stored!));
    }

    public IReadOnlyCollection<X509Certificate2> TestRoots() => _root is null ? [] : [_root];

    // ---------------- المغلفات ----------------

    public async Task<ProviderEnvelope> CreateEnvelopeAsync(EnvelopeRequest r, CancellationToken ct)
    {
        Guard();
        await RootAsync(ct);
        var providerRef = "MOCK-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var signers = new JsonArray();
        var refs = new Dictionary<string, string>();
        foreach (var s in r.Signers.OrderBy(s => s.Order))
        {
            var sref = "S" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            refs[s.ExternalId] = sref;
            signers.Add(new JsonObject
            {
                ["ref"] = sref, ["userId"] = s.UserId, ["name"] = s.Name, ["order"] = s.Order, ["status"] = "pending",
                ["token"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            });
        }
        var b64 = Convert.ToBase64String(r.Document);
        await db.ExecuteAsync("""
            INSERT INTO sig.mock_envelopes (provider_ref, correlation_id, reference, original_base64, original_sha256, current_base64, sequential, signers, status, expires_at)
            VALUES (@providerRef, @CorrelationId, @Reference, @b64, @DocumentSha256, @b64, @Sequential, @signers::jsonb, 'sent', @ExpiresAt)
            """, new { providerRef, r.CorrelationId, r.Reference, b64, r.DocumentSha256, r.Sequential, signers = signers.ToJsonString(), r.ExpiresAt }, ct);
        return new ProviderEnvelope(providerRef, refs);
    }

    public async Task<string> SigningUrlAsync(string providerRef, string signerRef, CancellationToken ct)
    {
        Guard();
        var env0 = await LoadAsync(providerRef, ct) ?? throw new SignatureProviderUnavailableException("المغلف غير موجود لدى المزوّد");
        var token = env0.Signers.FirstOrDefault(s => s!["ref"]!.GetValue<string>() == signerRef)?["token"]?.GetValue<string>()
            ?? throw new SignatureProviderUnavailableException("الموقّع غير موجود في المغلف");
        return "/esign/mock/" + token;
    }

    public async Task<ProviderSignedDocument> DownloadSignedAsync(string providerRef, CancellationToken ct)
    {
        Guard();
        var e = await LoadAsync(providerRef, ct) ?? throw new SignatureProviderUnavailableException("المغلف غير موجود لدى المزوّد");
        var verification = new JsonObject
        {
            ["provider"] = "mock", ["productionGrade"] = false, ["notice"] = "مزوّد تجريبي — ليس خدمة توقيع إنتاجية",
            ["envelope"] = providerRef, ["status"] = e.Status, ["originalSha256"] = e.OriginalSha256,
            ["signers"] = new JsonArray(e.Signers.Select(s => (JsonNode)new JsonObject
            {
                ["ref"] = s!["ref"]!.GetValue<string>(), ["status"] = s["status"]!.GetValue<string>(), ["signedAt"] = s["signedAt"]?.GetValue<string>(),
            }).ToArray()),
        };
        return new ProviderSignedDocument(Convert.FromBase64String(e.CurrentBase64), verification.ToJsonString());
    }

    public async Task CancelAsync(string providerRef, string reason, CancellationToken ct)
    {
        Guard();
        await db.ExecuteAsync("UPDATE sig.mock_envelopes SET status = 'cancelled', updated_at = now() WHERE provider_ref = @providerRef AND status = 'sent'", new { providerRef }, ct);
    }

    public async Task<RemoteSignature> SignDigestAsync(RemoteSignRequest r, CancellationToken ct)
    {
        Guard();
        var root = await RootAsync(ct);
        var cert = await SignerCertificateAsync(r.UserId, r.SignerName, ct);
        return new RemoteSignature(CmsBuilder.SignDetached(r.Sha256Digest, cert, [cert, root], r.SigningTimeUtc), cert);
    }

    // ---------------- الاستدعاءات الراجعة (HMAC-SHA256) ----------------

    public const string SignatureHeader = "X-Mock-Signature";

    private byte[] Secret() => Encoding.UTF8.GetBytes(options.Value.MockCallbackSecret.Length >= 16 ? options.Value.MockCallbackSecret : ProcessSecret.Value);
    private static readonly Lazy<string> ProcessSecret = new(() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public string Sign(byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Secret(), body)).ToLowerInvariant();

    public ProviderCallback? VerifyCallback(IHeaderDictionary headers, byte[] body)
    {
        var given = headers[SignatureHeader].ToString();
        var expected = Sign(body);
        if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected))) return null;
        try
        {
            var j = JsonNode.Parse(body)!;
            return new ProviderCallback(j["eventId"]!.GetValue<string>(), j["providerRef"]!.GetValue<string>(), Guid.Parse(j["correlationId"]!.GetValue<string>()),
                j["event"]!.GetValue<string>(), j["signerRef"]?.GetValue<string>(), j["documentSha256"]?.GetValue<string>(), j["reason"]?.GetValue<string>(),
                j["occurredAt"]?.GetValue<DateTime>() ?? DateTime.UtcNow);
        }
        catch (Exception e) when (e is JsonException or FormatException or NullReferenceException or InvalidOperationException) { return null; }
    }

    // ---------------- جهة المزوّد: صفحة التوقيع (تحاكي ما يجري لدى المزوّد) ----------------

    internal sealed record MockEnvelope(string ProviderRef, Guid CorrelationId, string Reference, string OriginalSha256, string CurrentBase64, bool Sequential,
        JsonArray Signers, string Status, DateTime ExpiresAt, int EventSeq);

    internal async Task<MockEnvelope?> LoadAsync(string providerRef, CancellationToken ct, ISql? sql = null, bool forUpdate = false) =>
        await (sql ?? db).QuerySingleAsync($"""
            SELECT provider_ref, correlation_id, reference, original_sha256, current_base64, sequential, signers::text, status, expires_at, event_seq
            FROM sig.mock_envelopes WHERE provider_ref = @providerRef {(forUpdate ? "FOR UPDATE" : "")}
            """, new { providerRef }, r => new MockEnvelope(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5),
            JsonNode.Parse(r.GetString(6))!.AsArray(), r.GetString(7), r.GetDateTime(8), r.GetInt32(9)), ct);

    internal async Task<(MockEnvelope Envelope, JsonNode Signer)?> FindByTokenAsync(string token, CancellationToken ct)
    {
        var refId = await db.ScalarAsync<string>("""
            SELECT provider_ref FROM sig.mock_envelopes e, jsonb_array_elements(e.signers) s WHERE s->>'token' = @token
            """, new { token }, ct);
        if (refId is null) return null;
        var e = await LoadAsync(refId, ct);
        var s = e!.Signers.First(x => x!["token"]!.GetValue<string>() == token)!;
        return (e, s);
    }

    /// <summary>يوقّع الموقّع لدى المزوّد: توقيع رقمي بشهادته الاختبارية على أحدث نسخة، ثم استدعاء راجع موقع إلى التطبيق.</summary>
    internal async Task<List<(string Body, string Signature)>> SignAtProviderAsync(string token, Guid currentUser, CancellationToken ct)
    {
        Guard();
        var callbacks = new List<(string, string)>();
        await db.InTransactionAsync(async tx =>
        {
            var found = await FindByTokenAsync(token, ct) ?? throw new SignatureProviderUnavailableException("جلسة التوقيع غير موجودة");
            var e = (await LoadAsync(found.Envelope.ProviderRef, ct, tx, true))!;
            var signer = e.Signers.First(x => x!["token"]!.GetValue<string>() == token)!;
            if (signer["userId"]!.GetValue<Guid>() != currentUser) throw new MockSessionException("not-your-session", "جلسة التوقيع لموقّع آخر");
            if (e.Status != "sent") throw new MockSessionException("envelope-closed", "المغلف لم يعد متاحًا للتوقيع");
            if (e.ExpiresAt < DateTime.UtcNow) throw new MockSessionException("envelope-expired", "انتهت صلاحية المغلف");
            if (signer["status"]!.GetValue<string>() != "pending") throw new MockSessionException("already-acted", "سبق أن تصرّف الموقّع");
            var order = signer["order"]!.GetValue<int>();
            if (e.Sequential && e.Signers.Any(x => x!["order"]!.GetValue<int>() < order && x["status"]!.GetValue<string>() != "signed"))
                throw new MockSessionException("not-your-turn", "التوقيع بالتسلسل: لم يحن دور هذا الموقّع لدى المزوّد");
            var name = signer["name"]!.GetValue<string>();
            var now = DateTime.UtcNow;
            var prepared = PdfSigner.Prepare(Convert.FromBase64String(e.CurrentBase64), "MockSig" + order, name, $"توقيع عبر المزوّد التجريبي — {e.Reference}", now);
            var root = await RootAsync(ct);
            var cert = await SignerCertificateAsync(signer["userId"]!.GetValue<Guid>(), name, ct);
            var signed = PdfSigner.Embed(prepared, CmsBuilder.SignDetached(prepared.Digest(), cert, [cert, root], now));
            signer["status"] = "signed";
            signer["signedAt"] = now.ToString("O");
            signer["certificateSerial"] = cert.SerialNumber;
            var allSigned = e.Signers.All(x => x!["status"]!.GetValue<string>() == "signed");
            var seq = e.EventSeq;
            callbacks.Add(Callback(e, ++seq, "signer_signed", signer["ref"]!.GetValue<string>(), null));
            if (allSigned) callbacks.Add(Callback(e, ++seq, "completed", null, null));
            await tx.ExecuteAsync("""
                UPDATE sig.mock_envelopes SET current_base64 = @cur, signers = @signers::jsonb, status = @status, event_seq = @seq, updated_at = now() WHERE provider_ref = @r
                """, new { r = e.ProviderRef, cur = Convert.ToBase64String(signed), signers = e.Signers.ToJsonString(), status = allSigned ? "completed" : "sent", seq }, ct);
        }, ct);
        return callbacks;
    }

    internal async Task<List<(string Body, string Signature)>> DeclineAtProviderAsync(string token, Guid currentUser, string reason, CancellationToken ct)
    {
        Guard();
        var callbacks = new List<(string, string)>();
        await db.InTransactionAsync(async tx =>
        {
            var found = await FindByTokenAsync(token, ct) ?? throw new SignatureProviderUnavailableException("جلسة التوقيع غير موجودة");
            var e = (await LoadAsync(found.Envelope.ProviderRef, ct, tx, true))!;
            var signer = e.Signers.First(x => x!["token"]!.GetValue<string>() == token)!;
            if (signer["userId"]!.GetValue<Guid>() != currentUser) throw new MockSessionException("not-your-session", "جلسة التوقيع لموقّع آخر");
            if (e.Status != "sent" || signer["status"]!.GetValue<string>() != "pending") throw new MockSessionException("envelope-closed", "المغلف لم يعد متاحًا");
            signer["status"] = "declined";
            callbacks.Add(Callback(e, e.EventSeq + 1, "signer_declined", signer["ref"]!.GetValue<string>(), reason));
            await tx.ExecuteAsync("UPDATE sig.mock_envelopes SET signers = @signers::jsonb, status = 'declined', event_seq = event_seq + 1, updated_at = now() WHERE provider_ref = @r",
                new { r = e.ProviderRef, signers = e.Signers.ToJsonString() }, ct);
        }, ct);
        return callbacks;
    }

    private (string Body, string Signature) Callback(MockEnvelope e, int seq, string evt, string? signerRef, string? reason)
    {
        var body = new JsonObject
        {
            ["eventId"] = $"{e.ProviderRef}-{seq}", ["providerRef"] = e.ProviderRef, ["correlationId"] = e.CorrelationId.ToString(), ["event"] = evt,
            ["signerRef"] = signerRef, ["documentSha256"] = e.OriginalSha256, ["reason"] = reason, ["occurredAt"] = DateTime.UtcNow,
        }.ToJsonString();
        return (body, Sign(Encoding.UTF8.GetBytes(body)));
    }
}

internal sealed class MockSessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
