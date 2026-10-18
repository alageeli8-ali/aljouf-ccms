namespace Ccms.BuildingBlocks.Web;

/// <summary>سلامة الملفات المرفوعة (24 §2): نوع المحتوى يُشتق من الامتداد في الخادم لا مما يرسله المتصفح، والمحتوى يطابق توقيع نوعه (magic bytes).
/// يمنع رفع HTML أو JavaScript متنكر بامتداد مسموح ثم تنفيذه من نطاق النظام نفسه.</summary>
public static class FileSafety
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
    };

    /// <summary>نوع المحتوى الآمن للامتداد؛ وغير المعروف يُخدم ثنائيًا للتنزيل فقط.</summary>
    public static string ContentTypeFor(string fileName) =>
        Types.TryGetValue(Path.GetExtension(fileName), out var t) ? t : "application/octet-stream";

    /// <summary>هل تطابق البايتات الأولى توقيع الامتداد؟</summary>
    public static bool MatchesSignature(string fileName, ReadOnlySpan<byte> head)
    {
        static bool Starts(ReadOnlySpan<byte> h, ReadOnlySpan<byte> sig) => h.Length >= sig.Length && h[..sig.Length].SequenceEqual(sig);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => Starts(head, "%PDF-"u8),
            ".docx" or ".xlsx" or ".pptx" => Starts(head, [0x50, 0x4B, 0x03, 0x04]),
            ".png" => Starts(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ".jpg" or ".jpeg" => Starts(head, [0xFF, 0xD8, 0xFF]),
            ".wav" => Starts(head, "RIFF"u8) && head.Length >= 12 && head[8..12].SequenceEqual("WAVE"u8),
            ".mp3" => Starts(head, "ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
            ".m4a" => head.Length >= 8 && head[4..8].SequenceEqual("ftyp"u8),
            _ => false,
        };
    }

    /// <summary>اسم ملف آمن للتخزين والعرض: بلا مسارات ولا محارف تحكم، وبطول محدود.</summary>
    public static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or '*' or '?' or ':')).ToArray()).Trim();
        if (name.Length > 200) name = name[..(200 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
        return name.Length == 0 ? "file" : name;
    }
}
