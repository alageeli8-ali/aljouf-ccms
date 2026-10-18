using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.Contracts;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Integrations;

public sealed class GraphOptions
{
    /// <summary>معرّف المستأجر في Microsoft Entra ID.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>معرّف تطبيق التكامل (App registration) — منفصل عن تطبيق تسجيل الدخول.</summary>
    public string ClientId { get; set; } = "";
    public string AuthorityHost { get; set; } = "https://login.microsoftonline.com";
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
}

public sealed class DirectoryOptions
{
    public int MaxUsers { get; set; } = 5000;
}

public sealed class VideoMeetingsOptions
{
    /// <summary>معرّف (Object ID) حساب المنظّم الذي تُنشأ الاجتماعات باسمه؛ يتطلب سياسة وصول تطبيقي في Teams (Application Access Policy).</summary>
    public string OrganizerUserId { get; set; } = "";
}

public sealed class DocumentRepositoryOptions
{
    /// <summary>معرّف مكتبة المستندات (Drive ID) في SharePoint.</summary>
    public string DriveId { get; set; } = "";
    public string RootFolder { get; set; } = "CCMS";
    /// <summary>الرفع البسيط في Graph حتى 4 م.ب؛ الأكبر يحتاج جلسة رفع (غير منفذة — يُرفض بسبب صريح).</summary>
    public int MaxUploadMb { get; set; } = 4;
}

/// <summary>رمز الوصول المخزن مؤقتًا (على مستوى العملية) حتى قبيل انتهائه.</summary>
internal sealed class GraphTokenCache
{
    public readonly SemaphoreSlim Lock = new(1, 1);
    public string? Token;
    public string[] Roles = [];
    public DateTime ExpiresAt;
    public string? Key;
}

/// <summary>عميل Microsoft Graph بمصادقة التطبيق (client credentials). يتحقق من أدوار التطبيق في الرمز قبل كل عملية،
/// ويحوّل استجابات الخطأ إلى أسباب موحدة: 401/403 رفض صلاحية، 408/429/5xx مؤقت قابل لإعادة المحاولة، وغيرها رفض.</summary>
internal sealed class GraphClient(IOptions<IntegrationOptions> options, IHttpClientFactory http, ISecretStore secrets, GraphTokenCache cache)
{
    private GraphOptions O => options.Value.Graph;

    public async Task<List<ConnectorRequirement>> RequirementsAsync(CancellationToken ct)
    {
        var secret = await secrets.InfoAsync(SecretNames.GraphClientSecret, ct);
        return
        [
            new("Integrations:Graph:TenantId", "معرّف المستأجر في Entra ID", "Entra ID tenant id", O.TenantId.Length > 0),
            new("Integrations:Graph:ClientId", "معرّف تطبيق التكامل المسجل في Entra ID", "Integration app registration client id", O.ClientId.Length > 0),
            new(SecretNames.GraphClientSecret, "سر تطبيق التكامل (مخزن الأسرار)", "Integration app client secret (secret store)", secret.Source != "none", true),
        ];
    }

    public async Task<bool> ConfiguredAsync(CancellationToken ct) => (await RequirementsAsync(ct)).All(r => r.Present);

