using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The monthly PDF of the Caisse d'Epargne (a French bank), holding the current account and the Livret A, read through
/// the import. Read once for the tests that only read it.
/// </summary>
public sealed class ImportServiceCaisseEpargneTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private static readonly CaisseEpargneGenerator.Statement September = CaisseEpargneGenerator.Sample(2025, 9, 12);
    private static readonly Lazy<byte[]> Pdf = new(() => CaisseEpargneGenerator.Pdf(September));

    private static long AddCaisse(TestStore store) => store.AddBank("Caisse d'Epargne", BankingSystem.France, "EUR", "France");

    /// <summary>The statements of both accounts, read once.</summary>
    private Task<List<ImportDraft>> ReadAsync() => read.GetAsync("releve.pdf", store => store.ParseAllAsync("releve.pdf", Pdf.Value, AddCaisse(store)));

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
        using var store = new TestStore();
        var caisse = AddCaisse(store);
        store.CommitAll(await store.ParseAllAsync("releve.pdf", Pdf.Value, caisse), bank: caisse);

        var again = await store.ParseAllAsync("releve.pdf", Pdf.Value, caisse);

        Assert.All(again, d => Assert.True(d.AlreadyImported));
    }
}
