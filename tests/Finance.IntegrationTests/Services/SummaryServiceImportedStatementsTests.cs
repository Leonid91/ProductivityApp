using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.ChequingYear;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>What a period sums up once real files were imported: statements that follow each other, a bank's PDFs and CSVs of the same month, an online bank's pockets.</summary>
public sealed class SummaryServiceImportedStatementsTests : IDisposable
{
    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Summarize_TwoPeriodsOneAfterTheOther_CountsEveryPurchaseAndSalaryOnce()
    {
        await TwoPeriodsAsync(_store);

        var all = _store.Summarize(D(1, 1), D(11, 10), Granularity.Range);

        Assert.Equal((-Year.Where(t => t.Amount < 0).Sum(t => t.Amount), Year.Where(t => t.Amount > 0).Sum(t => t.Amount)), (all.Expenses, all.Earnings));
    }

    [Fact]
    public async Task Summarize_MonthOfACardAChequingAndASavingsAccount_CountsTheSpendingAndIncomeOnce()
    {
        await NationalBankSeptember.PdfThenCsvAsync(_store);

        var month = _store.Summarize(new(2025, 9, 1), new(2025, 9, 30));

        Assert.Equal(-NationalBankSeptember.September.Card.Where(l => l.Amount < 0).Sum(l => l.Amount) + 9.95m, month.Expenses);
        Assert.Equal(2 * 2310.45m + 3.17m, month.Earnings);
    }

    [Fact]
    public async Task Report_MoneyPutInThePocketOfAnOnlineBank_IsOneMovementInsideTheBank()
    {
        _store.CommitAll(await _store.ParseAllAsync("revolut.csv", RevolutGenerator.Csv(RevolutGenerator.Sample())), bank: _store.AddBank("Revolut", BankingSystem.France, "EUR"));

        var report = _store.Summaries.Report(new(2026, 1, 1), new(2026, 1, 31), Granularity.Month, "EUR", new ExchangeRates(_store.Db));

        Assert.Contains(report.Global.Movements, m => !m.FromOutside && !m.ToOutside && m.Amount == 300m);
    }
}
