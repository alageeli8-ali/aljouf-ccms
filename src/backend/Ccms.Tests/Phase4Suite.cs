using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>سيناريوهات قبول المرحلة 4 (المهام والتكليفات، والتصعيد، وبوابة الجهات الخارجية، وخطابات التكليف، واللوحات) —
/// طرفًا لطرف عبر HTTP على قاعدة PostgreSQL حقيقية. البوابة والمراسلات والذكاء الاصطناعي والتوقيع معطلة ما لم يذكر خلاف ذلك.</summary>
public sealed class Phase4Suite(TestContext ctx)
{
    private const string Pw = "Test@Password2026";
    private const string ExtPw = "Portal#Entity2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private Guid _body, _unit, _entityA, _entityB, _resolution, _meeting, _minutes;
    private Guid _tUnit, _tMember, _tEntityA, _tEntityB, _tEsc;
    private DateOnly _due;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    private static int Year => Today.Year;

    private async Task<ApiClient> As(string user, string? password = null)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p4-{user}@test.jsdo.gov.sa", password ?? Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private static string Key() => Guid.NewGuid().ToString("N");
    private async Task<JsonNode> Card(Guid id, string user = "sec") => await (await As(user)).GetJsonAsync($"/api/v1/tasks/{id}");
    private static int I(JsonNode? n, string key) => n![key]!.GetValue<int>();

