using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.UnitTests.Services;

/// <summary>Reading the central banks' files of exchange rates (no database here).</summary>
public class ExchangeRatesTests
{
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Parse_FederalReserveTable_GivesRatesAgainstTheUsDollarForEveryCurrency()
    {
        var (series, currency) = ExchangeRates.Parse(Utf8(RateFiles.FederalReserve), CentralBank.FederalReserve);

        Assert.Equal("USD", currency);
        Assert.Equal(["CAD", "EUR", "JPY"], series.Keys.Order());
    }

    [Fact]
    public void Parse_FederalReserveEuroQuotedInDollarsPerEuro_KeepsItAsEurosPerDollarWithoutMissingDays()
    {
        var (series, _) = ExchangeRates.Parse(Utf8(RateFiles.FederalReserve), CentralBank.FederalReserve);

        Assert.Equal("2025-10-10=0.8333333333;2025-10-08=0.8;", series["EUR"]);
    }

    [Fact]
    public void Parse_FederalReserveCurrencyQuotedPerDollar_KeepsItsDaysNewestFirst()
    {
        var (series, _) = ExchangeRates.Parse(Utf8(RateFiles.FederalReserve), CentralBank.FederalReserve);

        Assert.Equal("2025-10-10=1.3900;2025-10-09=1.4100;2025-10-08=1.4000;", series["CAD"]);
    }
}
