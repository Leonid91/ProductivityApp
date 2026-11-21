using System.Text.RegularExpressions;

namespace ProductivityApp.Finance.Text;

/// <summary>Order of day and month in numeric dates such as 03/04/2025.</summary>
public enum DateOrder { Unknown = 0, DayMonth = 1, MonthDay = 2 }

/// <summary>
/// A date as printed on a statement. Textual dates ("12 oct.", "Oct 12, 2025") know their month.
/// Numeric dates ("12/10", "12/10/2025") keep both parts until the statement-wide order is known.
/// </summary>
public readonly record struct PrintedDate(int A, int B, int? Year, bool MonthKnown, bool IsoOrder)
{
    /// <summary>When MonthKnown, A is the day and B the month.</summary>
    public (int Day, int Month)? Resolve(DateOrder order)
    {
        if (MonthKnown || IsoOrder) return Valid(A, B);
        return order switch
        {
            DateOrder.MonthDay => Valid(B, A),
            _ => Valid(A, B),
        };
    }

    public bool PossibleAsDayMonth => MonthKnown || IsoOrder || (A is >= 1 and <= 31 && B is >= 1 and <= 12);
    public bool PossibleAsMonthDay => !MonthKnown && !IsoOrder && B is >= 1 and <= 31 && A is >= 1 and <= 12;

    public DateOnly? ToDate(DateOrder order, int fallbackYear)
    {
        var r = Resolve(order);
        if (r is null) return null;
        var y = Year ?? fallbackYear;
        if (r.Value.Day > DateTime.DaysInMonth(y, r.Value.Month)) return null;
        return new DateOnly(y, r.Value.Month, r.Value.Day);
    }

    private static (int, int)? Valid(int day, int month) =>
        day is >= 1 and <= 31 && month is >= 1 and <= 12 ? (day, month) : null;
}

public static partial class DateParser
{
    private static readonly Dictionary<string, int> Months = BuildMonths();

