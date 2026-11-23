using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.UnitTests.Services;

public sealed class SupportedBanksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-supported-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "supported-banks.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

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

    [Fact]
    public void All_TheFirstTime_WritesTheDefaultsToTheFile()
    {
        var list = new SupportedBanks(File).All();

        Assert.True(System.IO.File.Exists(File));
        Assert.Equal(SupportedBanks.Defaults().SelectMany(c => c.Banks), list.SelectMany(c => c.Banks));
        Assert.Contains("\"confirmed\": false", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Create_NoFile_WritesTheDefaults_AFileThereIsKept()
    {
        new SupportedBanks(File).Create();
        Assert.Equal(SupportedBanks.Defaults().Select(c => c.Code), new SupportedBanks(File).All().Select(c => c.Code));

        System.IO.File.WriteAllText(File, "{ changed by hand");
        new SupportedBanks(File).Create();

        Assert.Equal("{ changed by hand", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Update_ABankChanged_IsWrittenToTheFileAndReadBack()
    {
        new SupportedBanks(File).Update("FR", "CIC", new SupportedBank(" CIC Est ", true, true));

        var fr = new SupportedBanks(File).All().Single(c => c.Code == "FR").Banks;
        Assert.Equal(["BoursoBank", "Caisse d'Epargne", "CIC Est", "Fortuneo"], fr.Select(b => b.Name));
        Assert.True(fr.Single(b => b.Name == "CIC Est").Confirmed);
    }

    [Fact]
    public void All_TheFileChangedByHand_IsReadAsItIs()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, """[{ "code": "BE", "name": "Belgium", "banks": [{ "name": "Belfius", "considered": false, "confirmed": false }] }]""");

        var list = new SupportedBanks(File).All();

        Assert.Equal("Belgium", Assert.Single(list).Name);
        Assert.Equal("Belfius", Assert.Single(list[0].Banks).Name);
    }

    [Fact]
    public void All_AFileThatIsNotAList_SaysWhichFileCannotBeRead()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");

        var e = Assert.Throws<InvalidDataException>(() => new SupportedBanks(File).All());

        Assert.Contains(File, e.Message);
    }

    [Theory]
    [InlineData("FR", "HSBC", "BoursoBank", "No bank HSBC")]
    [InlineData("DE", "CIC", "CIC", "No country DE")]
    [InlineData("FR", "CIC", "fortuneo", "already in France")]
    [InlineData("FR", "CIC", " ", "1 to 80 characters")]
    public void Update_AnUnknownBankOrAName_TakenOrEmpty_IsRefused(string country, string name, string newName, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => new SupportedBanks(File).Update(country, name, new SupportedBank(newName, true, false)));

        Assert.Contains(message, e.Message);
    }
}
