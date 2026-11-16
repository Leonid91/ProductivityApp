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

    [Fact]
    public void ParseOfx_CaisseEpargneMoneyExportWithDecimalCommas_ReadsOperationsCurrencyAndBalance()
    {
        var statement = CaisseEpargneGenerator.Sample(2025, 10, 30);
        var account = statement.Accounts[0];

        var result = MoneyFiles.ParseOfx(CaisseEpargneGenerator.Ofx(statement, account), "CAD", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
        Assert.Equal(("EUR", account.Closing), (result.Currency, result.ClosingBalance));
    }

    [Fact]
    public void ParseQif_CaisseEpargneQuickenExportWithDecimalCommas_ReadsMinusSignedAmounts()
    {
        var account = CaisseEpargneGenerator.Sample(2025, 10, 30).Accounts[0];

        var result = MoneyFiles.ParseQif(CaisseEpargneGenerator.Qif(account), "EUR", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
    }

    [Fact]
    public void ParseOfx_CicMoneyExport_ReadsOperationsInEurosAndTheClosingBalance()
    {
        var statement = CicGenerator.Sample(2025, 10, 21);
        var account = statement.Accounts[0];

        var result = MoneyFiles.ParseOfx(CicGenerator.Ofx(statement, account), "CAD", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
        Assert.Equal(("EUR", account.Closing), (result.Currency, result.ClosingBalance));
    }

    [Fact]
    public void ParseQif_CicQuickenExport_ReadsOperationsInEurosByItsSepaOperations()
    {
        var account = CicGenerator.Sample(2025, 10, 21).Accounts[0];

        var result = MoneyFiles.ParseQif(CicGenerator.Qif(account), "CAD", null);

        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], Operations(result));
        Assert.Equal("EUR", result.Currency);
    }

    [Fact]
    public void ParseQif_OneOperationNamingAnotherCurrency_KeepsThePersonsCurrency()
    {
        const string qif = "!Type:Bank\nD03/02/2026\nT-12.00\nPExchanged to EUR\n^\nD03/04/2026\nT-5.00\nPBakery\n^\n";

        var result = MoneyFiles.ParseQif(System.Text.Encoding.ASCII.GetBytes(qif), "CAD", null);

        Assert.Equal("CAD", result.Currency);
    }
}
