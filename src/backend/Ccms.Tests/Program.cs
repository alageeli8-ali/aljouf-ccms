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
    md.AppendLine("# نتائج اختبارات المراحل 1–4").AppendLine();
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
