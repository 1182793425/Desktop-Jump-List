using System.Text;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Text matching shared by every search surface (panel tiles, registry apps, folder paths), so
/// "vscode" hits "Visual Studio Code" no matter which list it lives in. Three shapes of a name
/// matter: the lowercase name itself, its compact form (punctuation and spaces dropped) and its
/// word/camelCase initials — plus an ordered-subsequence test for abbreviations typed as one word.
/// </summary>
public static class TextMatch
{
    /// <summary>Lowercase, letters and digits only: "Visual Studio Code (User)" → "visualstudiocodeuser".</summary>
    public static string Compact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }

    /// <summary>
    /// First letter of every word (and of camelCase humps): "Visual Studio Code" → "vsc". CJK
    /// characters carry no separators, so a Chinese name only contributes its first initial —
    /// <see cref="Pinyin.Initials"/> is what matches those.
    /// </summary>
    public static string WordInitials(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var atWordStart = true;
        var previous = '\0';

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                atWordStart = true;
                previous = ch;
                continue;
            }

            var hump = char.IsUpper(ch) && (char.IsLower(previous) || char.IsDigit(previous));
            if (atWordStart || hump) builder.Append(Pinyin.FirstLetter(ch));
            atWordStart = false;
            previous = ch;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Every character of <paramref name="query"/> appears in <paramref name="text"/> in order —
    /// "vscode" is not a substring of "visual studio code" but it is a subsequence. The first
    /// character must sit on a word boundary, so "ode" does not pile up noise, and
    /// <paramref name="gap"/> (the number of skipped characters) orders the surviving hits.
    /// </summary>
    public static bool IsSubsequence(string text, string query, out int gap)
    {
        gap = 0;
        if (query.Length == 0 || text.Length < query.Length) return false;

        var from = 0;
        var previous = -1;
        var first = true;

        foreach (var ch in query)
        {
            if (ch == ' ') continue;

            var found = text.IndexOf(ch, from);
            while (first && found > 0 && char.IsLetterOrDigit(text[found - 1]))
                found = text.IndexOf(ch, found + 1);

            if (found < 0) { gap = 0; return false; }
            if (previous >= 0) gap += found - previous - 1;
            previous = found;
            from = found + 1;
            first = false;
        }

        return true;
    }

    /// <summary>True when some word of <paramref name="text"/> starts with <paramref name="query"/>.</summary>
    public static bool HasWordPrefix(string text, string query)
    {
        if (query.Length == 0) return false;
        var from = 0;
        while (from < text.Length)
        {
            var at = text.IndexOf(query, from, StringComparison.Ordinal);
            if (at < 0) return false;
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>
    /// The one ranking ladder every search surface shares, most to least specific. Callers resume
    /// their own tail (target path, exe name, publisher) from <see cref="RankBelowName"/>;
    /// -1 means "not a match at all".
    /// </summary>
    /// <param name="gap">Skipped-character count of the subsequence hit, for tie-breaking.</param>
    public static int RankName(string name, string compactName, string initials, string wordInitials,
        string pinyin, string query, string compactQuery, out int gap)
    {
        gap = 0;
        if (name.Length == 0 || query.Length == 0) return -1;

        if (string.Equals(name, query, StringComparison.Ordinal)) return 0;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (HasWordPrefix(name, query)) return 2;
        if (name.Contains(query, StringComparison.Ordinal)) return 3;
        if (compactQuery.Length > 0 && compactName.Contains(compactQuery, StringComparison.Ordinal)) return 4;
        if (initials.Length > 0 && initials.StartsWith(compactQuery, StringComparison.Ordinal)) return 5;
        if (wordInitials.Length > 0 && wordInitials.StartsWith(compactQuery, StringComparison.Ordinal)) return 6;
        if (initials.Length > 0 && initials.Contains(compactQuery, StringComparison.Ordinal)) return 7;
        if (wordInitials.Length > 0 && wordInitials.Contains(compactQuery, StringComparison.Ordinal)) return 8;

        // Full pinyin: "weixin" and "shejigao" reach 微信 / 设计稿, not just their initials.
        if (IsAsciiWord(compactQuery) && pinyin.Length > 0)
        {
            if (pinyin.StartsWith(compactQuery, StringComparison.Ordinal)) return 9;
            if (compactQuery.Length >= 3 && pinyin.Contains(compactQuery, StringComparison.Ordinal)) return 10;
        }

        // Abbreviations typed as one word: "vscode" is not a substring of "visual studio code",
        // but it is. Short queries are excluded — they would match almost everything.
        if (compactQuery.Length >= 3 && IsSubsequence(name, compactQuery, out gap)) return 11;
        return -1;
    }

    private static bool IsAsciiWord(string text)
    {
        if (text.Length == 0) return false;
        foreach (var ch in text)
            if (ch < 'a' || ch > 'z') return false;
        return true;
    }

    /// <summary>First rank a caller may use for its own tail rules (target path, exe name, ...).</summary>
    public const int RankBelowName = 12;
}
