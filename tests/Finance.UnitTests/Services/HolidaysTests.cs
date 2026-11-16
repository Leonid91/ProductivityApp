using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.UnitTests.Services;

/// <summary>Reading calendars of public holidays, their addresses and the working days they leave (no database here).</summary>
public class HolidaysTests
{
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Parse_CanadaHolidaysJsonWithAWeekendHoliday_KeepsTheHolidayAndItsObservedDayOff()
    {
        // Christmas 2027 is a Saturday; the day off is Monday the 27th.
        var result = Holidays.Parse(Utf8(HolidayFiles.QuebecJson(2027)));

        Assert.Equal([new Holiday(new(2027, 1, 1), "New Year’s Day"), new Holiday(new(2027, 6, 24), "Saint-Jean-Baptiste Day"),
            new Holiday(new(2027, 12, 25), "Christmas Day"), new Holiday(new(2027, 12, 27), "Christmas Day", Observed: true)], result);
    }

    [Fact]
    public void Parse_FederalListNamingProvinces_ReadsOnlyTheHolidays()
    {
        // Boxing Day 2026 (a Saturday) is given on Monday the 28th; the provinces of each holiday are not holidays.
        var result = Holidays.Parse(Utf8(HolidayFiles.FederalJson(2026)));

        Assert.Equal(5, result.Count);
        Assert.Contains(new Holiday(new(2026, 12, 28), "Boxing Day", Observed: true), result);
    }

    [Fact]
    public void Parse_IcsWithFoldedLinesEscapedCommasAndACancelledEvent_ReadsTheEventsThatHappen()
    {
        var result = Holidays.Parse(Utf8(HolidayFiles.OpmIcs));

        Assert.Equal(["Birthday of Martin Luther King, Jr.", "Thanksgiving Day", "Christmas Day"], result.Select(x => x.Name));
    }

    [Fact]
    public void Parse_IcsEventOverSeveralDays_GivesEachDayWithoutTheEndDay()
    {
        var result = Holidays.Parse(Utf8("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261224\r\nDTEND;VALUE=DATE:20261227\r\nSUMMARY:Holidays\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));

        Assert.Equal([new DateOnly(2026, 12, 24), new(2026, 12, 25), new(2026, 12, 26)], result.Select(x => x.Day));
    }

    [Fact]
    public void Parse_FrenchGovernmentObjectOfDays_ReadsEveryDay()
    {
        var result = Holidays.Parse(Utf8(HolidayFiles.FranceJson));

        Assert.Equal(4, result.Count);
        Assert.Contains(new Holiday(new(2026, 11, 11), "11 novembre"), result);
    }

    [Fact]
    public void Parse_ListOfObjectsWithDateAndName_ReadsEachHoliday()
    {
        var result = Holidays.Parse(Utf8("""[{"date":"2026-07-01","localName":"Canada Day","name":"Canada Day"}]"""));

        Assert.Equal([new Holiday(new(2026, 7, 1), "Canada Day")], result);
    }

    [Theory]
    [InlineData("<!doctype html><html></html>", "This address gives a web page, not a calendar file")]
    [InlineData(" \r\n", "The address gave an empty answer: try again later.")]
    [InlineData("hello", "The file at this address is not a calendar of holidays the app can read")]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR", "The file at this address holds no holidays.")]
    [InlineData("""{"holidays":[]}""", "The file at this address holds no holidays.")]
    public void Parse_FileThatIsNotACalendar_ThrowsSayingWhatItIs(string body, string message)
    {
        var error = Assert.Throws<HolidaysFileException>(() => Holidays.Parse(Utf8(body)));

        Assert.StartsWith(message, error.Message);
    }

    [Fact]
    public void Addresses_AddressWithYear_ListsThisYearTheTenBeforeAndTheNext()
    {
        var result = Holidays.Addresses("https://example.test/{year}.ics", 2026);

        Assert.Equal(12, result.Count);
        Assert.Equal(("https://example.test/2016.ics", "https://example.test/2027.ics"), (result[0], result[^1]));
    }

    [Fact]
    public void Addresses_AddressWithoutYear_IsTheOneAddress()
    {
        var result = Holidays.Addresses("https://example.test/all.ics", 2026);

        Assert.Equal(["https://example.test/all.ics"], result);
    }

    [Theory]
    [InlineData("https://canada-holidays.ca/api/v1/provinces/QC?year=2026", "https://canada-holidays.ca/api/v1/provinces/QC?year={year}")]
    [InlineData("https://example.test/2025/holidays.json", "https://example.test/{year}/holidays.json")]
    [InlineData("https://example.test/all.ics", "https://example.test/all.ics")]
    [InlineData("https://example.test/2020-2030.ics", "https://example.test/2020-2030.ics")]
    [InlineData("https://example.test/id/120261", "https://example.test/id/120261")]
    public void YearTemplate_PastedLink_MarksItsOneYearOrKeepsItAsItIs(string pasted, string template)
    {
        var result = Holidays.YearTemplate(pasted);

        Assert.Equal(template, result);
    }

    [Fact]
    public void ParseWeekend_NothingChosen_ReturnsTheDefaultWeekend() => Assert.Equal(Holidays.DefaultWeekend, Holidays.ParseWeekend(null));

    [Fact]
    public void ParseWeekend_EmptyChoice_ReturnsNoWeekend() => Assert.Empty(Holidays.ParseWeekend(""));

    [Theory]
    [InlineData("5,6,7", new[] { DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })]
    [InlineData("1, 9, x", new[] { DayOfWeek.Monday })]
    public void ParseWeekend_ChosenDays_KeepsTheValidOnes(string setting, DayOfWeek[] days)
    {
        var result = Holidays.ParseWeekend(setting);

        Assert.Equal(days.ToHashSet(), result);
    }

    [Theory]
    [InlineData("6,7", 23)]
    [InlineData("5,6,7", 19)]
    [InlineData("", 31)]
    [InlineData("1,2,3,4,5,6,7", 0)]
    public void WorkingDays_DecemberWithAWeekend_CountsTheOtherDays(string weekend, int days)
    {
        var result = Holidays.WorkingDays(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), Holidays.ParseWeekend(weekend));

        Assert.Equal(days, result);
    }

    [Fact]
    public void Weekdays_SaturdayAndSunday_IsZero() => Assert.Equal(0, Holidays.Weekdays(new(2026, 11, 7), new(2026, 11, 8)));
}
