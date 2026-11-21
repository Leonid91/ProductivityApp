using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// BoursoBank (Boursorama Banque before 2024) and Fortuneo, two French online banks: every form of their statements,
/// as printed and exported in 2021-2026 (invented data), read through the import of a bank of the French system in
/// euros: the monthly PDFs of the current account (on several pages, overdrawn, the first one, with a deferred debit
/// card's month in one line), of savings accounts and of deferred debit cards, and every export (CSV, tab-separated,
/// Excel, QIF, OFX).
/// </summary>
public sealed class BoursobankFortuneoImportTests : IDisposable
{
    private static readonly BoursobankGenerator.Card BoursoCard = BoursobankGenerator.DeferredCard();
    private static readonly BoursobankGenerator.Statement Bourso = BoursobankGenerator.Sample(cards: 70, deferred: BoursoCard);
    private static readonly BoursobankGenerator.Statement BoursoramaOverdrawn = BoursobankGenerator.Sample(2022, 3, seed: 3, opening: -215.40m, brand: "Boursorama Banque");
    private static readonly BoursobankGenerator.Statement BoursoSavings = BoursobankGenerator.Savings();

    private static readonly FortuneoGenerator.Card FortuneoCard = FortuneoGenerator.DeferredCard();
    private static readonly FortuneoGenerator.Statement Fortuneo = FortuneoGenerator.Sample(cards: 70, deferred: FortuneoCard);
    private static readonly FortuneoGenerator.Statement FortuneoFirst = FortuneoGenerator.Sample(2021, 4, seed: 4, first: true);
    private static readonly FortuneoGenerator.Statement FortuneoOverdrawn = FortuneoGenerator.Sample(2023, 11, seed: 5, opening: -342.18m);
    private static readonly FortuneoGenerator.Statement FortuneoLivret = FortuneoGenerator.Livret();

    private readonly TestStore _store = new();
    private readonly long _bourso, _fortuneo;

    public BoursobankFortuneoImportTests()
    {
        _bourso = _store.AddBank("BoursoBank", BankingSystem.France, "EUR", "France");
        _fortuneo = _store.AddBank("Fortuneo", BankingSystem.France, "EUR", "France");
    }

    /// <summary>A file read as the person imports it: its bank chosen.</summary>
    private async Task<StatementParse> ReadAsync(string fileName, byte[] bytes) =>
        (await _store.ParseAsync(fileName, bytes, fileName.StartsWith("Fortuneo", StringComparison.Ordinal) || fileName.StartsWith("Historique", StringComparison.Ordinal) ? _fortuneo : _bourso)).Parse;

    public void Dispose() => _store.Dispose();

    private static List<(DateOnly, decimal)> Ops(IEnumerable<(DateOnly Date, decimal Amount)> lines) => [.. lines.Order()];