    public async Task<(string Token, string[] Roles)> TokenAsync(CancellationToken ct)
    {
        var key = $"{O.AuthorityHost}|{O.TenantId}|{O.ClientId}";
        if (cache.Token is not null && cache.Key == key && cache.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return (cache.Token, cache.Roles);
        await cache.Lock.WaitAsync(ct);
        try
        {
            if (cache.Token is not null && cache.Key == key && cache.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return (cache.Token, cache.Roles);
            var secret = await secrets.GetAsync(SecretNames.GraphClientSecret, ct);
            if (O.TenantId.Length == 0 || O.ClientId.Length == 0 || string.IsNullOrEmpty(secret))
                throw new ConnectorCallException("not_configured", "بيانات تطبيق Microsoft Graph غير مكتملة (TenantId/ClientId/GRAPH_CLIENT_SECRET)");
            var scope = new Uri(O.BaseUrl).GetLeftPart(UriPartial.Authority) + "/.default";
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = O.ClientId, ["client_secret"] = secret, ["scope"] = scope,
            });
            using var resp = await http.CreateClient("integrations").PostAsync($"{O.AuthorityHost.TrimEnd('/')}/{Uri.EscapeDataString(O.TenantId)}/oauth2/v2.0/token", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                // نص الخطأ من الخادم قد يتضمن معرّفات؛ يُكتفى برمز الخطأ المعياري
                var err = await SafeErrorCodeAsync(resp, ct);
                throw code is 400 or 401 or 403
                    ? new ConnectorCallException("denied", $"رفض خادم الهوية إصدار الرمز ({code} {err}) — تحقق من معرّف التطبيق وسره", code)
                    : new ConnectorCallException("unavailable", $"خادم الهوية غير متاح ({code})", code, transient: code >= 500 || code is 408 or 429);
            }
            var body = await resp.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new ConnectorCallException("rejected", "استجابة رمز غير صالحة");
            var token = body["access_token"]?.GetValue<string>() ?? throw new ConnectorCallException("rejected", "الاستجابة بلا access_token");
            var expires = body["expires_in"]?.GetValue<int>() ?? 3600;
            cache.Token = token;
            cache.Roles = RolesOf(token);
            cache.ExpiresAt = DateTime.UtcNow.AddSeconds(expires);
            cache.Key = key;
            return (token, cache.Roles);
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    /// <summary>أدوار التطبيق (roles) من حمولة الرمز — للتحقق من الصلاحيات الممنوحة قبل الاستدعاء. التحقق من التوقيع مسؤولية Graph نفسه.</summary>
    public static string[] RolesOf(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return [];
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            return json?["roles"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray() : [];
        }
        catch (Exception e) when (e is FormatException or JsonException) { return []; }
    }

    public static void DemandRoles(string[] granted, string[] anyOf)
    {
        if (!anyOf.Any(granted.Contains))
            throw new ConnectorCallException("denied", $"تطبيق التكامل لا يملك صلاحية Graph المطلوبة: {string.Join(" أو ", anyOf)}");
    }

    private static async Task<string> SafeErrorCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var e = j?["error"];
            var code = e is JsonObject o ? o["code"]?.GetValue<string>() : e?.GetValue<string>();
            return code is { Length: <= 60 } ? code : "";
        }
        catch (Exception x) when (x is JsonException or InvalidOperationException) { return ""; }
    }

