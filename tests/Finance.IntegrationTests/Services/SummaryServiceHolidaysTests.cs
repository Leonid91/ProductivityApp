using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The business days and the salary per day and hour in the report of a period, from the calendars of public holidays
/// saved in the database.
/// </summary>
public sealed class SummaryServiceHolidaysTests : IDisposable
{
    private static readonly DateOnly Downloaded = new(2026, 11, 10);

    private readonly TestStore _store = new();
    private string _weekend = "6,7";
    private string? _hoursPerWeek;

    public void Dispose() => _store.Dispose();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private Holidays New() => new(_store.Db, () => _weekend, () => _hoursPerWeek);

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

    /// <summary>
    /// A pay of 2,000 every two weeks from November 20 2026 to January 1 2027 (the last one partly for December's last
    /// days), a refund and a purchase in December, with a euro rate of 1.5.
    /// </summary>
    private (Holidays Holidays, ExchangeRates Rates) DecemberWithSalaries()
    {
        var (holidays, rates, account) = DecemberPaidUpTo18th();
        _store.Enter(account, "2027-01-01", 2000m, "Employer", "salary");
        return (holidays, rates);
    }

    /// <summary>The same up to the pay of December 18: the month under way, its next pay not come yet.</summary>
    private (Holidays Holidays, ExchangeRates Rates, long Account) DecemberPaidUpTo18th()
    {
        var holidays = New();
        holidays.Save("ca", Utf8(HolidayFiles.FederalJson(2026)), Downloaded);
        holidays.Save("qc", Utf8(HolidayFiles.QuebecJson(2026)), Downloaded);
        var account = _store.AddAccount();
        _store.Enter(account, "2026-11-20", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-12-04", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-12-18", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-12-10", 150m, "Refund", "otherincome");
        _store.Enter(account, "2026-12-11", -80m, "Market", "groceries");
        var rates = new ExchangeRates(_store.Db, () => "ecb");
        // Downloaded after the salaries (rates are known up to the day of their download).
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,\n2026-11-02,1.5000,"), new DateOnly(2027, 1, 31));
        return (holidays, rates, account);
    }

    private SalaryRate? Salary(Holidays holidays, ExchangeRates rates, string currency = "CAD", int month = 12) =>
        _store.Summaries.Report(new(2026, month, 1), Formatting.MonthEnd(2026, month), Granularity.Month, currency, rates, holidays).Salary;

    [Fact]
    public void Report_SalariesOfAMonth_GivesTheNetPayPerWorkingDayAndHourWithPaidHolidaysCounted()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates)!;

        // Each pay is for the 14 days up to it: December's 31 days are 4 days of the pay of December 4, the pay of
        // December 18, and 13 days of the pay of January 1. 2,000 x 31 / 14 (not the refund), over December's 23 days
        // Monday to Friday, 8 hours a day.
        Assert.Equal((4428.57m, 23, 8m, 192.55m, 24.07m), (salary.Net, salary.WorkingDays, salary.HoursPerDay, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_MonthWithThreePays_CountsTheDaysItWasPaidFor()
    {
        var holidays = New();
        var account = _store.AddAccount();
        _store.Enter(account, "2026-09-18", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-10-02", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-10-16", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-10-30", 2000m, "Employer", "salary");
        _store.Enter(account, "2026-11-13", 2000m, "Employer", "salary");

        var october = Salary(holidays, new ExchangeRates(_store.Db), month: 10)!;

        // Three pays are dated in October, but October is 31 days of 14-day pays: 2,000 x 31 / 14, like December.
        Assert.Equal((4428.57m, 22), (october.Net, october.WorkingDays));
    }

    [Fact]
    public void Report_MonthUnderWay_CountsTheDaysUpToItsLastPay()
    {
        var (holidays, rates, _) = DecemberPaidUpTo18th();

        var salary = Salary(holidays, rates)!;

        // December 1 to 18: 4 days of the pay of December 4 and the pay of December 18, over 14 days Monday to Friday.
        Assert.Equal((2571.43m, 14, 183.67m), (salary.Net, salary.WorkingDays, salary.PerDay));
    }

    [Fact]
    public void Report_OnlyPayOfItsPayer_IsForTheWholePeriod()
    {
        var holidays = New();
        var account = _store.AddAccount();
        _store.Enter(account, "2026-12-15", 2300m, "Employer", "salary");

        var salary = Salary(holidays, new ExchangeRates(_store.Db))!;

        Assert.Equal((2300m, 23, 100m), (salary.Net, salary.WorkingDays, salary.PerDay));
    }

    [Fact]
    public void Report_SalariesOfAMonth_GivesThePayOfAYearOfWorkingDays()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates)!;

        // 2026: 261 days Monday to Friday. 4,428.57 / 23 x 261.
        Assert.Equal((261m, 50254.64m), (salary.YearWorkingDays, salary.PerYear));
    }

    [Fact]
    public void Report_SalariesInAnotherDisplayCurrency_ConvertsEachSalaryAtItsDaysRate()
    {
        var (holidays, rates) = DecemberWithSalaries();

        var salary = Salary(holidays, rates, "EUR")!;

        // Each pay is 1,333.33 euros (2,000 / 1.5 to the cent): 1,333.33 x 31 / 14.
        Assert.Equal((2952.37m, 128.36m, 16.05m), (salary.Net, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_HoursPerWeekEntered_GivesTheHourlyPayOfThoseHours()
    {
        var (holidays, rates) = DecemberWithSalaries();

        _hoursPerWeek = "37.5";

        var salary = Salary(holidays, rates)!;
        Assert.Equal((7.5m, 192.55m, 25.67m, 37.5m), (salary.HoursPerDay, salary.PerDay, salary.PerHour, salary.HoursPerWeek));
    }

    [Fact]
    public void Report_FourDayWeek_SpreadsFortyHoursOverFourDays()
    {
        var (holidays, rates) = DecemberWithSalaries();

        _weekend = "5,6,7";

        var salary = Salary(holidays, rates)!;
        Assert.Equal((19, 10m, 233.08m, 23.31m), (salary.WorkingDays, salary.HoursPerDay, salary.PerDay, salary.PerHour));
    }

    [Fact]
    public void Report_MonthWithoutSalary_SaysNoSalary()
    {
        var (holidays, rates) = DecemberWithSalaries();

        // The first pay, November 20, is for November 7 to 20: nothing of October.
        Assert.Null(Salary(holidays, rates, month: 10));
    }
}
