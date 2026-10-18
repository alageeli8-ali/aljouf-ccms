using System.Diagnostics;
using System.Text;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;
using Ccms.Tests;

// الاستخدام: dotnet run --project src/backend/Ccms.Tests -- [report.md]
// يتطلب خادم PostgreSQL 16 وحسابًا إداريًا عبر CCMS_TEST_ADMIN_CS
// (الافتراضي: الخادم المحلي للتطوير على المنفذ 5433).

var adminCs = Environment.GetEnvironmentVariable("CCMS_TEST_ADMIN_CS")
    ?? "Host=127.0.0.1;Port=5433;Database=postgres;Username=postgres;Password=pgadmin_local_pw";
var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = adminCs };
var server = $"Host={b["Host"]};Port={b["Port"]}";
const string dbName = "ccms_it";
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // Debug أو Release
var hostDll = Path.Combine(repo, "src", "backend", "Ccms.Host", "bin", configuration, "net10.0", "Ccms.Host.dll");
var work = Directory.CreateTempSubdirectory("ccms-it-");

var ctx = new TestContext
{
    AppConnectionString = $"{server};Database={dbName};Username=ccms_app;Password=ccms_app_dev",
    OwnerConnectionString = $"{server};Database={dbName};Username=ccms_owner;Password=ccms_owner_dev",
    MigrationsPath = Path.Combine(repo, "db", "migrations"),
};

var sw = Stopwatch.StartNew();
Console.WriteLine($"قاعدة الاختبار: {dbName} — الترحيلات: {ctx.MigrationsPath}");
await TestDatabase.RecreateAsync(adminCs, dbName);

var baseEnv = new Dictionary<string, string>
{
    ["ConnectionStrings__App"] = ctx.AppConnectionString,
    ["ConnectionStrings__Migrations"] = ctx.OwnerConnectionString,
    ["Database__MigrationsPath"] = ctx.MigrationsPath,
    ["Storage__Path"] = Path.Combine(work.FullName, "storage"),
    ["DataProtection__KeysPath"] = Path.Combine(work.FullName, "keys"),
};

var results = new List<TestOutcome>();
await using var idp = new MockIdentityProvider();
await idp.StartAsync();
ctx.Idp = idp;

// المرحلة أ: التطبيق دون بيانات Entra
ctx.App = await AppInstance.StartAsync(hostDll, baseEnv);
await using (var admin = new PgWireDataSource(ctx.OwnerConnectionString))
    ctx.AdminId = await Ccms.Tests.AdminBootstrap.CreateAsync(new Db(admin));
results.AddRange(await Runner.RunAsync(new Suite(ctx)));
results.AddRange(await Runner.RunAsync(new Phase2Suite(ctx)));
results.AddRange(await Runner.RunAsync(new Phase3Suite(ctx)));
results.AddRange(await Runner.RunAsync(new Phase4Suite(ctx)));
results.AddRange(await Runner.RunAsync(new Phase6Suite(ctx)));
await using (var p7 = new Phase7Suite(ctx)) results.AddRange(await Runner.RunAsync(p7));
results.AddRange(await Runner.RunAsync(new Phase8BaselineSuite(ctx)));

