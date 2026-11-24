using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.ExportFormatsGenerator;

namespace ProductivityApp.Finance.UnitTests.Parsing;

public class TableFilesTests
{
    [Theory]
    [InlineData("activity.xlsx", "Excel")]
    [InlineData("activity.ods", "OpenDocument")]
    [InlineData("statement.odt", "OpenDocument")]
    [InlineData("statement.docx", "Word")]
    [InlineData("activity.xls", "Web page")]
    public void Kind_TableFile_IsFoundByItsContent(string fileName, string kind)
    {
        var result = TableFiles.Kind(ExportFormatsGenerator.OfficeFiles[fileName](), fileName);

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
}
