using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.ExportFormatsGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Parsing;

/// <summary>
/// Office and web files (Excel, OpenDocument, Word, web pages) read by the table reader with the libraries it uses, then
/// by the statement reader: the operations come out of every format the same, balanced, in the currency their cells show.
/// </summary>
public class TableFilesTests
{
    private static readonly CicGenerator.Statement CicSeptember = CicGenerator.Sample(2025, 9, 3);

    /// <summary>Reads a table file as the import does: its table of operations, then its columns found.</summary>
    private static StatementParse Read(byte[] bytes, string fileName) =>
        CsvStatementReader.ParseTable(TableFiles.Read(bytes, fileName), fileName, "CAD", null, TableFiles.Kind(bytes, fileName)!);

    [Theory]
    [InlineData("activity.xlsx")]
    [InlineData("activity.ods")]
    [InlineData("statement.odt")]
    [InlineData("statement.docx")]
    [InlineData("activity.xls")]
    [InlineData("activity.html")]
    public void Read_TableOfOperationsInAnyFormat_GivesTheSameOperationsBalanced(string fileName)
    {
        var result = Read(ExportFormatsGenerator.OfficeFiles[fileName](), fileName);

        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
        Assert.All(Ops.Zip(result.Rows), x => Assert.Contains(x.First.Label.Split(' ')[0], x.Second.Label, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VerificationStatus.Verified, result.Status);
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

    /// <summary>Table files whose cells show money in a currency, or plain numbers, by a key of their own.</summary>
    private static readonly Dictionary<string, (string Name, Func<byte[]> Bytes)> Money = new()
    {
        ["Excel in euros"] = ("activity.xlsx", () => Xlsx("#,##0.00 [$€-40C]")),
        ["OpenDocument in francs"] = ("activity.ods", () => OpenDocument(text: false, currency: "CHF")),
        ["Excel of plain numbers"] = ("activity.xlsx", () => Xlsx()),
    };

    [Theory]
    [InlineData("Excel in euros", "EUR")]
    [InlineData("OpenDocument in francs", "CHF")]
    [InlineData("Excel of plain numbers", "CAD")]   // no currency shown: the person's
    public void Read_TableFileForAPersonInCanadianDollars_IsInTheCurrencyItsCellsShowElseThePersons(string file, string currency)
    {
        var (name, bytes) = Money[file];

        var result = Read(bytes(), name);

        Assert.Equal(currency, result.Currency);
    }

    [Fact]
    public void Read_ExcelWhoseMoneyIsFormattedInEuros_ReadsEveryOperation()
    {
        var result = Read(Xlsx("#,##0.00 [$€-40C]"), "activity.xlsx");

        Assert.Equal(Ops.Select(o => (o.Date, o.Amount)), result.Rows.Select(r => (r.Date, r.Amount)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadAccounts_CicSheetPerAccount_EachSheetIsInTheEurosOfItsFormats(int index)
    {
        var sheets = TableFiles.ReadAccounts(CicGenerator.Xlsx(CicSeptember), "export.xlsx");

        var result = CsvStatementReader.ParseTable(sheets[index].Table, "export.xlsx", "CAD", null, "Excel");

        Assert.Equal("EUR", result.Currency);
    }
}
