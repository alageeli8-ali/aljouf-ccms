using System.Globalization;
using System.Text.RegularExpressions;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;

namespace Ccms.Modules.Ai;

/// <summary>إعدادات بيئة الخادم التي لا تُغيَّر من الواجهة: المضيفون المسموحون للمزوّد (سيادة البيانات).</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    /// <summary>لا يُرسل أي نص إلا إلى مضيف في هذه القائمة؛ AI_BASE_URL يُرفض إن لم يكن مضيفه منها.</summary>
    public string[] AllowedHosts { get; set; } = [];
    /// <summary>السماح بـ http (لا https) — في بيئات التطوير والاختبار فقط؛ يُتجاهل في الإنتاج.</summary>
    public bool AllowHttp { get; set; }
    public int WorkerIntervalSeconds { get; set; } = 30;
}

/// <summary>لقطة من الإعدادات المركزية وقت الطلب (تُقرأ من القاعدة على الخادم دائمًا).</summary>
internal sealed record AiSettings(bool Enabled, string Provider, string Hosting, string MaxClassification, string BaseUrl, string Model, string TranscriptionModel,
    int TimeoutSeconds, int DailyPerUser, long MonthlyRequests, long MonthlyTokens, int AlertPercent, int RetentionDays)
{
    public static async Task<AiSettings> LoadAsync(IFeatureService f, CancellationToken ct)
    {
        async Task<string> V(string k) => await f.GetValueAsync(k, ct);
        static long L(string s, long d) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
        return new AiSettings(
            await f.IsEnabledAsync(FeatureKeys.AiEnabled, ct), await V(FeatureKeys.AiProvider), await V(FeatureKeys.AiProviderHosting),
            await V(FeatureKeys.AiMaxClassification), (await V(FeatureKeys.AiBaseUrl)).Trim(), (await V(FeatureKeys.AiModel)).Trim(),
            (await V(FeatureKeys.AiTranscriptionModel)).Trim(), (int)L(await V(FeatureKeys.AiTimeoutSeconds), 60), (int)L(await V(FeatureKeys.AiDailyRequestsPerUser), 50),
            L(await V(FeatureKeys.AiMonthlyRequestLimit), 3000), L(await V(FeatureKeys.AiMonthlyTokenLimit), 3_000_000),
            (int)L(await V(FeatureKeys.AiUsageAlertPercent), 80), (int)L(await V(FeatureKeys.AiRetentionDays), 90));
    }

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 300));
}

/// <summary>الوظائف العشر ومفتاح كل منها (22 §2). المفتاح الأعلى AI_ENABLED يحجبها جميعًا.</summary>
internal static class AiFunctions
{
    public const string MinutesSummary = "minutes_summary", AgendaDraft = "agenda_draft", MinutesDraft = "minutes_draft", Transcription = "transcription",
        ExtractDecisions = "extract_decisions", ExtractTasks = "extract_tasks", LinkTask = "link_task", SmartSearch = "smart_search",
        ExecutiveSummary = "executive_summary", Ask = "ask";

    public static readonly IReadOnlyDictionary<string, string> Flag = new Dictionary<string, string>
    {
        [MinutesSummary] = FeatureKeys.AiMinutesSummary, [AgendaDraft] = FeatureKeys.AiMinutesSummary, [MinutesDraft] = FeatureKeys.AiMinutesSummary,
        [ExecutiveSummary] = FeatureKeys.AiMinutesSummary, [Transcription] = FeatureKeys.AiAudioTranscription,
        [ExtractDecisions] = FeatureKeys.AiDecisionExtraction, [ExtractTasks] = FeatureKeys.AiTaskExtraction, [LinkTask] = FeatureKeys.AiTaskExtraction,
        [SmartSearch] = FeatureKeys.AiSmartSearch, [Ask] = FeatureKeys.AiSmartSearch,
    };

    public static readonly string[] All = [MinutesSummary, AgendaDraft, MinutesDraft, Transcription, ExtractDecisions, ExtractTasks, LinkTask, SmartSearch, ExecutiveSummary, Ask];

