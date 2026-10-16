using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;
using Ccms.Modules.Signatures.Crypto;
using Ccms.Modules.Signatures.Pdf;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Signatures;

/// <summary>تقارير التحقق: سلامة النسخة المخزنة مقابل بصمتها، والتوقيعات الرقمية في PDF، وسجل الموافقات الموثقة. حالات دقيقة دون تجميل.</summary>
internal sealed class VerificationService(Db db, IDocumentStore store, IOptions<SignatureOptions> options)
{
    private static IReadOnlyCollection<X509Certificate2>? _trusted;

    public IReadOnlyCollection<X509Certificate2> TrustedRoots()
    {
        if (_trusted is not null) return _trusted;
        var list = new List<X509Certificate2>();
        foreach (var f in options.Value.TrustedRootPemFiles.Where(File.Exists))
        {
            var col = new X509Certificate2Collection();
            col.ImportFromPemFile(f);
            list.AddRange(col);
        }
        return _trusted = list;
    }

    /// <summary>جهة الإصدار الاختبارية للمزوّد التجريبي إن وُجدت (لا تُنشأ هنا).</summary>
    public async Task<IReadOnlyCollection<X509Certificate2>> TestRootsAsync(CancellationToken ct)
    {
        var pfx = await db.ScalarAsync<string>("SELECT pfx_base64 FROM sig.mock_keys WHERE name = 'root'", null, ct);
        if (pfx is null) return [];
        using var full = TestCertificates.Reload(Convert.FromBase64String(pfx));
        return [X509CertificateLoader.LoadCertificate(full.RawData)];
    }

    public async Task<PdfVerification> VerifyPdfAsync(byte[] pdf, CancellationToken ct) =>
        PdfSignatureVerifier.Verify(pdf, TrustedRoots(), await TestRootsAsync(ct));

