using System.ComponentModel.DataAnnotations;

namespace Ccms.BuildingBlocks.Web;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public sealed record PageQuery(int Page, int PageSize)
{
    public int Offset => (Page - 1) * PageSize;

    public static PageQuery From(int? page, int? pageSize) =>
        new(Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 25, 1, 100));
}

public static class Classification
{
    public static readonly string[] Levels = ["public", "restricted", "secret", "top_secret"];
    public static int Rank(string level) => Array.IndexOf(Levels, level);
    public static bool IsValid(string? level) => level is not null && Rank(level) >= 0;
}

/// <summary>يقبل قيمة من قائمة محددة فقط.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class OneOfAttribute(params string[] allowed) : ValidationAttribute
{
    public string[] Allowed { get; } = allowed;

    protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
    {
        if (value is null) return ValidationResult.Success;
        if (value is string s && Allowed.Contains(s)) return ValidationResult.Success;
        if (value is IEnumerable<string> list && list.All(Allowed.Contains)) return ValidationResult.Success;
        return new ValidationResult($"القيمة غير مسموحة؛ المسموح: {string.Join("، ", Allowed)}", [ctx.MemberName!]);
    }
}

public static class SqlText
{
    /// <summary>نمط بحث آمن لـ ILIKE.</summary>
    public static string? Like(string? q) =>
        string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