// المرحلة 8: التطبيق بإعدادات تكامل تشير إلى خوادم وهمية (Graph، والمراسلات، وSMTP) على البيانات نفسها
await using (var fake = new FakeIntegrationServer())
await using (var smtp = new FakeSmtpServer())
{
    await fake.StartAsync();
    smtp.Start();
    var integEnv = new Dictionary<string, string>(baseEnv)
    {
        ["Integrations__Graph__TenantId"] = fake.TenantId,
        ["Integrations__Graph__ClientId"] = fake.ClientId,
        ["Integrations__Graph__AuthorityHost"] = fake.Root,
        ["Integrations__Graph__BaseUrl"] = fake.Root + "/v1.0",
        ["Secrets__Values__GRAPH_CLIENT_SECRET"] = fake.ClientSecret,
        ["Integrations__VideoMeetings__OrganizerUserId"] = fake.Organizer,
        ["Integrations__DocumentRepository__DriveId"] = fake.DriveId,
        ["Integrations__Correspondence__BaseUrl"] = fake.Root + "/corr",
        ["Integrations__Correspondence__Contract"] = "ccms-letters-v1",
        ["Secrets__Values__CORRESPONDENCE_API_KEY"] = fake.CorrespondenceKey,
        ["Integrations__Email__Provider"] = "smtp",
        ["Integrations__Email__SmtpHost"] = "127.0.0.1",
        ["Integrations__Email__SmtpPort"] = smtp.Port.ToString(),
        ["Integrations__Email__UseSsl"] = "false",
        ["Integrations__Email__From"] = "ccms@test.jsdo.gov.sa",
        ["Integrations__Policies__directory__TimeoutSeconds"] = "1",
        ["Integrations__Policies__directory__MaxAttempts"] = "2",
        ["Integrations__Policies__directory__BaseDelayMs"] = "50",
        ["Integrations__Policies__video_meetings__BaseDelayMs"] = "50",
        ["Integrations__Circuit__FailureThreshold"] = "3",
        ["Integrations__Circuit__OpenSeconds"] = "120",
    };
    await ctx.App.StopAsync();
    var p8App = await AppInstance.StartAsync(hostDll, integEnv);
    var p8Admin = new ApiClient(p8App.BaseUrl);
    foreach (System.Net.Cookie c in ctx.AdminClient!.Cookies.GetAllCookies()) p8Admin.Cookies.Add(new Uri(p8App.BaseUrl), new System.Net.Cookie(c.Name, c.Value));
    ctx.AdminClient = p8Admin;
    ctx.App = p8App;
    await using (var p8 = new Phase8Suite(ctx, fake, smtp)) results.AddRange(await Runner.RunAsync(p8));
    await using (var p9 = new Phase9Suite(ctx)) results.AddRange(await Runner.RunAsync(p9));
}
// القبول: بعد إيقاف الخوادم الوهمية، فالبريد مفعّل وخادمه غير متاح فعليًا
await using (var p10 = new Phase10Suite(ctx)) results.AddRange(await Runner.RunAsync(p10));
ctx.HistoryCountBeforeRestart = (await ctx.AdminClient!.GetJsonAsync("/api/v1/admin/features/history"))["total"]!.GetValue<long>();

// المرحلة ب: إيقاف ثم تشغيل بإعدادات مختلفة على البيانات نفسها
var firstApp = ctx.App;
await ctx.App.StopAsync();
var restartEnv = new Dictionary<string, string>(baseEnv)
{
    ["Authentication__Entra__TenantId"] = idp.TenantId,
    ["Authentication__Entra__ClientId"] = idp.ClientId,
    ["Authentication__Entra__ClientSecret"] = idp.ClientSecret,
    ["Authentication__Entra__AuthorityOverride"] = idp.BaseUrl,
    ["Authentication__Entra__AllowedEmailDomains__0"] = "jsdo.gov.sa",
    ["FeatureManagement__Locks__AI_ENABLED"] = "false",
};
var restarted = await AppInstance.StartAsync(hostDll, restartEnv);
// العميل نفسه بكعكاته على العنوان الجديد: تُنقل الكعكات لأن المنفذ تغيّر
var admin2 = new ApiClient(restarted.BaseUrl);
foreach (System.Net.Cookie c in ctx.AdminClient!.Cookies.GetAllCookies()) admin2.Cookies.Add(new Uri(restarted.BaseUrl), new System.Net.Cookie(c.Name, c.Value));
ctx.AdminClient = admin2;
ctx.App = restarted;
results.AddRange(await Runner.RunAsync(new RestartSuite(ctx)));
await restarted.StopAsync();

