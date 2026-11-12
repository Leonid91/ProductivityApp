using System.Globalization;

namespace ProductivityApp.Finance.Services;

public enum Granularity { Month = 0, Year = 1, Range = 2 }

/// <summary>Date and period wording shared by the interface and the PDF reports.</summary>
public static class Formatting
{
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Date formats offered in the Date column menu. The first one is the default.</summary>
    public static readonly IReadOnlyList<string> DateFormats = ["DD month YYYY", "YYYY-MM-DD", "DD-MM-YYYY", "DD/MM/YYYY", "MM/DD/YYYY"];

    public static string FormatDate(DateOnly d, string format) => format switch
    {
        "YYYY-MM-DD" => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "DD-MM-YYYY" => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        "DD/MM/YYYY" => d.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture),
        "MM/DD/YYYY" => d.ToString("MM'/'dd'/'yyyy", CultureInfo.InvariantCulture),
        _ => d.ToString("dd MMMM yyyy", English),
    };

    /// <summary>
    /// "October, from Wednesday 1 to Friday 31, 2025" for a month,
    /// "2025, from Wednesday, January 1 to Wednesday, December 31" for a year,
    /// "From Sunday, January 1, 2023 to Wednesday, December 31, 2025" for any other range.
    /// </summary>
    public static string PeriodTitle(DateOnly from, DateOnly to, Granularity g)
    {
        string Day(DateOnly d) => d.ToString("dddd", English) + " " + d.Day.ToString(CultureInfo.InvariantCulture);
        string DayMonth(DateOnly d) => d.ToString("dddd, MMMM ", English) + d.Day.ToString(CultureInfo.InvariantCulture);
        // A whole month or a whole year is named alone ("October 2026", "2026").
        if (g == Granularity.Month && from.Year == to.Year && from.Month == to.Month)
            return from.Day == 1 && to.Day == DateTime.DaysInMonth(to.Year, to.Month)
                ? $"{from.ToString("MMMM", English)} {from.Year}"
                : $"{from.ToString("MMMM", English)}, from {Day(from)} to {Day(to)}, {from.Year}";
        if (g == Granularity.Year && from.Year == to.Year)
            return from is { Month: 1, Day: 1 } && to is { Month: 12, Day: 31 }
                ? from.Year.ToString(CultureInfo.InvariantCulture)
                : $"{from.Year}, from {DayMonth(from)} to {DayMonth(to)}";
        return $"From {DayMonth(from)}, {from.Year} to {DayMonth(to)}, {to.Year}";
    }

    public static string Money(decimal value, string currency, bool sign = false)
    {
        var abs = Math.Abs(value).ToString("#,##0.00", CultureInfo.InvariantCulture);
        var prefix = value < 0 ? "−" : sign && value > 0 ? "+" : "";
        return currency switch
        {
            "CAD" or "USD" or "AUD" or "NZD" => $"{prefix}${abs}",
            "EUR" => $"{prefix}€{abs}",
            "GBP" => $"{prefix}£{abs}",
            _ => $"{prefix}{abs} {currency}",
        };
    }

    public static DateOnly MonthEnd(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month));
}
