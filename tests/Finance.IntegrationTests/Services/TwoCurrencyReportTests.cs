using System.Globalization;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// A Canadian bank in Canadian dollars and a French bank in euros, one month of each: each bank on its own in its
/// currency, every bank together converted to the display currency at the rate of each operation's day.
/// </summary>
public sealed class TwoCurrencyReportTests : IDisposable
{
    private static readonly CaisseEpargneGenerator.Statement Caisse = CaisseEpargneGenerator.Sample(2025, 10, 30);
    private static readonly CaisseEpargneGenerator.Account Current = Caisse.Accounts[0];
    private static readonly (DateOnly From, DateOnly To) October = (new(2025, 10, 1), new(2025, 10, 31));

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>The Caisse d'Epargne's PDF of two accounts in euros and the National Bank's chequing history in dollars, imported.</summary>
    private async Task ImportBothBanksAsync()
    {
        var caisse = _store.AddBank("Caisse d'Epargne", BankingSystem.France, "EUR", "France");
        _store.CommitAll(await _store.ParseAllAsync("releve.pdf", CaisseEpargneGenerator.Pdf(Caisse)), "EUR", caisse);
        await _store.ImportAsync("chequing.pdf", BncGenerator.Pdf(BncGenerator.Sample(2025, 10, 4), BncGenerator.BncAccount.Chequing));
    }

    /// <summary>A rate for every day of October 2025 (invented): 1.60 plus a thousandth for each day, in dollars per euro.</summary>
    private static decimal CadPerEuro(DateOnly day) => 1.60m + day.Day / 1000m;

    private static decimal InCad(decimal euros, DateOnly day) => Math.Round(euros * CadPerEuro(day), 2, MidpointRounding.AwayFromZero);

    private async Task<PeriodReport> OctoberInCadAsync()
    {
        var rates = new ExchangeRates(_store.Db);
        var csv = "Date,CAD,\n" + string.Join("\n", Enumerable.Range(1, 31).Reverse().Select(d => $"2025-10-{d:D2},{CadPerEuro(new DateOnly(2025, 10, d)).ToString(CultureInfo.InvariantCulture)},"));
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv));
        await ImportBothBanksAsync();
        return _store.Summaries.Report(October.From, October.To, Granularity.Month, "CAD", rates);
    }

    private static decimal SpentInCad => Current.Lines.Where(l => l.Amount < 0 && !l.Label.Contains("LIVRET", StringComparison.Ordinal)).Sum(l => -InCad(l.Amount, l.Date));

    private static decimal EarnedInCad => Current.Lines.Where(l => l.Amount > 0).Sum(l => InCad(l.Amount, l.Date));

    [Fact]
    public async Task Report_TwoBanks_ShowsEachInTheDisplayCurrencyAndSaysItsOwn()
    {
        var report = await OctoberInCadAsync();

        Assert.Equal(["Caisse d'Epargne", KnownBanks.National], report.Banks.Select(b => b.Name));
        Assert.Equal([("CAD", "EUR"), ("CAD", "CAD")], report.Banks.Select(b => (b.Summary.Currency, b.Native)));
    }

    [Fact]
    public async Task Report_EuroBank_ConvertsEachOperationAtItsDaysRateBeforeAddingThemUp()
    {
        var report = await OctoberInCadAsync();

        var caisse = report.Banks[0].Summary;
        Assert.Equal((SpentInCad, EarnedInCad), (caisse.Expenses, caisse.Earnings));
        Assert.All(caisse.Transactions, t => Assert.Equal(("CAD", "EUR"), (t.Currency, t.OriginalCurrency)));
    }

    [Fact]
    public async Task Report_AllBanks_AddsTheConvertedBankToTheOther()
    {
        var report = await OctoberInCadAsync();

        var national = report.Banks[1].Summary;
        Assert.Equal((national.Expenses + SpentInCad, national.Earnings + EarnedInCad), (report.Global.Expenses, report.Global.Earnings));
        Assert.Equal(report.Global.Earnings - report.Global.Expenses, report.Global.Balance);
        Assert.Equal(report.Global.Balance, Assert.Single(report.Global.Breakdown).Balance);
    }

    [Fact]
    public async Task Report_AllBanks_SendsEachOperationOnceInItsBanksPart()
    {
        var report = await OctoberInCadAsync();

        Assert.Empty(report.Global.Transactions);
        Assert.Empty(report.Unconverted);
    }

    [Fact]
    public async Task Report_EuroBank_ShowsTheConversionWithTheRateOfEveryDay()
    {
        var report = await OctoberInCadAsync();

        var line = Assert.Single(report.Conversion.Lines);
        Assert.Equal(("Caisse d'Epargne", "EUR", "CAD", 0), (line.Group, line.From, line.To, line.Unconverted));
        Assert.Equal((Current.Debits - 200m, Current.Credits, SpentInCad), (line.Expenses, line.Earnings, line.ConvertedExpenses));
        Assert.All(line.Rates, u => Assert.Equal(CadPerEuro(DateOnly.ParseExact(u.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)), u.Rate));
    }

    [Fact]
    public async Task Report_WithoutAnyRate_ListsTheEuroOperationsOutsideEveryTotal()
    {
        await ImportBothBanksAsync();

        var report = _store.Summaries.Report(October.From, October.To, Granularity.Month, "CAD", new ExchangeRates(_store.Db));

        var line = Assert.Single(report.Conversion.Lines);
        var euros = Caisse.Accounts.Sum(a => a.Lines.Count);
        Assert.Equal((0, euros, euros), (line.Operations, line.Unconverted, report.Unconverted.Count));
        Assert.Equal(euros, report.Banks[0].Summary.Transactions.Count(t => t.Currency == "EUR"));
        Assert.Equal((0m, report.Banks[1].Summary.Balance), (report.Banks[0].Summary.Balance, report.Global.Balance));
    }

    [Fact]
    public async Task Report_InEurosWithoutAnyRate_LeavesTheDollarBankOut()
    {
        await ImportBothBanksAsync();

        var report = _store.Summaries.Report(October.From, October.To, Granularity.Month, "EUR", new ExchangeRates(_store.Db));

        Assert.Equal(("CAD", "EUR"), (Assert.Single(report.Conversion.Lines).From, report.Conversion.Lines[0].To));
        Assert.Equal(report.Banks[0].Summary.Balance, report.Global.Balance);
    }

    [Fact]
    public async Task UpdateAccount_AccountWhoseMoneyComesInUsDollars_ConvertsItsOperationsFromDollarsAtTheDaysRate()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(RateFiles.EuropeanCentralBank));
        await ImportBothBanksAsync();
        var account = _store.Db.Accounts().First(a => a.BankId == _store.DefaultBank);

        account.Currency = "USD";
        _store.Db.UpdateAccount(account);

        var national = _store.Summaries.Report(new(2025, 10, 8), new(2025, 10, 10), Granularity.Range, "CAD", rates).Banks.Single(b => b.Name == KnownBanks.National).Summary;
        var tx = national.Transactions.First(t => t.AccountId == account.Id);
        var rate = new Dictionary<string, decimal> { ["2025-10-10"] = 1.62m / 1.15m, ["2025-10-09"] = 1.63m / 1.16m, ["2025-10-08"] = 1.60m / 1.17m }[tx.Date];
        Assert.All(national.Transactions.Where(t => t.AccountId == account.Id), t => Assert.Equal(("CAD", "USD"), (t.Currency, t.OriginalCurrency)));
        Assert.Equal(Math.Round(tx.Original!.Value * rate, 2, MidpointRounding.AwayFromZero), tx.Amount);
    }
}
