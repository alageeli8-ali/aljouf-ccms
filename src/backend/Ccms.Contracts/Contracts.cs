using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Contracts;

// ---------------- الوحدات ----------------

/// <summary>كل وحدة أعمال تسجل خدماتها ونقاط نهايتها بهذه الطريقة الموحدة (03 §4).</summary>
public interface IModule
{
    string Code { get; }
    bool IsOptional => false;
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder api);
    /// <summary>نقاط عامة خارج مجموعة الواجهة المحمية (مثل الاستدعاءات الراجعة لمزوّد موقّعة برسالة لا بجلسة).</summary>
    void MapPublicEndpoints(IEndpointRouteBuilder app) { }
}

// ---------------- الهوية والصلاحيات ----------------

public static class Roles
{
    public const string SystemAdmin = "system_admin", GovernanceOfficer = "governance_officer", Executive = "executive",
        Auditor = "auditor", ExternalRepresentative = "external_representative", Employee = "employee",
        Chair = "chair", ViceChair = "vice_chair", Secretary = "secretary", Member = "member", Alternate = "alternate", Observer = "observer",
        TaskExecutor = "task_executor", TaskFollowUp = "task_follow_up";

    public static readonly string[] BodyRoles = [Chair, ViceChair, Secretary, Member, Alternate, Observer];
    public static readonly string[] SystemRoles = [SystemAdmin, GovernanceOfficer, Executive, Auditor, ExternalRepresentative, Employee];
}

public static class Permissions
{
    public const string AdminUsersManage = "admin.users.manage", AdminSettingsView = "admin.settings.view",
        AdminSettingsManage = "admin.settings.manage", OrgManage = "org.manage", BodiesCreate = "bodies.create",
        ReportsExecutive = "reports.executive", AuditRead = "audit.read", UsersDirectory = "users.directory",
        BodyView = "body.view", BodyUpdate = "body.update", BodyMembersManage = "body.members.manage",
        MeetingView = "meeting.view", MeetingManage = "meeting.manage",
        MinutesView = "minutes.view", MinutesEdit = "minutes.edit", MinutesSign = "minutes.sign", MinutesApprove = "minutes.approve",
        ResolutionView = "resolution.view", ResolutionManage = "resolution.manage",
        TaskView = "task.view", TaskManage = "task.manage", TaskUpdate = "task.update", TaskVerify = "task.verify",
        TaskComment = "task.comment", TaskExtensionApprove = "task.extension.approve", TaskEscalate = "task.escalate",
        TaskRecordExternal = "task.record_external", TaskLettersManage = "task.letters.manage", TaskWorkflowManage = "task.workflow.manage",
        ESignRequest = "esign.request", ESignManage = "esign.manage", ESignVerify = "esign.verify", ESignRoutesManage = "esign.routes.manage",
        DocumentView = "document.view", DocumentUpload = "document.upload", ReportsBody = "reports.body",
        PortalAccess = "portal.access",
        BodyLifecycle = "body.lifecycle.manage", BodyPolicy = "body.policy.manage", BodyDelegate = "body.delegate",
        MeetingAttendance = "meeting.attendance", MeetingInvite = "meeting.invite",
        MinutesComment = "minutes.comment", MinutesReviewSecretary = "minutes.review_secretary", MinutesTemplates = "minutes.templates.manage",
        VoteCast = "vote.cast", VoteManage = "vote.manage", VoteViewIndividual = "vote.view_individual";
}

public enum ScopeType { System, Body, Task, Document, Record }

/// <summary>نطاق تقييم الصلاحية: النظام، أو جهة، أو مهمة، أو مستند، أو سجل يُرد إلى جهته (اجتماع، محضر، قرار).</summary>
public sealed record Scope(ScopeType Type, Guid? Id = null, string? RecordType = null)
{
    public static readonly Scope System = new(ScopeType.System);
    public static Scope Body(Guid id) => new(ScopeType.Body, id);
    public static Scope Task(Guid id) => new(ScopeType.Task, id);
    public static Scope Document(Guid id) => new(ScopeType.Document, id);
    public static Scope Record(string type, Guid id) => new(ScopeType.Record, id, type);
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    Guid? SessionId { get; }
    string DisplayName { get; }
    string? IpAddress { get; }
    string CorrelationId { get; }
}

/// <summary>شرط الجهات المسموحة لاستعلامات القوائم: إما كل الجهات، أو قائمة محددة.</summary>
public sealed record BodyFilter(bool All, Guid[] BodyIds)
{
    public static readonly BodyFilter None = new(false, []);
}

