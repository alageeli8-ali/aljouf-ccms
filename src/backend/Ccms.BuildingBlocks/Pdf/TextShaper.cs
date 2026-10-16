using System.Globalization;
using System.Text;

namespace Ccms.BuildingBlocks.Pdf;

/// <summary>محرف جاهز للرسم: رقمه في الخط، وعرضه، ونصه الأصلي (لاستخراج النص من PDF)، واتجاهه.</summary>
public sealed record ShapedGlyph(FontFace Font, int Gid, double Advance, string Source, BidiClass Class, bool IsSpace)
{
    public int Level { get; set; }
}

public enum BidiClass { L, R, EN, AN, WS, ON }

/// <summary>زوج خطين لكل وزن: عربي ولاتيني، يُختار بينهما لكل محرف.</summary>
public sealed class FontFace(string key, TrueTypeFont font)
{
    public string Key { get; } = key;
    public TrueTypeFont Font { get; } = font;
    /// <summary>المحارف المستخدمة في المستند ونصها الأصلي (للاقتطاع وخريطة ToUnicode).</summary>
    public Dictionary<int, string> Used { get; } = new();
}

public sealed class FontSet
{
    public FontFace Arabic { get; }
    public FontFace Latin { get; }

    public FontSet(FontFace arabic, FontFace latin) { Arabic = arabic; Latin = latin; }
}

/// <summary>تشكيل الحروف العربية بالأشكال السياقية (Presentation Forms-B) وترتيب النص ثنائي الاتجاه (تبسيط لخوارزمية Unicode Bidi UAX #9
/// يكفي للنصوص الإدارية: عربي مع أرقام وتواريخ وعبارات لاتينية قصيرة).</summary>
public static class TextShaper
{
    // [أساس الأشكال، يتصل من الجهتين؟] الترتيب داخل Presentation Forms-B: معزول، نهائي، ابتدائي، وسطي
    private static readonly Dictionary<char, (int Base, bool Dual)> Forms = new()
    {
        ['ء'] = (0xFE80, false), ['آ'] = (0xFE81, false), ['أ'] = (0xFE83, false), ['ؤ'] = (0xFE85, false),
        ['إ'] = (0xFE87, false), ['ئ'] = (0xFE89, true), ['ا'] = (0xFE8D, false), ['ب'] = (0xFE8F, true),
        ['ة'] = (0xFE93, false), ['ت'] = (0xFE95, true), ['ث'] = (0xFE99, true), ['ج'] = (0xFE9D, true),
        ['ح'] = (0xFEA1, true), ['خ'] = (0xFEA5, true), ['د'] = (0xFEA9, false), ['ذ'] = (0xFEAB, false),
        ['ر'] = (0xFEAD, false), ['ز'] = (0xFEAF, false), ['س'] = (0xFEB1, true), ['ش'] = (0xFEB5, true),
        ['ص'] = (0xFEB9, true), ['ض'] = (0xFEBD, true), ['ط'] = (0xFEC1, true), ['ظ'] = (0xFEC5, true),
        ['ع'] = (0xFEC9, true), ['غ'] = (0xFECD, true), ['ف'] = (0xFED1, true), ['ق'] = (0xFED5, true),
        ['ك'] = (0xFED9, true), ['ل'] = (0xFEDD, true), ['م'] = (0xFEE1, true), ['ن'] = (0xFEE5, true),
        ['ه'] = (0xFEE9, true), ['و'] = (0xFEED, false), ['ى'] = (0xFEEF, false), ['ي'] = (0xFEF1, true),
    };

    private static readonly Dictionary<char, int> LamAlef = new()
    {
        ['آ'] = 0xFEF5, ['أ'] = 0xFEF7, ['إ'] = 0xFEF9, ['ا'] = 0xFEFB,
    };

    private static readonly Dictionary<char, char> Mirror = new()
    {
        ['('] = ')', [')'] = '(', ['['] = ']', [']'] = '[', ['{'] = '}', ['}'] = '{', ['<'] = '>', ['>'] = '<', ['«'] = '»', ['»'] = '«',
    };

    private static bool IsTransparent(char c) => c is >= 'ً' and <= 'ٟ' or 'ٰ';
    private static bool IsTatweel(char c) => c == 'ـ';
    private static bool JoinsForward(char c) => IsTatweel(c) || Forms.TryGetValue(c, out var f) && f.Dual;
    private static bool CanJoinBack(char c) => IsTatweel(c) || Forms.ContainsKey(c) && c != 'ء';

    public static bool IsArabic(int cp) => cp is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF;

