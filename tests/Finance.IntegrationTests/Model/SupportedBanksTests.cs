using ProductivityApp.Finance.Model;

namespace ProductivityApp.Finance.IntegrationTests.Model;

/// <summary>The list of supported banks kept in a real JSON file of a folder of its own, deleted when the test ends.</summary>
public sealed class SupportedBanksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa-supported-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "supported-banks.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (DirectoryNotFoundException) { }
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
    public void Create_NoFile_WritesTheDefaults()
    {
        new SupportedBanks(File).Create();

        Assert.Equal(SupportedBanks.Defaults().Select(c => c.Code), new SupportedBanks(File).All().Select(c => c.Code));
    }

    [Fact]
    public void Create_AFileThere_KeepsIt()
    {
        Directory.CreateDirectory(_dir);
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
    public void Update_UnknownBankOrCountryOrNameTakenOrEmpty_IsRefused(string country, string name, string newName, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => new SupportedBanks(File).Update(country, name, new SupportedBank(newName, true, false)));

        Assert.Contains(message, e.Message);
    }
}
