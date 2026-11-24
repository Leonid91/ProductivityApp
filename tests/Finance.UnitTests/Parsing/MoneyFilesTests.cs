using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.ExportFormatsGenerator;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class MoneyFilesTests
{
    private static List<(DateOnly, decimal)> Operations(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x)];

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ParseOfx_OldOrXmlFile_ReadsEveryOperation(bool xml, bool card)
    {
        var result = MoneyFiles.ParseOfx(Ofx(xml, card), "CAD", null);

        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.All(Ops.Zip(result.Rows), x => Assert.Contains(x.First.Label.Split(' ')[0], x.Second.Label, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseOfx_LedgerBalance_GivesTheClosingAndOpeningBalances(bool xml)
    {
        var result = MoneyFiles.ParseOfx(Ofx(xml, card: false), "CAD", null);

        Assert.Equal((Opening, Opening + Ops.Sum(o => o.Amount)), (result.OpeningBalance, result.ClosingBalance));
    }

    [Theory]
    [InlineData(false, AccountKind.Bank)]
    [InlineData(true, AccountKind.CreditCard)]
    public void ParseOfx_AccountSection_GivesTheKindOfAccount(bool card, AccountKind kind)
    {
        var result = MoneyFiles.ParseOfx(Ofx(false, card), "CAD", null);

        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void ParseOfx_CardAccount_ReadsTheCardsLastFourDigits()
    {
        var result = MoneyFiles.ParseOfx(Ofx(false, card: true), "CAD", null);

        Assert.Equal("1234", result.Last4);
    }

    [Fact]
    public void ParseQif_MonthFirstDatesAndGroupedAmounts_ReadsEveryOperation()
    {
        var result = MoneyFiles.ParseQif(QifMonthFirst(), "CAD", null);

        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public void ParseQif_DayFirstDatesOfACard_ReadsEveryLineOfTheCard()
    {
        var result = MoneyFiles.ParseQif(QifDayFirstCard(), "CAD", null);

        Assert.Equal((Ops.Length + 1, AccountKind.CreditCard, Ops[2].Date), (result.Rows.Count, result.Kind, result.Rows[2].Date));
    }

    private static readonly CaisseEpargneGenerator.Statement CaisseOctober = CaisseEpargneGenerator.Sample(2025, 10, 30);
    private static readonly CicGenerator.Statement CicOctober = CicGenerator.Sample(2025, 10, 21);

    /// <summary>A French bank's OFX export (Caisse d'Epargne's with decimal commas), the operations and closing balance it holds.</summary>
    private static readonly Dictionary<string, (byte[] Ofx, List<(DateOnly, decimal)> Operations, decimal Closing)> FrenchOfx = new()
    {
        ["Caisse d'Epargne"] = (CaisseEpargneGenerator.Ofx(CaisseOctober, CaisseOctober.Accounts[0]), [.. CaisseOctober.Accounts[0].Lines.Select(l => (l.Date, l.Amount)).Order()], CaisseOctober.Accounts[0].Closing),
        ["CIC"] = (CicGenerator.Ofx(CicOctober, CicOctober.Accounts[0]), [.. CicOctober.Accounts[0].Lines.Select(l => (l.Date, l.Amount)).Order()], CicOctober.Accounts[0].Closing),
    };

    [Theory]
    [InlineData("Caisse d'Epargne")]
    [InlineData("CIC")]
    public void ParseOfx_FrenchBankMoneyExport_ReadsOperationsInEurosAndTheClosingBalance(string bank)
    {
        var (ofx, operations, closing) = FrenchOfx[bank];

        var result = MoneyFiles.ParseOfx(ofx, "CAD", null);

        Assert.Equal(operations, Operations(result));
        Assert.Equal(("EUR", closing), (result.Currency, result.ClosingBalance!.Value));
    }

    [Fact]
    public void ParseQif_CaisseEpargneQuickenExportWithDecimalCommas_ReadsMinusSignedAmounts()
    {
        var account = CaisseOctober.Accounts[0];

        var result = MoneyFiles.ParseQif(CaisseEpargneGenerator.Qif(account), "EUR", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
    }

    [Fact]
    public void ParseQif_CicQuickenExport_ReadsOperationsInEurosByItsSepaOperations()
    {
        var account = CicOctober.Accounts[0];

        var result = MoneyFiles.ParseQif(CicGenerator.Qif(account), "CAD", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
        Assert.Equal("EUR", result.Currency);
    }

    /// <summary>3 April and 5 April, or 4 March and 4 May: every number is 12 or less.</summary>
    private static readonly byte[] QifOfNumbersOf12OrLess = System.Text.Encoding.ASCII.GetBytes("!Type:Bank\nD03/04/2026\nT-4.50\nPBakery\n^\nD05/04/2026\nT2000.00\nPSalary\n^\n");

    [Theory]
    [InlineData(DateFormat.DayMonthYear, 4, 3)]
    [InlineData(DateFormat.MonthDayYear, 3, 4)]
    public void ParseQif_AtABankOfAFormat_ReadsTheDatesInThatFormat(DateFormat format, int month, int day)
    {
        var result = MoneyFiles.ParseQif(QifOfNumbersOf12OrLess, "CAD", null, format);

        Assert.Equal(new DateOnly(2026, month, day), result.Rows[0].Date);
    }

    [Fact]
    public void ParseQif_DatesThatDoNotFitTheBanksFormat_SaysSo()
    {
        var qif = System.Text.Encoding.ASCII.GetBytes("!Type:Bank\nD03/14/2026\nT-4.50\nPBakery\n^\n");

        var result = MoneyFiles.ParseQif(qif, "CAD", null, DateFormat.DayMonthYear);

        Assert.Contains("written MM/DD/YYYY, not DD/MM/YYYY", result.DateFormatMismatch);
    }

    [Fact]
    public void ParseQif_OneOperationNamingAnotherCurrency_KeepsThePersonsCurrency()
    {
        const string qif = "!Type:Bank\nD03/02/2026\nT-12.00\nPExchanged to EUR\n^\nD03/04/2026\nT-5.00\nPBakery\n^\n";

        var result = MoneyFiles.ParseQif(System.Text.Encoding.ASCII.GetBytes(qif), "CAD", null);

        Assert.Equal("CAD", result.Currency);
    }
}
