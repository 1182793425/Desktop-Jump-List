using System.Text;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Pinyin for search. Initials come from the GB2312 code-point ordering trick (~3700 chars, no
/// dictionary); full syllables come from TinyPinyin's table so "shejigao" reaches 设计稿 the way
/// "sjg" already does. Both are only ever called on strings the user can see, never per keystroke
/// over the whole index — the file entry caches its own copy at build/load time.
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

    /// <summary>First letter of a word: ASCII letters are lower-cased, Han gets its initial.</summary>
    public static char FirstLetter(char ch)
    {
        if (ch < 0x80) return char.ToLowerInvariant(ch);
        var initial = Initial(ch);
        return initial != '\0' ? initial : char.ToLowerInvariant(ch);
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

    /// <summary>True when the text has at least one Han character (the only case needing the table).</summary>
    public static bool HasHan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
            if (ch >= 0x3400 && ch <= 0x9FFF) return true;
        return false;
    }

    /// <summary>
    /// The one string file search scans: the lower-cased path, where every segment that contains
    /// Han characters also carries its pinyin right behind it —
    /// "d://新建文件夹//说明.txt" → "d://新建文件夹|xinjianwenjianjia//说明|shuoming.txt".
    /// One scan then answers the literal query, the pinyin query, and a query that mixes both in
    /// either order inside a segment. ASCII-only paths (the vast majority) come back lower-cased
    /// and unchanged, so they pay nothing.
    /// </summary>
    public static string SearchForm(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var lower = path.ToLowerInvariant();
        if (!HasHan(lower)) return lower;

        var segments = lower.Split('\\');
        for (var i = 0; i < segments.Length; i++)
            if (HasHan(segments[i])) segments[i] = segments[i] + "|" + Full(segments[i]);
        return string.Join('\\', segments);
    }

    /// <summary>
    /// Full pinyin, lower-case and compact: 设计稿 → "shejigao", 违规明细_qsi.csv →
    /// "weiguimingxi_qsi.csv". Pure-ASCII text is returned lower-cased untouched, which both keeps
    /// the common case free of table lookups and lets callers store one string for both.
    /// </summary>
    public static string Full(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!HasHan(text)) return text.ToLowerInvariant();
        try
        {
            return TinyPinyin.PinyinHelper.GetPinyin(text)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }
        catch { return text.ToLowerInvariant(); }
    }

    /// <summary>
    /// What search actually stores: <see cref="Full"/> plus the alternate readings of any
    /// polyphone, joined by <see cref="VariantSeparator"/>.
    /// </summary>
    /// <remarks>
    /// TinyPinyin gives one reading per character and often picks the wrong one for names:
    /// 音乐 came back as "yin le", so "qqyinyue" never found 「QQ音乐」. The variants are appended
    /// rather than kept in a second field so a plain <c>Contains</c> over this string already
    /// answers the alternate spellings — no caller has to know about them.
    /// </remarks>
    public static string FullSearch(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var primary = Full(text);
        if (!HasHan(text)) return primary;
        var variants = Variants(text, primary);
        return variants.Length == 0 ? primary : primary + VariantSeparator + variants;
    }

    /// <summary>Separates the primary spelling from its alternates inside the search string.</summary>
    public const char VariantSeparator = '\u0001';

    /// <summary>At most this many alternates per name; a name with more is already unusual.</summary>
    private const int MaxVariants = 3;

    /// <summary>
    /// Alternate spellings, one per polyphone in the name (each flipped on its own, which is what
    /// covers 音乐 → yinyue and 重庆 → chongqing). Returns "" when nothing is ambiguous.
    /// </summary>
    private static string Variants(string text, string primary)
    {
        var syllables = Syllables(text);
        if (syllables is null) return string.Empty;

        var variants = new List<string>(MaxVariants);
        for (var i = 0; i < text.Length && variants.Count < MaxVariants; i++)
        {
            if (!Polyphones.TryGetValue(text[i], out var readings)) continue;

            var chosen = syllables[i];
            foreach (var reading in readings)
            {
                if (string.Equals(reading, chosen, StringComparison.OrdinalIgnoreCase)) continue;
                syllables[i] = reading;
                var variant = string.Concat(syllables).ToLowerInvariant();
                if (!string.Equals(variant, primary, StringComparison.Ordinal)) variants.Add(variant);
                break;
            }
            syllables[i] = chosen;
        }

        return string.Join(VariantSeparator, variants);
    }

    /// <summary>Per-character syllables of <paramref name="text"/>, or null when a call fails.</summary>
    private static string[]? Syllables(string text)
    {
        var result = new string[text.Length];
        try
        {
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                result[i] = ch < 0x80
                    ? ch.ToString()
                    : TinyPinyin.PinyinHelper.GetPinyin(ch.ToString()).Replace(" ", string.Empty);
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>
    /// Characters whose reading depends on the word, with every common reading. The primary is
    /// whatever TinyPinyin returns; these are used to build the alternates.
    /// </summary>
    private static readonly Dictionary<char, string[]> Polyphones = new()
    {
        // 只收两个读音确实不同、且常见于文件名/软件名的字。
        ['乐'] = new[] { "le", "yue" }, ['行'] = new[] { "xing", "hang" },
        ['重'] = new[] { "zhong", "chong" }, ['长'] = new[] { "chang", "zhang" },
        ['传'] = new[] { "chuan", "zhuan" }, ['调'] = new[] { "diao", "tiao" },
        ['会'] = new[] { "hui", "kuai" }, ['觉'] = new[] { "jue", "jiao" },
        ['角'] = new[] { "jiao", "jue" }, ['强'] = new[] { "qiang", "jiang" },
        ['省'] = new[] { "sheng", "xing" }, ['系'] = new[] { "xi", "ji" },
        ['种'] = new[] { "zhong", "chong" }, ['着'] = new[] { "zhe", "zhao" },
        ['都'] = new[] { "dou", "du" }, ['还'] = new[] { "hai", "huan" },
        ['得'] = new[] { "de", "dei" }, ['差'] = new[] { "cha", "chai" },
        ['便'] = new[] { "bian", "pian" }, ['给'] = new[] { "gei", "ji" },
        ['落'] = new[] { "luo", "la" }, ['说'] = new[] { "shuo", "shui" },
        ['择'] = new[] { "ze", "zhai" }, ['朝'] = new[] { "chao", "zhao" },
        ['称'] = new[] { "cheng", "chen" }, ['乘'] = new[] { "cheng", "sheng" },
        ['恶'] = new[] { "e", "wu" }, ['和'] = new[] { "he", "huo" },
        ['解'] = new[] { "jie", "xie" }, ['屏'] = new[] { "ping", "bing" },
        ['亲'] = new[] { "qin", "qing" }, ['识'] = new[] { "shi", "zhi" },
        ['似'] = new[] { "si", "shi" }, ['血'] = new[] { "xue", "xie" },
        ['仔'] = new[] { "zai", "zi" }, ['地'] = new[] { "di", "de" },
        ['大'] = new[] { "da", "dai" }, ['石'] = new[] { "shi", "dan" },
        ['宿'] = new[] { "su", "xiu" }, ['提'] = new[] { "ti", "di" },
        ['扎'] = new[] { "zha", "za" }, ['曾'] = new[] { "zeng", "ceng" },
        ['车'] = new[] { "che", "ju" }, ['没'] = new[] { "mei", "mo" },
        ['归'] = new[] { "gui", "kui" }
    };


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
