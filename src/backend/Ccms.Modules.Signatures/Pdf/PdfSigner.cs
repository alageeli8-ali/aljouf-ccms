using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ccms.Modules.Signatures.Pdf;

/// <summary>توقيع PDF بتحديث تزايدي (incremental update) وفق ISO 32000: يُلحق قاموس توقيع (/Type /Sig، /SubFilter /ETSI.CAdES.detached)
/// وحقل توقيع غير مرئي، ويُترك /Contents فارغًا بحجم محجوز، ثم تُحسب بصمة نطاق البايتات (كل الملف عدا التوقيع نفسه) ويُضمَّن CMS.
/// المراجعات السابقة لا تُمس، فتبقى التوقيعات السابقة قابلة للتحقق، ويمكن التحقق أن النسخة الأصلية محفوظة كما هي في بداية الملف.
/// لا يُضاف أي صورة توقيع: الصورة وحدها ليست توقيعًا.</summary>
internal static partial class PdfSigner
{
    public const int ReservedSignatureBytes = 12288;

    public sealed record Prepared(byte[] Pdf, long[] ByteRange, int ContentsOffset, int ContentsLength)
    {
        public byte[] Digest()
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(Pdf, (int)ByteRange[0], (int)ByteRange[1]);
            sha.AppendData(Pdf, (int)ByteRange[2], (int)ByteRange[3]);
            return sha.GetHashAndReset();
        }
    }

    public static Prepared Prepare(byte[] pdf, string fieldName, string signerName, string reason, DateTime signingTimeUtc)
    {
        var doc = PdfStructure.Parse(pdf);
        var nextId = doc.Size;
        var sigId = nextId++;
        var fieldId = nextId++;
        var catalogId = doc.RootId;
        var pageId = doc.FirstPageId;

        var catalog = doc.ObjectDict(catalogId);
        var existingFields = AcroFields().Match(catalog) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        catalog = AcroForm().Replace(catalog, "");
        catalog = InsertBeforeEnd(catalog, $" /AcroForm << /Fields [{(existingFields.Length > 0 ? existingFields + " " : "")}{fieldId} 0 R] /SigFlags 3 >>");

        var page = doc.ObjectDict(pageId);
        var annots = Annots().Match(page) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
        page = Annots().Replace(page, "");
        page = InsertBeforeEnd(page, $" /Annots [{(annots.Length > 0 ? annots + " " : "")}{fieldId} 0 R]");

        const string byteRangePlaceholder = "/ByteRange [0 0000000000 0000000000 0000000000]";
        var contentsPlaceholder = "/Contents <" + new string('0', ReservedSignatureBytes * 2) + ">";
        var sigDict = $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached {byteRangePlaceholder} {contentsPlaceholder} "
            + $"/M (D:{signingTimeUtc:yyyyMMddHHmmss}Z) /Name {TextString(signerName)} /Reason {TextString(reason)} >>";
        var fieldDict = $"<< /Type /Annot /Subtype /Widget /FT /Sig /T {TextString(fieldName)} /V {sigId} 0 R /P {pageId} 0 R /Rect [0 0 0 0] /F 132 >>";

        using var o = new MemoryStream();
        o.Write(pdf);
        if (pdf[^1] != (byte)'\n') o.WriteByte((byte)'\n');
        var offsets = new SortedDictionary<int, long>();
        void Obj(int id, string body)
        {
            offsets[id] = o.Position;
            o.Write(Encoding.Latin1.GetBytes($"{id} 0 obj\n{body}\nendobj\n"));
        }
        Obj(catalogId, catalog);
        Obj(pageId, page);
        Obj(sigId, sigDict);
        Obj(fieldId, fieldDict);

        var xref = o.Position;
        var sb = new StringBuilder("xref\n");
        foreach (var run in Runs(offsets.Keys))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{run.First} {run.Count}\n");
            for (var i = run.First; i < run.First + run.Count; i++) sb.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {nextId} /Root {catalogId} 0 R{(doc.InfoId is { } info ? $" /Info {info} 0 R" : "")} /Prev {doc.StartXref} >>\nstartxref\n{xref}\n%%EOF\n");
        o.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        var bytes = o.ToArray();

        // مواضع العناصر المحجوزة في قاموس التوقيع
        var sigStart = (int)offsets[sigId];
        var text = Encoding.Latin1.GetString(bytes, sigStart, bytes.Length - sigStart);
        var contentsAt = sigStart + text.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;   // موضع '<'
        var contentsEnd = contentsAt + ReservedSignatureBytes * 2 + 2;                                                // بعد '>'
        var range = new long[] { 0, contentsAt, contentsEnd, bytes.Length - contentsEnd };
        var brAt = sigStart + text.IndexOf(byteRangePlaceholder, StringComparison.Ordinal);
        var br = $"/ByteRange [{range[0]} {range[1]} {range[2]} {range[3]}]".PadRight(byteRangePlaceholder.Length);
        if (br.Length != byteRangePlaceholder.Length) throw new InvalidOperationException("حجم نطاق البايتات تجاوز المحجوز");
        Encoding.ASCII.GetBytes(br).CopyTo(bytes, brAt);
        return new Prepared(bytes, range, contentsAt, contentsEnd - contentsAt);
    }

    public static byte[] Embed(Prepared prepared, byte[] cms)
    {
        if (cms.Length > ReservedSignatureBytes) throw new InvalidOperationException("حجم التوقيع يتجاوز المساحة المحجوزة");
        var bytes = (byte[])prepared.Pdf.Clone();
        var hex = Convert.ToHexString(cms).PadRight(ReservedSignatureBytes * 2, '0');
        Encoding.ASCII.GetBytes(hex).CopyTo(bytes, prepared.ContentsOffset + 1);
        return bytes;
    }

    private static string InsertBeforeEnd(string dict, string addition)
    {
        var end = dict.LastIndexOf(">>", StringComparison.Ordinal);
        return dict[..end].TrimEnd() + addition + " " + dict[end..];
    }

    /// <summary>سلسلة نصية بترميز UTF-16BE مع علامة الترتيب (للأسماء العربية).</summary>
    private static string TextString(string s) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";

    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> ids)
    {
        int? first = null, prev = null;
        foreach (var id in ids)
        {
            if (first is null) { first = prev = id; continue; }
            if (id == prev + 1) { prev = id; continue; }
            yield return (first.Value, prev!.Value - first.Value + 1);
            first = prev = id;
        }
        if (first is not null) yield return (first.Value, prev!.Value - first.Value + 1);
    }

    [GeneratedRegex(@"/AcroForm\s*<<\s*/Fields\s*\[([^\]]*)\][^>]*>>")]
    private static partial Regex AcroForm();
    [GeneratedRegex(@"/AcroForm\s*<<\s*/Fields\s*\[([^\]]*)\]")]
    private static partial Regex AcroFields();
    [GeneratedRegex(@"/Annots\s*\[([^\]]*)\]")]
    private static partial Regex Annots();
}

