using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Tests;

public class AmountParserTests
{
    [Theory]
    [InlineData("1 234,56", 1234.56, SignMarker.None)]
    [InlineData("1.234,56", 1234.56, SignMarker.None)]
    [InlineData("1,234.56", 1234.56, SignMarker.None)]
    [InlineData("1234.56", 1234.56, SignMarker.None)]
    [InlineData("12,34", 12.34, SignMarker.None)]
    [InlineData("-12.34", 12.34, SignMarker.Minus)]
    [InlineData("−12,34", 12.34, SignMarker.Minus)]
    [InlineData("12.34-", 12.34, SignMarker.Minus)]
    [InlineData("(12.34)", 12.34, SignMarker.Minus)]
    [InlineData("+45,00", 45.00, SignMarker.Plus)]
    [InlineData("12.34 CR", 12.34, SignMarker.Credit)]
    [InlineData("12.34CR", 12.34, SignMarker.Credit)]
    [InlineData("12.34 DR", 12.34, SignMarker.Debit)]
    [InlineData("$1,050.00", 1050.00, SignMarker.None)]
    [InlineData("-$8.99", 8.99, SignMarker.Minus)]
    [InlineData("12,34 €", 12.34, SignMarker.None)]
    [InlineData("1 234 567,89", 1234567.89, SignMarker.None)]
    [InlineData("0.50", 0.50, SignMarker.None)]
    public void Reads_french_and_english_amounts(string text, double magnitude, SignMarker marker)
    {
        Assert.True(AmountParser.TryParse(text, out var a), text);
        Assert.Equal((decimal)magnitude, a.Magnitude);
        Assert.Equal(marker, a.Marker);
    }

    [Theory]
    [InlineData("2025")]
    [InlineData("12")]
    [InlineData("1,234")]
    [InlineData("1,234,56")]
    [InlineData("12.345")]
    [InlineData("ABC")]
    [InlineData("(12.34")]
    [InlineData("0012.50")]
    public void Rejects_things_that_are_not_money(string text) => Assert.False(AmountParser.TryParse(text, out _), text);

    [Fact]
    public void Storage_round_trip_is_exact()
    {
        foreach (var v in new[] { 0.1m + 0.2m, -1234.56m, 0.01m, 99999999.99m })
            Assert.Equal(v, AmountParser.FromStorage(AmountParser.ToStorage(v)));
    }
}

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
    public void Reads_dates(string text, int day, int month, int? year)
    {
        Assert.True(DateParser.TryParse(text, out var d), text);
        var r = d.Resolve(DateOrder.DayMonth)!.Value;
        Assert.Equal(day, r.Day);
        Assert.Equal(month, r.Month);
        Assert.Equal(year, d.Year);
    }

    [Fact]
    public void Numeric_dates_follow_the_statement_order()
    {
        Assert.True(DateParser.TryParse("03/04/2025", out var d));
        Assert.Equal(new DateOnly(2025, 4, 3), d.ToDate(DateOrder.DayMonth, 2025));
        Assert.Equal(new DateOnly(2025, 3, 4), d.ToDate(DateOrder.MonthDay, 2025));
    }

    [Theory]
    [InlineData("12.50")]
    [InlineData("SOLDE")]
    [InlineData("31/02")]
    [InlineData("45/12")]
    public void Rejects_non_dates(string text)
    {
        var ok = DateParser.TryParse(text, out var d);
        Assert.True(!ok || d.ToDate(DateOrder.DayMonth, 2025) is null, text);
    }

    [Fact]
    public void Joins_split_tokens()
    {
        Assert.True(DateParser.TryParseTokens(["12", "OCT", "TIM", "HORTONS"], 0, out var d, out var n));
        Assert.Equal(2, n);
        Assert.Equal(10, d.B);
    }
}
