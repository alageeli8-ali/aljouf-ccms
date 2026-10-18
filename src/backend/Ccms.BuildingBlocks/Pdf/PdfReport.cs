using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Ccms.BuildingBlocks.Pdf;

public readonly record struct PdfColor(double R, double G, double B)
{
    public static PdfColor Hex(string hex) => new(
        Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);
    public static readonly PdfColor Black = new(0.13, 0.13, 0.13);
    public static readonly PdfColor White = new(1, 1, 1);
    public static readonly PdfColor Gray = new(0.42, 0.42, 0.42);
    public static readonly PdfColor Border = new(0.82, 0.82, 0.82);
    internal string Fill => F(R) + " " + F(G) + " " + F(B) + " rg";
    internal string Stroke => F(R) + " " + F(G) + " " + F(B) + " RG";
    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class PdfReportOptions
{
    public required string Title { get; init; }
    public string? HeaderLine1 { get; init; }
    public string? HeaderLine2 { get; init; }
    public string? FooterText { get; init; }
    public string? Watermark { get; init; }
    public bool Rtl { get; init; } = true;
    public bool Landscape { get; init; }
    public string Author { get; init; } = "";
    public PdfColor Brand { get; init; } = PdfColor.Hex("#006837");
    public PdfColor Accent { get; init; } = PdfColor.Hex("#B28655");
}

/// <summary>منشئ تقارير PDF عربية (RTL) بلا مكتبات خارجية: عناوين، وفقرات، وجداول بترويسة متكررة، وترقيم صفحات، وعلامة مائية للمسودات.
/// الخطوط مضمنة مقتطعة (Identity-H) مع خريطة ToUnicode ليبقى النص قابلًا للبحث والنسخ.</summary>
public sealed class PdfReport
{
    private const double Margin = 48, Top = 104, Bottom = 64;
    private readonly double PageW, PageH;
    private readonly PdfReportOptions _o;
    private readonly FontSet _regular, _bold;
    private readonly List<PdfPage> _pages = [];
    private PdfPage _page = null!;
    private double _y;

    public PdfReport(PdfReportOptions options)
    {
        _o = options;
        (PageW, PageH) = options.Landscape ? (841.89, 595.28) : (595.28, 841.89);
        _regular = new FontSet(new FontFace("F1", TrueTypeFont.Embedded("NotoSansArabic-Regular.ttf")), new FontFace("F2", TrueTypeFont.Embedded("NotoSans-Regular.ttf")));
        _bold = new FontSet(new FontFace("F3", TrueTypeFont.Embedded("NotoSansArabic-Bold.ttf")), new FontFace("F4", TrueTypeFont.Embedded("NotoSans-Bold.ttf")));
        NewPage();
    }

    private double ContentWidth => PageW - 2 * Margin;

    private void NewPage()
    {
        _page = new PdfPage();
        _pages.Add(_page);
        _y = PageH - Top;
        // الترويسة: شريط بلون الهوية وسطر ذهبي
        _page.Rect(0, PageH - 70, PageW, 70, _o.Brand);
        _page.Rect(0, PageH - 74, PageW, 4, _o.Accent);
        if (_o.HeaderLine1 is { } h1) DrawAligned(h1, _bold, 13, PageH - 34, PdfColor.White);
        if (_o.HeaderLine2 is { } h2) DrawAligned(h2, _regular, 10, PageH - 54, PdfColor.White);
    }

    private List<ShapedGlyph> Line(string text, FontSet fonts, double size)
    {
        var shaped = TextShaper.Shape(TextShaper.Normalize(text), fonts);
        return TextShaper.Reorder(shaped, TextShaper.IsRtl(text));
    }

    private void DrawAligned(string text, FontSet fonts, double size, double y, PdfColor color, double? left = null, double? width = null, bool? rtl = null)
    {
        var l = left ?? Margin;
        var w = width ?? ContentWidth;
        var glyphs = Line(text, fonts, size);
        var tw = TextShaper.Width(glyphs, size);
        var x = rtl ?? _o.Rtl ? l + w - tw : l;
        _page.Text(glyphs, x, y, size, color);
    }

    /// <summary>أسطر الفقرة بعد التفاف النص وترتيبه بصريًا.</summary>
    private List<List<ShapedGlyph>> Layout(string text, FontSet fonts, double size, double width)
    {
        var normalized = TextShaper.Normalize(text);
        var result = new List<List<ShapedGlyph>>();
        foreach (var para in normalized.Split('\n'))
        {
            var rtl = TextShaper.IsRtl(para);
            foreach (var line in TextShaper.Wrap(TextShaper.Shape(para, fonts), size, width))
            {
                var trimmed = line.ToList();
                while (trimmed.Count > 0 && trimmed[^1].IsSpace) trimmed.RemoveAt(trimmed.Count - 1);
                result.Add(TextShaper.Reorder(trimmed, rtl));
            }
        }
        return result;
    }

    private static double LineHeight(double size) => size * 1.75;

    private void Ensure(double height)
    {
        if (_y - height < Bottom) NewPage();
    }

    public void Title(string text)
    {
        var lines = Layout(text, _bold, 16, ContentWidth);
        Ensure(lines.Count * LineHeight(16) + 6);
        foreach (var l in lines) { _y -= LineHeight(16); DrawLine(l, 16, _o.Brand, Margin, ContentWidth); }
        _y -= 6;
    }

    public void Heading(string text)
    {
        const double size = 11.5, h = 24;
        Ensure(h + 40);
        _y -= 10;
        _page.Rect(Margin, _y - h, ContentWidth, h, new PdfColor(0.93, 0.96, 0.94));
        var barX = _o.Rtl ? Margin + ContentWidth - 4 : Margin;
        _page.Rect(barX, _y - h, 4, h, _o.Accent);
        var glyphs = Line(text, _bold, size);
        var tw = TextShaper.Width(glyphs, size);
        var x = _o.Rtl ? Margin + ContentWidth - 12 - tw : Margin + 12;
        _page.Text(glyphs, x, _y - h + 7.5, size, _o.Brand);
        _y -= h + 6;
    }

    public void Paragraph(string? text, double size = 10, bool bold = false, PdfColor? color = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in Layout(text, bold ? _bold : _regular, size, ContentWidth))
        {
            Ensure(LineHeight(size));
            _y -= LineHeight(size);
            DrawLine(line, size, color ?? PdfColor.Black, Margin, ContentWidth);
        }
        _y -= 3;
    }

    public void Spacer(double h = 8) => _y -= h;

    private void DrawLine(List<ShapedGlyph> line, double size, PdfColor color, double left, double width, bool? rtl = null)
    {
        var tw = TextShaper.Width(line, size);
        var x = rtl ?? _o.Rtl ? left + width - tw : left;
        _page.Text(line, x, _y + size * 0.45, size, color);
    }

    /// <summary>جدول مفتاح/قيمة بعمودين.</summary>
    public void KeyValues(IReadOnlyList<(string Key, string? Value)> rows, double keyFraction = 0.28) =>
        Table(null, [keyFraction, 1 - keyFraction], rows.Select(r => (IReadOnlyList<string?>)[r.Key, r.Value ?? "—"]).ToList(), boldFirstColumn: true);

    /// <summary>جدول بأعمدة نسبية؛ العمود الأول على اليمين في التخطيط العربي. يلتف النص داخل الخلايا وتتكرر الترويسة في كل صفحة.</summary>
    public void Table(IReadOnlyList<string>? headers, IReadOnlyList<double> fractions, IReadOnlyList<IReadOnlyList<string?>> rows, bool boldFirstColumn = false, double size = 9)
    {
        const double pad = 5;
        var total = fractions.Sum();
        var widths = fractions.Select(f => f / total * ContentWidth).ToArray();
        double ColumnLeft(int c)
        {
            var before = widths.Take(c).Sum();
            return _o.Rtl ? Margin + ContentWidth - before - widths[c] : Margin + before;
        }

        List<List<List<ShapedGlyph>>> Cells(IReadOnlyList<string?> row, bool header) =>
            row.Select((t, c) => Layout(string.IsNullOrEmpty(t) ? "—" : t!, header || boldFirstColumn && c == 0 ? _bold : _regular, size, widths[c] - 2 * pad)).ToList();

        double RowHeight(List<List<List<ShapedGlyph>>> cells) => cells.Max(c => c.Count) * LineHeight(size) + 2 * pad - 4;

        void DrawRow(List<List<List<ShapedGlyph>>> cells, bool header, bool shaded)
        {
            var h = RowHeight(cells);
            if (header) _page.Rect(Margin, _y - h, ContentWidth, h, _o.Brand);
            else if (shaded) _page.Rect(Margin, _y - h, ContentWidth, h, new PdfColor(0.975, 0.975, 0.97));
            for (var c = 0; c < cells.Count; c++)
            {
                var left = ColumnLeft(c);
                var y = _y - pad + 2;
                foreach (var line in cells[c])
                {
                    y -= LineHeight(size);
                    var tw = TextShaper.Width(line, size);
                    var x = _o.Rtl ? left + widths[c] - pad - tw : left + pad;
                    _page.Text(line, x, y + size * 0.45, size, header ? PdfColor.White : PdfColor.Black);
                }
                _page.Rect(left, _y - h, widths[c], h, null, PdfColor.Border);
            }
            _y -= h;
        }

        var headerCells = headers is null ? null : Cells(headers.Select(h => (string?)h).ToList(), true);
        var first = true;
        var i = 0;
        foreach (var row in rows)
        {
            var cells = Cells(row, false);
            var need = RowHeight(cells) + (first && headerCells is not null ? RowHeight(headerCells) : 0);
            if (_y - need < Bottom)
            {
                NewPage();
                first = true;
            }
            if (first && headerCells is not null) DrawRow(headerCells, true, false);
            first = false;
            DrawRow(cells, false, i++ % 2 == 1);
        }
        if (rows.Count == 0 && headerCells is not null) { Ensure(RowHeight(headerCells)); DrawRow(headerCells, true, false); }
        _y -= 8;
    }

    public byte[] Build()
    {
        // التذييل بعد معرفة عدد الصفحات، والعلامة المائية شفافة فوق المحتوى
        for (var p = 0; p < _pages.Count; p++)
        {
            _page = _pages[p];
            if (_o.Watermark is { Length: > 0 } wm)
            {
                var glyphs = Line(wm, _bold, 60);
                var w = TextShaper.Width(glyphs, 60);
                const double a = Math.PI / 5;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                var cx = PageW / 2 - cos * w / 2 + sin * 20;
                var cy = PageH / 2 - sin * w / 2 - cos * 20;
                _page.Raw($"q /GS1 gs {PdfColor.F(cos)} {PdfColor.F(sin)} {PdfColor.F(-sin)} {PdfColor.F(cos)} {PdfColor.F(cx)} {PdfColor.F(cy)} cm");
                _page.Text(glyphs, 0, 0, 60, new PdfColor(0.75, 0.1, 0.1));
                _page.Raw("Q");
            }
            _page.Line(Margin, Bottom - 18, PageW - Margin, Bottom - 18, PdfColor.Border, 0.6);
            var pageText = _o.Rtl ? $"صفحة {p + 1} من {_pages.Count}" : $"Page {p + 1} of {_pages.Count}";
            var pg = Line(pageText, _regular, 8);
            _page.Text(pg, _o.Rtl ? Margin : PageW - Margin - TextShaper.Width(pg, 8), Bottom - 32, 8, PdfColor.Gray);
            if (_o.FooterText is { } ft)
            {
                var lines = Layout(ft, _regular, 7.5, ContentWidth - 90);
                var y = Bottom - 32;
                foreach (var l in lines.Take(2))
                {
                    var tw = TextShaper.Width(l, 7.5);
                    _page.Text(l, _o.Rtl ? PageW - Margin - tw : Margin, y, 7.5, PdfColor.Gray);
                    y -= 11;
                }
            }
        }
        return PdfFileWriter.Write(_pages, _o.Title, _o.Author, PageW, PageH);
    }
}

