namespace Ccms.Contracts;

// ---------------- التكاملات المؤسسية (23 §4) ----------------
// كل بوابة تنفذها وحدة التكاملات الاختيارية؛ الوحدات الأساسية تستدعيها وتحفظ النتيجة في جداولها. بدونها (أو مع تعطيل مفتاحها) يعمل النظام كاملًا.

public sealed record DirectoryUser(string ExternalId, string Email, string DisplayName, string? JobTitle, string? Department, bool Enabled);

/// <summary>دليل المستخدمين المؤسسي (Microsoft Entra ID، ومنه حسابات Active Directory المزامنة).</summary>
public interface IDirectoryGateway
{
    Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryUser>> ListUsersAsync(int max, CancellationToken ct = default);
}

public sealed record OnlineMeeting(string JoinUrl, string ExternalId, string Provider);

/// <summary>منصة الاجتماعات المرئية: إنشاء رابط اجتماع. الاجتماع يُدار كاملًا دونها (الرابط يُدخل يدويًا).</summary>
public interface IVideoMeetingGateway
{
    Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default);
    Task<OnlineMeeting> CreateAsync(string subject, DateTime startsAtUtc, DateTime endsAtUtc, string idempotencyKey, CancellationToken ct = default);
}

public sealed record RepositoryItem(string RemoteId, string? WebUrl);

/// <summary>مستودع الوثائق المؤسسي: إيداع نسخة من مستند؛ الأصل والسجل يبقيان في النظام.</summary>
public interface IDocumentRepositoryGateway
{
    Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default);
    Task<RepositoryItem> UploadAsync(string folder, string fileName, string contentType, byte[] content, CancellationToken ct = default);
}

/// <summary>فشل تكامل بعد استنفاد المحاولات الآمنة أو لسبب غير قابل لإعادة المحاولة. Reason: not_configured | disabled | denied | timeout | unavailable | circuit_open | rejected.</summary>
public sealed class IntegrationFailedException(string connector, string reason, string message) : Exception(message)
{
    public string Connector { get; } = connector;
    public string Reason { get; } = reason;
}

// ---------------- إطار الموصلات: المهلة وإعادة المحاولة الآمنة وقاطع الدائرة والتسجيل ----------------

/// <summary>سياسة الاستدعاء. Idempotent=true فقط لعملية لا يتكرر أثرها عند إعادتها (قراءة، أو كتابة بمفتاح عدم تكرار يحترمه المزوّد)؛
/// غير ذلك محاولة واحدة لتجنب الأثر المزدوج. القيم قابلة للتجاوز من Integrations:Policies:{connector}.</summary>
public sealed record ConnectorCallPolicy(bool Idempotent, int MaxAttempts = 3, int TimeoutSeconds = 15, int BaseDelayMs = 500);

/// <summary>خطأ من المزوّد. Transient: يجوز إعادة المحاولة (انقطاع، 5xx، 429، 408). Reason: unavailable | timeout | denied | rejected | not_configured.</summary>
public sealed class ConnectorCallException(string reason, string message, int? httpStatus = null, bool transient = false) : Exception(message)
{
    public string Reason { get; } = reason;
    public int? HttpStatus { get; } = httpStatus;
    public bool Transient { get; } = transient;
}

/// <summary>تنفيذ استدعاء موصل: يتحقق من المفتاح، ويطبق المهلة لكل محاولة، ويعيد المحاولة بتباعد متزايد للعمليات الآمنة فقط، ويفتح الدائرة بعد إخفاقات متتالية،
/// ويسجل كل محاولة في integ.connector_calls (العملية والحالة والمدة ورمز HTTP ورسالة خطأ منقحة — دون حمولة أو أسرار). يرمي IntegrationFailedException عند الفشل النهائي.</summary>
public interface IConnectorRuntime
{
    Task<T> ExecuteAsync<T>(string connector, string operation, string? featureKey, ConnectorCallPolicy policy, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default);
    /// <summary>حالة قاطع الدائرة: closed | open (حتى وقت) | half_open.</summary>
    (string State, int ConsecutiveFailures, DateTime? OpenUntil) Circuit(string connector);
}