    /// <summary>طلب Graph بعد التحقق من الأدوار. path نسبي إلى BaseUrl، أو رابط مطلق على المضيف نفسه فقط (روابط الصفحات التالية).</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, HttpContent? content, string[] requiredRoles, CancellationToken ct)
    {
        var (token, roles) = await TokenAsync(ct);
        DemandRoles(roles, requiredRoles);
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{O.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        if (new Uri(url).Host != new Uri(O.BaseUrl).Host) throw new ConnectorCallException("rejected", "رابط خارج مضيف Graph المضبوط");
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await http.CreateClient("integrations").SendAsync(req, ct);
        var code = (int)resp.StatusCode;
        if (resp.IsSuccessStatusCode)
            return resp.StatusCode == HttpStatusCode.NoContent || resp.Content.Headers.ContentLength == 0 ? null : JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var err = await SafeErrorCodeAsync(resp, ct);
        if (code == 401) { cache.Token = null; }
        throw code switch
        {
            401 or 403 => new ConnectorCallException("denied", $"رفض Graph العملية ({code} {err})", code),
            408 or 429 or >= 500 => new ConnectorCallException("unavailable", $"Graph غير متاح مؤقتًا ({code} {err})", code, transient: true),
            _ => new ConnectorCallException("rejected", $"رفض Graph الطلب ({code} {err})", code),
        };
    }

    /// <summary>فحص الاتصال: رمز صالح + الأدوار المطلوبة + استدعاء قراءة فعلي.</summary>
    public async Task<ConnectorHealth> ProbeAsync(string[] roles, string readPath, CancellationToken ct)
    {
        var reqs = await RequirementsAsync(ct);
        if (reqs.Any(r => !r.Present)) return new ConnectorHealth(ConnectorState.NotConfigured, "متطلبات ناقصة: " + string.Join("، ", reqs.Where(r => !r.Present).Select(r => r.Key)));
        var sw = Stopwatch.StartNew();
        // الفحص يطلب رمزًا جديدًا دائمًا ليعكس أي تغيير في السر أو الأدوار الممنوحة
        cache.Token = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await SendAsync(HttpMethod.Get, readPath, null, roles, timeout.Token);
            return new ConnectorHealth(ConnectorState.Healthy, "رمز صالح، والصلاحيات ممنوحة، واستدعاء القراءة نجح", (int)sw.ElapsedMilliseconds);
        }
        catch (ConnectorCallException e)
        {
            return new ConnectorHealth(e.Reason == "not_configured" ? ConnectorState.NotConfigured : ConnectorState.Failing, e.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return new ConnectorHealth(ConnectorState.Failing, e is OperationCanceledException ? "انتهت المهلة" : "تعذر الاتصال: " + e.Message, (int)sw.ElapsedMilliseconds);
        }
    }
}

// ---------------- الدليل المؤسسي (Entra ID / AD) ----------------

internal sealed class DirectoryConnector(GraphClient graph) : IConnector
{
    public static readonly string[] Roles = ["User.Read.All", "Directory.Read.All"];
    public string Code => "directory";
    public string NameAr => "دليل المستخدمين (Microsoft Entra ID / Active Directory)";
    public string NameEn => "User directory (Microsoft Entra ID / Active Directory)";
    public Task<ConnectorHealth> CheckAsync(CancellationToken ct = default) => graph.ProbeAsync(Roles, "users?$top=1&$select=id", ct);

    public async Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default) => new(Code, NameAr, NameEn, FeatureKeys.DirectorySyncEnabled, "oauth2_client_credentials",
        await graph.RequirementsAsync(ct),
        "قراءة المستخدمين عبر Microsoft Graph. حسابات Active Directory المحلية تظهر بعد مزامنتها إلى Entra ID (Entra Connect)؛ لا اتصال LDAP مباشر.",
        "Reads users via Microsoft Graph. On-prem Active Directory accounts appear once synced to Entra ID (Entra Connect); no direct LDAP.", Roles);
}

internal sealed class DirectoryGateway(GraphClient graph, IConnectorRuntime runtime, IOptions<IntegrationOptions> options) : IDirectoryGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default) =>
        await graph.ConfiguredAsync(ct) ? (true, "ok") : (false, "connector_not_configured");

    public Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(int max, CancellationToken ct = default) =>
        runtime.ExecuteAsync<IReadOnlyList<DirectoryUser>>("directory", "list_users", FeatureKeys.DirectorySyncEnabled, new ConnectorCallPolicy(Idempotent: true, TimeoutSeconds: 30), async t =>
        {
            var cap = Math.Clamp(max, 1, options.Value.Directory.MaxUsers);
            var list = new List<DirectoryUser>();
            string? next = "users?$select=id,mail,userPrincipalName,displayName,jobTitle,department,accountEnabled&$top=999";
            while (next is not null && list.Count < cap)
            {
                var page = await graph.SendAsync(HttpMethod.Get, next, null, DirectoryConnector.Roles, t);
                foreach (var u in page?["value"] as JsonArray ?? [])
                {
                    var email = u?["mail"]?.GetValue<string>() ?? u?["userPrincipalName"]?.GetValue<string>();
                    if (u?["id"] is null || string.IsNullOrEmpty(email)) continue;
                    list.Add(new DirectoryUser(u["id"]!.GetValue<string>(), email, u["displayName"]?.GetValue<string>() ?? email, u["jobTitle"]?.GetValue<string>(),
                        u["department"]?.GetValue<string>(), u["accountEnabled"]?.GetValue<bool>() ?? true));
                    if (list.Count >= cap) break;
                }
                next = page?["@odata.nextLink"]?.GetValue<string>();
            }
            return list;
        }, ct);
}

