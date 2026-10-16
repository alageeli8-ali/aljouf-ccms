using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using System.Text.Json.Serialization;

namespace Ccms.Contracts;

/// <summary>مستوى تنبيه أو تصعيد في سياسة الجهة (08 §4.3). <c>due_soon</c> تنبيه قبل الاستحقاق بإزاحة سالبة، و<c>overdue*</c> تصعيد بعد التأخر.</summary>
public sealed record EscalationLevel(int Level, string Trigger, int OffsetDays, string[] Notify);

/// <summary>قاعدة التنبيه والتصعيد لكل جهة؛ قابلة للتهيئة ضمن إصدار السياسة.</summary>
public sealed record EscalationRule
{
    public bool WorkingDays { get; init; } = true;
    public int RepeatOverdueEveryDays { get; init; } = 7;
    public int ReviewOverdueDays { get; init; } = 3;
    public List<EscalationLevel> Levels { get; init; } = [];

    /// <summary>الأدوار التي يمكن أن تتلقى التنبيه أو التصعيد.</summary>
    public static readonly string[] Recipients =
        ["assignee", "accountable", "verifier", "secretary", "chair", "vice_chair", "assignee_org_unit_manager", "governance_officer", "executive"];

    public static readonly string[] Triggers = ["due_soon", "overdue", "overdue_l2", "overdue_l3", "overdue_l4"];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static EscalationRule Default => new()
    {
        Levels =
        [
            new(0, "due_soon", -3, ["assignee", "accountable"]),
            new(1, "overdue", 0, ["assignee", "accountable", "secretary"]),
            new(2, "overdue_l2", 7, ["assignee", "accountable", "secretary", "assignee_org_unit_manager"]),
            new(3, "overdue_l3", 14, ["secretary", "chair"]),
        ],
    };

    public static EscalationRule Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var r = JsonSerializer.Deserialize<EscalationRule>(json, Options);
            return r is null || r.Levels.Count == 0 ? Default : r;
        }
        catch (JsonException) { return Default; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>بوابة نظام المراسلات (اختيارية). لا تُعد جاهزة إلا بتكامل فعلي معتمد؛ وإلا لا يُدّعى إرسال الخطاب.</summary>
public interface ICorrespondenceGateway
{
    Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default);
    /// <summary>يُرسل الخطاب ويعيد رقم الصادر من النظام. يُستدعى فقط حين تكون البوابة جاهزة.</summary>
    Task<string> SubmitAsync(CorrespondenceLetter letter, CancellationToken ct = default);
}

public sealed record CorrespondenceLetter(Guid LetterId, string ReferenceNo, string RecipientName, string Subject, string BodyText, byte[] Pdf);

/// <summary>أثر سير المهام على القرار المصدر؛ تنفذه وحدة القرارات.</summary>
public interface IResolutionExecutionHook
{
    /// <summary>بدء أول مهمة يجعل القرار الصادر «قيد التنفيذ».</summary>
    Task TaskStartedAsync(ISql tx, Guid resolutionId, Guid by, CancellationToken ct = default);
    /// <summary>اكتمال كل مهام القرار يُشعر القائمين عليه لتسجيل تنفيذه.</summary>
    Task TasksCompletedAsync(ISql tx, Guid resolutionId, Guid by, CancellationToken ct = default);
}
