using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// Reports are kept while nothing they depend on changes: the same period asked again is the same report; any change
/// of the operations, of the settings or of the rates, or another period or currency, makes a new one.
/// </summary>
public class ReportCacheTests
{
    [Fact]
    public void A_report_is_kept_until_something_it_depends_on_changes()
    {
        using var s = new TempStore();
        var bank = s.Db.AddBank(new Bank { Name = "Banque Exemple", System = BankingSystem.NorthAmerica, Currency = "CAD" });
        var account = s.Db.AddAccount(new Account { Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank, BankId = bank });
        s.Transactions.Create(new TransactionInput("2026-03-04", -40m, "Market", "groceries", account, null));
        var source = "ecb";
        var rates = new ExchangeRates(s.Db, () => source);
        PeriodReport March(string currency = "CAD") => s.Summaries.Report(new(2026, 3, 1), new(2026, 3, 31), Granularity.Month, currency, rates);

        var first = March();
        Assert.Same(first, March());
        // Another currency, another period: their own reports.
        Assert.NotSame(first, March("EUR"));
        Assert.NotSame(first, s.Summaries.Report(new(2026, 3, 1), new(2026, 3, 30), Granularity.Range, "CAD", rates));
        // An operation added: made again, with it.
        s.Transactions.Create(new TransactionInput("2026-03-05", -10m, "Bakery", "groceries", account, null));
        var second = March();
        Assert.NotSame(first, second);
        Assert.Equal(-50m, second.Banks.Single().Summary.Transactions.Sum(t => t.Amount));
        Assert.Same(second, March());
        // A setting written, or another source of rates chosen: made again.
        s.Db.SetSetting("anything", "changed");
        var third = March();
        Assert.NotSame(second, third);
        source = "boc";
        Assert.NotSame(third, March());
    }
}
