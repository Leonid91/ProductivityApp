using System.Globalization;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// The date format the person set for a bank: the files of the bank are read with it, never guessed. Dates written so
/// that only one reading exists (a month in letters, the year first) are read as they are whatever the format.
/// </summary>
public static class BankDates
{
    /// <summary>How the format is written for the person.</summary>
    public static string Pattern(DateFormat format) => format switch
    {
        DateFormat.DayMonthYear => "DD/MM/YYYY",
        DateFormat.MonthDayYear => "MM/DD/YYYY",
        _ => "YYYY-MM-DD",
    };

    /// <summary>
    /// The format a new bank starts with, which the person can change: European (DD/MM/YYYY) for a bank on the French
    /// banking system or in Europe, Canadian (YYYY-MM-DD) for a bank in Canada, American (MM/DD/YYYY) for a bank in the
    /// United States; DD/MM/YYYY, the most common writing in the world, for any other country.
    /// </summary>
    public static DateFormat Default(string? country, BankingSystem system)
    {
        if (system == BankingSystem.France) return DateFormat.DayMonthYear;
        return Region(country) switch
        {
            "CA" => DateFormat.YearMonthDay,
            "US" => DateFormat.MonthDayYear,
            _ => DateFormat.DayMonthYear,
        };
    }

    /// <summary>The two-letter code of the country the person wrote ("Canada", "CA", "United States", "USA", "États-Unis"), or null.</summary>
    private static string? Region(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var wanted = TextNorm.Fold(country.Trim());
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); }
            catch (ArgumentException) { continue; } // a culture of no country
            if (new[] { region.Name, region.TwoLetterISORegionName, region.ThreeLetterISORegionName, region.EnglishName, region.NativeName, region.DisplayName }
                .Any(n => TextNorm.Fold(n) == wanted))
                return region.TwoLetterISORegionName;
        }
        return null;
    }

    /// <summary>The order of the day and the month in dates written with the year last; Unknown for the year-first format.</summary>
    public static DateOrder Order(DateFormat format) => format switch
    {
        DateFormat.DayMonthYear => DateOrder.DayMonth,
        DateFormat.MonthDayYear => DateOrder.MonthDay,
        _ => DateOrder.Unknown,
    };

    /// <summary>
    /// Why the numeric dates of a file (each as its first and second number, the year last) do not fit the bank's
    /// format, or null when they do: a day first above 12 in MM/DD/YYYY, a month second above 12 in DD/MM/YYYY, any of
    /// them in YYYY-MM-DD.
    /// </summary>
    public static string? Mismatch(DateFormat format, IReadOnlyCollection<(int First, int Second)> yearLast)
    {
        if (yearLast.Count == 0) return null;
        var dayFirst = yearLast.Any(d => d.First > 12);
        var monthFirst = yearLast.Any(d => d.Second > 12);
        var written = dayFirst && !monthFirst ? "DD/MM/YYYY" : monthFirst && !dayFirst ? "MM/DD/YYYY" : "with the year last";
        var fits = format switch
        {
            DateFormat.DayMonthYear => !monthFirst,
            DateFormat.MonthDayYear => !dayFirst,
            _ => false,
        };
        return fits ? null : $"The dates of this file are written {written}, not {Pattern(format)} as set for its bank: choose the date format they are written in.";
    }
}