    private static Dictionary<string, int> BuildMonths()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(int m, params string[] names) { foreach (var n in names) d[n] = m; }
        Add(1, "JANVIER", "JANV", "JAN", "JANUARY");
        Add(2, "FEVRIER", "FEVR", "FEV", "FEB", "FEBRUARY");
        Add(3, "MARS", "MAR", "MARCH");
        Add(4, "AVRIL", "AVR", "APR", "APRIL");
        Add(5, "MAI", "MAY");
        Add(6, "JUIN", "JUN", "JUNE");
        Add(7, "JUILLET", "JUIL", "JUL", "JULY");
        Add(8, "AOUT", "AOU", "AUG", "AUGUST");
        Add(9, "SEPTEMBRE", "SEPT", "SEP", "SEPTEMBER");
        Add(10, "OCTOBRE", "OCT", "OCTOBER");
        Add(11, "NOVEMBRE", "NOV", "NOVEMBER");
        Add(12, "DECEMBRE", "DEC", "DECEMBER");
        return d;
    }

    [GeneratedRegex(@"^(?<y>(?:19|20)\d{2})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})$")]
    private static partial Regex Iso();

    [GeneratedRegex(@"^(?<a>\d{1,2})[/.\-](?<b>\d{1,2})(?:[/.\-](?<y>\d{2}|\d{4}))?$")]
    private static partial Regex Numeric();

    // 12 OCT, 12OCT, 12-OCT-25, 1ER OCTOBRE 2025, 12 OCT. 2025
    [GeneratedRegex(@"^(?<d>\d{1,2})(?:ER)?[\s\-/.]*(?<mon>[A-Z]{3,9})\.?(?:[\s\-/.,]*(?<y>\d{4}|\d{2}))?$")]
    private static partial Regex DayMonthName();

    // OCT 12, OCT12, OCT. 12 2025, OCTOBER 12, 2025
    [GeneratedRegex(@"^(?<mon>[A-Z]{3,9})\.?[\s\-/.]*(?<d>\d{1,2})(?:ST|ND|RD|TH)?(?:[\s,\-/.]+(?<y>\d{4}))?$")]
    private static partial Regex MonthNameDay();

    /// <summary>Parses a single date written as one string (tokens already joined by spaces).</summary>
    /// <param name="lenient">Also accept "12.10" (day.month without a year), which is otherwise read as money.
    /// Used when a column template says the text is a date.</param>
    public static bool TryParse(string text, out PrintedDate date, bool lenient = false)
    {
        date = default;
        var t = TextNorm.Fold(text).Trim().TrimEnd(',', ';', ':');
        if (t.Length is < 3 or > 24) return false;

        var m = Iso().Match(t);
        if (m.Success)
        {
            var mo = int.Parse(m.Groups["m"].Value);
            var d = int.Parse(m.Groups["d"].Value);
            if (mo is < 1 or > 12 || d is < 1 or > 31) return false;
            date = new PrintedDate(d, mo, int.Parse(m.Groups["y"].Value), MonthKnown: false, IsoOrder: true);
            return true;
        }

        m = Numeric().Match(t);
        if (m.Success)
        {
            var a = int.Parse(m.Groups["a"].Value);
            var b = int.Parse(m.Groups["b"].Value);
            int? y = m.Groups["y"].Success ? NormalizeYear(int.Parse(m.Groups["y"].Value)) : null;
            var p = new PrintedDate(a, b, y, MonthKnown: false, IsoOrder: false);
            if (!p.PossibleAsDayMonth && !p.PossibleAsMonthDay) return false;
            // "12.50" style tokens without a year are amounts or rates, not dates.
            if (y is null && t.Contains('.') && !lenient) return false;
            date = p;
            return true;
        }

        m = DayMonthName().Match(t);
        if (m.Success && Months.TryGetValue(m.Groups["mon"].Value, out var month1))
        {
            var d = int.Parse(m.Groups["d"].Value);
            if (d is < 1 or > 31) return false;
            int? y = m.Groups["y"].Success ? NormalizeYear(int.Parse(m.Groups["y"].Value)) : null;
            date = new PrintedDate(d, month1, y, MonthKnown: true, IsoOrder: false);
            return true;
        }

        m = MonthNameDay().Match(t);
        if (m.Success && Months.TryGetValue(m.Groups["mon"].Value, out var month2))
        {
            var d = int.Parse(m.Groups["d"].Value);
            if (d is < 1 or > 31) return false;
            int? y = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : null;
            date = new PrintedDate(d, month2, y, MonthKnown: true, IsoOrder: false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tries to read a date made of up to four consecutive tokens starting at <paramref name="start"/>
    /// (dates are often split: "12" "OCT" or "Oct." "12," "2025"). Longest match wins.
    /// </summary>
    public static bool TryParseTokens(IReadOnlyList<string> tokens, int start, out PrintedDate date, out int consumed, bool lenient = false)
    {
        date = default;
        consumed = 0;
        for (var n = Math.Min(4, tokens.Count - start); n >= 1; n--)
        {
            var joined = string.Join(' ', tokens.Skip(start).Take(n));
            if (TryParse(joined, out date, lenient))
            {
                consumed = n;
                return true;
            }
            // A date printed in pieces ("16/" "12/" "2022", "10/" "12/2024"): one date.
            if (n > 1 && joined.Contains('/') && (joined.Contains("/ ", StringComparison.Ordinal) || joined.Contains(" /", StringComparison.Ordinal))
                && TryParse(joined.Replace("/ ", "/", StringComparison.Ordinal).Replace(" /", "/", StringComparison.Ordinal), out date, lenient)
                && !joined.Replace("/ ", "/", StringComparison.Ordinal).Replace(" /", "/", StringComparison.Ordinal).Contains(' '))
            {
                consumed = n;
                return true;
            }
        }
        return false;
    }

    private static int NormalizeYear(int y) => y < 100 ? 2000 + y : y;
}