// ---------------- الاجتماعات المرئية (Microsoft Teams) ----------------

internal sealed class VideoMeetingsConnector(GraphClient graph, IOptions<IntegrationOptions> options) : IConnector
{
    public static readonly string[] Roles = ["OnlineMeetings.ReadWrite.All"];
    public string Code => "video_meetings";
    public string NameAr => "الاجتماعات المرئية (Microsoft Teams)";
    public string NameEn => "Video meetings (Microsoft Teams)";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var organizer = options.Value.VideoMeetings.OrganizerUserId;
        if (organizer.Length == 0) return new ConnectorHealth(ConnectorState.NotConfigured, "لم يُحدد حساب المنظّم (Integrations:VideoMeetings:OrganizerUserId)");
        // قراءة بلا أثر: البحث عن اجتماع برابط غير موجود
        return await graph.ProbeAsync(Roles, $"users/{Uri.EscapeDataString(organizer)}/onlineMeetings?$filter=JoinWebUrl%20eq%20'https://teams.microsoft.com/l/meetup-join/ccms-probe'", ct);
    }

    public async Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default)
    {
        var reqs = await graph.RequirementsAsync(ct);
        reqs.Add(new("Integrations:VideoMeetings:OrganizerUserId", "حساب المنظّم (Object ID) مع سياسة وصول تطبيقي في Teams", "Organizer account object id with a Teams application access policy",
            options.Value.VideoMeetings.OrganizerUserId.Length > 0));
        return new(Code, NameAr, NameEn, FeatureKeys.VideoMeetingsEnabled, "oauth2_client_credentials", reqs,
            "إنشاء رابط اجتماع Teams للاجتماعات عن بعد أو المدمجة بعملية createOrGet بمعرّف الاجتماع (إعادة المحاولة لا تُنشئ اجتماعًا مكررًا). Zoom وWebex غير منفذين لعدم توفر واجهة معتمدة لدى المكتب.",
            "Creates a Teams join link for remote/hybrid meetings via createOrGet keyed by the meeting id (retries never duplicate). Zoom/Webex not implemented — no approved API at the office.", Roles);
    }
}

internal sealed class VideoMeetingGateway(GraphClient graph, IConnectorRuntime runtime, IOptions<IntegrationOptions> options) : IVideoMeetingGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default) =>
        !await graph.ConfiguredAsync(ct) || options.Value.VideoMeetings.OrganizerUserId.Length == 0 ? (false, "connector_not_configured") : (true, "ok");

    public Task<OnlineMeeting> CreateAsync(string subject, DateTime startsAtUtc, DateTime endsAtUtc, string idempotencyKey, CancellationToken ct = default) =>
        runtime.ExecuteAsync("video_meetings", "create_meeting", FeatureKeys.VideoMeetingsEnabled, new ConnectorCallPolicy(Idempotent: true), async t =>
        {
            var organizer = options.Value.VideoMeetings.OrganizerUserId;
            if (organizer.Length == 0) throw new ConnectorCallException("not_configured", "لم يُحدد حساب المنظّم");
            var body = JsonContent.Create(new
            {
                externalId = idempotencyKey, subject,
                startDateTime = DateTime.SpecifyKind(startsAtUtc, DateTimeKind.Utc).ToString("o"), endDateTime = DateTime.SpecifyKind(endsAtUtc, DateTimeKind.Utc).ToString("o"),
            });
            var r = await graph.SendAsync(HttpMethod.Post, $"users/{Uri.EscapeDataString(organizer)}/onlineMeetings/createOrGet", body, VideoMeetingsConnector.Roles, t);
            var url = r?["joinWebUrl"]?.GetValue<string>() ?? r?["joinUrl"]?.GetValue<string>() ?? throw new ConnectorCallException("rejected", "الاستجابة بلا رابط انضمام");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") throw new ConnectorCallException("rejected", "رابط الانضمام غير صالح");
            return new OnlineMeeting(url, r?["id"]?.GetValue<string>() ?? "", "teams");
        }, ct);
}

