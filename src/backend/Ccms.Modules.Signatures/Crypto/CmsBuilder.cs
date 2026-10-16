using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ccms.Modules.Signatures.Crypto;

/// <summary>يبني توقيع CMS منفصلًا (SignedData، RFC 5652) على بصمة SHA-256 جاهزة، بالسمات الموقعة التي يتطلبها PAdES الأساسي
/// (contentType وmessageDigest وsigningTime وsigning-certificate-v2). يُستخدم في جهة «المزوّد» التي تملك المفتاح الخاص؛
/// التطبيق لا يرسل إلا البصمة، كما في خدمات التوقيع عن بعد.</summary>
internal static class CmsBuilder
{
    private const string OidData = "1.2.840.113549.1.7.1";
    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidContentType = "1.2.840.113549.1.9.3";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string OidSigningTime = "1.2.840.113549.1.9.5";
    private const string OidSigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    private const string OidSha256 = "2.16.840.1.101.3.4.2.1";
    private const string OidRsaEncryption = "1.2.840.113549.1.1.1";

    public static byte[] SignDetached(byte[] sha256Digest, X509Certificate2 signer, IReadOnlyList<X509Certificate2> chain, DateTime signingTimeUtc)
    {
        if (sha256Digest.Length != 32) throw new ArgumentException("بصمة SHA-256 غير صالحة", nameof(sha256Digest));
        using var rsa = signer.GetRSAPrivateKey() ?? throw new InvalidOperationException("الشهادة لا تحمل مفتاح RSA خاصًا");

        // السمات الموقعة: التوقيع يقع على ترميز DER بوسم SET العام، وتُضمَّن في SignerInfo بالوسم [0] الضمني
        var signedForSignature = Attributes(sha256Digest, signer, signingTimeUtc, Asn1Tag.SetOf);
        var signature = rsa.SignData(signedForSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(OidSignedData);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) AlgorithmId(w, OidSha256, false);
                using (w.PushSequence()) w.WriteObjectIdentifier(OidData); // منفصل: بلا محتوى مضمن
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    w.WriteEncodedValue(signer.RawData);
                    foreach (var c in chain.Where(c => c.Thumbprint != signer.Thumbprint)) w.WriteEncodedValue(c.RawData);
                }
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(1);
                    using (w.PushSequence())
                    {
                        w.WriteEncodedValue(signer.IssuerName.RawData);
                        w.WriteInteger(signer.SerialNumberBytes.Span);
                    }
                    AlgorithmId(w, OidSha256, false);
                    w.WriteEncodedValue(Attributes(sha256Digest, signer, signingTimeUtc, new Asn1Tag(TagClass.ContextSpecific, 0, true)));
                    AlgorithmId(w, OidRsaEncryption, true);
                    w.WriteOctetString(signature);
                }
            }
        }
        return w.Encode();
    }

    private static byte[] Attributes(byte[] digest, X509Certificate2 signer, DateTime signingTimeUtc, Asn1Tag tag)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSetOf(tag))
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(OidContentType);
                using (w.PushSetOf()) w.WriteObjectIdentifier(OidData);
            }
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(OidSigningTime);
                using (w.PushSetOf()) w.WriteUtcTime(new DateTimeOffset(DateTime.SpecifyKind(signingTimeUtc, DateTimeKind.Utc)));
            }
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(OidMessageDigest);
                using (w.PushSetOf()) w.WriteOctetString(digest);
            }
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(OidSigningCertificateV2);
                using (w.PushSetOf())
                using (w.PushSequence())          // SigningCertificateV2
                using (w.PushSequence())          // certs
                using (w.PushSequence())          // ESSCertIDv2 (الخوارزمية الافتراضية SHA-256 تُحذف)
                    w.WriteOctetString(SHA256.HashData(signer.RawData));
            }
        }
        return w.Encode();
    }

    private static void AlgorithmId(AsnWriter w, string oid, bool withNull)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (withNull) w.WriteNull();
        }
    }
}
