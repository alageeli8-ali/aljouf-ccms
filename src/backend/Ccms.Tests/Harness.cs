using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute(string id, string title, int order = 100) : Attribute
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public int Order { get; } = order;
}

public sealed class AssertionException(string message) : Exception(message);

public static class Check
{
    public static void That(bool condition, string message,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertionException($"{message} [{expr}]");
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}: المتوقع «{expected}» والفعلي «{actual}»");
    }

    public static async Task<JsonNode?> Status(HttpResponseMessage resp, int expected, string message)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode != expected)
            throw new AssertionException($"{message}: المتوقع HTTP {expected} والفعلي {(int)resp.StatusCode} — {Trim(body)}");
        return string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{') && !body.TrimStart().StartsWith('[') ? null : JsonNode.Parse(body);
    }

    public static async Task Problem(HttpResponseMessage resp, int status, string code, string message)
    {
        var node = await Status(resp, status, message);
        Equal(code, node?["code"]?.GetValue<string>(), message + " (رمز المشكلة)");
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}

/// <summary>عميل HTTP بجلسة مستقلة (كعكات) وتعامل تلقائي مع رمز CSRF.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string? _csrf;

    public ApiClient(string baseUrl, bool followRedirects = true)
    {
        var handler = new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = followRedirects, UseCookies = true };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
    }

    public CookieContainer Cookies => _cookies;

    public async Task RefreshCsrfAsync()
    {
        var r = await _http.GetFromJsonAsync<JsonObject>("/api/v1/auth/csrf");
        _csrf = r!["token"]!.GetValue<string>();
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        await RefreshCsrfAsync();
        var resp = await PostAsync("/api/v1/auth/login", new { email, password });
        if (resp.IsSuccessStatusCode) await RefreshCsrfAsync();
        return resp;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => _http.GetAsync(url);

    public async Task<JsonNode> GetJsonAsync(string url)
    {
        var resp = await _http.GetAsync(url);
        return (await Check.Status(resp, 200, $"GET {url}"))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, bool csrf, string? idempotencyKey = null)
    {
        if (csrf && _csrf is null) await RefreshCsrfAsync();
        using var req = new HttpRequestMessage(method, url) { Content = content };
        if (csrf) req.Headers.Add("X-CSRF-Token", _csrf);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _http.SendAsync(req);
    }

    /// <summary>طلب إنشاء بمفتاح منع التكرار؛ إعادة الإرسال بالمفتاح نفسه تحاكي إعادة المحاولة أو تحديث الصفحة.</summary>
    public Task<HttpResponseMessage> PostOnceAsync(string url, object? body, string key) =>
        SendAsync(HttpMethod.Post, url, body is null ? null : JsonContent.Create(body), true, key);

    public Task<byte[]> GetBytesAsync(string url) => _http.GetByteArrayAsync(url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) =>
        SendAsync(HttpMethod.Post, url, body is null ? null : JsonContent.Create(body), csrf);
    public Task<HttpResponseMessage> PutAsync(string url, object body, bool csrf = true) => SendAsync(HttpMethod.Put, url, JsonContent.Create(body), csrf);
    public Task<HttpResponseMessage> PatchAsync(string url, object body, bool csrf = true) => SendAsync(HttpMethod.Patch, url, JsonContent.Create(body), csrf);
    public Task<HttpResponseMessage> PostContentAsync(string url, HttpContent content) => SendAsync(HttpMethod.Post, url, content, true);

    public void Dispose() => _http.Dispose();
}

/// <summary>يشغّل Ccms.Host الحقيقي كعملية مستقلة ببيئة Testing؛ الإيقاف ثم التشغيل يختبر الاستمرارية بعد إعادة التشغيل.</summary>
public sealed class AppInstance : IAsyncDisposable
{
    private Process? _process;
    private readonly List<string> _log = [];
    public string BaseUrl { get; }
    public IReadOnlyList<string> Log => _log;

