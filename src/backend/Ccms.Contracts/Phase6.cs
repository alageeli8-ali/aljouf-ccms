using Ccms.BuildingBlocks.Data;

namespace Ccms.Contracts;

// ============ وحدة التوقيع الإلكتروني (ADR-020) ============

/// <summary>أنواع التوقيع الثلاثة المنفصلة؛ لا يُعرض أحدها على أنه الآخر.</summary>
public static class SignatureTypes
{
    /// <summary>موافقة إلكترونية موثقة داخل النظام — ليست توقيعًا رقميًا بشهادة تشفير.</summary>
    public const string InAppApproval = "in_app_approval";
    /// <summary>توقيع إلكتروني عبر مزوّد خارجي.</summary>
    public const string ProviderESign = "provider_esign";
    /// <summary>توقيع رقمي تشفيري (CMS) مضمن في ملف PDF بشهادة الموقّع.</summary>
    public const string PdfDigitalSignature = "pdf_digital_signature";
    public static readonly string[] All = [InAppApproval, ProviderESign, PdfDigitalSignature];
}

/// <summary>المرشحون للتوقيع كما تحددهم وحدة المحاضر (سياسة الجهة والحضور)؛ ومسار التوقيع يختار منهم ويرتبهم.</summary>
public sealed record SignatureCandidates(IReadOnlyList<Guid> Secretaries, IReadOnlyList<Guid> Members, IReadOnlyList<Guid> Chairs, bool PolicyIncludesChair = false);

public sealed record MinutesSignatureSubject(Guid MinutesId, Guid VersionId, int VersionNo, string ContentHash, Guid BodyId, string MeetingType,
    string MeetingRef, string Classification, byte[] Pdf, string FileName, SignatureCandidates Candidates, bool Restart = false);

public sealed record CreatedSignatureSigner(Guid SignerId, Guid UserId, string Role);

public sealed record CreatedSignatureRequest(Guid RequestId, string SignatureType, IReadOnlyList<CreatedSignatureSigner> Signers);

public sealed record SignatureEvidenceLine(string RequestReference, string SignatureType, string Signer, string Role, string Status, DateTime? SignedAt,
    string? OnBehalfOf, string DocumentSha256, string? SignedDocumentSha256, string? CertificateSubject, string? CertificateSerial, string? VerificationStatus);

/// <summary>منفذ في وحدة المحاضر تبلغه وحدة التوقيع بالنتائج؛ المحاضر وحدها تملك حالة المحضر وقرار اكتمال الإقرارات.</summary>
public interface IMinutesSignatureSink
{
    /// <summary>اكتمل توقيع موقّع: يُسجل إقراره بنوع التوقيع، ويتقدم المحضر إن اكتملت الإقرارات.</summary>
    Task SignerCompletedAsync(ISql tx, Guid minutesVersionId, Guid userId, Guid requestId, Guid signerId, string signatureType, Guid actedBy, Guid? onBehalfOf,
        CancellationToken ct = default);

    /// <summary>رفض موقّع التوقيع وطلب التعديل: يعاد المحضر للتعديل بالسبب.</summary>
    Task SignerDeclinedAsync(ISql tx, Guid minutesVersionId, Guid userId, string reason, CancellationToken ct = default);

    /// <summary>تحويل صريح للمسار البديل: من لم يوقّع يُطلب منه الإقرار بالبديل في سياسة الجهة، والتوقيعات المكتملة تبقى صالحة.</summary>
    Task<int> SwitchToFallbackAsync(ISql tx, Guid minutesVersionId, string reason, Guid by, CancellationToken ct = default);

    /// <summary>بدء طلب توقيع جديد صراحة لمن لم يوقّع بعد على الإصدار الحالي (بعد التحويل للبديل وإعادة التفعيل، أو انتهاء طلب سابق).</summary>
    Task<CreatedSignatureRequest?> RestartAsync(ISql tx, Guid minutesId, Guid by, CancellationToken ct = default);

    /// <summary>هل الإصدار ما يزال في مرحلة التوقيع؟</summary>
    Task<bool> IsAwaitingSignaturesAsync(ISql sql, Guid minutesVersionId, CancellationToken ct = default);
}

/// <summary>إعادة التحقق من الهوية قبل التوقيع (E_SIGNATURE_REAUTH_REQUIRED). تنفذه وحدة الهوية.</summary>
public interface IReauthenticator
{
    /// <summary>يتحقق من كلمة المرور المحلية؛ يعيد null إن لم يكن للحساب كلمة مرور محلية (دخول موحد فقط).</summary>
    Task<bool?> VerifyPasswordAsync(Guid userId, string password, CancellationToken ct = default);
}
