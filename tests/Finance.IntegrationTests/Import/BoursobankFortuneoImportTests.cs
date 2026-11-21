using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// BoursoBank and Fortuneo, two French online banks: their monthly PDF statements and their CSV exports, as printed
/// and exported in 2021-2026 (invented data), read through the import of a bank of the French system in euros.
/// </summary>
public sealed class BoursobankFortuneoImportTests : IDisposable
{
    private static readonly BoursobankGenerator.Statement Bourso = BoursobankGenerator.Sample();
    private static readonly FortuneoGenerator.Statement Fortuneo = FortuneoGenerator.Sample();

    private readonly TestStore _store = new();

    public BoursobankFortuneoImportTests()
    {
        _store.AddBank("BoursoBank", BankingSystem.France, "EUR", "France");
        _store.AddBank("Fortuneo", BankingSystem.France, "EUR", "France");
    }

    public void Dispose() => _store.Dispose();

    private static List<(DateOnly, decimal)> Ops(IEnumerable<(DateOnly Date, decimal Amount)> lines) => [.. lines.Order()];

    private static List<(DateOnly, decimal)> Ops(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).Order()];

    public static TheoryData<string, byte[]> BoursoForms => new()
    {
        { "Releve BoursoBank 2025-09.pdf", BoursobankGenerator.Pdf(Bourso) },
        { "export-operations-30-09-2025.csv", BoursobankGenerator.OldCsv(Bourso) },
        { "export-operations-30-09-2025_nouveau.csv", BoursobankGenerator.NewCsv(Bourso) },
    };

    public static TheoryData<string, byte[]> FortuneoForms => new()
    {
        { "Releve Fortuneo 2026-08.pdf", FortuneoGenerator.Pdf(Fortuneo) },
        { "HistoriqueOperations_14806_du_01_08_2026_au_31_08_2026.csv", FortuneoGenerator.Csv(Fortuneo) },
    };

    [Theory]
    [MemberData(nameof(BoursoForms))]
    public async Task ParseAsync_AnyFormOfABoursobankStatement_ReadsEveryOperationInEuros(string fileName, byte[] bytes)
    {
        var parse = (await _store.ParseAsync(fileName, bytes)).Parse;

        Assert.Equal("EUR", parse.Currency);
        Assert.Equal(Ops(Bourso.Lines.Select(l => (l.Date, l.Amount))), Ops(parse));
    }

    [Theory]
    [MemberData(nameof(FortuneoForms))]
    public async Task ParseAsync_AnyFormOfAFortuneoStatement_ReadsEveryOperationInEuros(string fileName, byte[] bytes)
    {
        var parse = (await _store.ParseAsync(fileName, bytes)).Parse;

        Assert.Equal("EUR", parse.Currency);
        Assert.Equal(Ops(Fortuneo.Lines.Select(l => (l.Date, l.Amount))), Ops(parse));
    }

    [Fact]
    public async Task ParseAsync_BoursobankPdf_IsBalancedToTheCentWithoutTheBalanceLines()
    {
        var parse = (await _store.ParseAsync("Releve BoursoBank 2025-09.pdf", BoursobankGenerator.Pdf(Bourso))).Parse;

        Assert.Equal((VerificationStatus.Verified, AccountKind.Bank), (parse.Status, parse.Kind));
        Assert.Equal((Bourso.Opening, Bourso.Closing), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value));
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("A réception", StringComparison.Ordinal));
        Assert.Contains(parse.Rows, r => r.Label.StartsWith("VIR SEPA EXEMPLE SAS", StringComparison.Ordinal) && r.Label.Contains("SALAIRE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_FortuneoPdf_IsBalancedToTheCentWithoutTheTotalsAndBalances()
    {
        var parse = (await _store.ParseAsync("Releve Fortuneo 2026-08.pdf", FortuneoGenerator.Pdf(Fortuneo))).Parse;

        Assert.Equal((VerificationStatus.Verified, AccountKind.Bank), (parse.Status, parse.Kind));
        Assert.Equal((Fortuneo.Opening, Fortuneo.Closing), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value));
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("TOTAL DES", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("Émis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_NewBoursobankCsvWithTwoColumnsNamedSolde_TakesTheFirstAsTheAmountAndTheSecondAsTheBalance()
    {
        var parse = (await _store.ParseAsync("export.csv", BoursobankGenerator.NewCsv(Bourso))).Parse;

        Assert.Equal(VerificationStatus.Verified, parse.Status);
        Assert.Equal((Bourso.Opening, Bourso.Closing), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value));
    }
}
