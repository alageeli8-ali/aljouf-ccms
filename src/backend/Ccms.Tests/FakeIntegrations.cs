using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ccms.Tests;

/// <summary>خوادم وهمية داخل الاختبارات فقط تحاكي: خادم هوية Entra (client credentials)، وMicrosoft Graph (المستخدمين، واجتماعات Teams، وSharePoint)،
/// وعقد نظام المراسلات المقترح ccms-letters-v1. تحقن الأعطال (5xx متتالية، بطء، رفض) لاختبار المهلة وإعادة المحاولة وقاطع الدائرة.
/// نجاح هذه الاختبارات يثبت سلوك الموصلات لا توفر الخدمات الحقيقية لدى المكتب.</summary>
public sealed class FakeIntegrationServer : IAsyncDisposable
{
    private WebApplication? _app;
    public string Root { get; private set; } = "";
    public string TenantId => "p8-tenant";
    public string ClientId => "p8-integration-app";
    public string ClientSecret => "p8-graph-secret-DO-NOT-LOG-9f3a";
    public string CorrespondenceKey => "p8-corr-key-DO-NOT-LOG-77c1";
    public string DriveId => "p8-drive";
    public string Organizer => "p8-organizer";

    public string[] Roles { get; set; } = ["User.Read.All", "OnlineMeetings.ReadWrite.All", "Sites.Selected"];
    /// <summary>عدد الاستجابات 503 القادمة لطلبات Graph/المراسلات قبل النجاح.</summary>
    public int FailNext;
    public int SlowMs { get; set; }
    public bool DenyToken { get; set; }
    public int GraphRequests;
    public int TokenRequests;
    public ConcurrentDictionary<string, string> Meetings { get; } = new();
    public int MeetingsCreated;
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, string> Letters { get; } = new();
    public int LettersCreated;
    public ConcurrentQueue<string> SeenAuth { get; } = new();

    public List<(string Id, string Mail, string Name, string? Title, bool Enabled)> Users { get; } = [];

