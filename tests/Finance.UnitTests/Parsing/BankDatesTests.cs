using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;

namespace ProductivityApp.Finance.UnitTests.Parsing;

/// <summary>
/// The date format a bank starts with, from its country and banking system (how the readers use it: in
/// CsvStatementReaderTests and MoneyFilesTests).
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
}
