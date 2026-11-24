using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Statements and exports in the layouts of a Scotiabank chequing account and Visa card (invented data), read through
/// the import. Each file is read once; every test reads that reading.
/// </summary>
public sealed class ImportServiceScotiabankTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private static readonly ScotiaGenerator.Statement Chequing = ScotiaGenerator.Chequing();
    private static readonly ScotiaGenerator.Statement EStatement = ScotiaGenerator.EStatement();
    private static readonly ScotiaGenerator.Card Visa = ScotiaGenerator.VisaCard();

    /// <summary>Every file by its name.</summary>
    private static readonly Dictionary<string, Func<byte[]>> Files = new()
    {
        ["chequing.pdf"] = () => ScotiaGenerator.ChequingPdf(Chequing),
        ["August 2026 e-statement.pdf"] = () => ScotiaGenerator.EStatementPdf(EStatement),
        ["Scotia new.csv"] = () => ScotiaGenerator.NewCsv(Chequing),
        ["Scotia old.csv"] = () => ScotiaGenerator.OldCsv(Chequing),
        ["visa.pdf"] = () => ScotiaGenerator.CardPdf(Visa),
    };

    private Task<StatementParse> ReadAsync(string fileName) =>
        read.GetAsync(fileName, async store => (await store.ParseAsync(fileName, Files[fileName]())).Parse);

    [Fact]
    public async Task ParseAsync_ChequingPdf_ReadsEveryOperationBalancedToTheCent()
    {
        var result = await ReadAsync("chequing.pdf");

        Assert.Equal((VerificationStatus.Verified, AccountKind.Bank, Chequing.Lines.Count), (result.Status, result.Kind, result.Rows.Count));
        Assert.Equal((Chequing.Opening, Chequing.Closing), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
        Assert.Equal((-Chequing.Withdrawals, Chequing.Deposits), (result.Rows.Where(r => r.Amount < 0).Sum(r => r.Amount), result.Rows.Where(r => r.Amount > 0).Sum(r => r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_EStatement2026_ReadsEveryOperationBalancedToTheCent()
    {
        var result = await ReadAsync("August 2026 e-statement.pdf");

        Assert.Equal((VerificationStatus.Verified, AccountKind.Bank, EStatement.Lines.Count), (result.Status, result.Kind, result.Rows.Count));
        Assert.Equal((EStatement.Opening, EStatement.Closing), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
        Assert.Equal(EStatement.Lines.Select(l => (l.Date, l.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_EStatement2026_ReadsAmountsUnderOneDollarWrittenWithoutTheirZero()
    {
        var result = await ReadAsync("August 2026 e-statement.pdf");

        Assert.Contains(result.Rows, r => r.Amount == -0.50m && r.Label.StartsWith("Service charge", StringComparison.Ordinal));
        Assert.Contains(result.Rows, r => r.Amount == 0.42m && r.Label.StartsWith("Interest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_ChequingPdfWithTwoLinesOfDescription_JoinsTheKindOfOperationAndTheShop()
    {
        var result = await ReadAsync("chequing.pdf");

        Assert.Contains(result.Rows, r => r.Label == "Point of sale purchase Tim Hortons #3126 Brampton ONCD");
    }

    [Fact]
    public async Task ParseAsync_NewCsvExport_ReadsTheSameOperationsAsThePdf()
    {
        var pdf = await ReadAsync("chequing.pdf");

        var csv = await ReadAsync("Scotia new.csv");

        Assert.Equal(VerificationStatus.Verified, csv.Status);
        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount)), csv.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_OldCsvExport_ReadsTheSameOperationsAndLabelsAsThePdf()
    {
        var pdf = await ReadAsync("chequing.pdf");

        var csv = await ReadAsync("Scotia old.csv");

        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount, r.Label)), csv.Rows.Select(r => (r.Date, r.Amount, r.Label)));
    }

    [Fact]
    public async Task ParseAsync_VisaPdf_ReadsEveryChargeBalancedToTheCent()
    {
        var result = await ReadAsync("visa.pdf");

        Assert.Equal((VerificationStatus.Verified, AccountKind.CreditCard, Visa.Lines.Count), (result.Status, result.Kind, result.Rows.Count));
        Assert.Equal((Visa.Previous, Visa.New), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
    }

    [Fact]
    public async Task ParseAsync_VisaPdfOverTwoMonthsWithCreditsWrittenMinusAfter_ReadsThemAsMoneyBackOnTheirDays()
    {
        var result = await ReadAsync("visa.pdf");

        Assert.Contains(result.Rows, r => (r.Amount, r.Date) == (57.96m, new DateOnly(2026, 5, 14)));
        Assert.Contains(result.Rows, r => (r.Amount, r.Date) == (70.00m, new DateOnly(2026, 4, 25)));
    }
}
