using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.UnitTests.Text;

public class DateParserTests
{
    [Theory]
    [InlineData("12/10", 12, 10, null)]
    [InlineData("12/10/2025", 12, 10, 2025)]
    [InlineData("12-10-25", 12, 10, 2025)]
    [InlineData("12 OCT", 12, 10, null)]
    [InlineData("12 oct.", 12, 10, null)]
    [InlineData("1er octobre 2025", 1, 10, 2025)]
    [InlineData("12 décembre", 12, 12, null)]
    [InlineData("3 févr. 2026", 3, 2, 2026)]
    [InlineData("OCT 12", 12, 10, null)]
    [InlineData("OCT12", 12, 10, null)]
    [InlineData("Oct. 12, 2025", 12, 10, 2025)]
    [InlineData("October 1, 2025", 1, 10, 2025)]
    [InlineData("2025-10-12", 12, 10, 2025)]
    [InlineData("12AOU", 12, 8, null)]
    public void TryParse_DateInAnyBankWriting_ReturnsDayMonthAndYear(string text, int day, int month, int? year)
    {
        var parsed = DateParser.TryParse(text, out var date);

        Assert.True(parsed, text);
        var resolved = date.Resolve(DateOrder.DayMonth)!.Value;
        Assert.Equal((day, month, year), (resolved.Day, resolved.Month, date.Year));
    }

    [Theory]
    [InlineData(DateOrder.DayMonth, 4, 3)]
    [InlineData(DateOrder.MonthDay, 3, 4)]
    public void ToDate_NumericDate_FollowsTheStatementOrder(DateOrder order, int month, int day)
    {
        DateParser.TryParse("03/04/2025", out var date);

        var result = date.ToDate(order, 2025);

        Assert.Equal(new DateOnly(2025, month, day), result);
    }

    [Theory]
    [InlineData("12.50")]
    [InlineData("SOLDE")]
    [InlineData("31/02")]
    [InlineData("45/12")]
    public void TryParse_TextThatIsNotARealDate_GivesNoDate(string text)
    {
        var parsed = DateParser.TryParse(text, out var date);

        Assert.True(!parsed || date.ToDate(DateOrder.DayMonth, 2025) is null, text);
    }

    [Fact]
    public void TryParseTokens_DayAndMonthInTwoTokens_JoinsThemAndCountsTwoTokens()
    {
        var parsed = DateParser.TryParseTokens(["12", "OCT", "TIM", "HORTONS"], 0, out var date, out var used);

        Assert.True(parsed);
        Assert.Equal((2, 10), (used, date.B));
    }

    [Theory]
    [InlineData(new[] { "10/", "12/2024", "VIR" }, 2)]
    [InlineData(new[] { "16/", "12/", "2022", "CARTE" }, 3)]
    public void TryParseTokens_DatePrintedInPieces_IsOneDate(string[] tokens, int pieces)
    {
        var parsed = DateParser.TryParseTokens(tokens, 0, out var date, out var used);

        Assert.True(parsed);
        Assert.Equal((pieces, 12), (used, date.B));
    }
}
