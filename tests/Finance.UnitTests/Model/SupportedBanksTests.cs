using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Model;

/// <summary>The list of banks the app comes with (the file it is kept in: Finance.IntegrationTests).</summary>
public class SupportedBanksTests
{
    [Fact]
    public void Defaults_EveryBankReadByItself_IsListedOnceUnderItsCountry()
    {
        var byCountry = SupportedBanks.Defaults().ToDictionary(c => c.Code, c => c.Banks.Select(b => b.Name).ToList());

        Assert.Equal(["CA", "FR", "GB"], byCountry.Keys);
        Assert.Equal([KnownBanks.National, "Scotiabank"], byCountry["CA"]);
        Assert.Equal(["BoursoBank", "Caisse d'Epargne", "CIC", "Fortuneo"], byCountry["FR"]);
        Assert.Equal(["Revolut"], byCountry["GB"]);
    }

    [Fact]
    public void Defaults_OnlyTheBanksOfThePersonsOwnStatements_AreConfirmed()
    {
        var banks = SupportedBanks.Defaults().SelectMany(c => c.Banks).ToList();

        Assert.All(banks, b => Assert.True(b.Considered, b.Name));
        Assert.Equal([KnownBanks.National, "Scotiabank"], banks.Where(b => b.Confirmed).Select(b => b.Name));
    }
}
