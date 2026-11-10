using System.Globalization;
using System.Net;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Storage;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Definitions > Holidays: calendars of public holidays downloaded from their address (ICS or JSON, one file per year
/// with "{year}"), a province's under its country's federal calendar, two copies at most (the previous download and
/// the latest), several checked at once, downloaded about once a month; their holidays and the weekend days chosen
/// are not business days; the net salary per hour and per year follows from them.
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

    /// <summary>The day off given for a holiday on a weekend: the Monday after it.</summary>
    private static string Off(DateOnly d) => d.AddDays(d.DayOfWeek switch { DayOfWeek.Saturday => 2, DayOfWeek.Sunday => 1, _ => 0 }).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Item(int year, int month, int day, string name)
    {
        var d = new DateOnly(year, month, day);
        return $$"""{"date":"{{d:yyyy-MM-dd}}","nameEn":"{{name}}","observedDate":"{{Off(d)}}"}""";
    }

    /// <summary>One year of Québec's holidays in the layout of canada-holidays.ca's API (a few of them).</summary>
    private static string QuebecJson(int y) =>
        """{"province":{"id":"QC","holidays":[""" + string.Join(",", Item(y, 1, 1, "New Year’s Day"), Item(y, 6, 24, "Saint-Jean-Baptiste Day"), Item(y, 12, 25, "Christmas Day")) + "]}}";

    /// <summary>One year of Canada's federal holidays in the same layout (a few of them; each lists its provinces).</summary>
    private static string FederalJson(int y) =>
        """{"holidays":[""" + string.Join(",", Item(y, 1, 1, "New Year’s Day"), Item(y, 11, 11, "Remembrance Day"), Item(y, 12, 25, "Christmas Day"), Item(y, 12, 26, "Boxing Day")) + "]}";

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

    private static readonly DateOnly Downloaded = new(2026, 11, 10);

    [Fact]
    public void Four_calendars_to_start_with_Quebec_under_Canada_both_checked()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        Assert.Equal(["Canada (Federal)", "Québec", "United States (Federal)", "France"], h.Calendars().Select(c => c.Location));
        Assert.Equal([null, "ca", null, null], h.Calendars().Select(c => c.Parent));
        Assert.Equal([true, true, false, false], h.Calendars().Select(c => c.Active));
        Assert.Equal("https://canada-holidays.ca/api/v1/holidays?federal=true&year={year}", h.Calendars()[0].Url);
        Assert.Equal("https://canada-holidays.ca/api/v1/provinces/QC?year={year}", h.Calendars()[1].Url);
        Assert.StartsWith("https://www.opm.gov/", h.Calendars()[2].Url);
        Assert.StartsWith("https://calendrier.api.gouv.fr/", h.Calendars()[3].Url);
        // Nothing downloaded yet: nothing said about their holidays.
        Assert.All(h.Views(), v => Assert.Null(v.PerYear));
    }

    private static void Raw(string path, string sql)
    {
        using var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_database_made_before_gets_Canada_with_Quebec_under_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "finances-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.db");
        try
        {
            // Made before the holidays.
            new Database(path).Dispose();
            Raw(path, "DROP TABLE holiday_calendars; DROP TABLE IF EXISTS holiday_sets; PRAGMA user_version = 15;");
            using (var db = new Database(path))
                Assert.Equal(["ca", "qc", "us", "fr"], new Holidays(db).Calendars().Select(c => c.Id));
            // Made with the three calendars of version 16, Quebec downloaded from its old address, France renamed.
            Raw(path, """
                DROP TABLE holiday_calendars; DROP TABLE IF EXISTS holiday_sets;
                CREATE TABLE holiday_sets (calendar TEXT NOT NULL, slot INTEGER NOT NULL, fetched TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY (calendar, slot));
                CREATE TABLE holiday_calendars (id TEXT PRIMARY KEY, location TEXT NOT NULL, url TEXT NOT NULL, active INTEGER NOT NULL, position INTEGER NOT NULL);
                INSERT INTO holiday_calendars VALUES ('qc', 'Canada - Quebec', 'https://canada-holidays.ca/ics/QC/{year}', 1, 0),
                    ('us', 'United States', 'https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/holidays.ics', 0, 1),
                    ('fr', 'France métropolitaine', 'https://calendrier.api.gouv.fr/jours-feries/metropole.json', 0, 2);
                INSERT INTO holiday_sets VALUES ('qc', 0, '2026-11-10', '[["2026-12-25","Christmas Day"]]');
                PRAGMA user_version = 16;
                """);
            using (var db = new Database(path))
            {
                var h = new Holidays(db);
                Assert.Equal(["Canada (Federal)", "Québec", "United States (Federal)", "France métropolitaine"], h.Calendars().Select(c => c.Location));
                Assert.Equal([true, true, false, false], h.Calendars().Select(c => c.Active));
                Assert.Equal(("ca", HolidayCalendar.Quebec.Url), (h.Calendars()[1].Parent, h.Calendars()[1].Url));
                // Its old copy is dropped (the copies are files now): downloaded again from the new address.
                Assert.Empty(h.Copies(h.Find("qc")!));
                Assert.Null(db.GetSetting("holidays.qc.updatedAt"));
            }
            using (var db = new Database(path))
                Assert.Equal(4, new Holidays(db).Calendars().Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Calendars_are_read_from_ICS_and_JSON_files_with_the_days_off_of_weekend_holidays()
    {
        // canada-holidays.ca: Christmas 2027 is a Saturday, the day off is Monday the 27th: both kept, the second as observed.
        var qc = Holidays.Parse(Utf8(QuebecJson(2027)));
        Assert.Equal([new Holiday(new(2027, 1, 1), "New Year’s Day"), new Holiday(new(2027, 6, 24), "Saint-Jean-Baptiste Day"),
            new Holiday(new(2027, 12, 25), "Christmas Day"), new Holiday(new(2027, 12, 27), "Christmas Day", Observed: true)], qc);
        // The federal list: Boxing Day 2026 (a Saturday) given on Monday the 28th; the provinces of each holiday are not holidays.
        var ca = Holidays.Parse(Utf8(FederalJson(2026)));
        Assert.Contains(new Holiday(new(2026, 12, 28), "Boxing Day", Observed: true), ca);
        Assert.Equal(5, ca.Count);
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
        // Lists of objects with a date and a name (Nager.Date).
        Assert.Equal([new Holiday(new(2026, 7, 1), "Canada Day")], Holidays.Parse(Utf8("""[{"date":"2026-07-01","localName":"Canada Day","name":"Canada Day"}]""")));
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
    public void The_year_of_a_pasted_link_is_found_by_the_app()
    {
        // Pasted as given by the site: the one year it names is the year to download, every year.
        Assert.Equal("https://canada-holidays.ca/api/v1/provinces/QC?year={year}", Holidays.YearTemplate("https://canada-holidays.ca/api/v1/provinces/QC?year=2026"));
        Assert.Equal("https://example.test/{year}/holidays.json", Holidays.YearTemplate("https://example.test/2025/holidays.json"));
        // No year, two years, or a longer number: kept as it is.
        Assert.Equal("https://example.test/all.ics", Holidays.YearTemplate("https://example.test/all.ics"));
        Assert.Equal("https://example.test/2020-2030.ics", Holidays.YearTemplate("https://example.test/2020-2030.ics"));
        Assert.Equal("https://example.test/id/120261", Holidays.YearTemplate("https://example.test/id/120261"));
        // Saved from the table: kept with the year mark, downloaded for every year.
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var saved = h.SaveCalendar(null, "Ontario", "https://canada-holidays.ca/api/v1/provinces/ON?year=2026", "ca");
        Assert.Equal("https://canada-holidays.ca/api/v1/provinces/ON?year={year}", saved.Url);
        Assert.Equal(12, Holidays.Addresses(saved.Url, 2026).Count);
    }

    [Fact]
    public void Weekend_days_are_the_ones_chosen()
    {
        Assert.Equal(Holidays.DefaultWeekend, Holidays.ParseWeekend(null));
        Assert.Empty(Holidays.ParseWeekend(""));
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }, Holidays.ParseWeekend("5,6,7"));
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Monday }, Holidays.ParseWeekend("1, 9, x"));
        // December 2026: 31 days; 23 of them Monday to Friday, 19 Monday to Thursday, 31 without a weekend.
        var (from, to) = (new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(23, Holidays.WorkingDays(from, to, Holidays.ParseWeekend("6,7")));
        Assert.Equal(19, Holidays.WorkingDays(from, to, Holidays.ParseWeekend("5,6,7")));
        Assert.Equal(31, Holidays.WorkingDays(from, to, Holidays.ParseWeekend("")));
        Assert.Equal(0, Holidays.WorkingDays(from, to, Holidays.ParseWeekend("1,2,3,4,5,6,7")));
        Assert.Equal(0, Holidays.Weekdays(new(2026, 11, 7), new(2026, 11, 8)));
    }

    [Fact]
    public void Business_days_are_the_days_not_weekend_less_the_holidays_of_every_checked_calendar()
    {
        using var s = new TempStore();
        var weekend = "6,7";
        var h = new Holidays(s.Db, () => weekend);
        h.Save("ca", Utf8(FederalJson(2026)), Downloaded);
        h.Save("qc", Utf8(QuebecJson(2026)), Downloaded);
        h.Save("us", Utf8(OpmIcs), Downloaded);
        h.Save("fr", Utf8(FranceJson), Downloaded);
        var (from, to) = (new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));
        // Canada and Québec (checked to start with): 23 days Monday to Friday, less Christmas (Friday, in both: once) and
        // Monday the 28th, the day off for Boxing Day (Saturday the 26th, not a business day anyway).
        Assert.Equal(21, h.BusinessDays(from, to));
        // Friday to Sunday as the weekend: 19 days Monday to Thursday, less Monday the 28th.
        weekend = "5,6,7";
        Assert.Equal(18, h.BusinessDays(from, to));
        // No weekend: 31 days less the 25th, the 26th and the 28th.
        weekend = "";
        Assert.Equal(28, h.BusinessDays(from, to));
        weekend = "6,7";
        // France too: no other holiday in December on a business day.
        h.SetActive("fr", true);
        Assert.Equal(21, h.BusinessDays(from, to));
        // What the calendar shows: every checked calendar's holidays day by day, with its place.
        Assert.Equal([("2026-12-25", "Christmas Day", "Canada (Federal)", false), ("2026-12-25", "Christmas Day", "Québec", false),
            ("2026-12-25", "Jour de Noël", "France", false), ("2026-12-26", "Boxing Day", "Canada (Federal)", false), ("2026-12-28", "Boxing Day", "Canada (Federal)", true)],
            h.List(from, to).Select(x => (x.Day, x.Name, x.Location, x.Observed)));
        // November: Remembrance Day (Wednesday 11, federal; also France's 11 novembre: once); with the United States, Thanksgiving (Thursday 26).
        var (nf, nt) = (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(20, h.BusinessDays(nf, nt));
        h.SetActive("us", true);
        Assert.Equal(19, h.BusinessDays(nf, nt));
        // Every calendar unchecked: only the weekend is left out.
        h.SetActive("us", false);
        h.SetActive("fr", false);
        h.SetActive("ca", false);
        h.SetActive("qc", false);
        Assert.Equal(23, h.BusinessDays(from, to));
        Assert.Empty(h.List(from, to));
    }

    [Fact]
    public void A_province_sits_under_its_country_each_checked_on_its_own()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        bool Active(string id) => h.Find(id)!.Active;
        // Canada alone: Québec unchecked, Canada stays checked.
        h.SetActive("qc", false);
        Assert.Equal((true, false), (Active("ca"), Active("qc")));
        // Québec alone: checking it does not check Canada; unchecking Canada does not uncheck Québec.
        h.SetActive("ca", false);
        h.SetActive("qc", true);
        Assert.Equal((false, true), (Active("ca"), Active("qc")));
        h.SetActive("ca", true);
        h.SetActive("ca", false);
        Assert.Equal((false, true), (Active("ca"), Active("qc")));
        // Another province under Canada, after Québec; nothing under a province.
        var on = h.SaveCalendar(null, "Ontario", "https://example.test/on/{year}.json", "ca");
        Assert.Equal(["ca", "qc", on.Id, "us", "fr"], h.Calendars().Select(c => c.Id));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Montréal", "https://example.test/mtl.ics", "qc"));
        // Edited: moved under the United States, then under no country; a country with provinces stays a country.
        h.SaveCalendar(on.Id, "Ontario", on.Url, "us");
        Assert.Equal(["ca", "qc", "us", on.Id, "fr"], h.Calendars().Select(c => c.Id));
        h.SaveCalendar(on.Id, "Ontario", on.Url, null);
        Assert.Null(h.Find(on.Id)!.Parent);
        Assert.Throws<ArgumentException>(() => h.SaveCalendar("ca", "Canada (Federal)", HolidayCalendar.Canada.Url, "us"));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar("fr", "France", HolidayCalendar.France.Url, "fr"));
        // Deleting Canada deletes the provinces under it, with their holidays.
        h.Save("qc", Utf8(QuebecJson(2026)), Downloaded);
        Assert.True(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "quebec")));
        h.RemoveCalendar("ca");
        Assert.Equal(["us", "fr", on.Id], h.Calendars().Select(c => c.Id));
        Assert.False(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "quebec")));
    }

    [Fact]
    public void The_total_of_holidays_counts_each_day_once_across_federal_provincial_and_countries()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        h.Save("ca", Utf8(FederalJson(2026)), Downloaded);
        h.Save("qc", Utf8(QuebecJson(2026)), Downloaded);
        h.Save("fr", Utf8(FranceJson), Downloaded);
        // Canada: 1 January, 11 November, 25 and 26 December (the day off on Monday the 28th is not another holiday).
        h.SetActive("qc", false);
        Assert.Equal(4, h.TotalHolidays(2026));
        // Québec too: only Saint-Jean-Baptiste is new (1 January and Christmas are in both): 5, not 4 + 3.
        h.SetActive("qc", true);
        Assert.Equal(5, h.TotalHolidays(2026));
        // France too: Toussaint is new; 11 November and Christmas are already counted: 6, not 5 + 3.
        h.SetActive("fr", true);
        Assert.Equal(6, h.TotalHolidays(2026));
        // Québec alone.
        h.SetActive("ca", false);
        h.SetActive("fr", false);
        Assert.Equal(3, h.TotalHolidays(2026));
        Assert.Equal(0, h.TotalHolidays(2030));
    }

    [Fact]
    public void A_period_says_its_business_days()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        h.Save("qc", Utf8(QuebecJson(2026)), Downloaded);
        var june = s.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(s.Db), h);
        // June 2026: 22 days Monday to Friday, less Saint-Jean-Baptiste (Wednesday 24).
        Assert.Equal(21, june.BusinessDays);
        // A year, and any range: the same count.
        var year = s.Summaries.Report(new(2026, 1, 1), new(2026, 12, 31), Granularity.Year, "CAD", new ExchangeRates(s.Db), h);
        Assert.Equal(Holidays.Weekdays(new(2026, 1, 1), new(2026, 12, 31)) - 3, year.BusinessDays);
        Assert.Null(s.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(s.Db)).BusinessDays);
    }

    [Fact]
    public void The_net_salary_per_hour_and_per_year_from_the_business_days_in_the_display_currency()
    {
        using var s = new TempStore();
        var weekend = "6,7";
        var h = new Holidays(s.Db, () => weekend);
        h.Save("ca", Utf8(FederalJson(2026)), Downloaded);
        h.Save("qc", Utf8(QuebecJson(2026)), Downloaded);
        var bank = s.Db.AddBank(new Bank { Name = "Banque Exemple", System = BankingSystem.NorthAmerica, Currency = "CAD" });
        var account = s.Db.AddAccount(new Account { Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank, BankId = bank });
        s.Transactions.Create(new TransactionInput("2026-12-04", 2000m, "Employer", "salary", account, null));
        s.Transactions.Create(new TransactionInput("2026-12-18", 2000m, "Employer", "salary", account, null));
        s.Transactions.Create(new TransactionInput("2026-12-10", 150m, "Refund", "otherincome", account, null));
        s.Transactions.Create(new TransactionInput("2026-12-11", -80m, "Market", "groceries", account, null));
        var rates = new ExchangeRates(s.Db, () => "ecb");
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,\n2026-12-01,1.5000,"));
        SalaryRate Salary(string currency) => s.Summaries.Report(new(2026, 12, 1), new(2026, 12, 31), Granularity.Month, currency, rates, h).Salary!;

        // 4,000 of salary (not the refund) over December's 21 business days of 8 hours: 4,000 / 168 = 23.81 an hour.
        var dec = Salary("CAD");
        Assert.Equal((4000m, 21, 8m, 23.81m), (dec.Net, dec.BusinessDays, dec.HoursPerDay, dec.PerHour));
        // 2026: 261 days Monday to Friday, less New Year's Day, Saint-Jean-Baptiste, Remembrance Day, Christmas and the
        // day off for Boxing Day: 256. 4,000 / 21 x 256 = 48,761.90 a year.
        Assert.Equal((256m, 48761.90m), (dec.YearBusinessDays, dec.PerYear));
        // In euros: every salary converted at its day's rate (1,333.33 each), the same days.
        var eur = Salary("EUR");
        Assert.Equal((2666.66m, 15.87m), (eur.Net, eur.PerHour));
        // A four-day week (Friday to Sunday off): 40 hours over 4 days, 10 a day; December's 18 business days.
        weekend = "5,6,7";
        var four = Salary("CAD");
        Assert.Equal((18, 10m, 22.22m), (four.BusinessDays, four.HoursPerDay, four.PerHour));
        // No salary in a period: nothing said.
        Assert.Null(s.Summaries.Report(new(2026, 11, 1), new(2026, 11, 30), Granularity.Month, "CAD", rates, h).Salary);
    }

    [Fact]
    public void Two_copies_at_most_the_previous_download_and_the_latest_as_files()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var qc = h.Find("qc")!;
        var day1 = new DateOnly(2026, 11, 8);
        List<string> Days() => [.. h.Copies(qc).Select(c => c.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
        byte[] One(int m, string name) => Utf8($$"""{"2026-{{m:00}}-01": "{{name}}"}""");

        // Download n: one copy.
        h.Save("qc", One(1, "a"), day1);
        Assert.Equal(["2026-11-08"], Days());
        Assert.Equal(["holidays.json"], h.Copies(qc)[0].Files);
        // Downloaded again the same day: the latest copy is replaced, still one.
        h.Save("qc", One(1, "b"), day1);
        Assert.Equal(["2026-11-08"], Days());
        Assert.Equal("b", h.Of("qc").Single().Name);
        // Another day: the previous download (n-1) and the latest (n).
        h.Save("qc", One(2, "c"), day1.AddDays(30));
        Assert.Equal(["2026-12-08", "2026-11-08"], Days());
        // Again the same day: n is replaced, n-1 stays.
        h.Save("qc", One(2, "d"), day1.AddDays(30));
        Assert.Equal(["2026-12-08", "2026-11-08"], Days());
        // A new download: n becomes n-1, the older copy is deleted. Never more than two.
        h.Save("qc", One(3, "e"), day1.AddDays(60));
        Assert.Equal(["2027-01-07", "2026-12-08"], Days());
        Assert.Equal("e", h.Of("qc").Single().Name);
        Assert.False(h.FromPrevious("qc"));
        // A latest copy that cannot be read: the previous one is used.
        File.WriteAllText(Path.Combine(h.Copies(qc)[0].Folder, "holidays.json"), "garbage");
        h.Reload();
        Assert.Equal("d", h.Of("qc").Single().Name);
        Assert.True(h.FromPrevious("qc"));
        // A download stopped in the middle (its folder still partial) is never a copy, and is cleared by the next one.
        Directory.CreateDirectory(Path.Combine(s.Db.DownloadsFolder, "holidays", "quebec", "2027-02-01.partial"));
        h.Reload();
        Assert.Equal(["2027-01-07", "2026-12-08"], Days());
        h.Save("qc", One(4, "f"), day1.AddDays(90));
        Assert.False(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "quebec", "2027-02-01.partial")));
        Assert.Equal(["2027-02-06", "2027-01-07"], Days());
        // A file that gives no holidays is never kept.
        Assert.Throws<HolidaysFileException>(() => h.Save("qc", Utf8("<html></html>"), day1.AddDays(91)));
        Assert.Equal(["2027-02-06", "2027-01-07"], Days());
    }

    [Fact]
    public void Folders_and_files_have_plain_standard_names()
    {
        Assert.Equal("canada-federal", DownloadStore.Slug("Canada (Federal)"));
        Assert.Equal("quebec", DownloadStore.Slug("Québec"));
        Assert.Equal("united-states-federal", DownloadStore.Slug("United States (Federal)"));
        Assert.Equal("us-federal-reserve", DownloadStore.Slug("US Federal Reserve"));
        Assert.Equal("ile-de-france", DownloadStore.Slug("  Île-de-France! "));
        Assert.Equal("source", DownloadStore.Slug("???"));
        Assert.Equal("rates.zip", DownloadStore.FileName("rates", null, [0x50, 0x4B, 3, 4]));
        Assert.Equal("holidays-2026.json", DownloadStore.FileName("holidays", 2026, Utf8("{}")));
        Assert.Equal("holidays.ics", DownloadStore.FileName("holidays", null, Utf8("BEGIN:VCALENDAR")));
        Assert.Equal("rates.csv", DownloadStore.FileName("rates", null, Utf8("Date,USD")));
    }

    [Fact]
    public void A_calendar_is_downloaded_again_about_once_a_month()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var fr = h.Find("fr")!;
        Assert.True(h.Due(fr));
        void LastDownload(int daysAgo) => s.Db.SetSetting("holidays.fr.updatedAt", DateTime.Today.AddDays(-daysAgo).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        LastDownload(0);
        Assert.False(h.Due(fr));
        LastDownload(29);
        Assert.False(h.Due(fr));
        LastDownload(Holidays.RefreshDays);
        Assert.True(h.Due(fr));
    }

    [Fact]
    public async Task Refreshing_downloads_every_calendar_when_due_or_on_demand()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        var year = DateTime.Today.Year;
        var web = new Web(u =>
        {
            if (u.Host != "canada-holidays.ca") return (HttpStatusCode.OK, u.Host == "www.opm.gov" ? OpmIcs : FranceJson);
            // One file per year; the oldest years are refused (400): left out.
            var y = int.Parse(u.Query.Split("year=")[1], CultureInfo.InvariantCulture);
            if (y < year - 2) return (HttpStatusCode.BadRequest, """{"error":{"status":400}}""");
            return (HttpStatusCode.OK, u.AbsolutePath.Contains("/provinces/QC") ? QuebecJson(y) : FederalJson(y));
        });
        using var client = new HttpClient(web);
        // Canada and Québec are checked: their new holidays change the business days.
        Assert.True(await h.UpdateAllAsync(force: false, client));
        Assert.Equal(12 + 12 + 1 + 1, web.Calls);
        Assert.Contains($"https://canada-holidays.ca/api/v1/provinces/QC?year={year - 10}", web.Asked);
        // Every calendar has its holidays, counted for this year (a day off for a weekend holiday is not another one).
        var views = h.Views();
        Assert.All(views, v => Assert.Null(v.Error));
        Assert.Equal((4, 3), (views[0].PerYear, views[1].PerYear));
        Assert.Equal(year, views[0].Year);
        // Québec: the four years there, three holidays each, and the days off of the weekend ones.
        Assert.Equal(12, h.Of("qc").Count(x => !x.Observed));
        // Downloaded today: nothing is due; the Refresh button downloads them all again anyway.
        Assert.False(await h.UpdateAllAsync(force: false, client));
        Assert.Equal(26, web.Calls);
        Assert.False(await h.UpdateAllAsync(force: true, client));
        Assert.Equal(52, web.Calls);
        Assert.All(h.Calendars(), c => Assert.Single(h.Copies(c)));
        // Each calendar's files as downloaded: one per year, named by its year; or one file.
        var qcCopy = h.Copies(h.Find("qc")!)[0];
        Assert.Equal(Path.Combine(s.Db.DownloadsFolder, "holidays", "quebec", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), qcCopy.Folder);
        Assert.Equal(Enumerable.Range(year - 2, 4).Select(y => $"holidays-{y}.json"), qcCopy.Files);
        Assert.Equal(["holidays.ics"], h.Copies(h.Find("us")!)[0].Files);
        Assert.Equal(["holidays.json"], h.Copies(h.Find("fr")!)[0].Files);
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
        Assert.Null(await Check("fr", HttpStatusCode.OK, FranceJson));
    }

    [Fact]
    public void A_brand_is_renamed_everywhere()
    {
        using var s = new TempStore();
        var bank = s.Db.AddBank(new Bank { Name = "Banque Exemple", System = BankingSystem.NorthAmerica, Currency = "CAD" });
        var account = s.Db.AddAccount(new Account { Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank, BankId = bank });
        s.Transactions.Create(new TransactionInput("2026-12-04", -20m, "Cafe Lune", "restaurants", account, null));
        s.Transactions.Create(new TransactionInput("2026-12-05", -12m, "cafe lune", "restaurants", account, null));
        s.Transactions.Create(new TransactionInput("2026-12-06", -30m, "Marché Vert", "groceries", account, null));
        s.Transactions.SetBrandCategory("Cafe Lune", "groceries");
        Assert.Equal(2, s.Transactions.RenameBrand("Cafe Lune", "Café de la Lune"));
        var all = s.Db.AllTransactions();
        Assert.Equal(["Café de la Lune", "Café de la Lune", "Marché Vert"], all.Select(t => t.Brand).Order());
        // What was learned for it goes with it.
        Assert.All(all.Where(t => t.Brand == "Café de la Lune"), t => Assert.Equal("groceries", t.CategoryId));
        Assert.Throws<ArgumentException>(() => s.Transactions.RenameBrand("Marché Vert", " "));
    }

    [Fact]
    public void Calendars_are_added_changed_and_deleted()
    {
        using var s = new TempStore();
        var h = new Holidays(s.Db);
        // A new line: any place, any https address; not checked yet.
        var be = h.SaveCalendar(null, "  Belgium ", " https://example.test/be.ics ");
        Assert.Equal(("Belgium", "https://example.test/be.ics", false, (string?)null), (be.Location, be.Url, be.Active, be.Parent));
        Assert.Equal(5, new Holidays(s.Db).Calendars().Count);
        Assert.Equal("Belgium is already in the table.", Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "belgium", "https://example.test/x.ics")).Message);
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, " ", "https://example.test/x.ics"));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Spain", "http://example.test/x.ics"));
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Spain", "x.ics"));
        h.SaveCalendar(null, "Spain", "https://example.test/{year}/es.ics");
        // The place renamed: its holidays stay; a new address drops them.
        h.Save(be.Id, Utf8("""{"2026-07-21": "Fête nationale"}"""), Downloaded);
        Assert.True(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "belgium")));
        // Renamed: its files follow its name.
        h.SaveCalendar(be.Id, "Belgique", be.Url);
        Assert.Single(h.Of(be.Id));
        Assert.False(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "belgium")));
        Assert.True(Directory.Exists(Path.Combine(s.Db.DownloadsFolder, "holidays", "belgique")));
        h.SaveCalendar(be.Id, "Belgique", "https://example.test/other.ics");
        Assert.Empty(h.Of(be.Id));
        Assert.Empty(h.Copies(h.Find(be.Id)!));
        // Two names giving one folder (accents aside): one place.
        Assert.Throws<ArgumentException>(() => h.SaveCalendar(null, "Quebec", "https://example.test/q.ics"));
        Assert.Equal(["Canada (Federal)", "Québec", "United States (Federal)", "France", "Belgique", "Spain"], h.Calendars().Select(c => c.Location));
    }
}
