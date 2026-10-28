using System.Globalization;
using System.Text;

namespace ProductivityApp.Finance.Text;

public static class TextNorm
{
    public static string StripAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Upper case, no accents, typographic apostrophes and dashes made plain, spaces collapsed.</summary>
    public static string Fold(string s)
    {
        var t = StripAccents(s).ToUpperInvariant()
            .Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'')
            .Replace('–', '-').Replace('—', '-').Replace('−', '-')
            .Replace(' ', ' ').Replace(' ', ' ');
        return CollapseSpaces(t);
    }

    public static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>"TIM HORTONS" becomes "Tim Hortons". Two-letter all-letter tokens other than common particles stay upper case.</summary>
    public static string TitleCase(string s)
    {
        var words = CollapseSpaces(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (w.Length <= 2 && w.All(char.IsLetter) && !IsParticle(w)) { sb.Append(w.ToUpperInvariant()); continue; }
            sb.Append(char.ToUpperInvariant(w[0]));
            sb.Append(w[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    private static bool IsParticle(string w) => w.ToUpperInvariant() is "OF" or "LE" or "LA" or "DE" or "DU" or "ET" or "AU" or "EN" or "IN" or "ON" or "AT" or "TO";
}
