using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Ccms.BuildingBlocks.Pdf;

/// <summary>قارئ خطوط TrueType بالحد اللازم لإنتاج PDF: الخريطة (cmap)، وعروض المحارف، واقتطاع المحارف المستخدمة فقط للتضمين.
/// الخطوط المضمنة Noto Sans وNoto Sans Arabic برخصة SIL OFL 1.1 (Pdf/Fonts/OFL.txt).</summary>
public sealed class TrueTypeFont
{
    private static readonly ConcurrentDictionary<string, TrueTypeFont> Cache = new();

    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();
    private readonly Dictionary<int, int> _cmap = new();
    private readonly ushort[] _advances;
    private readonly int _numHMetrics;
    private readonly uint[] _loca;

    public string Name { get; }
    public int UnitsPerEm { get; }
    public int NumGlyphs { get; }
    public short Ascender { get; }
    public short Descender { get; }
    public (short XMin, short YMin, short XMax, short YMax) BBox { get; }

    public static TrueTypeFont Embedded(string fileName) => Cache.GetOrAdd(fileName, f =>
    {
        using var s = typeof(TrueTypeFont).Assembly.GetManifestResourceStream("Ccms.Pdf.Fonts." + f)
            ?? throw new FileNotFoundException("الخط غير مضمن", f);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new TrueTypeFont(Path.GetFileNameWithoutExtension(f), ms.ToArray());
    });