// النتيجة
var passed = results.Count(r => r.Passed);
Console.WriteLine($"\n{passed}/{results.Count} ناجح — {sw.Elapsed.TotalSeconds:0.0} ث");
if (args.Length > 0)
{
    var md = new StringBuilder();
    md.AppendLine("# نتائج اختبارات المراحل 1–4 ووحدات التوقيع الإلكتروني والذكاء الاصطناعي والتقارير والسجلات والتكاملات").AppendLine();
    md.AppendLine($"- التاريخ: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
    md.AppendLine($"- البيئة: PostgreSQL ({server}) — قاعدة جديدة `{dbName}` لكل تشغيل؛ التطبيق يعمل كعملية مستقلة ببيئة Testing");
    md.AppendLine($"- النتيجة: **{passed}/{results.Count} ناجح** خلال {sw.Elapsed.TotalSeconds:0} ثانية").AppendLine();
    md.AppendLine("| المعرّف | الاختبار | النتيجة | الزمن (ث) |").AppendLine("|---|---|---|---|");
    foreach (var r in results)
        md.AppendLine($"| {r.Id} | {r.Title} | {(r.Passed ? "✅ ناجح" : "❌ فاشل: " + r.Error?.Replace("|", "/"))} | {r.Seconds:0.00} |");
    File.WriteAllText(args[0], md.ToString());
}
if (passed != results.Count)
{
    Console.WriteLine("أخطاء التشغيل الأول:");
    var fl = firstApp.Log.ToList();
    for (var i = 0; i < fl.Count; i++)
        if (fl[i].StartsWith("fail")) foreach (var l in fl.Skip(i).Take(4)) Console.WriteLine("  " + l);
    Console.WriteLine("آخر سجلات التطبيق:");
    foreach (var l in restarted.Log.TakeLast(20)) Console.WriteLine("  " + l);
}
work.Delete(true);
return passed == results.Count ? 0 : 1;

namespace Ccms.Tests
{
    public sealed class TestContext
    {
        public const string AdminEmail = "admin@test.jsdo.gov.sa";
        public const string AdminPassword = "Admin@Password2026";
        public static readonly string[] ExpectedFlags =
        [
            "AI_ENABLED", "AI_MINUTES_SUMMARY_ENABLED", "AI_DECISION_EXTRACTION_ENABLED", "AI_TASK_EXTRACTION_ENABLED", "AI_SMART_SEARCH_ENABLED",
            "AI_AUDIO_TRANSCRIPTION_ENABLED", "E_SIGNATURE_ENABLED", "E_SIGNATURE_PROVIDER", "E_SIGNATURE_METHOD", "E_SIGNATURE_MEMBER_REQUIRED",
            "E_SIGNATURE_CHAIR_REQUIRED", "E_SIGNATURE_SECRETARY_REQUIRED", "E_SIGNATURE_SEQUENTIAL", "E_SIGNATURE_PDF_OUTPUT",
            "E_SIGNATURE_REAUTH_REQUIRED", "EXTERNAL_PORTAL_ENABLED", "EMAIL_INTEGRATION_ENABLED", "CORRESPONDENCE_INTEGRATION_ENABLED", "SSO_ENABLED",
            // إعدادات مزوّد الذكاء الاصطناعي (V012)
            "AI_PROVIDER", "AI_PROVIDER_HOSTING", "AI_MAX_CLASSIFICATION", "AI_BASE_URL", "AI_MODEL", "AI_TRANSCRIPTION_MODEL", "AI_TIMEOUT_SECONDS",
            "AI_DAILY_REQUESTS_PER_USER", "AI_MONTHLY_REQUEST_LIMIT", "AI_MONTHLY_TOKEN_LIMIT", "AI_USAGE_ALERT_PERCENT", "AI_RETENTION_DAYS",
            // تكاملات المرحلة 8 (V013)
            "DIRECTORY_SYNC_ENABLED", "DOCUMENT_REPOSITORY_ENABLED", "VIDEO_MEETINGS_ENABLED",
        ];

        public AppInstance? App { get; set; }
        public ApiClient? AdminClient { get; set; }
        public MockIdentityProvider? Idp { get; set; }
        public Guid AdminId { get; set; }
        public long HistoryCountBeforeRestart { get; set; }
        public required string AppConnectionString { get; init; }
        public required string OwnerConnectionString { get; init; }
        public required string MigrationsPath { get; init; }
    }

    internal static class AdminBootstrap
    {
        /// <summary>المسار نفسه لأمر bootstrap-admin في Ccms.Host.</summary>
        public static Task<Guid> CreateAsync(Db db) =>
            Ccms.Host.Cli.BootstrapAdminAsync(db, TestContext.AdminEmail, "مدير الاختبار", TestContext.AdminPassword);
    }
}