/// <summary>محتوى صفحة واحدة بأوامر PDF.</summary>
internal sealed class PdfPage
{
    public StringBuilder Content { get; } = new();
    public HashSet<FontFace> Fonts { get; } = [];

    public void Raw(string s) => Content.Append(s).Append('\n');

    public void Rect(double x, double y, double w, double h, PdfColor? fill, PdfColor? stroke = null, double lineWidth = 0.5)
    {
        var r = $"{PdfColor.F(x)} {PdfColor.F(y)} {PdfColor.F(w)} {PdfColor.F(h)} re";
        if (fill is { } f && stroke is { } s) Raw($"{f.Fill} {s.Stroke} {PdfColor.F(lineWidth)} w {r} B");
        else if (fill is { } f2) Raw($"{f2.Fill} {r} f");
        else if (stroke is { } s2) Raw($"{s2.Stroke} {PdfColor.F(lineWidth)} w {r} S");
    }

    public void Line(double x1, double y1, double x2, double y2, PdfColor color, double width) =>
        Raw($"{color.Stroke} {PdfColor.F(width)} w {PdfColor.F(x1)} {PdfColor.F(y1)} m {PdfColor.F(x2)} {PdfColor.F(y2)} l S");

    /// <summary>يرسم محارف مرتبة بصريًا من اليسار؛ كل مقطع بخط واحد في أمر Tj مستقل بموضع صريح.</summary>
    public void Text(List<ShapedGlyph> glyphs, double x, double y, double size, PdfColor color)
    {
        if (glyphs.Count == 0) return;
        var sb = Content;
        sb.Append("BT ").Append(color.Fill).Append('\n');
        var i = 0;
        while (i < glyphs.Count)
        {
            var face = glyphs[i].Font;
            Fonts.Add(face);
            var j = i;
            var hex = new StringBuilder();
            double w = 0;
            while (j < glyphs.Count && glyphs[j].Font == face)
            {
                hex.Append(glyphs[j].Gid.ToString("X4"));
                face.Used[glyphs[j].Gid] = glyphs[j].Source;
                w += glyphs[j].Advance;
                j++;
            }
            sb.Append('/').Append(face.Key).Append(' ').Append(PdfColor.F(size)).Append(" Tf 1 0 0 1 ")
              .Append(PdfColor.F(x)).Append(' ').Append(PdfColor.F(y)).Append(" Tm <").Append(hex).Append("> Tj\n");
            x += w * size / 1000;
            i = j;
        }
        sb.Append("ET\n");
    }
}

