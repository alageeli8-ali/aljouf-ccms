using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Ccms.Modules.Signatures.Crypto;

namespace Ccms.Modules.Signatures.Pdf;

/// <summary>نتيجة التحقق من توقيع واحد في ملف PDF، بحالات دقيقة لا تُجمِّل النتيجة.</summary>
public sealed record PdfSignatureCheck(
    int Index,
    string? SignerName,
    string? CertificateSubject,
    string? CertificateIssuer,
    string? CertificateSerial,
    string? CertificateThumbprint,
    DateTime? CertificateNotBefore,
    DateTime? CertificateNotAfter,
    DateTime? SigningTime,
    bool ByteRangeValid,
    bool DigestMatches,
    bool SignatureValid,
    bool CertificateValidAtSigning,
    string Chain,              // trusted | test_only | untrusted
    string? ChainDetail,
    bool CoversWholeDocument,
    string SubsequentChanges,  // none | signatures_only | other_changes
    string Revocation,         // not_checked (لا وصول لقوائم الإلغاء)
    string Timestamp,          // none (وقت التوقيع من ساعة جهة التوقيع، بلا ختم زمني موثوق)
    long RevisionLength,
    string RevisionSha256,
    string? Error)
{
    public bool CryptographicallyValid => ByteRangeValid && DigestMatches && SignatureValid && CertificateValidAtSigning && SubsequentChanges != "other_changes";
}

public sealed record PdfVerification(string FileSha256, long FileLength, IReadOnlyList<PdfSignatureCheck> Signatures, bool ModifiedAfterLastSignature,
    string Overall, IReadOnlyList<string> Notes);

