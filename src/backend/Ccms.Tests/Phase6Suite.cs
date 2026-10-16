using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Pdf;
using Ccms.Modules.Signatures.Crypto;
using Ccms.Modules.Signatures.Pdf;

namespace Ccms.Tests;

/// <summary>سيناريوهات قبول وحدة التوقيع الإلكتروني المستقلة (ADR-020) — طرفًا لطرف عبر HTTP على قاعدة حقيقية، مع المزوّد التجريبي (mock) فقط.
/// لا يُدّعى في أي اختبار أن التوقيع معتمد نظاميًا.</summary>
public sealed class Phase6Suite(TestContext ctx)
{
    private const string Pw = "Test@Password2026";
    private const string MockSecret = "test-only-mock-callback-secret-2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private Guid _body;
    private Guid _reqInApp, _reqDigital, _reqProvider, _minutesDigital;
    private string _providerRef = "";

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p6-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    private async Task Flags(params (string Key, string Value)[] flags)
    {
        var admin = ctx.AdminClient!;
        var current = await admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, v) in flags)
            if (current[k]!["value"]!.GetValue<string>() != v)
                await Check.Status(await admin.PutAsync($"/api/v1/admin/features/{k}", new { value = v, reason = $"اختبار وحدة التوقيع: {k}={v}", confirmImpact = true }), 200, k + "=" + v);
    }

    /// <summary>اجتماع منعقد ومغلق ومحضر بقرار، جاهز للرفع.</summary>
    private async Task<Guid> NewMinutesAsync(string title, string meetingType = "regular")
    {
        var sec = await As("sec");
        var meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _body, titleAr = title, meetingType, startsAt = DateTime.UtcNow.AddMinutes(30), location = "القاعة" }),
            201, "اجتماع " + title)).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/schedule"), 200, "جدولة");
        var item = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/agenda-items", new { titleAr = "اعتماد " + title }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/invitations/send"), 200, "دعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{meeting}/attendance", new
        {
            records = new[] { "chair", "m1", "m2", "m3" }.Select(u => new { userId = _u[u], status = "present", mode = "in_person" }).ToArray(),
        }), 200, "حضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/open", new { }), 200, "افتتاح");
        var minutes = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/minutes"), 201, "محضر")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/{minutes}/resolutions", new
        {
            resolutionType = "decision", titleAr = "قرار " + title, agendaItemId = item, category = "administrative", textAr = $"قررت اللجنة اعتماد {title}.",
        }), 201, "قرار");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/close"), 200, "إغلاق");
        return minutes;
    }

    private async Task<JsonNode> SubmitAsync(Guid minutes) =>
        (await Check.Status(await (await As("sec")).PostAsync($"/api/v1/minutes/{minutes}/submit", new { }), 200, "رفع المحضر"))!;

    private async Task<JsonNode> Esign(Guid minutes, string user = "sec") => await (await As(user)).GetJsonAsync($"/api/v1/esign/minutes/{minutes}");
    private async Task<JsonNode> Card(Guid id, string user = "sec") => await (await As(user)).GetJsonAsync($"/api/v1/esign/requests/{id}");
    private async Task<string> MinutesStatus(Guid id) => (await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{id}"))["minutes"].S("status");

    private async Task<string> ReauthAsync(string user) =>
        (await Check.Status(await (await As(user)).PostAsync("/api/v1/esign/reauth", new { password = Pw }), 200, "إعادة التحقق " + user)).S("reauthToken");

    private async Task SignAsync(string user, Guid request, Guid? onBehalfOf = null) =>
        await Check.Status(await (await As(user)).PostOnceAsync($"/api/v1/esign/requests/{request}/sign", new { reauthToken = await ReauthAsync(user), onBehalfOf }, Key()),
            200, "توقيع " + user);

    private async Task<byte[]> DocumentAsync(Guid docId) => await (await As("sec")).GetBytesAsync($"/api/v1/documents/{docId}/content");

    private async Task<JsonNode> VerifyUploadAsync(byte[] pdf)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "check.pdf");
        return (await Check.Status(await (await As("chair")).PostContentAsync("/api/v1/esign/verify", content), 200, "تحقق ملف مرفوع"))!;
    }

    private static string Hmac(string body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MockSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private async Task<HttpResponseMessage> CallbackAsync(string body, string? signature)
    {
        using var http = new HttpClient { BaseAddress = new Uri(Url) };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/esign/callbacks/mock") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null) req.Headers.Add("X-Mock-Signature", signature);
        return await http.SendAsync(req);
    }

    private async Task<JsonNode> Maintenance(DateTime? asOf = null) =>
        (await Check.Status(await ctx.AdminClient!.PostAsync("/api/v1/esign/maintenance/run", new { asOf }), 200, "صيانة التوقيع"))!;

    // ============================ المحرك التشفيري ============================

    [Test("P6-00", "محرك توقيع PDF: توقيعان متتابعان بتحديث تزايدي يتحقق منهما، واكتشاف تعديل المحتوى والإلحاق بعد التوقيع", 0)]
    public Task Engine()
    {
        var report = new PdfReport(new PdfReportOptions { Title = "اختبار التوقيع", HeaderLine1 = "المكتب الاستراتيجي لتطوير منطقة الجوف" });
        report.Title("محضر تجريبي للتوقيع");
        report.Paragraph("نص عربي للتحقق من سلامة النسخة الموقعة.");
        var original = report.Build();
        using var root = TestCertificates.CreateRoot();
        using var a = TestCertificates.IssueSigner(root, "عضو أ", "u-a");
        using var b = TestCertificates.IssueSigner(root, "رئيس اللجنة", "u-b");
        byte[] SignWith(byte[] pdf, System.Security.Cryptography.X509Certificates.X509Certificate2 cert, int n)
        {
            var p = PdfSigner.Prepare(pdf, "Sig" + n, cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), "اختبار", DateTime.UtcNow);
            return PdfSigner.Embed(p, CmsBuilder.SignDetached(p.Digest(), cert, [cert, root], DateTime.UtcNow));
        }
        var once = SignWith(original, a, 1);
        var twice = SignWith(once, b, 2);
        Check.That(twice.AsSpan(0, original.Length).SequenceEqual(original), "النسخة الأصلية محفوظة في بداية الملف");
        var v = PdfSignatureVerifier.Verify(twice, [], [root]);
        Check.Equal(2, v.Signatures.Count, "توقيعان");
        Check.That(v.Signatures.All(s => s.DigestMatches && s.SignatureValid && s.CertificateValidAtSigning), "كلاهما صحيح تشفيريًا");
        Check.Equal("signatures_only", v.Signatures[0].SubsequentChanges, "بعد الأول: توقيع فقط");
        Check.Equal("none", v.Signatures[1].SubsequentChanges, "الثاني يغطي الملف كاملًا");
        Check.Equal("test_only", v.Signatures[0].Chain, "السلسلة اختبارية لا موثوقة");
        Check.Equal("valid_test_only", v.Overall, "النتيجة: صالح للاختبار فقط");
        Check.Equal("valid_untrusted_chain", PdfSignatureVerifier.Verify(twice, [], []).Overall, "دون جهة الاختبار: سلسلة غير موثوقة");

        var tampered = (byte[])twice.Clone();
        var at = Encoding.Latin1.GetString(twice).IndexOf("/Title", StringComparison.Ordinal);
        tampered[at + 1] = (byte)'t';
        var tv = PdfSignatureVerifier.Verify(tampered, [], [root]);
        Check.That(!tv.Signatures[0].DigestMatches && tv.Overall == "invalid", "تعديل بايت في المحتوى يُكتشف");
        var appended = twice.Concat(Encoding.ASCII.GetBytes("\n% modified\n")).ToArray();
        var av = PdfSignatureVerifier.Verify(appended, [], [root]);
        Check.That(av.ModifiedAfterLastSignature && av.Overall == "invalid", "الإلحاق بعد التوقيع يُكتشف");
        return Task.CompletedTask;
    }

    // ============================ التهيئة ============================

    [Test("P6-SETUP", "تهيئة: لجنة بسياسة توقيع الحاضرين، وأعضاء ومفوَّض، والوحدة معطلة كحالة بدء", 1)]
    public async Task Setup()
    {
        await Flags(("E_SIGNATURE_ENABLED", "false"));
        var admin = ctx.AdminClient!;
        async Task User(string k, string[] roles) =>
            _u[k] = (await Check.Status(await admin.PostAsync("/api/v1/users", new
            {
                email = $"p6-{k}@test.jsdo.gov.sa", displayNameAr = "م6 " + k, systemRoles = roles, initialPassword = Pw, userType = "internal",
            }), 201, "مستخدم " + k)).G("id");
        foreach (var k in new[] { "chair", "sec", "m1", "m2", "m3", "deleg", "outsider" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        var gov = await As("gov");
        _body = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P6-SIG", bodyType = "permanent_committee", nameAr = "لجنة الحوكمة الرقمية", nameEn = "P6" }), 201, "لجنة")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member"), ("m2", "member"), ("m3", "member"), ("deleg", "member") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_body}/policy", new
        {
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false,
            votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = true,
            minutesReviewDays = 3, extensionApprover = "chair", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار وحدة التوقيع", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "present_members",
            minutesSignatureFallback = "attestation", minutesFinalApproval = "chair",
        }), 200, "سياسة بتوقيع الحاضرين");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/activate"), 200, "تفعيل");
    }

    // ============================ التعطيل ============================

    [Test("P6-01", "الوحدة معطلة: لا طلبات توقيع جديدة ولا إجراءات بدء، والمحاضر تُعتمد بالمسار البديل", 2)]
    public async Task Disabled()
    {
        var minutes = await NewMinutesAsync("خطة البيانات المفتوحة");
        Check.Equal("pending_signatures", (await SubmitAsync(minutes)).S("status"), "مرحلة التوقيع");
        var m = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutes}");
        Check.Equal("esign_disabled", m["signatureMethod"].S("reason"), "السبب موثق: الوحدة معطلة");
        Check.That(m["signoffs"]!.AsArray().All(s => s.S("method") == "attestation"), "الإقرار الداخلي (البديل) لكل الموقّعين");
        var e = await Esign(minutes);
        Check.That(!e["module"]!["enabled"]!.GetValue<bool>() && !e["canStart"]!.GetValue<bool>() && e["requests"]!.AsArray().Count == 0, "لا إجراء بدء ولا طلبات");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 404, "feature-disabled", "منع إنشاء طلب جديد");
        foreach (var u in new[] { "m1", "m2", "m3" })
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/minutes/{minutes}/signoffs", new { contentHash = m["currentVersion"].S("hash") }), 200, "إقرار " + u);
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "اكتملت الإقرارات بالبديل");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{minutes}/approve", new { contentHash = m["currentVersion"].S("hash") }), 200, "اعتماد");
        Check.Equal("approved", await MinutesStatus(minutes), "المحضر معتمد دون الوحدة");
    }

    // ============================ موافقة إلكترونية موثقة بالتتابع ============================

    [Test("P6-02", "التفعيل: موافقة إلكترونية موثقة بالتتابع (الأعضاء ثم الرئيس) بإعادة التحقق، ووسمها بأنها ليست توقيعًا رقميًا", 3)]
    public async Task InAppSequential()
    {
        await Flags(("E_SIGNATURE_ENABLED", "true"), ("E_SIGNATURE_METHOD", "in_app_approval"), ("E_SIGNATURE_MEMBER_REQUIRED", "true"),
            ("E_SIGNATURE_CHAIR_REQUIRED", "true"), ("E_SIGNATURE_SECRETARY_REQUIRED", "false"), ("E_SIGNATURE_SEQUENTIAL", "true"), ("E_SIGNATURE_REAUTH_REQUIRED", "true"),
            ("E_SIGNATURE_PDF_OUTPUT", "true"));
        var minutes = await NewMinutesAsync("سياسة الحوسبة السحابية");
        Check.Equal("pending_signatures", (await SubmitAsync(minutes)).S("status"), "مرحلة التوقيع");
        var e = await Esign(minutes);
        var r = e["requests"]![0]!;
        _reqInApp = r.G("id");
        Check.That(r.S("type") == "in_app_approval" && r.S("status") == "in_progress" && r.S("typeNotice").Contains("ليست توقيعًا رقميًا"), "النوع موسوم بدقة");
        var signers = r["signers"]!.AsArray();
        Check.Equal(4, signers.Count, "ثلاثة أعضاء ثم الرئيس");
        Check.That(signers[3].S("role") == "chair" && signers[0].S("status") == "pending" && signers.Skip(1).All(s => s.S("status") == "waiting"), "الأول بانتظاره والبقية بالدور");
        var m = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutes}");
        Check.That(m["signoffs"]!.AsArray().All(s => s.S("method") == "in_app_approval"), "إقرارات المحضر مرتبطة بالوحدة");
        Check.That(r.S("documentSha256").Length == 64 && r.S("contentHash") == m["currentVersion"].S("hash"), "الطلب مربوط بالإصدار الثابت وبصمته");

        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{_reqInApp}/sign", new { reauthToken = await ReauthAsync("m2") }), 409, "not-your-turn", "بالتتابع");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{_reqInApp}/sign", new { }), 403, "reauth-required", "إعادة التحقق إلزامية");
        await Check.Problem(await (await As("m1")).PostAsync("/api/v1/esign/reauth", new { password = "Wrong@Password1" }), 403, "reauth-failed", "كلمة مرور خاطئة");
        var token = await ReauthAsync("m1");
        await Check.Status(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{_reqInApp}/sign", new { reauthToken = token }), 200, "موافقة m1");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{_reqInApp}/sign", new { reauthToken = token }), 409, "already-signed", "لا توقيع مكرر");
        await SignAsync("m2", _reqInApp);
        await SignAsync("m3", _reqInApp);
        Check.Equal("pending_signatures", await MinutesStatus(minutes), "الرئيس لم يوقّع بعد");
        await SignAsync("chair", _reqInApp);
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "اكتملت التوقيعات فتقدّم المحضر");
        var card = await Card(_reqInApp);
        Check.Equal("completed", card.S("status"), "الطلب مكتمل");
        Check.Equal("documented_approval", card["verification"].S("overall"), "التحقق: موافقة موثقة لا توقيع رقمي");
        Check.That(card["signers"]!.AsArray().All(s => s["reauth"] is not null), "إعادة التحقق مسجلة لكل موقّع");
        Check.That(card["events"]!.AsArray().Count(x => x.S("kind") == "signed") == 4, "سجل الأحداث");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{minutes}/approve", new { contentHash = m["currentVersion"].S("hash") }), 200, "اعتماد نهائي");
        var v = await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{_reqInApp}/verify");
        Check.That(v["document"]!["intact"]!.GetValue<bool>() && v.S("overall") == "documented_approval", "إعادة التحقق لاحقًا: النسخة سليمة");
        var pdf = await (await As("chair")).GetBytesAsync($"/api/v1/minutes/{minutes}/pdf");
        Check.That(pdf.Length > 1000 && Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-", "PDF المعتمد صادر مع سجل الاعتماد");
    }

    // ============================ الرفض وطلب التعديل ============================

    [Test("P6-03", "رفض أحد الأعضاء التوقيع وطلب التعديل: يعاد المحضر بالسبب ويبقى الطلب المرفوض في السجل", 4)]
    public async Task Decline()
    {
        var minutes = await NewMinutesAsync("دليل إدارة المخاطر");
        await SubmitAsync(minutes);
        var r = (await Esign(minutes))["requests"]![0]!.G("id");
        await SignAsync("m1", r);
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{r}/decline", new { reason = "x" }), 400, "validation", "السبب إلزامي");
        await Check.Status(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{r}/decline", new { reason = "البند الثاني يحتاج تصحيح الصياغة" }), 200, "رفض m2");
        var m = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutes}");
        Check.That(m["minutes"].S("status") == "returned" && m["minutes"].S("statusReason").Contains("رفض التوقيع"), "أُعيد المحضر للتعديل بسبب الرفض");
        var card = await Card(r);
        Check.That(card.S("status") == "declined" && card["signers"]![0].S("status") == "signed" && card["signers"]![1].S("declineReason") == "البند الثاني يحتاج تصحيح الصياغة",
            "الرفض وسببه، والتوقيع السابق محفوظ");
        await SubmitAsync(minutes);
        var reqs = (await Esign(minutes))["requests"]!.AsArray();
        Check.That(reqs.Count == 2 && reqs.Count(x => x.S("status") == "in_progress") == 1 && reqs.Any(x => x.S("status") == "declined"), "طلب جديد والمرفوض باقٍ في السجل");
    }

    // ============================ التوقيع الرقمي على PDF والتعديل بعد توقيع بعضهم ============================

    [Test("P6-04", "توقيع رقمي تشفيري متتابع على PDF، وتعديل المحضر بعد توقيع بعضهم بإبطال موثق وإصدار جديد، والتحقق من سلامة النسخة الموقعة", 5)]
    public async Task DigitalAndAmend()
    {
        await Flags(("E_SIGNATURE_PROVIDER", "mock"), ("E_SIGNATURE_METHOD", "pdf_digital_signature"));
        _minutesDigital = await NewMinutesAsync("إطار حوكمة البيانات");
        var minutes = _minutesDigital;
        await SubmitAsync(minutes);
        var first = (await Esign(minutes))["requests"]![0]!;
        var r1 = first.G("id");
        Check.That(first.S("type") == "pdf_digital_signature" && first.S("provider") == "mock" && !first["productionGrade"]!.GetValue<bool>(), "توقيع رقمي عبر المزوّد التجريبي (ليس إنتاجيًا)");
        var original = await DocumentAsync(first.G("documentId"));
        Check.Equal(first.S("documentSha256"), Convert.ToHexStringLower(SHA256.HashData(original)), "نسخة التوقيع المخزنة تطابق بصمتها");
        await SignAsync("m1", r1);
        await SignAsync("m2", r1);
        var mid = await Card(r1);
        Check.That(mid["signers"]![0]["certificate"].S("subject").Contains("م6 m1") && mid["signers"]![0]["certificate"].S("chain") == "test_only", "شهادة الموقّع وسلسلتها الاختبارية مسجلة");
        Check.That(mid.S("currentDocumentSha256") != first.S("documentSha256"), "مراجعة موقعة جديدة لا تستبدل الأصل");

        // تعديل المحضر بعد توقيع عضوين: إبطال موثق، ثم إصدار جديد وطلب جديد
        var sec = await As("sec");
        await Check.Problem(await sec.PutAsync($"/api/v1/minutes/{minutes}/draft", new { sections = new[] { new { key = "opening", title = "الافتتاح", body = "نص معدل" } } }),
            409, "minutes-in-review", "لا تعديل صامت أثناء التوقيع");
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/{minutes}/return", new { reason = "تصحيح نص القرار بعد ملاحظة قانونية" }), 200, "إعادة للتعديل");
        var voided = await Card(r1);
        Check.That(voided.S("status") == "voided" && voided.S("statusReason").Contains("تصحيح نص القرار"), "الطلب مُبطل بسبب موثق");
        Check.That(voided["signers"]!.AsArray().Count(s => s.S("status") == "signed") == 2 && voided["signers"]!.AsArray().Count(s => s.S("status") == "cancelled") == 2,
            "التوقيعات المكتملة باقية في السجل، والبقية ملغاة");
        var oldReport = await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{r1}/verify");
        Check.That(oldReport["pdfSignatures"]!.AsArray().Count == 2 && oldReport["pdfSignatures"]!.AsArray().All(s => s!["digestMatches"]!.GetValue<bool>() && s["signatureValid"]!.GetValue<bool>()),
            "توقيعا الإصدار السابق ما يزالان قابلين للتحقق");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{minutes}/draft", new { sections = new[] { new { key = "opening", title = "الافتتاح", body = "افتتح الرئيس الاجتماع؛ وصُحح نص القرار." } } }),
            200, "تعديل المسودة");
        Check.Equal(2, (await SubmitAsync(minutes))["versionNo"]!.GetValue<int>(), "إصدار جديد");
        var reqs = (await Esign(minutes))["requests"]!.AsArray();
        var second = reqs.First(x => x.S("status") == "in_progress");
        _reqDigital = second.G("id");
        Check.That(second.S("documentSha256") != first.S("documentSha256") && second["versionNo"]!.GetValue<int>() == 2, "طلب جديد بنسخة وبصمة جديدتين");
        foreach (var u in new[] { "m1", "m2", "m3", "chair" }) await SignAsync(u, _reqDigital);
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "اكتمل التوقيع الرقمي");
        var done = await Card(_reqDigital);
        Check.Equal("completed", done.S("status"), "مكتمل");
        var report = await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{_reqDigital}/verify");
        Check.Equal("valid_test_only", report.S("overall"), "صالح تقنيًا بسلسلة اختبارية — لا يُعرض موثوقًا");
        Check.That(report["pdfSignatures"]!.AsArray().Count == 4 && report["signedDocument"]!["originalPreserved"]!.GetValue<bool>(), "أربعة توقيعات والأصل محفوظ");
        Check.That(report["notes"]!.AsArray().Any(n => n!.GetValue<string>().Contains("إلغاء الشهادات")), "يُصرَّح بعدم فحص الإلغاء");

        var signed = await DocumentAsync(done.G("signedDocumentId"));
        var up = await VerifyUploadAsync(signed);
        Check.That(up.S("overall") == "valid_test_only" && up["matchesRecord"] is not null, "النسخة الموقعة المنزلة تطابق السجل");
        var tampered = (byte[])signed.Clone();
        tampered[original.Length / 2] ^= 0x01;
        Check.Equal("invalid", (await VerifyUploadAsync(tampered)).S("overall"), "تعديل بايت في النسخة الموقعة يُكتشف");
        var appended = signed.Concat(Encoding.ASCII.GetBytes("\n%EOF-extra\n")).ToArray();
        var av = await VerifyUploadAsync(appended);
        Check.That(av.S("overall") == "invalid" && av["modifiedAfterLastSignature"]!.GetValue<bool>(), "إلحاق بيانات بعد التوقيع يُكتشف");
        var m = await sec.GetJsonAsync($"/api/v1/minutes/{minutes}");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{minutes}/approve", new { contentHash = m["currentVersion"].S("hash") }), 200, "اعتماد");
        var docs = (await sec.GetJsonAsync($"/api/v1/documents?ownerType=minutes&ownerId={minutes}")).AsArray();
        Check.That(docs.Any(d => d.S("purpose") == "signed_pdf"), "النسخة الموقعة النهائية مرفقة بالمحضر (E_SIGNATURE_PDF_OUTPUT)");
    }

    // ============================ مزوّد خارجي (تجريبي) ============================

    [Test("P6-05", "التوقيع عبر مزوّد خارجي (تجريبي): الإرسال، والجلسات بالتسلسل، والاستدعاءات الراجعة الموقعة، ورفض المزوّر والمكرر وغير المطابق، والتعليق والاستئناف", 6)]
    public async Task Provider()
    {
        await Flags(("E_SIGNATURE_METHOD", "provider_esign"));
        var minutes = await NewMinutesAsync("خطة استمرارية الأعمال");
        await SubmitAsync(minutes);
        var r = (await Esign(minutes))["requests"]![0]!;
        _reqProvider = r.G("id");
        Check.Equal("queued", r.S("status"), "بانتظار الإرسال للمزوّد");
        var run = await Maintenance();
        Check.That(run["dispatched"]!.GetValue<int>() >= 1, "أُرسل المستند");
        var card = await Card(_reqProvider);
        _providerRef = card.S("providerRef");
        Check.That(card.S("status") == "in_progress" && _providerRef.StartsWith("MOCK-"), "مغلف لدى المزوّد");

        var s1 = await Check.Status(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{_reqProvider}/provider-session", new { reauthToken = await ReauthAsync("m1") }), 200, "جلسة m1");
        var token1 = s1.S("url").Split('/').Last();
        var session = await (await As("m1")).GetJsonAsync($"/api/v1/esign/mock/sessions/{token1}");
        Check.That(session["testOnly"]!.GetValue<bool>() && session.S("notice").Contains("ليس خدمة توقيع إنتاجية"), "صفحة المزوّد التجريبي موسومة بوضوح");
        await Check.Problem(await (await As("m2")).GetAsync($"/api/v1/esign/mock/sessions/{token1}"), 403, "not-your-session", "جلسة موقّع آخر");
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{_reqProvider}/provider-session", new { reauthToken = await ReauthAsync("m2") }), 409, "not-your-turn", "بالتسلسل");
        await Check.Status(await (await As("m1")).PostAsync($"/api/v1/esign/mock/sessions/{token1}/sign"), 200, "توقيع m1 لدى المزوّد");
        Check.Equal("signed", (await Card(_reqProvider))["signers"]![0].S("status"), "طُبق الاستدعاء الراجع");

        // استدعاءات راجعة: مزوّرة، ومكررة، وغير مطابقة للطلب
        var forged = $$"""{"eventId":"{{_providerRef}}-99","providerRef":"{{_providerRef}}","correlationId":"{{_reqProvider}}","event":"completed"}""";
        await Check.Problem(await CallbackAsync(forged, "sha256=" + new string('0', 64)), 401, "callback-signature-invalid", "توقيع مزوّر");
        await Check.Problem(await CallbackAsync(forged, null), 401, "callback-signature-invalid", "دون توقيع");
        var dup = $$"""{"eventId":"{{_providerRef}}-1","providerRef":"{{_providerRef}}","correlationId":"{{_reqProvider}}","event":"signer_signed","signerRef":"x"}""";
        Check.Equal("duplicate", (await Check.Status(await CallbackAsync(dup, Hmac(dup)), 200, "مكرر")).S("outcome"), "الحدث المكرر لا يُطبق مرتين");
        var mismatch = $$"""{"eventId":"{{_providerRef}}-77","providerRef":"{{_providerRef}}","correlationId":"{{Guid.NewGuid()}}","event":"completed"}""";
        await Check.Problem(await CallbackAsync(mismatch, Hmac(mismatch)), 409, "callback-mismatch", "لا يعود للطلب الصحيح");
        var callbacks = (await Card(_reqProvider))["callbacks"]!.AsArray();
        Check.That(callbacks.Any(c => c.S("outcome") == "rejected_mismatch") && callbacks.Any(c => c.S("outcome") == "applied"), "سجل الاستدعاءات ونتائجها");

        // تعليق صريح: الحدث الوارد يُحفظ ولا يُطبق حتى الاستئناف
        await Check.Status(await (await As("sec")).PostAsync($"/api/v1/esign/requests/{_reqProvider}/hold", new { reason = "مراجعة مع الشؤون القانونية" }), 200, "تعليق");
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{_reqProvider}/provider-session", new { reauthToken = await ReauthAsync("m2") }), 409, "request-on-hold", "لا جلسات أثناء التعليق");
        var mock2 = await (await As("m2")).GetJsonAsync($"/api/v1/esign/requests/{_reqProvider}");
        Check.Equal("on_hold", mock2.S("status"), "معلّق");
        await Check.Status(await (await As("sec")).PostAsync($"/api/v1/esign/requests/{_reqProvider}/resume"), 200, "استئناف");
        foreach (var u in new[] { "m2", "m3", "chair" })
        {
            var s = await Check.Status(await (await As(u)).PostAsync($"/api/v1/esign/requests/{_reqProvider}/provider-session", new { reauthToken = await ReauthAsync(u) }), 200, "جلسة " + u);
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/esign/mock/sessions/{s.S("url").Split('/').Last()}/sign"), 200, "توقيع لدى المزوّد " + u);
        }
        var done = await Card(_reqProvider);
        Check.Equal("completed", done.S("status"), "اكتمل بنسخة المزوّد النهائية");
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "تقدّم المحضر");
        var report = await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{_reqProvider}/verify");
        Check.That(report.S("overall") == "valid_test_only" && report["signedDocument"]!["originalPreserved"]!.GetValue<bool>(), "النسخة النهائية تحفظ الأصل وتتحقق باختبارية");
    }

    // ============================ انقطاع المزوّد ============================

    [Test("P6-06", "انقطاع اتصال المزوّد: إعادة المحاولة بتراجع، ثم الإرسال عند العودة، أو فشل صريح بعد الحد الأقصى؛ ولا أسرار في السجلات", 7)]
    public async Task Outage()
    {
        var admin = ctx.AdminClient!;
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = true, failNext = 0 }), 200, "انقطاع المزوّد");
        var minutes = await NewMinutesAsync("سياسة النسخ الاحتياطي");
        await SubmitAsync(minutes);
        var r = (await Esign(minutes))["requests"]![0]!.G("id");
        await Maintenance();
        var c1 = await Card(r);
        Check.That(c1.S("status") == "queued" && c1["dispatchAttempts"]!.GetValue<int>() == 1 && c1.S("lastError").Length > 0, "المحاولة الأولى فشلت والطلب باقٍ");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{r}/provider-session", new { reauthToken = await ReauthAsync("m1") }),
            503, "esign-provider-unavailable", "رسالة انقطاع واضحة للموقّع");
        Check.Equal(2, (await Card(r))["dispatchAttempts"]!.GetValue<int>(), "محاولة ثانية");
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = false, failNext = 0 }), 200, "عودة المزوّد");
        await Maintenance(DateTime.UtcNow.AddMinutes(30));
        var c2 = await Card(r);
        Check.That(c2.S("status") == "in_progress" && c2.S("providerRef").StartsWith("MOCK-"), "أُرسل بعد عودة الاتصال");
        var kinds = c2["events"]!.AsArray().Select(e => e.S("kind")).ToList();
        Check.That(kinds.Contains("dispatch_failed") && kinds.Contains("dispatched"), "سجل المحاولات");
        Check.That(!c2.Raw().Contains(MockSecret) && !c2.Raw().Contains("token\""), "لا أسرار مزوّد في السجل");

        // انقطاع أثناء التوقيع الرقمي: لا يُحفظ شيء ويبقى الدور
        await Flags(("E_SIGNATURE_METHOD", "pdf_digital_signature"));
        var dm = await NewMinutesAsync("ميثاق تقنية المعلومات");
        await SubmitAsync(dm);
        var dr = (await Esign(dm))["requests"]![0]!.G("id");
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = false, failNext = 5 }), 200, "أعطال متتالية");
        await Check.Problem(await (await As("m1")).PostOnceAsync($"/api/v1/esign/requests/{dr}/sign", new { reauthToken = await ReauthAsync("m1") }, Key()),
            503, "esign-provider-unavailable", "تعذر التوقيع بعد إعادة المحاولة");
        var dc = await Card(dr);
        Check.That(dc["signers"]![0].S("status") == "pending" && dc.S("currentDocumentSha256") == dc.S("documentSha256"), "لم يُحفظ توقيع جزئي");
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = false, failNext = 0 }), 200, "إزالة الأعطال");
        await SignAsync("m1", dr);

        // فشل صريح بعد الحد الأقصى للمحاولات
        await Flags(("E_SIGNATURE_METHOD", "provider_esign"));
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = true, failNext = 0 }), 200, "انقطاع طويل");
        var fm = await NewMinutesAsync("دليل الأمن السيبراني");
        await SubmitAsync(fm);
        var fr = (await Esign(fm))["requests"]![0]!.G("id");
        for (var i = 0; i < 3; i++) await Maintenance(DateTime.UtcNow.AddHours(i + 1));
        var fc = await Card(fr);
        Check.That(fc.S("status") == "failed" && fc.S("statusReason").Contains("3 محاولات"), "فشل صريح بعد الحد الأقصى");
        var notes = (await (await As("sec")).GetJsonAsync("/api/v1/notifications?pageSize=100")).Items().Select(n => n.S("eventType")).ToList();
        Check.That(notes.Contains("signature_failed"), "أُشعر أمين السر");
        await Check.Status(await admin.PutAsync("/api/v1/esign/mock/faults", new { unavailable = false, failNext = 0 }), 200, "عودة");
        var restarted = await Check.Status(await (await As("sec")).PostOnceAsync($"/api/v1/esign/minutes/{fm}/requests", new { }, Key()), 200, "طلب جديد صريح");
        Check.That(restarted.G("requestId") != fr, "طلب جديد بعد الفشل");
    }

    // ============================ التعطيل أثناء طلب جارٍ ثم التفعيل ============================

    [Test("P6-07", "تعطيل الوحدة وطلب جارٍ: تأكيد صريح، وتجميد دون تغيير الحالة، وبقاء التوقيعات السابقة قابلة للتحقق، وتحويل صريح للبديل، ثم إعادة التفعيل", 8)]
    public async Task DisableWhileLive()
    {
        await Flags(("E_SIGNATURE_METHOD", "in_app_approval"));
        var minutes = await NewMinutesAsync("سياسة تصنيف البيانات");
        await SubmitAsync(minutes);
        var r = (await Esign(minutes))["requests"]![0]!.G("id");
        await SignAsync("m1", r);
        var admin = ctx.AdminClient!;
        var noConfirm = await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "false", reason = "إيقاف الوحدة للصيانة" });
        var body = await Check.Status(noConfirm, 409, "يتطلب تأكيدًا");
        Check.That(body!["impact"]!.AsArray().Any(i => i.S("code") == "esign_live_requests" && i!["count"]!.GetValue<int>() > 0), "الأثر يذكر الطلبات الجارية");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "false", reason = "إيقاف الوحدة للصيانة بعد مراجعة الأثر", confirmImpact = true }), 200, "تعطيل مؤكد");

        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{r}/sign", new { reauthToken = await ReauthAsync("m2") }), 409, "esign-disabled", "لا توقيع أثناء التعطيل");
        var frozen = await Card(r);
        Check.That(frozen.S("status") == "in_progress" && frozen["manage"]!["actionRequired"]!.GetValue<bool>() && !frozen["my"]!["canSign"]!.GetValue<bool>(),
            "الحالة لم تتغير آليًا، ويلزم إجراء صريح، وإجراءات التوقيع مخفية");
        Check.Equal("signed", frozen["signers"]![0].S("status"), "التوقيع السابق محفوظ");
        Check.Equal("documented_approval", (await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{_reqInApp}/verify")).S("overall"), "طلب سابق قابل للتحقق أثناء التعطيل");
        Check.Equal("valid_test_only", (await (await As("chair")).GetJsonAsync($"/api/v1/esign/requests/{_reqDigital}/verify")).S("overall"), "النسخة الموقعة رقميًا قابلة للتحقق أثناء التعطيل");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 404, "feature-disabled", "لا طلبات جديدة");

        await Check.Status(await (await As("sec")).PostAsync($"/api/v1/esign/requests/{r}/fallback", new { reason = "الوحدة معطلة للصيانة؛ يُستكمل بالإقرار الداخلي" }), 200, "تحويل صريح للبديل");
        var after = await Card(r);
        Check.That(after.S("status") == "cancelled" && after.S("statusReason").Contains("تحويل صريح للمسار البديل"), "الطلب أُغلق بإجراء مسجل");
        var m = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutes}");
        var signoffs = m["signoffs"]!.AsArray();
        Check.That(signoffs.Any(s => s.S("method") == "in_app_approval" && s.S("status") == "valid") && signoffs.Count(s => s.S("method") == "attestation" && s.S("status") == "requested") == 3,
            "الموافقة المكتملة صالحة، والبقية بالبديل");
        foreach (var u in new[] { "m2", "m3", "chair" })
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/minutes/{minutes}/signoffs", new { contentHash = m["currentVersion"].S("hash") }), 200, "إقرار بديل " + u);
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "استمرت إدارة المحاضر");

        await Flags(("E_SIGNATURE_ENABLED", "true"));
        var st = await (await As("sec")).GetJsonAsync("/api/v1/esign/status");
        Check.That(st["enabled"]!.GetValue<bool>() && st["ready"]!.GetValue<bool>(), "أُعيد التفعيل وأصبح إنشاء الطلبات ممكنًا");
        var hist = (await admin.GetJsonAsync("/api/v1/admin/features/history?key=E_SIGNATURE_ENABLED")).Items();
        Check.That(hist.Any(h => h.S("result") == "confirmation_required") && hist.Count(h => h.S("result") == "succeeded") >= 2, "تعطيل ثم تفعيل مسجلان");
    }

    // ============================ عدم التخويل ============================

    [Test("P6-08", "محاولة غير المخول بدء التوقيع أو إدارته أو التوقيع عن غيره", 9)]
    public async Task Unauthorized()
    {
        var minutes = await NewMinutesAsync("سياسة الاستخدام المقبول");
        await SubmitAsync(minutes);
        var r = (await Esign(minutes))["requests"]![0]!.G("id");
        // الطلب حيّ: لا يمكن إنشاء آخر حتى لصاحب الصلاحية
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 409, "request-live", "طلب جارٍ على الإصدار");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 403, "forbidden-action", "العضو لا يبدأ التوقيع");
        await Check.Problem(await (await As("outsider")).PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 404, "not-found", "من خارج الجهة");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/esign/requests/{r}"), 404, "not-found", "لا اطلاع لمن خارج الجهة");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{r}/fallback", new { reason = "تحويل غير مخول" }), 403, "forbidden-action", "العضو لا يدير الطلبات");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/esign/requests/{r}/sign", new { reauthToken = await ReauthAsync("sec") }), 403, "not-a-signer", "غير الموقّع");
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{r}/sign", new { reauthToken = await ReauthAsync("m2"), onBehalfOf = _u["m1"] }), 403,
            "delegation-not-allowed", "لا توقيع بالنيابة عن عضو");
        await Check.Problem(await (await As("sec")).PostAsync("/api/v1/esign/routes", new
        { nameAr = "مسار", steps = new[] { new { order = 1, role = "chair", allowDelegation = false } }, expiryDays = 7, reason = "محاولة غير مخولة" }), 403, "forbidden-action", "المسارات لمسؤول التهيئة");
        await Check.Problem(await (await As("sec")).PutAsync("/api/v1/esign/mock/faults", new { unavailable = true, failNext = 0 }), 403, "forbidden-action", "حقن الأعطال للمسؤول فقط");
    }

    // ============================ المسارات والتفويض ============================

    [Test("P6-09", "مسار مختلف حسب الجهة ونوع الاجتماع (الرئيس فقط)، والتوقيع بالنيابة بتفويض موثق مسموح، والإعدادات مسجلة بقيمها", 10)]
    public async Task RoutesAndDelegation()
    {
        var gov = await As("gov");
        var created = await Check.Status(await gov.PostAsync("/api/v1/esign/routes", new
        {
            bodyId = _body, meetingType = "extraordinary", nameAr = "الرئيس فقط للاجتماعات الاستثنائية", steps = new[] { new { order = 1, role = "chair", allowDelegation = true } },
            sequential = true, signatureType = "in_app_approval", expiryDays = 7, reason = "اعتماد سريع للاجتماعات الاستثنائية",
        }), 201, "مسار الرئيس فقط");
        await Check.Problem(await gov.PostAsync("/api/v1/esign/routes", new
        { nameAr = "خاطئ", steps = new[] { new { order = 1, role = "members", allowDelegation = true } }, expiryDays = 7, reason = "التفويض للأعضاء غير مسموح" }), 400, "validation", "التفويض لمرحلة الرئيس فقط");
        var list = await gov.GetJsonAsync("/api/v1/esign/routes");
        Check.That(list["routes"]!.AsArray().Any(x => x.G("id") == created.G("id")) && list["defaultRoute"]!["steps"]!.AsArray().Count == 2, "المسارات والمسار الافتراضي من الإعدادات");

        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/bodies/{_body}/delegations", new
        { delegateUserId = _u["deleg"], scope = "chair_authority", validFrom = DateTime.UtcNow.AddMinutes(-1), validTo = DateTime.UtcNow.AddDays(3), reason = "سفر الرئيس" }), 201, "تفويض موثق");
        var minutes = await NewMinutesAsync("قرار عاجل للتعاقد", "extraordinary");
        await SubmitAsync(minutes);
        var req = (await Esign(minutes))["requests"]![0]!;
        var r = req.G("id");
        Check.That(req["signers"]!.AsArray().Count == 1 && req["signers"]![0].S("role") == "chair" && req["route"].S("source") == "route", "مسار الجهة ونوع الاجتماع طُبّق");
        var dcard = await Card(r, "deleg");
        Check.That(dcard["my"]!["onBehalf"]!.AsArray().Count == 1, "المفوَّض يرى إمكانية التوقيع بالنيابة");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/esign/requests/{r}/sign", new { reauthToken = await ReauthAsync("m1"), onBehalfOf = _u["chair"] }), 403,
            "no-valid-delegation", "دون تفويض ساري");
        await SignAsync("deleg", r, _u["chair"]);
        var done = await Card(r);
        Check.That(done.S("status") == "completed" && done["signers"]![0]["onBehalf"]!.GetValue<bool>() && done["signers"]![0].S("actedBy") == "م6 deleg", "التوقيع بالنيابة موثق");
        var m = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutes}");
        Check.That(m["minutes"].S("status") == "pending_final_approval", "تقدّم المحضر بتوقيع المفوَّض");

        await Check.Status(await gov.PutAsync($"/api/v1/esign/routes/{created.G("id")}", new
        {
            nameAr = "الرئيس فقط (معدل)", steps = new[] { new { order = 1, role = "chair", allowDelegation = false } }, sequential = true, signatureType = "in_app_approval",
            expiryDays = 5, reason = "إلغاء التوقيع بالنيابة بعد مراجعة",
        }), 200, "تعديل المسار");
        Check.Equal(2, (await gov.GetJsonAsync("/api/v1/esign/routes"))["routes"]!.AsArray().First(x => x.G("id") == created.G("id"))!["version"]!.GetValue<int>(), "إصدار المسار");

        var h = (await ctx.AdminClient!.GetJsonAsync("/api/v1/admin/features/history?key=E_SIGNATURE_METHOD&result=succeeded")).Items();
        Check.That(h.Count >= 2 && h.All(x => x["oldValue"] is not null && x["newValue"] is not null && x["changedBy"] is not null && x.S("reason").Length > 0 && x["changedAt"] is not null),
            "كل تغيير إعداد: القيمة السابقة والجديدة والوقت والمسؤول والسبب");
        // إرجاع الحالة: الوحدة معطلة كما في بقية الاختبارات
        await Flags(("E_SIGNATURE_ENABLED", "false"));
    }
}
