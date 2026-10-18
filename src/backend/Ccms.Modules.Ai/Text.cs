using System.Text;
using System.Text.RegularExpressions;

namespace Ccms.Modules.Ai;

/// <summary>أدوات نص عربي: تطبيع، وكلمات دالة، وتقسيم جمل، واحتواء مطبّع (للتحقق من الاقتباسات).</summary>
internal static partial class ArText
{
    private static readonly HashSet<string> Stop =
    [
        "في", "من", "على", "الى", "الي", "عن", "ما", "ماذا", "هل", "التي", "الذي", "الذين", "او", "ام", "ثم", "مع", "هذا", "هذه", "ذلك", "تلك", "كل",
        "اي", "كم", "متي", "اين", "كيف", "لماذا", "قد", "تم", "لم", "لا", "ان", "انه", "هو", "هي", "به", "بها", "له", "لها", "عند", "بعد", "قبل",
        "وقد", "كما", "حيث", "التي", "علي", "الي", "بين", "ضمن", "خلال", "حول", "لدى", "لدي",
    ];

    [GeneratedRegex("[ً-ْـ]")] private static partial Regex Diacritics();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")] private static partial Regex Separators();
    [GeneratedRegex(@"(?<=[\.!\?؟؛\n])\s*|\n+")] private static partial Regex SentenceBreak();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(ch switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', 'ة' => 'ه', _ => ch });
        return Spaces().Replace(Diacritics().Replace(sb.ToString(), ""), " ").Trim();
    }

    public static string[] Tokens(string? s, int max = 40)
    {
        var list = new List<string>();
        foreach (var raw in Separators().Split(Normalize(s)))
        {
            var w = raw;
            if (w.Length == 0 || Stop.Contains(w)) continue;
            if (w.Length > 4 && (w.StartsWith("وال") || w.StartsWith("بال") || w.StartsWith("كال"))) w = w[3..];
            else if (w.Length > 4 && w.StartsWith("لل")) w = w[2..];
            else if (w.Length > 3 && w.StartsWith("ال")) w = w[2..];
            if (w.Length < 2 || Stop.Contains(w) || list.Contains(w)) continue;
            list.Add(w);
            if (list.Count >= max) break;
        }
        return list.ToArray();
    }

    public static IEnumerable<string> Sentences(string? text) =>
        SentenceBreak().Split(text ?? "").Select(x => Clean(x)).Where(x => x.Length >= 8);

    public static string Clean(string s) => Spaces().Replace(s.Trim().TrimStart('-', '•', '*', '–', '—', '·', ' ').Trim(), " ");

    /// <summary>هل يرد الاقتباس في النص بعد التطبيع؟ (يُقبل الاقتباس الحرفي أو بفروق تشكيل ومسافات فقط)</summary>
    public static bool Contains(string haystack, string needle)
    {
        var n = Normalize(needle).Trim(' ', '.', '،', '؛', '"', '«', '»');
        return n.Length >= 6 && Normalize(haystack).Contains(n, StringComparison.Ordinal);
    }

    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    public static double Overlap(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var set = b.ToHashSet();
        return (double)a.Count(set.Contains) / a.Length;
    }
}