    /// <summary>التشكيل: يعيد وحدات بترتيب منطقي، لكل منها المحرف المعروض والنص الأصلي.</summary>
    public static List<(int Display, string Source)> ShapeArabic(string text)
    {
        // التشكيل الحركي لا يُرسم دون جدول تموضع؛ يُحذف من العرض (النص الأصلي محفوظ في السجل)
        var chars = text.Where(c => !IsTransparent(c)).ToArray();
        var output = new List<(int, string)>(chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!Forms.TryGetValue(c, out var f))
            {
                output.Add((c, c.ToString()));
                continue;
            }
            var prevJoins = i > 0 && JoinsForward(chars[i - 1]);
            if (c == 'ل' && i + 1 < chars.Length && LamAlef.TryGetValue(chars[i + 1], out var la))
            {
                output.Add((la + (prevJoins ? 1 : 0), new string([c, chars[i + 1]])));
                i++;
                continue;
            }
            var nextJoins = f.Dual && i + 1 < chars.Length && CanJoinBack(chars[i + 1]);
            if (c == 'ء') { output.Add((f.Base, "ء")); continue; }
            int form = (prevJoins, nextJoins) switch
            {
                (true, true) => 3,
                (true, false) => 1,
                (false, true) => 2,
                _ => 0,
            };
            output.Add((f.Base + form, c.ToString()));
        }
        return output;
    }

    public static BidiClass Classify(string source)
    {
        var c = source[0];
        if (c is >= '0' and <= '9') return BidiClass.EN;
        if (c is >= '٠' and <= '٩' or >= '۰' and <= '۹') return BidiClass.AN;
        if (char.IsWhiteSpace(c)) return BidiClass.WS;
        if (IsArabic(c) && (char.IsLetter(c) || c is '؟' or '؛' or 'ـ')) return BidiClass.R;
        if (char.IsLetter(c)) return BidiClass.L;
        return BidiClass.ON;
    }

    /// <summary>من نص منطقي إلى محارف مشكّلة مع اختيار الخط لكل محرف.</summary>
    public static List<ShapedGlyph> Shape(string text, FontSet fonts)
    {
        var result = new List<ShapedGlyph>();
        foreach (var (display, source) in ShapeArabic(text.Replace("\r", "").Replace('\t', ' ')))
        {
            var cls = Classify(source);
            FontFace face;
            if (IsArabic(display) || cls == BidiClass.R) face = fonts.Arabic.Font.Has(display) ? fonts.Arabic : fonts.Latin;
            else face = fonts.Latin.Font.Has(display) ? fonts.Latin : fonts.Arabic;
            var gid = face.Font.Glyph(display);
            if (gid == 0 && display != source[0])
            {
                // لا يوجد شكل سياقي في الخط: يُرسم الحرف الأساسي
                gid = face.Font.Glyph(source[0]);
            }
            result.Add(new ShapedGlyph(face, gid, face.Font.Advance(gid), source, cls, display == ' ' || display == ' '));
        }
        return result;
    }

    public static bool IsRtl(string text)
    {
        foreach (var c in text)
        {
            var cls = Classify(c.ToString());
            if (cls == BidiClass.R) return true;
            if (cls == BidiClass.L) return false;
        }
        return true;
    }

    private static readonly HashSet<string> NumberSeparators = [",", ".", ":", "/", "-", "٫", "٬", "+"];
    private static readonly HashSet<string> NumberTerminators = ["%", "٪", "#", "$", "°"];

    /// <summary>ترتيب سطر واحد بصريًا (من اليسار إلى اليمين) وفق اتجاه الفقرة.</summary>
    public static List<ShapedGlyph> Reorder(List<ShapedGlyph> line, bool rtl)
    {
        var n = line.Count;
        if (n == 0) return line;
        var cls = line.Select(g => g.Class).ToArray();
        var baseStrong = rtl ? BidiClass.R : BidiClass.L;

        // W4/W5: فواصل الأرقام بين رقمين، ورموز النسبة الملاصقة للرقم، تُعامل رقمًا
        for (var i = 1; i < n - 1; i++)
            if (cls[i] == BidiClass.ON && NumberSeparators.Contains(line[i].Source) && IsNum(cls[i - 1]) && IsNum(cls[i + 1])) cls[i] = cls[i - 1];
        for (var i = 0; i < n; i++)
            if (cls[i] == BidiClass.ON && NumberTerminators.Contains(line[i].Source) && (i > 0 && IsNum(cls[i - 1]) || i + 1 < n && IsNum(cls[i + 1])))
                cls[i] = BidiClass.EN;

        // W7: الأرقام بعد نص لاتيني تأخذ اتجاهه
        var last = baseStrong;
        for (var i = 0; i < n; i++)
        {
            if (cls[i] is BidiClass.L or BidiClass.R) last = cls[i];
            else if (cls[i] == BidiClass.EN && last == BidiClass.L) cls[i] = BidiClass.L;
        }

        // N1/N2: المحايدات بين اتجاهين متماثلين تأخذهما، وإلا اتجاه الفقرة (الأرقام تُعد يمينية هنا)
        BidiClass Strong(BidiClass c) => c is BidiClass.EN or BidiClass.AN ? BidiClass.R : c;
        for (var i = 0; i < n;)
        {
            if (cls[i] is not (BidiClass.WS or BidiClass.ON)) { i++; continue; }
            var j = i;
            while (j < n && cls[j] is BidiClass.WS or BidiClass.ON) j++;
            var before = i == 0 ? baseStrong : Strong(cls[i - 1]);
            var after = j == n ? baseStrong : Strong(cls[j]);
            var resolved = before == after ? before : baseStrong;
            for (var k = i; k < j; k++) cls[k] = resolved;
            i = j;
        }

        // المستويات (I1/I2)، والمسافات في نهاية السطر بمستوى الفقرة (L1)
        var baseLevel = rtl ? 1 : 0;
        for (var i = 0; i < n; i++)
        {
            line[i].Level = (baseLevel, cls[i]) switch
            {
                (0, BidiClass.R) => 1,
                (0, BidiClass.EN or BidiClass.AN) => 2,
                (0, _) => 0,
                (_, BidiClass.L or BidiClass.EN or BidiClass.AN) => 2,
                _ => 1,
            };
        }
        for (var i = n - 1; i >= 0 && line[i].IsSpace; i--) line[i].Level = baseLevel;

        // L2: عكس المقاطع من أعلى مستوى إلى أدنى مستوى فردي
        var result = line.ToList();
        var max = result.Max(g => g.Level);
        var minOdd = result.Where(g => g.Level % 2 == 1).Select(g => g.Level).DefaultIfEmpty(max + 1).Min();
        for (var level = max; level >= minOdd; level--)
        {
            for (var i = 0; i < result.Count;)
            {
                if (result[i].Level < level) { i++; continue; }
                var j = i;
                while (j < result.Count && result[j].Level >= level) j++;
                result.Reverse(i, j - i);
                i = j;
            }
        }
        // L4: عكس الأقواس في المقاطع اليمينية
        for (var i = 0; i < result.Count; i++)
        {
            var g = result[i];
            if (g.Level % 2 == 1 && g.Source.Length == 1 && Mirror.TryGetValue(g.Source[0], out var m))
            {
                var gid = g.Font.Font.Glyph(m);
                result[i] = new ShapedGlyph(g.Font, gid, g.Font.Font.Advance(gid), g.Source, g.Class, false) { Level = g.Level };
            }
        }
        return result;
    }

    private static bool IsNum(BidiClass c) => c is BidiClass.EN or BidiClass.AN;

    /// <summary>تقسيم الفقرة إلى أسطر بعرض محدد (بالترتيب المنطقي)، مع كسر الكلمة الطويلة جدًا.</summary>
    public static List<List<ShapedGlyph>> Wrap(List<ShapedGlyph> glyphs, double size, double maxWidth)
    {
        var lines = new List<List<ShapedGlyph>>();
        var current = new List<ShapedGlyph>();
        double width = 0;
        var i = 0;
        while (i < glyphs.Count)
        {
            if (glyphs[i].Source == "\n")
            {
                lines.Add(current); current = []; width = 0; i++;
                continue;
            }
            // كلمة: حتى المسافة التالية (شاملة)
            var j = i;
            while (j < glyphs.Count && !glyphs[j].IsSpace && glyphs[j].Source != "\n") j++;
            if (j < glyphs.Count && glyphs[j].IsSpace) j++;
            var word = glyphs.GetRange(i, j - i);
            var w = word.Sum(g => g.Advance) * size / 1000;
            var trimmed = w - word.Where(g => g.IsSpace).Sum(g => g.Advance) * size / 1000;
            if (width + trimmed > maxWidth && current.Count > 0)
            {
                lines.Add(current); current = []; width = 0;
            }
            if (trimmed > maxWidth)
            {
                foreach (var g in word)
                {
                    var gw = g.Advance * size / 1000;
                    if (width + gw > maxWidth && current.Count > 0) { lines.Add(current); current = []; width = 0; }
                    current.Add(g); width += gw;
                }
            }
            else
            {
                current.AddRange(word); width += w;
            }
            i = j;
        }
        if (current.Count > 0 || lines.Count == 0) lines.Add(current);
        return lines;
    }

    public static double Width(IEnumerable<ShapedGlyph> glyphs, double size, bool trimTrailingSpaces = true)
    {
        var list = glyphs as IList<ShapedGlyph> ?? glyphs.ToList();
        var end = list.Count;
        if (trimTrailingSpaces) while (end > 0 && list[end - 1].IsSpace) end--;
        double w = 0;
        for (var i = 0; i < end; i++) w += list[i].Advance;
        return w * size / 1000;
    }

    public static string Normalize(string? s) => (s ?? "").Normalize(NormalizationForm.FormC);

    public static string Digits(long n) => n.ToString(CultureInfo.InvariantCulture);
}
