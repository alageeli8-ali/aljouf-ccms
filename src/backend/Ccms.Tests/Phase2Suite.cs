using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ccms.Contracts;
using Ccms.Modules.Meetings;

namespace Ccms.Tests;

/// <summary>سيناريوهات قبول المرحلة 2 (المجالس واللجان وفرق العمل والاجتماعات) — طرفًا لطرف عبر HTTP على قاعدة PostgreSQL حقيقية.
/// تعمل بالترتيب: كل سيناريو يبني على حالة سابقه كما في دورة العمل الفعلية.</summary>
public sealed class Phase2Suite(TestContext ctx)
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private Guid _committee, _temp, _sub, _joint, _other, _partner;
    private readonly Dictionary<string, Guid> _members = [];
    private Guid _m1, _m2, _m3;
    private readonly Guid[] _items = new Guid[4];
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p2-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private async Task<JsonNode> Json(ApiClient c, string url) => await c.GetJsonAsync(url);

    private async Task<JsonArray> Members(Guid body, bool includeEnded = false) =>
        (await Json(await As("gov"), $"/api/v1/bodies/{body}/members?includeEnded={includeEnded.ToString().ToLowerInvariant()}")).Items();

    private object Policy(string quorumType = "majority", bool chairRequired = true, bool allowProxy = true, bool allowCirculation = false, bool allowOverride = false,
        int? num = null, int? den = null, int? fixedCount = null, string reason = "ضبط سياسة الاختبار") => new
    {
        effectiveFrom = Today, quorumType, quorumNumerator = num, quorumDenominator = den, quorumFixedCount = fixedCount, chairRequired, countRemote = true, allowOverride,
        votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = true,
        minutesReviewDays = 5, extensionApprover = "secretary", allowProxy, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation, reason,
    };

    // ============================ التهيئة ============================

    [Test("P2-SETUP", "تهيئة المرحلة 2: مستخدمون وجهة خارجية شريكة", 0)]
    public async Task Setup()
    {
        var admin = ctx.AdminClient!;
        foreach (var (k, roles) in new (string, string[])[]
        {
            ("gov", ["governance_officer"]), ("chair", []), ("vice", []), ("sec", []), ("m1", []), ("m2", []), ("m3", []), ("obs", []), ("alt", []),
            ("chair2", []), ("guest", []), ("outsider", []), ("othersec", []), ("otherchair", []),
        })
        {
            var r = await admin.PostAsync("/api/v1/users", new { email = $"p2-{k}@test.jsdo.gov.sa", displayNameAr = "م2 " + k, systemRoles = roles, initialPassword = Pw });
            _u[k] = (await Check.Status(r, 201, "مستخدم " + k)).G("id");
        }
        _partner = (await Check.Status(await (await As("gov")).PostAsync("/api/v1/external-entities", new { nameAr = "هيئة شريكة (اختبار)", liaisonUserId = _u["sec"] }), 201, "جهة شريكة")).G("id");
    }

    // ============================ أ. الجهات ============================

    [Test("P2-01", "إنشاء لجنة دائمة برقم مرجعي فريد وكل حقول التشكيل، والتحقق من قواعد الأنواع (مؤقتة/فرعية/مشتركة)", 1)]
    public async Task CreateCommittee()
    {
        var gov = await As("gov");
        var c = await Check.Status(await gov.PostAsync("/api/v1/bodies", new
        {
            code = "P2-DTC", bodyType = "permanent_committee", nameAr = "لجنة التحول الرقمي", nameEn = "Digital Transformation Committee",
            objectiveAr = "تسريع التحول الرقمي", mandateAr = "الإشراف على المبادرات الرقمية", authorityRef = "المجلس التنفيذي",
            formationDecisionNo = "ق/12/1448", formationDecisionDate = Today.AddDays(-10), termStart = Today.AddDays(-10), meetingFrequency = "monthly", classification = "restricted",
        }), 201, "إنشاء اللجنة");
        _committee = c.G("id");
        Check.That(System.Text.RegularExpressions.Regex.IsMatch(c.S("referenceNo"), @"^BOD-\d{4}-\d{4}$"), "رقم مرجعي بالصيغة BOD-سنة-تسلسل: " + c.S("referenceNo"));
        Check.Equal("draft", c.S("status"), "تبدأ مسودة");

        await Check.Problem(await gov.PostAsync("/api/v1/bodies", new { code = "P2-TMP", bodyType = "temporary_committee", nameAr = "مؤقتة", nameEn = "Temp" }), 400, "validation", "المؤقتة بلا انتهاء");
        _temp = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new
        { code = "P2-TMP", bodyType = "temporary_committee", nameAr = "لجنة مؤقتة", nameEn = "Temp Committee", termStart = Today, termEnd = Today.AddDays(90) }), 201, "مؤقتة")).G("id");
        await Check.Problem(await gov.PostAsync("/api/v1/bodies", new { code = "P2-SUB", bodyType = "sub_committee", nameAr = "فرعية", nameEn = "Sub" }), 400, "validation", "فرعية بلا أم");
        _sub = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new
        { code = "P2-SUB", bodyType = "sub_committee", nameAr = "لجنة فرعية للبيانات", nameEn = "Data Sub-committee", parentBodyId = _committee }), 201, "فرعية")).G("id");
        await Check.Problem(await gov.PostAsync("/api/v1/bodies", new { code = "P2-JNT", bodyType = "joint_committee", nameAr = "مشتركة", nameEn = "Joint" }), 400, "validation", "مشتركة بلا شريك");
        _joint = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new
        { code = "P2-JNT", bodyType = "joint_committee", nameAr = "لجنة مشتركة", nameEn = "Joint Committee", partnerEntityIds = new[] { _partner } }), 201, "مشتركة")).G("id");
        _other = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P2-OTH", bodyType = "steering_council", nameAr = "المجلس التوجيهي", nameEn = "Steering Council" }), 201, "مجلس")).G("id");

        var detail = await Json(gov, $"/api/v1/bodies/{_committee}");
        Check.That(detail["children"]!.AsArray().Any(x => x!.G("id") == _sub), "اللجنة الفرعية مرتبطة بالأم");
        var jd = await Json(gov, $"/api/v1/bodies/{_joint}");
        Check.That(jd["partners"]!.AsArray().Count == 1, "الشريك محفوظ للجنة المشتركة");
        await Check.Problem(await (await As("outsider")).PostAsync("/api/v1/bodies", new { code = "P2-X", bodyType = "working_team", nameAr = "س", nameEn = "X", termEnd = Today.AddDays(5) }), 403, "forbidden-action", "موظف لا يُنشئ جهات");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_sub}/activate"), 409, "formation-incomplete", "الفرعية لا تُفعّل قبل الأم واستكمال التشكيل");
    }

    [Test("P2-02", "إضافة الأعضاء بصفاتهم وحق التصويت وعضو بديل، ورفض التكرار ورئيس ثانٍ، ثم تفعيل الجهة", 2)]
    public async Task AddMembers()
    {
        var gov = await As("gov");
        foreach (var (k, role, voting) in new[] { ("chair", "chair", true), ("vice", "vice_chair", true), ("sec", "secretary", false), ("m1", "member", true), ("m2", "member", true), ("m3", "member", true), ("obs", "observer", false) })
            _members[k] = (await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u[k], role, isVoting = voting, appointmentRef = "ق/12/1448" }), 201, "عضوية " + k)).G("id");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["alt"], role = "alternate" }), 400, "validation", "بديل بلا أصيل");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["alt"], role = "alternate", alternateForMemberId = _members["sec"] }), 400, "validation", "بديل لأمين السر (غير مصوت)");
        _members["alt"] = (await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["alt"], role = "alternate", alternateForMemberId = _members["m1"] }), 201, "بديل عن m1")).G("id");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["m1"], role = "member" }), 409, "duplicate", "عضوية مكررة");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["chair2"], role = "chair" }), 409, "chair-exists", "رئيس ثانٍ");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["obs"], role = "secretary", isVoting = true }), 400, "validation", "أمين سر مصوت");

        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/activate"), 200, "تفعيل اللجنة");
        var members = await Members(_committee);
        Check.Equal(8, members.Count, "ثمانية أعضاء نشطين");
        var alt = members.First(m => m!.G("id") == _members["alt"])!;
        Check.That(alt.S("alternateForName").Contains("m1") && !alt["isVoting"]!.GetValue<bool>(), "البديل مربوط بأصيله وغير مصوت");
        Check.That(members.All(m => m!["email"] is not null && m["displayNameAr"] is not null), "بيانات التواصل المؤسسية فقط");

        // العضو يرى جهته؛ الموظف من خارجها لا يراها (404)
        await Check.Status(await (await As("m1")).GetAsync($"/api/v1/bodies/{_committee}"), 200, "العضو يطّلع");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/bodies/{_committee}"), 404, "not-found", "الموظف لا يرى الجهة");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/bodies/{_committee}/members", new { userId = _u["chair2"], role = "member" }), 403, "forbidden-action", "العضو لا يضيف أعضاء");

        // الجهة الأخرى (للإحالة) والفرعية
        foreach (var (b, k, role) in new[] { (_other, "otherchair", "chair"), (_other, "othersec", "secretary") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{b}/members", new { userId = _u[k], role }), 201, "عضوية " + k);
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_other}/activate"), 200, "تفعيل المجلس");
    }

    [Test("P2-03", "تغيير صفة عضوية دون فقد السجل: السجل القديم يُنهى بسببه ويرتبط بالجديد، والتاريخ كاملًا محفوظ", 3)]
    public async Task ChangeMembership()
    {
        var gov = await As("gov");
        var r = await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/members/{_members["m3"]}/change",
            new { role = "observer", isVoting = false, reason = "تغيير الصفة بقرار الرئيس" }), 200, "تغيير صفة m3");
        var newId = r.G("id");
        Check.Equal(_members["m3"], r.G("previousId"), "يعيد السجل السابق");
        var all = await Members(_committee, includeEnded: true);
        var old = all.First(m => m!.G("id") == _members["m3"])!;
        Check.Equal("ended", old.S("status"), "السجل القديم منتهٍ");
        Check.Equal("member", old.S("role"), "السجل القديم يحتفظ بالصفة السابقة");
        Check.That(old.S("endReason").Contains("تغيير الصفة"), "سبب الإنهاء محفوظ");
        Check.Equal(newId, old.G("replacedByMemberId"), "السجل القديم يشير إلى الجديد");
        var cur = all.First(m => m!.G("id") == newId)!;
        Check.That(cur.S("role") == "observer" && cur.S("status") == "active", "السجل الجديد نشط بالصفة الجديدة");
        Check.That((await Members(_committee)).All(m => m!.G("id") != _members["m3"]), "القائمة الحالية تُخفي المنتهي");
        _members["m3"] = newId;
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_committee}/members/{_members["chair"]}/change", new { role = "member", reason = "تجربة" }), 409, "use-change-chair", "الرئيس يُغيّر بإجراء مستقل");

        // إيقاف ثم إعادة تفعيل عضوية، والسجل العام للعضويات
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/members/{_members["m2"]}/suspend", new { reason = "إجازة طويلة" }), 204, "إيقاف m2");
        Check.Equal("suspended", (await Members(_committee)).First(m => m!.G("id") == _members["m2"])!.S("status"), "موقوف");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/members/{_members["m2"]}/reactivate", new { reason = "العودة من الإجازة" }), 204, "إعادة m2");
        var global = await Json(gov, $"/api/v1/memberships?userId={_u["m3"]}&includeEnded=true");
        Check.That(global.Items().Count >= 2, "سجل العضويات العام يُظهر الصفتين للمستخدم");

        var activity = (await Json(gov, $"/api/v1/bodies/{_committee}/activity")).AsArray();
        Check.That(activity.Any(a => a!.S("action") == "body.member_changed"), "الخط الزمني يتضمن تغيير العضوية");
        Check.That(activity.Any(a => a!.S("action") == "body.activated"), "الخط الزمني يتضمن التفعيل");
    }

    [Test("P2-04", "تجديد مدة الجهة وتمديدها بسند، مع حفظ سجل المدد السابقة", 4)]
    public async Task RenewExtend()
    {
        var gov = await As("gov");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/extend", new { termEnd = Today.AddDays(150), decisionNo = "ق/20", reason = "تمديد لاستكمال الأعمال" }), 200, "تمديد");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_temp}/extend", new { termEnd = Today.AddDays(30), reason = "تقصير" }), 400, "validation", "التمديد لتاريخ أقدم");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/renew", new { termStart = Today.AddDays(151), termEnd = Today.AddDays(400), decisionNo = "ق/21", reason = "تجديد دورة" }), 200, "تجديد");
        var d = await Json(gov, $"/api/v1/bodies/{_temp}");
        var terms = d["terms"]!.AsArray();
        Check.Equal(3, terms.Count, "ثلاث مدد: أولية وتمديد وتجديد");
        Check.That(terms.Any(t => t!.S("changeType") == "extension") && terms.Any(t => t!.S("changeType") == "renewal"), "أنواع التغيير محفوظة");
    }

    [Test("P2-05", "سياسة الجهة بإصدارات: النصاب والتصويت والإنابة والحضور عن بعد والتمرير", 5)]
    public async Task PolicyVersions()
    {
        var gov = await As("gov");
        await Check.Problem(await (await As("sec")).PutAsync($"/api/v1/bodies/{_committee}/policy", Policy()), 403, "forbidden-action", "أمين السر لا يعدّل السياسة");
        var v = await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_committee}/policy", Policy()), 200, "سياسة v2");
        Check.Equal(2, v!["version"]!.GetValue<int>(), "إصدار جديد لا تعديل فوق القديم");
        var p = await Json(gov, $"/api/v1/bodies/{_committee}/policy");
        Check.Equal(2, p["versions"]!.AsArray().Count, "الإصداران محفوظان");
        Check.That(p["current"]!["allowProxy"]!.GetValue<bool>() && !p["current"]!["allowCirculation"]!.GetValue<bool>(), "السياسة السارية هي الأحدث");
    }

    // ============================ ج. الاجتماعات ============================

    [Test("P2-06", "إنشاء اجتماع (مختلط بمكان ورابط) وجدولته برقم، ورفض التمرير حين لا تجيزه السياسة، وظهوره في التقويم", 6)]
    public async Task CreateMeeting()
    {
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync("/api/v1/meetings", new { bodyId = _committee, titleAr = "س", mode = "remote", startsAt = DateTime.UtcNow.AddDays(2) }), 400, "validation", "عن بعد بلا رابط");
        await Check.Problem(await sec.PostAsync("/api/v1/meetings", new
        { bodyId = _committee, titleAr = "تمرير", meetingType = "circulation", startsAt = DateTime.UtcNow.AddDays(1), circulationDeadline = DateTime.UtcNow.AddDays(4) }), 409, "circulation-not-allowed", "التمرير غير مجاز");
        var m = await Check.Status(await sec.PostAsync("/api/v1/meetings", new
        {
            bodyId = _committee, titleAr = "الاجتماع الأول", meetingType = "regular", mode = "hybrid", startsAt = DateTime.UtcNow.AddDays(2), durationMinutes = 90,
            location = "قاعة المجلس", onlineLink = "https://meet.example.gov.sa/p2-1",
        }), 201, "اجتماع 1");
        _m1 = m.G("id");
        Check.That(m.S("chairName").Contains("chair") && m.S("secretaryName").Contains("sec"), "الرئيس وأمين السر من تشكيل الجهة");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/schedule"), 200, "جدولة");
        Check.That(s.S("referenceNo").StartsWith("P2-DTC-"), "ترقيم الاجتماع برمز الجهة");
        await Check.Problem(await (await As("m1")).PostAsync("/api/v1/meetings", new { bodyId = _committee, titleAr = "x", startsAt = DateTime.UtcNow.AddDays(2), location = "ق" }), 403, "forbidden-action", "العضو لا يُنشئ اجتماعًا");

        _m2 = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new
        { bodyId = _committee, titleAr = "الاجتماع الثاني", startsAt = DateTime.UtcNow.AddDays(20), location = "قاعة المجلس" }), 201, "اجتماع 2")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/schedule"), 200, "جدولة 2");
        var m2 = await Json(sec, $"/api/v1/meetings/{_m2}");
        Check.Equal(_m1, m2["meeting"]!.G("previousMeetingId"), "الاجتماع الثاني مرتبط بالسابق تلقائيًا");

        var cal = (await Json(sec, $"/api/v1/meetings/calendar?from={DateTime.UtcNow:O}&to={DateTime.UtcNow.AddDays(30):O}")).AsArray();
        Check.That(cal.Any(x => x!.G("id") == _m1) && cal.Any(x => x!.G("id") == _m2), "الاجتماعان في التقويم الموحد");
        var outCal = (await Json(await As("outsider"), $"/api/v1/meetings/calendar?from={DateTime.UtcNow:O}&to={DateTime.UtcNow.AddDays(30):O}")).AsArray();
        Check.That(outCal.All(x => x!.G("id") != _m1), "التقويم لا يُظهر اجتماعات جهات لا يتبعها المستخدم");
    }

    [Test("P2-07", "إعداد جدول الأعمال: بنود بمقدمها والجهة المالكة والوقت، وتعديلها وإعادة ترتيبها، ورفض الإرسال بجدول فارغ", 7)]
    public async Task PrepareAgenda()
    {
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m2}/invitations/send"), 409, "agenda-empty", "جدول فارغ");
        var titles = new[] { "اعتماد خطة التحول الرقمي", "مراجعة مؤشرات الأداء", "طلب دعم من المجلس التوجيهي", "ما يستجد من أعمال" };
        for (var i = 0; i < 4; i++)
            _items[i] = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/agenda-items", new
            {
                titleAr = titles[i], descriptionAr = "وصف " + titles[i], itemType = i == 1 ? "for_information" : "for_decision", presenterText = "مدير الإدارة",
                ownerText = "الإدارة العامة لتقنية المعلومات", estimatedMinutes = 20,
            }), 201, "بند " + (i + 1))).G("id");
        await Check.Status(await sec.PatchAsync($"/api/v1/agenda-items/{_items[1]}", new { estimatedMinutes = 10, presenterText = "مدير الأداء" }), 200, "تعديل بند");
        await Check.Problem(await sec.PatchAsync($"/api/v1/agenda-items/{_items[0]}", new { discussionSummary = "مبكر" }), 409, "invalid-transition", "لا ملخص نقاش قبل الانعقاد");
        await Check.Problem(await sec.PutAsync($"/api/v1/meetings/{_m1}/agenda-items/order", new { itemIds = new[] { _items[0], _items[1] } }), 400, "validation", "ترتيب ناقص");
        var order = new[] { _items[1], _items[0], _items[2], _items[3] };
        var ag = (await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_m1}/agenda-items/order", new { itemIds = order }), 200, "إعادة الترتيب"))!.AsArray();
        Check.That(ag.Select(x => x!.G("id")).SequenceEqual(order), "الترتيب الجديد محفوظ");
        var item = await Json(sec, $"/api/v1/agenda-items/{_items[1]}");
        Check.That(item["item"]!["estimatedMinutes"]!.GetValue<int>() == 10 && item["item"]!.S("presenterText") == "مدير الأداء", "التعديل محفوظ");
        await Check.Problem(await (await As("obs")).PostAsync($"/api/v1/meetings/{_m1}/agenda-items", new { titleAr = "بند" }), 403, "forbidden-action", "المراقب لا يضيف بنودًا");

        // مرفق البند: يرفعه أمين السر، ويطّلع عليه العضو، ولا يراه غير العضو
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4 agenda memo"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "kpi-memo.pdf");
        content.Add(new StringContent("agenda_item"), "ownerType");
        content.Add(new StringContent(_items[1].ToString()), "ownerId");
        content.Add(new StringContent("agenda_memo"), "purpose");
        var doc = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", content), 201, "مرفق البند")).G("id");
        var docs = (await Json(await As("m1"), $"/api/v1/documents?ownerType=agenda_item&ownerId={_items[1]}")).AsArray();
        Check.That(docs.Count == 1 && docs[0]!.S("purpose") == "agenda_memo", "العضو يرى مرفق البند");
        await Check.Status(await (await As("m1")).GetAsync($"/api/v1/documents/{doc}/content"), 200, "العضو ينزّل المرفق");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/documents/{doc}/content"), 404, "not-found", "غير العضو لا ينزّل");
    }

    [Test("P2-08", "إرسال الدعوات لكل الأعضاء بمن فيهم البديل، وضيف لبند بعينه، وتأكيد الحضور، والتذكير", 8)]
    public async Task SendInvitations()
    {
        var sec = await As("sec");
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/meetings/{_m1}/invitations/send"), 403, "forbidden-action", "العضو لا يرسل الدعوات");
        await Check.Problem(await (await As("guest")).GetAsync($"/api/v1/meetings/{_m1}"), 404, "not-found", "الضيف قبل دعوته لا يرى الاجتماع");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/guests", new { userId = _u["guest"], agendaItemId = _items[2] }), 200, "إضافة ضيف");
        var r = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/invitations/send"), 200, "إرسال الدعوات");
        Check.Equal(9, r!["sent"]!.GetValue<int>(), "8 أعضاء + ضيف");
        Check.Equal("agenda_published", (await Json(sec, $"/api/v1/meetings/{_m1}"))["meeting"]!.S("status"), "الحالة: الدعوات مرسلة");
        var again = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/invitations/send"), 200, "إعادة الإرسال");
        Check.Equal(0, again!["sent"]!.GetValue<int>(), "لا دعوات مكررة");

        await Check.Status(await (await As("guest")).GetAsync($"/api/v1/meetings/{_m1}"), 200, "الضيف المدعو يطّلع على الاجتماع");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/meetings/{_m1}"), 404, "not-found", "غير المدعو لا يرى");
        var notes = await Json(await As("m1"), "/api/v1/notifications");
        Check.That(notes.Items().Any(n => n!.S("eventType") == "meeting_invitation"), "إشعار الدعوة وصل للعضو");

        await Check.Status(await (await As("m1")).PostAsync($"/api/v1/meetings/{_m1}/rsvp", new { response = "declined", note = "مهمة رسمية" }), 200, "اعتذار m1");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/meetings/{_m1}/rsvp", new { response = "accepted" }), 200, "تأكيد الرئيس");
        await Check.Status(await (await As("alt")).PostAsync($"/api/v1/meetings/{_m1}/rsvp", new { response = "accepted" }), 200, "تأكيد البديل");
        await Check.Problem(await (await As("outsider")).PostAsync($"/api/v1/meetings/{_m1}/rsvp", new { response = "accepted" }), 404, "not-found", "غير المدعو لا يرد");
        var rem = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/invitations/remind"), 200, "تذكير");
        Check.Equal(6, rem!["reminded"]!.GetValue<int>(), "التذكير لمن لم يرد فقط");

        var inv = (await Json(sec, $"/api/v1/meetings/{_m1}/invitations")).AsArray();
        var m1 = inv.First(x => x!.G("userId") == _u["m1"])!;
        Check.That(m1.S("response") == "declined" && m1.S("responseNote") == "مهمة رسمية", "الرد وسببه محفوظان");
        var g = inv.First(x => x!.G("userId") == _u["guest"])!;
        Check.That(g.S("role") == "guest" && g.G("agendaItemId") == _items[2], "الضيف مرتبط ببنده");

        var forecast = await Json(sec, $"/api/v1/meetings/{_m1}/quorum");
        Check.Equal("forecast", forecast.S("mode"), "قبل الانعقاد: توقع النصاب من الردود");
        Check.Equal(2, forecast["result"]!["counted"]!.GetValue<int>(), "المتوقع: الرئيس + البديل عن m1");
    }

    [Test("P2-09", "تسجيل الحضور والاعتذار والغياب والإنابة، والتحقق من النصاب قبل الافتتاح ومنعه دون نصاب", 9)]
    public async Task AttendanceAndQuorum()
    {
        var sec = await As("sec");
        // المصوتون الأصلاء: الرئيس، النائب، m1، m2 (m3 أصبح مراقبًا) => N = 4 والأغلبية 3
        await Check.Problem(await (await As("m2")).PutAsync($"/api/v1/meetings/{_m1}/attendance", new { records = new[] { new { userId = _u["m2"], status = "present", mode = "in_person" } } }),
            403, "forbidden-action", "العضو لا يسجل الحضور");
        var q = await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new
        {
            records = new object[]
            {
                new { userId = _u["chair"], status = "present", mode = "in_person" },
                new { userId = _u["m1"], status = "excused", note = "مهمة رسمية" },
                new { userId = _u["m2"], status = "absent" },
                new { userId = _u["sec"], status = "present", mode = "in_person" },
            },
        }), 200, "حضور جزئي");
        Check.That(!q!["quorum"]!["isMet"]!.GetValue<bool>() && q["quorum"]!["counted"]!.GetValue<int>() == 1 && q["quorum"]!["required"]!.GetValue<int>() == 3, "النصاب غير مكتمل 1/3");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m1}/open", new { }), 409, "quorum-not-met", "لا افتتاح دون نصاب");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m1}/open", new { overrideReason = "ظرف طارئ" }), 409, "quorum-not-met", "التجاوز غير مجاز في السياسة");

        // إنابة: لا تُقبل لغير المصوت ولا فوق الحد
        await Check.Problem(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new { records = new[] { new { userId = _u["vice"], status = "proxy", proxyHolderUserId = (Guid?)_u["sec"] } } }),
            400, "validation", "الإنابة لأمين السر مرفوضة");
        await Check.Problem(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new
        {
            records = new[] { new { userId = _u["vice"], status = "proxy", proxyHolderUserId = (Guid?)_u["chair"] }, new { userId = _u["m2"], status = "proxy", proxyHolderUserId = (Guid?)_u["chair"] } },
        }), 400, "validation", "إنابتان لعضو واحد تتجاوزان الحد");

        q = await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new
        {
            records = new object[]
            {
                new { userId = _u["vice"], status = "proxy", proxyHolderUserId = _u["chair"] },
                new { userId = _u["alt"], status = "present", mode = "in_person" },
                new { userId = _u["m2"], status = "present", mode = "remote" },
                new { userId = _u["guest"], status = "present", mode = "in_person" },
            },
        }), 200, "اكتمال الحضور");
        var res = q!["quorum"]!;
        Check.That(res["isMet"]!.GetValue<bool>() && res["counted"]!.GetValue<int>() == 4, "النصاب مكتمل: الرئيس + إنابة النائب + البديل عن m1 + m2 عن بعد");
        var lines = res["lines"]!.AsArray();
        string Reason(string k) => lines.First(l => l!.G("userId") == _u[k])!.S("reason");
        Check.Equal("proxy", Reason("vice"), "النائب محتسب بالإنابة");
        Check.Equal("substituted_by_alternate", Reason("m1"), "m1 محتسب ببديله");
        Check.Equal("present_remote", Reason("m2"), "m2 محتسب عن بعد");
        Check.That(lines.All(l => l!.G("userId") != _u["guest"]), "الضيف لا يدخل في النصاب");

        var opened = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/open", new { }), 200, "الافتتاح");
        Check.Equal("in_progress", opened.S("status"), "منعقد");
        var hist = (await Json(sec, $"/api/v1/meetings/{_m1}/quorum"))["history"]!.AsArray();
        Check.That(hist.Count >= 3 && hist.Any(h => !h!["isMet"]!.GetValue<bool>()) && hist.Any(h => h!["isMet"]!.GetValue<bool>()), "فحوص النصاب الفاشلة والناجحة محفوظة");

        // المغادرة تعيد فحص النصاب
        var d1 = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/attendance/{_u["m2"]}/depart"), 200, "مغادرة m2");
        Check.That(!d1!["quorumLost"]!.GetValue<bool>(), "النصاب باقٍ (3 من 3)");
        var d2 = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/attendance/{_u["alt"]}/depart"), 200, "مغادرة البديل");
        Check.That(d2!["quorumLost"]!.GetValue<bool>(), "فقدان النصاب يُنبه به");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new { records = new[] { new { userId = _u["alt"], status = "present", mode = "in_person" } } }), 200, "عودة البديل");
    }

    [Test("P2-10", "حاسبة النصاب: الأغلبية والنسبة والعدد الثابت واشتراط الرئيس والحضور عن بعد", 10)]
    public Task QuorumCalculatorCases()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var members = new List<QuorumMember>
        {
            new(ids[0], ids[0], "رئيس", "chair", true, null), new(ids[1], ids[1], "أ", "member", true, null), new(ids[2], ids[2], "ب", "member", true, null),
            new(ids[3], ids[3], "ج", "member", true, null), new(ids[4], ids[4], "د", "member", true, null), new(ids[5], ids[5], "س", "secretary", false, null),
        };
        Dictionary<Guid, QuorumAttendance> Att(params (int I, string Mode)[] present) => present.ToDictionary(p => ids[p.I], p => new QuorumAttendance(ids[p.I], "present", p.Mode, null, null));
        var none = new HashSet<Guid>();
        Check.Equal(3, QuorumCalculator.Required(new QuorumRule(), 5), "أغلبية 5 = 3");
        Check.Equal(4, QuorumCalculator.Required(new QuorumRule { Type = "fraction", Numerator = 2, Denominator = 3 }, 5), "ثلثا 5 = 4");
        Check.Equal(5, QuorumCalculator.Required(new QuorumRule { Type = "fixed", FixedCount = 7 }, 5), "الثابت لا يتجاوز العدد");
        var r = QuorumCalculator.Compute(new QuorumRule(), false, 1, members, Att((1, "in_person"), (2, "in_person"), (3, "in_person"), (5, "in_person")), none);
        Check.That(r.IsMet && r.Eligible == 5 && r.Counted == 3, "أغلبية دون الرئيس؛ أمين السر لا يُحتسب");
        r = QuorumCalculator.Compute(new QuorumRule { ChairRequired = true }, false, 1, members, Att((1, "in_person"), (2, "in_person"), (3, "in_person")), none);
        Check.That(!r.IsMet && !r.ChairPresent, "اشتراط حضور الرئيس");
        r = QuorumCalculator.Compute(new QuorumRule { ChairRequired = true }, false, 1, members, Att((1, "in_person"), (2, "in_person"), (3, "in_person")), new HashSet<Guid> { ids[1] });
        Check.That(r.IsMet, "المفوض بصلاحيات الرئيس يقوم مقامه");
        r = QuorumCalculator.Compute(new QuorumRule { CountRemote = false }, false, 1, members, Att((0, "in_person"), (1, "remote"), (2, "remote")), none);
        Check.That(!r.IsMet && r.Counted == 1, "الحضور عن بعد لا يُحتسب حين تمنعه السياسة");
        r = QuorumCalculator.Compute(new QuorumRule { Type = "fraction", Numerator = 2, Denominator = 3 }, false, 1, members, Att((0, "in_person"), (1, "in_person"), (2, "in_person")), none);
        Check.That(!r.IsMet && r.Required == 4, "الثلثان: 3 من 5 لا تكفي");
        return Task.CompletedTask;
    }

    [Test("P2-11", "مناقشة البنود: ملخص وتوصية، وتأجيل بند إلى اجتماع لاحق، وإحالة بند لجهة أخرى، والإغلاق يؤجل غير المنظور", 11)]
    public async Task DiscussDeferRefer()
    {
        var sec = await As("sec");
        await Check.Status(await sec.PatchAsync($"/api/v1/agenda-items/{_items[0]}", new
        { discussionSummary = "نوقشت الخطة واستُعرضت مراحلها", recommendationText = "التوصية باعتماد الخطة", status = "discussed" }), 200, "مناقشة البند 1");
        await Check.Problem(await sec.PostAsync($"/api/v1/agenda-items/{_items[1]}/defer", new { reason = "x" }), 400, "validation", "سبب التأجيل قصير");
        await Check.Problem(await sec.PostAsync($"/api/v1/agenda-items/{_items[1]}/defer", new { reason = "لاستكمال البيانات", targetMeetingId = _other }), 400, "validation", "اجتماع مستهدف غير موجود");

        var d = await Check.Status(await sec.PostAsync($"/api/v1/agenda-items/{_items[1]}/defer", new { reason = "لاستكمال بيانات المؤشرات", targetMeetingId = _m2 }), 200, "تأجيل البند 2 للاجتماع الثاني");
        var newItem = d.G("newItemId");
        var m2 = await Json(sec, $"/api/v1/meetings/{_m2}");
        var carried = m2["agenda"]!.AsArray().First(a => a!.G("id") == newItem)!;
        Check.That(carried.G("carriedFromItemId") == _items[1] && carried.G("carriedFromMeetingId") == _m1, "البند في الاجتماع اللاحق مرتبط بأصله");
        Check.Equal(1L, carried["attachmentCount"]!.GetValue<long>(), "مرفق البند انتقل معه");
        var src = await Json(sec, $"/api/v1/agenda-items/{_items[1]}");
        Check.That(src["item"]!.S("status") == "deferred" && src["item"]!.G("forwardedToMeetingId") == _m2 && src["item"]!.S("statusReason").Contains("المؤشرات"),
            "الأصل يبقى «مؤجل» بسببه ويشير إلى الاجتماع اللاحق");
        await Check.Problem(await sec.PostAsync($"/api/v1/agenda-items/{_items[1]}/defer", new { reason = "تكرار التأجيل" }), 409, "invalid-transition", "لا يُؤجل مرتين");

        var rf = await Check.Status(await sec.PostAsync($"/api/v1/agenda-items/{_items[2]}/refer", new { targetBodyId = _other, reason = "يتطلب قرارًا من المجلس التوجيهي" }), 200, "إحالة البند 3");
        var pending = (await Json(await As("othersec"), $"/api/v1/bodies/{_other}/pending-items")).AsArray();
        Check.That(pending.Any(p => p!.G("id") == rf.G("pendingItemId") && p.S("kind") == "referred" && p.G("sourceBodyId") == _committee), "البند في قائمة الجهة المحال إليها");
        var notes = await Json(await As("othersec"), "/api/v1/notifications");
        Check.That(notes.Items().Any(n => n!.S("eventType") == "agenda_item_referred"), "أمين سر الجهة المحال إليها أُشعر");

        var closed = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/close"), 200, "إغلاق الاجتماع");
        Check.Equal(1, closed!["deferredItems"]!.GetValue<int>(), "البند 4 غير المنظور أُجل آليًا");
        var own = (await Json(sec, $"/api/v1/bodies/{_committee}/pending-items?status=pending")).AsArray();
        Check.That(own.Any(p => p!.G("sourceItemId") == _items[3]), "البند 4 في قائمة البنود المعلقة للجهة");

        var ag1 = (await Json(sec, $"/api/v1/meetings/{_m1}"))["agenda"]!.AsArray();
        string St(Guid i) => ag1.First(a => a!.G("id") == i)!.S("status");
        Check.That(St(_items[0]) == "discussed" && St(_items[1]) == "deferred" && St(_items[2]) == "referred" && St(_items[3]) == "deferred", "حالات البنود الأربعة صحيحة");
        Check.That(ag1.First(a => a!.G("id") == _items[0])!.S("recommendationText") == "التوصية باعتماد الخطة", "التوصية محفوظة");
    }

    [Test("P2-12", "إدراج البند المعلق في اجتماع لاحق، وإعادة جدولة الاجتماع بسبب مع ربطه، ثم إلغاء بسبب", 12)]
    public async Task ImportRescheduleCancel()
    {
        var sec = await As("sec");
        var pid = (await Json(sec, $"/api/v1/bodies/{_committee}/pending-items?status=pending")).AsArray().First(p => p!.G("sourceItemId") == _items[3])!.G("id");
        var imp = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/agenda-items/import", new { pendingItemIds = new[] { pid } }), 200, "إدراج البند المعلق");
        Check.Equal(2, imp!["agenda"]!.AsArray().Count, "الاجتماع الثاني فيه البندان المؤجلان");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m2}/agenda-items/import", new { pendingItemIds = new[] { pid } }), 409, "invalid-transition", "لا يُدرج مرتين");

        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m2}/reschedule", new { startsAt = DateTime.UtcNow.AddDays(25) }), 400, "validation", "إعادة الجدولة بلا سبب");
        var rs = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/reschedule", new { startsAt = DateTime.UtcNow.AddDays(25), reason = "تعارض مع زيارة رسمية" }), 200, "إعادة الجدولة");
        _m3 = rs.G("id");
        var old = (await Json(sec, $"/api/v1/meetings/{_m2}"))["meeting"]!;
        Check.That(old.S("status") == "postponed" && old.S("statusReason").Contains("زيارة") && old.G("rescheduledToId") == _m3, "الأصل مؤجل بسببه ومرتبط بالجديد");
        var neu = await Json(sec, $"/api/v1/meetings/{_m3}");
        Check.That(neu["meeting"]!.G("rescheduledFromMeetingId") == _m2 && neu["meeting"]!.G("previousMeetingId") == _m1, "الجديد مرتبط بالأصل وبالاجتماع السابق");
        Check.Equal(2, neu["agenda"]!.AsArray().Count, "البنود انتقلت للموعد الجديد");
        Check.That(neu["meeting"]!.S("referenceNo") != old.S("referenceNo"), "رقم جديد للموعد الجديد");

        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m3}/cancel", new { }), 400, "validation", "الإلغاء بلا سبب");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m3}/cancel", new { reason = "دمج الموضوعات في اجتماع استثنائي" }), 200, "الإلغاء");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m3}/schedule"), 409, "invalid-transition", "لا جدولة لاجتماع ملغى");
        var act = (await Json(sec, $"/api/v1/meetings/{_m2}/activity")).AsArray();
        Check.That(act.Any(a => a!.S("action") == "meeting.rescheduled"), "الخط الزمني للاجتماع يتضمن إعادة الجدولة");
        var act1 = (await Json(sec, $"/api/v1/meetings/{_m1}/activity")).AsArray().Select(a => a!.S("action")).ToHashSet();
        foreach (var a in new[] { "meeting.created", "meeting.scheduled", "agenda.item_added", "meeting.invitations_sent", "meeting.attendance_recorded", "meeting.opened", "agenda.item_deferred", "agenda.item_referred", "meeting.closed" })
            Check.That(act1.Contains(a), "الخط الزمني يتضمن " + a);
    }

    [Test("P2-13", "الاجتماع بالتمرير حين تجيزه السياسة: النصاب من ردود الموافقة", 13)]
    public async Task Circulation()
    {
        var gov = await As("gov");
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_committee}/policy", Policy(allowCirculation: true, reason: "إجازة التمرير")), 200, "سياسة v3");
        var sec = await As("sec");
        var c = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new
        { bodyId = _committee, titleAr = "اعتماد بالتمرير", meetingType = "circulation", startsAt = DateTime.UtcNow.AddHours(2), circulationDeadline = DateTime.UtcNow.AddDays(3) }), 201, "تمرير")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{c}/schedule"), 200, "جدولة");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{c}/agenda-items", new { titleAr = "اعتماد الدليل الإجرائي" }), 201, "بند");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{c}/invitations/send"), 200, "إرسال");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{c}/open", new { }), 409, "invalid-transition", "التمرير لا يُفتتح");
        foreach (var k in new[] { "chair", "vice", "m2" })
            await Check.Status(await (await As(k)).PostAsync($"/api/v1/meetings/{c}/rsvp", new { response = "accepted" }), 200, "موافقة " + k);
        var closed = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{c}/close"), 200, "إغلاق التمرير");
        Check.Equal("concluded", closed.S("status"), "اكتمل نصاب الردود (3 من 4)");
    }

    [Test("P2-14", "تغيير الرئيس بقرار مع حفظ السجل، والتفويض بصلاحيات الرئيس، وإنهاء الجهة دون حذف بياناتها", 14)]
    public async Task ChairDelegationTerminate()
    {
        var gov = await As("gov");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_committee}/change-chair", new { userId = _u["chair2"], decisionNo = "ق/30", reason = "تكليف رئيس جديد" }), 200, "تغيير الرئيس");
        var all = await Members(_committee, includeEnded: true);
        Check.That(all.Any(m => m!.G("userId") == _u["chair"] && m.S("role") == "chair" && m.S("status") == "ended"), "رئاسة السابق منتهية ومحفوظة");
        Check.That(all.Any(m => m!.G("userId") == _u["chair2"] && m.S("role") == "chair" && m.S("status") == "active"), "الرئيس الجديد نشط");
        await Check.Problem(await (await As("chair")).PostAsync($"/api/v1/meetings", new { bodyId = _committee, titleAr = "x", startsAt = DateTime.UtcNow.AddDays(3), location = "ق" }), 404, "not-found", "الرئيس السابق فقد صلاحياته");

        var chair2 = await As("chair2");
        await Check.Status(await chair2.PostAsync($"/api/v1/bodies/{_committee}/delegations", new
        { delegateUserId = _u["vice"], scope = "chair_authority", validFrom = DateTime.UtcNow.AddMinutes(-1), validTo = DateTime.UtcNow.AddDays(7), reason = "سفر الرئيس" }), 201, "تفويض النائب");
        var perms = (await Json(await As("vice"), $"/api/v1/bodies/{_committee}"))["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet();
        Check.That(perms.Contains(Permissions.BodyDelegate) && perms.Contains("minutes.approve"), "المفوض يحمل صلاحيات الرئيس مؤقتًا");

        // إنهاء الجهة المؤقتة بعد تفعيلها: لا حذف، والسجل يبقى قابلًا للعرض
        foreach (var (k, role) in new[] { ("otherchair", "chair"), ("othersec", "secretary") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/members", new { userId = _u[k], role }), 201, "عضوية مؤقتة " + k);
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/activate"), 200, "تفعيل المؤقتة");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/terminate", new { decisionNo = "ق/40", reason = "انتهاء الغرض من اللجنة" }), 200, "إنهاء المؤقتة");
        var t = await Json(gov, $"/api/v1/bodies/{_temp}");
        Check.Equal("dissolved", t["body"]!.S("status"), "الجهة منتهية لا محذوفة");
        Check.That((await Members(_temp, includeEnded: true)).All(m => m!.S("status") == "ended"), "العضويات منتهية ومحفوظة");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_temp}/archive", new { reason = "أرشفة بعد الإنهاء" }), 200, "أرشفة");
        await Check.Status(await gov.GetAsync($"/api/v1/bodies/{_temp}"), 200, "المؤرشفة قابلة للعرض");
    }
}
