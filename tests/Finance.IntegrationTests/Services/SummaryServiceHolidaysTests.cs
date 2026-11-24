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
        // Downloaded after the salaries (rates are known up to the day of their download).
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,\n2026-12-01,1.5000,"), new DateOnly(2026, 12, 31));
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
}
