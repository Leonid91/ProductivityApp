using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.NationalBankSeptember;
using static ProductivityApp.Finance.Testing.BncGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The National Bank of Canada's PDF histories and CSV exports (invented data in its layouts) saved through the import.
/// The month of three accounts, PDFs then CSVs, is saved once for the tests that only read what it saved.
/// </summary>
public sealed class ImportServiceNationalBankTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private Task<IReadOnlyDictionary<BncAccount, CommitResult>> PdfThenCsvAsync() => read.GetAsync("pdf then csv", NationalBankSeptember.PdfThenCsvAsync);

    [Fact]
    public async Task Commit_CsvExportAfterThePdfOfTheSameMonth_AddsNothing()
    {
        var csv = await PdfThenCsvAsync();

        Assert.All(csv.Values, r => Assert.Equal(0, r.Added));
        Assert.Equal(3, (await read.StoreAsync("pdf then csv")).Db.Accounts().Count);
    }

    [Fact]
    public async Task Commit_PaymentsBetweenTheAccounts_AreCardPaymentsInvestmentsAndTransfers()
    {
        await PdfThenCsvAsync();

        var all = (await read.StoreAsync("pdf then csv")).Db.AllTransactions();
        Assert.All(all.Where(t => t.CategoryId == Categories.CardPayments), t => Assert.Equal("Credit card payment", t.Brand));
        Assert.Contains(all, t => t.CategoryId == Categories.CardPayments);
        Assert.Equal(Categories.Investments, all.Single(t => t.RawLabel == "Paiement de facture").CategoryId);
        Assert.Equal(2, all.Count(t => t.RawLabel == "Transfert entre comptes" && t.CategoryId == Categories.Transfers));
    }

    [Fact]
    public async Task Commit_LinesOfAnotherAccountOfTheSameDaysAndAmounts_AreNeverTakenForDuplicates()
    {
        using var store = new TestStore();
        var savings = await store.ImportAsync("a.csv", Csv(September, BncAccount.Savings));

        var chequing = await store.ImportIntoAsync(savings.AccountId, "b.csv", Csv(September, BncAccount.Chequing));

        Assert.Equal((September.Chequing.Count(l => l.Amount != 0), 0), (chequing.Added, chequing.SkippedDuplicates));
    }
}
