using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// An online bank holding several currencies (Revolut's layouts, invented data): the export of the app, where one
/// file holds every currency and product, dates carry their time, the fee has a column of its own and pending,
/// declined and reverted operations are listed though they never moved money; and the PDF statement of a currency.
/// </summary>
public sealed class RevolutImportTests : IDisposable
{
    private static readonly List<RevolutGenerator.Account> Accounts = RevolutGenerator.Sample();
    private static readonly RevolutGenerator.Account Euros = Accounts.Single(a => a.Currency == "EUR" && a.Product == "Current");

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>What an account really moved: each completed operation, then its fee as a line of its own.</summary>
    private static List<(DateOnly, decimal)> Moves(RevolutGenerator.Account a) =>
        [.. a.Done.SelectMany(l => new[] { (l.Date, l.Amount), (l.Date, -l.Fee) }).Where(x => x.Item2 != 0)];

    private async Task<List<StatementParse>> ReadAsync(string fileName, byte[] bytes) => [.. (await _store.ParseAllAsync(fileName, bytes)).Select(d => d.Parse)];

    private static StatementParse Part(List<StatementParse> parts, RevolutGenerator.Account a) =>
        parts.Single(x => x.Currency == a.Currency && (x.Kind == AccountKind.Savings) == (a.Product == "Savings"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseAsync_ExportOfEveryCurrencyInEnglishOrFrench_GivesOneStatementPerAccount(bool french)
    {
        var parts = await ReadAsync("account-statement_2026-01-01_2026-01-31.csv", RevolutGenerator.Csv(Accounts, french));

        Assert.Equal(Accounts.Select(a => (a.Currency, a.Product == "Savings")).Order(), parts.Select(p => (p.Currency, p.Kind == AccountKind.Savings)).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseAsync_ExportOfEveryCurrency_BalancesEachAccountToTheCentWithItsFeesAsLinesOfTheirOwn(bool french)
    {
        var parts = await ReadAsync("account-statement_2026-01-01_2026-01-31.csv", RevolutGenerator.Csv(Accounts, french));

        Assert.All(Accounts, a => Assert.Equal((VerificationStatus.Verified, a.Opening, a.Closing),
            (Part(parts, a).Status, Part(parts, a).OpeningBalance!.Value, Part(parts, a).ClosingBalance!.Value)));
        Assert.All(Accounts, a => Assert.Equal(Moves(a).Order(), Part(parts, a).Rows.Select(r => (r.Date, r.Amount)).Order()));
    }

    [Fact]
    public async Task ParseAsync_ExportWithPendingDeclinedAndRevertedOperations_LeavesThemOut()
    {
        var parts = await ReadAsync("revolut.csv", RevolutGenerator.Csv(Accounts));

        Assert.All(parts, p => Assert.DoesNotContain(p.Rows, r => r.Label.Contains("Hotel Imaginaire") || r.Label.Contains("Librairie Fictive") || r.Label.Contains("Sample Diner")));
        Assert.All(parts, p => Assert.DoesNotContain(p.Rows, r => r.Amount == 0));
    }

    [Fact]
    public async Task ParseAsync_ExportDatesWithTheirTime_KeepTheDayTheOperationWasMadeAndNameItsFee()
    {
        var rows = Part(await ReadAsync("revolut.csv", RevolutGenerator.Csv(Accounts)), Euros).Rows;

        Assert.Contains(rows, r => (r.Label, r.Amount, r.Date) == ("Cash at Example Bank ATM", -200m, new DateOnly(2026, 1, 7)));
        Assert.Contains(rows, r => r.Label.EndsWith(": Cash at Example Bank ATM", StringComparison.Ordinal) && r.Amount == -4m);
        Assert.Contains(rows, r => r.Label.EndsWith(": Premium plan fee", StringComparison.Ordinal) && r.Amount == -7.99m);
    }

    [Fact]
    public async Task Commit_EveryAccountOfTheExport_SavesEachInItsCurrencyAndChoosingTheKindKeepsItsOwnLines()
    {
        var bank = _store.AddBank("Revolut", BankingSystem.France, "EUR", "France");
        var drafts = await _store.ParseAllAsync("revolut.csv", RevolutGenerator.Csv(Accounts));

        _store.CommitAll(drafts, bank: bank);

        Assert.All(drafts, d => Assert.Equal(VerificationStatus.Verified, d.Parse.Status));
        Assert.Equal(["EUR", "EUR", "USD"], _store.Db.Accounts().Where(a => a.BankId == bank).Select(a => a.Currency).Order());
    }

    [Fact]
    public async Task ParseAsync_ExportSavedBefore_SaysEveryAccountIsAlreadyImported()
    {
        _store.CommitAll(await _store.ParseAllAsync("revolut.csv", RevolutGenerator.Csv(Accounts)), bank: _store.AddBank("Revolut", BankingSystem.France, "EUR"));

        var again = await _store.ParseAllAsync("revolut.csv", RevolutGenerator.Csv(Accounts));

        Assert.All(again, d => Assert.True(d.AlreadyImported));
    }

    [Fact]
    public async Task Report_MoneyPutInThePocket_IsOneMovementInsideTheBank()
    {
        _store.CommitAll(await _store.ParseAllAsync("revolut.csv", RevolutGenerator.Csv(Accounts)), bank: _store.AddBank("Revolut", BankingSystem.France, "EUR"));

        var report = _store.Summaries.Report(new(2026, 1, 1), new(2026, 1, 31), Granularity.Month, "EUR", new ExchangeRates(_store.Db));

        Assert.Contains(report.Global.Movements, m => !m.FromOutside && !m.ToOutside && m.Amount == 300m);
    }

    public static TheoryData<string> CurrentAccountCurrencies => [.. Accounts.Where(a => a.Product == "Current").Select(a => a.Currency)];

    [Theory]
    [MemberData(nameof(CurrentAccountCurrencies))]
    public async Task ParseAsync_PdfStatementOfACurrency_ReadsItToTheCentWithTheFeeAfterItsOperation(string currency)
    {
        var account = Accounts.Single(a => a.Currency == currency && a.Product == "Current");

        var result = (await ReadAsync($"{currency} statement.pdf", RevolutGenerator.Pdf(account))).Single();

        Assert.Equal((VerificationStatus.Verified, currency, "Revolut", AccountKind.Bank), (result.Status, result.Currency, result.Bank, result.Kind));
        Assert.Equal((account.Opening, account.Closing), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
        Assert.Equal(Moves(account), result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_PdfStatement_LeavesTheFeeNoteAndRevertedOperationsOut()
    {
        var rows = (await ReadAsync("EUR statement.pdf", RevolutGenerator.Pdf(Euros))).Single().Rows;

        Assert.DoesNotContain(rows, r => r.Label.Contains("€4.00") || r.Label.Contains("$0.33") || r.Label.Contains("Hotel Imaginaire"));
    }

    public static TheoryData<string, byte[]> ExportAndPdf => new()
    {
        { "revolut.csv", RevolutGenerator.Csv(Accounts) },
        { "EUR statement.pdf", RevolutGenerator.Pdf(Euros) },
    };

    [Theory]
    [MemberData(nameof(ExportAndPdf))]
    public async Task ParseAsync_ExchangesPocketsAndTopUps_AreMoneyMovedNotSpentOrEarned(string fileName, byte[] bytes)
    {
        var rows = (await _store.ParseAsync(fileName, bytes)).Rows;

        var moved = rows.Where(r => r.Label.StartsWith("Exchanged to USD", StringComparison.Ordinal) || r.Label == "To pocket Holidays" || r.Label.StartsWith("Apple Pay Top-Up", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, moved.Count);
        Assert.All(moved, r => Assert.True(Categories.IsTransfer(r.CategoryId), $"{r.Label}: {r.CategoryId}"));
    }

    [Theory]
    [MemberData(nameof(ExportAndPdf))]
    public async Task ParseAsync_WhatTheBankTookAndCashTakenOut_AreFeesAndCash(string fileName, byte[] bytes)
    {
        var rows = (await _store.ParseAsync(fileName, bytes)).Rows;

        Assert.All(rows.Where(r => r.Amount is -4m or -2.5m or -7.99m), r => Assert.Equal("fees", r.CategoryId));
        Assert.Equal("cash", rows.Single(r => r.Amount == -200m).CategoryId);
        Assert.Equal("groceries", rows.First(r => r.Label.StartsWith("Lidl", StringComparison.Ordinal)).CategoryId);
    }

    [Fact]
    public async Task ParseAsync_ExportNamingAPaymentToAPersonATransfer_MakesItATransfer()
    {
        var rows = (await _store.ParseAsync("revolut.csv", RevolutGenerator.Csv(Accounts))).Rows;

        Assert.All(rows.Where(r => r.Label.StartsWith("To Jordan Example", StringComparison.Ordinal)), r => Assert.True(Categories.IsTransfer(r.CategoryId)));
    }

    [Fact]
    public async Task ParseAsync_BusinessAccountExport_UsesTheAmountPaidNotTheOneBeforeConversion()
    {
        var result = (await ReadAsync("transaction-statement.csv", RevolutGenerator.BusinessCsv(Euros))).Single();

        Assert.Equal((VerificationStatus.Verified, "EUR"), (result.Status, result.Currency));
        Assert.Equal(Moves(Euros).Order(), result.Rows.Select(r => (r.Date, r.Amount)).Order());
    }

    [Fact]
    public async Task ParseAsync_OneAccountWithAColumnNamingWhoWasPaid_StaysOneStatement()
    {
        const string csv = "Date,Description,Beneficiary account,Amount,Currency,Balance\n2026-03-01 08:00,Rent,FR76 0000 1111,-700.00,EUR,300.00\n" +
            "2026-03-02T09:30:00Z,Phone,FR76 0000 2222,-20.00,EUR,280.00\n03/03/2026 10:15,Salary,,2000.00,EUR,2280.00\n";

        var parts = await ReadAsync("export.csv", Encoding.UTF8.GetBytes(csv));

        var only = Assert.Single(parts);
        Assert.Equal(VerificationStatus.Verified, only.Status);
        Assert.Equal([new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3)], only.Rows.Select(r => r.Date));
    }
}
