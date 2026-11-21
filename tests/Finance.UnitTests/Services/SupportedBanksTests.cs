using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Services;

public class SupportedBanksTests
{
    [Fact]
    public void All_EveryBankReadByItself_IsListedOnceUnderItsCountry()
    {
        var byCountry = SupportedBanks.All.ToDictionary(c => c.Code, c => c.Banks.Select(b => b.Name).ToList());

        Assert.Equal(["CA", "FR", "GB"], byCountry.Keys);
        Assert.Equal([KnownBanks.National, "Scotiabank"], byCountry["CA"]);
        Assert.Equal(["BoursoBank", "Caisse d'Epargne", "CIC", "Fortuneo"], byCountry["FR"]);
        Assert.Equal(["Revolut"], byCountry["GB"]);
    }

    [Fact]
    public void All_ABankConfirmedOnARealStatement_IsAlsoConsidered()
    {
        var banks = SupportedBanks.All.SelectMany(c => c.Banks).ToList();

        Assert.All(banks.Where(b => b.Confirmed), b => Assert.True(b.Considered, b.Name));
        Assert.Equal([KnownBanks.National, "Scotiabank", "BoursoBank", "Fortuneo"], banks.Where(b => b.Confirmed).Select(b => b.Name));
    }
}