/// <summary>يتحقق من التوقيعات الرقمية المضمنة في PDF: نطاق البايتات، وتطابق البصمة، وصحة توقيع CMS، وصلاحية الشهادة وقت التوقيع،
/// وسلسلة الثقة مقابل جهات الثقة المضبوطة (أو جهة الاختبار مصنفة «اختبارية»)، وأي تعديل لاحق غير التوقيعات.</summary>
internal static partial class PdfSignatureVerifier
{
    public static PdfVerification Verify(byte[] pdf, IReadOnlyCollection<X509Certificate2> trustedRoots, IReadOnlyCollection<X509Certificate2> testRoots)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var ranges = ByteRangeRx().Matches(text).Select(m => m.Groups.Cast<Group>().Skip(1).Select(g => long.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray())
            .OrderBy(r => r[2] + r[3]).ToList();
        var notes = new List<string>();
        var checks = new List<PdfSignatureCheck>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            var end = r[2] + r[3];
            var next = i + 1 < ranges.Count ? ranges[i + 1][2] + ranges[i + 1][3] : pdf.Length;
            var subsequent = end == pdf.Length ? "none" : i + 1 < ranges.Count ? SignatureOnlyRevision(pdf, end, next) : "other_changes";
            checks.Add(CheckOne(pdf, i + 1, r, subsequent, trustedRoots, testRoots));
        }
        var modifiedAfter = ranges.Count > 0 && ranges[^1][2] + ranges[^1][3] != pdf.Length;
        string overall;
        if (checks.Count == 0) { overall = "no_signatures"; notes.Add("لا يحتوي الملف على توقيعات رقمية مضمنة."); }
        else if (modifiedAfter || checks.Any(c => !c.CryptographicallyValid)) overall = "invalid";
        else if (checks.All(c => c.Chain == "trusted")) overall = "valid";
        else if (checks.All(c => c.Chain is "trusted" or "test_only")) overall = "valid_test_only";
        else overall = "valid_untrusted_chain";
        if (checks.Count > 0)
        {
            notes.Add("لم يُفحص إلغاء الشهادات (لا وصول لقوائم الإلغاء أو OCSP من بيئة الخادم).");
            notes.Add("لا يوجد ختم زمني موثوق (RFC 3161)؛ وقت التوقيع مأخوذ من السمة الموقعة.");
        }
        if (overall == "valid_test_only") notes.Add("السلسلة تنتهي بجهة إصدار اختبارية للمزوّد التجريبي: صالحة تقنيًا للاختبار وليست توقيعًا موثوقًا أو ذا حجية.");
        if (overall == "valid_untrusted_chain") notes.Add("التوقيع سليم تقنيًا لكن الشهادة لا تتسلسل إلى جهة ثقة مضبوطة في النظام.");
        if (modifiedAfter) notes.Add("أُضيفت بيانات إلى الملف بعد آخر توقيع.");
        return new PdfVerification(Convert.ToHexStringLower(SHA256.HashData(pdf)), pdf.Length, checks, modifiedAfter, overall, notes);
    }

    private static PdfSignatureCheck CheckOne(byte[] pdf, int index, long[] r, string subsequent, IReadOnlyCollection<X509Certificate2> trusted, IReadOnlyCollection<X509Certificate2> test)
    {
        var end = r[2] + r[3];
        var revisionSha = end <= pdf.Length ? Convert.ToHexStringLower(SHA256.HashData(pdf.AsSpan(0, (int)end))) : "";
        var rangeOk = r[0] == 0 && r[1] > 0 && r[2] > r[1] && end <= pdf.Length && pdf[r[1]] == (byte)'<' && pdf[r[2] - 1] == (byte)'>';
        PdfSignatureCheck Fail(string error) => new(index, null, null, null, null, null, null, null, null, rangeOk, false, false, false, "untrusted", null,
            end == pdf.Length, subsequent, "not_checked", "none", end, revisionSha, error);
        if (!rangeOk) return Fail("نطاق البايتات غير صالح");
        byte[] cmsBytes;
        try
        {
            var hex = Encoding.ASCII.GetString(pdf, (int)r[1] + 1, (int)(r[2] - r[1] - 2));
            var raw = Convert.FromHexString(hex);
            AsnDecoder.TryReadEncodedValue(raw, AsnEncodingRules.BER, out _, out _, out _, out var consumed);
            cmsBytes = raw[..consumed];
        }
        catch (Exception e) when (e is FormatException or AsnContentException) { return Fail("محتوى التوقيع غير قابل للقراءة"); }

        var data = new byte[r[1] + r[3]];
        Buffer.BlockCopy(pdf, 0, data, 0, (int)r[1]);
        Buffer.BlockCopy(pdf, (int)r[2], data, (int)r[1], (int)r[3]);
        CmsReader cms;
        try { cms = CmsReader.Parse(cmsBytes); }
        catch (Exception e) when (e is CryptographicException or AsnContentException or InvalidOperationException) { return Fail("بنية CMS غير صالحة"); }
        var cert = cms.Signer;
        var signingTime = cms.SigningTime;
        var digestMatches = cms.DigestMatches(data);
        var sigValid = false;
        string? error = null;
        try { sigValid = cms.SignatureValid(); }
        catch (CryptographicException e) { error = "فشل التحقق التشفيري: " + e.Message; }
        if (!digestMatches) error ??= "بصمة المحتوى لا تطابق البصمة الموقعة: عُدّل الملف بعد التوقيع";
        else if (!sigValid) error ??= "التوقيع على السمات غير صحيح بالمفتاح العام للشهادة";
        if (cert is null) error ??= "شهادة الموقّع غير مضمنة";
        var at = signingTime ?? DateTime.UtcNow;
        var certValid = cert is not null && cert.NotBefore.ToUniversalTime() <= at && at <= cert.NotAfter.ToUniversalTime();
        var (chain, chainDetail) = cert is null ? ("untrusted", "لا شهادة موقّع") : Chain(cert, new X509Certificate2Collection(cms.Certificates.ToArray()), at, trusted, test);
        var signerName = cert?.GetNameInfo(X509NameType.SimpleName, false);
        return new PdfSignatureCheck(index, signerName, cert?.Subject, cert?.Issuer, cert?.SerialNumber, cert?.Thumbprint, cert?.NotBefore.ToUniversalTime(),
            cert?.NotAfter.ToUniversalTime(), signingTime, true, digestMatches, sigValid && digestMatches, certValid, chain, chainDetail, end == pdf.Length, subsequent, "not_checked", "none",
            end, revisionSha, error);
    }

    private static (string, string?) Chain(X509Certificate2 cert, X509Certificate2Collection extra, DateTime at, IReadOnlyCollection<X509Certificate2> trusted,
        IReadOnlyCollection<X509Certificate2> test)
    {
        string? Build(IReadOnlyCollection<X509Certificate2> roots)
        {
            if (roots.Count == 0) return "لا توجد جهات ثقة مضبوطة";
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = at.ToLocalTime();
            foreach (var r in roots) chain.ChainPolicy.CustomTrustStore.Add(r);
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            return chain.Build(cert) ? null : string.Join("؛ ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Distinct());
        }
        var t = Build(trusted);
        if (t is null) return ("trusted", null);
        var x = Build(test);
        return x is null ? ("test_only", "تتسلسل إلى جهة الإصدار الاختبارية للمزوّد التجريبي") : ("untrusted", t);
    }

    /// <summary>هل التحديث التزايدي بين نهايتي توقيعين يقتصر على إضافة توقيع (قاموس توقيع وحقله، وإعادة تعريف الفهرس والصفحة الأولى دون تغيير غير الحقول)؟</summary>
    private static string SignatureOnlyRevision(byte[] pdf, long from, long to)
    {
        try
        {
            var prev = PdfStructure.Parse(pdf[..(int)from]);
            var segment = Encoding.Latin1.GetString(pdf, (int)from, (int)(to - from));
            var ids = ObjRx().Matches(segment).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            var next = PdfStructure.Parse(pdf[..(int)to]);
            foreach (var id in ids)
            {
                if (id == prev.RootId)
                {
                    if (Strip(prev.ObjectDict(id), AcroRx()) != Strip(next.ObjectDict(id), AcroRx())) return "other_changes";
                }
                else if (id == prev.FirstPageId)
                {
                    if (Strip(prev.ObjectDict(id), AnnotsRx()) != Strip(next.ObjectDict(id), AnnotsRx())) return "other_changes";
                }
                else if (id >= prev.Size)
                {
                    var d = next.ObjectDict(id);
                    if (!d.Contains("/Type /Sig", StringComparison.Ordinal) && !d.Contains("/FT /Sig", StringComparison.Ordinal)) return "other_changes";
                }
                else return "other_changes";
            }
            return "signatures_only";
        }
        catch (Exception e) when (e is PdfFormatException or FormatException or ArgumentException) { return "other_changes"; }
    }

    private static string Strip(string dict, Regex rx) => Regex.Replace(rx.Replace(dict, ""), @"\s+", " ").Trim();

    [GeneratedRegex(@"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]")] private static partial Regex ByteRangeRx();
    [GeneratedRegex(@"(?m)^(\d+) 0 obj")] private static partial Regex ObjRx();
    [GeneratedRegex(@"/AcroForm\s*<<\s*/Fields\s*\[[^\]]*\][^>]*>>")] private static partial Regex AcroRx();
    [GeneratedRegex(@"/Annots\s*\[[^\]]*\]")] private static partial Regex AnnotsRx();
}