    public async Task StartAsync()
    {
        var port = AppInstance.FreePort();
        Root = $"http://127.0.0.1:{port}";
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls(Root);
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync();
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var res = ctx.Response;
        if (path == $"/{TenantId}/oauth2/v2.0/token" && ctx.Request.Method == "POST")
        {
            Interlocked.Increment(ref TokenRequests);
            var form = await ctx.Request.ReadFormAsync();
            if (DenyToken || form["client_secret"] != ClientSecret || form["client_id"] != ClientId)
            {
                res.StatusCode = 401;
                await res.WriteAsJsonAsync(new { error = "invalid_client", error_description = "AADSTS7000215 secret " + form["client_secret"] });
                return;
            }
            var payload = JsonSerializer.Serialize(new { aud = "https://graph.microsoft.com", roles = Roles, tid = TenantId });
            await res.WriteAsJsonAsync(new { access_token = $"{B64("{\"alg\":\"none\"}")}.{B64(payload)}.sig", expires_in = 3600, token_type = "Bearer" });
            return;
        }

        if (path.StartsWith("/v1.0/") || path.StartsWith("/corr/"))
        {
            SeenAuth.Enqueue(ctx.Request.Headers.Authorization.ToString() + ctx.Request.Headers["X-Api-Key"]);
            if (path.StartsWith("/v1.0/")) Interlocked.Increment(ref GraphRequests);
            if (SlowMs > 0) await Task.Delay(SlowMs, ctx.RequestAborted).ContinueWith(_ => { });
            if (Interlocked.Decrement(ref FailNext) >= 0) { res.StatusCode = 503; await res.WriteAsJsonAsync(new { error = new { code = "serviceNotAvailable" } }); return; }
            Interlocked.Exchange(ref FailNext, Math.Max(FailNext, 0));
        }

        if (path.StartsWith("/v1.0/"))
        {
            if (!ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ")) { res.StatusCode = 401; return; }
            var p = path["/v1.0/".Length..];
            if (p == "users" && ctx.Request.Method == "GET")
            {
                var page = ctx.Request.Query["page"].ToString() == "2";
                var slice = page ? Users.Skip(2) : Users.Take(2);
                var value = slice.Select(u => new { id = u.Id, mail = u.Mail, userPrincipalName = u.Mail, displayName = u.Name, jobTitle = u.Title, department = (string?)null, accountEnabled = u.Enabled });
                if (!page && Users.Count > 2) await res.WriteAsJsonAsync(new Dictionary<string, object> { ["value"] = value, ["@odata.nextLink"] = $"{Root}/v1.0/users?page=2" });
                else await res.WriteAsJsonAsync(new { value });
                return;
            }
            if (p == $"users/{Organizer}/onlineMeetings" && ctx.Request.Method == "GET") { await res.WriteAsJsonAsync(new { value = Array.Empty<object>() }); return; }
            if (p == $"users/{Organizer}/onlineMeetings/createOrGet" && ctx.Request.Method == "POST")
            {
                var body = JsonNode.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync())!;
                var ext = body["externalId"]!.GetValue<string>();
                var url = Meetings.GetOrAdd(ext, k => { Interlocked.Increment(ref MeetingsCreated); return $"https://teams.microsoft.com/l/meetup-join/fake-{k}"; });
                await res.WriteAsJsonAsync(new { id = "mtg-" + ext, joinWebUrl = url, subject = body["subject"]?.GetValue<string>() });
                return;
            }
            if (p == $"drives/{DriveId}/root" && ctx.Request.Method == "GET") { await res.WriteAsJsonAsync(new { id = "root", webUrl = "https://sp.example/ccms" }); return; }
            var prefix = $"drives/{DriveId}/root:/";
            if (p.StartsWith(prefix) && p.EndsWith(":/content") && ctx.Request.Method == "PUT")
            {
                var filePath = Uri.UnescapeDataString(p[prefix.Length..^":/content".Length]);
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                Files[filePath] = ms.ToArray();
                await res.WriteAsJsonAsync(new { id = "item-" + Math.Abs(filePath.GetHashCode()), webUrl = "https://sp.example/" + filePath });
                return;
            }
            res.StatusCode = 404;
            await res.WriteAsJsonAsync(new { error = new { code = "itemNotFound" } });
            return;
        }

        if (path.StartsWith("/corr/"))
        {
            if (ctx.Request.Headers["X-Api-Key"] != CorrespondenceKey) { res.StatusCode = 401; return; }
            if (path == "/corr/health") { await res.WriteAsJsonAsync(new { status = "ok", contract = "ccms-letters-v1" }); return; }
            if (path == "/corr/letters" && ctx.Request.Method == "POST")
            {
                var key = ctx.Request.Headers["Idempotency-Key"].ToString();
                if (key.Length == 0) { res.StatusCode = 400; return; }
                var no = Letters.GetOrAdd(key, _ => $"OUT-{Interlocked.Increment(ref LettersCreated):0000}");
                await res.WriteAsJsonAsync(new { outgoingNo = no });
                return;
            }
        }
        res.StatusCode = 404;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

/// <summary>خادم SMTP وهمي مبسط: يقبل الرسائل أو يرد بخطأ مؤقت 451 حسب الوضع. لا يحفظ إلا المستلم والموضوع للتحقق.</summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; private set; }
    public bool Transient451 { get; set; }
    public ConcurrentQueue<string> Received { get; } = new();
    public int Attempts;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(() => SessionAsync(c));
            }
        });
    }

    private async Task SessionAsync(TcpClient c)
    {
        using var _ = c;
        try
        {
            await using var s = c.GetStream();
            using var r = new StreamReader(s, Encoding.ASCII);
            await using var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 fake-smtp ready");
            string? rcpt = null;
            while (await r.ReadLineAsync() is { } line)
            {
                var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO": await w.WriteLineAsync("250-fake-smtp"); await w.WriteLineAsync("250 8BITMIME"); break;
                    case "HELO": await w.WriteLineAsync("250 fake-smtp"); break;
                    case "MAIL":
                        Interlocked.Increment(ref Attempts);
                        await w.WriteLineAsync(Transient451 ? "451 4.3.0 try again later" : "250 OK"); break;
                    case "RCPT": rcpt = line[(line.IndexOf(':') + 1)..].Trim(' ', '<', '>'); await w.WriteLineAsync("250 OK"); break;
                    case "DATA":
                        await w.WriteLineAsync("354 end with .");
                        while (await r.ReadLineAsync() is { } d && d != ".") { }
                        Received.Enqueue(rcpt ?? "");
                        await w.WriteLineAsync("250 queued"); break;
                    case "QUIT": await w.WriteLineAsync("221 bye"); return;
                    case "RSET": await w.WriteLineAsync("250 OK"); break;
                    default: await w.WriteLineAsync("250 OK"); break;
                }
            }
        }
        catch (IOException) { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        return ValueTask.CompletedTask;
    }
}
