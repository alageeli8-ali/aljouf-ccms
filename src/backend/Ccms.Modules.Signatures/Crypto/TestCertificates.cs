using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ccms.Modules.Signatures.Crypto;

/// <summary>شهادات المزوّد التجريبي فقط: جهة إصدار اختبارية وشهادة لكل موقّع. ليست شهادات موثوقة ولا يُقبل استخدامها في الإنتاج،
/// وتقارير التحقق تصنّف سلسلتها «اختبارية» صراحة.</summary>
internal static class TestCertificates
{
    public const string RootSubject = "CN=CCMS Mock Signing CA (TEST ONLY - NOT TRUSTED), O=CCMS Mock Provider, C=SA";
    public const string Organization = "CCMS Mock Provider (TEST ONLY)";

    public static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest(RootSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        using var cert = req.CreateSelfSigned(now, now.AddYears(10));
        return Reload(cert.Export(X509ContentType.Pfx));
    }

    public static X509Certificate2 IssueSigner(X509Certificate2 root, string commonName, string serialNumberAttribute)
    {
        using var key = RSA.Create(2048);
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCountryOrRegion("SA");
        dn.AddOrganizationName(Organization);
        dn.Add("2.5.4.5", serialNumberAttribute);   // serialNumber: معرّف المستخدم في النظام، لا رقم هوية وطنية
        dn.AddCommonName(commonName);
        var req = new CertificateRequest(dn.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.4.1.311.10.3.12", "Document Signing")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var cert = req.Create(root, now, now.AddYears(2), serial);
        using var withKey = cert.CopyWithPrivateKey(key);
        return Reload(withKey.Export(X509ContentType.Pfx));
    }

    public static X509Certificate2 Reload(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
}