    private static List<(DateOnly, decimal)> Ops(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).Order()];

    private static List<(DateOnly, decimal)> Ops(BoursobankGenerator.Statement s) => Ops(s.Lines.Select(l => (l.Date, l.Amount)));

    private static List<(DateOnly, decimal)> Ops(FortuneoGenerator.Statement s) => Ops(s.Lines.Select(l => (l.Date, l.Amount)));

    private static List<(DateOnly, decimal)> Ops(BoursobankGenerator.Card c) => Ops(c.Lines.Select(l => (l.Date, l.Kind == "AVOIR" ? l.Euros : -l.Euros)));

    private static List<(DateOnly, decimal)> Ops(FortuneoGenerator.Card c) => Ops(c.Lines.Select(l => (l.Date, l.Amount)));

    /// <summary>Every form: its name, its bytes, the operations it holds, whether it prints balances to check.</summary>
    public static TheoryData<string, byte[], List<(DateOnly, decimal)>, bool> EveryForm => new()
    {
        { "BoursoBank releve 2025-09.pdf", BoursobankGenerator.Pdf(Bourso), Ops(Bourso), true },
        { "Boursorama releve 2022-03.pdf", BoursobankGenerator.Pdf(BoursoramaOverdrawn), Ops(BoursoramaOverdrawn), true },
        { "BoursoBank Livret A 2025-12.pdf", BoursobankGenerator.Pdf(BoursoSavings), Ops(BoursoSavings), true },
        { "BoursoBank releve de carte 2025-09.pdf", BoursobankGenerator.CardPdf(BoursoCard), Ops(BoursoCard), true },
        { "export-operations-30-09-2025.csv", BoursobankGenerator.OldCsv(Bourso), Ops(Bourso), false },
        { "export-operations-30-09-2025_nouveau.csv", BoursobankGenerator.NewCsv(Bourso), Ops(Bourso), true },
        { "export-carte-30-09-2025.csv", BoursobankGenerator.CardCsv(BoursoCard), Ops(BoursoCard), false },
        { "BoursoBank 2025-09.ofx", BoursobankGenerator.Ofx(Bourso), Ops(Bourso), false },
        { "BoursoBank 2025-09.qif", BoursobankGenerator.Qif(Bourso), Ops(Bourso), false },
        { "Fortuneo releve 2026-08.pdf", FortuneoGenerator.Pdf(Fortuneo), Ops(Fortuneo), true },
        { "Fortuneo premier releve 2021-04.pdf", FortuneoGenerator.Pdf(FortuneoFirst), Ops(FortuneoFirst), true },
        { "Fortuneo releve 2023-11.pdf", FortuneoGenerator.Pdf(FortuneoOverdrawn), Ops(FortuneoOverdrawn), true },
        { "Fortuneo Livret+ 2026-08.pdf", FortuneoGenerator.Pdf(FortuneoLivret), Ops(FortuneoLivret), true },
        { "Fortuneo releve de carte 2026-08.pdf", FortuneoGenerator.CardPdf(FortuneoCard), Ops(FortuneoCard), true },
        { "HistoriqueOperations_14806_du_01_08_2026_au_31_08_2026.csv", FortuneoGenerator.Csv(Fortuneo), Ops(Fortuneo), false },
        { "HistoriqueOperations_libre.txt", FortuneoGenerator.Libre(Fortuneo), Ops(Fortuneo), false },
        { "HistoriqueOperations.xls", FortuneoGenerator.Xls(Fortuneo), Ops(Fortuneo), false },
        { "HistoriqueOperations.qif", FortuneoGenerator.Qif(Fortuneo), Ops(Fortuneo), false },
        { "HistoriqueOperations.ofx", FortuneoGenerator.Ofx(Fortuneo), Ops(Fortuneo), false },
    };

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheirStatements_ReadsEveryOperationInEuros(string fileName, byte[] bytes, List<(DateOnly, decimal)> expected, bool balances)
    {
        var parse = await ReadAsync(fileName, bytes);

        Assert.Equal("EUR", parse.Currency);
        Assert.Equal(expected, Ops(parse));
        if (balances) Assert.Equal(VerificationStatus.Verified, parse.Status);
    }

    [Fact]
    public async Task ParseAsync_BoursobankPdfOnSeveralPages_IsBalancedToTheCentWithoutTheBalanceLines()
    {
        var parse = (await ReadAsync("Releve BoursoBank 2025-09.pdf", BoursobankGenerator.Pdf(Bourso)));

        Assert.Equal((Bourso.Opening, Bourso.Closing, AccountKind.Bank), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value, parse.Kind));
        Assert.True(parse.Rows.Select(r => r.Page).Distinct().Count() > 1);
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("A réception", StringComparison.Ordinal));
        Assert.Contains(parse.Rows, r => r.Label.StartsWith("VIR SEPA EXEMPLE SAS", StringComparison.Ordinal) && r.Label.Contains("SALAIRE", StringComparison.Ordinal));
        Assert.Contains(parse.Rows, r => r.Label.StartsWith("Relevé différé Carte", StringComparison.Ordinal) && r.Amount == -BoursoCard.Total);
    }

    [Fact]
    public async Task ParseAsync_FortuneoPdfOnSeveralPages_IsBalancedToTheCentWithoutTheTotalsAndBalances()
    {
        var parse = (await ReadAsync("Releve Fortuneo 2026-08.pdf", FortuneoGenerator.Pdf(Fortuneo)));

        Assert.Equal((Fortuneo.Opening, Fortuneo.Closing, AccountKind.Bank), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value, parse.Kind));
        Assert.True(parse.Rows.Select(r => r.Page).Distinct().Count() > 1);
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("TOTAL DES", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("Émis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_SavingsStatements_AreSavingsAccounts()
    {
        var bourso = (await ReadAsync("BoursoBank Livret A 2025-12.pdf", BoursobankGenerator.Pdf(BoursoSavings)));
        var fortuneo = (await ReadAsync("Fortuneo Livret+ 2026-08.pdf", FortuneoGenerator.Pdf(FortuneoLivret)));

        Assert.Equal((AccountKind.Savings, AccountKind.Savings), (bourso.Kind, fortuneo.Kind));
        Assert.Equal((BoursoSavings.Closing, FortuneoLivret.Closing), (bourso.ClosingBalance!.Value, fortuneo.ClosingBalance!.Value));
    }

    [Fact]
    public async Task ParseAsync_DeferredCardStatements_AddUpToWhatTheCurrentAccountPays()
    {
        var bourso = (await ReadAsync("BoursoBank releve de carte 2025-09.pdf", BoursobankGenerator.CardPdf(BoursoCard)));
        var fortuneo = (await ReadAsync("Fortuneo releve de carte 2026-08.pdf", FortuneoGenerator.CardPdf(FortuneoCard)));

        Assert.Equal((-BoursoCard.Total, -FortuneoCard.Total), (bourso.Rows.Sum(r => r.Amount), fortuneo.Rows.Sum(r => r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_BoursobankCardPaidAbroad_TakesTheAmountInEurosNotInTheCurrencyPaid()
    {
        var rows = (await ReadAsync("BoursoBank releve de carte 2025-09.pdf", BoursobankGenerator.CardPdf(BoursoCard))).Rows;

        Assert.Contains(rows, r => r.Label.Contains("AMAZON SAMPLE LONDON", StringComparison.Ordinal) && r.Amount == -58.35m);
        Assert.DoesNotContain(rows, r => r.Amount == -49.99m);
    }

    [Fact]
    public async Task ParseAsync_NewBoursobankCsvWithTwoColumnsNamedSolde_TakesTheFirstAsTheAmountAndTheSecondAsTheBalance()
    {
        var parse = (await ReadAsync("export.csv", BoursobankGenerator.NewCsv(Bourso)));

        Assert.Equal((Bourso.Opening, Bourso.Closing), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value));
    }
}
