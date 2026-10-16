using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Signatures;

// ============ واجهة التكامل مع مزوّد التوقيع (قابلة للتبديل دون تغيير منطق المحاضر) ============

public sealed record ProviderReadiness(bool Configured, bool AllowedInEnvironment, bool ProductionGrade, string Reason);

public sealed record EnvelopeSigner(string ExternalId, Guid UserId, string Name, string? Email, int Order);

/// <summary>طلب إنشاء مغلف توقيع لدى المزوّد: المستند وبصمته، والموقّعون وتسلسلهم، ومعرّف الطلب لدينا للربط والتحقق.</summary>
public sealed record EnvelopeRequest(Guid CorrelationId, string Reference, string Subject, byte[] Document, string DocumentSha256, IReadOnlyList<EnvelopeSigner> Signers,
    bool Sequential, DateTime ExpiresAt, string Reason);

public sealed record ProviderEnvelope(string ProviderRef, IReadOnlyDictionary<string, string> SignerRefs);

public sealed record ProviderSignedDocument(byte[] Pdf, string VerificationJson);

/// <summary>طلب توقيع بصمة بمفتاح الموقّع لدى المزوّد (توقيع رقمي عن بعد): التطبيق لا يرسل إلا البصمة.</summary>
public sealed record RemoteSignRequest(Guid UserId, string SignerName, byte[] Sha256Digest, string Reason, DateTime SigningTimeUtc, Guid CorrelationId);

public sealed record RemoteSignature(byte[] Cms, X509Certificate2 Certificate);

/// <summary>استدعاء راجع بعد التحقق من توقيع الرسالة. لا يُقبل ما لم يُتحقق منه.</summary>
public sealed record ProviderCallback(string EventId, string ProviderRef, Guid CorrelationId, string Event, string? SignerRef, string? DocumentSha256, string? Reason,
    DateTime OccurredAt);

/// <summary>انقطاع مؤقت (اتصال أو مهلة أو 5xx): يُعاد المحاولة لاحقًا ولا يُغيّر حالة الطلب إلى فشل نهائي مباشرة.</summary>
public sealed class SignatureProviderUnavailableException(string message) : Exception(message);

/// <summary>المزوّد غير مضبوط أو غير منفذ: لا محاولة ولا ادعاء نجاح.</summary>
public sealed class SignatureProviderNotConfiguredException(string message) : Exception(message);

public interface ISignatureProvider
{
    string Code { get; }
    string NameAr { get; }
    bool Supports(string signatureType);
    ProviderReadiness Readiness();
    Task<ProviderEnvelope> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct);
    Task<string> SigningUrlAsync(string providerRef, string signerRef, CancellationToken ct);
    Task<ProviderSignedDocument> DownloadSignedAsync(string providerRef, CancellationToken ct);
    Task CancelAsync(string providerRef, string reason, CancellationToken ct);
    Task<RemoteSignature> SignDigestAsync(RemoteSignRequest request, CancellationToken ct);
    /// <summary>يتحقق من توقيع الرسالة الراجعة (HMAC أو ما يحدده المزوّد) ويعيدها مفككة، أو null إن لم يصح التوقيع.</summary>
    ProviderCallback? VerifyCallback(IHeaderDictionary headers, byte[] body);
    /// <summary>جهات الإصدار الاختبارية لهذا المزوّد (تُصنَّف سلسلتها «اختبارية» لا «موثوقة»).</summary>
    IReadOnlyCollection<X509Certificate2> TestRoots();
}

/// <summary>موصل المزوّد المعتمد: لا يوجد عقد أو واجهة موثقة بعد (Q-09)، فلا يُدّعى أي اتصال أو توقيع.</summary>
internal sealed class NationalProviderAdapter : ISignatureProvider
{
    public string Code => "national_provider";
    public string NameAr => "مزوّد توقيع معتمد";
    public bool Supports(string signatureType) => signatureType is Contracts.SignatureTypes.ProviderESign or Contracts.SignatureTypes.PdfDigitalSignature;
    public ProviderReadiness Readiness() => new(false, true, true, "esign_provider_not_integrated");
    private static SignatureProviderNotConfiguredException NotIntegrated() => new("لم يُتعاقد مع مزوّد توقيع معتمد ولم يُنفذ موصله بعد (Q-09)");
    public Task<ProviderEnvelope> CreateEnvelopeAsync(EnvelopeRequest request, CancellationToken ct) => throw NotIntegrated();
    public Task<string> SigningUrlAsync(string providerRef, string signerRef, CancellationToken ct) => throw NotIntegrated();
    public Task<ProviderSignedDocument> DownloadSignedAsync(string providerRef, CancellationToken ct) => throw NotIntegrated();
    public Task CancelAsync(string providerRef, string reason, CancellationToken ct) => throw NotIntegrated();
    public Task<RemoteSignature> SignDigestAsync(RemoteSignRequest request, CancellationToken ct) => throw NotIntegrated();
    public ProviderCallback? VerifyCallback(IHeaderDictionary headers, byte[] body) => null;
    public IReadOnlyCollection<X509Certificate2> TestRoots() => [];
}

internal sealed class ProviderRegistry(IEnumerable<ISignatureProvider> providers)
{
    public ISignatureProvider? Find(string code) => providers.FirstOrDefault(p => p.Code == code);
    public ISignatureProvider Get(string code) => Find(code) ?? throw new SignatureProviderNotConfiguredException($"المزوّد {code} غير معروف");
    public IReadOnlyCollection<X509Certificate2> AllTestRoots() => providers.SelectMany(p => p.TestRoots()).ToList();
}
