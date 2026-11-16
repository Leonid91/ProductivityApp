using System.Globalization;
using System.Net;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Calendars of public holidays saved in the database and as downloaded files: the calendars to start with, a
/// province under its country, two copies at most, downloads when due, and the business days and salary they give.
/// </summary>
public sealed class HolidaysTests : IDisposable
{
    private static readonly DateOnly Downloaded = new(2026, 11, 10);
    private static readonly (DateOnly From, DateOnly To) December = (new(2026, 12, 1), new(2026, 12, 31));

    private readonly TestStore _store = new();
    private string _weekend = "6,7";
    private string? _hoursPerWeek;

    public void Dispose() => _store.Dispose();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private Holidays New() => new(_store.Db, () => _weekend, () => _hoursPerWeek);

    /// <summary>Canada's federal holidays and Québec's of 2026, the United States' and France's, downloaded.</summary>
    private Holidays EveryCalendarDownloaded()
    {
        var holidays = New();
        holidays.Save("ca", Utf8(HolidayFiles.FederalJson(2026)), Downloaded);
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);
        holidays.Save("us", Utf8(HolidayFiles.OpmIcs), Downloaded);
        holidays.Save("fr", Utf8(HolidayFiles.FranceJson), Downloaded);
        return holidays;
    }

    // ------------------------------------------------------------------------------------------------ the calendars

    [Fact]
    public void Calendars_NewDatabase_AreFourWithQuebecUnderCanadaBothChecked()
    {
        var calendars = New().Calendars();

        Assert.Equal(["Canada (Federal)", "Québec", "United States (Federal)", "France"], calendars.Select(c => c.Location));
        Assert.Equal([null, "ca", null, null], calendars.Select(c => c.Parent));
        Assert.Equal([true, true, false, false], calendars.Select(c => c.Active));
    }

    [Fact]
    public void Calendars_NewDatabase_AreDownloadedFromTheServicesOfEachCountry()
    {
        var calendars = New().Calendars();

        Assert.Equal("https://canada-holidays.ca/api/v1/holidays?federal=true&year={year}", calendars[0].Url);
        Assert.Equal("https://canada-holidays.ca/api/v1/provinces/QC?year={year}", calendars[1].Url);
        Assert.StartsWith("https://www.opm.gov/", calendars[2].Url);
        Assert.StartsWith("https://calendrier.api.gouv.fr/", calendars[3].Url);
    }

    [Fact]
    public void Views_NothingDownloaded_SayNothingOfTheirHolidays() => Assert.All(New().Views(), v => Assert.Null(v.PerYear));

    [Fact]
    public void SaveCalendar_PastedLinkNamingAYear_KeepsItWithTheYearMark()
    {
        var saved = New().SaveCalendar(null, "Ontario", "https://canada-holidays.ca/api/v1/provinces/ON?year=2026", "ca");

        Assert.Equal("https://canada-holidays.ca/api/v1/provinces/ON?year={year}", saved.Url);
    }

    [Fact]
    public void SaveCalendar_NewPlaceWithSpaces_IsTrimmedUncheckedAndKept()
    {
        var belgium = New().SaveCalendar(null, "  Belgium ", " https://example.test/be.ics ");

        Assert.Equal(("Belgium", "https://example.test/be.ics", false, (string?)null), (belgium.Location, belgium.Url, belgium.Active, belgium.Parent));
        Assert.Equal(5, New().Calendars().Count);
    }

    [Theory]
    [InlineData("belgium", "https://example.test/x.ics")] // already there, whatever the case
    [InlineData("Quebec", "https://example.test/q.ics")]  // the same folder as Québec
    [InlineData(" ", "https://example.test/x.ics")]
    [InlineData("Spain", "http://example.test/x.ics")]
    [InlineData("Spain", "x.ics")]
    public void SaveCalendar_PlaceTakenEmptyOrAddressNotFullHttps_Throws(string place, string url)
    {
        var holidays = New();
        holidays.SaveCalendar(null, "Belgium", "https://example.test/be.ics");

        Assert.Throws<ArgumentException>(() => holidays.SaveCalendar(null, place, url));
    }

    [Fact]
    public void SaveCalendar_PlaceRenamed_KeepsItsHolidaysAndMovesItsFiles()
    {
        var holidays = New();
        var belgium = holidays.SaveCalendar(null, "Belgium", "https://example.test/be.ics");
        holidays.Save(belgium.Id, Utf8("""{"2026-07-21": "Fête nationale"}"""), Downloaded);

        holidays.SaveCalendar(belgium.Id, "Belgique", belgium.Url);

        Assert.Single(holidays.Of(belgium.Id));
        Assert.False(Directory.Exists(Path.Combine(_store.Db.DownloadsFolder, "holidays", "belgium")));
        Assert.True(Directory.Exists(Path.Combine(_store.Db.DownloadsFolder, "holidays", "belgique")));
    }

    [Fact]
    public void SaveCalendar_NewAddress_DropsTheHolidaysOfTheOldOne()
    {
        var holidays = New();
        var belgium = holidays.SaveCalendar(null, "Belgium", "https://example.test/be.ics");
        holidays.Save(belgium.Id, Utf8("""{"2026-07-21": "Fête nationale"}"""), Downloaded);

        holidays.SaveCalendar(belgium.Id, "Belgium", "https://example.test/other.ics");

        Assert.Empty(holidays.Of(belgium.Id));
        Assert.Empty(holidays.Copies(holidays.Find(belgium.Id)!));
    }

    // ------------------------------------------------------------------------------------------------ provinces under countries

    private static bool Active(Holidays h, string id) => h.Find(id)!.Active;

    [Fact]
    public void SetActive_ProvinceUnchecked_LeavesItsCountryChecked()
    {
        var holidays = New();

        holidays.SetActive("qc", false);

        Assert.Equal((true, false), (Active(holidays, "ca"), Active(holidays, "qc")));
    }

    [Fact]
    public void SetActive_CountryUncheckedThenProvinceChecked_LeavesTheCountryUnchecked()
    {
        var holidays = New();
        holidays.SetActive("ca", false);
        holidays.SetActive("qc", false);

        holidays.SetActive("qc", true);

        Assert.Equal((false, true), (Active(holidays, "ca"), Active(holidays, "qc")));
    }

    [Fact]
    public void SaveCalendar_AnotherProvinceOfACountry_ComesAfterTheProvincesAlreadyUnderIt()
    {
        var holidays = New();

        var ontario = holidays.SaveCalendar(null, "Ontario", "https://example.test/on/{year}.json", "ca");

        Assert.Equal(["ca", "qc", ontario.Id, "us", "fr"], holidays.Calendars().Select(c => c.Id));
    }

    [Fact]
    public void SaveCalendar_ProvinceMovedUnderAnotherCountry_SitsUnderIt()
    {
        var holidays = New();
        var ontario = holidays.SaveCalendar(null, "Ontario", "https://example.test/on/{year}.json", "ca");

        holidays.SaveCalendar(ontario.Id, "Ontario", ontario.Url, "us");

        Assert.Equal(["ca", "qc", "us", ontario.Id, "fr"], holidays.Calendars().Select(c => c.Id));
    }

    [Theory]
    [InlineData(null, "Montréal", "https://example.test/mtl.ics", "qc")] // nothing under a province
    [InlineData("ca", "Canada (Federal)", "https://canada-holidays.ca/api/v1/holidays?federal=true&year={year}", "us")] // a country with provinces stays a country
    [InlineData("fr", "France", "https://calendrier.api.gouv.fr/jours-feries/metropole.json", "fr")] // under itself
    public void SaveCalendar_CalendarUnderAProvinceOrACountryWithProvincesMoved_Throws(string? id, string place, string url, string parent) =>
        Assert.Throws<ArgumentException>(() => New().SaveCalendar(id, place, url, parent));

    [Fact]
    public void RemoveCalendar_Country_RemovesItsProvincesWithTheirFiles()
    {
        var holidays = New();
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);

        holidays.RemoveCalendar("ca");

        Assert.Equal(["us", "fr"], holidays.Calendars().Select(c => c.Id));
        Assert.False(Directory.Exists(Path.Combine(_store.Db.DownloadsFolder, "holidays", "quebec")));
    }

    // ------------------------------------------------------------------------------------------------ business days

    [Fact]
    public void BusinessDays_CanadaAndQuebecChecked_LeavesOutTheWeekendAndEachHolidayOnce()
    {
        var holidays = EveryCalendarDownloaded();

        // 23 days Monday to Friday, less Christmas (Friday, in both calendars: once) and Monday the 28th (the day off for
        // Boxing Day, a Saturday).
        Assert.Equal(21, holidays.BusinessDays(December.From, December.To));
    }

    [Theory]
    [InlineData("5,6,7", 18)] // Monday to Thursday: 19, less Monday the 28th
    [InlineData("", 28)]      // no weekend: 31, less the 25th, the 26th and the 28th
    public void BusinessDays_OtherWeekend_LeavesOutThoseDaysAndTheHolidays(string weekend, int days)
    {
        var holidays = EveryCalendarDownloaded();

        _weekend = weekend;

        Assert.Equal(days, holidays.BusinessDays(December.From, December.To));
    }

    [Fact]
    public void BusinessDays_FranceCheckedToo_CountsAHolidayOfBothCountriesOnce()
    {
        var holidays = EveryCalendarDownloaded();

        holidays.SetActive("fr", true);

        Assert.Equal(21, holidays.BusinessDays(December.From, December.To));
    }

    [Fact]
    public void BusinessDays_UnitedStatesChecked_LeavesOutThanksgiving()
    {
        var holidays = EveryCalendarDownloaded();
        var november = (From: new DateOnly(2026, 11, 1), To: new DateOnly(2026, 11, 30));
        Assert.Equal(20, holidays.BusinessDays(november.From, november.To));

        holidays.SetActive("us", true);

        Assert.Equal(19, holidays.BusinessDays(november.From, november.To));
    }

    [Fact]
    public void BusinessDays_NoCalendarChecked_LeavesOutOnlyTheWeekend()
    {
        var holidays = EveryCalendarDownloaded();

        holidays.SetActive("ca", false);
        holidays.SetActive("qc", false);

        Assert.Equal(23, holidays.BusinessDays(December.From, December.To));
        Assert.Empty(holidays.List(December.From, December.To));
    }

    [Fact]
    public void List_CheckedCalendars_GivesEveryHolidayDayByDayWithItsPlace()
    {
        var holidays = EveryCalendarDownloaded();

        holidays.SetActive("fr", true);

        Assert.Equal([("2026-12-25", "Christmas Day", "Canada (Federal)", false), ("2026-12-25", "Christmas Day", "Québec", false),
            ("2026-12-25", "Jour de Noël", "France", false), ("2026-12-26", "Boxing Day", "Canada (Federal)", false), ("2026-12-28", "Boxing Day", "Canada (Federal)", true)],
            holidays.List(December.From, December.To).Select(x => (x.Day, x.Name, x.Location, x.Observed)));
    }

    [Theory]
    [InlineData(new[] { "ca" }, 4)]             // 1 January, 11 November, 25 and 26 December
    [InlineData(new[] { "ca", "qc" }, 5)]       // Saint-Jean-Baptiste is new
    [InlineData(new[] { "ca", "qc", "fr" }, 6)] // Toussaint is new
    [InlineData(new[] { "qc" }, 3)]
    public void TotalHolidays_CheckedCalendars_CountsEachDayOnce(string[] checkedCalendars, int total)
    {
        var holidays = EveryCalendarDownloaded();
        foreach (var c in holidays.Calendars()) holidays.SetActive(c.Id, checkedCalendars.Contains(c.Id));

        Assert.Equal(total, holidays.TotalHolidays(2026));
    }

    [Fact]
    public void TotalHolidays_YearNotDownloaded_IsZero() => Assert.Equal(0, EveryCalendarDownloaded().TotalHolidays(2030));

    [Fact]
    public void Report_MonthWithAHoliday_SaysItsBusinessDays()
    {
        var holidays = New();
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);

        var june = _store.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(_store.Db), holidays);

        // 22 days Monday to Friday, less Saint-Jean-Baptiste (Wednesday 24).
        Assert.Equal(21, june.BusinessDays);
    }

    [Fact]
    public void Report_Year_SaysTheWeekdaysLessItsHolidays()
    {
        var holidays = New();
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);

        var year = _store.Summaries.Report(new(2026, 1, 1), new(2026, 12, 31), Granularity.Year, "CAD", new ExchangeRates(_store.Db), holidays);

        Assert.Equal(Holidays.Weekdays(new(2026, 1, 1), new(2026, 12, 31)) - 3, year.BusinessDays);
    }

    [Fact]
    public void Report_WithoutCalendars_SaysNoBusinessDays() =>
        Assert.Null(_store.Summaries.Report(new(2026, 6, 1), new(2026, 6, 30), Granularity.Month, "CAD", new ExchangeRates(_store.Db)).BusinessDays);

    // ------------------------------------------------------------------------------------------------ salary per day and hour

    /// <summary>December 2026: two salaries of 2,000, a refund and a purchase, with a euro rate of 1.5.</summary>
    private (Holidays Holidays, ExchangeRates Rates) DecemberWithSalaries()
    {
        var holidays = New();
        holidays.Save("ca", Utf8(HolidayFiles.FederalJson(2026)), Downloaded);
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);
        var account = _store.AddAccount();
        _store.Enter(account, "2026-12-04", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-12-18", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-12-10", 150m, "Refund", "otherincome");
        _store.Enter(account, "2026-12-11", -80m, "Market", "groceries");
        var rates = new ExchangeRates(_store.Db, () => "ecb");
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,\n2026-12-01,1.5000,"));
        return (holidays, rates);
    }

    private SalaryRate? Salary(Holidays holidays, ExchangeRates rates, string currency = "CAD", int month = 12) =>
        _store.Summaries.Report(new(2026, month, 1), Formatting.MonthEnd(2026, month), Granularity.Month, currency, rates, holidays).Salary;

    [Fact]
    public void Report_SalariesOfAMonth_GivesTheNetPayPerWorkingDayAndHourWithPaidHolidaysCounted()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates)!;

        // 4,000 of salary (not the refund) over December's 23 days Monday to Friday, 8 hours a day.
        Assert.Equal((4000m, 23, 8m, 173.91m, 21.74m), (salary.Net, salary.WorkingDays, salary.HoursPerDay, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_SalariesOfAMonth_GivesThePayOfAYearOfWorkingDays()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates)!;

        // 2026: 261 days Monday to Friday. 4,000 / 23 x 261.
        Assert.Equal((261m, 45391.30m), (salary.YearWorkingDays, salary.PerYear));
    }

    [Fact]
    public void Report_SalariesInAnotherDisplayCurrency_ConvertsEachSalaryAtItsDaysRate()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates, "EUR")!;

        Assert.Equal((2666.66m, 115.94m, 14.49m), (salary.Net, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_HoursPerWeekEntered_GivesTheHourlyPayOfThoseHours()
    {
        var (holidays, rates) = DecemberWithSalaries();

        _hoursPerWeek = "37.5";

        var salary = Salary(holidays, rates)!;
        Assert.Equal((7.5m, 173.91m, 23.19m, 37.5m), (salary.HoursPerDay, salary.PerDay, salary.PerHour, salary.HoursPerWeek));
    }

    [Fact]
    public void Report_FourDayWeek_SpreadsFortyHoursOverFourDays()
    {
        var (holidays, rates) = DecemberWithSalaries();

        _weekend = "5,6,7";

        var salary = Salary(holidays, rates)!;
        Assert.Equal((19, 10m, 210.53m, 21.05m), (salary.WorkingDays, salary.HoursPerDay, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_MonthWithoutSalary_SaysNoSalary()
    {
        var (holidays, rates) = DecemberWithSalaries();

        Assert.Null(Salary(holidays, rates, month: 11));
    }

    // ------------------------------------------------------------------------------------------------ copies kept

    private static byte[] OneHoliday(int month, string name) => Utf8($$"""{"2026-{{month:00}}-01": "{{name}}"}""");

    private static List<string> Days(Holidays h) => [.. h.Copies(h.Find("qc")!).Select(c => c.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];

    private static readonly DateOnly Day1 = new(2026, 11, 8);

    [Fact]
    public void Save_FirstDownload_KeepsTheFileAsDownloaded()
    {
        var holidays = New();

        holidays.Save("qc", OneHoliday(1, "a"), Day1);

        Assert.Equal(["2026-11-08"], Days(holidays));
        Assert.Equal(["holidays.json"], holidays.Copies(holidays.Find("qc")!)[0].Files);
    }

    [Fact]
    public void Save_AgainTheSameDay_ReplacesTheLatestCopy()
    {
        var holidays = New();
        holidays.Save("qc", OneHoliday(1, "a"), Day1);

        holidays.Save("qc", OneHoliday(1, "b"), Day1);

        Assert.Equal(["2026-11-08"], Days(holidays));
        Assert.Equal("b", holidays.Of("qc").Single().Name);
    }

    [Fact]
    public void Save_ThirdDownload_KeepsTheLatestAndThePreviousOnly()
    {
        var holidays = New();
        holidays.Save("qc", OneHoliday(1, "a"), Day1);
        holidays.Save("qc", OneHoliday(2, "c"), Day1.AddDays(30));

        holidays.Save("qc", OneHoliday(3, "e"), Day1.AddDays(60));

        Assert.Equal(["2027-01-07", "2026-12-08"], Days(holidays));
        Assert.Equal(("e", false), (holidays.Of("qc").Single().Name, holidays.FromPrevious("qc")));
    }

    [Fact]
    public void Reload_LatestCopyThatCannotBeRead_UsesThePreviousOne()
    {
        var holidays = New();
        holidays.Save("qc", OneHoliday(2, "d"), Day1);
        holidays.Save("qc", OneHoliday(3, "e"), Day1.AddDays(30));
        File.WriteAllText(Path.Combine(holidays.Copies(holidays.Find("qc")!)[0].Folder, "holidays.json"), "garbage");

        holidays.Reload();

        Assert.Equal(("d", true), (holidays.Of("qc").Single().Name, holidays.FromPrevious("qc")));
    }

    [Fact]
    public void Save_AfterADownloadStoppedInTheMiddle_IsNeverACopyAndIsCleared()
    {
        var holidays = New();
        holidays.Save("qc", OneHoliday(1, "a"), Day1);
        var partial = Path.Combine(_store.Db.DownloadsFolder, "holidays", "quebec", "2027-02-01.partial");
        Directory.CreateDirectory(partial);
        holidays.Reload();
        Assert.Equal(["2026-11-08"], Days(holidays));

        holidays.Save("qc", OneHoliday(4, "f"), Day1.AddDays(90));

        Assert.False(Directory.Exists(partial));
    }

    [Fact]
    public void Save_FileGivingNoHolidays_ThrowsAndKeepsTheCopies()
    {
        var holidays = New();
        holidays.Save("qc", OneHoliday(1, "a"), Day1);

        Assert.Throws<HolidaysFileException>(() => holidays.Save("qc", Utf8("<html></html>"), Day1.AddDays(1)));
        Assert.Equal(["2026-11-08"], Days(holidays));
    }

    // ------------------------------------------------------------------------------------------------ downloading

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(Holidays.RefreshDays, true)]
    public void Due_DaysSinceTheLastDownload_IsDueAboutOnceAMonth(int daysAgo, bool due)
    {
        _store.Db.SetSetting("holidays.fr.updatedAt", DateTime.Today.AddDays(-daysAgo).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var holidays = New();

        Assert.Equal(due, holidays.Due(holidays.Find("fr")!));
    }

    [Fact]
    public void Due_NeverDownloaded_IsTrue()
    {
        var holidays = New();

        Assert.True(holidays.Due(holidays.Find("fr")!));
    }

    /// <summary>Every service answers its own file; canada-holidays.ca one per year, refusing years before two years ago (400).</summary>
    private static FakeWeb EveryService(int year) => new(u =>
    {
        if (u.Host != "canada-holidays.ca") return (HttpStatusCode.OK, u.Host == "www.opm.gov" ? HolidayFiles.OpmIcs : HolidayFiles.FranceJson);
        var y = int.Parse(u.Query.Split("year=")[1], CultureInfo.InvariantCulture);
        if (y < year - 2) return (HttpStatusCode.BadRequest, """{"error":{"status":400}}""");
        return (HttpStatusCode.OK, u.AbsolutePath.Contains("/provinces/QC", StringComparison.Ordinal) ? HolidayFiles.QuebecJson(y) : HolidayFiles.FederalJson(y));
    });

    [Fact]
    public async Task UpdateAllAsync_NothingDownloaded_DownloadsEveryYearOfTheYearlyCalendarsAndEachFileOnce()
    {
        var year = DateTime.Today.Year;
        var holidays = New();
        using var web = EveryService(year);
        using var client = web.Client();

        var changed = await holidays.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(12 + 12 + 1 + 1, web.Calls);
        Assert.Contains($"https://canada-holidays.ca/api/v1/provinces/QC?year={year - 10}", web.Asked);
    }

    [Fact]
    public async Task UpdateAllAsync_SomeYearsRefused_KeepsTheYearsGivenAndCountsThisYearsHolidays()
    {
        var year = DateTime.Today.Year;
        var holidays = New();
        using var web = EveryService(year);
        using var client = web.Client();

        await holidays.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        var views = holidays.Views();
        Assert.All(views, v => Assert.Null(v.Error));
        Assert.Equal((4, 3, year), (views[0].PerYear, views[1].PerYear, views[0].Year));
        Assert.Equal(12, holidays.Of("qc").Count(x => !x.Observed));
    }

    [Fact]
    public async Task UpdateAllAsync_KeepsEachCalendarsFilesNamedByTheirYear()
    {
        var year = DateTime.Today.Year;
        var holidays = New();
        using var web = EveryService(year);
        using var client = web.Client();

        await holidays.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        var quebec = holidays.Copies(holidays.Find("qc")!)[0];
        Assert.Equal(Path.Combine(_store.Db.DownloadsFolder, "holidays", "quebec", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), quebec.Folder);
        Assert.Equal(Enumerable.Range(year - 2, 4).Select(y => $"holidays-{y}.json"), quebec.Files);
        Assert.Equal(["holidays.ics"], holidays.Copies(holidays.Find("us")!)[0].Files);
        Assert.Equal(["holidays.json"], holidays.Copies(holidays.Find("fr")!)[0].Files);
    }

    [Fact]
    public async Task UpdateAllAsync_DownloadedToday_DownloadsNothingUnlessRefreshIsAsked()
    {
        var holidays = New();
        using var web = EveryService(DateTime.Today.Year);
        using var client = web.Client();
        await holidays.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        var due = await holidays.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);
        var callsWhenNotDue = web.Calls;
        await holidays.UpdateAllAsync(force: true, client, TestContext.Current.CancellationToken);

        Assert.False(due);
        Assert.Equal((26, 52), (callsWhenNotDue, web.Calls));
        Assert.All(holidays.Calendars(), c => Assert.Single(holidays.Copies(c)));
    }

    public static TheoryData<string, HttpStatusCode, string, string> WrongAnswers => new()
    {
        { "fr", HttpStatusCode.NotFound, "", "Nothing at this address (404): check it." },
        { "qc", HttpStatusCode.NotFound, "", "Nothing at this address (404): check it." },
        { "fr", HttpStatusCode.OK, "<html></html>", "This address gives a web page, not a calendar file" },
        { "fr", HttpStatusCode.InternalServerError, "", "The address answered with an error (500 Internal Server Error)." },
    };

    [Theory]
    [MemberData(nameof(WrongAnswers))]
    public async Task UpdateAsync_WrongAddress_SaysCleanlyWhatIsWrong(string calendar, HttpStatusCode code, string body, string error)
    {
        using var web = new FakeWeb(code, body);
        using var client = web.Client();

        var result = await New().UpdateAsync(calendar, client, TestContext.Current.CancellationToken);

        Assert.StartsWith(error, result.Error);
    }

    public static TheoryData<Exception, string> Faults => new()
    {
        { new HttpRequestException("no route"), "The address could not be reached: check it, and the connection to the Internet." },
        { new InvalidOperationException("disk full"), "The holidays could not be downloaded: disk full" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task UpdateAsync_NoConnectionOrSomethingUnexpected_SaysItAndKeepsTheHolidaysDownloadedBefore(Exception fault, string error)
    {
        var holidays = New();
        holidays.Save("fr", Utf8(HolidayFiles.FranceJson), Downloaded);
        using var web = new FakeWeb(HttpStatusCode.OK, "", fault);
        using var client = web.Client();

        var result = await holidays.UpdateAsync("fr", client, TestContext.Current.CancellationToken);

        Assert.Equal(error, result.Error);
        Assert.Equal(4, holidays.Of("fr").Length);
    }
}
