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

    [Fact]
    public void A_long_period_is_sent_in_pieces_that_make_the_whole_list_again()
    {
        using var s = new TempStore();
        var bank = s.Db.AddBank(new Bank { Name = "Banque Exemple", System = BankingSystem.NorthAmerica, Currency = "CAD" });
        var account = s.Db.AddAccount(new Account { Name = "Chequing", Currency = "CAD", Kind = AccountKind.Bank, BankId = bank });
        for (var d = 1; d <= 28; d++) s.Transactions.Create(new TransactionInput($"2026-02-{d:00}", -d, "Market", "groceries", account, null));
        var rates = new ExchangeRates(s.Db, () => "ecb");
        var full = s.Summaries.Report(new(2026, 2, 1), new(2026, 2, 28), Granularity.Month, "CAD", rates);
        var all = full.Banks.Single().Summary.Transactions;
        // The latest 10 first, and how many there are in all; the totals are the whole period's.
        var first = full.Listing(10);
        var part = first.Banks.Single();
        Assert.Equal(all.TakeLast(10).Select(t => t.Id), part.Summary.Transactions.Select(t => t.Id));
        Assert.Equal(28, part.Summary.TransactionCount);
        Assert.Equal(full.Banks.Single().Summary.Expenses, part.Summary.Expenses);
        Assert.Null(full.Banks.Single().Summary.TransactionCount);
        // The older ones in pieces, from the end: together, the whole list in its order.
        var pieces = new List<TransactionView>(part.Summary.Transactions);
        for (var end = 18; end > 0; end -= 7) pieces.InsertRange(0, full.Slice(part.Key, Math.Max(0, end - 7), end));
        Assert.Equal(all.Select(t => t.Id), pieces.Select(t => t.Id));
        // A short period is sent whole.
        Assert.Same(full, full.Listing(100));
    }
}
