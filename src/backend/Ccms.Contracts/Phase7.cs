namespace Ccms.Contracts;

// ---------------- مخزن الأسرار (22 §4) ----------------

public static class SecretNames
{
    /// <summary>مفتاح API لمزوّد الذكاء الاصطناعي. لا يُخزن في الإعدادات ولا يُعاد في أي استجابة أو سجل.</summary>
    public const string AiApiKey = "AI_API_KEY";
    /// <summary>سر تطبيق Microsoft Entra ID (client credentials) لموصلات الدليل والاجتماعات المرئية ومستودع الوثائق عبر Microsoft Graph.</summary>
    public const string GraphClientSecret = "GRAPH_CLIENT_SECRET";
    /// <summary>مفتاح واجهة نظام المراسلات.</summary>
    public const string CorrespondenceApiKey = "CORRESPONDENCE_API_KEY";
    /// <summary>كلمة مرور حساب SMTP المؤسسي (بديل آمن عن Integrations:Email:Password).</summary>
    public const string SmtpPassword = "SMTP_PASSWORD";
    public static readonly string[] All = [AiApiKey, GraphClientSecret, CorrespondenceApiKey, SmtpPassword];
}

/// <summary>مصدر السر: environment (بيئة الخادم أو مخزن أسرار خارجي مركّب فيها، للقراءة فقط) أو database (مشفر بمفتاح رئيسي من البيئة) أو none.</summary>
public sealed record SecretInfo(string Name, string Source, bool CanStore, DateTime? UpdatedAt, string? UpdatedBy, string? Fingerprint);

public interface ISecretStore
{
    /// <summary>القيمة الصريحة للاستخدام داخل الخادم فقط؛ لا تُمرر لأي استجابة أو سجل.</summary>
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task<SecretInfo> InfoAsync(string name, CancellationToken ct = default);
}

/// <summary>تحقق إضافي من قيمة إعداد قبل حفظها (مثل اشتراط أن يكون عنوان المزوّد ضمن المضيفين المسموحين من البيئة).
/// تسجله الوحدة المالكة للإعداد؛ يعيد رسالة الرفض أو null.</summary>
public interface IFeatureValueValidator
{
    IReadOnlyCollection<string> FeatureKeys { get; }
    Task<string?> ValidateAsync(string key, string value, CancellationToken ct = default);
}

// ---------------- البحث التقليدي (أساسي، يعمل دون الذكاء الاصطناعي) ----------------

public static class SearchTypes
{
    public const string Minutes = "minutes", Resolution = "resolution", Task = "task", Meeting = "meeting";
    public static readonly string[] All = [Minutes, Resolution, Task, Meeting];
    /// <summary>البحث الموحد (23 §3) يضيف الجهات والأعضاء والمرفقات؛ All يبقى أنواع السجلات التي يستند إليها المساعد الذكي.</summary>
    public const string Body = "body", Member = "member", Document = "document";
    public static readonly string[] Every = [.. All, Body, Member, Document];
}

/// <summary>استعلام منظم: نص (كلمات) واختياريًا نوع السجل والجهة والحالة والتأخر والفترة. كل النتائج مقيدة بصلاحيات المستخدم الحالي.</summary>
public sealed record SearchQuery(string? Text, string[]? Types = null, Guid? BodyId = null, string? Status = null, bool OverdueOnly = false,
    DateOnly? From = null, DateOnly? To = null, int Limit = 20);

public sealed record SearchHit(string Type, Guid Id, string Title, string? Reference, Guid BodyId, string BodyName, string? Status, DateTime? Date,
    string Snippet, string Classification, string Link, int Score, DateOnly? DueDate = null);

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);
}
