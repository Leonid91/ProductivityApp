using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.TransferBanks;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Money moved between the person's accounts, at one bank or two, in one currency or two (<see cref="TransferBanks"/>):
/// once both statements are saved, its two halves are paired into one movement. The four banks of
/// <see cref="Testing.FourBanksGenerator"/> are saved once for the tests that only read the pairs.
/// </summary>
public sealed class ImportServiceTransfersBetweenBanksTests : IClassFixture<ReadOnce>, IDisposable
{
    private readonly ReadOnce read;
    private readonly TestStore _store = new();

    /// <summary>The banks, in this test's own database.</summary>
    private readonly TransferBanks Banks;

    public ImportServiceTransfersBetweenBanksTests(ReadOnce read)
    {
        this.read = read;
        Banks = new TransferBanks(_store);
    }

    public void Dispose() => _store.Dispose();

    private void AssertPaired(long northwind, long national)
    {
        var txs = _store.Db.AllTransactions();
        var sent = txs.Single(t => t.AccountId == northwind && t.Amount == -500m);
        var received = txs.Single(t => t.AccountId == national && t.Amount == 500m);
        Assert.Equal((received.Id, sent.Id), (sent.LinkedId, received.LinkedId));
        Assert.All([sent, received], t => Assert.Equal(Categories.Transfers, t.CategoryId));
    }

    [Fact]
    public async Task Commit_SendingBankFirst_PairsTheTwoHalvesAsATransfer()
    {
        var (northwind, national) = await Banks.NorthwindThenNationalAsync();

        AssertPaired(northwind, national);
    }

    [Fact]
    public async Task Commit_ReceivingBankFirst_PairsTheTwoHalvesAsATransfer()
    {
        var (northwind, national) = await Banks.NationalThenNorthwindAsync();

        AssertPaired(northwind, national);
    }

    [Fact]
    public async Task Commit_OtherHalfOfAMovementThatWentOutside_PairsThemFromOneAccountToTheOther()
    {
        var (northwind, _) = await Banks.NorthwindThenNationalAsync();

        await Banks.ImportIntoAsync(Statement("Northwind Bank", [new(D(11, 5), "ONLINE TRANSFER FROM NATIONAL BANK", 300m)], 11), northwind);

        var november = Banks.Month(11);
        var back = Assert.Single(november.Global.Movements);
        Assert.Equal(("National chequing", "Northwind chequing", false, false), (back.From, back.To, back.FromOutside, back.ToOutside));
        Assert.Equal((4.75m, 0m), (november.Global.Expenses, november.Global.Earnings));
    }

    [Fact]
    public async Task Commit_CrossingExportsOfBothBanks_AddNothingAndPairNothingTwice()
    {
        var northwind = await Banks.ImportAsync(NorthwindOctober, Banks.Northwind, "Northwind chequing");
        var national = await Banks.ImportAsync(NationalOctober, Banks.National, "National chequing");
        var before = _store.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList();

        await _store.ImportIntoAsync(northwind, "nw.csv", Csv(NorthwindOctober.Transactions.Where(t => t.Date.Day >= 15)));
        await _store.ImportIntoAsync(national, "nat.csv", Csv(NationalOctober.Transactions.Where(t => t.Date.Day >= 15)));

        Assert.Equal(before, _store.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList());
        Assert.Single(Banks.Month(10).Global.Diagram.Arrows);
    }

