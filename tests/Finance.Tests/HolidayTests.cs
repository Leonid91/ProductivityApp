using System.Globalization;
using System.Net;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Definitions > Holidays: calendars of public holidays downloaded from their address (ICS or JSON, one file per year
/// with "{year}"), two copies at most (the day before and today), several checked at once; their holidays are not
/// business days.
/// </summary>
public class HolidayTests
{
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>Answers each address with what <paramref name="answer"/> gives for it (status and body).</summary>
    private sealed class Web(Func<Uri, (HttpStatusCode Code, string Body)> answer, Exception? fault = null) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Asked) Asked.Add(request.RequestUri!.ToString());
            if (fault is not null) throw fault;
            var (code, body) = answer(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(Utf8(body)) });
        }
    }

    /// <summary>One year of a province's calendar in the layout of canada-holidays.ca (ICS, a few of its holidays).</summary>
    private static string QuebecIcs(int year) => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//pcraig3//hols//EN
        BEGIN:VEVENT
        UID:a{year}@hols.ca
        SUMMARY:New Year’s Day
        DTSTART;VALUE=DATE:{year}0101
        DTEND;VALUE=DATE:{year}0102
        END:VEVENT
        BEGIN:VEVENT
        UID:b{year}@hols.ca
        SUMMARY:Saint-Jean-Baptiste Day
        DTSTART;VALUE=DATE:{year}0624
        DTEND;VALUE=DATE:{year}0625
        END:VEVENT
        BEGIN:VEVENT
        UID:c{year}@hols.ca
        SUMMARY:Christmas Day
        DTSTART;VALUE=DATE:{year}1225
        DTEND;VALUE=DATE:{year}1226
        END:VEVENT
        END:VCALENDAR
        """;

    /// <summary>The layout of the Office of Personnel Management's file: folded lines, escaped commas, no DTEND.</summary>
    private const string OpmIcs = """
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
    private const string FranceJson = """{"2026-11-11": "11 novembre", "2026-12-25": "Jour de Noël", "2026-11-01": "Toussaint", "2025-12-25": "Jour de Noël"}""";

    [Fact]
    public void Three_calendars_to_start_with_Quebec_checked()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        Assert.Equal(["Canada - Quebec", "United States", "France"], h.Calendars().Select(c => c.Location));
        Assert.Equal([true, false, false], h.Calendars().Select(c => c.Active));
        Assert.Equal("https://canada-holidays.ca/ics/QC/{year}", h.Calendars()[0].Url);
        Assert.StartsWith("https://www.opm.gov/", h.Calendars()[1].Url);
        Assert.StartsWith("https://calendrier.api.gouv.fr/", h.Calendars()[2].Url);
        // Nothing downloaded yet: nothing said about their holidays.
        Assert.All(h.Views(), v => Assert.Null(v.PerYear));
    }

    [Fact]
    public void A_database_made_before_gets_the_three_calendars()
    {
        var dir = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.db");
        try
        {
            new Database(path).Dispose();
            using (var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                cn.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "DROP TABLE holiday_calendars; DROP TABLE holiday_sets; PRAGMA user_version = 15;";
                cmd.ExecuteNonQuery();
            }
            using (var db = new Database(path))
                Assert.Equal(["qc", "us", "fr"], new Holidays(db).Calendars().Select(c => c.Id));
            using (var db = new Database(path))
                Assert.Equal(3, new Holidays(db).Calendars().Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Calendars_are_read_from_ICS_and_JSON_files()
    {
        var qc = Holidays.Parse(Utf8(QuebecIcs(2026)));
        Assert.Equal([new Holiday(new(2026, 1, 1), "New Year’s Day"), new Holiday(new(2026, 6, 24), "Saint-Jean-Baptiste Day"), new Holiday(new(2026, 12, 25), "Christmas Day")], qc);
        // Folded lines joined, escaped commas read, cancelled events left out.
        var us = Holidays.Parse(Utf8(OpmIcs));
        Assert.Equal(["Birthday of Martin Luther King, Jr.", "Thanksgiving Day", "Christmas Day"], us.Select(x => x.Name));
        // An event over several days gives each day (DTEND excluded).
        var long_ = Holidays.Parse(Utf8("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261224\r\nDTEND;VALUE=DATE:20261227\r\nSUMMARY:Holidays\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));
        Assert.Equal([new DateOnly(2026, 12, 24), new(2026, 12, 25), new(2026, 12, 26)], long_.Select(x => x.Day));
        // France's service: an object of days.
        var fr = Holidays.Parse(Utf8(FranceJson));
        Assert.Equal(4, fr.Count);
        Assert.Contains(new Holiday(new(2026, 11, 11), "11 novembre"), fr);
        // Lists of objects with a date and a name (Nager.Date); the observed day preferred (canada-holidays.ca's API).
        var nager = Holidays.Parse(Utf8("""[{"date":"2026-07-01","localName":"Canada Day","name":"Canada Day"}]"""));
        Assert.Equal([new Holiday(new(2026, 7, 1), "Canada Day")], nager);
        var observed = Holidays.Parse(Utf8("""{"province":{"id":"QC","holidays":[{"date":"2027-12-25","nameEn":"Christmas Day","observedDate":"2027-12-27","provinces":[{"id":"QC"}]}]}}"""));
        Assert.Equal([new Holiday(new(2027, 12, 27), "Christmas Day")], observed);
    }

    [Fact]
    public void What_is_not_a_calendar_is_said_plainly()
    {
        string Error(string body) => Assert.Throws<HolidaysFileException>(() => Holidays.Parse(Utf8(body))).Message;
        Assert.StartsWith("This address gives a web page, not a calendar file", Error("<!doctype html><html></html>"));
        Assert.Equal("The address gave an empty answer: try again later.", Error(" \r\n"));
        Assert.StartsWith("The file at this address is not a calendar of holidays the app can read", Error("hello"));
        Assert.Equal("The file at this address holds no holidays.", Error("BEGIN:VCALENDAR\r\nEND:VCALENDAR"));
        Assert.Equal("The file at this address holds no holidays.", Error("""{"holidays":[]}"""));
    }

    [Fact]
    public void An_address_with_year_is_downloaded_for_this_year_the_ten_before_and_the_next()
    {
        var years = Holidays.Addresses("https://example.test/{year}.ics", 2026);
        Assert.Equal(12, years.Count);
        Assert.Equal(("https://example.test/2016.ics", "https://example.test/2027.ics"), (years[0], years[^1]));
        Assert.Equal(["https://example.test/all.ics"], Holidays.Addresses("https://example.test/all.ics", 2026));
    }

    [Fact]
    public void Monday_to_Friday_less_the_holidays_of_every_checked_calendar()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        // November 2026: 30 days, 21 of them Monday to Friday.
        var (from, to) = (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(21, Holidays.Weekdays(from, to));
        Assert.Equal(5, Holidays.Weekdays(new(2026, 11, 2), new(2026, 11, 8)));
        Assert.Equal(0, Holidays.Weekdays(new(2026, 11, 7), new(2026, 11, 8)));
        h.Save("qc", Holidays.Parse(Utf8(QuebecIcs(2026))), new DateOnly(2026, 11, 10));
        h.Save("us", Holidays.Parse(Utf8(OpmIcs)), new DateOnly(2026, 11, 10));
        h.Save("fr", Holidays.Parse(Utf8(FranceJson)), new DateOnly(2026, 11, 10));
        // Quebec alone (checked to start with): no holiday in November.
        Assert.Equal(21, h.BusinessDays(from, to));
        // The United States too: Thanksgiving (Thursday 26).
        h.SetActive("us", true);
        Assert.Equal(20, h.BusinessDays(from, to));
        // France too: 11 November (Wednesday); 1 November is a Sunday, not a business day anyway.
        h.SetActive("fr", true);
        Assert.Equal(19, h.BusinessDays(from, to));
        // Christmas, a holiday in the three: counted once.
        Assert.Equal(Holidays.Weekdays(new(2026, 12, 1), new(2026, 12, 31)) - 1, h.BusinessDays(new(2026, 12, 1), new(2026, 12, 31)));
        // What the calendar shows: every checked calendar's holidays, day by day, with its place.
        Assert.Equal([("2026-11-01", "Toussaint", "France"), ("2026-11-11", "11 novembre", "France"), ("2026-11-26", "Thanksgiving Day", "United States")],
            h.List(from, to).Select(x => (x.Day, x.Name, x.Location)));
        // Unchecked again: their holidays are business days again.
        h.SetActive("us", false);
        h.SetActive("fr", false);
        Assert.Equal(21, h.BusinessDays(from, to));
        Assert.Empty(h.List(from, to));
    }

    [Fact]
    public void A_period_says_its_business_days()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        h.Save("qc", Holidays.Parse(Utf8(QuebecIcs(2026))), new DateOnly(2026, 11, 10));
        var june = s.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(s.Db), h);
        // June 2026: 22 days Monday to Friday, less Saint-Jean-Baptiste (Wednesday 24).
        Assert.Equal(21, june.BusinessDays);
        Assert.Null(s.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(s.Db)).BusinessDays);
    }

    [Fact]
    public void Two_copies_at_most_the_day_before_and_today()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var day1 = new DateOnly(2026, 11, 8);
        List<string> Days() => [.. s.Db.HolidayCopies("qc").Select(c => c.Day)];
        Holiday[] One(int m, string name) => [new(new(2026, m, 1), name)];

        // Day n: one copy.
        h.Save("qc", One(1, "a"), day1);
        Assert.Equal(["2026-11-08"], Days());
        // Downloaded again the same day: today's copy is replaced, still one.
        h.Save("qc", One(1, "b"), day1);
        Assert.Equal(["2026-11-08"], Days());
        Assert.Equal("b", h.Of("qc").Single().Name);
        // The next day: the day before (n-1) and today (n).
        h.Save("qc", One(2, "c"), day1.AddDays(1));
        Assert.Equal(["2026-11-09", "2026-11-08"], Days());
        // Again the same day: n is replaced, n-1 stays.
        h.Save("qc", One(2, "d"), day1.AddDays(1));
        Assert.Equal(["2026-11-09", "2026-11-08"], Days());
        // A new day: n becomes n-1, the older copy is deleted. Never more than two.
        h.Save("qc", One(3, "e"), day1.AddDays(2));
        Assert.Equal(["2026-11-10", "2026-11-09"], Days());
        Assert.Null(s.Db.LoadHolidaySet("qc", 2));
        // Today's copy is the one used.
        Assert.Equal("e", h.Of("qc").Single().Name);
        // A copy of today that cannot be read: the day before's is used.
        s.Db.SaveHolidaySet("qc", "garbage", day1.AddDays(2));
        h.Reload();
        Assert.Equal("d", h.Of("qc").Single().Name);
        Assert.True(h.FromPrevious("qc"));
        Assert.Equal(["2026-11-10", "2026-11-09"], Days());
    }

    [Fact]
    public async Task Refreshing_downloads_every_calendar_once_a_day_or_on_demand()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var year = DateTime.Today.Year;
        var web = new Web(u => u.Host switch
        {
            // One file per year; the oldest years are not there (404): left out.
            "canada-holidays.ca" => int.Parse(u.Segments[^1], CultureInfo.InvariantCulture) is var y && y >= year - 2 ? (HttpStatusCode.OK, QuebecIcs(y)) : (HttpStatusCode.NotFound, ""),
            "www.opm.gov" => (HttpStatusCode.OK, OpmIcs),
            _ => (HttpStatusCode.OK, FranceJson),
        });
        using var client = new HttpClient(web);
        // Quebec is checked: its new holidays change the business days.
        Assert.True(await h.UpdateAllAsync(force: false, client));
        Assert.Equal(12 + 1 + 1, web.Calls);
        Assert.Contains($"https://canada-holidays.ca/ics/QC/{year - 10}", web.Asked);
        // Every calendar has its holidays, counted for this year, read from what was downloaded.
        var views = h.Views();
        Assert.All(views, v => Assert.Null(v.Error));
        Assert.Equal(3, views[0].PerYear);
        Assert.Equal(year == 2026 ? 3 : null, views[1].PerYear);
        Assert.Equal(year, views[0].Year);
        // Quebec: the four years there, three holidays each.
        Assert.Equal(12, h.Of("qc").Length);
        // The same day, nothing is due; the Refresh button downloads them all again anyway.
        Assert.False(await h.UpdateAllAsync(force: false, client));
        Assert.Equal(14, web.Calls);
        Assert.False(await h.UpdateAllAsync(force: true, client));
        Assert.Equal(28, web.Calls);
        Assert.All(h.Calendars(), c => Assert.Single(s.Db.HolidayCopies(c.Id)));
    }

    [Fact]
    public async Task A_wrong_address_or_anything_unexpected_is_said_cleanly_and_the_copies_stay()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        async Task<string?> Check(string id, HttpStatusCode code, string body, Exception? fault = null) =>
            (await h.UpdateAsync(id, new HttpClient(new Web(_ => (code, body), fault)))).Error;

        Assert.Null(await Check("fr", HttpStatusCode.OK, FranceJson));
        Assert.Equal("Nothing at this address (404): check it.", await Check("fr", HttpStatusCode.NotFound, ""));
        Assert.Equal("Nothing at this address (404): check it.", await Check("qc", HttpStatusCode.NotFound, ""));
        Assert.StartsWith("This address gives a web page, not a calendar file", await Check("fr", HttpStatusCode.OK, "<html></html>"));
        Assert.Equal("The address answered with an error (500 Internal Server Error).", await Check("fr", HttpStatusCode.InternalServerError, ""));
        Assert.Equal("The address could not be reached: check it, and the connection to the Internet.", await Check("fr", HttpStatusCode.OK, "", new HttpRequestException("no route")));
        Assert.Equal("The holidays could not be downloaded: disk full", await Check("fr", HttpStatusCode.OK, "", new InvalidOperationException("disk full")));
        // The holidays downloaded before stay in use.
        Assert.Equal(4, h.Of("fr").Length);
        // The good file again: the error is gone.
        Assert.Null(await Check("fr", HttpStatusCode.OK, FranceJson));
    }

    [Fact]
    public void Calendars_are_added_changed_and_deleted()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        // A new line: any place, any https address; not checked yet.
        var be = h.SaveCalendar(null, "  Belgium ", " https://example.test/be.ics ");
        Assert.Equal(("Belgium", "https://example.test/be.ics", false), (be.Location, be.Url, be.Active));
        Assert.Equal(4, new Holidays(s.Db).Calendars().Count);
        Assert.Equal("Belgium is already in the table.", Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "belgium", "https://example.test/x.ics")).Message);
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, " ", "https://example.test/x.ics"));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Spain", "http://example.test/x.ics"));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Spain", "x.ics"));
        // "{year}" in an address is allowed.
        h.SaveCalendar(null, "Spain", "https://example.test/{year}/es.ics");
        // The place renamed: its holidays stay; a new address drops them.
        h.Save(be.Id, [new Holiday(new(2026, 7, 21), "Fête nationale")], new DateOnly(2026, 11, 10));
        h.SaveCalendar(be.Id, "Belgique", be.Url);
        Assert.Single(h.Of(be.Id));
        h.SaveCalendar(be.Id, "Belgique", "https://example.test/other.ics");
        Assert.Empty(h.Of(be.Id));
        Assert.Empty(s.Db.HolidayCopies(be.Id));
        // Any calendar can be deleted, a checked one too, with its holidays.
        h.Save("qc", Holidays.Parse(Utf8(QuebecIcs(2026))), new DateOnly(2026, 11, 10));
        h.RemoveCalendar("qc");
        Assert.Null(s.Db.LoadHolidaySet("qc", 0));
        Assert.Equal(["United States", "France", "Belgique", "Spain"], h.Calendars().Select(c => c.Location));
        // Nothing checked: business days are Monday to Friday.
        Assert.Equal(22, h.BusinessDays(new(2026, 6, 1), new(2026, 6, 30)));
    }
}
