using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>المرحلة النهائية — مراجعة الأمن السيبراني وسلامة السجلات (24 §2–3): الوصول إلى سجلات الغير بتغيير المعرّف، ورفع الملفات الضارة،
/// وترويسات الأمان، وحقن SQL، والجلسات، وتزوير عنوان العميل، وتجاوز مفاتيح التحكم، وثبات المعتمد، وسلسلة التدقيق، وبقاء البيانات عند تعطيل الوحدات.</summary>
public sealed class Phase9Suite(TestContext ctx) : IAsyncDisposable
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private readonly PgWireDataSource _ownerDs = new(ctx.OwnerConnectionString);
    private Db Owner => new(_ownerDs);
    private Guid _body, _other, _meeting, _minutes, _resolution, _task, _doc, _item;

    public async ValueTask DisposeAsync() => await _ownerDs.DisposeAsync();
    private ApiClient Admin => ctx.AdminClient!;

    private async Task<ApiClient> As(string user, bool fresh = false)
    {
        if (!fresh && _clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"s9-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return fresh ? c : _clients[user] = c;
    }

    private async Task Flags(params (string Key, string Value)[] flags)
    {
        var current = await Admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, v) in flags)
            if (current[k]!["value"]!.GetValue<string>() != v)
                await Check.Status(await Admin.PutAsync($"/api/v1/admin/features/{k}", new { value = v, reason = $"اختبار الأمن: {k}", confirmImpact = true }), 200, k + "=" + v);
    }

    private static MultipartFormDataContent File(string name, byte[] bytes, string contentType, string ownerType, Guid ownerId)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", name);
        content.Add(new StringContent(ownerType), "ownerType");
        content.Add(new StringContent(ownerId.ToString()), "ownerId");
        content.Add(new StringContent("attachment"), "purpose");
        return content;
    }

    [Test("S9-SETUP", "تهيئة: لجنة باجتماع ومحضر معتمد وقرار ومهمة ومرفق، ولجنة أخرى، ومستخدم من خارج الجهتين", 0)]
    public async Task Setup()
    {
        async Task User(string k, string[] roles) =>
            _u[k] = (await Check.Status(await Admin.PostAsync("/api/v1/users", new { email = $"s9-{k}@test.jsdo.gov.sa", displayNameAr = "أ9 " + k, systemRoles = roles, initialPassword = Pw, userType = "internal" }), 201, "مستخدم " + k)).G("id");
        foreach (var k in new[] { "chair", "sec", "m1", "osec", "out" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        await User("aud", ["auditor"]);
        var gov = await As("gov");
        object Policy() => new
        {
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false,
            votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = false,
            minutesReviewDays = 3, extensionApprover = "chair", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار الأمن", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "none", minutesFinalApproval = "chair",
        };
        _body = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "S9-SEC", bodyType = "permanent_committee", nameAr = "لجنة الأمن السيبراني (أ9)", nameEn = "S9" }), 201, "لجنة")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_body}/policy", Policy()), 200, "سياسة");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/activate"), 200, "تفعيل");
        _other = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "S9-OTH", bodyType = "permanent_committee", nameAr = "لجنة أخرى (أ9)", nameEn = "S9 other" }), 201, "لجنة أخرى")).G("id");
        foreach (var (u, role) in new[] { ("osec", "secretary"), ("out", "chair") })
            if (u == "osec") await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_other}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_other}/members", new { userId = _u["gov"], role = "chair" }), 201, "رئيس الأخرى");
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_other}/policy", Policy()), 200, "سياسة أخرى");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_other}/activate"), 200, "تفعيل أخرى");

        var sec = await As("sec");
        _meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _body, titleAr = "اجتماع سري للاختبار", startsAt = DateTime.UtcNow.AddMinutes(30), location = "القاعة" }), 201, "اجتماع")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/schedule"), 200, "جدولة");
        _item = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = "خطة الاستجابة للحوادث" }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/invitations/send"), 200, "دعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_meeting}/attendance", new
        { records = new[] { "chair", "sec", "m1" }.Select(u => new { userId = _u[u], status = "present", mode = "in_person" }).ToArray() }), 200, "حضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/open", new { }), 200, "افتتاح");
        _minutes = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/minutes"), 201, "محضر")).G("id");
        _resolution = (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        { resolutionType = "decision", titleAr = "اعتماد خطة الاستجابة", textAr = "قررت اللجنة اعتماد خطة الاستجابة للحوادث.", agendaItemId = _item, category = "technical" }), 201, "قرار")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/close"), 200, "إغلاق");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { }), 200, "رفع");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = s.S("contentHash") }), 200, "اعتماد");
        _task = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _body, resolutionId = _resolution, titleAr = "تحديث خطة الاستجابة", assigneeType = "member", assigneeUserId = _u["m1"], dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(9)), verificationRequired = false }), 201, "مهمة")).G("id");
        _doc = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", File("incident-plan.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 secret plan"), "application/pdf", "body", _body)), 201, "مرفق")).G("id");
    }

    // ============================ الوصول إلى سجلات الغير (IDOR) ============================

    [Test("S9-01", "لا وصول إلى سجلات جهة أخرى بتغيير المعرّف: قراءة وتعديل الاجتماع والمحضر وPDF والقرار والمهمة والمرفق وإصداراته والتوقيع والنشاط", 1)]
    public async Task Idor()
    {
        var reads = new[]
        {
            $"/api/v1/bodies/{_body}", $"/api/v1/meetings/{_meeting}", $"/api/v1/meetings/{_meeting}/activity", $"/api/v1/minutes/{_minutes}", $"/api/v1/minutes/{_minutes}/pdf",
            $"/api/v1/minutes/{_minutes}/versions", $"/api/v1/resolutions/{_resolution}", $"/api/v1/tasks/{_task}", $"/api/v1/documents/{_doc}", $"/api/v1/documents/{_doc}/content",
            $"/api/v1/documents/{_doc}/versions", $"/api/v1/documents?ownerType=body&ownerId={_body}", $"/api/v1/esign/minutes/{_minutes}", $"/api/v1/meetings/{_meeting}/invitations",
        };
        foreach (var user in new[] { "out", "osec" })
        {
            var c = await As(user);
            foreach (var path in reads)
            {
                var code = (int)(await c.GetAsync(path)).StatusCode;
                Check.That(code is 403 or 404, $"{user} يُمنع من {path} (HTTP {code})");
            }
            async Task Denied(HttpResponseMessage r, string what) => Check.That((int)r.StatusCode is 403 or 404, $"{user}: {what} مرفوض (HTTP {(int)r.StatusCode})");
            await Denied(await c.PutAsync($"/api/v1/minutes/{_minutes}/draft", new { sections = Array.Empty<object>() }), "تعديل المحضر");
            await Denied(await c.PostAsync($"/api/v1/minutes/{_minutes}/amend", new { reason = "محاولة تعديل غير مخولة للمحضر" }), "فتح التعديل");
            await Denied(await c.PostAsync($"/api/v1/meetings/{_meeting}/cancel", new { reason = "محاولة إلغاء" }), "إلغاء الاجتماع");
            await Denied(await c.PostAsync($"/api/v1/tasks/{_task}/actions/start", new { }), "تحريك المهمة");
            await Denied(await c.PostAsync($"/api/v1/resolutions/{_resolution}/transition", new { action = "mark_executed", reason = "محاولة غير مخولة" }), "تغيير حالة القرار");
            await Denied(await c.PostAsync($"/api/v1/documents/{_doc}/share", new { taskId = _task, share = true }), "مشاركة المرفق");
            await Denied(await c.PostContentAsync("/api/v1/documents", File("x.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 x"), "application/pdf", "body", _body)), "الرفع إلى جهة أخرى");
        }
        // البحث والتقارير لا تكشف شيئًا
        foreach (var user in new[] { "out", "osec" })
        {
            var hits = (await (await As(user)).GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("خطة الاستجابة")))["items"]!.AsArray();
            Check.That(hits.All(h => h.G("bodyId") != _body), $"{user}: البحث لا يكشف سجلات الجهة");
        }
        // المستخدمون: غير المسؤول لا يقرأ بيانات مستخدم آخر ولا يعدلها
        var m1 = await As("m1");
        Check.That((int)(await m1.GetAsync($"/api/v1/users/{_u["chair"]}")).StatusCode is 403 or 404, "قراءة مستخدم آخر");
        Check.That((int)(await m1.PatchAsync($"/api/v1/users/{_u["m1"]}", new { displayNameAr = "تغيير" })).StatusCode is 403 or 404, "تعديل الحساب نفسه عبر واجهة الإدارة");
        Check.That((int)(await m1.PutAsync($"/api/v1/users/{_u["m1"]}/system-roles", new { roles = new[] { "system_admin" }, reason = "محاولة رفع صلاحياتي ذاتيًا" })).StatusCode is 403 or 404, "رفع الصلاحيات ذاتيًا");
        // الإشعارات خاصة بصاحبها
        var other = (await Owner.ScalarAsync<Guid?>("SELECT id FROM notify.in_app_notifications WHERE user_id = @u AND read_at IS NULL LIMIT 1", new { u = _u["chair"] }));
        if (other is { } nid) Check.That((int)(await m1.PostAsync($"/api/v1/notifications/{nid}/read")).StatusCode is 403 or 404 or 204, "قراءة إشعار الغير");
        if (other is { } nid2) Check.That(await Owner.ScalarAsync<bool>("SELECT read_at IS NULL FROM notify.in_app_notifications WHERE id = @nid2", new { nid2 }), "إشعار الغير لم يتغير");
    }

    // ============================ رفع الملفات ============================

    [Test("S9-02", "رفع الملفات: HTML أو برنامج بامتداد مسموح يُرفض، والامتداد غير المسموح يُرفض، والاسم يُنظف، والتنزيل بنوع من الخادم ومعزول", 2)]
    public async Task Uploads()
    {
        var sec = await As("sec");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", File("report.pdf", Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>"), "text/html", "body", _body)), 400, "validation", "HTML متنكر بامتداد PDF");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", File("photo.png", Encoding.ASCII.GetBytes("MZ\x90\0 executable"), "image/png", "body", _body)), 400, "validation", "برنامج باسم صورة");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", File("page.html", Encoding.UTF8.GetBytes("<html></html>"), "text/html", "body", _body)), 400, "validation", "امتداد غير مسموح");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", File("tool.exe", [0x4D, 0x5A], "application/octet-stream", "body", _body)), 400, "validation", "ملف تنفيذي");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", File("doc.pdf", [], "application/pdf", "body", _body)), 400, "validation", "ملف فارغ");
        // نوع المحتوى من الخادم لا من المتصفح، والاسم بلا مسار
        var id = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", File("../../etc/<evil>.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 ok"), "text/javascript", "body", _body)), 201, "رفع صحيح بنوع مزوّر")).G("id");
        var row = await Owner.QuerySingleAsync("SELECT file_name, content_type, storage_key FROM docs.documents WHERE id = @id", new { id }, r => (r.GetString(0), r.GetString(1), r.GetString(2)));
        Check.That(row.Item1 == "evil.pdf" && row.Item2 == "application/pdf" && !row.Item3.Contains(".."), "الاسم منظف والنوع من الامتداد ومفتاح التخزين من الخادم");
        var resp = await sec.GetAsync($"/api/v1/documents/{id}/content");
        Check.Equal("application/pdf", resp.Content.Headers.ContentType?.MediaType, "نوع التنزيل");
        Check.That(resp.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("sandbox"), "التنزيل معزول بسياسة sandbox");
        Check.That(resp.Headers.TryGetValues("X-Content-Type-Options", out var nos) && nos.First() == "nosniff", "nosniff");
        Check.That(resp.Content.Headers.ContentDisposition?.DispositionType == "attachment", "تنزيل لا عرض داخل الصفحة");
        // الإصدار الجديد بنوع مختلف مرفوض
        var v = new MultipartFormDataContent();
        var f = new ByteArrayContent(Encoding.UTF8.GetBytes("<svg onload=alert(1)>"));
        v.Add(f, "file", "x.png");
        await Check.Problem(await sec.PostContentAsync($"/api/v1/documents/{id}/versions", v), 400, "validation", "إصدار بنوع مختلف");
    }

    // ============================ الترويسات والحقن والأخطاء ============================

    [Test("S9-03", "ترويسات الأمان على الواجهة والبرمجيات، وحقن SQL في البحث والمرشحات لا يغير النتائج ولا يسبب خطأ داخليًا، ولا تفاصيل داخلية في الأخطاء", 3)]
    public async Task HeadersAndInjection()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Url) };
        foreach (var path in new[] { "/", "/api/v1/system/info", "/api/v1/auth/csrf" })
        {
            var r = await http.GetAsync(path);
            string H(string n) => r.Headers.TryGetValues(n, out var v) ? v.First() : r.Content.Headers.TryGetValues(n, out var c) ? c.First() : "";
            Check.That(H("Content-Security-Policy").Contains("default-src 'self'") && H("Content-Security-Policy").Contains("frame-ancestors 'none'"), $"CSP على {path}");
            Check.Equal("DENY", H("X-Frame-Options"), $"منع التضمين على {path}");
            Check.Equal("nosniff", H("X-Content-Type-Options"), $"nosniff على {path}");
            Check.That(H("Referrer-Policy").Length > 0 && H("Permissions-Policy").Length > 0, $"سياسة الإحالة والأذونات على {path}");
        }
        var sec = await As("sec");
        var baseline = (await sec.GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("خطة")))["items"]!.AsArray().Count;
        foreach (var payload in new[] { "' OR '1'='1", "'; DROP TABLE iam.users; --", "\" OR 1=1 --", "%' OR 1=1 --", "1) UNION SELECT password_hash FROM iam.local_credentials --", "\\'; SELECT pg_sleep(5); --" })
        {
            var r = await sec.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(payload));
            Check.Equal(200, (int)r.StatusCode, "البحث بمدخل حقن: " + payload);
            var body = await r.Content.ReadAsStringAsync();
            Check.That(!body.Contains("AQAAAA"), "لا تسرب بصمات كلمات المرور: " + payload);
            var list = await sec.GetAsync("/api/v1/tasks?status=" + Uri.EscapeDataString(payload) + "&q=" + Uri.EscapeDataString(payload));
            Check.That((int)list.StatusCode is 200 or 400, $"مرشحات المهام بمدخل حقن (HTTP {(int)list.StatusCode})");
            var bodies = await sec.GetAsync("/api/v1/bodies?q=" + Uri.EscapeDataString(payload));
            Check.That((int)bodies.StatusCode is 200 or 400, $"مرشحات الجهات بمدخل حقن (HTTP {(int)bodies.StatusCode})");
        }
        Check.Equal(baseline, (await sec.GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("خطة")))["items"]!.AsArray().Count, "النتائج لم تتغير بعد محاولات الحقن");
        Check.That(await Owner.ScalarAsync<long>("SELECT count(*) FROM iam.users") > 0, "الجداول سليمة");
        // نص مخزن بوسوم HTML يعود نصًا في JSON (الواجهة تعرضه نصًا)، والاستجابة ليست HTML
        var gov = await As("gov");
        var xss = await gov.PatchAsync($"/api/v1/bodies/{_other}", new { mandateAr = "<img src=x onerror=alert(1)>" });
        Check.That((int)xss.StatusCode is 200 or 204 or 400, "حفظ نص بوسوم");
        var b = await gov.GetAsync($"/api/v1/bodies/{_other}");
        Check.Equal("application/json", b.Content.Headers.ContentType?.MediaType, "الواجهة البرمجية JSON دائمًا");
        // الأخطاء: لا مسار ملفات ولا تتبع استدعاءات ولا SQL
        var nf = await sec.GetAsync("/api/v1/meetings/not-a-guid");
        var nfBody = await nf.Content.ReadAsStringAsync();
        Check.That(!nfBody.Contains("   at ") && !nfBody.Contains(".cs:") && !nfBody.Contains("SELECT"), "لا تفاصيل داخلية في الخطأ");
        var bad = await sec.PostAsync("/api/v1/meetings", new { bodyId = _body, titleAr = new string('x', 5000), startsAt = "not-a-date" });
        Check.That((int)bad.StatusCode == 400 && !(await bad.Content.ReadAsStringAsync()).Contains("   at "), "مدخلات غير صالحة: 400 دون تتبع");
    }

    // ============================ الجلسات والمصادقة ============================

    [Test("S9-04", "الجلسات: الكعكة HttpOnly وSameSite=Strict، وتغيير كلمة المرور يُبطل الجلسات الأخرى، وتعيينها من المسؤول يُبطل الكل، والطلب بلا CSRF مرفوض", 4)]
    public async Task Sessions()
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = false };
        using var raw = new HttpClient(handler) { BaseAddress = new Uri(Url) };
        var csrfResp = await raw.GetAsync("/api/v1/auth/csrf");
        var token = (await csrfResp.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        var csrfCookie = csrfResp.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ccms.csrf"));
        Check.That(csrfCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && csrfCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "كعكة CSRF محمية");
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = "s9-m1@test.jsdo.gov.sa", password = Pw }) };
        login.Headers.Add("X-CSRF-Token", token);
        login.Headers.Add("Cookie", csrfCookie.Split(';')[0]);
        // ترويسة عنوان مزوّرة من عميل غير موثوق لا تُعتمد
        login.Headers.Add("X-Forwarded-For", "203.0.113.77");
        var lr = await raw.SendAsync(login);
        Check.Equal(200, (int)lr.StatusCode, "الدخول");
        var session = lr.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ccms.session"));
        Check.That(session.Contains("httponly", StringComparison.OrdinalIgnoreCase) && session.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "كعكة الجلسة HttpOnly وSameSite=Strict");
        var ip = await Owner.ScalarAsync<string>("SELECT actor_ip FROM audit.audit_events WHERE action = 'auth.login' AND actor_user_id = @u ORDER BY chain_seq DESC LIMIT 1", new { u = _u["m1"] });
        Check.That(ip != "203.0.113.77", $"عنوان العميل في التدقيق لا يُزوَّر بترويسة من غير وكيل موثوق ({ip})");

        // CSRF: طلب يغيّر الحالة بجلسة صحيحة دون الرمز
        using var noCsrf = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(Url) };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        req.Headers.Add("Cookie", session.Split(';')[0]);
        var nr = await noCsrf.SendAsync(req);
        Check.Equal(400, (int)nr.StatusCode, "دون رمز CSRF");
        // جلستان للمستخدم نفسه؛ تغيير كلمة المرور من إحداهما يُبطل الأخرى
        var a = await As("m1", fresh: true);
        var b = await As("m1", fresh: true);
        await Check.Status(await a.PostAsync("/api/v1/auth/password", new { currentPassword = Pw, newPassword = "New@Password2026x" }), 204, "تغيير كلمة المرور");
        await Check.Status(await a.GetAsync("/api/v1/me"), 200, "الجلسة الحالية باقية");
        Check.Equal(401, (int)(await b.GetAsync("/api/v1/me")).StatusCode, "الجلسة الأخرى أُبطلت");
        // المسؤول يعيد تعيين كلمة المرور: كل الجلسات تنتهي
        await Check.Status(await Admin.PutAsync($"/api/v1/users/{_u["m1"]}/password", new { password = Pw }), 204, "تعيين من المسؤول");
        Check.Equal(401, (int)(await a.GetAsync("/api/v1/me")).StatusCode, "كل الجلسات أُبطلت بعد التعيين");
        await Owner.ExecuteAsync("UPDATE iam.local_credentials SET must_change = false WHERE user_id = @u", new { u = _u["m1"] });
        _clients.Remove("m1");
    }

    // ============================ مفاتيح التحكم ============================

    [Test("S9-05", "لا تجاوز لمفاتيح التحكم من الواجهة البرمجية: الذكاء الاصطناعي والتوقيع والبوابة والاجتماعات المرئية معطلة على الخادم مهما كان الطلب", 5)]
    public async Task FlagBypass()
    {
        var before = await Admin.GetJsonAsync("/api/v1/features");
        string V(string k) => before[k]!["value"]!.GetValue<string>();
        var saved = new[] { "AI_ENABLED", "E_SIGNATURE_ENABLED", "EXTERNAL_PORTAL_ENABLED", "VIDEO_MEETINGS_ENABLED" }.Select(k => (k, V(k))).ToArray();
        await Flags(("AI_ENABLED", "false"), ("E_SIGNATURE_ENABLED", "false"), ("EXTERNAL_PORTAL_ENABLED", "false"), ("VIDEO_MEETINGS_ENABLED", "false"));
        try
        {
            var sec = await As("sec");
            await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutes}/summary", new { }), 404, "feature-disabled", "الذكاء الاصطناعي معطل");
            await Check.Problem(await sec.PostAsync("/api/v1/ai/ask", new { query = "ما القرارات المتأخرة؟" }), 404, "feature-disabled", "السؤال معطل");
            await Check.Problem(await sec.PostAsync($"/api/v1/esign/minutes/{_minutes}/requests"), 404, "feature-disabled", "التوقيع معطل");
            await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_meeting}/online-meeting"), 404, "feature-disabled", "الاجتماع المرئي معطل");
            // محاولة تغيير المفتاح دون صلاحية
            await Check.Problem(await sec.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "محاولة تفعيل غير مخولة" }), 403, "forbidden-action", "غير المخول لا يفعّل");
            // العمليات الأساسية تعمل
            await Check.Status(await sec.GetAsync($"/api/v1/minutes/{_minutes}"), 200, "المحضر");
            await Check.Status(await sec.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("خطة")), 200, "البحث التقليدي");
        }
        finally
        {
            await Flags(saved);
        }
    }

    // ============================ سلامة السجلات ============================

    [Test("S9-06", "سلامة السجلات: العمليات الحساسة كلها في التدقيق، والمعتمد لا يُعدل صامتًا، وسلسلة التدقيق تكشف العبث ثم تعود سليمة بعد إرجاعه", 6)]
    public async Task Integrity()
    {
        var actions = (await Owner.QueryAsync("SELECT DISTINCT action FROM audit.audit_events", null, r => r.GetString(0))).ToHashSet();
        var required = new[]
        {
            "auth.login", "auth.logout", "auth.login_failed", "user.created", "user.password_set", "config.flag_changed", "body.created", "body.activated", "body.member_added",
            "meeting.scheduled", "meeting.opened", "meeting.closed", "minutes.created", "minutes.submitted", "minutes.approved", "resolution.issued", "task.created",
            "document.uploaded", "document.downloaded", "document.version_registered", "report.export", "destruction.executed",
        };
        var missing = required.Where(a => !actions.Contains(a)).ToList();
        Check.That(missing.Count == 0, "العمليات الحساسة مسجلة؛ الناقص: " + string.Join("، ", missing));
        // المعتمد: لا تعديل صامت عبر الواجهة ولا بحساب التطبيق
        await Check.Problem(await (await As("sec")).PutAsync($"/api/v1/minutes/{_minutes}/draft", new { sections = Array.Empty<object>() }), 409, "minutes-approved", "المحضر المعتمد لا يُعدل مباشرة");
        await using var app = new PgWireDataSource(ctx.AppConnectionString);
        var appDb = new Db(app);
        async Task<bool> Denied(string sql)
        {
            try { await appDb.ExecuteAsync(sql, new { id = _minutes }); return false; } catch (Exception) { return true; }
        }
        Check.That(await Denied("UPDATE minutes.minutes_versions SET content = '{}'::jsonb WHERE minutes_id = @id"), "إصدار المحضر غير قابل للتعديل");
        Check.That(await Denied("DELETE FROM minutes.minutes_versions WHERE minutes_id = @id"), "إصدار المحضر غير قابل للحذف");
        Check.That(await Denied("UPDATE audit.audit_events SET action = 'x' WHERE chain_seq = 1"), "التدقيق غير قابل للتعديل بحساب التطبيق");
        Check.That(await Denied("DELETE FROM sig.requests WHERE minutes_id = @id"), "طلبات التوقيع لا تُحذف");
        Check.That(await Denied("DELETE FROM ai.outputs WHERE id IS NOT NULL"), "مخرجات الذكاء الاصطناعي لا تُحذف");
        // سلسلة التدقيق
        var aud = await Check.Status(await (await As("aud")).GetAsync("/api/v1/audit/integrity"), 200, "التحقق من السلسلة");
        Check.That(aud!["intact"]!.GetValue<bool>(), "السلسلة سليمة قبل العبث");
        var seq = await Owner.ScalarAsync<long>("SELECT max(chain_seq) - 3 FROM audit.audit_events");
        var original = await Owner.ScalarAsync<string>("SELECT action FROM audit.audit_events WHERE chain_seq = @seq", new { seq });
        await Owner.ExecuteAsync("ALTER TABLE audit.audit_events DISABLE TRIGGER USER");
        try
        {
            await Owner.ExecuteAsync("UPDATE audit.audit_events SET action = 'tampered' WHERE chain_seq = @seq", new { seq });
            var broken = await Owner.ScalarAsync<long?>("SELECT first_broken_seq FROM audit.verify_chain()");
            Check.Equal(seq, broken, "العبث يُكتشف بموضعه");
        }
        finally
        {
            await Owner.ExecuteAsync("UPDATE audit.audit_events SET action = @original WHERE chain_seq = @seq", new { original, seq });
            await Owner.ExecuteAsync("ALTER TABLE audit.audit_events ENABLE TRIGGER USER");
        }
        Check.That(await Owner.ScalarAsync<long?>("SELECT first_broken_seq FROM audit.verify_chain()") is null, "السلسلة سليمة بعد الإرجاع");
    }

    [Test("S9-07", "تعطيل التوقيع لا يحذف التوقيعات السابقة، وتعطيل الذكاء الاصطناعي لا يحذف مخرجاته ولا يعطل الأساسي، والإعادة تُظهرها كما هي", 7)]
    public async Task DisablingKeepsData()
    {
        var before = await Admin.GetJsonAsync("/api/v1/features");
        string V(string k) => before[k]!["value"]!.GetValue<string>();
        var (esign, ai) = (V("E_SIGNATURE_ENABLED"), V("AI_ENABLED"));
        var counts = async () => (
            Signed: await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE status = 'signed'"),
            Requests: await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.requests"),
            Evidence: await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.events"),
            Outputs: await Owner.ScalarAsync<long>("SELECT count(*) FROM ai.outputs"),
            Saved: await Owner.ScalarAsync<long>("SELECT count(*) FROM ai.outputs WHERE status IN ('saved','applied')"),
            Signoffs: await Owner.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_signoffs WHERE status = 'valid'"));
        var c0 = await counts();
        Check.That(c0.Signed > 0 && c0.Outputs > 0, "توجد توقيعات ومخرجات سابقة من المراحل السابقة");
        try
        {
        await Flags(("E_SIGNATURE_ENABLED", "false"), ("AI_ENABLED", "false"));
        var c1 = await counts();
        Check.Equal(c0, c1, "لا شيء حُذف بالتعطيل");
        var completed = await Owner.ScalarAsync<Guid>("SELECT r.id FROM sig.requests r JOIN bodies.bodies b ON b.id = r.body_id WHERE r.status = 'completed' AND b.code = 'P6-SIG' ORDER BY r.completed_at DESC LIMIT 1");
        var p6 = new ApiClient(Url);
        await Check.Status(await p6.LoginAsync("p6-chair@test.jsdo.gov.sa", Pw), 200, "دخول رئيس لجنة التوقيع");
        var v = (await Check.Status(await p6.GetAsync($"/api/v1/esign/requests/{completed}/verify"), 200, "التحقق من توقيع سابق والوحدة معطلة"))!;
        Check.That(v["document"]!["intact"]!.GetValue<bool>(), "النسخة الموقعة سليمة والوحدة معطلة");
        await Check.Status(await (await As("sec")).PostAsync("/api/v1/tasks", new
        { bodyId = _body, titleAr = "مهمة والوحدات معطلة", assigneeType = "member", assigneeUserId = _u["chair"], accountableUserId = _u["sec"], dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), verificationRequired = false }), 201, "الأساسي يعمل");
        }
        finally
        {
            await Flags(("E_SIGNATURE_ENABLED", esign), ("AI_ENABLED", ai));
        }
        Check.Equal(c0.Saved, (await counts()).Saved, "المحفوظ باقٍ بعد الإعادة");
    }
}