public interface IAccessService
{
    /// <summary>يرمي 401 لغير المصادق، و404 لما خارج نطاق الاطلاع، و403 لما داخل النطاق دون الصلاحية.</summary>
    Task DemandAsync(string permission, Scope scope, CancellationToken ct = default);
    Task<bool> HasAsync(string permission, Scope scope, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetPermissionsAsync(Scope scope, CancellationToken ct = default);
    Task<BodyFilter> GetBodyFilterAsync(string permission, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSystemRolesAsync(Guid userId, CancellationToken ct = default);
    /// <summary>صحيح إن كان اطلاع المستخدم على المهمة عبر بوابة الجهات الخارجية فقط (لا عضوية ولا علاقة داخلية).
    /// يرى حينها البطاقة المقيدة والمستندات المشاركة صراحة فقط.</summary>
    Task<bool> IsPortalOnlyAsync(Guid taskId, CancellationToken ct = default);
    void Invalidate(Guid userId);
}

public sealed record BodyRole(Guid BodyId, string Role);

public interface IBodyMembershipQuery
{
    Task<IReadOnlyList<BodyRole>> GetActiveRolesAsync(Guid userId, DateOnly on, CancellationToken ct = default);
}

/// <summary>أطراف المهمة لتقييم الصلاحية: المنفذ (المكلَّف أو المنفذ المسمى أو مدير الإدارة المكلفة أو ممثل الجهة)، ومسؤول المتابعة، والمتحقق.</summary>
public sealed record TaskRelation(Guid TaskId, Guid BodyId, Guid? AssigneeUserId, Guid? AccountableUserId, Guid? AssigneeOrgUnitManagerId, Guid? AssigneeExternalEntityId,
    Guid? ExecutorUserId = null, Guid? VerifierUserId = null, Guid? ExecutingUnitManagerId = null);

public interface ITaskRelationQuery
{
    Task<TaskRelation?> GetAsync(Guid taskId, CancellationToken ct = default);
}

/// <summary>يرد سجلًا (اجتماع، محضر، قرار) إلى جهته؛ تسجله الوحدة المالكة للسجل.</summary>
public interface IRecordBodyResolver
{
    string RecordType { get; }
    Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default);
}

public sealed record DocumentOwner(string OwnerType, Guid OwnerId, bool ExternalVisible = false);

public interface IDocumentOwnerQuery
{
    Task<IReadOnlyList<DocumentOwner>> GetOwnersAsync(Guid documentId, CancellationToken ct = default);
}

public sealed record UserSummary(Guid Id, string Email, string DisplayNameAr, string? DisplayNameEn, Guid? ExternalEntityId, string Status);

public interface IUserQuery
{
    Task<UserSummary?> GetAsync(Guid userId, CancellationToken ct = default);
}

// ---------------- الإعدادات المركزية ----------------

public static class FeatureKeys
{
    public const string AiEnabled = "AI_ENABLED", AiMinutesSummary = "AI_MINUTES_SUMMARY_ENABLED",
        AiDecisionExtraction = "AI_DECISION_EXTRACTION_ENABLED", AiTaskExtraction = "AI_TASK_EXTRACTION_ENABLED",
        AiSmartSearch = "AI_SMART_SEARCH_ENABLED", AiAudioTranscription = "AI_AUDIO_TRANSCRIPTION_ENABLED",
        ESignatureEnabled = "E_SIGNATURE_ENABLED", ESignatureProvider = "E_SIGNATURE_PROVIDER",
        ESignatureMethod = "E_SIGNATURE_METHOD", ESignatureMemberRequired = "E_SIGNATURE_MEMBER_REQUIRED",
        ESignatureChairRequired = "E_SIGNATURE_CHAIR_REQUIRED", ESignatureSecretaryRequired = "E_SIGNATURE_SECRETARY_REQUIRED",
        ESignatureSequential = "E_SIGNATURE_SEQUENTIAL", ESignaturePdfOutput = "E_SIGNATURE_PDF_OUTPUT",
        ESignatureReauthRequired = "E_SIGNATURE_REAUTH_REQUIRED", ExternalPortalEnabled = "EXTERNAL_PORTAL_ENABLED",
        EmailIntegrationEnabled = "EMAIL_INTEGRATION_ENABLED", CorrespondenceIntegrationEnabled = "CORRESPONDENCE_INTEGRATION_ENABLED",
        SsoEnabled = "SSO_ENABLED";
}

public interface IFeatureService
{
    /// <summary>القيمة الفعلية بعد تطبيق المفتاح الأعلى وأقفال البيئة. تُقرأ من القاعدة على الخادم دائمًا.</summary>
    Task<bool> IsEnabledAsync(string key, CancellationToken ct = default);
    Task<string> GetValueAsync(string key, CancellationToken ct = default);
    /// <summary>يرمي FeatureDisabledException (404) إن كانت الخاصية غير مفعلة فعليًا.</summary>
    Task DemandEnabledAsync(string key, CancellationToken ct = default);
}

/// <summary>أثر تغيير إعداد على عمليات جارية؛ تسجله الوحدة التي تملك تلك العمليات.</summary>
public sealed record FeatureImpactItem(string Code, string DescriptionAr, string DescriptionEn, int Count);

public interface IFeatureImpactProvider
{
    IReadOnlyCollection<string> FeatureKeys { get; }
    Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default);
}