    private AppInstance(string baseUrl) => BaseUrl = baseUrl;

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<AppInstance> StartAsync(string hostDll, IDictionary<string, string> env)
    {
        var port = FreePort();
        var app = new AppInstance($"http://127.0.0.1:{port}");
        var psi = new ProcessStartInfo("dotnet", $"\"{hostDll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostDll)!,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["ASPNETCORE_URLS"] = app.BaseUrl;
        foreach (var (k, v) in env) psi.Environment[k] = v;
        app._process = Process.Start(psi)!;
        app._process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (app._log) app._log.Add(e.Data); };
        app._process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (app._log) app._log.Add(e.Data); };
        app._process.BeginOutputReadLine();
        app._process.BeginErrorReadLine();

        using var http = new HttpClient();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (app._process.HasExited) throw new InvalidOperationException("توقف التطبيق عند الإقلاع:\n" + string.Join("\n", app._log.TakeLast(30)));
            try
            {
                var r = await http.GetAsync(app.BaseUrl + "/health/ready");
                if (r.IsSuccessStatusCode) return app;
            }
            catch (HttpRequestException) { }
            await Task.Delay(300);
        }
        throw new TimeoutException("لم يجهز التطبيق خلال 60 ثانية");
    }

    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited) return;
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed record TestOutcome(string Id, string Title, bool Passed, double Seconds, string? Error);

public static class Runner
{
    public static async Task<List<TestOutcome>> RunAsync(object suite)
    {
        var tests = suite.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<TestAttribute>()))
            .Where(x => x.Attr is not null)
            .OrderBy(x => x.Attr!.Order).ThenBy(x => x.Attr!.Id, StringComparer.Ordinal)
            .ToList();
        var results = new List<TestOutcome>();
        foreach (var (method, attr) in tests)
        {
            var sw = Stopwatch.StartNew();
            string? error = null;
            try
            {
                await (Task)method.Invoke(suite, null)!;
            }
            catch (TargetInvocationException e) { error = e.InnerException?.Message ?? e.Message; }
            catch (Exception e) { error = e.Message; }
            results.Add(new TestOutcome(attr!.Id, attr.Title, error is null, sw.Elapsed.TotalSeconds, error));
            Console.WriteLine($"{(error is null ? "PASS" : "FAIL")}  {attr.Id,-6} {attr.Title}{(error is null ? "" : "\n        → " + error)}");
        }
        return results;
    }
}

/// <summary>إدارة قاعدة الاختبار: قاعدة جديدة نظيفة لكل تشغيل، بالأدوار نفسها في bootstrap.sql.</summary>
public static class TestDatabase
{
    public static async Task RecreateAsync(string adminCs, string dbName)
    {
        await using var admin = new PgWireDataSource(adminCs);
        var db = new Db(admin);
        await db.ExecuteAsync($"""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid()
            """);
        await db.ExecuteAsync($"DROP DATABASE IF EXISTS {dbName}");
        await db.ExecuteAsync("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ccms_owner') THEN CREATE ROLE ccms_owner LOGIN PASSWORD 'ccms_owner_dev'; END IF;
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ccms_app') THEN CREATE ROLE ccms_app LOGIN PASSWORD 'ccms_app_dev'; END IF;
            END $$
            """);
        await db.ExecuteAsync($"CREATE DATABASE {dbName} OWNER ccms_owner ENCODING 'UTF8' TEMPLATE template0");
        await using var inDb = new PgWireDataSource(adminCs.Replace("Database=postgres", $"Database={dbName}"));
        await new Db(inDb).ExecuteAsync($"""
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            GRANT USAGE, CREATE ON SCHEMA public TO ccms_owner;
            GRANT USAGE ON SCHEMA public TO ccms_app;
            GRANT CONNECT ON DATABASE {dbName} TO ccms_app;
            """);
    }
}

public static class JsonExt
{
    public static string S(this JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? throw new AssertionException($"الحقل {key} مفقود");
    public static Guid G(this JsonNode? n, string key) => Guid.Parse(n.S(key));
    public static JsonArray Items(this JsonNode? n) => n as JsonArray ?? (n?["items"] as JsonArray) ?? throw new AssertionException("لا توجد عناصر");
    public static string Raw(this JsonNode? n) => n?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
}