    /// <summary>وظائف على سجل محدد: إن تجاوز أي مصدر التصنيف المسموح يُرفض الطلب كله. وظائف الاستعلام تستبعد المتجاوز وتكمل بما يُسمح.</summary>
    public static bool IsSubjectBound(string fn) => fn is MinutesSummary or AgendaDraft or MinutesDraft or ExtractDecisions or ExtractTasks or LinkTask or Transcription;
}

/// <summary>سياسة التصنيف: لا يُرسل للمزوّد سجل أعلى من AI_MAX_CLASSIFICATION، ولا أعلى من «مقيد» لمزوّد خارجي مهما كان الإعداد.</summary>
internal static class DataPolicy
{
    public const int ExternalCeiling = 1; // restricted

    public static int AllowedRank(AiSettings s)
    {
        var configured = s.MaxClassification == "none" ? -1 : Classification.Rank(s.MaxClassification);
        return s.Hosting == "external" ? Math.Min(configured, ExternalCeiling) : configured;
    }

    public static string AllowedLabel(AiSettings s) => AllowedRank(s) < 0 ? "none" : Classification.Levels[AllowedRank(s)];

    public static int Rank(string? level) => Classification.Rank(level ?? "restricted") is var r && r < 0 ? 1 : r;
}

/// <summary>تقليل البيانات للمزوّد الخارجي: حجب أرقام الهوية والإقامة والجوال والبريد والآيبان قبل الإرسال.</summary>
internal static partial class Redactor
{
    [GeneratedRegex(@"(?<!\d)[12]\d{9}(?!\d)")] private static partial Regex NationalId();
    [GeneratedRegex(@"(?<!\d)(?:\+?966|00966|0)5\d{8}(?!\d)")] private static partial Regex Mobile();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"\bSA\d{2}[0-9A-Z]{20}\b", RegexOptions.IgnoreCase)] private static partial Regex Iban();

    public const string Mask = "[محجوب]";

    public static (string Text, int Count) Apply(string text)
    {
        var count = 0;
        string Repl(Match _) { count++; return Mask; }
        text = Iban().Replace(text, Repl);
        text = Email().Replace(text, Repl);
        text = Mobile().Replace(text, Repl);
        text = NationalId().Replace(text, Repl);
        return (text, count);
    }
}

// ---------------- الأخطاء ----------------

/// <summary>طلب للمزوّد فشل بعد وصوله (رد خاطئ، أو بيانات اعتماد مرفوضة، أو مخرج غير صالح) — 502.</summary>
internal sealed class AiRequestFailedException(string reason, string message)
    : AppException("ai-request-failed", 502, message, new Dictionary<string, object?> { ["reason"] = reason });

internal sealed class AiQuotaExceededException(string limit, string message)
    : AppException("ai-quota-exceeded", 429, message, new Dictionary<string, object?> { ["limit"] = limit });

internal sealed class AiClassificationBlockedException(string highest, string allowed, int count)
    : AppException("ai-classification-blocked", 403,
        $"لا يُسمح بإرسال هذه البيانات لمزوّد الذكاء الاصطناعي: تصنيفها ({highest}) أعلى من المسموح ({allowed}) وفق السياسة المعتمدة",
        new Dictionary<string, object?> { ["highest"] = highest, ["allowed"] = allowed, ["blockedSources"] = count });

/// <summary>عُطّلت الخدمة أثناء المعالجة: حُفظت النتيجة محجوبة ولا تُعرض حتى إعادة التفعيل والإفراج عنها صراحة.</summary>
internal sealed class AiHeldException(Guid outputId)
    : AppException("ai-disabled-during-processing", 409, "عُطّلت خدمة الذكاء الاصطناعي أثناء المعالجة؛ حُفظت النتيجة محجوبة ولن تُعرض حتى إعادة التفعيل",
        new Dictionary<string, object?> { ["outputId"] = outputId });
