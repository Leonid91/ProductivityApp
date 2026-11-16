using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.ExportFormatsGenerator;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class TableFilesTests
{
    /// <summary>Reads a table file as the import does: its table of operations, then its columns found.</summary>
    private static StatementParse Read(byte[] bytes, string fileName) =>
        CsvStatementReader.ParseTable(TableFiles.Read(bytes, fileName), fileName, "CAD", null, TableFiles.Kind(bytes, fileName)!);

    public static TheoryData<string, byte[]> EveryTableFormat => new()
    {
        { "activity.xlsx", Xlsx() },
        { "activity.ods", OpenDocument(text: false) },
        { "statement.odt", OpenDocument(text: true) },
        { "statement.docx", Docx() },
        { "activity.xls", HtmlXls() },
        { "activity.html", HtmlXls() },
    };

    [Theory]
    [MemberData(nameof(EveryTableFormat))]
    public void Read_TableOfOperationsInAnyFormat_GivesTheSameOperationsBalanced(string fileName, byte[] bytes)
    {
        var result = Read(bytes, fileName);

        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.All(Ops.Zip(result.Rows), x => Assert.Contains(x.First.Label.Split(' ')[0], x.Second.Label, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VerificationStatus.Verified, result.Status);
    }

    public static TheoryData<string, byte[], string> TableFormatKinds => new()
    {
        { "activity.xlsx", Xlsx(), "Excel" },
        { "activity.ods", OpenDocument(text: false), "OpenDocument" },
        { "statement.odt", OpenDocument(text: true), "OpenDocument" },
        { "statement.docx", Docx(), "Word" },
        { "activity.xls", HtmlXls(), "Web page" },
    };

    [Theory]
    [MemberData(nameof(TableFormatKinds))]
    public void Kind_TableFile_IsFoundByItsContent(string fileName, byte[] bytes, string kind)
    {
        var result = TableFiles.Kind(bytes, fileName);

        Assert.Equal(kind, result);
    }

    [Fact]
    public void Kind_UnknownBytes_ReturnsNull()
    {
        var result = TableFiles.Kind([1, 2, 3, 0, 5, 6, 7, 8, 9, 10, 11, 12, 13], "notes.bin");

        Assert.Null(result);
    }

    // ------------------------------------------------------------------------------------------- a sheet per account

    private static readonly CicGenerator.Statement CicSeptember = CicGenerator.Sample(2025, 9, 3);

    [Fact]
    public void ReadAccounts_SheetPerAccountTitledWithItsNumber_GivesOneTablePerAccountNamedAfterIt()
    {
        var accounts = TableFiles.ReadAccounts(CicGenerator.Xlsx(CicSeptember), "CIC export.xlsx");

        Assert.Equal(["C/C EUROCOMPTE DUO CONFORT", "LIVRET A"], accounts.Select(a => a.Title));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadAccounts_SheetPerAccount_EachTableReadsItsOwnOperationsBalanced(int index)
    {
        var expected = CicSeptember.Accounts[index];
        var table = TableFiles.ReadAccounts(CicGenerator.Xlsx(CicSeptember), "CIC export.xlsx")[index].Table;

        var result = CsvStatementReader.ParseTable(table, "CIC export.xlsx", "CAD", null, "Excel");

        Assert.Equal([.. expected.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], result.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x));
        Assert.Equal((VerificationStatus.Verified, expected.Opening, expected.Closing), (result.Status, result.OpeningBalance, result.ClosingBalance));
    }

    [Fact]
    public void ReadAccounts_SheetsOfOneAccount_StayOneStatement()
    {
        var account = CicSeptember.Accounts[0];
        var half = account.Lines.Count / 2;
        var split = CicSeptember with
        {
            Accounts = [account with { Lines = account.Lines[..half] }, account with { Opening = account.Opening + account.Lines[..half].Sum(l => l.Amount), Lines = account.Lines[half..] }],
        };

        var accounts = TableFiles.ReadAccounts(CicGenerator.Xlsx(split), "export.xlsx");

        var single = Assert.Single(accounts);
        var result = CsvStatementReader.ParseTable(single.Table, "export.xlsx", "EUR", null, "Excel");
        Assert.Equal([.. account.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x)], result.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x));
    }

    [Theory]
    [InlineData("#,##0.00 [$€-40C]", "EUR")]
    [InlineData("[$CHF] #,##0.00", "CHF")]
    [InlineData("[$$-1009]#,##0.00", "CAD")]   // a dollar of the locale of English (Canada)
    [InlineData("[$$-409]#,##0.00", "USD")]    // a dollar of the locale of English (United States)
    [InlineData("#,##0.00\\ \"€\"", "EUR")]
    [InlineData("[Red]-#,##0.00 £", "GBP")]
    public void FormatCurrency_NumberFormatShowingACurrency_ReturnsIt(string format, string currency)
    {
        var result = TableFiles.FormatCurrency(format);

        Assert.Equal(currency, result);
    }

    [Theory]
    [InlineData("#,##0.00")]
    [InlineData("\"$\"#,##0.00")]   // a dollar without its country
    [InlineData("General")]
    [InlineData("[Red]0.00")]
    public void FormatCurrency_NumberFormatWithoutACurrency_ReturnsNull(string format)
    {
        var result = TableFiles.FormatCurrency(format);

        Assert.Null(result);
    }

    [Fact]
    public void Read_ExcelWhoseMoneyIsFormattedInEuros_IsAStatementInEurosForAPersonInCanadianDollars()
    {
        var result = Read(Xlsx("#,##0.00 [$€-40C]"), "activity.xlsx");

        Assert.Equal("EUR", result.Currency);
        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Fact]
    public void Read_OpenDocumentWhoseCellsAreMoneyInFrancs_IsAStatementInFrancs()
    {
        var result = Read(OpenDocument(text: false, currency: "CHF"), "activity.ods");

        Assert.Equal("CHF", result.Currency);
    }

    [Fact]
    public void Read_ExcelWithPlainNumbers_IsInThePersonsCurrency()
    {
        var result = Read(Xlsx(), "activity.xlsx");

        Assert.Equal("CAD", result.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadAccounts_CicSheetPerAccount_EachSheetIsInTheEurosOfItsFormats(int index)
    {
        var sheets = TableFiles.ReadAccounts(CicGenerator.Xlsx(CicGenerator.Sample(2025, 9, 3)), "export.xlsx");

        var result = CsvStatementReader.ParseTable(sheets[index].Table, "export.xlsx", "CAD", null, "Excel");

        Assert.Equal("EUR", result.Currency);
    }
}
