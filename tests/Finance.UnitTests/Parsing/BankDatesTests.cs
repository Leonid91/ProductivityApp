using System.Text;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;

namespace ProductivityApp.Finance.UnitTests.Parsing;

/// <summary>
/// The date format set for a bank decides how the numeric dates of its files are read; dates that do not fit it are
/// reported, never read another way in silence.
/// </summary>
public class BankDatesTests
{
    [Theory]
    [InlineData("", BankingSystem.France, DateFormat.DayMonthYear)]
    [InlineData("France", BankingSystem.France, DateFormat.DayMonthYear)]
    [InlineData("Canada", BankingSystem.NorthAmerica, DateFormat.YearMonthDay)]
    [InlineData("CA", BankingSystem.NorthAmerica, DateFormat.YearMonthDay)]
    [InlineData("United States", BankingSystem.NorthAmerica, DateFormat.MonthDayYear)]
    [InlineData("USA", BankingSystem.NorthAmerica, DateFormat.MonthDayYear)]
    [InlineData("Germany", BankingSystem.NorthAmerica, DateFormat.DayMonthYear)]
    [InlineData("", BankingSystem.NorthAmerica, DateFormat.DayMonthYear)]
    public void Default_BankOfACountryAndBankingSystem_StartsWithTheFormatWrittenThere(string country, BankingSystem system, DateFormat format)
    {
        var result = BankDates.Default(country, system);

        Assert.Equal(format, result);
    }

    /// <summary>3 April and 5 April, or 4 March and 4 May: every number is 12 or less.</summary>
    private static readonly byte[] TwoNumbersOf12OrLess = Encoding.UTF8.GetBytes("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n05/04/2026,Salary,2000.00\n");

    [Theory]
    [InlineData(DateFormat.DayMonthYear, 4, 3)]
    [InlineData(DateFormat.MonthDayYear, 3, 4)]
    public void Parse_CsvAtABankOfAFormat_ReadsTheDatesInThatFormat(DateFormat format, int month, int day)
    {
        var result = CsvStatementReader.Parse(TwoNumbersOf12OrLess, "export.csv", "CAD", null, format);

        Assert.Equal(new DateOnly(2026, month, day), result.Rows[0].Date);
        Assert.Null(result.DateFormatMismatch);
    }

    [Theory]
    [InlineData("Date,Description,Amount\n03/14/2026,Bakery,-4.50\n", DateFormat.DayMonthYear, "written MM/DD/YYYY, not DD/MM/YYYY")]
    [InlineData("Date,Description,Amount\n14/03/2026,Bakery,-4.50\n", DateFormat.MonthDayYear, "written DD/MM/YYYY, not MM/DD/YYYY")]
    [InlineData("Date,Description,Amount\n14/03/2026,Bakery,-4.50\n", DateFormat.YearMonthDay, "written DD/MM/YYYY, not YYYY-MM-DD")]
    [InlineData("Date,Description,Amount\n03/04/2026,Bakery,-4.50\n", DateFormat.YearMonthDay, "written with the year last, not YYYY-MM-DD")]
    public void Parse_CsvWhoseDatesDoNotFitTheBanksFormat_SaysHowTheyAreWritten(string csv, DateFormat format, string said)
    {
        var result = CsvStatementReader.Parse(Encoding.UTF8.GetBytes(csv), "export.csv", "CAD", null, format);

        Assert.Contains(said, result.DateFormatMismatch);
    }

    [Theory]
    [InlineData(DateFormat.DayMonthYear)]
    [InlineData(DateFormat.MonthDayYear)]
    [InlineData(DateFormat.YearMonthDay)]
    public void Parse_CsvWithDatesYearFirst_FitsEveryFormat(DateFormat format)
    {
        var result = CsvStatementReader.Parse(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-03-14,Bakery,-4.50\n"), "export.csv", "CAD", null, format);

        Assert.Equal((new DateOnly(2026, 3, 14), (string?)null), (result.Rows[0].Date, result.DateFormatMismatch));
    }

    [Fact]
    public void Parse_CsvBeforeABankIsChosen_ReportsNoMismatch()
    {
        var result = CsvStatementReader.Parse(Encoding.UTF8.GetBytes("Date,Description,Amount\n03/14/2026,Bakery,-4.50\n"), "export.csv", "CAD");

        Assert.Null(result.DateFormatMismatch);
    }

    private static readonly byte[] Qif = Encoding.ASCII.GetBytes("!Type:Bank\nD03/04/2026\nT-4.50\nPBakery\n^\nD05/04/2026\nT2000.00\nPSalary\n^\n");

    [Theory]
    [InlineData(DateFormat.DayMonthYear, 4, 3)]
    [InlineData(DateFormat.MonthDayYear, 3, 4)]
    public void ParseQif_AtABankOfAFormat_ReadsTheDatesInThatFormat(DateFormat format, int month, int day)
    {
        var result = MoneyFiles.ParseQif(Qif, "CAD", null, format);

        Assert.Equal(new DateOnly(2026, month, day), result.Rows[0].Date);
    }

    [Fact]
    public void ParseQif_DatesThatDoNotFitTheBanksFormat_SaysSo()
    {
        var qif = Encoding.ASCII.GetBytes("!Type:Bank\nD03/14/2026\nT-4.50\nPBakery\n^\n");

        var result = MoneyFiles.ParseQif(qif, "CAD", null, DateFormat.DayMonthYear);

        Assert.Contains("written MM/DD/YYYY, not DD/MM/YYYY", result.DateFormatMismatch);
    }
}
