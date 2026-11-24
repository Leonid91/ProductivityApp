using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// BoursoBank (Boursorama Banque before 2024) and Fortuneo, two French online banks: every form of their statements,
/// as printed and exported in 2021-2026 (invented data), read through the import of a bank of the French system in
/// euros: the monthly PDFs of the current account (on several pages, overdrawn, the first one, with a deferred debit
/// card's month in one line), of savings accounts and of deferred debit cards, and every export (CSV, tab-separated,
/// Excel, QIF, OFX). Each file is read once; every test reads that reading.
/// </summary>
public sealed class ImportServiceBoursobankFortuneoTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private const string Boursobank = "BoursoBank", Fortuneo = "Fortuneo";

    private static readonly BoursobankGenerator.Card BoursoCard = BoursobankGenerator.DeferredCard();
    private static readonly BoursobankGenerator.Statement Bourso = BoursobankGenerator.Sample(cards: 70, deferred: BoursoCard);
    private static readonly BoursobankGenerator.Statement BoursoramaOverdrawn = BoursobankGenerator.Sample(2022, 3, seed: 3, opening: -215.40m, brand: "Boursorama Banque");
    private static readonly BoursobankGenerator.Statement BoursoSavings = BoursobankGenerator.Savings();

    private static readonly FortuneoGenerator.Card FortuneoCard = FortuneoGenerator.DeferredCard();
    private static readonly FortuneoGenerator.Statement FortuneoCurrent = FortuneoGenerator.Sample(cards: 70, deferred: FortuneoCard);
    private static readonly FortuneoGenerator.Statement FortuneoFirst = FortuneoGenerator.Sample(2021, 4, seed: 4, first: true);
    private static readonly FortuneoGenerator.Statement FortuneoOverdrawn = FortuneoGenerator.Sample(2023, 11, seed: 5, opening: -342.18m);
    private static readonly FortuneoGenerator.Statement FortuneoLivret = FortuneoGenerator.Livret();

    private static List<(DateOnly, decimal)> Ops(IEnumerable<(DateOnly Date, decimal Amount)> lines) => [.. lines.Order()];

    private static List<(DateOnly, decimal)> Ops(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).Order()];

    private static List<(DateOnly, decimal)> Ops(BoursobankGenerator.Statement s) => Ops(s.Lines.Select(l => (l.Date, l.Amount)));

    private static List<(DateOnly, decimal)> Ops(FortuneoGenerator.Statement s) => Ops(s.Lines.Select(l => (l.Date, l.Amount)));

    /// <summary>A card's lines: what was paid out, and the refunds ("AVOIR") in.</summary>
    private static readonly Dictionary<string, int> CardSign = new() { ["AVOIR"] = 1 };

    private static List<(DateOnly, decimal)> Ops(BoursobankGenerator.Card c) => Ops(c.Lines.Select(l => (l.Date, CardSign.GetValueOrDefault(l.Kind, -1) * l.Euros)));

    private static List<(DateOnly, decimal)> Ops(FortuneoGenerator.Card c) => Ops(c.Lines.Select(l => (l.Date, l.Amount)));

    /// <summary>Every form by its file's name: the bank it is read at, its bytes, the operations it holds, whether it prints balances to check.</summary>
    private static readonly Dictionary<string, (string Bank, Func<byte[]> Bytes, List<(DateOnly, decimal)> Operations, bool Balances)> Forms = new()
    {
        ["BoursoBank releve 2025-09.pdf"] = (Boursobank, () => BoursobankGenerator.Pdf(Bourso), Ops(Bourso), true),
        ["Boursorama releve 2022-03.pdf"] = (Boursobank, () => BoursobankGenerator.Pdf(BoursoramaOverdrawn), Ops(BoursoramaOverdrawn), true),
        ["BoursoBank Livret A 2025-12.pdf"] = (Boursobank, () => BoursobankGenerator.Pdf(BoursoSavings), Ops(BoursoSavings), true),
        ["BoursoBank releve de carte 2025-09.pdf"] = (Boursobank, () => BoursobankGenerator.CardPdf(BoursoCard), Ops(BoursoCard), true),
        ["export-operations-30-09-2025.csv"] = (Boursobank, () => BoursobankGenerator.OldCsv(Bourso), Ops(Bourso), false),
        ["export-operations-30-09-2025_nouveau.csv"] = (Boursobank, () => BoursobankGenerator.NewCsv(Bourso), Ops(Bourso), true),
        ["export-carte-30-09-2025.csv"] = (Boursobank, () => BoursobankGenerator.CardCsv(BoursoCard), Ops(BoursoCard), false),
        ["BoursoBank 2025-09.ofx"] = (Boursobank, () => BoursobankGenerator.Ofx(Bourso), Ops(Bourso), false),
        ["BoursoBank 2025-09.qif"] = (Boursobank, () => BoursobankGenerator.Qif(Bourso), Ops(Bourso), false),
        ["Fortuneo releve 2026-08.pdf"] = (Fortuneo, () => FortuneoGenerator.Pdf(FortuneoCurrent), Ops(FortuneoCurrent), true),
        ["Fortuneo premier releve 2021-04.pdf"] = (Fortuneo, () => FortuneoGenerator.Pdf(FortuneoFirst), Ops(FortuneoFirst), true),
        ["Fortuneo releve 2023-11.pdf"] = (Fortuneo, () => FortuneoGenerator.Pdf(FortuneoOverdrawn), Ops(FortuneoOverdrawn), true),
        ["Fortuneo Livret+ 2026-08.pdf"] = (Fortuneo, () => FortuneoGenerator.Pdf(FortuneoLivret), Ops(FortuneoLivret), true),
        ["Fortuneo releve de carte 2026-08.pdf"] = (Fortuneo, () => FortuneoGenerator.CardPdf(FortuneoCard), Ops(FortuneoCard), true),
        ["HistoriqueOperations_14806_du_01_08_2026_au_31_08_2026.csv"] = (Fortuneo, () => FortuneoGenerator.Csv(FortuneoCurrent), Ops(FortuneoCurrent), false),
        ["HistoriqueOperations_libre.txt"] = (Fortuneo, () => FortuneoGenerator.Libre(FortuneoCurrent), Ops(FortuneoCurrent), false),
        ["HistoriqueOperations.xls"] = (Fortuneo, () => FortuneoGenerator.Xls(FortuneoCurrent), Ops(FortuneoCurrent), false),
        ["HistoriqueOperations.qif"] = (Fortuneo, () => FortuneoGenerator.Qif(FortuneoCurrent), Ops(FortuneoCurrent), false),
        ["HistoriqueOperations.ofx"] = (Fortuneo, () => FortuneoGenerator.Ofx(FortuneoCurrent), Ops(FortuneoCurrent), false),
        // The same files under other names, as a person may save them.
        ["Releve BoursoBank 2025-09.pdf"] = (Boursobank, () => BoursobankGenerator.Pdf(Bourso), Ops(Bourso), true),
        ["Releve Fortuneo 2026-08.pdf"] = (Boursobank, () => FortuneoGenerator.Pdf(FortuneoCurrent), Ops(FortuneoCurrent), true),
        ["export.csv"] = (Boursobank, () => BoursobankGenerator.NewCsv(Bourso), Ops(Bourso), true),
    };

    /// <summary>A file read as the person imports it, its bank chosen.</summary>
    private Task<StatementParse> ReadAsync(string fileName) => read.GetAsync(fileName, async store =>
    {
        var (bank, bytes, _, _) = Forms[fileName];
        var id = store.AddBank(bank, BankingSystem.France, "EUR", "France");
        return (await store.ParseAsync(fileName, bytes(), id)).Parse;
    });

    public static TheoryData<string> EveryForm => [.. Forms.Keys];

    public static TheoryData<string> FormsPrintingBalances => [.. Forms.Where(f => f.Value.Balances).Select(f => f.Key)];

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheirStatements_ReadsEveryOperationInEuros(string fileName)
    {
        var parse = await ReadAsync(fileName);

        Assert.Equal("EUR", parse.Currency);
        Assert.Equal(Forms[fileName].Operations, Ops(parse));
    }

    [Theory]
    [MemberData(nameof(FormsPrintingBalances))]
    public async Task ParseAsync_AnyFormPrintingBalances_IsBalancedToTheCent(string fileName)
    {
        var parse = await ReadAsync(fileName);

        Assert.Equal(VerificationStatus.Verified, parse.Status);
    }

    [Fact]
    public async Task ParseAsync_BoursobankPdfOnSeveralPages_IsBalancedToTheCentWithoutTheBalanceLines()
    {
        var parse = await ReadAsync("Releve BoursoBank 2025-09.pdf");

        Assert.Equal((Bourso.Opening, Bourso.Closing, AccountKind.Bank), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value, parse.Kind));
        Assert.True(parse.Rows.Select(r => r.Page).Distinct().Count() > 1);
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("A réception", StringComparison.Ordinal));
        Assert.Contains(parse.Rows, r => r.Label.StartsWith("VIR SEPA EXEMPLE SAS", StringComparison.Ordinal) && r.Label.Contains("SALAIRE", StringComparison.Ordinal));
        Assert.Contains(parse.Rows, r => r.Label.StartsWith("Relevé différé Carte", StringComparison.Ordinal) && r.Amount == -BoursoCard.Total);
    }

    [Fact]
    public async Task ParseAsync_FortuneoPdfOnSeveralPages_IsBalancedToTheCentWithoutTheTotalsAndBalances()
    {
        var parse = await ReadAsync("Releve Fortuneo 2026-08.pdf");

        Assert.Equal((FortuneoCurrent.Opening, FortuneoCurrent.Closing, AccountKind.Bank), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value, parse.Kind));
        Assert.True(parse.Rows.Select(r => r.Page).Distinct().Count() > 1);
        Assert.DoesNotContain(parse.Rows, r => r.Label.Contains("SOLDE", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("TOTAL DES", StringComparison.OrdinalIgnoreCase) || r.Label.StartsWith("Émis", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BoursoBank Livret A 2025-12.pdf")]
    [InlineData("Fortuneo Livret+ 2026-08.pdf")]
    public async Task ParseAsync_SavingsStatement_IsASavingsAccountWithItsClosingBalance(string fileName)
    {
        var parse = await ReadAsync(fileName);

        Assert.Equal((AccountKind.Savings, SavingsClosing[fileName]), (parse.Kind, parse.ClosingBalance!.Value));
    }

    private static readonly Dictionary<string, decimal> SavingsClosing = new()
    {
        ["BoursoBank Livret A 2025-12.pdf"] = BoursoSavings.Closing,
        ["Fortuneo Livret+ 2026-08.pdf"] = FortuneoLivret.Closing,
    };

    [Theory]
    [InlineData("BoursoBank releve de carte 2025-09.pdf")]
    [InlineData("Fortuneo releve de carte 2026-08.pdf")]
    public async Task ParseAsync_DeferredCardStatement_AddsUpToWhatTheCurrentAccountPays(string fileName)
    {
        var parse = await ReadAsync(fileName);

        Assert.Equal(-CardTotal[fileName], parse.Rows.Sum(r => r.Amount));
    }

    private static readonly Dictionary<string, decimal> CardTotal = new()
    {
        ["BoursoBank releve de carte 2025-09.pdf"] = BoursoCard.Total,
        ["Fortuneo releve de carte 2026-08.pdf"] = FortuneoCard.Total,
    };

    [Fact]
    public async Task ParseAsync_BoursobankCardPaidAbroad_TakesTheAmountInEurosNotInTheCurrencyPaid()
    {
        var rows = (await ReadAsync("BoursoBank releve de carte 2025-09.pdf")).Rows;

        Assert.Contains(rows, r => r.Label.Contains("AMAZON SAMPLE LONDON", StringComparison.Ordinal) && r.Amount == -58.35m);
        Assert.DoesNotContain(rows, r => r.Amount == -49.99m);
    }

    [Fact]
    public async Task ParseAsync_NewBoursobankCsvWithTwoColumnsNamedSolde_TakesTheFirstAsTheAmountAndTheSecondAsTheBalance()
    {
        var parse = await ReadAsync("export.csv");

        Assert.Equal((Bourso.Opening, Bourso.Closing), (parse.OpeningBalance!.Value, parse.ClosingBalance!.Value));
    }
}
