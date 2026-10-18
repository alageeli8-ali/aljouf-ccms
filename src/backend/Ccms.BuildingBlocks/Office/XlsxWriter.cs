using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Ccms.BuildingBlocks.Office;

/// <summary>كاتب Excel (Office Open XML) مصغّر دون مكتبات خارجية: أوراق باتجاه من اليمين لليسار، وصف عناوين مثبت بمرشح، ونصوص وأرقام وتواريخ.
/// يكفي لتصدير التقارير الجدولية؛ لا صيغ ولا رسوم.</summary>
public sealed class XlsxWriter
{
    private sealed record Sheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<double> Widths, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Rtl);
    private readonly List<Sheet> _sheets = [];

    public string Creator { get; init; } = "CCMS";

    public XlsxWriter AddSheet(string name, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<double>? widths = null, bool rtl = true)
    {
        var safe = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        safe = safe.Length > 31 ? safe[..31] : safe.Length == 0 ? $"Sheet{_sheets.Count + 1}" : safe;
        _sheets.Add(new Sheet(safe, headers, widths ?? headers.Select(_ => 18.0).ToList(), rows, rtl));
        return this;
    }

    public byte[] Build()
    {
        if (_sheets.Count == 0) throw new InvalidOperationException("no sheets");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, string xml)
            {
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
                s.Write(xml);
            }
            var overrides = string.Concat(_sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
            Entry("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" + overrides + "</Types>");
            Entry("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Entry("docProps/core.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                $"<dc:creator>{Esc(Creator)}</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>");
            Entry("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets>" + string.Concat(_sheets.Select((s, i) => $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" +
                "<definedNames>" + string.Concat(_sheets.Select((s, i) => s.Rows.Count == 0 ? "" :
                    $"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Esc(s.Name.Replace("'", "''"))}'!$A$1:${Col(s.Headers.Count - 1)}${s.Rows.Count + 1}</definedName>")) +
                "</definedNames></workbook>");
            Entry("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(_sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Entry("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\"/><numFmt numFmtId=\"165\" formatCode=\"yyyy-mm-dd hh:mm\"/></numFmts>" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Arial\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF006837\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
            for (var i = 0; i < _sheets.Count; i++) Entry($"xl/worksheets/sheet{i + 1}.xml", SheetXml(_sheets[i]));
        }
        return ms.ToArray();
    }

    private static string SheetXml(Sheet s)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(s.Rtl ? " rightToLeft=\"1\"" : "").Append('>');
        sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols>");
        for (var c = 0; c < s.Headers.Count; c++)
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{(c < s.Widths.Count ? s.Widths[c] : 18).ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < s.Headers.Count; c++) sb.Append($"<c r=\"{Col(c)}1\" t=\"inlineStr\" s=\"1\"><is><t>{Esc(s.Headers[c])}</t></is></c>");
        sb.Append("</row>");
        for (var r = 0; r < s.Rows.Count; r++)
        {
            var row = s.Rows[r];
            sb.Append($"<row r=\"{r + 2}\">");
            for (var c = 0; c < row.Count; c++)
            {
                var refc = $"{Col(c)}{r + 2}";
                switch (row[c])
                {
                    case null: break;
                    case int or long or short or decimal or double or float:
                        sb.Append($"<c r=\"{refc}\"><v>{Convert.ToString(row[c], CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case bool b:
                        sb.Append($"<c r=\"{refc}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                        break;
                    case DateOnly d:
                        sb.Append($"<c r=\"{refc}\" s=\"2\"><v>{Serial(d.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case DateTime dt:
                        sb.Append($"<c r=\"{refc}\" s=\"3\"><v>{Serial(dt).ToString("0.########", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    default:
                        var text = Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? "";
                        sb.Append($"<c r=\"{refc}\" t=\"inlineStr\"{(text.Length > 60 ? " s=\"4\"" : "")}><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                        break;
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (s.Rows.Count > 0) sb.Append($"<autoFilter ref=\"A1:{Col(s.Headers.Count - 1)}{s.Rows.Count + 1}\"/>");
        sb.Append("<pageSetup orientation=\"landscape\" paperSize=\"9\"/></worksheet>");
        return sb.ToString();
    }

    private static double Serial(DateTime d) => (d - new DateTime(1899, 12, 30)).TotalDays;

    public static string Col(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) continue; // محارف غير مسموحة في XML
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }
        return sb.ToString();
    }
}
