using System.Globalization;
using System.Net;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Exchange rates of the central banks: downloaded (from a stand-in for the Internet), kept as the banks' own files,
/// two copies at most, chosen as a source, and used to convert an amount at the rate of its day.
/// </summary>
public sealed class ExchangeRatesTests : IDisposable
{
    private static readonly DateOnly October10 = new(2025, 10, 10);

    private readonly TestStore _store = new();

    public void Dispose() => _store.Dispose();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private ExchangeRates WithEcbRates()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Utf8(RateFiles.EuropeanCentralBank));
        return rates;
    }

    // ------------------------------------------------------------------------------------------------ converting

    [Fact]
    public void Convert_EurosToDollarsOnADayWithARate_UsesThatDaysRate()
    {
        var result = WithEcbRates().Convert(100m, "EUR", "CAD", October10);

        Assert.Equal(new Converted(162.00m, 1.62m, October10), result);
    }

    [Fact]
    public void Convert_OnASaturday_UsesFridaysRate()
    {
        var result = WithEcbRates().Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 11));

        Assert.Equal(October10, result!.RateDay);
    }

    [Fact]
    public void Convert_DollarsToEuros_DividesAndRoundsToTheCent()
    {
        var result = WithEcbRates().Convert(100m, "CAD", "EUR", October10);

        Assert.Equal(61.73m, result!.Amount);
    }

    [Fact]
    public void Convert_BetweenTwoCurrenciesOtherThanTheEuro_GoesThroughTheEuroTheSameDay()
    {
        var result = WithEcbRates().Convert(100m, "CAD", "USD", October10);

        Assert.Equal((Math.Round(100m * 1.15m / 1.62m, 2, MidpointRounding.AwayFromZero), 0.709877m), (result!.Amount, result.Rate));
    }

    [Fact]
    public void Convert_DayWithoutARateForTheCurrency_UsesTheLastDayThatHadOne()
    {
        var result = WithEcbRates().Convert(10m, "EUR", "GBP", new DateOnly(2025, 10, 9));

        Assert.Equal(new DateOnly(2025, 10, 8), result!.RateDay);
    }

    [Theory]
    [InlineData("CAD", "2025-10-01")] // before the first rate
    [InlineData("JPY", "2025-10-10")] // a currency without rates
    public void Convert_NoRateKnown_ReturnsNull(string currency, string day)
    {
        var result = WithEcbRates().Convert(10m, "EUR", currency, DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture));

        Assert.Null(result);
    }

    [Fact]
    public void Convert_SameCurrency_ReturnsTheAmountWhateverTheDay()
    {
        var result = WithEcbRates().Convert(10m, "EUR", "EUR", new DateOnly(2025, 10, 1));

        Assert.Equal(new Converted(10m, 1m, new DateOnly(2025, 10, 1)), result);
    }

    [Fact]
    public void Status_AfterSaving_GivesTheLatestDay()
    {
        var status = WithEcbRates().Status();

        Assert.Equal("2025-10-10", status.Latest);
    }

    [Fact]
    public void Convert_BankOfCanadasRates_UsesItsOwnFigureAgainstTheCanadianDollar()
    {
        var rates = new ExchangeRates(_store.Db, () => "boc");
        rates.Save(RateSource.BankOfCanada, Utf8(RateFiles.BankOfCanada));

        Assert.Equal(("Bank of Canada", "CAD"), (rates.Source.Name, rates.Source.Base));
        Assert.Equal(new Converted(162.00m, 1.62m, October10), rates.Convert(100m, "EUR", "CAD", October10));
        Assert.Equal(Math.Round(100m * 1.62m / 1.39m, 2, MidpointRounding.AwayFromZero), rates.Convert(100m, "EUR", "USD", October10)!.Amount);
    }

    [Fact]
    public void Knows_CurrencyWhoseValueIsEmptyInTheFile_IsFalse()
    {
        var rates = new ExchangeRates(_store.Db, () => "boc");

        rates.Save(RateSource.BankOfCanada, Utf8(RateFiles.BankOfCanada));

        Assert.False(rates.Knows("VND"));
    }

    [Fact]
    public void Convert_AfterGoingBackToAnotherSource_UsesThatSourcesOwnCopyOnly()
    {
        var source = "boc";
        var rates = new ExchangeRates(_store.Db, () => source);
        rates.Save(RateSource.BankOfCanada, Utf8(RateFiles.BankOfCanada));

        source = "ecb";

        Assert.Null(rates.Convert(100m, "EUR", "CAD", October10));
        Assert.Equal(("European Central Bank", "EUR"), (rates.Status().Source, rates.Status().Base));
    }

    [Fact]
    public void Convert_FederalReservesRates_GoThroughTheUsDollar()
    {
        var rates = new ExchangeRates(_store.Db, () => "fed");

        rates.Save(RateSource.FederalReserve, Utf8(RateFiles.FederalReserve));

        Assert.Equal(120.00m, rates.Convert(100m, "EUR", "USD", October10)!.Amount);
        Assert.Equal(166.80m, rates.Convert(100m, "EUR", "CAD", October10)!.Amount);
        Assert.Equal(new DateOnly(2025, 10, 8), rates.Convert(1m, "EUR", "USD", new DateOnly(2025, 10, 9))!.RateDay);
    }

    [Fact]
    public void Daily_PairOverDaysWithAndWithoutRates_GivesEveryDayWithTheLastRatePublished()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,USD,\n2026-10-12,1.6000,1.2000,\n2026-10-09,1.5000,1.0000,\n"));

        var days = rates.Daily("CAD", "EUR", new(2026, 10, 8), new(2026, 10, 12));

        Assert.Equal(["2026-10-09", "2026-10-10", "2026-10-11", "2026-10-12"], days.Select(d => d.Day));
        Assert.Equal([0.666667m, 0.666667m, 0.666667m, 0.625m], days.Select(d => d.Rate));
        Assert.Equal("2026-10-09", days[2].RateDay);
    }

    [Fact]
    public void Daily_OtherWayOrBetweenTwoCurrenciesOtherThanTheEuro_GivesTheirRate()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Utf8("Date,CAD,USD,\n2026-10-12,1.6000,1.2000,\n2026-10-09,1.5000,1.0000,\n"));

        Assert.Equal(["CAD", "EUR", "USD"], rates.Currencies());
        Assert.Equal(1.5m, rates.Daily("EUR", "CAD", new(2026, 10, 9), new(2026, 10, 9)).Single().Rate);
        Assert.Equal(1.333333m, rates.Daily("USD", "CAD", new(2026, 10, 12), new(2026, 10, 12)).Single().Rate);
    }

    [Fact]
    public void Daily_MoreThanTheLongestPeriodShown_Throws()
    {
        var rates = WithEcbRates();

        Assert.Throws<ArgumentException>(() => rates.Daily("CAD", "EUR", new(2026, 1, 1), new(2026, 12, 31)));
    }

    // ------------------------------------------------------------------------------------------------ the table of sources

    [Fact]
    public void Sources_NewDatabase_AreTheThreeCentralBanksTheAppReads()
    {
        var sources = new ExchangeRates(_store.Db).Sources();

        Assert.Equal(["European Central Bank", "Bank of Canada", "US Federal Reserve"], sources.Select(x => x.Name));
        Assert.Equal(["ecb", "boc", "fed"], sources.Select(x => x.Format));
        Assert.Equal(CentralBank.FederalReserve.DefaultUrl, sources[2].Url);
    }

    [Fact]
    public void SaveSource_BankAlreadyInTheTable_Throws() =>
        Assert.Throws<ArgumentException>(() => new ExchangeRates(_store.Db).SaveSource(null, "fed", CentralBank.FederalReserve.DefaultUrl));

    [Fact]
    public void SaveSource_BankRemovedBefore_IsAddedAgainWithTheAddressTrimmed()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.RemoveSource("fed");

        var fed = rates.SaveSource(null, "fed", " " + CentralBank.FederalReserve.DefaultUrl + " ");

        Assert.Equal(("US Federal Reserve", "USD", CentralBank.FederalReserve.DefaultUrl), (fed.Name, fed.Base, fed.Url));
        Assert.Equal(3, new ExchangeRates(_store.Db).Sources().Count);
    }

    [Theory]
    [InlineData("nowhere", "https://example.test/x")] // not a bank the app reads
    [InlineData("fed", "http://example.test/rates.csv")] // not https
    [InlineData("fed", "rates.csv")]                   // not a full address
    public void SaveSource_UnknownBankOrAddressNotFullHttps_Throws(string format, string url)
    {
        var rates = new ExchangeRates(_store.Db);
        rates.RemoveSource("fed");

        Assert.Throws<ArgumentException>(() => rates.SaveSource(null, format, url));
    }

    [Fact]
    public void SaveSource_NewAddress_DropsWhatTheOldOneGaveAndKeepsTheBank()
    {
        var rates = new ExchangeRates(_store.Db);
        var fed = rates.Sources().Single(s => s.Format == "fed");
        rates.Save(fed, Utf8(RateFiles.FederalReserve));

        var saved = rates.SaveSource(fed.Id, "ecb", "https://example.test/other.csv");

        Assert.Equal("fed", saved.Format);
        Assert.Empty(rates.Copies(fed));
        Assert.Equal((0, (string?)null), (rates.Views().Single(v => v.Id == fed.Id).Currencies, rates.Views().Single(v => v.Id == fed.Id).Latest));
    }

    [Fact]
    public void RemoveSource_SourceInUse_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new ExchangeRates(_store.Db).RemoveSource("ecb"));

    // ------------------------------------------------------------------------------------------------ downloading

    /// <summary>The error of a download whose address answers <paramref name="code"/> and <paramref name="body"/>.</summary>
    private static async Task<string?> UpdateAsync(ExchangeRates rates, string id, HttpStatusCode code, string body)
    {
        using var web = new FakeWeb(code, body);
        using var client = web.Client();
        return (await rates.UpdateAsync(id, client, TestContext.Current.CancellationToken)).Error;
    }

    /// <summary>The error of a download whose connection fails with <paramref name="fault"/>.</summary>
    private static async Task<string?> UpdateAsync(ExchangeRates rates, string id, Exception fault)
    {
        using var web = new BrokenWeb(fault);
        using var client = web.Client();
        return (await rates.UpdateAsync(id, client, TestContext.Current.CancellationToken)).Error;
    }

    public static TheoryData<HttpStatusCode, string, string> WrongAnswers => new()
    {
        { HttpStatusCode.NotFound, "", "Nothing at this address (404): check it." },
        { HttpStatusCode.OK, "<!doctype html><html></html>", "This address gives a web page, not a file of rates" },
        { HttpStatusCode.OK, " \r\n", "The address gave an empty answer: try again later." },
        { HttpStatusCode.OK, "{ broken", "The file at this address is not a file of exchange rates the app can read." },
        { HttpStatusCode.OK, """{"observations":[]}""", "The file at this address holds no exchange rates." },
        { HttpStatusCode.OK, RateFiles.EuropeanCentralBank, "This file is not in the layout of the Bank of Canada: check the address." },
    };

    [Theory]
    [MemberData(nameof(WrongAnswers))]
    public async Task UpdateAsync_AddressGivingWhatIsNotTheBanksRates_SaysPlainlyWhatIsWrong(HttpStatusCode code, string body, string error)
    {
        var rates = new ExchangeRates(_store.Db, () => "boc");

        var result = await UpdateAsync(rates, "boc", code, body);

        Assert.StartsWith(error, result);
    }

    [Theory]
    [InlineData("hello", "The file at this address is not a table of exchange rates")]
    [InlineData("Date,EUR,USD\n2025-10-10,1,1.15", "The table does not say which currency")]
    public async Task UpdateAsync_EuropeanCentralBankAddressGivingAnotherTable_SaysWhatIsWrong(string body, string error)
    {
        var result = await UpdateAsync(new ExchangeRates(_store.Db), "ecb", HttpStatusCode.OK, body);

        Assert.StartsWith(error, result);
    }

    [Fact]
    public async Task UpdateAsync_SomethingUnexpected_SaysItPlainlyAndKeepsNoRate()
    {
        var rates = new ExchangeRates(_store.Db, () => "boc");

        var error = await UpdateAsync(rates, "boc", new InvalidOperationException("disk full"));

        Assert.Equal("The rates could not be downloaded: disk full", error);
        Assert.Null(rates.Views().Single(v => v.Id == "boc").Latest);
    }

    [Fact]
    public async Task UpdateAsync_TheBanksOwnFile_GivesItsRatesWithoutError()
    {
        var rates = new ExchangeRates(_store.Db, () => "boc");

        var error = await UpdateAsync(rates, "boc", HttpStatusCode.OK, RateFiles.BankOfCanada);

        Assert.Null(error);
        Assert.Equal("2025-10-10", rates.Views().Single(v => v.Id == "boc").Latest);
        Assert.Equal(162.00m, rates.Convert(100m, "EUR", "CAD", October10)!.Amount);
    }

    [Fact]
    public async Task UpdateAsync_AddressThatIsNotHttps_IsRefused()
    {
        _store.Db.SaveRateSource(RateSource.BankOfCanada with { Url = "http://example.test/rates" });
        var rates = new ExchangeRates(_store.Db, () => "boc");

        var error = await UpdateAsync(rates, "boc", HttpStatusCode.OK, RateFiles.BankOfCanada);

        Assert.Equal("The address must be a full web address starting with https://.", error);
    }

    /// <summary>Each central bank's site answers its own file (any other site is not in the table: the test fails).</summary>
    private static readonly Dictionary<string, string> FileOfSite = new()
    {
        ["www.ecb.europa.eu"] = RateFiles.EuropeanCentralBank,
        ["www.bankofcanada.ca"] = RateFiles.BankOfCanada,
        ["www.federalreserve.gov"] = RateFiles.FederalReserve,
    };

    private static FakeWeb EveryBank() => new(u => (HttpStatusCode.OK, FileOfSite[u.Host]));

    [Fact]
    public async Task UpdateAllAsync_NothingDownloadedYet_DownloadsEveryBankOnceAndReadsItsRates()
    {
        var rates = new ExchangeRates(_store.Db);
        using var web = EveryBank();
        using var client = web.Client();

        var changed = await rates.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(3, web.Calls);
        Assert.All(rates.Views(), v => Assert.Equal(("2025-10-10", (string?)null), (v.Latest, v.Error)));
        Assert.Equal([4, 3, 4], rates.Views().Select(v => v.Currencies));
    }

    [Fact]
    public async Task UpdateAllAsync_AlreadyDownloadedToday_DownloadsNothing()
    {
        var rates = new ExchangeRates(_store.Db);
        using var web = EveryBank();
        using var client = web.Client();
        await rates.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        var changed = await rates.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        Assert.False(changed);
        Assert.Equal(3, web.Calls);
    }

    [Fact]
    public async Task UpdateAllAsync_RefreshAskedTheSameDay_DownloadsEveryBankAgainKeepingOneCopyEach()
    {
        var rates = new ExchangeRates(_store.Db);
        using var web = EveryBank();
        using var client = web.Client();
        await rates.UpdateAllAsync(force: false, client, TestContext.Current.CancellationToken);

        var changed = await rates.UpdateAllAsync(force: true, client, TestContext.Current.CancellationToken);

        Assert.True(changed);
        Assert.Equal(6, web.Calls);
        Assert.All(rates.Sources(), x => Assert.Single(rates.Copies(x)));
    }

    [Fact]
    public void DueToday_NeverDownloaded_IsTrue() => Assert.True(new ExchangeRates(_store.Db).DueToday());

    [Fact]
    public void DueToday_DownloadedToday_IsFalse()
    {
        _store.Db.SetSetting("rates.ecb.updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        Assert.False(new ExchangeRates(_store.Db).DueToday());
    }

    [Fact]
    public void DueToday_DownloadedYesterday_IsTrue()
    {
        _store.Db.SetSetting("rates.ecb.updatedAt", DateTime.Today.AddDays(-1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        Assert.True(new ExchangeRates(_store.Db).DueToday());
    }

    // ------------------------------------------------------------------------------------------------ the copies kept

    private static readonly DateOnly Day1 = new(2025, 10, 8);

    private static byte[] Ecb(string day, string rate) => Utf8($"Date,CAD,\n{day},{rate},");

    private static List<string> Days(ExchangeRates rates) => [.. rates.Copies(RateSource.Ecb).Select(c => c.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];

    [Fact]
    public void Save_FirstDownload_KeepsTheBanksFileAsDownloadedInAFolderOfItsBankAndDay()
    {
        var rates = new ExchangeRates(_store.Db);

        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);

        var copy = Assert.Single(rates.Copies(RateSource.Ecb));
        Assert.Equal(Path.Combine(_store.Db.DownloadsFolder, "exchange-rates", "european-central-bank", "2025-10-08"), copy.Folder);
        Assert.Equal(["rates.csv"], copy.Files);
        Assert.Equal("Date,CAD,\n2025-10-08,1.5000,", File.ReadAllText(Path.Combine(copy.Folder, "rates.csv")));
    }

    [Fact]
    public void Save_AgainTheSameDay_ReplacesTheLatestCopy()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);

        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5100"), Day1);

        Assert.Equal(["2025-10-08"], Days(rates));
        Assert.Equal(1.51m, rates.Convert(1m, "EUR", "CAD", Day1)!.Amount);
    }

    [Fact]
    public void Save_AnotherDay_KeepsTheCopyBeforeAndTheLatest()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);

        rates.Save(RateSource.Ecb, Ecb("2025-10-09", "1.6000"), Day1.AddDays(1));

        Assert.Equal(["2025-10-09", "2025-10-08"], Days(rates));
    }

    [Fact]
    public void Save_ThirdDay_DeletesTheOldestCopyKeepingTwo()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);
        rates.Save(RateSource.Ecb, Ecb("2025-10-09", "1.6000"), Day1.AddDays(1));

        rates.Save(RateSource.Ecb, Ecb("2025-10-10", "1.7000"), Day1.AddDays(2));

        Assert.Equal(["2025-10-10", "2025-10-09"], Days(rates));
        Assert.False(Directory.Exists(Path.Combine(_store.Db.DownloadsFolder, "exchange-rates", "european-central-bank", "2025-10-08")));
        Assert.Equal((1.7m, false), (rates.Convert(1m, "EUR", "CAD", October10)!.Amount, rates.Status().FromPrevious));
    }

    [Fact]
    public void Reload_LatestCopyThatCannotBeRead_UsesTheOneBeforeAndSaysSo()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-09", "1.6100"), Day1.AddDays(1));
        rates.Save(RateSource.Ecb, Ecb("2025-10-10", "1.7000"), Day1.AddDays(2));
        File.WriteAllText(Path.Combine(rates.Copies(RateSource.Ecb)[0].Folder, "rates.csv"), "garbage");

        rates.Reload();

        Assert.Equal((1.61m, true), (rates.Convert(1m, "EUR", "CAD", October10.AddDays(-1))!.Amount, rates.Status().FromPrevious));
        // Its rates end on the day it was downloaded: the next day's rate is only in the copy that cannot be read.
        Assert.Null(rates.Convert(1m, "EUR", "CAD", October10));
    }

    [Fact]
    public void Convert_DayAfterTheRatesInUseWereDownloaded_HasNoRate()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);

        var tomorrow = rates.Convert(100m, "EUR", "CAD", Day1.AddDays(1));
        var daily = rates.Daily("EUR", "CAD", Day1, Day1.AddDays(5));

        Assert.Null(tomorrow);
        Assert.Equal(["2025-10-08"], daily.Select(d => d.Day));
    }

    [Fact]
    public void Convert_WeekendBeforeTheDownload_TakesTheRateOfTheFridayBefore()
    {
        var rates = new ExchangeRates(_store.Db);
        // Friday October 10, 2025, downloaded on Monday 13: Saturday and Sunday have no rate of their own.
        rates.Save(RateSource.Ecb, Ecb("2025-10-10", "1.6200"), new DateOnly(2025, 10, 13));

        var sunday = rates.Convert(100m, "EUR", "CAD", new DateOnly(2025, 10, 12));

        Assert.Equal(new Converted(162.00m, 1.62m, October10), sunday);
    }

    [Fact]
    public void Save_FileThatIsNotRates_ThrowsAndKeepsTheCopies()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Ecb("2025-10-08", "1.5000"), Day1);

        Assert.Throws<RatesFileException>(() => rates.Save(RateSource.Ecb, Utf8("<html></html>"), Day1.AddDays(3)));
        Assert.Equal(["2025-10-08"], Days(rates));
    }

    [Fact]
    public void RemoveSource_BankWithDownloadedRates_DeletesItsFiles()
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.BankOfCanada, Utf8(RateFiles.BankOfCanada), Day1);
        Assert.Equal(["rates.json"], rates.Copies(RateSource.BankOfCanada)[0].Files);

        rates.RemoveSource("boc");

        Assert.False(Directory.Exists(Path.Combine(_store.Db.DownloadsFolder, "exchange-rates", "bank-of-canada")));
    }
}
