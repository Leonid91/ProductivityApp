using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.UnitTests.Text;

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
    [InlineData("+46,00", 46.00, SignMarker.Plus)]
    [InlineData("12.34 CR", 12.34, SignMarker.Credit)]
    [InlineData("12.34CR", 12.34, SignMarker.Credit)]
    [InlineData("12.34 DR", 12.34, SignMarker.Debit)]
    [InlineData("$1,050.00", 1050.00, SignMarker.None)]
    [InlineData("-$8.99", 8.99, SignMarker.Minus)]
    [InlineData("12,34 €", 12.34, SignMarker.None)]
    [InlineData("1 234 567,89", 1234567.89, SignMarker.None)]
    [InlineData("0.50", 0.50, SignMarker.None)]
    public void TryParse_FrenchOrEnglishAmount_ReturnsMagnitudeAndSignMarker(string text, double magnitude, SignMarker marker)
    {
        var parsed = AmountParser.TryParse(text, out var amount);

        Assert.True(parsed, text);
        Assert.Equal((decimal)magnitude, amount.Magnitude);
        Assert.Equal(marker, amount.Marker);
    }

    [Theory]
    [InlineData("CHF 1'234.50", 1234.50)]
    [InlineData("12,50 GBP", 12.50)]
    [InlineData("SEK 1 020,00", 1020.00)]
    [InlineData("-45.10 AUD", -45.10)]
    [InlineData("PLN 7.00", 7.00)]
    [InlineData("$12.34", 12.34)]
    [InlineData("12,34 €", 12.34)]
    [InlineData("₹1,234.56", 1234.56)]
    [InlineData("¥ 980.00", 980.00)]
    [InlineData("-₩15,000.00", -15000.00)]
    [InlineData("KES 1,200.00", 1200.00)]
    [InlineData("250.00 MXN", 250.00)]
    [InlineData("₺99,90", 99.90)]
    [InlineData("JPY 1,200", 1200)]
    [InlineData("1 200 JPY", 1200)]
    [InlineData("₩15,000", 15000)]
    [InlineData("KWD 12.500", 12.500)]
    [InlineData("-1.250,750 BHD", -1250.750)]
    public void TryParse_AmountWithAnyCurrencySignOrCode_ReturnsSignedValue(string text, double expected)
    {
        var parsed = AmountParser.TryParse(text, out var amount);

        Assert.True(parsed, text);
        Assert.Equal((decimal)Math.Abs(expected), amount.Magnitude);
        Assert.Equal(expected < 0, amount.Marker == SignMarker.Minus);
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
    [InlineData("ATM 12.50")]
    [InlineData("12.50 NYC")]
    [InlineData("REF 2026.01")]
    [InlineData("1,200")]
    [InlineData("$1,200")]
    [InlineData("12.500 EUR")]
    [InlineData("¥1,200")]
    public void TryParse_TextThatIsNotMoney_ReturnsFalse(string text)
    {
        var parsed = AmountParser.TryParse(text, out _);

        Assert.False(parsed, text);
    }

    [Theory]
    [InlineData("¥")]
    [InlineData("₹")]
    [InlineData("₽")]
    [InlineData("NGN")]
    [InlineData("VND")]
    public void IsDetachedMarker_CurrencySignOrCode_ReturnsTrue(string text)
    {
        var isMarker = AmountParser.IsDetachedMarker(text);

        Assert.True(isMarker, text);
    }

    [Theory]
    [InlineData("Amount (CHF)", "CHF")]
    [InlineData("-45.10 CHF", "CHF")]
    [InlineData("12,34 €", "EUR")]
    [InlineData("£5.00", "GBP")]
    [InlineData("-₹1,234.56", "INR")]
    [InlineData("CA$ 12.00", "CAD")]
    [InlineData("12.00 $ US", "USD")]
    [InlineData("Montant EUR", "EUR")]
    public void CurrencyOf_TextNamingOneCurrency_ReturnsItsCode(string text, string code)
    {
        var result = AmountParser.CurrencyOf(text);

        Assert.Equal(code, result);
    }

    [Theory]
    [InlineData("$12.34")]       // the dollar sign of many currencies
    [InlineData("¥980")]         // the yen's and the yuan's
    [InlineData("Amount")]
    [InlineData("12.50")]
    [InlineData("EUR or CHF")]   // two currencies
    [InlineData("DEBIT")]        // three letters inside a word are not a code
    [InlineData("")]
    public void CurrencyOf_TextNamingNoOneCurrency_ReturnsNull(string text)
    {
        var result = AmountParser.CurrencyOf(text);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("-1234.56")]
    [InlineData("0.01")]
    [InlineData("99999999.99")]
    public void FromStorage_ValueWrittenByToStorage_ReturnsTheExactValue(string value)
    {
        var original = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var roundTripped = AmountParser.FromStorage(AmountParser.ToStorage(original));

        Assert.Equal(original, roundTripped);
    }
}