public enum ConnectorState { NotConfigured, Healthy, Degraded, Failing, Disabled }

public sealed record ConnectorHealth(ConnectorState State, string Detail, int? LatencyMs = null)
{
    public string StateCode => State switch
    {
        ConnectorState.NotConfigured => "not_configured",
        ConnectorState.Healthy => "healthy",
        ConnectorState.Degraded => "degraded",
        ConnectorState.Failing => "failing",
        _ => "disabled",
    };
}

/// <summary>مزوّد خدمة خارجي. حالة اتصاله مستقلة تمامًا عن تفعيل الخاصية (المرحلة 1، البند 4).</summary>
public interface IConnector
{
    string Code { get; }
    string NameAr { get; }
    string NameEn { get; }
    Task<ConnectorHealth> CheckAsync(CancellationToken ct = default);
}

public interface IConnectorStatusStore
{
    Task<ConnectorHealth?> GetAsync(string code, CancellationToken ct = default);
    /// <summary>يفحص المزوّد ويحفظ النتيجة؛ يرمي ProviderUnavailableException إن لم يكن سليمًا.</summary>
    Task DemandAvailableAsync(string code, CancellationToken ct = default);
}

// ---------------- التدقيق والإشعارات والوقت ----------------

public sealed record AuditEntry(string Module, string Action, string EntityType, string? EntityId,
    object? Before = null, object? After = null, string Outcome = "success", string? Classification = null,
    Guid? ActorUserId = null);

public interface IAuditWriter
{
    /// <summary>يكتب في المعاملة نفسها إن مُررت، فيُرجع التدقيق مع العملية عند فشلها.</summary>
    Task WriteAsync(ISql sql, AuditEntry entry, CancellationToken ct = default);
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record NotificationRequest(Guid UserId, string EventType, string TitleAr, string TitleEn, string? Link,
    string DedupeKey, string Severity = "info");

public interface INotifier
{
    Task NotifyAsync(ISql sql, NotificationRequest request, CancellationToken ct = default);
}

public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly TodayLocal { get; }
}

public interface INumberingService
{
    /// <summary>يحجز الرقم التالي داخل المعاملة نفسها (قفل صف) — بلا تكرار عند التزامن.</summary>
    Task<int> NextAsync(ISql sql, string scopeKey, int year, CancellationToken ct = default);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>منفذ البريد (03 §5.5). تنفيذه في وحدة التكاملات الاختيارية؛ بدونه تُسجَّل الرسائل في الصادر ولا تُرسل.</summary>
public interface IEmailSender
{
    string ConnectorCode { get; }
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>صلاحيات تنشأ من علاقة مباشرة بسجل لا من العضوية، مثل الضيف المدعو لاجتماع.</summary>
public interface IRecordRelationProvider
{
    string RecordType { get; }
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid recordId, Guid userId, CancellationToken ct = default);
}

public sealed record ActivityEntry(long Seq, DateTime OccurredAt, Guid? ActorUserId, string? ActorName, string Module, string Action,
    string EntityType, string? EntityId, string Outcome, string? Details);

/// <summary>السجل الزمني لنشاط سجلٍ ما، مقروءًا من سجل التدقيق. الوحدة المستدعية تتحقق من صلاحية الاطلاع على السجل أولًا.</summary>
public interface IActivityQuery
{
    Task<IReadOnlyList<ActivityEntry>> ForEntityAsync(string entityType, Guid entityId, int limit = 200, CancellationToken ct = default);
}
