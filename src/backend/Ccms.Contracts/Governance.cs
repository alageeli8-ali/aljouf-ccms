using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ccms.Contracts;

/// <summary>قاعدة النصاب في سياسة الجهة (08 §5.1). تُحفظ jsonb وتُثبَّت لقطة منها في كل فحص نصاب.</summary>
public sealed record QuorumRule
{
    /// <summary>majority: الأغلبية المطلقة للأعضاء المصوتين؛ fraction: نسبة؛ fixed: عدد ثابت.</summary>
    public string Type { get; init; } = "majority";
    public int Numerator { get; init; } = 2;
    public int Denominator { get; init; } = 3;
    public int? FixedCount { get; init; }
    public bool ChairRequired { get; init; }
    public bool CountRemote { get; init; } = true;
    public bool CountProxies { get; init; } = true;
    public bool AllowOverride { get; init; }

    public static readonly string[] Types = ["majority", "fraction", "fixed"];
}

/// <summary>قاعدة التصويت (08 §6.1). تُطبق فعليًا في المرحلة 3، وتُدار هنا ضمن سياسة الجهة.</summary>
public sealed record VotingRule
{
    public string Threshold { get; init; } = "majority_of_votes_cast";
    public string Abstentions { get; init; } = "excluded";
    public string TieBreaker { get; init; } = "chair_casting_vote";
    public bool SecretAllowed { get; init; } = true;

    public static readonly string[] Thresholds = ["majority_of_votes_cast", "majority_of_present", "absolute_majority_of_members", "two_thirds_of_present", "unanimity"];
    public static readonly string[] TieBreakers = ["chair_casting_vote", "motion_fails", "revote"];
}

public sealed record BodyPolicy(
    Guid Id, Guid BodyId, int Version, DateOnly EffectiveFrom, QuorumRule Quorum, VotingRule Voting,
    bool SignoffRequired, int MinutesReviewDays, string ExtensionApprover, bool AllowProxy, int MaxProxiesPerMember,
    bool AllowRemoteAttendance, bool AllowCirculation, DateTime CreatedAt, string? CreatedByName, MinutesWorkflow? Minutes = null)
{
    public MinutesWorkflow MinutesFlow => Minutes ?? new MinutesWorkflow();
}

public static class PolicyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static QuorumRule Quorum(string json) => JsonSerializer.Deserialize<QuorumRule>(json, Options) ?? new QuorumRule();
    public static VotingRule Voting(string json) => JsonSerializer.Deserialize<VotingRule>(json, Options) ?? new VotingRule();

    public static (bool Required, int MaxProxies) Signoff(string json)
    {
        using var d = JsonDocument.Parse(json);
        var required = d.RootElement.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
        var max = d.RootElement.TryGetProperty("maxProxiesPerMember", out var m) && m.TryGetInt32(out var mv) ? mv : 1;
        return (required, max);
    }
}

public interface IBodyPolicyQuery
{
    /// <summary>السياسة السارية للجهة في تاريخ معيّن (أعلى إصدار تاريخ سريانه ≤ التاريخ).</summary>
    Task<BodyPolicy?> GetEffectiveAsync(Guid bodyId, DateOnly on, CancellationToken ct = default);
    Task<BodyPolicy?> GetByIdAsync(Guid policyId, CancellationToken ct = default);
}
