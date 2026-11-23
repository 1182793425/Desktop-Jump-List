using System.Text;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Pinyin initials for common Chinese characters (GB2312 level-1, ~3700 chars), so "wx" finds 微信.
/// Uses the GB2312 code-point ordering trick; no dictionary file needed.
/// </summary>
public static class Pinyin
{
    private static readonly Encoding? Gb2312;
    private static readonly int[] Bounds =
    {
        0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE, 0xBBF7,
        0xBFA6, 0xC0AC, 0xC2E8, 0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA, 0xC8BB, 0xC8F6,
        0xCBFA, 0xCDDA, 0xCEF4, 0xD1B9, 0xD4D1, 0xD7FA
    };
    private const string Letters = "abcdefghjklmnopqrstwxyz";

    static Pinyin()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Gb2312 = Encoding.GetEncoding("GB2312");
        }
        catch { Gb2312 = null; }
    }

    public static string Initials(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x80)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                continue;
            }
            var c = Initial(ch);
            if (c != '\0') sb.Append(c);
        }
        return sb.ToString();
    }

    private static char Initial(char ch)
    {
        if (Gb2312 is null || ch < 0x4E00 || ch > 0x9FA5) return '\0';
        var bytes = Gb2312.GetBytes(ch.ToString());
        if (bytes.Length != 2) return '\0';
        int code = bytes[0] << 8 | bytes[1];
        if (code < Bounds[0] || code > 0xD7F9) return '\0';
        for (int i = Bounds.Length - 2; i >= 0; i--)
        {
            if (code >= Bounds[i]) return Letters[i];
        }
        return '\0';
    }
}
