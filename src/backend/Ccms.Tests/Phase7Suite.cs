using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>سيناريوهات قبول وحدة الذكاء الاصطناعي المستقلة (ADR-021) طرفًا لطرف: المزوّد التجريبي (mock) للمنطق، وخادم وهمي متوافق مع OpenAI لموصل الإنتاج.
/// لا يُدّعى في أي اختبار جودة نموذج لغوي فعلي.</summary>
public sealed class Phase7Suite(TestContext ctx) : IAsyncDisposable
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private readonly PgWireDataSource _ownerDs = new(ctx.OwnerConnectionString);
    private Db Owner => new(_ownerDs);
    private readonly FakeOpenAiServer _fake = new();
    private Guid _bodyA, _bodyB, _unit, _minutesA, _minutesDraft, _minutesB, _meetingA2, _resolutionA, _taskLinked, _taskFree, _savedOutput;

    public async ValueTask DisposeAsync() { await _fake.DisposeAsync(); await _ownerDs.DisposeAsync(); }

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p7-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private ApiClient Admin => ctx.AdminClient!;

    private async Task Flags(params (string Key, string Value)[] flags)
    {
        var current = await Admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, v) in flags)
            if (current[k]!["value"]!.GetValue<string>() != v)
                await Check.Status(await Admin.PutAsync($"/api/v1/admin/features/{k}", new { value = v, reason = $"اختبار وحدة الذكاء الاصطناعي: {k}", confirmImpact = true }), 200, k + "=" + v);
    }

    private async Task Faults(string mode, int delayMs = 0, int count = 0) =>
        await Check.Status(await Admin.PutAsync("/api/v1/ai/mock/faults", new { mode, delayMs, count }), 200, "عطل محقون " + mode);

    private async Task<HttpResponseMessage> Post(string user, string path, object? body = null) => await (await As(user)).PostAsync("/api/v1/ai" + path, body ?? new { });

    private async Task<JsonNode> Ok(string user, string path, object? body = null) => (await Check.Status(await Post(user, path, body), 200, "AI " + path))!;

    private async Task<long> LogCount(string status, string? function = null) =>
        (await Admin.GetJsonAsync($"/api/v1/admin/ai/requests?status={status}{(function is null ? "" : "&function=" + function)}&pageSize=1"))["total"]!.GetValue<long>();

    private object NewTask(string title) => new
    {
        bodyId = _bodyA, titleAr = title, assigneeType = "org_unit", assigneeOrgUnitId = _unit, accountableUserId = _u["sec"], dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(9)),
    };

    private static IEnumerable<JsonObject> Arr(JsonNode? n) => (n as JsonArray ?? []).OfType<JsonObject>();

    private async Task<Guid> MeetingWithMinutesAsync(string user, Guid body, string title, string[] agenda, string[] attendees)
    {
        var c = await As(user);
        var m = (await Check.Status(await c.PostAsync("/api/v1/meetings", new { bodyId = body, titleAr = title, meetingType = "regular", startsAt = DateTime.UtcNow.AddMinutes(30), location = "القاعة" }), 201, "اجتماع")).G("id");
        await Check.Status(await c.PostAsync($"/api/v1/meetings/{m}/schedule"), 200, "جدولة");
        foreach (var a in agenda) await Check.Status(await c.PostAsync($"/api/v1/meetings/{m}/agenda-items", new { titleAr = a }), 201, "بند");
        await Check.Status(await c.PostAsync($"/api/v1/meetings/{m}/invitations/send"), 200, "دعوات");
        await Check.Status(await c.PutAsync($"/api/v1/meetings/{m}/attendance", new { records = attendees.Select(u => new { userId = _u[u], status = "present", mode = "in_person" }).ToArray() }), 200, "حضور");
        await Check.Status(await c.PostAsync($"/api/v1/meetings/{m}/open", new { }), 200, "افتتاح");
        return m;
    }

    private async Task<Guid> ApprovedMinutesAsync(string sec, string chair, Guid body, string title, string[] agenda, string[] attendees, Func<JsonArray, object> draft, (string Title, string Text, int Item)[] resolutions)
    {
        var meeting = await MeetingWithMinutesAsync(sec, body, title, agenda, attendees);
        var c = await As(sec);
        var minutes = (await Check.Status(await c.PostAsync($"/api/v1/meetings/{meeting}/minutes"), 201, "محضر")).G("id");
        var items = (await c.GetJsonAsync($"/api/v1/meetings/{meeting}"))["agenda"]!.AsArray();
        await Check.Status(await c.PutAsync($"/api/v1/minutes/{minutes}/draft", draft(items)), 200, "مسودة");
        foreach (var r in resolutions)
            await Check.Status(await c.PostAsync($"/api/v1/minutes/{minutes}/resolutions", new { resolutionType = "decision", titleAr = r.Title, textAr = r.Text, agendaItemId = items[r.Item]!.G("id"), category = "technical" }), 201, "قرار");
        await Check.Status(await c.PostAsync($"/api/v1/meetings/{meeting}/close"), 200, "إغلاق");
        await Check.Status(await c.PostAsync($"/api/v1/minutes/{minutes}/submit", new { }), 200, "رفع");
        var hash = (await c.GetJsonAsync($"/api/v1/minutes/{minutes}"))["currentVersion"].S("hash");
        await Check.Status(await (await As(chair)).PostAsync($"/api/v1/minutes/{minutes}/approve", new { contentHash = hash }), 200, "اعتماد");
        return minutes;
    }

    // ============================ التهيئة ============================

    [Test("P7-SETUP", "تهيئة: جهتان (مقيدة وسرية)، ومحضر معتمد بقرار ومهام متأخرة، ومسودة محضر، ومستخدمون بأدوار مختلفة", 0)]
    public async Task Setup()
    {
        await _fake.StartAsync();
        await Flags(("AI_ENABLED", "true"), ("AI_PROVIDER", "none"));
        async Task User(string k, string[] roles) =>
            _u[k] = (await Check.Status(await Admin.PostAsync("/api/v1/users", new { email = $"p7-{k}@test.jsdo.gov.sa", displayNameAr = "م7 " + k, systemRoles = roles, initialPassword = Pw, userType = "internal" }), 201, "مستخدم " + k)).G("id");
        foreach (var k in new[] { "chair", "sec", "m1", "emp", "bchair", "bsec" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        await User("exec", ["executive"]);
        await User("auditor", ["auditor"]);
        _unit = (await Check.Status(await Admin.PostAsync("/api/v1/org-units", new { code = "P7-IT", nameAr = "الإدارة العامة لتقنية المعلومات" }), 201, "إدارة")).G("id");
        var gov = await As("gov");
        object Policy() => new
        {
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false,
            votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = false,
            minutesReviewDays = 3, extensionApprover = "chair", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار الذكاء الاصطناعي", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "none", minutesFinalApproval = "chair",
        };
        _bodyA = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P7-DTC", bodyType = "permanent_committee", nameAr = "لجنة التحول الرقمي للاختبار", nameEn = "P7 A" }), 201, "جهة أ")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_bodyA}/policy", Policy()), 200, "سياسة أ");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/activate"), 200, "تفعيل أ");
        _bodyB = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P7-SEC", bodyType = "permanent_committee", nameAr = "لجنة أمن المعلومات السرية", nameEn = "P7 B", classification = "secret" }), 201, "جهة ب")).G("id");
        foreach (var (u, role) in new[] { ("bchair", "chair"), ("bsec", "secretary") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_bodyB}/policy", Policy()), 200, "سياسة ب");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/activate"), 200, "تفعيل ب");

        _minutesA = await ApprovedMinutesAsync("sec", "chair", _bodyA, "اجتماع التحول السحابي", ["خطة الحوسبة السحابية", "سياسة أمن المعلومات"], ["chair", "sec", "m1"], items => new
        {
            sections = new[]
            {
                new { key = "opening", title = "الافتتاح", body = "افتتح رئيس اللجنة الاجتماع ورحب بالأعضاء. للتواصل مع منسق الاجتماع: 0551234567." },
                new { key = "closing", title = "الختام", body = "اختتم الرئيس الاجتماع بشكر الحضور." },
            },
            items = new object[]
            {
                new { agendaItemId = items[0]!.G("id"), discussion = "ناقشت اللجنة خطة الحوسبة السحابية ومراحل الانتقال. قررت اللجنة اعتماد خطة الحوسبة السحابية للمرحلة الأولى. تكليف الإدارة العامة لتقنية المعلومات بإعداد دراسة الجدوى خلال أسبوعين.", recommendation = "" },
                new { agendaItemId = items[1]!.G("id"), discussion = "استعرضت اللجنة مسودة سياسة أمن المعلومات.", recommendation = "أوصت اللجنة برفع السياسة إلى مجلس الإدارة لاعتمادها." },
            },
        }, [("اعتماد خطة الحوسبة السحابية", "قررت اللجنة اعتماد خطة الحوسبة السحابية للمرحلة الأولى وتكليف الإدارة العامة لتقنية المعلومات بالتنفيذ.", 0)]);
        var sec = await As("sec");
        _resolutionA = (await Owner.ScalarAsync<Guid>("SELECT id FROM resolutions.resolutions WHERE minutes_id = @id", new { id = _minutesA }));
        object Task(string title, Guid? res) => new
        {
            bodyId = _bodyA, resolutionId = res, titleAr = title, assigneeType = "org_unit", assigneeOrgUnitId = _unit, accountableUserId = _u["sec"],
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), priority = "high",
        };
        _taskLinked = (await Check.Status(await sec.PostAsync("/api/v1/tasks", Task("تنفيذ المرحلة الأولى من خطة الحوسبة السحابية", _resolutionA)), 201, "مهمة مرتبطة")).G("id");
        _taskFree = (await Check.Status(await sec.PostAsync("/api/v1/tasks", Task("إعداد دراسة جدوى خطة الحوسبة السحابية للمرحلة الأولى", null)), 201, "مهمة دون قرار")).G("id");
        // محاكاة مرور الوقت: المهمة المرتبطة متأخرة عشرة أيام
        await Owner.ExecuteAsync("UPDATE tasks.tasks SET due_date = (now() AT TIME ZONE 'Asia/Riyadh')::date - 10, status = 'in_progress' WHERE id = @id", new { id = _taskLinked });
        await Owner.ExecuteAsync("UPDATE resolutions.resolutions SET status = 'in_execution' WHERE id = @id", new { id = _resolutionA });

        _meetingA2 = await MeetingWithMinutesAsync("sec", _bodyA, "اجتماع متابعة المشاريع", ["مشروع البوابة الإلكترونية", "تجديد التراخيص"], ["chair", "sec", "m1"]);
        _minutesDraft = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA2}/minutes"), 201, "محضر مسودة")).G("id");

        _minutesB = await ApprovedMinutesAsync("bsec", "bchair", _bodyB, "اجتماع الميزانية السرية", ["ميزانية الأمن"], ["bchair", "bsec"], items => new
        {
            sections = new[] { new { key = "opening", title = "الافتتاح", body = "افتتح الرئيس الاجتماع السري لمناقشة الميزانية." } },
            items = new object[] { new { agendaItemId = items[0]!.G("id"), discussion = "قررت اللجنة اعتماد ميزانية الأمن السرية.", recommendation = "" } },
        }, [("اعتماد ميزانية الأمن", "قررت اللجنة اعتماد ميزانية الأمن السرية للعام القادم.", 0)]);
    }

    // ============================ المفاتيح وحالة الاتصال ============================

    [Test("P7-01", "التفعيل لا يعني الإتاحة: مفعل بلا مزوّد = 503، وحالة الاتصال مستقلة، وعنوان المزوّد مقيد بالمضيفين المسموحين، والإعدادات مسجلة", 1)]
    public async Task EnabledIsNotAvailable()
    {
        var status = await (await As("sec")).GetJsonAsync("/api/v1/ai/status");
        Check.That(status["enabled"]!.GetValue<bool>() && !status["provider"]!["ready"]!.GetValue<bool>(), "مفعل لكن غير جاهز");
        Check.Equal("not_selected", status["provider"].S("reason"), "السبب: لم يُختر مزوّد");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "مفعل دون مزوّد: لا نتيجة وهمية");
        var check = await Check.Status(await Admin.PostAsync("/api/v1/admin/connectors/ai/check"), 200, "فحص الاتصال");
        Check.Equal("not_configured", check.S("status"), "حالة الاتصال: غير مضبوط");
        Check.That(await LogCount("not_configured") >= 1, "الرفض مسجل في سجل التشغيل");

        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_BASE_URL", new { value = "https://api.unapproved-llm.example/v1", reason = "مضيف غير معتمد" }), 400, "validation", "مضيف خارج القائمة المسموحة");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_TIMEOUT_SECONDS", new { value = "1", reason = "مهلة أقل من الحد" }), 400, "validation", "عدد خارج الحدود");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_TIMEOUT_SECONDS", new { value = "abc", reason = "قيمة غير عددية" }), 400, "validation", "قيمة غير عددية");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/AI_TIMEOUT_SECONDS", new { value = "20", reason = "مهلة الاختبار" }), 200, "مهلة صحيحة");
        var h = (await Admin.GetJsonAsync("/api/v1/admin/features/history?key=AI_TIMEOUT_SECONDS")).Items();
        Check.That(h[0].S("oldValue") == "60" && h[0].S("newValue") == "20" && h[0].S("reason") == "مهلة الاختبار", "سجل الإعداد: القديم والجديد والسبب");
        await Check.Problem(await (await As("sec")).PutAsync("/api/v1/admin/features/AI_PROVIDER", new { value = "mock", reason = "محاولة غير مخولة" }), 403, "forbidden-action", "غير المخول لا يغيّر المزوّد");
    }

    // ============================ الوظائف بالمزوّد التجريبي ============================

    [Test("P7-02", "الوظائف العشر بالمزوّد التجريبي: مسودات موسومة بمراجع لسجلات حقيقية، ومكلف من الدليل، وتاريخ من النص، ولا تطبيق على السجلات", 2)]
    public async Task FunctionsWithMock()
    {
        await Flags(("AI_PROVIDER", "mock"), ("AI_MAX_CLASSIFICATION", "restricted"), ("AI_MINUTES_SUMMARY_ENABLED", "true"), ("AI_DECISION_EXTRACTION_ENABLED", "true"),
            ("AI_TASK_EXTRACTION_ENABLED", "true"), ("AI_SMART_SEARCH_ENABLED", "true"), ("AI_AUDIO_TRANSCRIPTION_ENABLED", "true"));
        // 1) التلخيص
        var s = await Ok("sec", $"/minutes/{_minutesA}/summary");
        Check.That(s["generated"]!.GetValue<bool>() && s["draft"]!.GetValue<bool>() && s["testOnly"]!.GetValue<bool>(), "مسودة مولدة موسومة بأنها تجريبية");
        Check.That(Arr(s["content"]!["points"]).Any() && Arr(s["content"]!["decisions"]).Any(), "نقاط وقرارات");
        Check.That(Arr(s["citations"]).Any(c => c.S("type") == "resolution" && c.G("id") == _resolutionA), "مرجع للقرار الحقيقي");
        Check.That(Arr(s["content"]!["points"]).All(p => p!["refs"]!.AsArray().Count > 0), "كل عبارة بمرجع");
        var outputId = s.G("outputId");
        await Check.Status(await (await As("sec")).PostAsync($"/api/v1/ai/outputs/{outputId}/save", new { note = "ملخص للأرشيف" }), 200, "حفظ الملخص");
        _savedOutput = outputId;

        // 5) استخراج القرارات والتوصيات بالاقتباس
        var d = await Ok("sec", $"/minutes/{_minutesA}/extract-decisions");
        var items = Arr(d["content"]!["items"]).ToList();
        Check.That(items.Any(i => i.S("kind") == "decision" && i.S("quote").Contains("اعتماد خطة الحوسبة")), "القرار باقتباسه");
        Check.That(items.Any(i => i.S("kind") == "recommendation" && i.S("quote").Contains("أوصت اللجنة")), "التوصية باقتباسها");
        Check.That(items.Any(i => i["possibleDuplicateOf"] is not null), "تنبيه تكرار مع قرار مسجل");

        // 6) المهام: المكلف من الدليل والتاريخ محسوب من نصه
        var t = await Ok("sec", $"/minutes/{_minutesA}/extract-tasks");
        var task = Arr(t["content"]!["items"]).First(i => i.S("quote").Contains("دراسة الجدوى"));
        Check.That(task["assignee"]!["matched"]!.GetValue<bool>() && task["assignee"].G("id") == _unit, "المكلف مطابق للإدارة في الدليل");
        Check.Equal("خلال أسبوعين", task.S("dueText"), "نص الموعد كما ورد");
        Check.That(task["dueDate"] is not null && task.S("dueBasis").StartsWith("relative"), "تاريخ محسوب صراحة من نص نسبي");

        // 7) ربط المهمة بالقرار
        var l = await Ok("sec", $"/tasks/{_taskFree}/link-suggestion");
        Check.Equal(_resolutionA, l["content"]!["best"].G("resolutionId"), "القرار المقترح هو الصحيح");

        // 2) مسودة جدول الأعمال من المفتوح والمتأخر
        var a = await Ok("sec", $"/bodies/{_bodyA}/agenda-draft", new { meetingId = _meetingA2 });
        Check.That(Arr(a["content"]!["items"]).Any(i => i.S("title").Contains("متابعة")), "بند متابعة من المتأخر");
        Check.That(Arr(a["citations"]).Any(c => c.S("type") == "task" && c.G("id") == _taskLinked), "مرجع للمهمة المتأخرة");

        // 3) مسودة المحضر من الملاحظات
        var notes = "افتتح الرئيس الاجتماع ورحب بالأعضاء.\nمشروع البوابة الإلكترونية: عرض مدير المشروع نسبة الإنجاز 60%.\nتوصية: تسريع ربط البوابة بنظام المراسلات.\nتجديد التراخيص: ناقش الأعضاء خيارات التجديد.\nاختتم الرئيس الاجتماع.";
        var dr = await Ok("sec", $"/minutes/{_minutesDraft}/draft-from-notes", new { notes });
        Check.That(Arr(dr["content"]!["sections"]).Any(x => x.S("key") == "opening") && Arr(dr["content"]!["sections"]).Any(x => x.S("key") == "closing"), "قسما الافتتاح والختام");
        Check.That(Arr(dr["content"]!["items"]).Any(x => x.S("agendaTitle") == "مشروع البوابة الإلكترونية" && x.S("recommendation").Contains("تسريع")), "ملخص البند وتوصيته");
        var draftNow = await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{_minutesDraft}");
        Check.That(!draftNow.ToJsonString().Contains("نسبة الإنجاز 60%"), "المسودة الآلية لم تُكتب في المحضر");

        // 10) الأسئلة: القرارات المتأخرة وآخر إجراء
        var q = await Ok("chair", "/ask", new { query = "ما القرارات المتأخرة في لجنة التحول الرقمي للاختبار؟" });
        Check.Equal("overdue_resolutions", q["intent"].S("intent"), "المقصد");
        Check.That(!q["content"]!["noAnswer"]!.GetValue<bool>() && Arr(q["citations"]).Any(c => c.G("id") == _resolutionA), "إجابة بمرجع للقرار المتأخر");
        var number = (await (await As("sec")).GetJsonAsync($"/api/v1/resolutions/{_resolutionA}"))["resolution"].S("number");
        var last = await Ok("chair", "/ask", new { query = $"ما آخر إجراء على القرار {number}؟" });
        Check.Equal("last_action", last["intent"].S("intent"), "مقصد آخر إجراء");
        Check.Equal(number, last["intent"].S("resolutionNumber"), "رقم القرار مستخرج من السؤال كما ورد");
        Check.That(Arr(last["content"]!["answer"]).Any(x => x.S("text").StartsWith("آخر إجراء")), "آخر إجراء مذكور");
        Check.That(Arr(last["citations"]).Any(c => c.S("type") == "task"), "مرجع للمهمة التي تحمل الإجراء");

        // 8) البحث باللغة الطبيعية
        var sr = await Ok("chair", "/search", new { query = "المهام المتأخرة في لجنة التحول الرقمي للاختبار" });
        Check.That(sr["interpretation"]!["overdue"]!.GetValue<bool>() && sr["interpretation"].G("bodyId") == _bodyA, "تفسير السؤال إلى مرشحات");
        Check.That(Arr(sr["items"]).Any(i => i.G("id") == _taskLinked), "نتيجة حقيقية: المهمة المتأخرة");

        // 9) الملخص التنفيذي للإدارة العليا
        var ex = await Ok("exec", "/executive-summary", new { });
        Check.That(ex["content"]!["headline"] is not null && Arr(ex["content"]!["risks"]).Any(), "عنوان ومخاطر");
        Check.That(Arr(ex["citations"]).Any(c => c.S("type") == "metric") && Arr(ex["citations"]).Any(c => c.S("type") == "resolution"), "مراجع للمؤشرات والقرارات");
        await Check.Problem(await Post("m1", "/executive-summary"), 403, "forbidden-action", "العضو لا يطلب الملخص التنفيذي للمحفظة");
    }

    [Test("P7-03", "لا اختلاق: المخرج المختلق (مراجع ومسؤولون وتواريخ وقرارات غير موجودة) يُسقط في الخادم ويُنبَّه إليه", 3)]
    public async Task NoFabrication()
    {
        await Faults("hallucinate");
        var s = await Ok("sec", $"/minutes/{_minutesA}/summary");
        Check.That(!s["content"]!.ToJsonString().Contains("عشرة ملايين") && !s["content"]!.ToJsonString().Contains("عبارة بلا مرجع"), "أُسقط القرار المختلق والعبارة بلا مرجع");
        Check.That(Arr(s["citations"]).All(c => c.S("ref") != "S999"), "لا مرجع مختلق");
        Check.That(s["warnings"]!.AsArray().Any(w => w!.GetValue<string>().Contains("أُسقط")), "تنبيه بالإسقاط");
        await Faults("hallucinate");
        var d = await Ok("sec", $"/minutes/{_minutesA}/extract-decisions");
        Check.That(!d["content"]!.ToJsonString().Contains("إنشاء إدارة جديدة"), "قرار باقتباس غير موجود في المصدر أُسقط");
        await Faults("hallucinate");
        var t = await Ok("sec", $"/minutes/{_minutesA}/extract-tasks");
        var all = t["content"]!.ToJsonString();
        Check.That(!all.Contains("مدير المشاريع الوهمي") && !all.Contains("2027-01-01") && !all.Contains("جهة غير موجودة"), "مكلف وتاريخ ومهمة مختلقة أُسقطت");
        await Faults("hallucinate");
        var q = await Ok("chair", "/ask", new { query = "ما القرارات المتأخرة؟" });
        Check.That(!q["content"]!.ToJsonString().Contains("ABC-9999"), "إجابة بمرجع مختلق أُسقطت");
        await Faults("hallucinate");
        var l = await Ok("sec", $"/tasks/{_taskFree}/link-suggestion");
        Check.That(l["content"]!["best"] is null, "ربط بقرار غير موجود أُسقط");
        await Faults("none");
    }

    // ============================ التعطيل ============================

    [Test("P7-04", "تعطيل وظيفة واحدة دون بقية الوظائف", 4)]
    public async Task DisableOneFunction()
    {
        await Flags(("AI_TASK_EXTRACTION_ENABLED", "false"));
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/extract-tasks"), 404, "feature-disabled", "استخراج المهام معطل");
        await Check.Problem(await Post("sec", $"/tasks/{_taskFree}/link-suggestion"), 404, "feature-disabled", "الربط تابع للمفتاح نفسه");
        await Ok("sec", $"/minutes/{_minutesA}/summary");
        await Ok("sec", $"/minutes/{_minutesA}/extract-decisions");
        var st = await (await As("sec")).GetJsonAsync("/api/v1/ai/status");
        Check.That(!st["functions"]!["extract_tasks"]!.GetValue<bool>() && st["functions"]!["minutes_summary"]!.GetValue<bool>(), "حالة الوظائف للواجهة");
        Check.That(await LogCount("blocked_disabled", "extract_tasks") >= 1, "الرفض مسجل");
        await Flags(("AI_TASK_EXTRACTION_ENABLED", "true"));
    }

    [Test("P7-05", "تعطيل الخدمة بالكامل: لا طلبات ولا نتائج جديدة، والعمليات الأساسية والبحث التقليدي تعمل، والمحفوظ باقٍ ويُطّلع عليه", 5)]
    public async Task DisableAll()
    {
        await Flags(("AI_ENABLED", "false"));
        var paths = new (string Path, object? Body)[]
        {
            ($"/minutes/{_minutesA}/summary", null), ($"/bodies/{_bodyA}/agenda-draft", null), ($"/minutes/{_minutesDraft}/draft-from-notes", new { notes = "ملاحظات اختبار كافية الطول للتحقق" }),
            ($"/minutes/{_minutesA}/extract-decisions", null), ($"/minutes/{_minutesA}/extract-tasks", null), ($"/tasks/{_taskFree}/link-suggestion", null),
            ("/search", new { query = "القرارات" }), ("/executive-summary", null), ("/ask", new { query = "ما القرارات المتأخرة؟" }),
        };
        foreach (var (p, b) in paths) await Check.Problem(await Post(p.Contains("executive") ? "exec" : "sec", p, b), 404, "feature-disabled", "معطل: " + p);
        await Check.Problem(await (await As("sec")).GetAsync("/api/v1/ai/status"), 404, "feature-disabled", "حالة الوحدة للواجهة");
        await Check.Problem(await (await As("sec")).PostContentAsync($"/api/v1/ai/meetings/{_meetingA2}/transcriptions", new MultipartFormDataContent()), 404, "feature-disabled", "التفريغ");

        // العمليات الأساسية
        var sec = await As("sec");
        await sec.GetJsonAsync($"/api/v1/minutes/{_minutesA}");
        await sec.GetJsonAsync($"/api/v1/resolutions/{_resolutionA}");
        await sec.GetJsonAsync($"/api/v1/tasks/{_taskLinked}");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutesDraft}/draft", new { sections = new[] { new { key = "opening", title = "الافتتاح", body = "افتتح الرئيس الاجتماع والذكاء الاصطناعي معطل." } } }), 200, "تحرير المحضر يعمل");
        await Check.Status(await sec.PostAsync("/api/v1/tasks", NewTask("مهمة تُنشأ والذكاء الاصطناعي معطل")), 201, "إنشاء مهمة يعمل");
        var search = await sec.GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("الحوسبة السحابية"));
        Check.That(search["items"]!.AsArray().Any(i => i.G("id") == _resolutionA), "البحث التقليدي يعمل ويجد القرار");

        // المحفوظ باقٍ ويُطّلع عليه، ولا ينشأ مخرج جديد
        var saved = await sec.GetJsonAsync($"/api/v1/ai/outputs/{_savedOutput}");
        Check.That(saved.S("status") == "saved" && saved["content"] is not null, "الملخص المحفوظ باقٍ بمحتواه");
        var list = await sec.GetJsonAsync($"/api/v1/ai/outputs?subjectType=minutes&subjectId={_minutesA}");
        Check.That(list.AsArray().Count >= 1, "قائمة المخرجات تُعرض");
        var before = (await Admin.GetJsonAsync("/api/v1/admin/ai/requests?status=succeeded&pageSize=1"))["total"]!.GetValue<long>();
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 404, "feature-disabled", "لا جديد");
        Check.Equal(before, (await Admin.GetJsonAsync("/api/v1/admin/ai/requests?status=succeeded&pageSize=1"))["total"]!.GetValue<long>(), "لا طلبات ناجحة جديدة أثناء التعطيل");
        Check.That(await LogCount("blocked_disabled") >= 10, "المحاولات المرفوضة مسجلة");
        await Flags(("AI_ENABLED", "true"));
    }

    // ============================ أعطال المزوّد ============================

    [Test("P7-06", "انقطاع المزوّد وفشل الطلب ومهلته ومخرجه غير الصالح: رمز صريح، ولا حفظ، ولا أثر على العمليات الأساسية", 6)]
    public async Task ProviderFailures()
    {
        var drafts = (await (await As("sec")).GetJsonAsync($"/api/v1/ai/outputs?subjectType=minutes&subjectId={_minutesA}")).AsArray().Count;
        await Faults("unavailable");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "انقطاع المزوّد");
        await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{_minutesA}"); // الأساسي يعمل أثناء الانقطاع
        await Faults("error");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 502, "ai-request-failed", "فشل الطلب");
        await Faults("invalid_output");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 502, "ai-request-failed", "مخرج غير صالح");
        await Faults("timeout");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "تجاوز المهلة");
        await Faults("none");
        Check.Equal(drafts, (await (await As("sec")).GetJsonAsync($"/api/v1/ai/outputs?subjectType=minutes&subjectId={_minutesA}")).AsArray().Count, "لم يُحفظ أي مخرج من الطلبات الفاشلة");
        Check.That(await LogCount("provider_unavailable") >= 1 && await LogCount("failed") >= 1 && await LogCount("invalid_output") >= 1 && await LogCount("timeout") >= 1, "كل حالة مسجلة بنوعها");
        await Check.Status(await (await As("sec")).PostAsync("/api/v1/tasks", NewTask("مهمة بعد عطل المزوّد")), 201, "العمليات الأساسية بعد العطل");
    }

    // ============================ موصل الإنتاج ============================

    [Test("P7-07", "موصل الخادم المتوافق: غياب مفتاح API، ومخزن الأسرار، والنجاح باستهلاك المزوّد، ورفض المفتاح، وخطأ الخادم، والمخرج المشوه، والانقطاع — دون أسرار أو محتوى في السجلات", 7)]
    public async Task OpenAiCompatibleAdapter()
    {
        await Flags(("AI_PROVIDER", "openai_compatible"), ("AI_BASE_URL", _fake.BaseUrl), ("AI_MODEL", "fake-arabic-model"));
        var st = await (await As("sec")).GetJsonAsync("/api/v1/ai/status");
        Check.Equal("missing_api_key", st["provider"].S("reason"), "غياب مفتاح API");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "لا طلب دون مفتاح");

        await Check.Problem(await (await As("gov")).PutAsync("/api/v1/admin/secrets/AI_API_KEY", new { value = _fake.ExpectedKey, reason = "محاولة غير مخولة" }), 403, "forbidden-action", "غير المخول لا يضبط السر");
        var set = await Check.Status(await Admin.PutAsync("/api/v1/admin/secrets/AI_API_KEY", new { value = "sk-wrong-key-000000000000", reason = "مفتاح خاطئ للاختبار" }), 200, "ضبط سر خاطئ");
        Check.That(set.S("source") == "database" && !set!.ToJsonString().Contains("sk-wrong"), "المصدر مشفر في القاعدة والقيمة لا تُعاد");
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 502, "ai-request-failed", "المزوّد رفض المفتاح");

        await Check.Status(await Admin.PutAsync("/api/v1/admin/secrets/AI_API_KEY", new { value = _fake.ExpectedKey, reason = "المفتاح الصحيح للاختبار" }), 200, "ضبط السر الصحيح");
        var secrets = await Admin.GetJsonAsync("/api/v1/admin/secrets");
        Check.That(!secrets.ToJsonString().Contains(_fake.ExpectedKey) && secrets["items"]![0]!["fingerprint"] is not null, "القائمة بالبصمة فقط");
        var check = await Check.Status(await Admin.PostAsync("/api/v1/admin/connectors/ai/check"), 200, "فحص الاتصال");
        Check.Equal("healthy", check.S("status"), "متصل");

        _fake.Mode = "ok";
        var s = await Ok("sec", $"/minutes/{_minutesA}/summary");
        Check.That(!s["testOnly"]!.GetValue<bool>() && s.S("provider") == "openai_compatible", "مزوّد الإنتاج (ليس تجريبيًا)");
        Check.That(Arr(s["content"]!["points"]).Count() == 1 && Arr(s["citations"]).All(c => c.S("ref") != "S99"), "المرجع المختلق من المزوّد الحقيقي أُسقط أيضًا");
        var sent = _fake.Requests.Last();
        var sentJson = JsonNode.Parse(sent.Body)!;
        Check.That(sent.Auth == "Bearer " + _fake.ExpectedKey && sentJson.S("model") == "fake-arabic-model"
            && sentJson["messages"]![1]!.S("content").Contains("<data>") && sentJson["messages"]![0]!.S("content").Contains("لا تخترع"), "الطلب بالمفتاح والنموذج والتعليمات والبيانات داخل وسم");
        var log = (await Admin.GetJsonAsync("/api/v1/admin/ai/requests?status=succeeded&function=minutes_summary&pageSize=1")).Items()[0];
        Check.That(log["inputTokens"]!.GetValue<int>() == 1234 && log["outputTokens"]!.GetValue<int>() == 56 && !log["usageEstimated"]!.GetValue<bool>(), "الاستهلاك كما أبلغه المزوّد");

        _fake.Mode = "error500";
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "خطأ خادم المزوّد = غير متاح مؤقتًا");
        _fake.Mode = "malformed";
        var n = _fake.Requests.Count;
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 502, "ai-request-failed", "مخرج مشوه بعد محاولة تصحيح");
        Check.Equal(n + 2, _fake.Requests.Count, "محاولة تصحيح واحدة فقط");
        _fake.Mode = "slow";
        _fake.SlowMs = 7000;
        await Flags(("AI_TIMEOUT_SECONDS", "5"));
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "المهلة الزمنية");
        await Flags(("AI_TIMEOUT_SECONDS", "20"));
        _fake.Mode = "ok";
        await _fake.StopAsync();
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "المزوّد متوقف");
        await (await As("sec")).GetJsonAsync($"/api/v1/minutes/{_minutesA}");

        // لا أسرار ولا محتوى في سجل التشغيل ولا في سجل الإعدادات
        var dump = await Owner.QueryAsync("SELECT row_to_json(r)::text FROM ai.requests r", null, r => r.GetString(0));
        Check.That(dump.All(x => !x.Contains(_fake.ExpectedKey) && !x.Contains("الحوسبة السحابية") && !x.Contains("0551234567")), "سجل التشغيل دون أسرار ولا نصوص");
        var flagHist = await Owner.QueryAsync("SELECT coalesce(old_value,'') || coalesce(new_value,'') FROM config.feature_flag_history", null, r => r.GetString(0));
        var secretHist = await Owner.QueryAsync("SELECT row_to_json(h)::text FROM config.secret_history h", null, r => r.GetString(0));
        Check.That(flagHist.Concat(secretHist).All(x => !x.Contains(_fake.ExpectedKey)), "السر ليس في أي سجل");
        Check.That(secretHist.Any(x => x.Contains("\"denied\"")) && secretHist.Count(x => x.Contains("\"set\"")) == 2, "محاولات ضبط السر مسجلة");
        var raw = await Owner.ScalarAsync<string>("SELECT ciphertext FROM config.secrets WHERE name = 'AI_API_KEY'");
        Check.That(raw is not null && !Encoding.UTF8.GetString(Convert.FromBase64String(raw)).Contains(_fake.ExpectedKey), "مخزن مشفرًا");
        await Flags(("AI_PROVIDER", "mock"));
    }

    // ============================ الصلاحيات والتصنيف ============================

    [Test("P7-08", "منع الوصول إلى سجلات غير مصرح بها: لا تلخيص ولا إجابة ولا نتائج من جهة لا يطّلع عليها المستخدم، ولا اطلاع على مخرجات غيره أو السجل", 8)]
    public async Task UnauthorizedRecords()
    {
        await Check.Problem(await Post("bsec", $"/minutes/{_minutesA}/summary"), 404, "not-found", "محضر جهة أخرى");
        await Check.Problem(await Post("bsec", $"/tasks/{_taskFree}/link-suggestion"), 404, "not-found", "مهمة جهة أخرى");
        await Check.Problem(await Post("emp", "/ask", new { query = "ما القرارات المتأخرة؟" }), 403, "forbidden-action", "مستخدم بلا ai.use");
        await Check.Problem(await Post("m1", $"/minutes/{_minutesA}/extract-decisions"), 403, "forbidden-action", "العضو لا يستخرج (تحرير المحضر)");
        var q = await Ok("bsec", "/ask", new { query = "ما القرارات المتأخرة في لجنة التحول الرقمي للاختبار؟" });
        Check.That(Arr(q["citations"]).All(c => c["id"] is null || c.G("id") != _resolutionA) && Arr(q["facts"]).All(f => f["id"] is null || f.G("id") != _resolutionA), "لا مراجع من جهة لا يطّلع عليها");
        var sr = await Ok("bsec", "/search", new { query = "خطة الحوسبة السحابية" });
        Check.That(Arr(sr["items"]).All(i => i.G("bodyId") != _bodyA), "البحث الذكي ضمن صلاحياته");
        var trad = await (await As("bsec")).GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("الحوسبة السحابية"));
        Check.Equal(0, trad["items"]!.AsArray().Count, "البحث التقليدي ضمن صلاحياته");
        await Check.Problem(await (await As("bsec")).GetAsync($"/api/v1/ai/outputs/{_savedOutput}"), 404, "not-found", "مخرج على محضر لا يطّلع عليه");
        await Check.Problem(await (await As("m1")).GetAsync("/api/v1/admin/ai/requests"), 403, "forbidden-action", "سجل التشغيل لغير المخول");
        await Check.Status(await (await As("auditor")).GetAsync("/api/v1/admin/ai/requests"), 200, "المدقق يطّلع على سجل التشغيل");
        await Check.Problem(await (await As("auditor")).PostAsync("/api/v1/ai/maintenance/run"), 403, "forbidden-action", "المدقق لا يشغّل الصيانة");
    }

    [Test("P7-09", "سياسة التصنيف: لا يُرسل ما يتجاوز المسموح، والمزوّد الخارجي سقفه «مقيد» مع حجب المعرّفات الشخصية، وnone يمنع كل إرسال", 9)]
    public async Task ClassificationPolicy()
    {
        await Flags(("AI_MAX_CLASSIFICATION", "public"));
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 403, "ai-classification-blocked", "محضر مقيد والمسموح عام");
        var q = await Ok("chair", "/ask", new { query = "ما القرارات المتأخرة؟" });
        Check.That(q["noSources"]!.GetValue<bool>() && q["warnings"]!.AsArray().Any(w => w!.GetValue<string>().Contains("استُبعد")), "السؤال: استُبعد المقيد ولم يُرسل ونُبّه المستخدم");
        Check.That(await LogCount("blocked_classification") >= 1, "الحجب مسجل");

        await Flags(("AI_MAX_CLASSIFICATION", "secret"));
        await Ok("bsec", $"/minutes/{_minutesB}/summary"); // داخلي: السري مسموح بالإعداد
        await Flags(("AI_PROVIDER_HOSTING", "external"));
        await Check.Problem(await Post("bsec", $"/minutes/{_minutesB}/summary"), 403, "ai-classification-blocked", "مزوّد خارجي: السري ممنوع رغم الإعداد");
        var st = await (await As("sec")).GetJsonAsync("/api/v1/ai/status");
        Check.Equal("restricted", st["policy"].S("maxClassification"), "السقف الفعلي للمزوّد الخارجي");
        await Ok("sec", $"/minutes/{_minutesA}/summary");
        var log = (await Admin.GetJsonAsync("/api/v1/admin/ai/requests?status=succeeded&function=minutes_summary&pageSize=1")).Items()[0];
        Check.That(log["redactions"]!.GetValue<int>() >= 1, "حُجب رقم الجوال قبل الإرسال للمزوّد الخارجي");
        await Flags(("AI_PROVIDER_HOSTING", "on_premises"), ("AI_MAX_CLASSIFICATION", "none"));
        await Check.Problem(await Post("chair", "/search", new { query = "القرارات" }), 403, "ai-classification-blocked", "none يمنع حتى الاستعلام");
        await Flags(("AI_MAX_CLASSIFICATION", "restricted"));
    }

    // ============================ الحدود والتنبيهات ============================

    [Test("P7-10", "حدود الاستخدام: الحد اليومي لكل مستخدم والحد الشهري (429)، وتنبيه مديري النظام عند الاقتراب وعند البلوغ", 10)]
    public async Task Quotas()
    {
        var mine = (await (await As("m1")).GetJsonAsync("/api/v1/ai/status"))["usage"]!["myToday"]!.GetValue<long>();
        await Flags(("AI_DAILY_REQUESTS_PER_USER", (mine + 1).ToString()));
        await Ok("m1", $"/minutes/{_minutesA}/summary");
        await Check.Problem(await Post("m1", $"/minutes/{_minutesA}/summary"), 429, "ai-quota-exceeded", "الحد اليومي");
        Check.That((await Admin.GetJsonAsync("/api/v1/admin/ai/requests?status=blocked_quota&pageSize=1")).Items()[0].S("errorCode") == "daily_per_user", "سبب الرفض: الحد اليومي");
        await Flags(("AI_DAILY_REQUESTS_PER_USER", "50"));

        var o = await Admin.GetJsonAsync("/api/v1/admin/ai/overview");
        var month = o["usage"]!["monthRequests"]!.GetValue<long>();
        // حد شهري يجعل الاستهلاك الحالي فوق 80٪ ودون 100٪ بطلب واحد
        var limit = Math.Max(10, (long)Math.Ceiling((month + 1) / 0.85));
        await Flags(("AI_MONTHLY_REQUEST_LIMIT", limit.ToString()), ("AI_USAGE_ALERT_PERCENT", "80"));
        await Ok("sec", $"/minutes/{_minutesA}/summary");
        var notes = await Admin.GetJsonAsync("/api/v1/notifications?pageSize=50");
        Check.That(notes.Items().Any(n => n.S("titleAr").Contains("الحد الشهري")), "تنبيه الاقتراب من الحد لمدير النظام");
        await Flags(("AI_MONTHLY_REQUEST_LIMIT", (await Admin.GetJsonAsync("/api/v1/admin/ai/overview"))["usage"]!["monthRequests"]!.GetValue<long>().ToString()));
        await Check.Problem(await Post("sec", $"/minutes/{_minutesA}/summary"), 429, "ai-quota-exceeded", "الحد الشهري");
        Check.That(await LogCount("blocked_quota") >= 2, "الرفض بالحد مسجل");
        await Flags(("AI_MONTHLY_REQUEST_LIMIT", "3000"));
        var ov = await Admin.GetJsonAsync("/api/v1/admin/ai/overview");
        Check.That(ov["alerts"]!.AsArray().Count >= 1 && ov["byFunction"]!.AsArray().Count > 0, "لوحة الاستهلاك: التنبيهات والتوزيع");
    }

    // ============================ التفريغ الصوتي ============================

    private static MultipartFormDataContent Audio(byte[] bytes, string type, bool consent, string? reference)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", "meeting.wav");
        if (consent) form.Add(new StringContent("true"), "consentConfirmed");
        if (reference is not null) form.Add(new StringContent(reference), "consentReference");
        return form;
    }

    private static byte[] TestWav(string transcript) =>
        [.. Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt "), .. new byte[24], .. Encoding.UTF8.GetBytes("LISTCCMS-TEST-TRANSCRIPT:" + transcript + "\0")];

    [Test("P7-11", "التفريغ الصوتي: تسجيل مصرح به بموافقة موثقة فقط، ومعالجة غير متزامنة، وحجز المنتظر عند التعطيل واستئنافه صراحة", 11)]
    public async Task Transcriptions()
    {
        var sec = await As("sec");
        var url = $"/api/v1/ai/meetings/{_meetingA2}/transcriptions";
        await Check.Problem(await sec.PostContentAsync(url, Audio(TestWav("نص"), "audio/wav", false, "إقرار 1")), 400, "validation", "دون موافقة موثقة");
        await Check.Problem(await sec.PostContentAsync(url, Audio(TestWav("نص"), "audio/wav", true, null)), 400, "validation", "دون مرجع الموافقة");
        await Check.Problem(await sec.PostContentAsync(url, Audio(TestWav("نص"), "text/plain", true, "إقرار 1")), 400, "validation", "ليس تسجيلًا صوتيًا");
        await Check.Problem(await (await As("m1")).PostContentAsync(url, Audio(TestWav("نص"), "audio/wav", true, "إقرار 1")), 403, "forbidden-action", "العضو لا يرفع تسجيلًا");

        var job = (await Check.Status(await sec.PostContentAsync(url, Audio(TestWav("افتتح الرئيس الاجتماع وناقش الأعضاء مشروع البوابة."), "audio/wav", true, "البند 1 من محضر الاجتماع: موافقة الحضور")), 200, "رفع تسجيل"))!.G("jobId");
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "معالجة");
        var list = await sec.GetJsonAsync(url);
        var j = list["jobs"]!.AsArray().First(x => x.G("id") == job)!;
        Check.Equal("completed", j.S("status"), "اكتمل التفريغ");
        var output = await sec.GetJsonAsync($"/api/v1/ai/outputs/{j.G("outputId")}");
        Check.That(output["content"].S("text").Contains("مشروع البوابة") && output["testOnly"]!.GetValue<bool>(), "النص المفرّغ مسودة موسومة");
        Check.Equal("البند 1 من محضر الاجتماع: موافقة الحضور", j["consent"].S("reference"), "مرجع الموافقة محفوظ");

        // ملف صوتي حقيقي: المزوّد التجريبي لا يدّعي تفريغه
        var real = (await Check.Status(await sec.PostContentAsync(url, Audio([.. Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE"), .. new byte[2000]], "audio/wav", true, "إقرار 2")), 200, "تسجيل بلا نص مضمن"))!.G("jobId");
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "معالجة");
        var r = (await sec.GetJsonAsync(url))["jobs"]!.AsArray().First(x => x.G("id") == real)!;
        Check.That(r.S("status") == "failed" && r.S("lastError").Contains("لا يفرّغ صوتًا حقيقيًا"), "فشل صريح لا نص وهمي");

        // التعطيل والمهمة منتظرة: تُحجز ولا تُرسل، ولا تُستأنف إلا صراحة
        var queued = (await Check.Status(await sec.PostContentAsync(url, Audio(TestWav("نص يُحجز"), "audio/wav", true, "إقرار 3")), 200, "تسجيل ثالث"))!.G("jobId");
        var impact = await Admin.GetJsonAsync("/api/v1/admin/features/AI_AUDIO_TRANSCRIPTION_ENABLED/impact?value=false");
        Check.That(impact["requiresConfirmation"]!.GetValue<bool>(), "التعطيل يتطلب تأكيد الأثر");
        await Flags(("AI_AUDIO_TRANSCRIPTION_ENABLED", "false"));
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "صيانة أثناء التعطيل");
        var h = (await sec.GetJsonAsync(url))["jobs"]!.AsArray().First(x => x.G("id") == queued)!;
        Check.That(h.S("status") == "held" && h.S("heldReason") == "ai_disabled" && h["outputId"] is null, "محجوزة دون إرسال");
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/jobs/{queued}/resume"), 404, "feature-disabled", "لا استئناف والوظيفة معطلة");
        await Flags(("AI_AUDIO_TRANSCRIPTION_ENABLED", "true"));
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "صيانة بعد التفعيل");
        Check.Equal("held", (await sec.GetJsonAsync(url))["jobs"]!.AsArray().First(x => x.G("id") == queued)!.S("status"), "لا استئناف آلي بعد التفعيل");
        await Check.Status(await sec.PostAsync($"/api/v1/ai/jobs/{queued}/resume"), 200, "استئناف صريح");
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "معالجة");
        Check.Equal("completed", (await sec.GetJsonAsync(url))["jobs"]!.AsArray().First(x => x.G("id") == queued)!.S("status"), "اكتملت بعد الاستئناف");
    }

    [Test("P7-12", "التعطيل أثناء طلب جارٍ: تأكيد الأثر، وحجز النتيجة دون عرضها، ثم الإفراج الصريح بعد إعادة التفعيل", 12)]
    public async Task DisableInFlight()
    {
        await Faults("none", delayMs: 3000);
        var pending = Post("chair", "/ask", new { query = "ما القرارات المتأخرة؟" });
        await Task.Delay(1500);
        var impact = await Admin.GetJsonAsync("/api/v1/admin/features/AI_ENABLED/impact?value=false");
        Check.That(Arr(impact["items"]).Any(i => i.S("code") == "ai_inflight_requests" && i["count"]!.GetValue<int>() >= 1), "الأثر يذكر الطلب الجاري");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "false", reason = "تعطيل دون تأكيد" }), 409, "confirmation-required", "يتطلب تأكيدًا");
        await Flags(("AI_ENABLED", "false"));
        var resp = await pending;
        var body = await Check.Status(resp, 409, "النتيجة محجوزة");
        Check.Equal("ai-disabled-during-processing", body.S("code"), "رمز الحجز");
        var outputId = body.G("outputId");
        await Faults("none");
        var held = await (await As("chair")).GetJsonAsync($"/api/v1/ai/outputs/{outputId}");
        Check.That(held.S("status") == "held" && held["content"] is null && held["citations"] is null, "محجوزة ولا يُعرض محتواها");
        await Check.Problem(await (await As("chair")).PostAsync($"/api/v1/ai/outputs/{outputId}/release"), 404, "feature-disabled", "لا إفراج والخدمة معطلة");
        await Flags(("AI_ENABLED", "true"));
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/ai/outputs/{outputId}/release"), 200, "إفراج صريح");
        var released = await (await As("chair")).GetJsonAsync($"/api/v1/ai/outputs/{outputId}");
        Check.That(released.S("status") == "draft" && released["content"] is not null, "أصبحت مسودة قابلة للمراجعة");
        Check.That(await LogCount("held") >= 1, "الحجز مسجل");
    }

    // ============================ الضوابط المعمارية ============================

    [Test("P7-13", "الذكاء الاصطناعي لا يعتمد ولا يوقّع ولا يصوّت ولا يغلق ولا يصدر قرارًا: لا كتابة خارج مخطط ai، و«طُبّق» لا يغيّر السجلات الأساسية", 13)]
    public async Task NoAutonomousActions()
    {
        var root = Path.GetFullPath(Path.Combine(ctx.MigrationsPath, "../../src/backend/Ccms.Modules.Ai"));
        var code = string.Join("\n", Directory.GetFiles(root, "*.cs").Select(File.ReadAllText));
        var writes = Regex.Matches(code, @"(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+([a-z_]+)\.", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
        Check.That(writes.All(s => s == "ai"), "كتابة في مخطط ai فقط: " + string.Join(", ", writes));
        foreach (var forbidden in new[] { "/approve", "/sign", "/votes", "/close", "/issue", "/verify" })
            Check.That(!Regex.IsMatch(code, $"Map(Post|Put)\\(\"[^\"]*{Regex.Escape(forbidden)}"), "لا نقطة نهاية: " + forbidden);
        var refs = File.ReadAllText(Path.Combine(root, "Ccms.Modules.Ai.csproj"));
        Check.That(!refs.Contains("Ccms.Modules.") , "الوحدة تعتمد على العقود فقط");

        var resBefore = (await (await As("sec")).GetJsonAsync($"/api/v1/resolutions/{_resolutionA}"))["resolution"].S("status");
        var d = await Ok("sec", $"/minutes/{_minutesA}/extract-decisions");
        await Check.Status(await (await As("sec")).PostAsync($"/api/v1/ai/outputs/{d.G("outputId")}/applied", new { refs = new[] { new { recordType = "resolution", recordId = _resolutionA, index = 0 } }, note = "أنشأه أمين السر يدويًا" }), 200, "تسجيل ما طُبّق");
        Check.Equal(resBefore, (await (await As("sec")).GetJsonAsync($"/api/v1/resolutions/{_resolutionA}"))["resolution"].S("status"), "حالة القرار لم تتغير");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/ai/outputs/{d.G("outputId")}/applied", new { refs = new[] { new { recordType = "resolution", recordId = Guid.NewGuid(), index = 0 } } }), 400, "validation", "لا يُسجل سجل غير موجود");
    }

    [Test("P7-14", "سياسة الاحتفاظ: يُفرغ محتوى المسودات غير المحفوظة بعد المدة ويبقى سجلها، والمحفوظ لا يُمس", 14)]
    public async Task Retention()
    {
        var draft = (await Ok("sec", $"/minutes/{_minutesA}/summary")).G("outputId");
        await Owner.ExecuteAsync("UPDATE ai.outputs SET created_at = now() - interval '200 days' WHERE id IN (@a, @b)", new { a = draft, b = _savedOutput });
        await Check.Status(await Admin.PostAsync("/api/v1/ai/maintenance/run"), 200, "صيانة");
        var row = await Owner.QuerySingleAsync("SELECT status, content IS NULL, purged_at IS NOT NULL FROM ai.outputs WHERE id = @id", new { id = draft }, r => (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2)));
        Check.That(row.Item1 == "expired" && row.Item2 && row.Item3, "المسودة القديمة: انتهت وأُفرغ محتواها وبقي سجلها");
        var saved = await (await As("sec")).GetJsonAsync($"/api/v1/ai/outputs/{_savedOutput}");
        Check.That(saved.S("status") == "saved" && saved["content"] is not null, "المحفوظ باقٍ بمحتواه");
        // الحالة الختامية لاختبارات إعادة التشغيل
        await Flags(("AI_ENABLED", "true"), ("AI_MINUTES_SUMMARY_ENABLED", "true"));
    }
}
