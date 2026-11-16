using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>The monthly PDF of the Caisse d'Epargne (a French bank), holding the current account and the Livret A, read through the import.</summary>
public sealed class CaisseEpargneImportTests : IDisposable
{
    private static readonly CaisseEpargneGenerator.Statement September = CaisseEpargneGenerator.Sample(2025, 9, 12);

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private readonly long _caisse;

    public CaisseEpargneImportTests() => _caisse = _store.AddBank("Caisse d'Epargne", BankingSystem.France, "EUR", "France");

    private Task<List<ImportDraft>> ReadAsync() => _store.ParseAllAsync("releve.pdf", CaisseEpargneGenerator.Pdf(September), _caisse);

    [Fact]
    public async Task ParseAsync_PdfOfTwoAccounts_GivesOneBalancedStatementInEurosEach()
    {
        var drafts = await ReadAsync();

        Assert.Equal(September.Accounts.Select(a => (VerificationStatus.Verified, a.Opening, a.Closing, "EUR")),
            drafts.Select(d => (d.Parse.Status, d.Parse.OpeningBalance!.Value, d.Parse.ClosingBalance!.Value, d.Parse.Currency)));
        Assert.Equal(September.Accounts[0].Lines.Select(l => (l.Date, l.Amount)), drafts[0].Parse.Rows.Select(r => (r.Date, r.Amount)));
        Assert.Equal(September.Accounts[1].Lines.Select(l => (l.Date, l.Amount)), drafts[1].Parse.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_PdfOfTwoAccounts_NamesEachAfterItsAccountWithItsKindAndNumber()
    {
        var drafts = await ReadAsync();

        Assert.EndsWith("· COMPTE DE DEPOT", drafts[0].FileName, StringComparison.Ordinal);
        Assert.EndsWith("· LIVRET A", drafts[1].FileName, StringComparison.Ordinal);
        Assert.Equal([AccountKind.Bank, AccountKind.Savings], drafts.Select(d => d.Parse.Kind));
        Assert.Equal(September.Accounts.Select(a => a.Number[^4..]), drafts.Select(d => d.Parse.Last4));
    }

    [Fact]
    public async Task ParseAsync_OperationWithASecondLine_KeepsItInTheLabelAndLeavesFeesLinesAndTotalsOut()
    {
        var rows = (await ReadAsync())[0].Parse.Rows;

        Assert.Contains(rows, r => r.Label.Contains("SALAIRE EXEMPLE SARL", StringComparison.Ordinal) && r.Label.Contains("SALAIRE SEPTEMBRE", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Label.Contains("TOTAL DES", StringComparison.Ordinal) || r.Label.Contains("frais bancaires", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_PdfSavedBefore_SaysBothAccountsAreAlreadyImported()
    {
        _store.CommitAll(await ReadAsync(), bank: _caisse);

        var again = await ReadAsync();

        Assert.All(again, d => Assert.True(d.AlreadyImported));
    }
}
