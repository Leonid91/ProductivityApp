using System.Globalization;
using System.Text;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Infrastructure;

/// <summary>
/// A chequing account at Northwind Bank (invented) from 1 January to 10 November 2026: rent, salaries, groceries, a
/// coffee, a phone bill; printed as statements of any period, or as the bank's CSV export.
/// </summary>
public static class ChequingYear
{
    public static DateOnly D(int month, int day) => new(2026, month, day);

    public static readonly List<FakeTransaction> Year = BuildYear();

    /// <summary>
    /// Day after day, in this order on a day they share: the rent on the 1st (January to November), the salary on the
    /// 15th and the last day (January to October), the metro every fourth day and a coffee every ninth (counted from
    /// day 0 of the calendar), the phone on the 20th (January to October).
    /// </summary>
    private static List<FakeTransaction> BuildYear()
    {
        var first = D(1, 1);
        var last = D(11, 10);
        IEnumerable<DateOnly> Every(int step)
        {
            var start = first.AddDays((step - first.DayNumber % step) % step);
            return Enumerable.Range(0, (last.DayNumber - start.DayNumber) / step + 1).Select(k => start.AddDays(k * step));
        }
        var months = Enumerable.Range(1, 10).ToList();
        return
        [
            .. Enumerable.Range(1, 11).Select(m => new FakeTransaction(D(m, 1), "RENT EXAMPLE PROPERTIES", -1400m))
                .Concat(months.SelectMany(m => new[] { D(m, 15), D(m, DateTime.DaysInMonth(2026, m)) }).Select(d => new FakeTransaction(d, "PAYROLL DEPOSIT EXAMPLE CORP", 2100m)))
                .Concat(Every(4).Select(d => new FakeTransaction(d, "METRO #123 MONTREAL QC", -(40m + d.DayNumber % 37 + d.Day / 100m))))
                .Concat(Every(9).Select(d => new FakeTransaction(d, "CAFE EXEMPLE MONTREAL", -4.75m)))
                .Concat(months.Select(m => new FakeTransaction(D(m, 20), "TELEPHONE EXEMPLE INC", -65.50m)))
                .OrderBy(t => t.Date),
        ];
    }

    public static List<FakeTransaction> Between(IEnumerable<FakeTransaction> all, DateOnly from, DateOnly to) => [.. all.Where(t => t.Date >= from && t.Date <= to)];

    public static Spec Statement(List<FakeTransaction> txs, DateOnly from, DateOnly to, decimal opening = 1000m) =>
        new(Style.EnglishWithdrawalsDeposits, "Northwind Bank", from, to, opening, txs, "CAD");

    /// <summary>The same operations as a bank's CSV export ("Date,Description,Amount"), the wording as written.</summary>
    public static byte[] Csv(IEnumerable<FakeTransaction> txs) => Csv(txs, l => l);

    /// <summary>The same operations as a bank's CSV export ("Date,Description,Amount"), the wording as <paramref name="wording"/> writes it.</summary>
    public static byte[] Csv(IEnumerable<FakeTransaction> txs, Func<string, string> wording) =>
        Encoding.UTF8.GetBytes("Date,Description,Amount\n" + string.Concat(txs.Select(t =>
            $"{t.Date:yyyy-MM-dd},\"{wording(t.Label)}\",{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n")));

    /// <summary>The year saved as two statements one after the other (1 January to 15 October, then to 10 November) into one new account.</summary>
    public static async Task<long> TwoPeriodsAsync(TestStore store)
    {
        var first = Between(Year, D(1, 1), D(10, 15));
        var account = (await store.ImportAsync("2026-01-01 2026-10-15.pdf", Render(Statement(first, D(1, 1), D(10, 15))))).AccountId;
        await store.ImportIntoAsync(account, "2026-10-16 2026-11-10.pdf", Render(Statement(Between(Year, D(10, 16), D(11, 10)), D(10, 16), D(11, 10), 1000m + first.Sum(t => t.Amount))));
        return account;
    }
}