    private static async Task<HttpResponseMessage> Upload(ApiClient c, Guid taskId, string name, bool share = false)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 " + name + " " + Guid.NewGuid()));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", name);
        content.Add(new StringContent("task"), "ownerType");
        content.Add(new StringContent(taskId.ToString()), "ownerId");
        content.Add(new StringContent("attachment"), "purpose");
        if (share) content.Add(new StringContent("true"), "shareWithEntity");
        return await c.PostContentAsync("/api/v1/documents", content);
    }

    private async Task<List<string>> NotificationTypes(string user) =>
        (await (await As(user)).GetJsonAsync("/api/v1/notifications?pageSize=100")).Items().Select(n => n.S("eventType")).ToList();

    private async Task<JsonNode> RunEscalations(DateOnly asOf) =>
        (await Check.Status(await ctx.AdminClient!.PostAsync("/api/v1/tasks/escalations/run", new { asOf }), 200, "تقييم التصعيد " + asOf))!;

    // ============================ التهيئة ============================

    [Test("P4-SETUP", "تهيئة المرحلة 4: إدارة ومنفذ ومتابع، وجهتان خارجيتان بممثلين، ولجنة بسياسة تصعيد مهيأة، وقرار صادر", 0)]
    public async Task Setup()
    {
        var admin = ctx.AdminClient!;
        var features = await admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, flag) in new[] { ("EXTERNAL_PORTAL_ENABLED", "false"), ("CORRESPONDENCE_INTEGRATION_ENABLED", "false"), ("AI_ENABLED", "false") })
            if (features[k]!["value"]!.GetValue<string>() != flag)
                await Check.Status(await admin.PutAsync($"/api/v1/admin/features/{k}", new { value = flag, reason = "حالة البدء لاختبارات المرحلة 4", confirmImpact = true }), 200, k);

        async Task<Guid> User(string k, string[] roles, string type = "internal", Guid? unit = null, Guid? entity = null) =>
            _u[k] = (await Check.Status(await admin.PostAsync("/api/v1/users", new
            {
                email = $"p4-{k}@test.jsdo.gov.sa", displayNameAr = "م4 " + k, systemRoles = roles, initialPassword = Pw, userType = type, orgUnitId = unit, externalEntityId = entity,
            }), 201, "مستخدم " + k)).G("id");
        foreach (var k in new[] { "chair", "sec", "m1", "mgr", "fo", "outsider" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        await User("exec", ["executive"]);
        var gov = await As("gov");
        _unit = (await Check.Status(await gov.PostAsync("/api/v1/org-units", new { code = "P4-IT", nameAr = "الإدارة العامة لتقنية المعلومات (م4)", managerUserId = _u["mgr"] }), 201, "إدارة")).G("id");
        await User("emp", [], unit: _unit);
        _entityA = (await Check.Status(await gov.PostAsync("/api/v1/external-entities", new { nameAr = "أمانة منطقة الجوف (م4)", liaisonUserId = _u["fo"], contactName = "مدير التخطيط" }), 201, "جهة أ")).G("id");
        _entityB = (await Check.Status(await gov.PostAsync("/api/v1/external-entities", new { nameAr = "فرع وزارة الإسكان (م4)", liaisonUserId = _u["fo"] }), 201, "جهة ب")).G("id");
        await User("extA", ["external_representative"], "external", entity: _entityA);
        await User("extB", ["external_representative"], "external", entity: _entityB);

        _body = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P4-COMM", bodyType = "permanent_committee", nameAr = "لجنة متابعة المشاريع", nameEn = "P4" }), 201, "لجنة")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_body}/policy", new
        {
            effectiveFrom = Today, quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false, votingThreshold = "majority_of_votes_cast",
            abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = false, minutesReviewDays = 3,
            extensionApprover = "chair", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار المرحلة 4", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "none", minutesFinalApproval = "chair",
            escalation = new
            {
                workingDays = false, repeatOverdueEveryDays = 7, reviewOverdueDays = 2,
                levels = new object[]
                {
                    new { level = 0, trigger = "due_soon", offsetDays = -2, notify = new[] { "assignee", "accountable" } },
                    new { level = 1, trigger = "overdue", offsetDays = 0, notify = new[] { "assignee", "accountable" } },
                    new { level = 2, trigger = "overdue_l2", offsetDays = 3, notify = new[] { "secretary", "assignee_org_unit_manager" } },
                    new { level = 3, trigger = "overdue_l3", offsetDays = 6, notify = new[] { "chair", "executive" } },
                },
            },
        }), 200, "سياسة بتصعيد مهيأ");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_body}/activate"), 200, "تفعيل");

        // قرار صادر عبر الدورة الكاملة: اجتماع ← محضر ← قرار ← اعتماد
        var sec = await As("sec");
        _meeting = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _body, titleAr = "اجتماع متابعة المشاريع", startsAt = DateTime.UtcNow.AddHours(1), location = "القاعة" }), 201, "اجتماع")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/schedule"), 200, "جدولة");
        var item = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/agenda-items", new { titleAr = "خطة رقمنة الخدمات البلدية" }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/invitations/send"), 200, "دعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_meeting}/attendance", new
        { records = new[] { new { userId = _u["chair"], status = "present", mode = "in_person" }, new { userId = _u["m1"], status = "present", mode = "in_person" } } }), 200, "حضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/open", new { }), 200, "افتتاح");
        _minutes = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/minutes"), 201, "محضر")).G("id");
        _resolution = (await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/resolutions", new
        {
            resolutionType = "decision", titleAr = "اعتماد خطة رقمنة الخدمات البلدية", agendaItemId = item, category = "technical",
            textAr = "قررت اللجنة اعتماد خطة رقمنة الخدمات البلدية وتكليف الجهات المعنية بالتنفيذ.",
        }), 201, "قرار")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meeting}/close"), 200, "إغلاق");
        var s = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes}/submit", new { }), 200, "رفع المحضر");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes}/approve", new { contentHash = s.S("contentHash") }), 200, "اعتماد المحضر");
        Check.Equal("issued", (await sec.GetJsonAsync($"/api/v1/resolutions/{_resolution}"))["resolution"].S("status"), "القرار صادر");
        _due = Today.AddDays(10);
    }

    // ============================ إنشاء المهام ============================

    [Test("P4-01", "إنشاء مهمة من قرار وإسنادها لإدارة داخلية: منفذ مسؤول ومتابع منفصل ومخرجات بمعايير قبول، دون تكرار", 1)]
    public async Task CreateFromResolution()
    {
        var sec = await As("sec");
        var body = new
        {
            bodyId = _body, resolutionId = _resolution, titleAr = "إعداد كراسة الشروط لمنصة الخدمات الرقمية", description = "إعداد كراسة الشروط والمواصفات الفنية",
            assigneeType = "org_unit", assigneeOrgUnitId = _unit, executorUserId = _u["emp"], accountableUserId = _u["fo"], priority = "high", dueDate = _due,
            acceptanceCriteria = "مطابقة نموذج هيئة الحكومة الرقمية واعتماد الإدارة القانونية",
            deliverables = new[]
            {
                new { title = "كراسة الشروط", acceptanceCriteria = "تشمل نطاق العمل ومعايير التقييم", required = true },
                new { title = "تقدير التكلفة", acceptanceCriteria = "مبني على ثلاثة عروض أسعار على الأقل", required = true },
            },
        };
        await Check.Problem(await sec.PostAsync("/api/v1/tasks", new
        {
            bodyId = _body, resolutionId = _resolution, titleAr = "x", assigneeType = "org_unit", assigneeOrgUnitId = _unit, executorUserId = _u["emp"], accountableUserId = _u["emp"], dueDate = _due,
        }), 400, "validation", "المتابع لا يكون المنفذ");
        await Check.Problem(await (await As("m1")).PostAsync("/api/v1/tasks", body), 403, "forbidden-action", "العضو لا يُسند مهام");
        var key = Key();
        var created = await Check.Status(await sec.PostOnceAsync("/api/v1/tasks", body, key), 201, "إنشاء وإسناد");
        _tUnit = created.G("id");
        Check.Equal(_tUnit, (await Check.Status(await sec.PostOnceAsync("/api/v1/tasks", body, key), 201, "إعادة الطلب")).G("id"), "لا تكرار بإعادة المحاولة");
        Check.That(created.S("status") == "assigned" && created.S("referenceNo").StartsWith($"P4-COMM-T-{Year}-"), "مسندة برقم مرجعي");

        var card = await Card(_tUnit);
        var t = card["task"]!;
        Check.That(t.S("sourceType") == "decision" && t["resolutionNumber"] is not null && t.S("meetingReference").StartsWith("P4-COMM-"), "المصدر: القرار والاجتماع");
        Check.That(t.S("executorName") == "م4 emp" && t.S("executingUnitName").Contains("تقنية المعلومات"), "المنفذ المسؤول والإدارة المنفذة");
        Check.Equal(_u["fo"], t.G("accountableUserId"), "مسؤول المتابعة داخل المكتب");
        Check.That(t["assignedAt"] is not null && t.S("priority") == "high" && t["verificationRequired"]!.GetValue<bool>(), "تاريخ الإسناد والأولوية والتحقق");
        Check.Equal(2, card["deliverables"]!.AsArray().Count, "المخرجات المطلوبة");
        Check.That(card["deliverables"]![0]!.S("acceptanceCriteria").Contains("نطاق العمل"), "معايير القبول");
        Check.That((await NotificationTypes("emp")).Contains("task_assigned") && (await NotificationTypes("mgr")).Contains("task_assigned"), "إشعار المنفذ ومدير الإدارة");

        // مهمة لعضو، وتحقق العضوية والحساب الداخلي
        _tMember = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _body, resolutionId = _resolution, titleAr = "إعداد تقرير المؤشرات الربعية", assigneeType = "member", assigneeUserId = _u["m1"], dueDate = _due, verificationRequired = false }), 201, "مهمة عضو")).G("id");
        await Check.Problem(await sec.PostAsync("/api/v1/tasks", new { bodyId = _body, titleAr = "y", assigneeType = "member", assigneeUserId = _u["emp"], dueDate = _due }), 400, "validation", "ليس عضوًا");
        await Check.Problem(await sec.PostAsync("/api/v1/tasks", new { bodyId = _body, titleAr = "y", assigneeType = "employee", assigneeUserId = _u["extA"], dueDate = _due }), 400, "validation", "حساب خارجي ليس موظفًا");
        await Check.Problem(await sec.PostAsync("/api/v1/tasks", new { bodyId = _body, titleAr = "y", assigneeType = "employee", assigneeUserId = _u["emp"], dueDate = Today.AddDays(-1) }), 400, "validation", "استحقاق في الماضي");
        _tEsc = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _body, titleAr = "تحديث سجل الأصول التقنية", assigneeType = "employee", assigneeUserId = _u["emp"], accountableUserId = _u["fo"], dueDate = _due }), 201, "مهمة موظف")).G("id");
        Check.Equal("manual", (await Card(_tEsc))["task"].S("sourceType"), "مهمة يدوية بلا قرار");
    }

    [Test("P4-02", "إسناد مهمة إلى جهة خارجية: المتابع ضابط الاتصال افتراضيًا، والتحقق إلزامي، وموجز للجهة", 2)]
    public async Task CreateExternal()
    {
        var sec = await As("sec");
        var a = await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        {
            bodyId = _body, resolutionId = _resolution, titleAr = "تزويد المكتب ببيانات الخدمات البلدية المرقمنة", assigneeType = "external_entity", assigneeExternalEntityId = _entityA,
            dueDate = _due, verificationRequired = false, shareResolutionWithEntity = true, externalBrief = "نأمل تزويدنا ببيانات الخدمات وفق النموذج المرفق",
            deliverables = new[] { new { title = "ملف البيانات", acceptanceCriteria = "وفق النموذج المعتمد ومكتمل الحقول", required = true } },
        }), 201, "مهمة جهة أ");
        _tEntityA = a.G("id");
        var t = (await Card(_tEntityA))["task"]!;
        Check.Equal(_u["fo"], t.G("accountableUserId"), "المتابع الافتراضي ضابط الاتصال");
        Check.That(t["verificationRequired"]!.GetValue<bool>(), "التحقق إلزامي للجهة الخارجية رغم طلب تعطيله");
        _tEntityB = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        {
            bodyId = _body, resolutionId = _resolution, titleAr = "إعداد خطة ربط بيانات الإسكان", assigneeType = "external_entity", assigneeExternalEntityId = _entityB, dueDate = _due,
            deliverables = new[] { new { title = "خطة الربط", acceptanceCriteria = "معتمدة من الجهة", required = true } },
        }), 201, "مهمة جهة ب")).G("id");
    }

    // ============================ دورة التنفيذ والتحقق ============================

    [Test("P4-03", "التنفيذ والتحقق: الاستلام، والتحديث، ورفع المخرج لا يُكمل المهمة، والإعادة، ثم التحقق والإغلاق من غير المنفذ", 3)]
    public async Task ExecutionAndVerification()
    {
        var emp = await As("emp");
        var fo = await As("fo");
        await Check.Problem(await (await As("outsider")).PostAsync($"/api/v1/tasks/{_tUnit}/actions/accept", new { }), 404, "not-found", "من خارج المهمة");
        Check.Equal("accepted", (await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/accept", new { note = "استلمنا التكليف" }), 200, "تأكيد الاستلام")).S("status"), "مقبولة");
        Check.That((await NotificationTypes("fo")).Contains("task_accepted"), "المتابع أُشعر بالاستلام");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/updates", new { progressPct = 40, note = "اكتملت المسودة الأولى من الكراسة" }), 204, "تحديث التقدم");
        var card = await Card(_tUnit);
        Check.That(card["task"].S("status") == "in_progress" && I(card["task"], "progressPct") == 40, "قيد التنفيذ 40٪");
        Check.Equal("in_execution", (await (await As("sec")).GetJsonAsync($"/api/v1/resolutions/{_resolution}"))["resolution"].S("status"), "القرار قيد التنفيذ ببدء أول مهمة");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/updates", new { progressPct = 100, note = "انتهينا" }), 400, "validation", "100٪ لا تُسجل بالتحديث");

        var d = card["deliverables"]!.AsArray();
        var doc1 = (await Check.Status(await Upload(emp, _tUnit, "rfp.pdf"), 201, "رفع ملف")).G("id");
        Check.Equal("in_progress", (await Card(_tUnit))["task"].S("status"), "رفع الملف وحده لا يغير الحالة");
        await Check.Status(await emp.PostOnceAsync($"/api/v1/tasks/deliverables/{d[0].G("id")}/submissions", new { documentIds = new[] { doc1 }, note = "الكراسة كاملة" }, Key()), 201, "تقديم المخرج 1");
        card = await Card(_tUnit);
        Check.That(card["task"].S("status") == "in_progress" && card["deliverables"]![0].S("status") == "submitted", "المخرج مقدم والمهمة لم تكتمل");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/complete", new { note = "أنجزنا المطلوب" }), 409, "verification-required", "لا إقرار ذاتي لمهمة تتطلب تحققًا");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/submit", new { note = "رفع للمراجعة" }), 409, "deliverables-missing", "مخرج إلزامي لم يُقدم");
        var doc2 = (await Check.Status(await Upload(emp, _tUnit, "cost.pdf"), 201, "رفع ملف 2")).G("id");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/deliverables/{d[1].G("id")}/submissions", new { documentIds = new[] { doc2 } }), 201, "تقديم المخرج 2");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/submit", new { note = "x" }), 400, "validation", "ملخص الإنجاز إلزامي");
        Check.Equal("pending_review", (await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/submit", new { note = "اكتملت الكراسة وتقدير التكلفة" }), 200, "رفع للمراجعة")).S("status"), "بانتظار المراجعة");
        Check.That((await NotificationTypes("fo")).Contains("task_submitted") && (await NotificationTypes("sec")).Contains("task_submitted"), "تنبيه رفع المخرج للمراجعة");

        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new { }), 403, "forbidden-action", "المنفذ لا يملك التحقق");
        await Check.Problem(await (await As("mgr")).PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new { }), 403, "forbidden-action", "مدير الإدارة المنفذة لا يتحقق");
        await Check.Problem(await fo.PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new { }), 409, "deliverable-not-accepted", "لا إغلاق قبل قبول المخرجات");
        await Check.Problem(await fo.PostAsync($"/api/v1/tasks/{_tUnit}/actions/return", new { }), 400, "validation", "سبب الإعادة إلزامي");
        Check.Equal("in_progress", (await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tUnit}/actions/return", new
        {
            reason = "تقدير التكلفة يحتاج عرض سعر ثالث",
            deliverableDecisions = new object[] { new { deliverableId = d[0].G("id"), decision = "accepted" }, new { deliverableId = d[1].G("id"), decision = "rejected", note = "عرضان فقط" } },
        }), 200, "إعادة للاستكمال")).S("status"), "عادت قيد التنفيذ");
        card = await Card(_tUnit);
        Check.That(card["deliverables"]![0].S("status") == "accepted" && card["deliverables"]![1].S("status") == "rejected", "قرار لكل مخرج");
        Check.That((await NotificationTypes("emp")).Contains("task_returned"), "المنفذ أُشعر بالإعادة");
        var doc3 = (await Check.Status(await Upload(emp, _tUnit, "cost-v2.pdf"), 201, "رفع 3")).G("id");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/deliverables/{d[1].G("id")}/submissions", new { documentIds = new[] { doc3 }, note = "أُضيف العرض الثالث" }), 201, "إعادة تقديم");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tUnit}/actions/submit", new { note = "استُكمل تقدير التكلفة" }), 200, "رفع ثانٍ");
        var v = await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tUnit}/actions/verify", new
        { note = "مطابق لمعايير القبول", deliverableDecisions = new[] { new { deliverableId = d[1].G("id"), decision = "accepted", note = (string?)null } } }), 200, "التحقق والإغلاق");
        Check.Equal("completed", v.S("status"), "مكتملة");
        card = await Card(_tUnit);
        var t = card["task"]!;
        Check.That(t.S("completionMethod") == "verified" && t.S("verifiedByName") == "م4 fo" && I(t, "progressPct") == 100 && t["completedAt"] is not null, "تحقق موثق: من وكيف ومتى");
        Check.Equal(2, card["reviews"]!.AsArray().Count, "سجل قرارات التحقق");
        Check.That(card["updates"]!.AsArray().Any(u => u.S("updateType") == "deliverable") && card["updates"]!.AsArray().Count(u => u.S("updateType") == "status_change") >= 5, "سجل التحديثات");

        // مهمة لا تتطلب تحققًا: إقرار الإنجاز بملاحظة إثبات لا برفع ملف
        var m1 = await As("m1");
        await Check.Status(await m1.PostAsync($"/api/v1/tasks/{_tMember}/actions/start", new { }), 200, "بدء");
        await Check.Problem(await m1.PostAsync($"/api/v1/tasks/{_tMember}/actions/complete", new { }), 400, "validation", "الإقرار يحتاج إثباتًا");
    }

    [Test("P4-04", "طلب التمديد وأسباب التأخير: البت لصاحب الصلاحية في السياسة، ويحفظ الموعد الأصلي ويعيد مستوى التصعيد", 4)]
    public async Task Extension()
    {
        var m1 = await As("m1");
        var req = new { requestedDueDate = _due.AddDays(14), delayCause = "data_availability", reason = "تأخر وصول بيانات الربع من الإدارات" };
        var e = await Check.Status(await m1.PostOnceAsync($"/api/v1/tasks/{_tMember}/extension-requests", req, Key()), 201, "طلب تمديد");
        await Check.Problem(await m1.PostAsync($"/api/v1/tasks/{_tMember}/extension-requests", req), 409, "extension-pending", "طلب واحد معلق");
        Check.That((await NotificationTypes("chair")).Contains("extension_requested"), "تنبيه صاحب البت عند طلب التمديد");
        var eid = e.G("id");
        await Check.Problem(await m1.PostAsync($"/api/v1/tasks/extension-requests/{eid}/decision", new { decision = "approved" }), 403, "forbidden-action", "العضو لا يبت");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/tasks/extension-requests/{eid}/decision", new { decision = "approved" }), 403, "not-extension-approver", "السياسة تحصر البت في الرئيس");
        var r = await Check.Status(await (await As("chair")).PostAsync($"/api/v1/tasks/extension-requests/{eid}/decision", new { decision = "approved", approvedDueDate = _due.AddDays(7), note = "يُمدد أسبوعًا" }), 200, "موافقة");
        Check.Equal(_due.AddDays(7).ToString("yyyy-MM-dd"), r.S("dueDate"), "موعد جديد بقدر ما وافق عليه");
        var t = (await Card(_tMember))["task"]!;
        Check.That(t.S("dueDate") == _due.AddDays(7).ToString("yyyy-MM-dd") && t.S("originalDueDate") == _due.ToString("yyyy-MM-dd"), "الموعد الأصلي محفوظ");
        Check.Equal("تأخر وصول بيانات الربع من الإدارات", t.S("delayReason"), "سبب التأخير على البطاقة");
        var x = (await Card(_tMember))["extensions"]![0]!;
        Check.That(x.S("status") == "approved" && x.S("delayCause") == "data_availability" && x.S("decidedBy") == "م4 chair", "سجل الطلب والبت");
        Check.That((await NotificationTypes("m1")).Contains("extension_decided"), "مقدم الطلب أُشعر");
    }

    [Test("P4-05", "التنبيهات والتصعيد بمستويات السياسة: قبل الاستحقاق، وعند التأخر، ودون تكرار، مع تسجيل المتلقين وإقرارهم، والتصعيد اليدوي", 5)]
    public async Task Escalation()
    {
        await Check.Problem(await (await As("sec")).PostAsync("/api/v1/tasks/escalations/run", new { }), 403, "forbidden-action", "التشغيل اليدوي لمسؤول التهيئة");
        // قبل الاستحقاق بيوم: تنبيه المستوى 0 للمنفذ والمتابع
        var r0 = await RunEscalations(_due.AddDays(-1));
        Check.That(I(r0, "reminders") >= 1, "تنبيه قبل الاستحقاق");
        var esc = (await Card(_tEsc))["escalations"]!.AsArray();
        Check.That(esc.Count == 1 && esc[0].S("kind") == "reminder" && I(esc[0], "level") == 0, "مسجل تنبيهًا");
        var recipients = esc[0]!["recipients"]!.AsArray();
        Check.That(recipients.Any(x => x.S("role") == "assignee" && x.G("userId") == _u["emp"]) && recipients.Any(x => x.S("role") == "accountable" && x.G("userId") == _u["fo"]), "المتلقون وصفاتهم");
        Check.That((await NotificationTypes("emp")).Contains("task_due_soon"), "إشعار قرب الاستحقاق");
        await RunEscalations(_due.AddDays(-1));
        Check.Equal(1, (await Card(_tEsc))["escalations"]!.AsArray().Count, "لا تكرار لنفس الفترة");

        await RunEscalations(_due.AddDays(1));
        var t = await Card(_tEsc);
        Check.That(I(t["task"], "escalationLevel") == 1 && t["escalations"]!.AsArray().Any(x => x.S("kind") == "escalation" && I(x, "level") == 1), "تصعيد المستوى 1 عند التأخر");
        Check.That((await NotificationTypes("fo")).Contains("task_overdue"), "تنبيه التأخير");
        await RunEscalations(_due.AddDays(4));
        t = await Card(_tEsc);
        var l2 = t["escalations"]!.AsArray().First(x => I(x, "level") == 2)!;
        Check.That(l2["recipients"]!.AsArray().Any(x => x.S("role") == "secretary") && l2["recipients"]!.AsArray().Any(x => x.S("role") == "assignee_org_unit_manager" && x.G("userId") == _u["mgr"]),
            "المستوى 2: أمين السر ومدير إدارة الموظف");
        Check.That(l2["recipients"]!.AsArray().First(x => x.S("role") == "assignee_org_unit_manager").S("unit").Contains("تقنية المعلومات"), "الجهة التي تلقت التصعيد");
        await RunEscalations(_due.AddDays(7));
        t = await Card(_tEsc);
        Check.Equal(3, I(t["task"], "escalationLevel"), "المستوى 3");
        Check.That(t["escalations"]!.AsArray().First(x => I(x, "level") == 3)["recipients"]!.AsArray().Any(x => x.S("role") == "executive" && x.G("userId") == _u["exec"]), "الإدارة العليا تلقت التصعيد");
        var dash = await (await As("gov")).GetJsonAsync($"/api/v1/tasks/dashboard?bodyId={_body}");
        Check.That(dash["executive"]!.AsArray().Any(x => x.G("id") == _tEsc), "في قائمة تصعيد الإدارة العليا");
        // مهمة متوقفة لا تُصعّد؛ والمكتملة لا تُصعّد
        Check.That(!(await Card(_tUnit))["escalations"]!.AsArray().Any(x => x.S("kind") == "escalation"), "المكتملة لا تُصعّد");

        // إقرار المتلقي بالتصعيد
        var mine = t["escalations"]!.AsArray().First(x => I(x, "level") == 2)!;
        await Check.Problem(await (await As("outsider")).PostAsync($"/api/v1/tasks/escalations/{mine.G("id")}/acknowledge", new { note = "تمت المعالجة" }), 404, "not-found", "من لا يرى المهمة");
        await Check.Problem(await (await As("chair")).PostAsync($"/api/v1/tasks/escalations/{mine.G("id")}/acknowledge", new { note = "تمت المعالجة" }), 403, "not-recipient", "غير المتلقي");
        await Check.Status(await (await As("mgr")).PostAsync($"/api/v1/tasks/escalations/{mine.G("id")}/acknowledge", new { note = "وُجّه الموظف بالإنجاز خلال يومين" }), 204, "إقرار مدير الإدارة");
        Check.That((await Card(_tEsc))["escalations"]!.AsArray().First(x => x.G("id") == mine.G("id")).S("acknowledgedBy") == "م4 mgr", "الإقرار مسجل");

        // التصعيد اليدوي بسبب
        await Check.Problem(await (await As("m1")).PostAsync($"/api/v1/tasks/{_tEsc}/escalate", new { reason = "تصعيد مهم" }), 403, "forbidden-action", "العضو يرى ولا يصعّد");
        await Check.Problem(await (await As("emp")).PostAsync($"/api/v1/tasks/{_tEsc}/escalate", new { reason = "تصعيد مهم" }), 403, "forbidden-action", "المنفذ لا يصعّد");
        var man = await Check.Status(await (await As("sec")).PostAsync($"/api/v1/tasks/{_tEsc}/escalate", new { reason = "تأخر يؤثر على خطة الرقمنة", notify = new[] { "chair" } }), 200, "تصعيد يدوي");
        Check.That(I(man, "level") == 4 && I(man, "recipients") == 1, "المستوى التالي للمتلقي المحدد");
    }

    // ============================ بوابة الجهات الخارجية ============================

    [Test("P4-06", "البوابة مفعلة: دخول موثق، وتكليفات جهته فقط، وتأكيد الاستلام، والتحديث، والمخرجات، والرد، والتمديد — دون محاضر أو قرارات أو مرفقات غير مشاركة", 6)]
    public async Task PortalEnabled()
    {
        var anon = new ApiClient(Url);
        await Check.Problem(await anon.LoginAsync("p4-extA@test.jsdo.gov.sa", Pw), 403, "portal-disabled", "لا دخول والبوابة معطلة");
        var sec = await As("sec");
        var internalDoc = (await Check.Status(await Upload(sec, _tEntityA, "internal-memo.pdf"), 201, "مستند داخلي")).G("id");
        var sharedDoc = (await Check.Status(await Upload(sec, _tEntityA, "data-template.pdf", share: true), 201, "نموذج مشارك")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/tasks/{_tEntityA}/comments", new { text = "ملاحظة داخلية: الجهة متأخرة عادة", shareWithEntity = false }), 201, "تعليق داخلي");
        await Check.Status(await sec.PostAsync($"/api/v1/tasks/{_tEntityA}/comments", new { text = "يرجى الالتزام بالنموذج المرفق", shareWithEntity = true }), 201, "تعليق مشارك");
        await Check.Status(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/EXTERNAL_PORTAL_ENABLED", new { value = "true", reason = "تشغيل بوابة الجهات" }), 200, "تفعيل البوابة");

        var ext = await As("extA");
        await Check.Problem(await ext.GetAsync("/api/v1/portal/tasks"), 403, "password-change-required", "كلمة مرور مؤقتة");
        await Check.Problem(await ext.PostAsync("/api/v1/auth/password", new { currentPassword = Pw, newPassword = "short" }), 400, "validation", "سياسة كلمة المرور");
        await Check.Status(await ext.PostAsync("/api/v1/auth/password", new { currentPassword = Pw, newPassword = ExtPw }), 204, "تغيير كلمة المرور");
        var me = await ext.GetJsonAsync("/api/v1/me");
        Check.That(me["portalUser"]!.GetValue<bool>() && me["user"]!.S("externalEntityName").Contains("أمانة"), "حساب بوابة مرتبط بجهته");

        var list = (await ext.GetJsonAsync("/api/v1/portal/tasks")).Items();
        Check.That(list.Count == 1 && list[0].G("id") == _tEntityA, "تكليفات جهته فقط");
        await Check.Problem(await ext.GetAsync($"/api/v1/portal/tasks/{_tEntityB}"), 404, "not-found", "تكليف جهة أخرى");
        await Check.Problem(await ext.GetAsync($"/api/v1/tasks/{_tEntityA}"), 404, "not-found", "البطاقة الداخلية");
        await Check.Problem(await ext.GetAsync($"/api/v1/resolutions/{_resolution}"), 404, "not-found", "سجل القرار");
        await Check.Problem(await ext.GetAsync($"/api/v1/minutes/{_minutes}"), 404, "not-found", "المحضر");
        await Check.Problem(await ext.GetAsync($"/api/v1/minutes/{_minutes}/pdf"), 404, "not-found", "PDF المحضر");
        await Check.Problem(await ext.GetAsync($"/api/v1/meetings/{_meeting}"), 404, "not-found", "الاجتماع");
        Check.Equal(0, (await ext.GetJsonAsync("/api/v1/tasks")).Items().Count, "لا قوائم داخلية");
        await Check.Problem(await ext.GetAsync($"/api/v1/documents/{internalDoc}/content"), 404, "not-found", "مرفق غير مشارك");
        await Check.Status(await ext.GetAsync($"/api/v1/documents/{sharedDoc}/content"), 200, "مرفق مشارك");
        var docs = (await ext.GetJsonAsync($"/api/v1/documents?ownerType=task&ownerId={_tEntityA}")).AsArray();
        Check.That(docs.Count == 1 && docs[0].G("id") == sharedDoc, "قائمة المرفقات: المشارك فقط");

        var card = await ext.GetJsonAsync($"/api/v1/portal/tasks/{_tEntityA}");
        Check.That(card["task"]!["resolutionText"] is not null && card["task"]!.S("externalBrief").Contains("النموذج"), "نص القرار المشارك صراحة والموجز");
        var updates = card["updates"]!.AsArray();
        Check.That(updates.Any(u => u.S("note").Contains("الالتزام بالنموذج")) && !updates.Any(u => (u?["note"]?.GetValue<string>() ?? "").Contains("ملاحظة داخلية")), "الملاحظات المشاركة فقط");
        Check.That(card["actions"]!.AsArray().Select(a => a.S("action")).OrderBy(a => a).SequenceEqual(["accept", "reject", "start"]), "إجراءات الجهة المتاحة");

        Check.Equal("accepted", (await Check.Status(await ext.PostAsync($"/api/v1/portal/tasks/{_tEntityA}/actions/accept", new { note = "تم الاستلام" }), 200, "تأكيد الاستلام")).S("status"), "مقبولة");
        await Check.Problem(await ext.PostAsync($"/api/v1/portal/tasks/{_tEntityA}/actions/verify", new { }), 404, "not-found", "لا تحقق عبر البوابة");
        await Check.Problem(await ext.PostAsync($"/api/v1/portal/tasks/{_tEntityA}/actions/cancel", new { reason = "إلغاء" }), 404, "not-found", "لا إلغاء عبر البوابة");
        await Check.Status(await ext.PostAsync($"/api/v1/portal/tasks/{_tEntityA}/progress", new { progressPct = 60, note = "جُمعت بيانات 60٪ من الخدمات", delayReason = (string?)null }), 200, "تحديث الحالة");
        var file = (await Check.Status(await Upload(ext, _tEntityA, "services-data.pdf"), 201, "رفع المخرج")).G("id");
        var deliverable = card["deliverables"]![0]!.G("id");
        await Check.Status(await ext.PostOnceAsync($"/api/v1/portal/deliverables/{deliverable}/submissions", new { documentIds = new[] { file }, note = "ملف البيانات" }, Key()), 201, "تقديم المخرج");
        await Check.Status(await ext.PostOnceAsync($"/api/v1/portal/tasks/{_tEntityA}/comments", new { text = "سنستكمل بقية الخدمات خلال أسبوع" }, Key()), 201, "رد الجهة");
        var ex = await Check.Status(await ext.PostOnceAsync($"/api/v1/portal/tasks/{_tEntityA}/extension-requests", new
        { requestedDueDate = _due.AddDays(10), delayCause = "external_party", reason = "انتظار ربط نظام البلديات" }, Key()), 201, "طلب تمديد عبر البوابة");
        Check.Equal("submitted", (await ext.GetJsonAsync($"/api/v1/portal/tasks/{_tEntityA}"))["deliverables"]![0].S("status"), "المخرج مقدم");
        var internalView = await Card(_tEntityA, "fo");
        Check.That(internalView["updates"]!.AsArray().Any(u => u.S("channel") == "portal" && u["authorIsExternal"]!.GetValue<bool>()), "التحديثات موسومة بقناة البوابة");
        Check.Equal("portal", internalView["extensions"]![0].S("channel"), "طلب التمديد عبر البوابة");
        Check.That((await NotificationTypes("fo")).Contains("task_comment"), "المتابع أُشعر برد الجهة");
        Check.Equal("pending_review", (await Check.Status(await ext.PostAsync($"/api/v1/portal/tasks/{_tEntityA}/actions/submit", new { note = "سُلمت البيانات وفق النموذج" }), 200, "رفع للمراجعة")).S("status"), "بانتظار مراجعة المكتب");
        _ = ex;
    }

    [Test("P4-07", "تعطيل البوابة لا يعطل إدارة المهام الداخلية: المتابع يسجل ردود الجهة الواردة رسميًا، والتحقق يستمر", 7)]
    public async Task PortalDisabled()
    {
        var admin = ctx.AdminClient!;
        await Check.Problem(await admin.PutAsync("/api/v1/admin/features/EXTERNAL_PORTAL_ENABLED", new { value = "false", reason = "إيقاف البوابة للصيانة" }), 409, "confirmation-required", "يُطلب تأكيد الأثر");
        await Check.Status(await admin.PutAsync("/api/v1/admin/features/EXTERNAL_PORTAL_ENABLED", new { value = "false", reason = "إيقاف البوابة للصيانة", confirmImpact = true }), 200, "تعطيل مؤكد");
        var ext = await As("extA");
        await Check.Problem(await ext.GetAsync("/api/v1/portal/tasks"), 404, "feature-disabled", "البوابة معطلة على الخادم");
        await Check.Problem(await ext.GetAsync($"/api/v1/documents?ownerType=task&ownerId={_tEntityA}"), 404, "not-found", "لا وصول للمرفقات");
        await Check.Problem(await new ApiClient(Url).LoginAsync("p4-extA@test.jsdo.gov.sa", ExtPw), 403, "portal-disabled", "لا دخول جديد");

        // المهام الداخلية تعمل: التحقق من مخرج الجهة أ
        var fo = await As("fo");
        var card = await Card(_tEntityA, "fo");
        var did = card["deliverables"]![0]!.G("id");
        Check.Equal("completed", (await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tEntityA}/actions/verify", new
        { note = "البيانات مكتملة", deliverableDecisions = new[] { new { deliverableId = did, decision = "accepted", note = (string?)null } } }), 200, "تحقق والبوابة معطلة")).S("status"), "أُغلقت");

        // الجهة ب: الرد ورد بخطاب رسمي؛ المتابع يسجله نيابة عنها
        await Check.Problem(await (await As("emp")).PostAsync($"/api/v1/tasks/{_tEntityB}/external-updates", new
        { kind = "acknowledged", officialChannel = "letter", receivedOn = Today, note = "x استلام" }), 404, "not-found", "غير المتابع");
        var r = await Check.Status(await fo.PostOnceAsync($"/api/v1/tasks/{_tEntityB}/external-updates", new
        { kind = "acknowledged", officialChannel = "letter", reference = "صادر الإسكان 4471", receivedOn = Today, note = "أكدت الجهة استلام التكليف بخطابها" }, Key()), 201, "تسجيل الاستلام");
        Check.Equal("accepted", r.S("status"), "مقبولة نيابة عن الجهة");
        await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tEntityB}/external-updates", new
        { kind = "progress", officialChannel = "email", reference = "بريد 2026-10-20", receivedOn = Today, note = "أفادت الجهة بإنجاز 30٪", progressPct = 30 }), 201, "تسجيل التقدم");
        var doc = (await Check.Status(await Upload(fo, _tEntityB, "housing-plan.pdf"), 201, "مرفق وارد")).G("id");
        var dB = (await Card(_tEntityB, "fo"))["deliverables"]![0]!.G("id");
        await Check.Status(await fo.PostAsync($"/api/v1/tasks/{_tEntityB}/external-updates", new
        { kind = "deliverable", officialChannel = "correspondence_system", reference = "وارد 9921", receivedOn = Today, note = "ورد مخرج الجهة", deliverableId = dB, documentIds = new[] { doc } }), 201, "تسجيل المخرج");
        var b = await Card(_tEntityB, "fo");
        Check.That(b["task"].S("status") == "in_progress" && I(b["task"], "progressPct") == 30 && b["deliverables"]![0].S("status") == "submitted", "الحالة والمخرج من القناة الرسمية");
        var rec = b["updates"]!.AsArray().Where(u => u.S("updateType") == "external_update").ToList();
        Check.That(rec.Count == 3 && rec.All(u => u.S("channel") == "recorded" && u.S("recordedForEntity").Contains("الإسكان")), "موثقة بالقناة والمرجع والجهة");
        Check.That(rec.Any(u => u.S("officialChannel") == "letter" && u.S("reference") == "صادر الإسكان 4471"), "مرجع الخطاب الوارد");
    }

    // ============================ الحالات القابلة للتهيئة ============================

    [Test("P4-08", "الحالات والصلاحيات قابلة للتهيئة مع قيود حامية: حالة انتظار مضافة، وتغيير صلاحية انتقال، ولا اكتمال خارج التحقق", 8)]
    public async Task ConfigurableWorkflow()
    {
        var gov = await As("gov");
        var wf = await gov.GetJsonAsync("/api/v1/tasks/workflow");
        var codes = wf["statuses"]!.AsArray().Select(s => s.S("code")).ToList();
        foreach (var c in new[] { "new", "assigned", "accepted", "in_progress", "on_hold", "pending_review", "completed", "overdue", "rejected", "cancelled" })
            Check.That(codes.Contains(c), "الحالة " + c);
        Check.Equal("derived", wf["statuses"]!.AsArray().First(s => s.S("code") == "overdue").S("category"), "المتأخرة مشتقة لا تُخزن");
        await Check.Problem(await (await As("sec")).PostAsync("/api/v1/tasks/workflow/statuses", new
        { code = "awaiting_input", nameAr = "بانتظار مدخلات", nameEn = "Awaiting input", category = "waiting", reason = "حالة جديدة" }), 403, "forbidden-action", "أمين السر لا يهيئ الدورة");
        await Check.Status(await gov.PostAsync("/api/v1/tasks/workflow/statuses", new
        { code = "awaiting_input", nameAr = "بانتظار مدخلات جهة أخرى", nameEn = "Awaiting input", category = "waiting", color = "gold", reason = "حالة انتظار تطلبها اللجان" }), 201, "إضافة حالة");
        await Check.Status(await gov.PostAsync("/api/v1/tasks/workflow/transitions", new
        { action = "await_input", from = "in_progress", to = "awaiting_input", nameAr = "انتظار مدخلات", nameEn = "Await input", permission = "task.update", actors = new[] { "executor", "follow_up" }, requiresReason = true, active = true, reason = "انتقال جديد" }), 201, "انتقال للانتظار");
        await Check.Status(await gov.PostAsync("/api/v1/tasks/workflow/transitions", new
        { action = "input_received", from = "awaiting_input", to = "in_progress", nameAr = "وصلت المدخلات", nameEn = "Input received", permission = "task.update", actors = new[] { "executor", "follow_up" }, requiresReason = false, active = true, reason = "انتقال العودة" }), 201, "انتقال العودة");
        await Check.Problem(await gov.PostAsync("/api/v1/tasks/workflow/transitions", new
        { action = "finish_fast", from = "in_progress", to = "completed", nameAr = "إنهاء", nameEn = "Finish", permission = "task.update", actors = Array.Empty<string>(), requiresReason = false, active = true, reason = "تجاوز التحقق" }), 400, "validation", "لا انتقال مضاف إلى الاكتمال");
        var verify = wf["transitions"]!.AsArray().First(t => t.S("action") == "verify");
        await Check.Problem(await gov.PutAsync($"/api/v1/tasks/workflow/transitions/{verify.G("id")}", new
        { nameAr = "تحقق", nameEn = "Verify", permission = "task.verify", actors = Array.Empty<string>(), requiresReason = false, active = false, reason = "تعطيل التحقق" }), 409, "locked-transition", "التحقق لا يُعطل");
        await Check.Problem(await gov.PutAsync($"/api/v1/tasks/workflow/transitions/{verify.G("id")}", new
        { nameAr = "تحقق", nameEn = "Verify", permission = "task.update", actors = Array.Empty<string>(), requiresReason = false, active = true, reason = "إضعاف التحقق" }), 409, "locked-transition", "التحقق لا يُمنح بصلاحية التحديث");

        var emp = await As("emp");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tEsc}/actions/start", new { }), 200, "بدء");
        await Check.Problem(await emp.PostAsync($"/api/v1/tasks/{_tEsc}/actions/await_input", new { }), 400, "validation", "السبب مطلوب حسب التهيئة");
        Check.Equal("awaiting_input", (await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tEsc}/actions/await_input", new { reason = "بانتظار بيانات الأصول من المالية" }), 200, "إلى حالة مضافة")).S("status"), "الحالة المضافة");
        var esc1 = (await Card(_tEsc))["escalations"]!.AsArray().Count;
        await RunEscalations(_due.AddDays(30));
        Check.Equal(esc1, (await Card(_tEsc))["escalations"]!.AsArray().Count, "حالة الانتظار توقف ساعة التصعيد");
        await Check.Status(await emp.PostAsync($"/api/v1/tasks/{_tEsc}/actions/input_received", new { }), 200, "العودة");

        // تغيير صلاحية الإيقاف: مدير المهام فقط
        var hold = wf["transitions"]!.AsArray().First(t => t.S("action") == "hold" && t.S("from") == "in_progress");
        await Check.Status(await (await As("fo")).PostAsync($"/api/v1/tasks/{_tEsc}/actions/hold", new { reason = "إيقاف للمراجعة" }), 200, "المتابع يوقف (التهيئة الافتراضية)");
        await Check.Status(await (await As("fo")).PostAsync($"/api/v1/tasks/{_tEsc}/actions/resume", new { }), 200, "استئناف");
        await Check.Status(await gov.PutAsync($"/api/v1/tasks/workflow/transitions/{hold.G("id")}", new
        { nameAr = "إيقاف مؤقت", nameEn = "Put on hold", permission = "task.manage", actors = new[] { "manager" }, requiresReason = true, active = true, reason = "الإيقاف بقرار أمين السر" }), 204, "تعديل صلاحية الإيقاف");
        await Check.Problem(await (await As("fo")).PostAsync($"/api/v1/tasks/{_tEsc}/actions/hold", new { reason = "إيقاف" }), 403, "forbidden-action", "المتابع لم يعد يوقف");
        Check.Equal("on_hold", (await Check.Status(await (await As("sec")).PostAsync($"/api/v1/tasks/{_tEsc}/actions/hold", new { reason = "إيقاف بقرار اللجنة" }), 200, "أمين السر يوقف")).S("status"), "متوقفة");
        var items = (await (await As("sec")).GetJsonAsync($"/api/v1/tasks?bodyId={_body}&status=awaiting_input")).Items();
        Check.Equal(0, items.Count, "التصفية بالحالات المهيأة");
    }

    // ============================ خطاب التكليف ============================

    [Test("P4-09", "خطاب التكليف: مسودة برقم مرجعي مرتبطة بالقرار، واعتماد من غير المعد، ولا إرسال دون تكامل فعلي، وتسجيل رقم الصادر يدويًا", 9)]
    public async Task AssignmentLetter()
    {
        var sec = await As("sec");
        var attach = (await Check.Status(await Upload(sec, _tEntityB, "spec.pdf"), 201, "مرفق الخطاب")).G("id");
        await Check.Problem(await (await As("m1")).PostAsync("/api/v1/tasks/letters", new { taskIds = new[] { _tEntityB } }), 403, "forbidden-action", "العضو لا يعد خطابات");
        await Check.Problem(await sec.PostAsync("/api/v1/tasks/letters", new { taskIds = new[] { _tEntityB, _tMember } }), 400, "validation", "جهة مكلفة واحدة لكل خطاب");
        var l = await Check.Status(await sec.PostOnceAsync("/api/v1/tasks/letters", new { taskIds = new[] { _tEntityB }, recipientAttention = "مدير فرع الوزارة", attachmentIds = new[] { attach } }, Key()), 201, "مسودة");
        var lid = l.G("id");
        Check.That(l.S("referenceNo").StartsWith($"P4-COMM-L-{Year}-") && l.S("status") == "draft" && l.G("resolutionId") == _resolution, "رقم مرجعي ومرتبط بالقرار");
        Check.That(l.S("recipientName").Contains("الإسكان") && l.S("bodyText").Contains("نأمل التكرم بتنفيذ") && l.S("dueDate") == _due.ToString("yyyy-MM-dd"), "بيانات الجهة والمطلوب والموعد");
        Check.That(!l["officiallySent"]!.GetValue<bool>(), "ليس صادرًا");
        var draftPdf = await sec.GetBytesAsync($"/api/v1/tasks/letters/{lid}/pdf");
        Check.That(draftPdf.Length > 5000 && System.Text.Encoding.ASCII.GetString(draftPdf, 0, 5) == "%PDF-", "PDF المسودة");
        await Check.Status(await sec.PatchAsync($"/api/v1/tasks/letters/{lid}", new { subject = "تكليف بإعداد خطة ربط بيانات الإسكان" }), 200, "تعديل المسودة");
        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/approve"), 403, "separation-of-duties", "المعد لا يعتمد");
        Check.Equal("approved", (await Check.Status(await (await As("chair")).PostAsync($"/api/v1/tasks/letters/{lid}/approve"), 200, "اعتماد الرئيس")).S("status"), "معتمد");
        await Check.Problem(await sec.PatchAsync($"/api/v1/tasks/letters/{lid}", new { subject = "x" }), 409, "letter-locked", "المعتمد لا يُعدل");

        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/send"), 404, "feature-disabled", "التكامل معطل");
        await Check.Status(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/CORRESPONDENCE_INTEGRATION_ENABLED", new { value = "true", reason = "تجربة الإرسال" }), 200, "تفعيل التكامل");
        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/send"), 409, "correspondence-unavailable", "مفعّل دون تكامل جاهز: لا يُدّعى الإرسال");
        Check.Equal("approved", (await sec.GetJsonAsync($"/api/v1/tasks/letters/{lid}"))["letter"].S("status"), "الحالة لم تتغير");
        await Check.Status(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/CORRESPONDENCE_INTEGRATION_ENABLED", new { value = "false", reason = "إرجاع" }), 200, "إرجاع");

        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/record-dispatch", new { channel = "correspondence_system_manual", outgoingDate = Today }), 400, "validation", "رقم الصادر إلزامي");
        var d = await Check.Status(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/record-dispatch", new
        { channel = "correspondence_system_manual", outgoingNo = "ص/1448/3391", outgoingDate = Today, note = "أُرسل عبر نظام المراسلات" }), 200, "تسجيل الصدور");
        Check.That(d.S("status") == "dispatched_manual" && d["officiallySent"]!.GetValue<bool>() && d.S("outgoingNo") == "ص/1448/3391", "صدر برقم مسجل");
        var t = await Card(_tEntityB, "fo");
        Check.That(t["task"].S("correspondenceNo") == "ص/1448/3391" && t["letters"]!.AsArray().Count == 1, "رقم الصادر على المهمة");
        Check.That(t["updates"]!.AsArray().Any(u => u.S("updateType") == "letter" && u.S("note").Contains("ص/1448/3391")), "السجل الزمني");
        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/letters/{lid}/cancel", new { reason = "إلغاء بعد الصدور" }), 409, "invalid-transition", "لا يُلغى بعد الصدور");
        var finalPdf = await sec.GetBytesAsync($"/api/v1/tasks/letters/{lid}/pdf");
        Check.That(finalPdf.Length > 5000 && !finalPdf.SequenceEqual(draftPdf), "PDF المعتمد محفوظ");
    }

    // ============================ اللوحات ============================

    [Test("P4-10", "قوائم ولوحات المهام حسب المجلس والجهة والمنفذ والأولوية والحالة والاستحقاق، بنسب محسوبة من البيانات الفعلية", 10)]
    public async Task Dashboards()
    {
        // محاكاة مرور الوقت على مهمة واحدة: تجاوز موعدها
        await using (var app = new PgWireDataSource(ctx.AppConnectionString))
            await new Db(app).ExecuteAsync("UPDATE tasks.tasks SET due_date = (now() AT TIME ZONE 'Asia/Riyadh')::date - 3 WHERE id = @id", new { id = _tMember });
        var gov = await As("gov");
        var dash = await gov.GetJsonAsync($"/api/v1/tasks/dashboard?bodyId={_body}");
        var k = dash["kpis"]!;
        var all = (await gov.GetJsonAsync($"/api/v1/tasks?bodyId={_body}&pageSize=100")).Items().Where(t => t.S("status") != "new").ToList();
        var total = all.Count(t => t.S("statusCategory") != "closed");
        var completed = all.Count(t => t.S("status") == "completed");
        Check.Equal(total, I(k, "total"), "الإجمالي من البيانات");
        Check.Equal(completed, I(k, "completed"), "المكتملة");
        Check.Equal(2, completed, "مكتملتان فعلًا (الإدارة والجهة أ)");
        Check.Equal(1, I(k, "overdue"), "متأخرة واحدة");
        Check.Equal(Math.Round(100.0 * completed / total, 1), k["completionRate"]!.GetValue<double>(), "نسبة الإنجاز");
        Check.Equal(100.0, k["onTimeRate"]!.GetValue<double>(), "الإنجاز في الموعد");
        Check.That(dash["byStatus"]!.AsArray().Any(s => s.S("status") == "overdue" && I(s, "count") == 1), "مؤشر المتأخرة");
        var byExec = dash["byExecutor"]!.AsArray();
        Check.That(byExec.Any(x => x.S("kind") == "external_entity" && x.S("name").Contains("أمانة") && I(x, "completed") == 1), "حسب الجهة المنفذة");
        Check.That(byExec.Any(x => x.S("kind") == "member" && I(x, "overdue") == 1), "حسب المنفذ");
        Check.That(dash["byPriority"]!.AsArray().Any(x => x.S("key") == "high" && I(x, "completed") == 1), "حسب الأولوية");
        Check.That(dash["byBody"]!.AsArray().Single()!.G("key") == _body, "حسب المجلس");
        Check.That(dash["byDue"]!.AsArray().Any(x => x.S("bucket") == "overdue" && I(x, "count") == 1), "حسب الاستحقاق");
        Check.Equal(_tMember, dash["topOverdue"]!.AsArray()[0].G("id"), "الأكثر تأخرًا");
        var overdueList = (await gov.GetJsonAsync($"/api/v1/tasks?bodyId={_body}&status=overdue")).Items();
        Check.That(overdueList.Count == 1 && overdueList[0]["isOverdue"]!.GetValue<bool>() && I(overdueList[0], "daysOverdue") == 3, "قائمة المتأخرة");
        Check.Equal(1, (await gov.GetJsonAsync($"/api/v1/tasks?bodyId={_body}&externalEntityId={_entityA}")).Items().Count, "تصفية بالجهة");
        Check.Equal(1, (await gov.GetJsonAsync($"/api/v1/tasks?bodyId={_body}&priority=high")).Items().Count, "تصفية بالأولوية");
        var board = (await gov.GetJsonAsync($"/api/v1/tasks/board?bodyId={_body}")).AsArray();
        Check.That(board.Any(c => c.S("status") == "completed" && c["items"]!.AsArray().Count == 2), "لوحة كانبان حسب الحالة");
        // المستخدم يرى أرقام ما يحق له فقط
        var emp = await (await As("emp")).GetJsonAsync("/api/v1/tasks/dashboard");
        Check.That(I(emp["kpis"], "total") == 2, "المنفذ يرى مهامه فقط في اللوحة");
    }

    [Test("P4-11", "مهمة من بند اجتماع دون قرار، والإلغاء بسبب، والنظام يعمل والذكاء الاصطناعي والتوقيع والمراسلات والبوابة معطلة", 11)]
    public async Task OptionalModulesOff()
    {
        var f = await ctx.AdminClient!.GetJsonAsync("/api/v1/features");
        foreach (var key in new[] { "AI_ENABLED", "E_SIGNATURE_ENABLED", "EXTERNAL_PORTAL_ENABLED", "CORRESPONDENCE_INTEGRATION_ENABLED" })
            Check.Equal(false, f[key]!["enabled"]!.GetValue<bool>(), key + " معطل");
        var sec = await As("sec");
        var item = (await sec.GetJsonAsync($"/api/v1/meetings/{_meeting}"))["agenda"]![0]!.G("id");
        var t = await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _body, agendaItemId = item, titleAr = "متابعة ملاحظات البند", assigneeType = "employee", assigneeUserId = _u["emp"], accountableUserId = _u["fo"], dueDate = _due }), 201, "مهمة من بند");
        Check.Equal("agenda_item", (await Card(t.G("id")))["task"].S("sourceType"), "المصدر بند الاجتماع");
        await Check.Problem(await sec.PostAsync($"/api/v1/tasks/{t.G("id")}/actions/cancel", new { }), 400, "validation", "الإلغاء بسبب");
        Check.Equal("cancelled", (await Check.Status(await sec.PostAsync($"/api/v1/tasks/{t.G("id")}/actions/cancel", new { reason = "دُمجت في مهمة أخرى" }), 200, "إلغاء")).S("status"), "ملغاة");
        // إعادة الذكاء الاصطناعي كما كان (اختبار الاستمرارية بعد إعادة التشغيل)
        await Check.Status(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "إعادة الحالة بعد اختبارات المرحلة 4" }), 200, "إعادة AI");
    }
}