    [Fact]
    public async Task Commit_TwoTransfersOfOneAmountToTwoAccounts_PairsEachWithItsOwnAccount()
    {
        var northwind = await Banks.ImportAsync(Statement("Northwind Bank",
            [new(D(10, 5), "ONLINE TRANSFER TO NATIONAL BANK", -200m), new(D(10, 6), "ONLINE TRANSFER TO NATIONAL SAVINGS", -200m)]), Banks.Northwind, "Northwind chequing");
        var chequing = await Banks.ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)]), Banks.National, "National chequing");

        var savings = await Banks.ImportAsync(Statement("National Bank of Canada", [new(D(10, 8), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)]), Banks.National, "National savings", AccountKind.Savings);

        var txs = _store.Db.AllTransactions().ToDictionary(t => t.Id);
        Assert.Equal(chequing, txs[txs.Values.Single(t => t.AccountId == northwind && t.Date == D(10, 5)).LinkedId!.Value].AccountId);
        Assert.Equal(savings, txs[txs.Values.Single(t => t.AccountId == northwind && t.Date == D(10, 6)).LinkedId!.Value].AccountId);
    }

    [Fact]
    public async Task Commit_TransferBetweenTwoCurrenciesWithinTheBanksMargin_PairsTheHalves()
    {
        Banks.Rates();

        var (euro, cad) = await Banks.TwoCurrenciesAsync();

        var txs = _store.Db.AllTransactions();
        Assert.Equal(txs.Single(t => t.AccountId == cad && t.Amount == 790.40m).Id, txs.Single(t => t.AccountId == euro && t.Amount == -500m).LinkedId);
    }

    [Fact]
    public async Task Commit_SalaryOfAboutTheConvertedAmount_IsNeverTakenForTheOtherHalf()
    {
        Banks.Rates();
        await Banks.ImportAsync(Statement("Banque Exemple", [new(D(10, 8), "VIR SEPA EMIS NORTHWIND BANK", -500m)], style: Style.FrenchDebitCredit, currency: "EUR"), Banks.Exemple, "Compte courant");

        await Banks.ImportAsync(Statement("Northwind Bank", [new(D(10, 9), "PAYROLL DEPOSIT EXAMPLE CORP", 798m)]), Banks.Northwind, "Northwind chequing");

        Assert.All(_store.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Theory]
    [InlineData(700, 8)]    // 12.5% off the rate of 1.60: far more than banks keep
    [InlineData(790.40, 2)] // sent before the first rate
    public async Task Commit_AmountTooFarFromTheRateOrSentBeforeTheFirstRate_IsNotTheOtherHalf(double received, int sentDay)
    {
        Banks.Rates();

        await Banks.TwoCurrenciesAsync((decimal)received, sentDay);

        Assert.All(_store.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    // ------------------------------------------------------------------------------------------------ four banks, Wise between them

    public static TheoryData<string, double, string, double> FourBanksMovements => new()
    {
        { "Northwind chequing", -2000, "Wise CAD", 2000 },           // into Wise
        { "Wise CAD", -1985, "Wise EUR", 1228.22 },                  // between its currencies
        { "Wise EUR", -1228.22, "Compte courant", 1228.22 },         // out of it
        { "Contoso checking", -1500, "Wise USD", 1500 },
        { "Wise USD", -1490, "Wise CAD", 2120.00 },
        { "Compte courant", -820, "Northwind chequing", 1296.26 },   // one bank to another without Wise
        { "Northwind chequing", -900, "Northwind savings", 900 },    // inside each bank
        { "Northwind chequing", -1240, "Northwind card", 1240 },
        { "Compte courant", -600, "Livret A", 600 },
        { "Contoso checking", -700, "Contoso savings", 700 },
        { "Contoso checking", -830, "Contoso card", 830 },
    };

    [Theory]
    [MemberData(nameof(FourBanksMovements))]
    public async Task Commit_FourBanksInThreeCurrenciesWithWiseBetweenThem_PairsEveryMovement(string from, double sent, string to, double received)
    {
        var (_, accounts) = await read.GetAsync("four banks", store => new TransferBanks(store).FourBanksAsync());

        var txs = (await read.StoreAsync("four banks")).Db.AllTransactions();
        var outgoing = txs.Single(t => t.AccountId == accounts[from] && t.Amount == (decimal)sent);
        var incoming = txs.Single(t => t.AccountId == accounts[to] && t.Amount == (decimal)received);
        Assert.Equal((incoming.Id, outgoing.Id), (outgoing.LinkedId, incoming.LinkedId));
    }
}