    public TrueTypeFont(string name, byte[] data)
    {
        Name = name;
        _data = data;
        int numTables = U16(4);
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            _tables[tag] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }
        var head = T("head");
        UnitsPerEm = U16(head + 18);
        BBox = (S16(head + 36), S16(head + 38), S16(head + 40), S16(head + 42));
        var longLoca = S16(head + 50) == 1;
        var hhea = T("hhea");
        Ascender = S16(hhea + 4);
        Descender = S16(hhea + 6);
        _numHMetrics = U16(hhea + 34);
        NumGlyphs = U16(T("maxp") + 4);
        var hmtx = T("hmtx");
        _advances = new ushort[_numHMetrics];
        for (var i = 0; i < _numHMetrics; i++) _advances[i] = (ushort)U16(hmtx + i * 4);
        var loca = T("loca");
        _loca = new uint[NumGlyphs + 1];
        for (var i = 0; i <= NumGlyphs; i++) _loca[i] = longLoca ? U32(loca + i * 4) : (uint)U16(loca + i * 2) * 2;
        ReadCmap();
    }

    private int T(string tag) => _tables.TryGetValue(tag, out var t) ? t.Offset : throw new InvalidDataException($"جدول {tag} مفقود في الخط {Name}");
    private int U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o));
    private short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(o));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(o));

    private void ReadCmap()
    {
        var cmap = T("cmap");
        int n = U16(cmap + 2);
        int best = -1, bestFormat = 0;
        for (var i = 0; i < n; i++)
        {
            int platform = U16(cmap + 4 + i * 8), encoding = U16(cmap + 6 + i * 8);
            var off = cmap + (int)U32(cmap + 8 + i * 8);
            var format = U16(off);
            if (platform == 3 && encoding == 10 && format == 12) { best = off; bestFormat = 12; break; }
            if ((platform == 3 && encoding == 1 || platform == 0) && format == 4 && bestFormat != 12) { best = off; bestFormat = 4; }
        }
        if (best < 0) throw new InvalidDataException("لا توجد خريطة Unicode في الخط");
        if (bestFormat == 12)
        {
            var groups = U32(best + 12);
            for (var g = 0; g < groups; g++)
            {
                var o = best + 16 + g * 12;
                uint start = U32(o), end = U32(o + 4), gid = U32(o + 8);
                for (var c = start; c <= end; c++) _cmap[(int)c] = (int)(gid + c - start);
            }
            return;
        }
        var segX2 = U16(best + 6);
        var ends = best + 14;
        var starts = ends + segX2 + 2;
        var deltas = starts + segX2;
        var ranges = deltas + segX2;
        for (var s = 0; s < segX2 / 2; s++)
        {
            int end = U16(ends + s * 2), start = U16(starts + s * 2);
            var delta = S16(deltas + s * 2);
            var rangeOffset = U16(ranges + s * 2);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid;
                if (rangeOffset == 0) gid = (c + delta) & 0xFFFF;
                else
                {
                    var addr = ranges + s * 2 + rangeOffset + (c - start) * 2;
                    gid = U16(addr);
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0) _cmap[c] = gid;
            }
        }
    }

    public bool Has(int codepoint) => _cmap.ContainsKey(codepoint);
    public int Glyph(int codepoint) => _cmap.TryGetValue(codepoint, out var g) ? g : 0;

    /// <summary>العرض بوحدات 1/1000 من حجم الخط (وحدة PDF).</summary>
    public double Advance(int gid) => _advances[Math.Min(gid, _numHMetrics - 1)] * 1000.0 / UnitsPerEm;

    public double Scale(short v) => v * 1000.0 / UnitsPerEm;

    /// <summary>خط مقتطع: تبقى أرقام المحارف كما هي، وتُفرغ المحارف غير المستخدمة، فيصغر الملف دون إعادة ترقيم.</summary>
    public byte[] Subset(IEnumerable<int> usedGlyphs)
    {
        var glyf = T("glyf");
        var keep = new HashSet<int> { 0 };
        var queue = new Queue<int>(usedGlyphs.Where(g => g > 0 && g < NumGlyphs));
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (!keep.Add(g)) continue;
            var start = glyf + (int)_loca[g];
            if (_loca[g + 1] == _loca[g] || S16(start) >= 0) continue;
            var p = start + 10;
            while (true)
            {
                var flags = U16(p);
                queue.Enqueue(U16(p + 2));
                p += 4 + ((flags & 0x1) != 0 ? 4 : 2);
                if ((flags & 0x8) != 0) p += 2;
                else if ((flags & 0x40) != 0) p += 4;
                else if ((flags & 0x80) != 0) p += 8;
                if ((flags & 0x20) == 0) break;
            }
        }

        using var newGlyf = new MemoryStream();
        var newLoca = new byte[(NumGlyphs + 1) * 4];
        for (var g = 0; g < NumGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Length);
            if (!keep.Contains(g)) continue;
            var len = (int)(_loca[g + 1] - _loca[g]);
            newGlyf.Write(_data, glyf + (int)_loca[g], len);
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(NumGlyphs * 4), (uint)newGlyf.Length);

        var head = _data.AsSpan(T("head"), _tables["head"].Length).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head, ["glyf"] = newGlyf.ToArray(), ["loca"] = newLoca,
        };
        foreach (var tag in new[] { "hhea", "maxp", "hmtx", "cvt ", "fpgm", "prep", "OS/2", "post" })
            if (_tables.TryGetValue(tag, out var t)) tables[tag] = _data.AsSpan(t.Offset, t.Length).ToArray();

        using var o = new MemoryStream();
        var count = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = (1 << entrySelector) * 16;
        var header = new byte[12 + count * 16];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)(count * 16 - searchRange));
        var offset = header.Length;
        var i = 0;
        foreach (var (tag, bytes) in tables)
        {
            var rec = 12 + i++ * 16;
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(header, rec);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 4), Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 12), (uint)bytes.Length);
            offset += (bytes.Length + 3) & ~3;
        }
        o.Write(header);
        foreach (var bytes in tables.Values)
        {
            o.Write(bytes);
            for (var p = bytes.Length; p % 4 != 0; p++) o.WriteByte(0);
        }
        return o.ToArray();
    }

    private static uint Checksum(byte[] b)
    {
        uint sum = 0;
        for (var i = 0; i < b.Length; i += 4)
        {
            uint v = 0;
            for (var j = 0; j < 4; j++) v = (v << 8) | (i + j < b.Length ? b[i + j] : 0u);
            sum += v;
        }
        return sum;
    }
}
