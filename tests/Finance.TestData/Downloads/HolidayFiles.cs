using System.Globalization;

namespace ProductivityApp.Finance.Testing;

/// <summary>
/// Calendars of public holidays in the layouts of the services the app reads (a few holidays each, real dates): the
/// canada-holidays.ca API (federal and Québec), the Office of Personnel Management's ICS file, France's government
/// service.
/// </summary>
public static class HolidayFiles
{
    /// <summary>The day off given for a holiday on a weekend: the Monday after it.</summary>
    private static string Off(DateOnly d) => d.AddDays(d.DayOfWeek switch { DayOfWeek.Saturday => 2, DayOfWeek.Sunday => 1, _ => 0 }).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Item(int year, int month, int day, string name)
    {
        var d = new DateOnly(year, month, day);
        return $$"""{"date":"{{d:yyyy-MM-dd}}","nameEn":"{{name}}","observedDate":"{{Off(d)}}"}""";
    }

    /// <summary>One year of Québec's holidays in the layout of canada-holidays.ca's API (three of them).</summary>
    public static string QuebecJson(int y) =>
        """{"province":{"id":"QC","holidays":[""" + string.Join(",", Item(y, 1, 1, "New Year’s Day"), Item(y, 6, 24, "Saint-Jean-Baptiste Day"), Item(y, 12, 25, "Christmas Day")) + "]}}";

    /// <summary>One year of Canada's federal holidays in the same layout (four of them; each lists its provinces).</summary>
    public static string FederalJson(int y) =>
        """{"holidays":[""" + string.Join(",", Item(y, 1, 1, "New Year’s Day"), Item(y, 11, 11, "Remembrance Day"), Item(y, 12, 25, "Christmas Day"), Item(y, 12, 26, "Boxing Day")) + "]}";

    /// <summary>The layout of the Office of Personnel Management's file: folded lines, escaped commas, no DTEND, a cancelled event.</summary>
    public const string OpmIcs = """
        BEGIN:VCALENDAR
        PRODID:-//github.com/rianjs/ical.net//NONSGML ical.net 2.2//EN
        VERSION:2.0
        X-WR-CALNAME:OPM United States Holidays
        BEGIN:VEVENT
        DESCRIPTION:OPM.gov URL: https://www.opm.gov/policy-data-oversight/pay-lea
         ve/federal-holidays/tabs/2026/
        DTSTART;VALUE=DATE:20260119
        SUMMARY:Birthday of Martin Luther King\, Jr.
        UID:1
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261126
        SUMMARY:Thanksgiving
          Day
        UID:2
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261225
        SUMMARY:Christmas Day
        UID:3
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261224
        SUMMARY:Cancelled day off
        STATUS:CANCELLED
        UID:4
        END:VEVENT
        END:VCALENDAR
        """;

    /// <summary>The layout of France's government service: "yyyy-MM-dd": name.</summary>
    public const string FranceJson = """{"2026-11-11": "11 novembre", "2026-12-25": "Jour de Noël", "2026-11-01": "Toussaint", "2025-12-25": "Jour de Noël"}""";
}