    /// <summary>تقرير طلب: يعاد حسابه من الملفات المخزنة الآن (ولا يُكتفى بالتقرير المحفوظ وقت الاكتمال).</summary>
    public async Task<JsonObject> ReportAsync(SigRequest r, IReadOnlyList<SigSigner> signers, CancellationToken ct)
    {
        var report = new JsonObject
        {
            ["checkedAt"] = DateTime.UtcNow, ["request"] = r.Reference, ["type"] = r.Type, ["typeLabel"] = Labels.Type(r.Type), ["provider"] = r.Provider,
            ["legalNotice"] = "لا يُدّعى أن أي نوع من التوقيع معتمد نظاميًا قبل نجاح التكامل الفعلي مع مزوّد معتمد ومراجعة متطلبات المكتب.",
        };
        var notes = new JsonArray();
        // 1) النسخة المرسلة للتوقيع لم تُستبدل
        var original = await store.ReadAsync(r.DocumentId, ct);
        var originalSha = original is null ? null : ESignatureGateway.Sha256(original);
        report["document"] = new JsonObject
        {
            ["expectedSha256"] = r.DocumentSha256, ["actualSha256"] = originalSha, ["intact"] = originalSha == r.DocumentSha256, ["versionNo"] = r.VersionNo,
            ["contentHash"] = r.ContentHash,
        };
        if (originalSha != r.DocumentSha256) notes.Add("نسخة PDF المخزنة للتوقيع لا تطابق بصمتها المسجلة.");
        string overall;
        if (r.Type == SignatureTypes.InAppApproval)
        {
            report["approvals"] = new JsonArray(signers.Select(s => (JsonNode)new JsonObject
            {
                ["signer"] = s.Name, ["role"] = s.Role, ["status"] = s.Status, ["at"] = s.SignedAt, ["actedBy"] = s.ActedByName,
                ["onBehalf"] = s.ActedBy is { } a && a != s.UserId, ["reauth"] = s.EvidenceJson is null ? null : JsonNode.Parse(s.EvidenceJson)?["reauth"]?.DeepClone(),
                ["documentSha256"] = s.SignedDocumentSha256,
            }).ToArray());
            var allSigned = signers.All(s => s.Status == "signed");
            var boundToDocument = signers.Where(s => s.Status == "signed").All(s => s.SignedDocumentSha256 == r.DocumentSha256);
            overall = originalSha != r.DocumentSha256 || !boundToDocument ? "invalid" : allSigned ? "documented_approval" : "incomplete";
            notes.Add("موافقة إلكترونية موثقة داخل النظام: هوية الدخول، وإعادة التحقق من الهوية، والوقت، وبصمة النسخة، وسجل تدقيق مقاوم للتلاعب. ليست توقيعًا رقميًا بشهادة تشفير.");
        }
        else
        {
            var signedId = r.SignedDocumentId ?? r.CurrentDocumentId;
            var signedBytes = signedId is { } sid && sid != r.DocumentId ? await store.ReadAsync(sid, ct) : null;
            if (signedBytes is null)
            {
                overall = "incomplete";
                notes.Add("لم تصدر بعد نسخة تحمل توقيعًا رقميًا.");
            }
            else
            {
                var pdf = await VerifyPdfAsync(signedBytes, ct);
                var preserved = original is not null && signedBytes.Length >= original.Length && signedBytes.AsSpan(0, original.Length).SequenceEqual(original);
                report["signedDocument"] = new JsonObject
                {
                    ["sha256"] = pdf.FileSha256, ["expectedSha256"] = r.SignedDocumentSha256 ?? r.CurrentDocumentSha256,
                    ["matchesRecord"] = pdf.FileSha256 == (r.SignedDocumentSha256 ?? r.CurrentDocumentSha256),
                    ["originalPreserved"] = preserved, ["modifiedAfterLastSignature"] = pdf.ModifiedAfterLastSignature,
                };
                report["pdfSignatures"] = JsonSerializer.SerializeToNode(pdf.Signatures, Json.Web);
                foreach (var n in pdf.Notes) notes.Add(n);
                if (!preserved) notes.Add("النسخة الموقعة لا تبدأ بالنسخة الأصلية المرسلة للتوقيع.");
                var expectedSignatures = signers.Count(s => s.Status == "signed");
                if (pdf.Signatures.Count < expectedSignatures) notes.Add("عدد التوقيعات الرقمية في الملف أقل من عدد الموقّعين المسجلين.");
                overall = !preserved || pdf.FileSha256 != (r.SignedDocumentSha256 ?? r.CurrentDocumentSha256) ? "invalid"
                    : pdf.Overall is "invalid" or "no_signatures" ? "invalid"
                    : pdf.Signatures.Count < expectedSignatures ? "invalid"
                    : signers.Any(s => s.Status != "signed") ? "incomplete" : pdf.Overall;
            }
        }
        report["overall"] = overall;
        report["notes"] = notes;
        return report;
    }

    /// <summary>تحقق من ملف مرفوع: توقيعاته، ومطابقته لنسخة موقعة مسجلة في النظام إن وُجدت.</summary>
    public async Task<JsonObject> VerifyUploadAsync(byte[] file, CancellationToken ct)
    {
        var pdf = await VerifyPdfAsync(file, ct);
        var sha = pdf.FileSha256;
        var match = await db.QuerySingleAsync("""
            SELECT reference_no, signature_type, version_no FROM sig.requests WHERE signed_document_sha256 = @sha OR current_document_sha256 = @sha OR document_sha256 = @sha
            ORDER BY created_at DESC LIMIT 1
            """, new { sha }, r => new JsonObject { ["reference"] = r.GetString(0), ["type"] = r.GetString(1), ["versionNo"] = r.GetInt32(2) }, ct);
        return new JsonObject
        {
            ["checkedAt"] = DateTime.UtcNow, ["sha256"] = sha, ["length"] = pdf.FileLength, ["matchesRecord"] = match,
            ["signatures"] = JsonSerializer.SerializeToNode(pdf.Signatures, Json.Web), ["modifiedAfterLastSignature"] = pdf.ModifiedAfterLastSignature,
            ["overall"] = pdf.Overall, ["notes"] = new JsonArray(pdf.Notes.Select(n => (JsonNode)n).ToArray()),
        };
    }
}
