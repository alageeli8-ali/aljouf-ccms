using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ccms.Modules.Signatures.Crypto;

/// <summary>قراءة توقيع CMS منفصل (SignedData) والتحقق منه: تطابق messageDigest مع بصمة البيانات، وصحة التوقيع على السمات الموقعة
/// بالمفتاح العام لشهادة الموقّع. (مكتبة System.Security.Cryptography.Pkcs غير متاحة كمرجع في بيئة البناء — ADR-015.)</summary>
internal sealed class CmsReader
{
    public required IReadOnlyList<X509Certificate2> Certificates { get; init; }
    public X509Certificate2? Signer { get; init; }
    public byte[]? MessageDigest { get; init; }
    public DateTime? SigningTime { get; init; }
    public required string DigestAlgorithm { get; init; }
    public required byte[] SignedAttributesForSignature { get; init; }
    public required byte[] Signature { get; init; }
    public required string SignatureAlgorithm { get; init; }

    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string OidSigningTime = "1.2.840.113549.1.9.5";
    private const string OidSha256 = "2.16.840.1.101.3.4.2.1";

    public static CmsReader Parse(byte[] der)
    {
        var ci = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
        if (ci.ReadObjectIdentifier() != OidSignedData) throw new CryptographicException("ليس SignedData");
        var sd = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
        sd.ReadInteger();                     // version
        sd.ReadSetOf();                       // digestAlgorithms
        sd.ReadSequence();                    // encapContentInfo (منفصل)
        var certs = new List<X509Certificate2>();
        if (sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            var set = sd.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            while (set.HasData) certs.Add(X509CertificateLoader.LoadCertificate(set.ReadEncodedValue().ToArray()));
        }
        if (sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1))) sd.ReadEncodedValue();   // crls
        var signerInfos = sd.ReadSetOf();
        var si = signerInfos.ReadSequence();
        si.ReadInteger();
        var sid = si.ReadSequence();
        var issuer = sid.ReadEncodedValue().ToArray();
        var serial = sid.ReadIntegerBytes().ToArray();
        var digestAlg = si.ReadSequence().ReadObjectIdentifier();
        byte[] attrsForSignature = [];
        byte[]? md = null;
        DateTime? signingTime = null;
        if (si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            var raw = si.ReadEncodedValue().ToArray();
            attrsForSignature = (byte[])raw.Clone();
            attrsForSignature[0] = 0x31;      // التوقيع يقع على ترميز SET العام للسمات
            var attrs = new AsnReader(raw, AsnEncodingRules.BER).ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            while (attrs.HasData)
            {
                var a = attrs.ReadSequence();
                var oid = a.ReadObjectIdentifier();
                var values = a.ReadSetOf();
                if (oid == OidMessageDigest) md = values.ReadOctetString();
                else if (oid == OidSigningTime)
                    signingTime = (values.PeekTag().TagValue == (int)UniversalTagNumber.UtcTime ? values.ReadUtcTime() : values.ReadGeneralizedTime()).UtcDateTime;
            }
        }
        var sigAlg = si.ReadSequence().ReadObjectIdentifier();
        var signature = si.ReadOctetString();
        var signer = certs.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(issuer) && c.SerialNumberBytes.Span.SequenceEqual(serial));
        return new CmsReader
        {
            Certificates = certs, Signer = signer, MessageDigest = md, SigningTime = signingTime, DigestAlgorithm = digestAlg,
            SignedAttributesForSignature = attrsForSignature, Signature = signature, SignatureAlgorithm = sigAlg,
        };
    }

    /// <summary>هل تطابق البصمة الموقعة بصمة البيانات المحسوبة؟</summary>
    public bool DigestMatches(byte[] data) =>
        DigestAlgorithm == OidSha256 && MessageDigest is not null && MessageDigest.AsSpan().SequenceEqual(SHA256.HashData(data));

    /// <summary>هل التوقيع على السمات الموقعة صحيح بالمفتاح العام لشهادة الموقّع؟</summary>
    public bool SignatureValid()
    {
        if (Signer is null || SignedAttributesForSignature.Length == 0) return false;
        using var rsa = Signer.GetRSAPublicKey();
        if (rsa is not null) return rsa.VerifyData(SignedAttributesForSignature, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ec = Signer.GetECDsaPublicKey();
        return ec is not null && ec.VerifyData(SignedAttributesForSignature, Signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
