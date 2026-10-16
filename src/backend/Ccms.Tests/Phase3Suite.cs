using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;
using Ccms.Data.PgWire;
using Ccms.Modules.Meetings;

namespace Ccms.Tests;

/// <summary>سيناريوهات قبول المرحلة 3 (المحاضر والقرارات والتصويت) — طرفًا لطرف عبر HTTP على قاعدة PostgreSQL حقيقية،
/// والذكاء الاصطناعي والتوقيع الإلكتروني معطلان بالكامل طوال السيناريوهات.</summary>
public sealed class Phase3Suite(TestContext ctx)
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private readonly Dictionary<string, Guid> _members = [];
    private Guid _comm, _board, _meeting, _minutes, _boardMeeting, _boardMinutes, _draftTask, _draftTask4;
    private readonly Guid[] _items = new Guid[3];
    private readonly Dictionary<string, Guid> _r = [];
    private string _approvedHash = "";
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    private static int Year => Today.Year;

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p3-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    private async Task<JsonNode> Minutes(string user = "sec") => await (await As(user)).GetJsonAsync($"/api/v1/minutes/{_minutes}");
    private async Task<string> Hash(Guid minutesId) => (await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{minutesId}"))["minutes"]!.S("contentHash");

    private static async Task<HttpResponseMessage> Upload(ApiClient c, string ownerType, Guid ownerId, string name, string purpose)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 " + name + " " + Guid.NewGuid()));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", name);
        content.Add(new StringContent(ownerType), "ownerType");
        content.Add(new StringContent(ownerId.ToString()), "ownerId");
        content.Add(new StringContent(purpose), "purpose");
        return await c.PostContentAsync("/api/v1/documents", content);
    }

    private object Policy(bool chairRequired, string signatures, bool chairReview, bool memberReview, string finalApproval, string threshold = "majority_of_votes_cast", bool circulation = false) => new
    {
        effectiveFrom = Today, quorumType = "majority", chairRequired, countRemote = true, allowOverride = false,
        votingThreshold = threshold, abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = true, signoffRequired = signatures != "none",
        minutesReviewDays = 3, extensionApprover = "secretary", allowProxy = true, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = circulation,
        reason = "سياسة اختبار المرحلة 3", minutesSecretaryReview = false, minutesChairReview = chairReview, minutesMemberReview = memberReview,
        minutesSignatures = signatures, minutesSignatureFallback = "attestation", minutesFinalApproval = finalApproval,
    };

    private async Task<Guid> Body(string code, string type, string name, (string User, string Role)[] members, object policy)
    {
        var gov = await As("gov");
        var id = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code, bodyType = type, nameAr = name, nameEn = code }), 201, "جهة " + code)).G("id");
        foreach (var (u, role) in members)
            _members[code + ":" + u] = (await Check.Status(await gov.PostAsync($"/api/v1/bodies/{id}/members", new { userId = _u[u], role }), 201, $"عضوية {u}")).G("id");
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{id}/policy", policy), 200, "سياسة " + code);
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{id}/activate"), 200, "تفعيل " + code);
        return id;
    }

    // ============================ التهيئة ============================

    [Test("P3-SETUP", "تهيئة المرحلة 3: الذكاء الاصطناعي والتوقيع معطلان، ولجنة باجتماع منعقد وحضور بديل، ومجلس أعلى", 0)]
    public async Task Setup()
    {
        var admin = ctx.AdminClient!;
        // النظام يعمل والوحدتان الاختياريتان معطلتان بالكامل
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "false", reason = "اختبار المرحلة 3 دون ذكاء اصطناعي" }), 200, "تعطيل AI");
        var f = await admin.GetJsonAsync("/api/v1/features");
        Check.Equal(false, f["AI_ENABLED"]!["enabled"]!.GetValue<bool>(), "AI معطل");
        Check.Equal(false, f["E_SIGNATURE_ENABLED"]!["enabled"]!.GetValue<bool>(), "التوقيع معطل");

        foreach (var (k, roles) in new (string, string[])[]
        {
            ("gov", ["governance_officer"]), ("chair", []), ("vice", []), ("sec", []), ("m1", []), ("m2", []), ("m3", []), ("alt", []), ("obs", []), ("outsider", []),
            ("bchair", []), ("bsec", []), ("bm1", []), ("auditor", ["auditor"]),
        })
            _u[k] = (await Check.Status(await admin.PostAsync("/api/v1/users", new { email = $"p3-{k}@test.jsdo.gov.sa", displayNameAr = "م3 " + k, systemRoles = roles, initialPassword = Pw }), 201, "مستخدم " + k)).G("id");

        _comm = await Body("P3-COMM", "permanent_committee", "لجنة الحوكمة الرقمية",
            [("chair", "chair"), ("vice", "vice_chair"), ("sec", "secretary"), ("m1", "member"), ("m2", "member"), ("m3", "member"), ("obs", "observer")],
            Policy(chairRequired: true, signatures: "present_members", chairReview: true, memberReview: true, finalApproval: "chair"));
        await Check.Status(await (await As("gov")).PostAsync($"/api/v1/bodies/{_comm}/members", new { userId = _u["alt"], role = "alternate", alternateForMemberId = _members["P3-COMM:m3"] }), 201, "بديل m3");
        _board = await Body("P3-BOARD", "steering_council", "المجلس التوجيهي للتحول",
            [("bchair", "chair"), ("bsec", "secretary"), ("bm1", "member")],
            Policy(chairRequired: false, signatures: "none", chairReview: false, memberReview: false, finalApproval: "recorded_external"));

        var sec = await As("sec");
        _meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new
        { bodyId = _comm, titleAr = "الاجتماع الثالث للجنة الحوكمة الرقمية", startsAt = DateTime.UtcNow.AddHours(1), durationMinutes = 120, location = "قاعة المجلس" }), 201, "اجتماع")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/schedule"), 200, "جدولة");
        var titles = new[] { "اعتماد خطة التحول الرقمي 2027", "ميزانية مشروع البوابة الموحدة", "سياسة تصنيف البيانات" };
        for (var i = 0; i < 3; i++)
            _items[i] = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = titles[i], presenterText = "مدير التحول الرقمي" }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/invitations/send"), 200, "الدعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_meeting}/attendance", new
        {
            records = new object[]
            {
                new { userId = _u["chair"], status = "present", mode = "in_person" }, new { userId = _u["vice"], status = "present", mode = "in_person" },
                new { userId = _u["m1"], status = "present", mode = "in_person" }, new { userId = _u["m2"], status = "present", mode = "remote" },
                new { userId = _u["m3"], status = "excused", note = "مهمة رسمية" }, new { userId = _u["alt"], status = "present", mode = "in_person" },
                new { userId = _u["sec"], status = "present", mode = "in_person" }, new { userId = _u["obs"], status = "present", mode = "in_person" },
            },
        }), 200, "الحضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/open", new { }), 200, "الافتتاح");
    }

    // ============================ المحضر ============================

    [Test("P3-01", "إنشاء محضر من الاجتماع بقالب قابل للتهيئة، دون تكرار عند إعادة المحاولة", 1)]
    public async Task CreateMinutes()
    {
        var sec = await As("sec");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/meetings/{_meeting}/minutes"), 403, "forbidden-action", "العضو لا يُنشئ المحضر");
        var key = Key();
        var first = await sec.PostOnceAsync($"/api/v1/meetings/{_meeting}/minutes", null, key);
        _minutes = (await Check.Status(first, 201, "إنشاء المحضر")).G("id");
        var again = await sec.PostOnceAsync($"/api/v1/meetings/{_meeting}/minutes", null, key);
        Check.Equal(_minutes, (await Check.Status(again, 201, "إعادة الطلب بالمفتاح نفسه")).G("id"), "النتيجة الأولى تُعاد");
        Check.That(again.Headers.Contains("Idempotent-Replayed"), "الرد معاد لا منفذ ثانية");
        await Check.Problem(await sec.PostOnceAsync($"/api/v1/meetings/{_meeting}/minutes", null, Key()), 409, "duplicate", "محضر ثانٍ للاجتماع نفسه");

        var m = await Minutes();
        Check.Equal("draft", m["minutes"].S("status"), "مسودة");
        Check.That(m["template"]!.AsArray().Any(s => s?["source"]?.GetValue<string>() == "attendance") && m["template"]!.AsArray().Any(s => s.S("key") == "opening"), "القالب الرسمي: أقسام آلية ونصية");
        var att = m["snapshot"]!["attendance"]!.AsArray();
        Check.That(att.Any(a => a.S("role") == "alternate" && a.S("status") == "present") && att.Any(a => a.S("status") == "excused"), "الحضور والاعتذار في اللقطة");
        Check.Equal(3, m["snapshot"]!["agenda"]!.AsArray().Count, "جدول الأعمال");
        Check.That(m["snapshot"]!["quorum"]!["isMet"]!.GetValue<bool>(), "النصاب من فحص الافتتاح");
        Check.That(m["actions"]!["canEdit"]!.GetValue<bool>() && !m["actions"]!["canSubmit"]!.GetValue<bool>(), "قابل للتحرير ولا يُرفع قبل إغلاق الاجتماع");

        var templates = (await sec.GetJsonAsync("/api/v1/minutes/templates")).AsArray();
        Check.That(templates.Count >= 2 && templates.Any(t => t["isDefault"]!.GetValue<bool>()), "قوالب متعددة وأحدها افتراضي");
        await Check.Problem(await sec.PostAsync("/api/v1/minutes/templates", new
        { code = "custom", nameAr = "قالب", isDefault = false, sections = new object[] { new { key = "notes", titleAr = "ملاحظات", kind = "text" }, new { key = "approval", titleAr = "الاعتماد", kind = "auto", source = "approval" } } }), 403, "forbidden-action", "أمين السر لا يدير القوالب");
        var tpl = await Check.Status(await (await As("gov")).PostAsync("/api/v1/minutes/templates", new
        {
            code = "workshop", nameAr = "محضر ورشة عمل", isDefault = false,
            sections = new object[]
            {
                new { key = "meeting_info", titleAr = "بيانات الورشة", kind = "auto", source = "meeting_info" },
                new { key = "outputs", titleAr = "المخرجات", kind = "text", defaultBody = "" },
                new { key = "approval", titleAr = "الاعتماد", kind = "auto", source = "approval" },
            },
        }), 201, "قالب جديد من مسؤول المجالس");
        Check.Equal("workshop", tpl.S("code"), "القالب محفوظ");
    }

    [Test("P3-02", "قرارات وتوصيات متعددة مرتبطة ببنود جدول الأعمال، دون تكرار القرار عند إعادة الإرسال", 2)]
    public async Task ProposeResolutions()
    {
        var sec = await As("sec");
        var url = $"/api/v1/minutes/{_minutes}/resolutions";
        await Check.Problem(await (await As("m1")).PostAsync(url, new { resolutionType = "decision", titleAr = "س", textAr = "ص" }), 403, "forbidden-action", "العضو لا يضيف قرارات");
        var r1Key = Key();
        var r1Body = new
        {
            resolutionType = "decision", category = "policy", titleAr = "اعتماد خطة التحول الرقمي 2027", agendaItemId = _items[0],
            textAr = "قررت اللجنة اعتماد خطة التحول الرقمي لعام 2027 ورفعها لصاحب الصلاحية.", authorityText = "رئيس اللجنة", responsibleText = "الإدارة العامة لتقنية المعلومات",
        };
        _r["R1"] = (await Check.Status(await sec.PostOnceAsync(url, r1Body, r1Key), 201, "قرار 1")).G("id");
        // إعادة المحاولة أو تحديث الصفحة: المفتاح نفسه لا ينشئ قرارًا ثانيًا
        Check.Equal(_r["R1"], (await Check.Status(await sec.PostOnceAsync(url, r1Body, r1Key), 201, "إعادة إرسال القرار")).G("id"), "القرار نفسه");
        await Check.Problem(await sec.PostOnceAsync(url, new { resolutionType = "decision", titleAr = "آخر", textAr = "نص" }, r1Key), 409, "idempotency-key-reused", "المفتاح نفسه لطلب مختلف");
        _r["R2"] = (await Check.Status(await sec.PostOnceAsync(url, new
        {
            resolutionType = "decision", category = "financial", titleAr = "اعتماد ميزانية البوابة الموحدة", agendaItemId = _items[1],
            textAr = "قررت اللجنة اعتماد ميزانية تقديرية للبوابة الموحدة وفق العرض المقدم.", responsibleText = "الإدارة المالية",
        }, Key()), 201, "قرار 2")).G("id");
        _r["R3"] = (await Check.Status(await sec.PostOnceAsync(url, new
        {
            resolutionType = "recommendation", category = "policy", titleAr = "التوصية باعتماد سياسة تصنيف البيانات", agendaItemId = _items[2],
            textAr = "توصي اللجنة المجلس التوجيهي باعتماد سياسة تصنيف البيانات.", targetBodyId = _board,
        }, Key()), 201, "توصية لجهة أعلى")).G("id");
        _r["R4"] = (await Check.Status(await sec.PostOnceAsync(url, new { resolutionType = "decision", titleAr = "قرار سيُسحب", textAr = "نص مؤقت", agendaItemId = _items[0] }, Key()), 201, "قرار 4")).G("id");
        await Check.Problem(await sec.PostAsync(url, new { resolutionType = "decision", titleAr = "رفع", textAr = "ن", targetBodyId = _board }), 400, "validation", "القرار لا يُرفع لجهة أعلى");
        await Check.Problem(await sec.PostAsync(url, new { resolutionType = "decision", titleAr = "بند غريب", textAr = "ن", agendaItemId = Guid.NewGuid() }), 400, "validation", "بند لا يتبع الاجتماع");

        var list = (await sec.GetJsonAsync($"/api/v1/resolutions?minutesId={_minutes}")).Items();
        Check.Equal(4, list.Count, "أربعة قرارات بلا تكرار");
        Check.That(list.All(r => r.S("status") == "proposed" && r["number"] is null), "مقترحة بلا رقم قبل الاعتماد");
        Check.Equal(_items[0], list.First(r => r.G("id") == _r["R1"]).G("agendaItemId"), "مرتبط بالبند");
        Check.That(list.Select(r => r["position"]!.GetValue<int>()).SequenceEqual([1, 2, 3, 4]), "ترتيب القرارات");

        // التكليف منفصل عن القرار: مهمة مسودة مرتبطة بالقرار المقترح، بلا رقم ولا إشعار حتى الاعتماد
        var t = await Check.Status(await sec.PostOnceAsync("/api/v1/tasks", new
        { bodyId = _comm, resolutionId = _r["R1"], titleAr = "إعداد الخطة التنفيذية", assigneeType = "user", assigneeUserId = _u["m1"], dueDate = Today.AddDays(30) }, Key()), 201, "مهمة مسودة");
        _draftTask = t.G("id");
        Check.Equal("new", t.S("status"), "المهمة جديدة حتى اعتماد المحضر");
        _draftTask4 = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _comm, resolutionId = _r["R4"], titleAr = "مهمة القرار المسحوب", assigneeType = "user", assigneeUserId = _u["m2"], dueDate = Today.AddDays(10) }), 201, "مهمة 4")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/resolutions/{_r["R4"]}/withdraw", new { reason = "دُمج في القرار الأول" }), 204, "سحب القرار 4");
        Check.Equal("cancelled", (await sec.GetJsonAsync($"/api/v1/tasks/{_draftTask4}"))["task"].S("status"), "مهمة القرار المسحوب أُلغيت");
        Check.Equal("withdrawn", (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R4"]}"))["resolution"].S("status"), "القرار المسحوب محفوظ بحالته");
    }

    // ============================ التصويت ============================

    [Test("P3-03", "التصويت: الأهلية وقت التصويت، ومنع التكرار، والبديل، والتحفظ، ورفع الأيدي، والسري، والترجيح، والنصاب", 3)]
    public async Task Voting()
    {
        var sec = await As("sec");
        var motions = $"/api/v1/meetings/{_meeting}/motions";
        await Check.Problem(await (await As("m1")).PostAsync(motions, new { resolutionId = _r["R1"], method = "electronic" }), 403, "forbidden-action", "العضو لا يفتح التصويت");
        var openKey = Key();
        var mo1 = (await Check.Status(await sec.PostOnceAsync(motions, new { resolutionId = _r["R1"], method = "electronic" }, openKey), 201, "فتح التصويت الإلكتروني")).G("id");
        Check.Equal(mo1, (await Check.Status(await sec.PostOnceAsync(motions, new { resolutionId = _r["R1"], method = "electronic" }, openKey), 201, "إعادة الفتح بالمفتاح نفسه")).G("id"), "لا مقترح مكرر");
        await Check.Problem(await sec.PostAsync(motions, new { resolutionId = _r["R1"], method = "electronic" }), 409, "motion-open", "تصويت مفتوح مسبقًا");

        var vote = $"/api/v1/motions/{mo1}/votes";
        var k = Key();
        await Check.Status(await (await As("m1")).PostOnceAsync(vote, new { choice = "for" }, k), 200, "صوت m1");
        await Check.Status(await (await As("m1")).PostOnceAsync(vote, new { choice = "for" }, k), 200, "إعادة إرسال الصوت نفسه (يُعاد الرد)");
        await Check.Problem(await (await As("m1")).PostAsync(vote, new { choice = "against" }), 409, "already-voted", "لا صوت مكرر");
        await Check.Problem(await (await As("obs")).PostAsync(vote, new { choice = "for" }), 403, "forbidden-action", "المراقب لا يصوت");
        await Check.Problem(await (await As("m3")).PostAsync(vote, new { choice = "for" }), 403, "forbidden-action", "العضو المعتذر غير مؤهل وقت التصويت");
        await Check.Problem(await (await As("outsider")).PostAsync(vote, new { choice = "for" }), 404, "not-found", "من خارج الجهة");
        await Check.Status(await (await As("alt")).PostAsync(vote, new { choice = "for", principalUserId = _u["m3"] }), 200, "البديل يصوت عن أصيله");
        await Check.Status(await (await As("m2")).PostAsync(vote, new { choice = "against", reservation = "أتحفظ على الجدول الزمني المقترح" }), 200, "صوت ضد مع تحفظ");
        await Check.Status(await (await As("vice")).PostAsync(vote, new { choice = "abstain" }), 200, "امتناع");
        var last = await Check.Status(await (await As("chair")).PostAsync(vote, new { choice = "for" }), 200, "صوت الرئيس");
        Check.That(last!["closed"]!.GetValue<bool>(), "أُغلق آليًا بعد تصويت كل المؤهلين");
        Check.Equal("adopted", last.S("result"), "الأغلبية: 3 مع، 1 ضد، 1 ممتنع");

        // رفع الأيدي يسجله أمين السر؛ والأصوات الفردية لمن يملك الصلاحية فقط
        var mo2 = (await Check.Status(await sec.PostAsync(motions, new { resolutionId = _r["R2"], method = "show_of_hands" }), 201, "رفع الأيدي")).G("id");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/motions/{mo2}/votes", new { choice = "for" }), 409, "invalid-method", "رفع الأيدي لا يُدلى به ذاتيًا");
        await Check.Status(await sec.PostAsync($"/api/v1/motions/{mo2}/record", new
        {
            votes = new object[]
            {
                new { principalUserId = _u["chair"], choice = "for" }, new { principalUserId = _u["vice"], choice = "for" }, new { principalUserId = _u["m1"], choice = "for" },
                new { principalUserId = _u["m2"], choice = "against" }, new { principalUserId = _u["m3"], choice = "for" },
            },
        }), 204, "تسجيل الأصوات");
        await Check.Problem(await sec.PostAsync($"/api/v1/motions/{mo2}/record", new { votes = new[] { new { principalUserId = _u["m1"], choice = "against" } } }), 409, "already-voted", "لا تسجيل مكرر");
        Check.Equal("adopted", (await Check.Status(await sec.PostAsync($"/api/v1/motions/{mo2}/close"), 200, "إغلاق")).S("result"), "4 مقابل 1");
        var memberView = (await (await As("m1")).GetJsonAsync(motions))["items"]!.AsArray().First(x => x.G("id") == mo2);
        Check.That(memberView["votes"] is null && memberView["tally"]!["for"]!.GetValue<int>() == 4, "العضو يرى النتيجة لا الأصوات الفردية");
        var chairView = (await (await As("chair")).GetJsonAsync(motions))["items"]!.AsArray().First(x => x.G("id") == mo2);
        Check.Equal(5, chairView["votes"]!.AsArray().Count, "الرئيس يرى الأصوات الفردية في العلني");

        // السري: لا يُحفظ من صوّت بماذا، والمشاركة تمنع التكرار
        var mo3 = (await Check.Status(await sec.PostAsync(motions, new { resolutionId = _r["R3"], method = "secret" }), 201, "سري")).G("id");
        foreach (var (u, c) in new[] { ("chair", "for"), ("vice", "for"), ("m1", "for"), ("m2", "against") })
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/motions/{mo3}/votes", new { choice = c }), 200, "سري " + u);
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/motions/{mo3}/votes", new { choice = "for" }), 409, "already-voted", "لا تكرار في السري");
        // قبل اكتمال الأصوات: لا يُغلق الاجتماع وفيه تصويت مفتوح
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_meeting}/close"), 409, "motion-open", "تصويت مفتوح يمنع إغلاق الاجتماع");
        var r3 = await Check.Status(await (await As("alt")).PostAsync($"/api/v1/motions/{mo3}/votes", new { choice = "abstain", principalUserId = _u["m3"] }), 200, "سري البديل");
        Check.Equal("adopted", r3.S("result"), "السري: 3 مع، 1 ضد");
        var secretView = (await (await As("chair")).GetJsonAsync(motions))["items"]!.AsArray().First(x => x.G("id") == mo3);
        Check.That(secretView["votes"] is null && secretView["votedCount"]!.GetValue<int>() == 5, "لا أصوات فردية في السري حتى للرئيس");
        await using (var app = new PgWireDataSource(ctx.AppConnectionString))
        {
            var named = await new Db(app).ScalarAsync<long>("SELECT count(*) FROM deliberation.votes WHERE motion_id = @mo3 AND (principal_user_id IS NOT NULL OR cast_by_user_id IS NOT NULL)", new { mo3 });
            Check.Equal(0L, named, "الأصوات السرية مخزنة بلا هوية");
        }

        // التعادل يرجحه صوت الرئيس وفق سياسة الجهة
        _r["R5"] = (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        { resolutionType = "decision", titleAr = "تشكيل فريق عمل للبيانات المفتوحة", textAr = "قررت اللجنة تشكيل فريق عمل للبيانات المفتوحة.", agendaItemId = _items[2] }), 201, "قرار 5")).G("id");
        var mo5 = (await Check.Status(await sec.PostAsync(motions, new { resolutionId = _r["R5"], method = "electronic" }), 201, "تصويت 5")).G("id");
        foreach (var (u, c, p) in new[] { ("chair", "for", (Guid?)null), ("vice", "against", null), ("m1", "for", null), ("m2", "against", null), ("alt", "abstain", _u["m3"]) })
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/motions/{mo5}/votes", new { choice = c, principalUserId = p }), 200, "صوت " + u);
        var tie = (await sec.GetJsonAsync(motions))["items"]!.AsArray().First(x => x.G("id") == mo5);
        Check.Equal("adopted", tie.S("result"), "التعادل 2-2 بصوت الرئيس المرجح");
        Check.That(tie["tally"]!["chairCasting"]!.GetValue<bool>(), "مسجل أنه ترجيح");

        // تغيّر الحضور يغير الأهلية والنصاب: بعد مغادرة ثلاثة لا يُفتح تصويت
        _r["R6"] = (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new { resolutionType = "decision", titleAr = "قرار بعد المغادرة", textAr = "نص" }), 201, "قرار 6")).G("id");
        foreach (var u in new[] { "vice", "m1", "m2" })
            await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/attendance/{_u[u]}/depart"), 200, "مغادرة " + u);
        await Check.Problem(await sec.PostAsync(motions, new { resolutionId = _r["R6"], method = "electronic" }), 409, "quorum-not-met", "لا تصويت دون نصاب");
        await Check.Status(await sec.PostAsync($"/api/v1/resolutions/{_r["R6"]}/withdraw", new { reason = "لم يكتمل النصاب للتصويت عليه" }), 204, "سحب 6");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/close"), 200, "إغلاق الاجتماع");
    }

    [Test("P3-04", "قواعد تصويت قابلة للتهيئة (V1–V8): الأغلبية والثلثان والإجماع والامتناع والترجيح", 4)]
    public Task VotingRules()
    {
        VotingRule R(string threshold, string abst = "excluded", string tie = "chair_casting_vote") => new() { Threshold = threshold, Abstentions = abst, TieBreaker = tie };
        var cases = new (string Id, VotingRule Rule, Tally Tally, string Expected)[]
        {
            ("V1", R("majority_of_votes_cast"), new(3, 2, 2, 7, 9, "for"), "adopted"),
            ("V2", R("majority_of_votes_cast"), new(3, 3, 0, 6, 9, "for"), "adopted"),
            ("V3", R("majority_of_votes_cast", tie: "motion_fails"), new(3, 3, 0, 6, 9, "for"), "rejected"),
            ("V4", R("majority_of_votes_cast", "count_as_against"), new(3, 2, 2, 7, 9, "for"), "rejected"),
            ("V5", R("two_thirds_of_present"), new(6, 3, 0, 9, 9, "against"), "adopted"),
            ("V6", R("two_thirds_of_present"), new(5, 2, 2, 9, 9, "for"), "rejected"),
            ("V7", R("unanimity"), new(4, 0, 1, 5, 9, "for"), "rejected"),
            ("V8", R("absolute_majority_of_members"), new(5, 1, 0, 6, 9, "for"), "adopted"),
            ("V9", R("majority_of_votes_cast", tie: "revote"), new(2, 2, 1, 5, 5, "for"), "tied_unresolved"),
            ("V10", R("majority_of_present"), new(3, 1, 2, 6, 9, null), "rejected"),
        };
        foreach (var c in cases) Check.Equal(c.Expected, VotingCalculator.Decide(c.Rule, c.Tally).Result, c.Id);
        return Task.CompletedTask;
    }

    // ============================ دورة الاعتماد ============================

    [Test("P3-05", "دورة الاعتماد: مراجعة الرئيس، وملاحظات الأعضاء، وإعادة المحضر للمراجعة، وإصدارات بالبصمة، والتوقيع البديل", 5)]
    public async Task ApprovalCycle()
    {
        var sec = await As("sec");
        var draft = new
        {
            sections = new[]
            {
                new { key = "opening", title = "الافتتاح", body = "افتتح رئيس اللجنة الاجتماع بالترحيب بالأعضاء." },
                new { key = "closing", title = "الختام", body = "اختُتم الاجتماع في تمام الساعة الثانية عشرة." },
            },
            items = _items.Select((id, i) => new { agendaItemId = id, discussion = $"ناقشت اللجنة البند {i + 1} واستعرضت الملاحظات.", recommendation = (string?)null }).ToArray(),
        };
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", draft), 200, "حفظ المسودة");
        await Check.Problem(await (await As("m1")).PutAsync($"/api/v1/minutes/{_minutes}/draft", draft), 403, "forbidden-action", "العضو لا يحرر");

        var s1 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "المسودة الأولى" }), 200, "رفع للمراجعة");
        Check.Equal("chair_review", s1.S("status"), "أول مرحلة مفعلة: مراجعة الرئيس");
        Check.Equal(1, s1["versionNo"]!.GetValue<int>(), "الإصدار 1");
        var h1 = s1.S("contentHash");
        await Check.Problem(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", draft), 409, "minutes-in-review", "لا تعديل أثناء المراجعة");
        await Check.Problem(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new { resolutionType = "decision", titleAr = "س", textAr = "ص" }), 409, "minutes-locked", "لا قرارات جديدة أثناء المراجعة");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h1 }), 403, "forbidden-action", "العضو لا يقر مرحلة الرئيس");
        await Check.Problem(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = new string('a', 64) }), 409, "hash-mismatch", "بصمة قديمة");
        Check.Equal("member_review", (await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h1, note = "لا ملاحظات" }), 200, "إقرار الرئيس")).S("status"), "ملاحظات الأعضاء");

        // الملاحظات: من الأعضاء لا المراقبين، وتمنع إنهاء الجولة حتى تُعالج
        await Check.Status(await (await As("m1")).PostOnceAsync($"/api/v1/minutes/{_minutes}/comments", new { anchor = "decisions", text = "يُقترح ذكر الموعد المستهدف في القرار الأول." }, Key()), 201, "ملاحظة عضو");
        await Check.Problem(await (await As("obs")).PostAsync($"/api/v1/minutes/{_minutes}/comments", new { anchor = "opening", text = "ملاحظة" }), 403, "forbidden-action", "المراقب لا يعلّق");
        await Check.Problem(await sec.PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h1 }), 409, "open-comments", "ملاحظات مفتوحة");
        await Check.Problem(await (await As("m2")).PostAsync($"/api/v1/minutes/{_minutes}/return", new { reason = "أرغب في التعديل" }), 403, "forbidden-action", "العضو لا يعيد المحضر");
        Check.Equal("returned", (await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/return", new { reason = "معالجة ملاحظة الموعد المستهدف" }), 200, "الإعادة للتعديل")).S("status"), "معاد للتعديل");

        var m = await Minutes();
        Check.Equal("returned", m["minutes"].S("status"), "الحالة محفوظة");
        Check.That(m["approvals"]!.AsArray().Any(a => a.S("decision") == "returned" && a.S("note").Contains("الموعد")), "سبب الإعادة موثق");
        var comment = (await sec.GetJsonAsync($"/api/v1/minutes/{_minutes}/comments")).AsArray()[0]!;
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/comments/{comment.G("id")}/resolve", new { status = "accepted", note = "أُضيف الموعد" }), 204, "قبول الملاحظة");
        // نص قرار صُوّت عليه لا يُغير؛ والتصنيف يُعدل
        await Check.Problem(await sec.PatchAsync($"/api/v1/resolutions/{_r["R1"]}", new { textAr = "نص مختلف بعد التصويت" }), 409, "voted-text-locked", "نص مصوت عليه");
        await Check.Status(await sec.PatchAsync($"/api/v1/resolutions/{_r["R1"]}", new { responsibleText = "الإدارة العامة لتقنية المعلومات — خلال الربع الأول 2027" }), 200, "تعديل الجهة المنفذة");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", draft with { sections = [draft.sections[0] with { body = "افتتح رئيس اللجنة الاجتماع، وأُقر جدول الأعمال." }, draft.sections[1]] }), 200, "تعديل بعد الإعادة");

        var s2 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "معالجة ملاحظات الرئيس" }), 200, "إعادة الرفع");
        Check.Equal(2, s2["versionNo"]!.GetValue<int>(), "إصدار جديد");
        Check.That(s2.S("contentHash") != h1, "بصمة مختلفة");
        var h2 = s2.S("contentHash");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h2 }), 200, "الرئيس");
        Check.Equal("pending_signatures", (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h2, note = "انتهت مهلة الملاحظات" }), 200, "إنهاء الملاحظات")).S("status"), "انتظار التوقيع");

        m = await Minutes();
        var signoffs = m["signoffs"]!.AsArray().Where(s => s["versionNo"]!.GetValue<int>() == 2).ToList();
        Check.Equal(3, signoffs.Count, "الموقعون: الحاضرون من الأعضاء المصوتين عدا الرئيس (النائب، m1، m2)");
        Check.That(signoffs.All(s => s.S("method") == "attestation" && s.S("methodReason") == "esign_disabled"), "التوقيع الإلكتروني معطل: إقرار داخل النظام موثق السبب");
        Check.Equal("attestation", m["signatureMethod"]!.S("method"), "الطريقة المعروضة");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h1 }), 409, "hash-mismatch", "الإقرار على بصمة الإصدار الحالي فقط");
        await Check.Problem(await (await As("m3")).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h2 }), 409, "no-signoff-request", "المعتذر غير مطلوب");
        await Check.Status(await (await As("m1")).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h2 }), 200, "إقرار m1");
        await Check.Status(await (await As("vice")).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h2, reservation = "أتحفظ على صياغة القرار الثاني" }), 200, "إقرار مع تحفظ");
        await Check.Problem(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = h2 }), 409, "invalid-transition", "لا اعتماد قبل اكتمال التوقيعات");
        var done = await Check.Status(await (await As("m2")).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h2 }), 200, "إقرار m2");
        Check.Equal("pending_final_approval", done.S("status"), "انتقال آلي للاعتماد النهائي");
        _approvedHash = h2;
    }

    [Test("P3-06", "منع غير المخول من الاعتماد، والاعتماد يصدر القرارات بأرقام فريدة ويفعّل المهام ويغلق الاجتماع، دون تكرار", 6)]
    public async Task FinalApproval()
    {
        var url = $"/api/v1/minutes/{_minutes}/approve";
        var body = new { contentHash = _approvedHash };
        await Check.Problem(await (await As("m1")).PostAsync(url, body), 403, "forbidden-action", "العضو لا يعتمد");
        await Check.Problem(await (await As("sec")).PostAsync(url, body), 403, "forbidden-action", "أمين السر لا يعتمد");
        await Check.Problem(await (await As("obs")).PostAsync(url, body), 403, "forbidden-action", "المراقب لا يعتمد");
        await Check.Problem(await (await As("gov")).PostAsync(url, body), 403, "forbidden-action", "مسؤول المجالس لا يعتمد محاضر اللجان");
        await Check.Problem(await (await As("outsider")).PostAsync(url, body), 404, "not-found", "من خارج الجهة لا يرى المحضر");
        var r = await Check.Status(await (await As("chair")).PostAsync(url, body), 200, "اعتماد الرئيس");
        Check.Equal("approved", r.S("status"), "معتمد");
        var issued = r["issued"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Check.Equal(4, issued.Count, "أربعة قرارات صدرت (المسحوبان لا يصدران)");
        Check.Equal($"P3-COMM/{Year}/001", issued[0], "الترقيم بتسلسل سنوي للجهة");
        Check.That(r.S("verificationCode").Length == 11 && r.S("pdfSha256").Length == 64, "رمز تحقق وPDF محفوظ ببصمة");
        await Check.Problem(await (await As("chair")).PostAsync(url, body), 409, "invalid-transition", "الاعتماد لا يتكرر");

        var sec = await As("sec");
        var r1 = (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R1"]}"))["resolution"]!;
        Check.Equal("issued", r1.S("status"), "صادر");
        Check.Equal($"P3-COMM/{Year}/001", r1.S("number"), "رقم القرار");
        Check.Equal(2, r1["minutesVersionNo"]!.GetValue<int>(), "مرتبط بالإصدار المعتمد");
        Check.Equal(_meeting, r1.G("meetingId"), "مصدره الاجتماع");
        Check.Equal("vote", r1.S("adoptionMethod"), "اعتُمد بالتصويت");
        Check.Equal(3, r1["voteSummary"]!["for"]!.GetValue<int>(), "نتيجة التصويت محفوظة مع القرار");
        Check.Equal("referred", (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R3"]}"))["resolution"].S("status"), "التوصية رُفعت للجهة الأعلى آليًا");
        var notif = (await (await As("bsec")).GetJsonAsync("/api/v1/notifications")).Items();
        Check.That(notif.Any(n => n.S("eventType") == "recommendation_referred"), "أمين سر الجهة الأعلى أُشعر");
        Check.That((await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R4"]}"))["resolution"]!["number"] is null, "المسحوب بلا رقم");
        var task = (await sec.GetJsonAsync($"/api/v1/tasks/{_draftTask}"))["task"]!;
        Check.That(task.S("status") == "assigned" && task.S("referenceNo").StartsWith($"P3-COMM-T-{Year}-"), "المهمة فُعّلت ورُقّمت عند الاعتماد");
        Check.Equal("closed", (await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}"))["meeting"]!.S("status"), "الاجتماع مغلق");
        var approvals = (await Minutes())["approvals"]!.AsArray();
        var final = approvals.Last(a => a.S("decision") == "approved");
        Check.That(final.S("capacity") == "chair" && final.S("method") == "in_system" && final.S("contentHash") == _approvedHash, "توثيق من اعتمد وكيف وعلى أي بصمة");
    }

    [Test("P3-07", "تصدير PDF: نسخة المعتمد محفوظة ببصمتها ومطابقة، والإصدارات السابقة تُصدر مسودة", 7)]
    public async Task Pdf()
    {
        var sec = await As("sec");
        var m = (await Minutes())["minutes"]!;
        var bytes = await sec.GetBytesAsync($"/api/v1/minutes/{_minutes}/pdf");
        Check.That(bytes.Length > 8000 && System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-", "ملف PDF");
        Check.Equal(m.S("pdfSha256"), Convert.ToHexStringLower(SHA256.HashData(bytes)), "الملف المسلم هو المحفوظ ببصمته");
        var again = await (await As("m1")).GetBytesAsync($"/api/v1/minutes/{_minutes}/pdf");
        Check.That(again.SequenceEqual(bytes), "النسخة نفسها لكل مستفيد");
        var v1 = await sec.GetBytesAsync($"/api/v1/minutes/{_minutes}/pdf?versionNo=1");
        Check.That(v1.Length > 8000 && !v1.SequenceEqual(bytes), "الإصدار 1 يُصدر مسودة مستقلة");
        var verify = await sec.GetJsonAsync($"/api/v1/minutes/verify/{m.S("verificationCode")}");
        Check.That(verify.S("contentHash") == _approvedHash && verify["approvedVersionNo"]!.GetValue<int>() == 2, "رمز التحقق يرد البصمة والإصدار");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/minutes/{_minutes}/pdf"), 404, "not-found", "PDF محكوم بصلاحية المحضر");
    }

    [Test("P3-08", "لا تعديل صامت للمعتمد: التعديل بسبب ينتج إصدارًا جديدًا يُعاد اعتماده، وقرار لاحق يلغي سابقًا", 8)]
    public async Task Amendment()
    {
        var sec = await As("sec");
        var draft = new { sections = new[] { new { key = "opening", title = "الافتتاح", body = "تعديل صامت" } } };
        await Check.Problem(await sec.PutAsync($"/api/v1/minutes/{_minutes}/draft", draft), 409, "minutes-approved", "المعتمد لا يُعدل مباشرة");
        await using (var app = new PgWireDataSource(ctx.AppConnectionString))
        {
            var denied = false;
            try { await new Db(app).ExecuteAsync("UPDATE minutes.minutes_versions SET content_hash = 'x' WHERE minutes_id = @id", new { id = _minutes }); }
            catch (Exception e) when (e.Is(SqlErrors.InsufficientPrivilege)) { denied = true; }
            Check.That(denied, "حساب التطبيق لا يملك تعديل الإصدارات في القاعدة");
        }
        await Check.Problem(await sec.PostAsync($"/api/v1/minutes/{_minutes}/amend", new { reason = "س" }), 400, "validation", "سبب التعديل إلزامي ومفصل");
        var a = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/amend", new { reason = "تصحيح صياغة القرار الثاني بإلغائه بقرار لاحق" }), 200, "فتح التعديل");
        Check.That(a["amendmentNo"]!.GetValue<int>() == 1 && a.S("status") == "draft", "تعديل رقم 1");
        _r["R7"] = (await Check.Status(await sec.PostOnceAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        {
            resolutionType = "decision", titleAr = "إلغاء قرار الميزانية", textAr = "قررت اللجنة إلغاء القرار الخاص بميزانية البوابة لحين ورود التسعيرة النهائية.",
            supersedesResolutionId = _r["R2"], supersedeKind = "revokes", agendaItemId = _items[1],
        }, Key()), 201, "قرار يلغي سابقًا")).G("id");
        var s3 = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { changeSummary = "التعديل 1" }), 200, "رفع التعديل");
        Check.Equal(3, s3["versionNo"]!.GetValue<int>(), "إصدار 3");
        var h3 = s3.S("contentHash");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h3 }), 200, "مراجعة الرئيس");
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/review/approve", new { contentHash = h3 }), 200, "ملاحظات الأعضاء");
        foreach (var u in new[] { "vice", "m1", "m2" })
            await Check.Status(await (await As(u)).PostAsync($"/api/v1/minutes/{_minutes}/signoffs", new { contentHash = h3 }), 200, "إقرار " + u);
        var r = await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = h3 }), 200, "إعادة الاعتماد");
        Check.Equal($"P3-COMM/{Year}/005", r!["issued"]!.AsArray().Single()!.GetValue<string>(), "يصدر الجديد وحده برقم تالٍ");

        Check.Equal("superseded", (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R2"]}"))["resolution"].S("status"), "القرار السابق أُلغي");
        var r1 = (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R1"]}"))["resolution"]!;
        Check.That(r1.S("number") == $"P3-COMM/{Year}/001" && r1["minutesVersionNo"]!.GetValue<int>() == 2, "القرارات السابقة لم يُعد إصدارها ولم تتغير أرقامها");
        var versions = (await sec.GetJsonAsync($"/api/v1/minutes/{_minutes}/versions")).AsArray();
        Check.Equal(3, versions.Count, "ثلاثة إصدارات محفوظة");
        Check.Equal(4L, versions.First(v => v["versionNo"]!.GetValue<int>() == 2)!["resolutionsIssued"]!.GetValue<long>(), "الإصدار 2 أصدر أربعة قرارات");
        Check.Equal(1L, versions.First(v => v["versionNo"]!.GetValue<int>() == 3)!["resolutionsIssued"]!.GetValue<long>(), "الإصدار 3 أصدر قرارًا واحدًا");
        var v2 = await sec.GetJsonAsync($"/api/v1/minutes/{_minutes}/versions/2");
        Check.Equal(_approvedHash, v2["version"]!.S("contentHash"), "الإصدار المعتمد الأول بقي ببصمته");
        Check.Equal(4, v2["issuedResolutions"]!.AsArray().Count, "علاقة الإصدار بقراراته");
        var m = (await Minutes())["minutes"]!;
        Check.That(m["approvedVersionNo"]!.GetValue<int>() == 3 && m["amendmentNo"]!.GetValue<int>() == 1, "المعتمد الحالي هو الإصدار 3");
    }

    [Test("P3-09", "مسار الاعتماد البديل الذي يحدده المكتب: تسجيل اعتماد صاحب الصلاحية خارج النظام بمرجع ودليل", 9)]
    public async Task ExternalApproval()
    {
        var bsec = await As("bsec");
        _boardMeeting = (await Check.Status(await bsec.PostAsync("/api/v1/meetings", new
        { bodyId = _board, titleAr = "اجتماع المجلس التوجيهي", startsAt = DateTime.UtcNow.AddHours(2), location = "مكتب الرئيس التنفيذي" }), 201, "اجتماع المجلس")).G("id");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/schedule"), 200, "جدولة");
        var item = (await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/agenda-items", new { titleAr = "توصية لجنة الحوكمة بسياسة تصنيف البيانات" }), 201, "بند")).G("id");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/invitations/send"), 200, "دعوات");
        await Check.Status(await bsec.PutAsync($"/api/v1/meetings/{_boardMeeting}/attendance", new
        { records = new[] { new { userId = _u["bchair"], status = "present", mode = "in_person" }, new { userId = _u["bm1"], status = "present", mode = "in_person" } } }), 200, "حضور");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/open", new { }), 200, "افتتاح");
        _boardMinutes = (await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/minutes"), 201, "محضر المجلس")).G("id");
        var rb = (await Check.Status(await bsec.PostAsync($"/api/v1/minutes/{_boardMinutes}/resolutions", new
        { resolutionType = "decision", titleAr = "اعتماد سياسة تصنيف البيانات", textAr = "قرر المجلس اعتماد سياسة تصنيف البيانات.", agendaItemId = item, adoptionMethod = "consensus" }), 201, "قرار المجلس")).G("id");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{_boardMeeting}/close"), 200, "إغلاق");
        var s = await Check.Status(await bsec.PostAsync($"/api/v1/minutes/{_boardMinutes}/submit", new { }), 200, "رفع");
        Check.Equal("pending_final_approval", s.S("status"), "لا مراجعات ولا توقيع في سياسة هذه الجهة");
        await Check.Problem(await (await As("bchair")).PostAsync($"/api/v1/minutes/{_boardMinutes}/approve", new { contentHash = s.S("contentHash") }), 409, "external-approval-required", "السياسة تقتضي الاعتماد الخارجي");
        await Check.Problem(await bsec.PostAsync($"/api/v1/minutes/{_boardMinutes}/record-approval", new
        { contentHash = s.S("contentHash"), approverName = "الرئيس التنفيذي", referenceNo = "خ/1448/77", approvedOn = Today, documentId = Guid.NewGuid() }), 400, "validation", "الدليل يجب أن يكون مرفقًا بالمحضر");
        var doc = (await Check.Status(await Upload(bsec, "minutes", _boardMinutes, "approval-letter.pdf", "approval_evidence"), 201, "خطاب الاعتماد")).G("id");
        var r = await Check.Status(await bsec.PostAsync($"/api/v1/minutes/{_boardMinutes}/record-approval", new
        { contentHash = s.S("contentHash"), approverName = "د. الرئيس التنفيذي", approverTitle = "الرئيس التنفيذي", referenceNo = "خ/1448/77", approvedOn = Today, documentId = doc, note = "اعتُمد بخطاب" }), 200, "تسجيل الاعتماد");
        Check.Equal($"P3-BOARD/{Year}/001", r!["issued"]!.AsArray().Single()!.GetValue<string>(), "قرار المجلس صدر");
        var a = (await bsec.GetJsonAsync($"/api/v1/minutes/{_boardMinutes}"))["approvals"]!.AsArray().Last(x => x.S("decision") == "approved");
        Check.That(a.S("method") == "recorded_external" && a.S("externalApprover").Contains("الرئيس التنفيذي") && a.S("externalReference") == "خ/1448/77" && a.G("evidenceDocumentId") == doc,
            "موثق: من اعتمد، وكيف، وبأي مرجع ودليل");

        // بتّ الجهة الأعلى في التوصية المرفوعة
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync($"/api/v1/resolutions/{_r["R3"]}/transition", new { action = "record_outcome", reason = "اعتمده المجلس" }), 400, "validation", "البت يحتاج نتيجة");
        await Check.Status(await sec.PostAsync($"/api/v1/resolutions/{_r["R3"]}/transition", new { action = "record_outcome", outcome = "adopted", reason = $"اعتمدها المجلس التوجيهي بالقرار P3-BOARD/{Year}/001", reference = $"P3-BOARD/{Year}/001" }), 200, "تسجيل بت الجهة الأعلى");
        var r3 = (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R3"]}"))["resolution"]!;
        Check.That(r3.S("status") == "closed" && r3.S("referralOutcome") == "adopted", "التوصية أُغلقت باعتمادها");
        _ = rb;
    }

    [Test("P3-10", "سجل القرار المستقل: المصدر والنص والتصويت والمهام وحالة التنفيذ وتاريخ الحالات، بانتقالات محكومة", 10)]
    public async Task ResolutionRegister()
    {
        var sec = await As("sec");
        var url = $"/api/v1/resolutions/{_r["R1"]}/transition";
        await Check.Problem(await (await As("m1")).PostAsync(url, new { action = "start_execution" }), 403, "forbidden-action", "العضو لا يغير حالة القرار");
        await Check.Problem(await sec.PostAsync(url, new { action = "close" }), 409, "invalid-transition", "لا إغلاق قبل التنفيذ");
        await Check.Status(await sec.PostAsync(url, new { action = "start_execution" }), 200, "بدء التنفيذ");
        await Check.Problem(await sec.PostAsync(url, new { action = "mark_executed" }), 400, "validation", "التنفيذ يحتاج إثباتًا");
        await Check.Status(await sec.PostAsync(url, new { action = "mark_executed", reason = "اعتُمدت الخطة التنفيذية" }), 200, "منفذ");
        await Check.Problem(await sec.PostAsync($"/api/v1/resolutions/{_r["R5"]}/transition", new { action = "cancel", reason = "تغير الأولويات" }), 400, "validation", "الإلغاء يحتاج مرجعًا");
        await Check.Status(await sec.PostAsync($"/api/v1/resolutions/{_r["R5"]}/transition", new { action = "cancel", reason = "تغير الأولويات", reference = "محضر لاحق" }), 200, "إلغاء");

        var d = await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R1"]}");
        var res = d["resolution"]!;
        Check.That(res.S("bodyName").Contains("الحوكمة") && res.S("meetingReference").StartsWith("P3-COMM-") && res.S("agendaItemTitle").Contains("خطة"), "المصدر: الجهة والاجتماع والبند");
        Check.That(res.S("category") == "policy" && res.S("authorityText") == "رئيس اللجنة" && res["issuedAt"] is not null && res["approvedAt"] is not null, "النوع وصاحب الصلاحية والتواريخ");
        Check.Equal(1, d["tasks"]!.AsArray().Count, "المهام المرتبطة");
        Check.That(d["history"]!.AsArray().Select(h => h.S("toStatus")).SequenceEqual(["proposed", "issued", "in_execution", "executed"]), "تاريخ الحالات");
        Check.That(d["reservations"]!.AsArray().Any(r => r.S("text").Contains("الجدول الزمني")), "التحفظ مرتبط بالقرار");
        var r2 = await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R7"]}");
        Check.Equal(_r["R2"], r2["resolution"]!.G("supersedesResolutionId"), "القرار الذي يلغيه");
        Check.Equal(_r["R7"], (await sec.GetJsonAsync($"/api/v1/resolutions/{_r["R2"]}"))["supersededBy"]!.AsArray()[0]!.G("id"), "والعكس");
        var issued = (await sec.GetJsonAsync($"/api/v1/resolutions?bodyId={_comm}&status=superseded")).Items();
        Check.That(issued.Count == 1 && issued[0].G("id") == _r["R2"], "التصفية بالحالة");
        await Check.Problem(await sec.PatchAsync($"/api/v1/resolutions/{_r["R1"]}", new { titleAr = "تعديل بعد الإصدار" }), 409, "resolution-issued", "الصادر لا يُعدل");
        // المدقق المخول يرى الأصوات الفردية؟ لا: ليس عضوًا في الجهة، فلا يرى سجلاتها
        await Check.Problem(await (await As("auditor")).GetAsync($"/api/v1/resolutions/{_r["R1"]}"), 404, "not-found", "المدقق بلا نطاق على الجهة");
    }

    [Test("P3-11", "النظام يعمل كاملًا والذكاء الاصطناعي والتوقيع معطلان؛ وتفعيل التوقيع قبل ربطه لا يعطل الدورة", 11)]
    public async Task OptionalModulesDisabled()
    {
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutes}/summary"), 404, "feature-disabled", "الذكاء الاصطناعي مرفوض من الخادم");
        var admin = ctx.AdminClient!;
        // تفعيل الوحدة بنوع يتطلب مزوّدًا معتمدًا لم يُربط بعد: يعمل البديل ويوثّق السبب (المزوّد المعتمد غير منفذ — Q-09)
        var flags = await admin.GetJsonAsync("/api/v1/features");
        var provider = flags["E_SIGNATURE_PROVIDER"]!["value"]!.GetValue<string>();
        Check.Equal("in_app_approval", flags["E_SIGNATURE_METHOD"]!["value"]!.GetValue<string>(), "النوع الافتراضي: موافقة موثقة داخل النظام");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_METHOD", new { value = "pdf_digital_signature", reason = "تجربة نوع يتطلب مزوّدًا", confirmImpact = true }), 200, "النوع");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_PROVIDER", new { value = "national_provider", reason = "مزوّد معتمد غير مربوط", confirmImpact = true }), 200, "المزوّد");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "true", reason = "تجربة التفعيل قبل ربط المزود" }), 200, "تفعيل التوقيع");
        var m = await (await As("bsec")).GetJsonAsync($"/api/v1/minutes/{_boardMinutes}");
        Check.Equal("esign_provider_not_integrated", m["signatureMethod"]!.S("reason"), "مفعّل والمزوّد غير مربوط: يعمل البديل ويوثّق السبب");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "false", reason = "إعادة التعطيل بعد التجربة", confirmImpact = true }), 200, "إعادة التعطيل");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_PROVIDER", new { value = provider, reason = "إرجاع المزوّد", confirmImpact = true }), 200, "إرجاع المزوّد");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_METHOD", new { value = "in_app_approval", reason = "إرجاع النوع الافتراضي", confirmImpact = true }), 200, "إرجاع النوع");
        // إعادة الذكاء الاصطناعي كما كان قبل المرحلة 3 (تختبر الاستمرارية بعد إعادة التشغيل)
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "إعادة الحالة بعد اختبارات المرحلة 3" }), 200, "إعادة AI");
        var activity = (await sec.GetJsonAsync($"/api/v1/minutes/{_minutes}/activity")).AsArray();
        foreach (var action in new[] { "minutes.created", "minutes.submitted", "minutes.returned", "minutes.signed_off", "minutes.approved", "minutes.amendment_opened" })
            Check.That(activity.Any(x => x.S("action") == action), "السجل الزمني: " + action);
    }

    [Test("P3-12", "التصويت بالتمرير حين تجيزه السياسة: كل الأعضاء المصوتين مؤهلون، ويُغلق آليًا بالإجماع المطلوب", 12)]
    public async Task CirculationVoting()
    {
        var circ = await Body("P3-CIRC", "permanent_committee", "لجنة البيانات المفتوحة",
            [("bchair", "chair"), ("bsec", "secretary"), ("bm1", "member"), ("m1", "member")],
            Policy(chairRequired: false, signatures: "none", chairReview: false, memberReview: false, finalApproval: "chair", threshold: "unanimity", circulation: true));
        var bsec = await As("bsec");
        var mt = (await Check.Status(await bsec.PostAsync("/api/v1/meetings", new
        { bodyId = circ, titleAr = "تمرير اعتماد دليل البيانات", meetingType = "circulation", startsAt = DateTime.UtcNow.AddMinutes(5), circulationDeadline = DateTime.UtcNow.AddDays(3) }), 201, "اجتماع تمرير")).G("id");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/schedule"), 200, "جدولة");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/agenda-items", new { titleAr = "اعتماد دليل البيانات المفتوحة" }), 201, "بند");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/invitations/send"), 200, "إرسال");
        var mn = (await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/minutes"), 201, "محضر التمرير")).G("id");
        var res = (await Check.Status(await bsec.PostAsync($"/api/v1/minutes/{mn}/resolutions", new { resolutionType = "decision", titleAr = "اعتماد الدليل", textAr = "يعتمد الفريق دليل البيانات المفتوحة." }), 201, "قرار")).G("id");
        var mo = await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/motions", new { resolutionId = res, method = "electronic" }), 201, "فتح التمرير");
        var motions = (await bsec.GetJsonAsync($"/api/v1/meetings/{mt}/motions"))["items"]!.AsArray();
        Check.That(motions[0]!.S("method") == "circulation" && motions[0]!["eligibleCount"]!.GetValue<int>() == 3 && motions[0]!["deadlineAt"] is not null, "التمرير: كل الأعضاء المصوتين ومهلة الاجتماع");
        await Check.Status(await (await As("bchair")).PostAsync($"/api/v1/motions/{mo.G("id")}/votes", new { choice = "for" }), 200, "صوت الرئيس");
        await Check.Status(await (await As("bm1")).PostAsync($"/api/v1/motions/{mo.G("id")}/votes", new { choice = "for" }), 200, "صوت bm1");
        var last = await Check.Status(await (await As("m1")).PostAsync($"/api/v1/motions/{mo.G("id")}/votes", new { choice = "for" }), 200, "صوت m1");
        Check.That(last!["closed"]!.GetValue<bool>() && last.S("result") == "adopted", "الإجماع بعد تصويت الجميع");
        await Check.Status(await bsec.PostAsync($"/api/v1/meetings/{mt}/close"), 200, "إغلاق التمرير");
        var s = await Check.Status(await bsec.PostAsync($"/api/v1/minutes/{mn}/submit", new { }), 200, "رفع محضر التمرير");
        var r = await Check.Status(await (await As("bchair")).PostAsync($"/api/v1/minutes/{mn}/approve", new { contentHash = s.S("contentHash") }), 200, "اعتماد");
        Check.Equal($"P3-CIRC/{Year}/001", r!["issued"]!.AsArray().Single()!.GetValue<string>(), "قرار التمرير صدر");
        Check.Equal("circulation", (await bsec.GetJsonAsync($"/api/v1/resolutions/{res}"))["resolution"].S("adoptionMethod"), "طريقة الاعتماد: بالتمرير");
    }
}
