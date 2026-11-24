using System.Globalization;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.TwoBanksOctober;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// A Canadian bank in Canadian dollars and a French bank in euros, one month of each (<see cref="TwoBanksOctober"/>):
/// each bank on its own in its currency, every bank together converted to the display currency at the rate of each
/// operation's day. Each report is made once; every test reads it.
/// </summary>
public sealed class SummaryServiceTwoCurrenciesTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private Task<PeriodReport> InCadAsync() => read.GetAsync("CAD", TwoBanksOctober.InCadAsync);

    private Task<PeriodReport> WithoutRatesAsync(string currency) => read.GetAsync(currency + " without rates", store => TwoBanksOctober.WithoutRatesAsync(store, currency));

    private static decimal SpentInCad => Current.Lines.Where(l => l.Amount < 0 && !l.Label.Contains("LIVRET", StringComparison.Ordinal)).Sum(l => -InCad(l.Amount, l.Date));

    private static decimal EarnedInCad => Current.Lines.Where(l => l.Amount > 0).Sum(l => InCad(l.Amount, l.Date));

    [Fact]
    public async Task Report_TwoBanks_ShowsEachInTheDisplayCurrencyAndSaysItsOwn()
    {
        var report = await InCadAsync();

        Assert.Equal(["Caisse d'Epargne", KnownBanks.National], report.Banks.Select(b => b.Name));
        Assert.Equal([("CAD", "EUR"), ("CAD", "CAD")], report.Banks.Select(b => (b.Summary.Currency, b.Native)));
    }

    [Fact]
    public async Task Report_EuroBank_ConvertsEachOperationAtItsDaysRateBeforeAddingThemUp()
    {
        var report = await InCadAsync();

        var caisse = report.Banks[0].Summary;
        Assert.Equal((SpentInCad, EarnedInCad), (caisse.Expenses, caisse.Earnings));
        Assert.All(caisse.Transactions, t => Assert.Equal(("CAD", "EUR"), (t.Currency, t.OriginalCurrency)));
    }

    [Fact]
    public async Task Report_AllBanks_AddsTheConvertedBankToTheOther()
    {
        var report = await InCadAsync();

        var national = report.Banks[1].Summary;
        Assert.Equal((national.Expenses + SpentInCad, national.Earnings + EarnedInCad), (report.Global.Expenses, report.Global.Earnings));
        Assert.Equal(report.Global.Earnings - report.Global.Expenses, report.Global.Balance);
        Assert.Equal(report.Global.Balance, Assert.Single(report.Global.Breakdown).Balance);
    }

    [Fact]
    public async Task Report_AllBanks_SendsEachOperationOnceInItsBanksPart()
    {
        var report = await InCadAsync();

        Assert.Empty(report.Global.Transactions);
        Assert.Empty(report.Unconverted);
    }

    [Fact]
    public async Task Report_EuroBank_ShowsTheConversionWithTheRateOfEveryDay()
    {
        var report = await InCadAsync();

        var line = Assert.Single(report.Conversion.Lines);
        Assert.Equal(("Caisse d'Epargne", "EUR", "CAD", 0), (line.Group, line.From, line.To, line.Unconverted));
        Assert.Equal((Current.Debits - 200m, Current.Credits, SpentInCad), (line.Expenses, line.Earnings, line.ConvertedExpenses));
        Assert.All(line.Rates, u => Assert.Equal(CadPerEuro(DateOnly.ParseExact(u.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)), u.Rate));
    }

    [Fact]
    public async Task Report_WithoutAnyRate_ListsTheEuroOperationsOutsideEveryTotal()
    {
        var report = await WithoutRatesAsync("CAD");

        var line = Assert.Single(report.Conversion.Lines);
        var euros = Caisse.Accounts.Sum(a => a.Lines.Count);
        Assert.Equal((0, euros, euros), (line.Operations, line.Unconverted, report.Unconverted.Count));
        Assert.Equal(euros, report.Banks[0].Summary.Transactions.Count(t => t.Currency == "EUR"));
        Assert.Equal((0m, report.Banks[1].Summary.Balance), (report.Banks[0].Summary.Balance, report.Global.Balance));
    }

    [Fact]
    public async Task Report_InEurosWithoutAnyRate_LeavesTheDollarBankOut()
    {
        var report = await WithoutRatesAsync("EUR");

        Assert.Equal(("CAD", "EUR"), (Assert.Single(report.Conversion.Lines).From, report.Conversion.Lines[0].To));
        Assert.Equal(report.Banks[0].Summary.Balance, report.Global.Balance);
    }
}