/// <summary>قارئ مبسّط لبنية ملفات PDF التي يولّدها النظام وتحديثاتها التزايدية: جداول xref التقليدية وسلسلة /Prev.</summary>
internal sealed partial class PdfStructure
{
    private readonly byte[] _pdf;
    private readonly Dictionary<int, long> _offsets = new();
    public int Size { get; private set; }
    public int RootId { get; private set; }
    public int? InfoId { get; private set; }
    public long StartXref { get; private set; }
    public int FirstPageId { get; private set; }

    private PdfStructure(byte[] pdf) => _pdf = pdf;

    public static PdfStructure Parse(byte[] pdf)
    {
        var s = new PdfStructure(pdf);
        var tail = Encoding.Latin1.GetString(pdf, Math.Max(0, pdf.Length - 1024), Math.Min(1024, pdf.Length));
        var sx = StartXrefRx().Matches(tail);
        if (sx.Count == 0) throw new PdfFormatException("الملف لا يحتوي startxref");
        s.StartXref = long.Parse(sx[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var at = s.StartXref;
        var first = true;
        var guard = 0;
        while (at > 0 && guard++ < 64)
        {
            var (trailer, prev) = s.ReadXrefSection(at);
            if (first)
            {
                s.Size = int.Parse(SizeRx().Match(trailer).Groups[1].Value, CultureInfo.InvariantCulture);
                s.RootId = int.Parse(RootRx().Match(trailer).Groups[1].Value, CultureInfo.InvariantCulture);
                if (InfoRx().Match(trailer) is { Success: true } info) s.InfoId = int.Parse(info.Groups[1].Value, CultureInfo.InvariantCulture);
                first = false;
            }
            at = prev;
        }
        var pagesRef = RefRx("Pages").Match(s.ObjectDict(s.RootId));
        if (!pagesRef.Success) throw new PdfFormatException("لا يوجد /Pages في الفهرس");
        var kids = KidsRx().Match(s.ObjectDict(int.Parse(pagesRef.Groups[1].Value, CultureInfo.InvariantCulture)));
        if (!kids.Success) throw new PdfFormatException("لا توجد صفحات");
        s.FirstPageId = int.Parse(kids.Groups[1].Value, CultureInfo.InvariantCulture);
        return s;
    }

    private (string Trailer, long Prev) ReadXrefSection(long at)
    {
        var len = (int)Math.Min(_pdf.Length - at, 1 << 20);
        var text = Encoding.Latin1.GetString(_pdf, (int)at, len);
        if (!text.StartsWith("xref", StringComparison.Ordinal)) throw new PdfFormatException("جدول xref غير مدعوم (يُدعم الجدول التقليدي فقط)");
        var trailerAt = text.IndexOf("trailer", StringComparison.Ordinal);
        var lines = text[4..trailerAt].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        while (i < lines.Length)
        {
            var head = lines[i].Trim().Split(' ');
            if (head.Length != 2) { i++; continue; }
            var start = int.Parse(head[0], CultureInfo.InvariantCulture);
            var count = int.Parse(head[1], CultureInfo.InvariantCulture);
            for (var k = 0; k < count; k++)
            {
                var e = lines[i + 1 + k].Trim().Split(' ');
                var id = start + k;
                if (e.Length >= 3 && e[2] == "n" && !_offsets.ContainsKey(id)) _offsets[id] = long.Parse(e[0], CultureInfo.InvariantCulture); // الأحدث أولًا
            }
            i += count + 1;
        }
        var trailerEnd = text.IndexOf("startxref", trailerAt, StringComparison.Ordinal);
        var trailer = text[trailerAt..trailerEnd];
        var prev = PrevRx().Match(trailer);
        return (trailer, prev.Success ? long.Parse(prev.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>نص قاموس الكائن (أحدث تعريف له)؛ لكائنات القواميس البسيطة فقط (الفهرس والصفحات).</summary>
    public string ObjectDict(int id)
    {
        if (!_offsets.TryGetValue(id, out var off)) throw new PdfFormatException($"الكائن {id} غير موجود");
        var len = (int)Math.Min(_pdf.Length - off, 64 * 1024);
        var text = Encoding.Latin1.GetString(_pdf, (int)off, len);
        var start = text.IndexOf("<<", StringComparison.Ordinal);
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i++; }
            else if (text[i] == '>' && text[i + 1] == '>') { depth--; i++; if (depth == 0) return text[start..(i + 1)]; }
        }
        throw new PdfFormatException($"قاموس الكائن {id} غير مكتمل");
    }

    [GeneratedRegex(@"startxref\s+(\d+)")] private static partial Regex StartXrefRx();
    [GeneratedRegex(@"/Size\s+(\d+)")] private static partial Regex SizeRx();
    [GeneratedRegex(@"/Root\s+(\d+)\s+0\s+R")] private static partial Regex RootRx();
    [GeneratedRegex(@"/Info\s+(\d+)\s+0\s+R")] private static partial Regex InfoRx();
    [GeneratedRegex(@"/Prev\s+(\d+)")] private static partial Regex PrevRx();
    [GeneratedRegex(@"/Kids\s*\[\s*(\d+)\s+0\s+R")] private static partial Regex KidsRx();
    private static Regex RefRx(string key) => new($@"/{key}\s+(\d+)\s+0\s+R");
}

internal sealed class PdfFormatException(string message) : Exception(message);
