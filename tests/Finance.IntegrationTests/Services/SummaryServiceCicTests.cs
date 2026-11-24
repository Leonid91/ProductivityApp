using System.Globalization;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.CicOctober;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The CIC's October (<see cref="CicOctober"/>) reported for a person whose own currency is the Canadian dollar: every
/// euro converted at the European Central Bank's rate of its day. The month is imported and reported once per display
/// currency; every test reads that report.
/// </summary>
public sealed class SummaryServiceCicTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private Task<PeriodReport> ReportedAsync(string currency) => read.GetAsync(currency, store => CicOctober.ReportedAsync(store, currency));

    private static BankPart CicPart(PeriodReport r) => r.Banks.Single(b => b.Name == "CIC");

    private static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Report_InCanadianDollars_SaysTheBanksOwnCurrencyIsTheEuro()
    {
        var report = await ReportedAsync("CAD");

        Assert.Equal(("CAD", "EUR"), (CicPart(report).Summary.Currency, CicPart(report).Native));
    }

    [Fact]
    public async Task Report_InCanadianDollars_ConvertsEachOperationAtTheEcbRateOfItsDay()
    {
        var report = await ReportedAsync("CAD");

        var shown = CicPart(report).Summary.Transactions;
        Assert.Equal(Current.Lines.Count + LivretA.Lines.Count, shown.Count);
        Assert.All(shown, t => Assert.Equal(("CAD", "EUR"), (t.Currency, t.OriginalCurrency)));
        Assert.All(shown, t => Assert.Equal(InCad(t.Original!.Value, Day(t.Date)), t.Amount));
    }

    [Fact]
    public async Task Report_InCanadianDollars_OperationOnASaturdayTakesFridaysRate()
    {
        var report = await ReportedAsync("CAD");

        var saturday = CicPart(report).Summary.Transactions.First(t => Day(t.Date).DayOfWeek == DayOfWeek.Saturday);
        Assert.Equal(Math.Round(saturday.Original!.Value * CadPerEuro(Day(saturday.Date).AddDays(-1)), 2, MidpointRounding.AwayFromZero), saturday.Amount);
    }

    [Fact]
    public async Task Report_InCanadianDollars_AddsUpTheConvertedExpensesLeavingTheMoneyPutOnTheLivretAOut()
    {
        var report = await ReportedAsync("CAD");

        var spent = Current.Lines.Where(l => l.Amount < 0 && l.Label != "VIR LIVRET A").Sum(l => -InCad(l.Amount, l.Date));
        Assert.Equal(spent, CicPart(report).Summary.Expenses);
        Assert.Equal(spent, report.Global.Expenses);
    }

    [Fact]
    public async Task Report_InCanadianDollars_ShowsHowTheEurosWereConverted()
    {
        var report = await ReportedAsync("CAD");

        var line = Assert.Single(report.Conversion.Lines);
        Assert.Equal(("CIC", "EUR", "CAD", 0), (line.Group, line.From, line.To, line.Unconverted));
        Assert.Equal(Current.Debits - 150m, line.Expenses);
        Assert.All(line.Rates, u => Assert.Equal(CadPerEuro(RateDay(Day(u.Day))), u.Rate));
        Assert.Equal((RateSource.Ecb.Url, "EUR"), (report.Conversion.Url, report.Conversion.Base));
        Assert.Empty(report.Unconverted);
    }

    [Fact]
    public async Task Report_InEuros_ShowsTheAmountsAsTheBankWroteThem()
    {
        var report = await ReportedAsync("EUR");

        Assert.Equal(Current.Lines.Concat(LivretA.Lines).Select(l => l.Amount).Order(), CicPart(report).Summary.Transactions.Select(t => t.Amount).Order());
        Assert.Equal(Current.Debits - 150m, report.Global.Expenses);
        Assert.Empty(report.Conversion.Lines);
    }

    [Fact]
    public async Task Report_MoneyPutOnTheLivretA_IsOneMovementInsideTheBank()
    {
        var report = await ReportedAsync("EUR");

        var names = (await read.StoreAsync("EUR")).Db.Accounts().ToDictionary(a => a.Kind, a => a.Name);
        var inside = Assert.Single(report.Global.Movements, m => !m.FromOutside && !m.ToOutside);
        Assert.Equal((names[AccountKind.Bank], names[AccountKind.Savings], 150m), (inside.From, inside.To, inside.Amount));
    }

    [Fact]
    public async Task Report_WithoutAnyRate_LeavesTheEurosOutOfTheCanadianTotalsAndSaysSo()
    {
        using var store = new TestStore();
        await ImportAsync(store);

        var report = store.Summaries.Report(October.Start, October.End, Granularity.Month, "CAD", new ExchangeRates(store.Db));

        Assert.Equal(Current.Lines.Count + LivretA.Lines.Count, report.Unconverted.Count);
        Assert.Equal((0m, 0m), (report.Global.Expenses, report.Global.Earnings));
    }
}
