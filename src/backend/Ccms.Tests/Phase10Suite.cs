using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>اختبار قبول المستخدم (UAT) — الدورة الكاملة بالترتيب الوارد في طلب المرحلة النهائية (24 §5)، مع الحالات السلبية:
/// رفض التوقيع، وتعديل المحضر، وانتهاء العضوية، وفشل مزوّد خارجي، وعدم توفر البريد، ومحاولة غير المخول.
/// يعمل على التطبيق بإعدادات بريد تشير إلى خادم SMTP متوقف (البريد مفعّل وغير متاح فعليًا).</summary>
public sealed class Phase10Suite(TestContext ctx) : IAsyncDisposable
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<Guid, string> _who = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private readonly PgWireDataSource _ownerDs = new(ctx.OwnerConnectionString);
    private Db Owner => new(_ownerDs);
    private Guid _council, _unit, _entity, _meeting, _item1, _item2, _minutes, _r1, _r2, _tUnit, _tExt, _m3Member, _aiOutput;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    public async ValueTask DisposeAsync() => await _ownerDs.DisposeAsync();
    private ApiClient Admin => ctx.AdminClient!;

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"u10-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private async Task Flags(params (string Key, string Value)[] flags)
    {
        var current = await Admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, v) in flags)
            if (current[k]!["value"]!.GetValue<string>() != v)
                await Check.Status(await Admin.PutAsync($"/api/v1/admin/features/{k}", new { value = v, reason = $"اختبار القبول: {k}", confirmImpact = true }), 200, k + "=" + v);
    }

    private static string Key() => Guid.NewGuid().ToString("N");
    private async Task<JsonNode> Minutes(Guid id) => await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{id}");
    private async Task<string> MinutesStatus(Guid id) => (await Minutes(id))["minutes"].S("status");

    private static MultipartFormDataContent Pdf(string name, string text, string ownerType, Guid ownerId)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 " + text));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", name);
        content.Add(new StringContent(ownerType), "ownerType");
        content.Add(new StringContent(ownerId.ToString()), "ownerId");
        content.Add(new StringContent("attachment"), "purpose");
        return content;
    }

    /// <summary>يوقّع كل موقّع بانتظار دوره في الطلب (موافقة إلكترونية موثقة بإعادة التحقق).</summary>
    private async Task SignAllAsync(Guid request)
    {
        for (var round = 0; round < 6; round++)
        {
            var card = await (await As("sec")).GetJsonAsync($"/api/v1/esign/requests/{request}");
            var pending = card["signers"]!.AsArray().Where(s => s.S("status") == "pending").ToList();
            if (pending.Count == 0) return;
            foreach (var s in pending)
            {
                var who = _who[s.G("userId")];
                var c = await As(who);
                var token = (await Check.Status(await c.PostAsync("/api/v1/esign/reauth", new { password = Pw }), 200, "إعادة التحقق " + who)).S("reauthToken");
                await Check.Status(await c.PostOnceAsync($"/api/v1/esign/requests/{request}/sign", new { reauthToken = token }, Key()), 200, "توقيع " + who);
            }
        }
    }

    // ============================ التهيئة ============================

    private (string, string)[] _initialFlags = [];

    [Test("U-99", "إرجاع مفاتيح التحكم إلى حالتها قبل اختبار القبول", 99)]
    public async Task Restore() => await Flags(_initialFlags);

    [Test("U-SETUP", "تهيئة القبول: مستخدمون بأدوار المكتب، وإدارة بمدير وموظف، وجهة خارجية بضابط اتصال؛ التوقيع مفعّل (موافقة موثقة)، والبريد مفعّل وخادمه متوقف", 0)]
    public async Task Setup()
    {
        var f0 = await Admin.GetJsonAsync("/api/v1/features");
        _initialFlags = new[] { "E_SIGNATURE_ENABLED", "E_SIGNATURE_METHOD", "E_SIGNATURE_MEMBER_REQUIRED", "E_SIGNATURE_CHAIR_REQUIRED", "E_SIGNATURE_SECRETARY_REQUIRED",
            "E_SIGNATURE_SEQUENTIAL", "E_SIGNATURE_REAUTH_REQUIRED", "E_SIGNATURE_PDF_OUTPUT", "EXTERNAL_PORTAL_ENABLED", "EMAIL_INTEGRATION_ENABLED", "AI_ENABLED", "AI_PROVIDER",
            "AI_MAX_CLASSIFICATION", "AI_MINUTES_SUMMARY_ENABLED" }.Select(k => (k, f0[k]!["value"]!.GetValue<string>())).ToArray();
        await Flags(("E_SIGNATURE_ENABLED", "true"), ("E_SIGNATURE_METHOD", "in_app_approval"), ("E_SIGNATURE_MEMBER_REQUIRED", "true"), ("E_SIGNATURE_CHAIR_REQUIRED", "true"),
            ("E_SIGNATURE_SECRETARY_REQUIRED", "false"), ("E_SIGNATURE_SEQUENTIAL", "false"), ("E_SIGNATURE_REAUTH_REQUIRED", "true"), ("E_SIGNATURE_PDF_OUTPUT", "false"),
            ("EXTERNAL_PORTAL_ENABLED", "false"), ("EMAIL_INTEGRATION_ENABLED", "true"), ("AI_ENABLED", "false"));
        async Task<Guid> User(string k, string[] roles, Guid? unit = null)
        {
            var id = (await Check.Status(await Admin.PostAsync("/api/v1/users", new { email = $"u10-{k}@test.jsdo.gov.sa", displayNameAr = "ق10 " + k, systemRoles = roles, initialPassword = Pw, userType = "internal", orgUnitId = unit }), 201, "مستخدم " + k)).G("id");
            _u[k] = id;
            _who[id] = k;
            return id;
        }
        foreach (var k in new[] { "chair", "sec", "m1", "m2", "m3", "mgr", "fo", "outsider" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        await User("exec", ["executive"]);
        await User("aud", ["auditor"]);
        var gov = await As("gov");
        _unit = (await Check.Status(await gov.PostAsync("/api/v1/org-units", new { code = "U10-PMO", nameAr = "إدارة المشاريع والتخطيط (ق10)", managerUserId = _u["mgr"] }), 201, "إدارة")).G("id");
        await User("emp", [], _unit);
        _entity = (await Check.Status(await gov.PostAsync("/api/v1/external-entities", new { nameAr = "أمانة منطقة الجوف (ق10)", liaisonUserId = _u["fo"], contactName = "مدير التخطيط" }), 201, "جهة خارجية")).G("id");
    }

    // ============================ 1–4 ============================

    [Test("U-01", "إنشاء مجلس وتعيين أعضائه وسياسته (مراجعة الرئيس، وتوقيع الحاضرين، واعتماد الرئيس) وتفعيله", 1)]
    public async Task Council()
    {
        var gov = await As("gov");
        _council = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "U10-EXC", bodyType = "executive_council", nameAr = "المجلس التنفيذي لمشاريع الجوف (ق10)", nameEn = "U10 Executive Council" }), 201, "إنشاء المجلس")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member"), ("m2", "member"), ("m3", "member") })
        {
            var mid = (await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_council}/members", new { userId = _u[u], role }), 201, "تعيين " + u)).G("id");
            if (u == "m3") _m3Member = mid;
        }
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_council}/policy", new
        {
            effectiveFrom = Today, quorumType = "majority", chairRequired = true, countRemote = true, allowOverride = false, votingThreshold = "majority_of_votes_cast",
            abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = true, minutesReviewDays = 3, extensionApprover = "chair",
            allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false, reason = "سياسة المجلس التنفيذي",
            minutesChairReview = true, minutesMemberReview = false, minutesSignatures = "present_members", minutesSignatureFallback = "attestation", minutesFinalApproval = "chair",
        }), 200, "السياسة");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_council}/activate"), 200, "التفعيل");
        var b = await gov.GetJsonAsync($"/api/v1/bodies/{_council}");
        Check.Equal("active", b["body"].S("status"), "المجلس فعال");
        Check.Equal(5, (await gov.GetJsonAsync($"/api/v1/bodies/{_council}/members")).Items().Count(m => m.S("status") == "active"), "خمسة أعضاء");
        await Check.Problem(await (await As("outsider")).PostAsync("/api/v1/bodies", new { code = "U10-X", bodyType = "permanent_committee", nameAr = "لجنة غير مخولة", nameEn = "Unauthorized" }), 403, "forbidden-action", "غير المخول لا ينشئ جهة");
    }

    [Test("U-02", "إنشاء اجتماع وجدول أعمال ببندين، وجدولته برقم، وإرسال الدعوات — والبريد غير متاح لا يوقف الدعوات", 2)]
    public async Task Meeting()
    {
        var sec = await As("sec");
        _meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _council, titleAr = "الاجتماع الأول للمجلس التنفيذي", meetingType = "regular", startsAt = DateTime.UtcNow.AddMinutes(30), location = "قاعة المجلس" }), 201, "إنشاء الاجتماع")).G("id");
        _item1 = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = "اعتماد خطة مشاريع البنية الرقمية 2027" }), 201, "بند 1")).G("id");
        _item2 = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = "مؤشرات أداء المكتب للربع الثالث" }), 201, "بند 2")).G("id");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/schedule"), 200, "الجدولة");
        Check.That(s.S("referenceNo").StartsWith("U10-EXC-"), "رقم مرجعي");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = "بند من عضو" }), 403, "forbidden-action", "العضو لا يضيف بنودًا");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/invitations/send"), 200, "الدعوات");
        var inv = (await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}/invitations")).Items();
        Check.That(inv.Count >= 5, "دعوة لكل الأعضاء");
        var notif = (await (await As("m1")).GetJsonAsync("/api/v1/notifications")).Items();
        Check.That(notif.Any(n => n.S("link").Contains(_meeting.ToString())), "إشعار داخل النظام للعضو");
        // البريد مفعّل وخادمه متوقف: الرسائل تُؤجل بإعادة محاولة، والاجتماع لم يتأثر
        await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "توزيع الصادر");
        var mail = await Owner.QuerySingleAsync("SELECT status, attempts, last_error FROM notify.outbox_messages WHERE recipient_user_id = @u AND channel = 'email' ORDER BY created_at DESC LIMIT 1",
            new { u = _u["m1"] }, r => (Status: r.GetString(0), Attempts: r.GetInt32(1), Error: r.Str("last_error")));
        Check.That(mail.Status == "pending" && mail.Attempts >= 1 && mail.Error is not null, $"البريد غير متاح: مؤجل لا مفقود ({mail.Status}/{mail.Attempts})");
        Check.Equal("agenda_published", (await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}"))["meeting"].S("status"), "الاجتماع مستمر: نُشر جدول الأعمال");
    }

    [Test("U-03", "تسجيل الحضور والاعتذار، والتحقق من النصاب قبل الافتتاح، ثم الافتتاح", 3)]
    public async Task Attendance()
    {
        var sec = await As("sec");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_meeting}/attendance", new
        {
            records = new object[]
            {
                new { userId = _u["chair"], status = "present", mode = "in_person" }, new { userId = _u["sec"], status = "present", mode = "in_person" },
                new { userId = _u["m1"], status = "present", mode = "in_person" }, new { userId = _u["m2"], status = "present", mode = "remote" },
                new { userId = _u["m3"], status = "excused", note = "مهمة رسمية" },
            },
        }), 200, "الحضور");
        var q = await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}/quorum");
        Check.That(q["result"]!["isMet"]!.GetValue<bool>(), "النصاب مكتمل");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/open", new { }), 200, "الافتتاح");
        Check.Equal("in_progress", (await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}"))["meeting"].S("status"), "منعقد");
    }

    [Test("U-04", "إصدار قرار من بند الاجتماع (مقترح في المحضر حتى الاعتماد)، وتوصية من البند الثاني، وإغلاق الاجتماع", 4)]
    public async Task Resolution()
    {
        var sec = await As("sec");
        _minutes = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/minutes"), 201, "إنشاء المحضر")).G("id");
        _r1 = (await Check.Status(await sec.PostOnceAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        {
            resolutionType = "decision", titleAr = "اعتماد خطة مشاريع البنية الرقمية 2027", agendaItemId = _item1, category = "technical",
            textAr = "قرر المجلس اعتماد خطة مشاريع البنية الرقمية لعام 2027 وتكليف إدارة المشاريع وأمانة المنطقة بالتنفيذ.",
        }, Key()), 201, "قرار من البند 1")).G("id");
        _r2 = (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        { resolutionType = "decision", titleAr = "اعتماد تقرير مؤشرات الربع الثالث", agendaItemId = _item2, category = "administrative", textAr = "قرر المجلس اعتماد تقرير مؤشرات الربع الثالث." }), 201, "قرار من البند 2")).G("id");
        var r = (await sec.GetJsonAsync($"/api/v1/resolutions/{_r1}"))["resolution"]!;
        Check.That(r.S("status") == "proposed" && r.G("agendaItemId") == _item1, "مقترح ومرتبط بالبند");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/close"), 200, "إغلاق الاجتماع");
    }

    // ============================ 5–6 ============================

    [Test("U-05", "إنشاء مهام مرتبطة بالقرار وإسنادها لإدارة داخلية (منفذ ومتابع ومخرج) ولجهة خارجية — تبقى «جديدة» حتى اعتماد المحضر", 5)]
    public async Task Tasks()
    {
        var sec = await As("sec");
        _tUnit = (await Check.Status(await sec.PostOnceAsync("/api/v1/tasks", new
        {
            bodyId = _council, resolutionId = _r1, titleAr = "إعداد وثيقة نطاق مشاريع البنية الرقمية", assigneeType = "org_unit", assigneeOrgUnitId = _unit,
            executorUserId = _u["emp"], accountableUserId = _u["fo"], priority = "high", dueDate = Today.AddDays(14),
            deliverables = new[] { new { title = "وثيقة النطاق", acceptanceCriteria = "معتمدة من مدير الإدارة", required = true } },
        }, Key()), 201, "مهمة الإدارة")).G("id");
        _tExt = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        {
            bodyId = _council, resolutionId = _r1, titleAr = "تزويد المكتب بمواقع المشاريع البلدية", assigneeType = "external_entity", assigneeExternalEntityId = _entity, dueDate = Today.AddDays(20),
            deliverables = new[] { new { title = "قائمة المواقع", acceptanceCriteria = "بإحداثيات وحالة الملكية", required = true } },
        }), 201, "مهمة الجهة الخارجية")).G("id");
        foreach (var t in new[] { _tUnit, _tExt })
            Check.Equal("new", (await sec.GetJsonAsync($"/api/v1/tasks/{t}"))["task"].S("status"), "جديدة حتى صدور القرار");
        Check.Equal(_u["fo"], (await sec.GetJsonAsync($"/api/v1/tasks/{_tExt}"))["task"].G("accountableUserId"), "متابع الجهة ضابط الاتصال");
        await Check.Problem(await (await As("m1")).PostAsync("/api/v1/tasks", new { bodyId = _council, titleAr = "س", assigneeType = "org_unit", assigneeOrgUnitId = _unit, accountableUserId = _u["fo"], dueDate = Today.AddDays(5) }), 403, "forbidden-action", "العضو لا يسند مهام");
    }

    // ============================ 8–9 ============================

    [Test("U-08", "إعداد المحضر ومراجعته: الرئيس يعيده بملاحظة (تعديل المحضر)، فإصدار جديد ببصمة مختلفة، ثم إقرار الرئيس فانتظار التوقيع", 6)]
    public async Task MinutesReview()
    {
        var sec = await As("sec");
        object Draft(string opening) => new
        {
            sections = new[] { new { key = "opening", title = "الافتتاح", body = opening }, new { key = "closing", title = "الختام", body = "اختُتم الاجتماع بشكر الأعضاء." } },
            items = new[]
            {
                new { agendaItemId = _item1, discussion = "استعرض المجلس خطة مشاريع البنية الرقمية ومراحلها وتكلفتها التقديرية.", recommendation = (string?)null },
                new { agendaItemId = _item2, discussion = "ناقش المجلس مؤشرات الربع الثالث وأسباب الانحراف.", recommendation = (string?)null },
            },
        };
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", Draft("افتتح الرئيس الاجتماع.")), 200, "حفظ المسودة");
        await Check.Problem(await (await As("m1")).PutAsync($"/api/v1/minutes/{_minutes}/draft", Draft("تعديل من عضو")), 403, "forbidden-action", "العضو لا يحرر المحضر");
        var s1 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "المسودة الأولى" }), 200, "الرفع");
        Check.Equal("chair_review", s1.S("status"), "مراجعة الرئيس");
        await Check.Problem(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", Draft("تعديل صامت")), 409, "minutes-in-review", "لا تعديل أثناء المراجعة");
        Check.Equal("returned", (await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/return", new { reason = "إضافة نسبة إنجاز الربع الثالث في الافتتاح" }), 200, "إعادة للتعديل")).S("status"), "معاد للتعديل");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", Draft("افتتح الرئيس الاجتماع مشيرًا إلى نسبة إنجاز 78٪ في الربع الثالث.")), 200, "التعديل");
        var s2 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "معالجة ملاحظة الرئيس" }), 200, "إعادة الرفع");
        Check.That(s2["versionNo"]!.GetValue<int>() == 2 && s2.S("contentHash") != s1.S("contentHash"), "إصدار 2 ببصمة مختلفة");
        Check.Equal("pending_signatures", (await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = s2.S("contentHash") }), 200, "إقرار الرئيس")).S("status"), "انتظار التوقيع");
        var versions = (await sec.GetJsonAsync($"/api/v1/minutes/{_minutes}/versions")).AsArray();
        Check.That(versions.Count == 2 && versions.Any(v => v.S("contentHash") == s1.S("contentHash")), "الإصدار الأول محفوظ ببصمته");
    }

    [Test("U-09", "مسار التوقيع: طلب مربوط بالإصدار وبصمته، ورفض عضو بسبب يعيد المحضر ويبقى في السجل، ثم طلب جديد يكتمل وأعتمد المحضر فصدرت القرارات وفُعّلت المهام", 7)]
    public async Task Signatures()
    {
        var sec = await As("sec");
        var e = await sec.GetJsonAsync($"/api/v1/esign/minutes/{_minutes}");
        var req = e["requests"]![0]!;
        var m = await Minutes(_minutes);
        Check.That(req.S("type") == "in_app_approval" && req.S("contentHash") == m["currentVersion"].S("hash") && req["versionNo"]!.GetValue<int>() == 2, "الطلب مربوط بالإصدار 2 وبصمته");
        await Check.Problem(await (await As("m3")).PostAsync($"/api/v1/esign/requests/{req.G("id")}/sign", new { reauthToken = "x" }), 403, "not-a-signer", "المعتذر ليس موقّعًا");
        // الرفض
        await Check.Status(await (await As("m2")).PostAsync($"/api/v1/esign/requests/{req.G("id")}/decline", new { reason = "الرقم في الافتتاح يحتاج تدقيقًا" }), 200, "رفض التوقيع");
        Check.Equal("returned", await MinutesStatus(_minutes), "عاد المحضر للتعديل");
        var card = await sec.GetJsonAsync($"/api/v1/esign/requests/{req.G("id")}");
        Check.That(card.S("status") == "declined" && card["signers"]!.AsArray().Any(s => s?["declineReason"]?.GetValue<string>() == "الرقم في الافتتاح يحتاج تدقيقًا"), "الرفض وسببه محفوظان");
        var s3 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "تدقيق الرقم" }), 200, "رفع ثالث");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = s3.S("contentHash") }), 200, "إقرار الرئيس");
        var reqs = (await sec.GetJsonAsync($"/api/v1/esign/minutes/{_minutes}"))["requests"]!.AsArray();
        Check.That(reqs.Count == 2 && reqs.Any(r => r.S("status") == "declined"), "طلب جديد والمرفوض باقٍ");
        var live = reqs.Single(r => r.S("status") == "in_progress");
        Check.Equal(3, live["versionNo"]!.GetValue<int>(), "الطلب الجديد على الإصدار 3");
        await SignAllAsync(live.G("id"));
        Check.Equal("pending_final_approval", await MinutesStatus(_minutes), "اكتملت التوقيعات");
        await Check.Problem(await sec.PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = s3.S("contentHash") }), 403, "forbidden-action", "أمين السر لا يعتمد");
        var a = await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = s3.S("contentHash") }), 200, "الاعتماد");
        Check.Equal(2, a["issued"]!.AsArray().Count, "صدر القراران بأرقام");
        Check.Equal("issued", (await sec.GetJsonAsync($"/api/v1/resolutions/{_r1}"))["resolution"].S("status"), "القرار صادر");
        foreach (var t in new[] { _tUnit, _tExt })
            Check.Equal("assigned", (await sec.GetJsonAsync($"/api/v1/tasks/{t}"))["task"].S("status"), "المهمة فُعّلت عند الاعتماد");
        var v = await sec.GetJsonAsync($"/api/v1/esign/requests/{live.G("id")}/verify");
        Check.That(v["document"]!["intact"]!.GetValue<bool>() && v.S("overall") == "documented_approval", "التحقق: النسخة سليمة وموافقة موثقة");
    }

    // ============================ 7 ============================

    [Test("U-07", "تحديث المهام ورفع المخرجات: المنفذ يستلم ويحدّث ويقدم المخرج والمتابع يتحقق؛ والجهة الخارجية (البوابة معطلة) يسجل المتابع ردها الرسمي ثم يتحقق", 8)]
    public async Task TaskExecution()
    {
        var emp = await As("emp");
        var fo = await As("fo");
        await Check.Problem(await (await As("outsider")).PostAsync($"/api/v1/tasks/{_tUnit}/actions/accept", new { }), 404, "not-found", "غير المعني لا يرى المهمة");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/accept", new { note = "تم الاستلام" }), 200, "الاستلام");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/updates", new { progressPct = 60, note = "اكتملت مسودة الوثيقة" }), 204, "تحديث التقدم");
        Check.Equal("in_execution", (await (await As("sec")).GetJsonAsync($"/api/v1/resolutions/{_r1}"))["resolution"].S("status"), "القرار قيد التنفيذ");
        var card = await emp.GetJsonAsync($"/api/v1/tasks/{_tUnit}");
        var doc = (await Check.Status(await emp.PostContentAsync("/api/v1/documents", Pdf("scope.pdf", "وثيقة النطاق", "task", _tUnit)), 201, "رفع ملف")).G("id");
        await Check.Status(await emp.PostOnceAsync($"/api/v1/tasks/deliverables/{card["deliverables"]![0]!.G("id")}/submissions", new { documentIds = new[] { doc }, note = "الوثيقة كاملة" }, Key()), 201, "تقديم المخرج");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/submit", new { note = "اكتملت وثيقة النطاق وأُرفقت" }), 200, "رفع للمراجعة");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new { }), 403, "forbidden-action", "المنفذ لا يتحقق من عمله");
        var v = await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new
        { note = "مطابقة لمعايير القبول", deliverableDecisions = new[] { new { deliverableId = card["deliverables"]![0]!.G("id"), decision = "accepted", note = (string?)null } } }), 200, "التحقق");
        Check.Equal("completed", v.S("status"), "مهمة الإدارة مكتملة");

        // الجهة الخارجية: البوابة معطلة؛ المتابع يسجل ما ورد رسميًا
        await Check.Status(await fo.PostOnceAsync($"/api/v1/tasks/{_tExt}/external-updates", new
        { kind = "acknowledged", officialChannel = "letter", reference = "صادر الأمانة 1182", receivedOn = Today, note = "أكدت الأمانة الاستلام" }, Key()), 201, "تسجيل الاستلام");
        await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tExt}/external-updates", new
        { kind = "progress", officialChannel = "email", reference = "بريد الأمانة", receivedOn = Today, note = "أفادت الأمانة بإنجاز 90٪", progressPct = 90 }), 201, "تسجيل التقدم");
        var extDoc = (await Check.Status(await fo.PostContentAsync("/api/v1/documents", Pdf("sites.pdf", "قائمة المواقع", "task", _tExt)), 201, "مرفق وارد")).G("id");
        var dExt = (await fo.GetJsonAsync($"/api/v1/tasks/{_tExt}"))["deliverables"]![0]!.G("id");
        await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tExt}/external-updates", new
        { kind = "deliverable", officialChannel = "correspondence_system", reference = "وارد 5521", receivedOn = Today, note = "ورد مخرج الأمانة", deliverableId = dExt, documentIds = new[] { extDoc } }), 201, "تسجيل المخرج");
        await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tExt}/actions/submit", new { note = "ورد مخرج الأمانة كاملًا بخطابها الرسمي" }), 200, "رفع مهمة الجهة للمراجعة");
        Check.Equal("completed", (await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tExt}/actions/verify", new
        { note = "القائمة مكتملة", deliverableDecisions = new[] { new { deliverableId = dExt, decision = "accepted", note = (string?)null } } }), 200, "تحقق مهمة الجهة")).S("status"), "مهمة الجهة مكتملة");
    }

    // ============================ 10 ============================

    [Test("U-10", "تعطيل التوقيع (بتأكيد الأثر): المسار البديل (الإقرار الداخلي) يكمل اعتماد محضر جديد، والتوقيعات السابقة باقية وقابلة للتحقق", 9)]
    public async Task SignatureDisabled()
    {
        var signedBefore = await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.signers s JOIN sig.requests r ON r.id = s.request_id WHERE r.minutes_id = @m AND s.status = 'signed'", new { m = _minutes });
        await Flags(("E_SIGNATURE_ENABLED", "false"));
        var sec = await As("sec");
        var meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _council, titleAr = "اجتماع المتابعة الثاني", startsAt = DateTime.UtcNow.AddMinutes(30), location = "قاعة المجلس" }), 201, "اجتماع ثانٍ")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/schedule"), 200, "جدولة");
        var item = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/agenda-items", new { titleAr = "متابعة القرارات" }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/invitations/send"), 200, "دعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{meeting}/attendance", new
        { records = new[] { "chair", "sec", "m1", "m2" }.Select(u => new { userId = _u[u], status = "present", mode = "in_person" }).ToArray() }), 200, "حضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/open", new { }), 200, "افتتاح");
        var minutes = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/minutes"), 201, "محضر")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/{minutes}/resolutions", new { resolutionType = "decision", titleAr = "متابعة القرارات", agendaItemId = item, category = "administrative", textAr = "قرر المجلس متابعة القرارات شهريًا." }), 201, "قرار");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/close"), 200, "إغلاق");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{minutes}/submit", new { }), 200, "رفع");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{minutes}/review/approve", new { contentHash = s.S("contentHash") }), 200, "مراجعة الرئيس");
        var m = await Minutes(minutes);
        Check.That(m["minutes"].S("status") == "pending_signatures" && m["signatureMethod"].S("reason") == "esign_disabled", "المسار البديل موثق السبب");
        await Check.Problem(await sec.PostAsync($"/api/v1/esign/minutes/{minutes}/requests"), 404, "feature-disabled", "لا طلبات توقيع والوحدة معطلة");
        foreach (var signoff in m["signoffs"]!.AsArray().Where(x => x.S("status") == "requested"))
            await Check.Status(await (await As(_who[signoff.G("userId")])).PostAsync($"/api/v1/minutes/{minutes}/signoffs", new { contentHash = s.S("contentHash") }), 200, "إقرار");
        Check.Equal("pending_final_approval", await MinutesStatus(minutes), "اكتملت الإقرارات");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{minutes}/approve", new { contentHash = s.S("contentHash") }), 200, "اعتماد بالمسار البديل");
        Check.Equal(signedBefore, await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.signers s JOIN sig.requests r ON r.id = s.request_id WHERE r.minutes_id = @m AND s.status = 'signed'", new { m = _minutes }), "التوقيعات السابقة باقية");
        await Flags(("E_SIGNATURE_ENABLED", "true"));
    }

    // ============================ 11–12 ============================

    [Test("U-11", "تشغيل الذكاء الاصطناعي (المزوّد التجريبي): ملخص المحضر مسودة موسومة بمراجع لسجلات حقيقية، وفشل المزوّد لا يمس العمليات الأساسية", 10)]
    public async Task Ai()
    {
        await Flags(("AI_ENABLED", "true"), ("AI_PROVIDER", "mock"), ("AI_MAX_CLASSIFICATION", "restricted"), ("AI_MINUTES_SUMMARY_ENABLED", "true"));
        var sec = await As("sec");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/ai/minutes/{_minutes}/summary", new { }), 200, "الملخص");
        Check.That(s!["draft"]!.GetValue<bool>() && s["testOnly"]!.GetValue<bool>() && s["citations"]!.AsArray().Count > 0, "مسودة تجريبية بمراجع");
        _aiOutput = s.G("outputId");
        await Check.Status(await sec.PostAsync($"/api/v1/ai/outputs/{_aiOutput}/save", new { note = "للأرشيف" }), 200, "حفظ المسودة");
        // فشل مزوّد خارجي
        await Check.Status(await Admin.PutAsync("/api/v1/ai/mock/faults", new { mode = "unavailable", delayMs = 0, count = 0 }), 200, "عطل محقون");
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutes}/summary", new { }), 503, "provider-unavailable", "المزوّد غير متاح");
        await Check.Status(await sec.GetAsync($"/api/v1/minutes/{_minutes}"), 200, "المحضر متاح أثناء العطل");
        await Check.Status(await Admin.PutAsync("/api/v1/ai/mock/faults", new { mode = "none", delayMs = 0, count = 0 }), 200, "إزالة العطل");
    }

    [Test("U-12", "تعطيل الذكاء الاصطناعي: الوظائف الذكية متوقفة على الخادم، والمخرج المحفوظ باقٍ، والاجتماعات والمهام والبحث التقليدي تعمل", 11)]
    public async Task AiDisabled()
    {
        await Flags(("AI_ENABLED", "false"));
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutes}/summary", new { }), 404, "feature-disabled", "متوقف");
        Check.Equal("saved", await Owner.ScalarAsync<string>("SELECT status FROM ai.outputs WHERE id = @id", new { id = _aiOutput }), "المخرج المحفوظ باقٍ");
        await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _council, titleAr = "اجتماع دون ذكاء اصطناعي", startsAt = DateTime.UtcNow.AddDays(3), location = "القاعة" }), 201, "إنشاء اجتماع");
        await Check.Status(await sec.PostAsync("/api/v1/tasks", new { bodyId = _council, titleAr = "مهمة دون ذكاء اصطناعي", assigneeType = "member", assigneeUserId = _u["m1"], dueDate = Today.AddDays(7), verificationRequired = false }), 201, "إنشاء مهمة");
        var hits = (await sec.GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("البنية الرقمية")))["items"]!.AsArray();
        Check.That(hits.Any(h => h.G("bodyId") == _council), "البحث التقليدي");
        await Flags(("AI_ENABLED", "true"));
    }

    // ============================ 13 والحالات السلبية ============================

    [Test("U-13", "استخراج التقارير ومراجعة سجل التدقيق: اللوحة والتقرير وتصديره تعكس الدورة، والمدقق يرى كل خطوة بمنفذها", 12)]
    public async Task ReportsAndAudit()
    {
        var exec = await As("exec");
        var e = await exec.GetJsonAsync($"/api/v1/reports/executive?bodyId={_council}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}");
        var k = e["kpis"]!;
        Check.That(k["meetings_held"]!.GetValue<long>() == 2 && k["resolutions_issued"]!.GetValue<long>() == 3 && k["tasks_completed"]!.GetValue<long>() == 2, $"اللوحة: اجتماعان منعقدان، 3 قرارات، مهمتان مكتملتان ({k.ToJsonString()})");
        var rep = await exec.GetJsonAsync($"/api/v1/reports/run/resolutions?bodyId={_council}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}");
        Check.Equal(3L, rep["rowCount"]!.GetValue<long>(), "تقرير القرارات");
        var x = await exec.GetAsync($"/api/v1/reports/run/compliance?bodyId={_council}&format=xlsx");
        Check.Equal(200, (int)x.StatusCode, "تصدير Excel");
        var aud = await As("aud");
        var events = (await aud.GetJsonAsync($"/api/v1/audit/events?entityId={_minutes}&pageSize=100")).Items();
        var acts = events.Select(ev => ev.S("action")).ToHashSet();
        foreach (var a in new[] { "minutes.created", "minutes.submitted", "minutes.returned", "minutes.approved" })
            Check.That(acts.Contains(a), "التدقيق يتضمن " + a);
        Check.That(events.All(ev => ev["actorName"] is not null || ev["actorUserId"] is null), "كل حدث بمنفذه");
        await Check.Problem(await (await As("sec")).GetAsync("/api/v1/audit/events"), 403, "forbidden-action", "أمين السر لا يقرأ التدقيق");
    }

    [Test("U-14", "انتهاء عضوية عضو: يبقى سجله وتوقيعاته، ولا يُدعى لاجتماع جديد ولا يرى سجلاته، ومحاولات غير المخول تُرفض", 13)]
    public async Task MembershipEnd()
    {
        var gov = await As("gov");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/bodies/{_council}/members/{_m3Member}/end", new { reason = "انتهاء التكليف" }), 403, "forbidden-action", "العضو لا ينهي عضوية غيره");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_council}/members/{_m3Member}/end", new { reason = "انتهاء مدة التكليف بقرار التشكيل" }), 204, "إنهاء العضوية");
        var members = (await gov.GetJsonAsync($"/api/v1/bodies/{_council}/members?includeEnded=true")).Items();
        Check.That(members.Any(m => m.G("userId") == _u["m3"] && m.S("status") == "ended"), "سجل العضوية محفوظ بحالة «منتهية»");
        var sec = await As("sec");
        var meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _council, titleAr = "اجتماع بعد انتهاء العضوية", startsAt = DateTime.UtcNow.AddDays(2), location = "القاعة" }), 201, "اجتماع")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/schedule"), 200, "جدولة");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/agenda-items", new { titleAr = "بند" }), 201, "بند");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{meeting}/invitations/send"), 200, "دعوات");
        var inv = (await sec.GetJsonAsync($"/api/v1/meetings/{meeting}/invitations")).Items();
        Check.That(inv.All(i => i.G("userId") != _u["m3"]), "العضو المنتهي لم يُدعَ");
        var m3 = new ApiClient(Url);
        await Check.Status(await m3.LoginAsync("u10-m3@test.jsdo.gov.sa", Pw), 200, "دخول العضو المنتهي");
        Check.That((int)(await m3.GetAsync($"/api/v1/meetings/{meeting}")).StatusCode is 403 or 404, "لا يرى الاجتماع الجديد");
        await Check.Problem(await m3.PostAsync($"/api/v1/meetings/{meeting}/agenda-items", new { titleAr = "س" }), 404, "not-found", "لا إجراء على الجهة");
        // محاولات غير مخولة متنوعة
        var outsider = await As("outsider");
        foreach (var (method, path) in new[] { ("POST", $"/api/v1/minutes/{_minutes}/amend"), ("POST", $"/api/v1/meetings/{meeting}/cancel"), ("PUT", "/api/v1/admin/features/AI_ENABLED"), ("POST", "/api/v1/users") })
        {
            var r = method == "PUT" ? await outsider.PutAsync(path, new { value = "false", reason = "محاولة غير مخولة" })
                : path.EndsWith("/users") ? await outsider.PostAsync(path, new { email = "u10-intruder@test.jsdo.gov.sa", displayNameAr = "متسلل", systemRoles = new[] { "system_admin" }, initialPassword = Pw, userType = "internal" })
                : await outsider.PostAsync(path, new { reason = "محاولة غير مخولة للإجراء" });
            Check.That((int)r.StatusCode is 403 or 404, $"غير المخول: {method} {path} (HTTP {(int)r.StatusCode})");
        }
        Check.That(await Owner.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE actor_user_id = @u AND outcome = 'denied'", new { u = _u["outsider"] }) >= 1, "المحاولات المرفوضة مسجلة في التدقيق");
    }
}