internal static class PdfFileWriter
{
    public static byte[] Write(List<PdfPage> pages, string title, string author, double pageW = 595.28, double pageH = 841.89)
    {
        var objects = new List<byte[]>();
        int Reserve() { objects.Add([]); return objects.Count; }
        void Set(int id, string s) => objects[id - 1] = Encoding.Latin1.GetBytes(s);
        void SetStream(int id, string dict, byte[] data)
        {
            var compressed = Deflate(data);
            using var ms = new MemoryStream();
            var head = Encoding.Latin1.GetBytes($"<< {dict} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            ms.Write(head);
            ms.Write(compressed);
            ms.Write("\nendstream"u8);
            objects[id - 1] = ms.ToArray();
        }

        var catalog = Reserve();
        var pagesId = Reserve();
        var info = Reserve();
        var faces = pages.SelectMany(p => p.Fonts).Distinct().OrderBy(f => f.Key).ToList();
        var fontIds = new Dictionary<FontFace, int>();
        foreach (var face in faces)
        {
            var type0 = Reserve();
            var cid = Reserve();
            var descriptor = Reserve();
            var file = Reserve();
            var toUnicode = Reserve();
            fontIds[face] = type0;
            var f = face.Font;
            var tag = SubsetTag(face.Key + f.Name);
            var baseName = $"{tag}+{f.Name}";
            var used = face.Used.Keys.Order().ToList();
            Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
            var widths = string.Join(" ", used.Select(g => $"{g} [{PdfColor.F(f.Advance(g))}]"));
            Set(cid, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 600 /W [{widths}] /CIDToGIDMap /Identity >>");
            var bb = f.BBox;
            Set(descriptor, $"<< /Type /FontDescriptor /FontName /{baseName} /Flags 32 /FontBBox [{PdfColor.F(f.Scale(bb.XMin))} {PdfColor.F(f.Scale(bb.YMin))} {PdfColor.F(f.Scale(bb.XMax))} {PdfColor.F(f.Scale(bb.YMax))}] /ItalicAngle 0 /Ascent {PdfColor.F(f.Scale(f.Ascender))} /Descent {PdfColor.F(f.Scale(f.Descender))} /CapHeight {PdfColor.F(f.Scale(f.Ascender))} /StemV 80 /FontFile2 {file} 0 R >>");
            var subset = f.Subset(used);
            SetStream(file, $"/Length1 {subset.Length}", subset);
            SetStream(toUnicode, "", Encoding.ASCII.GetBytes(ToUnicodeCMap(face.Used)));
        }

        var pageIds = new List<int>();
        var fontRes = string.Join(" ", fontIds.Select(kv => $"/{kv.Key.Key} {kv.Value} 0 R"));
        foreach (var p in pages)
        {
            var page = Reserve();
            var content = Reserve();
            pageIds.Add(page);
            Set(page, $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {pageW.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {pageH.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}] /Resources << /Font << {fontRes} >> /ExtGState << /GS1 << /Type /ExtGState /ca 0.10 /CA 0.10 >> >> >> /Contents {content} 0 R >>");
            SetStream(content, "", Encoding.Latin1.GetBytes(p.Content.ToString()));
        }
        Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pagesId} 0 R /Lang (ar-SA) /ViewerPreferences << /Direction /R2L /DisplayDocTitle true >> >>");
        Set(info, $"<< /Title {Utf16(title)} /Author {Utf16(author)} /Producer (CCMS) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>");

        using var o = new MemoryStream();
        o.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = o.Position;
            o.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            o.Write(objects[i]);
            o.Write("\nendobj\n"u8);
        }
        var xref = o.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        o.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        return o.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    private static string SubsetTag(string seed)
    {
        var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new string(h.Take(6).Select(b => (char)('A' + b % 26)).ToArray());
    }

    private static string Utf16(string s)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(s);
        return "<FEFF" + Convert.ToHexString(bytes) + ">";
    }

    private static string ToUnicodeCMap(Dictionary<int, string> map)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in map.OrderBy(k => k.Key).Chunk(100))
        {
            sb.Append(chunk.Length).Append(" beginbfchar\n");
            foreach (var (gid, text) in chunk)
                sb.Append('<').Append(gid.ToString("X4")).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }
}