// ---------------- مستودع الوثائق (SharePoint) ----------------

internal sealed class DocumentRepositoryConnector(GraphClient graph, IOptions<IntegrationOptions> options) : IConnector
{
    public static readonly string[] Roles = ["Sites.Selected", "Files.ReadWrite.All", "Sites.ReadWrite.All"];
    public string Code => "document_repository";
    public string NameAr => "مستودع الوثائق المؤسسي (SharePoint)";
    public string NameEn => "Enterprise document repository (SharePoint)";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var drive = options.Value.DocumentRepository.DriveId;
        if (drive.Length == 0) return new ConnectorHealth(ConnectorState.NotConfigured, "لم تُحدد مكتبة المستندات (Integrations:DocumentRepository:DriveId)");
        return await graph.ProbeAsync(Roles, $"drives/{Uri.EscapeDataString(drive)}/root?$select=id,webUrl", ct);
    }

    public async Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default)
    {
        var reqs = await graph.RequirementsAsync(ct);
        reqs.Add(new("Integrations:DocumentRepository:DriveId", "معرّف مكتبة المستندات في SharePoint", "SharePoint document library drive id",
            options.Value.DocumentRepository.DriveId.Length > 0));
        return new(Code, NameAr, NameEn, FeatureKeys.DocumentRepositoryEnabled, "oauth2_client_credentials", reqs,
            $"إيداع نسخة من المستند (الأصل والسجل يبقيان في النظام) في مسار ثابت؛ إعادة الإيداع تستبدل الملف نفسه فلا تكرار. الحد {options.Value.DocumentRepository.MaxUploadMb} م.ب (الرفع البسيط).",
            $"Deposits a copy (the original and its record stay in CCMS) at a deterministic path; re-deposit replaces the same file, so no duplicates. Limit {options.Value.DocumentRepository.MaxUploadMb} MB (simple upload).", Roles);
    }
}

internal sealed class DocumentRepositoryGateway(GraphClient graph, IConnectorRuntime runtime, IOptions<IntegrationOptions> options) : IDocumentRepositoryGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default) =>
        !await graph.ConfiguredAsync(ct) || options.Value.DocumentRepository.DriveId.Length == 0 ? (false, "connector_not_configured") : (true, "ok");

    private static string Segment(string s) => string.Join('/', s.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => Uri.EscapeDataString(new string(p.Where(c => !"\"*:<>?\\|#%".Contains(c)).ToArray()).Trim())));

    public Task<RepositoryItem> UploadAsync(string folder, string fileName, string contentType, byte[] content, CancellationToken ct = default) =>
        runtime.ExecuteAsync("document_repository", "upload", FeatureKeys.DocumentRepositoryEnabled, new ConnectorCallPolicy(Idempotent: true, TimeoutSeconds: 60), async t =>
        {
            var o = options.Value.DocumentRepository;
            if (o.DriveId.Length == 0) throw new ConnectorCallException("not_configured", "لم تُحدد مكتبة المستندات");
            if (content.Length > o.MaxUploadMb * 1024 * 1024) throw new ConnectorCallException("rejected", $"الملف أكبر من {o.MaxUploadMb} م.ب؛ الرفع المجزأ غير مفعّل");
            var path = Segment($"{o.RootFolder}/{folder}/{fileName}");
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = new(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
            var r = await graph.SendAsync(HttpMethod.Put, $"drives/{Uri.EscapeDataString(o.DriveId)}/root:/{path}:/content?@microsoft.graph.conflictBehavior=replace", body,
                DocumentRepositoryConnector.Roles, t);
            return new RepositoryItem(r?["id"]?.GetValue<string>() ?? throw new ConnectorCallException("rejected", "الاستجابة بلا معرّف"), r?["webUrl"]?.GetValue<string>());
        }, ct);
}
