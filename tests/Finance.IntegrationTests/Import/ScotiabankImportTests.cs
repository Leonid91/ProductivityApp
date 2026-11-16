using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>Statements and exports in the layouts of a Scotiabank chequing account and Visa card (invented data), read through the import.</summary>
public sealed class ScotiabankImportTests : IDisposable
{
    private static readonly ScotiaGenerator.Statement Chequing = ScotiaGenerator.Chequing();
    private static readonly ScotiaGenerator.Card Visa = ScotiaGenerator.VisaCard();

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private async Task<StatementParse> ReadAsync(string fileName, byte[] bytes) => (await _store.ParseAsync(fileName, bytes)).Parse;

    [Fact]
    public async Task ParseAsync_ChequingPdf_ReadsEveryOperationBalancedToTheCent()
    {
        var result = await ReadAsync("chequing.pdf", ScotiaGenerator.ChequingPdf(Chequing));

        Assert.Equal((VerificationStatus.Verified, AccountKind.Bank, Chequing.Lines.Count), (result.Status, result.Kind, result.Rows.Count));
        Assert.Equal((Chequing.Opening, Chequing.Closing), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
        Assert.Equal((-Chequing.Withdrawals, Chequing.Deposits), (result.Rows.Where(r => r.Amount < 0).Sum(r => r.Amount), result.Rows.Where(r => r.Amount > 0).Sum(r => r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_ChequingPdfWithTwoLinesOfDescription_JoinsTheKindOfOperationAndTheShop()
    {
        var result = await ReadAsync("chequing.pdf", ScotiaGenerator.ChequingPdf(Chequing));

        Assert.Contains(result.Rows, r => r.Label == "Point of sale purchase Tim Hortons #3126 Brampton ONCD");
    }

    [Fact]
    public async Task ParseAsync_NewCsvExport_ReadsTheSameOperationsAsThePdf()
    {
        var pdf = await ReadAsync("chequing.pdf", ScotiaGenerator.ChequingPdf(Chequing));

        var csv = await ReadAsync("Scotia new.csv", ScotiaGenerator.NewCsv(Chequing));

        Assert.Equal(VerificationStatus.Verified, csv.Status);
        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount)), csv.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public async Task ParseAsync_OldCsvExport_ReadsTheSameOperationsAndLabelsAsThePdf()
    {
        var pdf = await ReadAsync("chequing.pdf", ScotiaGenerator.ChequingPdf(Chequing));

        var csv = await ReadAsync("Scotia old.csv", ScotiaGenerator.OldCsv(Chequing));

        Assert.Equal(pdf.Rows.Select(r => (r.Date, r.Amount, r.Label)), csv.Rows.Select(r => (r.Date, r.Amount, r.Label)));
    }

    [Fact]
    public async Task ParseAsync_VisaPdf_ReadsEveryChargeBalancedToTheCent()
    {
        var result = await ReadAsync("visa.pdf", ScotiaGenerator.CardPdf(Visa));

        Assert.Equal((VerificationStatus.Verified, AccountKind.CreditCard, Visa.Lines.Count), (result.Status, result.Kind, result.Rows.Count));
        Assert.Equal((Visa.Previous, Visa.New), (result.OpeningBalance!.Value, result.ClosingBalance!.Value));
    }

    [Fact]
    public async Task ParseAsync_VisaPdfOverTwoMonthsWithCreditsWrittenMinusAfter_ReadsThemAsMoneyBackOnTheirDays()
    {
        var result = await ReadAsync("visa.pdf", ScotiaGenerator.CardPdf(Visa));

        Assert.Contains(result.Rows, r => (r.Amount, r.Date) == (57.96m, new DateOnly(2026, 5, 14)));
        Assert.Contains(result.Rows, r => (r.Amount, r.Date) == (70.00m, new DateOnly(2026, 4, 25)));
    }
}
