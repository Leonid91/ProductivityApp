using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using ProductivityApp.Finance.Text;
using static ProductivityApp.Finance.Testing.BncGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>The National Bank of Canada's PDF histories and CSV exports (invented data in its layouts), read and saved.</summary>
public sealed class NationalBankImportTests : IDisposable
{
    private static readonly Month September = Sample(2025, 9, 31);

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static async Task<StatementParse> ReadPdfAsync(BncAccount account) =>
        StatementParser.Parse(await PdfReader.ReadAsync(BncGenerator.Pdf(September, account), TestContext.Current.CancellationToken), null, "CAD");

    [Theory]
    [InlineData(BncAccount.Card)]
    [InlineData(BncAccount.Chequing)]
    [InlineData(BncAccount.Savings)]
    public async Task Parse_PdfHistory_ReadsEveryLineAsWrittenNewestFirst(BncAccount account)
    {
        var result = await ReadPdfAsync(account);

        var expected = September.Of(account).Where(l => l.Amount != 0).ToList();
        Assert.Equal(expected.Select(l => (l.Date, l.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.Equal(expected.Select(l => TextNorm.Fold(l.Description)), result.Rows.Select(r => TextNorm.Fold(r.Label)));
        Assert.Equal(expected.Select(l => TextNorm.Fold(l.Category)), result.Rows.Select(r => TextNorm.Fold(r.BankCategory)));
        Assert.Equal(("National Bank of Canada", "bnc", "CAD", true), (result.Bank, result.Profile, result.Currency, result.NewestFirst));
    }

    [Theory]
    [InlineData(BncAccount.Card, AccountKind.CreditCard, VerificationStatus.NoBalances)]
    [InlineData(BncAccount.Chequing, AccountKind.Bank, VerificationStatus.Verified)]
    [InlineData(BncAccount.Savings, AccountKind.Savings, VerificationStatus.Verified)]
    public async Task Parse_PdfHistory_ReturnsTheKindOfAccountAndTheBalanceCheck(BncAccount account, AccountKind kind, VerificationStatus status)
    {
        var result = await ReadPdfAsync(account);

        Assert.Equal((kind, status), (result.Kind, result.Status));
    }

    [Fact]
    public async Task Parse_CardPdfHistory_ReadsTheCardsLastFourDigits()
    {
        var result = await ReadPdfAsync(BncAccount.Card);

        Assert.Equal("1234", result.Last4);
    }

    [Fact]
    public async Task Parse_ChequingPdfWithWaivedFees_LeavesTheLinesOfZeroOut()
    {
        var result = await ReadPdfAsync(BncAccount.Chequing);

        Assert.Equal(2, result.ZeroRowsSkipped);
    }

    /// <summary>The PDF of each of the three accounts saved into a new account each, then their CSV exports into the same accounts.</summary>
    private async Task<IReadOnlyDictionary<BncAccount, CommitResult>> PdfThenCsvAsync()
    {
        var kinds = new Dictionary<BncAccount, AccountKind> { [BncAccount.Card] = AccountKind.CreditCard, [BncAccount.Chequing] = AccountKind.Bank, [BncAccount.Savings] = AccountKind.Savings };
        var accounts = new Dictionary<BncAccount, long>();
        foreach (var (account, kind) in kinds)
            accounts[account] = (await _store.ImportAsync(FileName(account, "pdf"), BncGenerator.Pdf(September, account), kind: kind)).AccountId;
        var csv = new Dictionary<BncAccount, CommitResult>();
        foreach (var account in kinds.Keys)
            csv[account] = await _store.ImportAsync(FileName(account, "csv"), Csv(September, account), account: accounts[account]);
        return csv;
    }

    [Fact]
    public async Task ImportAsync_CsvExportAfterThePdfOfTheSameMonth_AddsNothing()
    {
        var csv = await PdfThenCsvAsync();

        Assert.All(csv.Values, r => Assert.Equal(0, r.Added));
        Assert.Equal(3, _store.Db.Accounts().Count);
    }

    [Fact]
    public async Task Summarize_MonthOfACardAChequingAndASavingsAccount_CountsTheSpendingAndIncomeOnce()
    {
        await PdfThenCsvAsync();

        var month = _store.Summarize(new(2025, 9, 1), new(2025, 9, 30));

        Assert.Equal(-September.Card.Where(l => l.Amount < 0).Sum(l => l.Amount) + 9.95m, month.Expenses);
        Assert.Equal(2 * 2310.45m + 3.17m, month.Earnings);
    }

    [Fact]
    public async Task ImportAsync_PaymentsBetweenTheAccounts_AreCardPaymentsInvestmentsAndTransfers()
    {
        await PdfThenCsvAsync();

        var all = _store.Db.AllTransactions();
        Assert.All(all.Where(t => t.CategoryId == Categories.CardPayments), t => Assert.Equal("Credit card payment", t.Brand));
        Assert.Contains(all, t => t.CategoryId == Categories.CardPayments);
        Assert.Equal(Categories.Investments, all.Single(t => t.RawLabel == "Paiement de facture").CategoryId);
        Assert.Equal(2, all.Count(t => t.RawLabel == "Transfert entre comptes" && t.CategoryId == Categories.Transfers));
    }

    [Fact]
    public async Task ImportAsync_LinesOfAnotherAccountOfTheSameDaysAndAmounts_AreNeverTakenForDuplicates()
    {
        var savings = await _store.ImportAsync("a.csv", Csv(September, BncAccount.Savings));

        var chequing = await _store.ImportAsync("b.csv", Csv(September, BncAccount.Chequing), account: savings.AccountId);

        Assert.Equal((September.Chequing.Count(l => l.Amount != 0), 0), (chequing.Added, chequing.SkippedDuplicates));
    }

    [Fact]
    public void Link_SavingsMovementWithoutAnyTellingWording_PairsBothHalvesAsATransfer()
    {
        var chequing = _store.AddAccount("Chequing");
        var savings = _store.AddAccount("Savings", kind: AccountKind.Savings);
        var sent = _store.Db.AddTransaction(new Transaction { AccountId = chequing, Date = new(2025, 9, 3), Amount = -300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "other" });
        var received = _store.Db.AddTransaction(new Transaction { AccountId = savings, Date = new(2025, 9, 4), Amount = 300m, Currency = "CAD", RawLabel = "WEB 0042", Brand = "Web", CategoryId = "otherincome" });

        var linked = TransferMatcher.Link(_store.Db);

        Assert.Equal(2, linked);
        Assert.Equal(Categories.Transfers, _store.Db.GetTransaction(sent)!.CategoryId);
        Assert.Equal(ValueSource.Linked, _store.Db.GetTransaction(received)!.CategorySource);
    }
}
