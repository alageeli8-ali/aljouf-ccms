using System.Text.Json;
using Ccms.BuildingBlocks.Data;

namespace Ccms.Contracts;

/// <summary>دورة اعتماد المحضر في سياسة الجهة. كل جهة تختار خطواتها؛ لا يُفرض توقيع ولا موافقة موحدة (المرحلة 3، ب).</summary>
public sealed record MinutesWorkflow
{
    /// <summary>مراجعة أمين السر قبل عرض المحضر على الرئيس (مفيدة حين يُعد المحضر مقرر غير أمين السر).</summary>
    public bool SecretaryReview { get; init; }
    public bool ChairReview { get; init; } = true;
    /// <summary>جولة ملاحظات الأعضاء بمهلة.</summary>
    public bool MemberReview { get; init; } = true;
    public int MemberReviewDays { get; init; } = 3;
    /// <summary>none | present_members | present_members_and_chair | all_members</summary>
    public string Signatures { get; init; } = "none";
    /// <summary>طريقة التوقيع البديلة حين تكون وحدة التوقيع الإلكتروني معطلة أو غير جاهزة: attestation | wet_signature</summary>
    public string SignatureFallback { get; init; } = "attestation";
    /// <summary>chair: يعتمد الرئيس أو المفوض بصلاحياته داخل النظام. recorded_external: يسجل أمين السر اعتماد صاحب الصلاحية خارج النظام بمرجع ودليل.</summary>
    public string FinalApproval { get; init; } = "chair";
    /// <summary>full: التعديل بعد الاعتماد يمر بالدورة كاملة. final_only: يُعاد للاعتماد النهائي مباشرة.</summary>
    public string AmendmentReapproval { get; init; } = "full";
    public string TemplateCode { get; init; } = "standard";

    public static readonly string[] SignatureModes = ["none", "present_members", "present_members_and_chair", "all_members"];
    public static readonly string[] Fallbacks = ["attestation", "wet_signature"];
    public static readonly string[] FinalApprovalModes = ["chair", "recorded_external"];
    public static readonly string[] ReapprovalModes = ["full", "final_only"];

    public static MinutesWorkflow Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new MinutesWorkflow() : JsonSerializer.Deserialize<MinutesWorkflow>(json, PolicyJson.Options) ?? new MinutesWorkflow();

    public string ToJson() => JsonSerializer.Serialize(this, PolicyJson.Options);

    /// <summary>الخطوات المفعلة بالترتيب بعد المسودة.</summary>
    public IReadOnlyList<string> Steps(bool amendment = false)
    {
        var steps = new List<string>();
        if (!(amendment && AmendmentReapproval == "final_only"))
        {
            if (SecretaryReview) steps.Add("secretary_review");
            if (ChairReview) steps.Add("chair_review");
            if (MemberReview) steps.Add("member_review");
        }
        if (Signatures != "none") steps.Add("pending_signatures");
        steps.Add("pending_final_approval");
        return steps;
    }
}

/// <summary>نتيجة آخر تصويت مغلق على قرار مقترح.</summary>
public sealed record MotionOutcome(Guid MotionId, Guid ResolutionId, string Method, string Result, string TallyJson, DateTime ClosedAt);

/// <summary>تقرأ وحدة المحاضر والقرارات نتائج التصويت عبر هذا العقد دون الاعتماد على وحدة الاجتماعات.</summary>
public interface IVotingQuery
{
    Task<IReadOnlyList<MotionOutcome>> LatestOutcomesAsync(ISql sql, Guid meetingId, CancellationToken ct = default);
    Task<int> OpenMotionsAsync(ISql sql, Guid meetingId, CancellationToken ct = default);
}

public sealed record IssueSummary(IReadOnlyList<Guid> Issued, IReadOnlyList<Guid> NotIssued, IReadOnlyList<string> Numbers);

/// <summary>إصدار قرارات المحضر عند اعتماده (ترقيم، وحالة، وربط بالإصدار المعتمد) — تنفذه وحدة القرارات داخل معاملة الاعتماد.</summary>
public interface IResolutionIssuer
{
    Task<IssueSummary> IssueForMinutesAsync(ISql tx, Guid minutesId, Guid minutesVersionId, Guid by, CancellationToken ct = default);
}

/// <summary>تفعيل المهام المسودة المرتبطة بقرارات المحضر عند اعتماده — تنفذه وحدة المهام.</summary>
public interface ITaskActivator
{
    Task<int> ActivateAsync(ISql tx, IReadOnlyList<Guid> issuedResolutions, IReadOnlyList<Guid> notIssuedResolutions, Guid by, CancellationToken ct = default);
}

public sealed record StoredDocument(Guid Id, string Sha256, long Size);

/// <summary>حفظ مستند يولّده النظام (مثل PDF المحضر المعتمد) في مخزن المستندات وربطه بمالكه.</summary>
public interface IDocumentStore
{
    Task<StoredDocument> SaveAsync(ISql tx, string fileName, string contentType, byte[] content, string ownerType, Guid ownerId,
        string purpose, string classification, Guid by, CancellationToken ct = default);
    Task<byte[]?> ReadAsync(Guid documentId, CancellationToken ct = default);
    /// <summary>هل المستند مرتبط بالمالك المحدد؟ (للتحقق من أدلة التوقيع الورقي والاعتماد الخارجي).</summary>
    Task<bool> IsLinkedAsync(ISql sql, Guid documentId, string ownerType, Guid ownerId, CancellationToken ct = default);
}

/// <summary>بوابة التوقيع الإلكتروني. تنفيذها الفعلي في وحدة التوقيع (المرحلة 6)؛ قبل ذلك لا تكون جاهزة،
/// فيعمل مسار الاعتماد البديل المحدد في سياسة الجهة ويُوثَّق سببه.</summary>
public interface ISignatureGateway
{
    /// <summary>جاهزة لاستقبال طلبات التوقيع: الوحدة مفعلة والمزوّد سليم والتكامل منفذ.</summary>
    Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default);
}
