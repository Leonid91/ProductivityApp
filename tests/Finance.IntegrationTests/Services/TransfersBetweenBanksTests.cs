using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Money moved between the person's accounts, at one bank or two, in one currency or two: its two halves are paired
/// into one movement, out of the spending and the income, drawn as one arrow from one account to the other.
/// </summary>
public sealed class TransfersBetweenBanksTests : IDisposable
{
    private readonly TestStore _store = new();
    private readonly long _northwind;
    private readonly long _national;
    private readonly long _exemple;

    public TransfersBetweenBanksTests()
    {
        _northwind = _store.AddBank("Northwind Bank", country: "Canada");
        _national = _store.DefaultBank;
        _exemple = _store.AddBank("Banque Exemple", BankingSystem.France, "EUR", "France");
    }

    public void Dispose() => _store.Dispose();

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static Spec Statement(string bank, List<FakeTransaction> txs, int month = 10, Style style = Style.EnglishWithdrawalsDeposits, string currency = "CAD") =>
        new(style, bank, D(month, 1), D(month, DateTime.DaysInMonth(2026, month)), 1000m, txs, currency);

    private async Task<long> ImportAsync(Spec spec, long bank, string accountName, AccountKind kind = AccountKind.Bank, long? account = null) =>
        (await _store.ImportAsync($"{spec.BankName} {spec.Start:yyyy-MM}.pdf", Render(spec), spec.Currency, kind, account, bank, accountName)).AccountId;

    /// <summary>Invented rates in the European Central Bank's layout (units per euro), newest first; also used to pair halves in two currencies.</summary>
    private ExchangeRates Rates(string csv = "Date,CAD,\n2026-10-12,1.6100,\n2026-10-09,1.6000,\n2026-10-08,1.6000,\n2026-10-07,1.5900,\n")
    {
        var rates = new ExchangeRates(_store.Db);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes(csv));
        _store.Transactions.Convert = (amount, from, to, day) => rates.Convert(amount, from, to, day)?.Amount;
        return rates;
    }

    private PeriodReport Month(int month, string currency = "CAD", ExchangeRates? rates = null) =>
        _store.Summaries.Report(D(month, 1), D(month, DateTime.DaysInMonth(2026, month)), Granularity.Month, currency, rates ?? Rates());

    private static string Box(FlowDiagram d, string account) => d.Nodes.Single(n => n.Name == account).Id;

    private static void AssertInside(FlowDiagram d, string account, string bank)
    {
        var n = d.Nodes.Single(x => x.Name == account);
        var b = d.Banks.Single(x => x.Name == bank);
        Assert.InRange(n.X - n.W / 2, b.X - b.W / 2, b.X + b.W / 2);
        Assert.InRange(n.X + n.W / 2, b.X - b.W / 2, b.X + b.W / 2);
        Assert.InRange(n.Y - n.H / 2, b.Y - b.H / 2, b.Y + b.H / 2);
        Assert.InRange(n.Y + n.H / 2, b.Y - b.H / 2, b.Y + b.H / 2);
    }

    // ------------------------------------------------------------------------------------------------ one currency, two banks

    private static readonly Spec NorthwindOctober = Statement("Northwind Bank",
    [
        new(D(10, 3), "METRO #123 MONTREAL QC", -52.10m),
        new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m),
        new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m),
    ]);

    private static readonly Spec NationalOctober = Statement("National Bank of Canada",
    [
        new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m),
        new(D(10, 25), "LIBRAIRIE EXEMPLE", -38.20m),
    ]);

    /// <summary>In November National sends 300 back to Northwind.</summary>
    private static readonly Spec NationalNovember = Statement("National Bank of Canada",
    [
        new(D(11, 4), "ONLINE TRANSFER TO NORTHWIND BANK", -300m),
        new(D(11, 6), "CAFE EXEMPLE MONTREAL", -4.75m),
    ], 11);

    private async Task<(long Northwind, long National)> NorthwindThenNationalAsync()
    {
        var northwind = await ImportAsync(NorthwindOctober, _northwind, "Northwind chequing");
        var national = await ImportAsync(NationalOctober, _national, "National chequing");
        await ImportAsync(NationalNovember, _national, "National chequing", account: national);
        return (northwind, national);
    }

    private async Task<(long Northwind, long National)> NationalThenNorthwindAsync()
    {
        var national = await ImportAsync(NationalOctober, _national, "National chequing");
        await ImportAsync(NationalNovember, _national, "National chequing", account: national);
        var northwind = await ImportAsync(NorthwindOctober, _northwind, "Northwind chequing");
        return (northwind, national);
    }

    private void AssertPaired(long northwind, long national)
    {
        var txs = _store.Db.AllTransactions();
        var sent = txs.Single(t => t.AccountId == northwind && t.Amount == -500m);
        var received = txs.Single(t => t.AccountId == national && t.Amount == 500m);
        Assert.Equal((received.Id, sent.Id), (sent.LinkedId, received.LinkedId));
        Assert.All([sent, received], t => Assert.Equal(Categories.Transfers, t.CategoryId));
    }

    [Fact]
    public async Task ImportAsync_SendingBankFirst_PairsTheTwoHalvesAsATransfer()
    {
        var (northwind, national) = await NorthwindThenNationalAsync();

        AssertPaired(northwind, national);
    }

    [Fact]
    public async Task ImportAsync_ReceivingBankFirst_PairsTheTwoHalvesAsATransfer()
    {
        var (northwind, national) = await NationalThenNorthwindAsync();

        AssertPaired(northwind, national);
    }

    [Fact]
    public async Task Report_TransferBetweenTwoBanks_IsNeitherSpendingNorIncomeAndTheBanksAddUp()
    {
        await NorthwindThenNationalAsync();

        var october = Month(10);

        Assert.Equal((52.10m + 38.20m, 2100m), (october.Global.Expenses, october.Global.Earnings));
        Assert.Equal((october.Global.Expenses, october.Global.Earnings), (october.Banks.Sum(b => b.Summary.Expenses), october.Banks.Sum(b => b.Summary.Earnings)));
    }

    [Fact]
    public async Task Report_TransferBetweenTwoBanks_IsOneArrowBetweenTheAccountsEachInsideItsBanksBox()
    {
        await NorthwindThenNationalAsync();

        var october = Month(10);

        var movement = Assert.Single(october.Global.Movements);
        Assert.Equal(("Northwind chequing", "National chequing", 500m), (movement.From, movement.To, movement.Amount));
        var d = october.Global.Diagram;
        var arrow = Assert.Single(d.Arrows);
        Assert.Equal((Box(d, "Northwind chequing"), Box(d, "National chequing"), 500m, false), (arrow.From, arrow.To, arrow.Amount, arrow.Outside));
        AssertInside(d, "Northwind chequing", "Northwind Bank");
        AssertInside(d, "National chequing", "National Bank of Canada");
    }

    [Fact]
    public async Task Report_SeveralBanks_DrawsTheDiagramOnceForAllBanks()
    {
        await NorthwindThenNationalAsync();

        var october = Month(10);

        Assert.All(october.Banks, b => Assert.Empty(b.Summary.Diagram.Nodes));
    }

    [Fact]
    public async Task Report_HalfWhoseOtherHalfIsNotImportedYet_GoesOutsideTheApp()
    {
        await NorthwindThenNationalAsync();

        var november = Month(11);

        Assert.Single(november.Global.Movements, x => x.ToOutside && x.Amount == 300m);
    }

    [Fact]
    public async Task ImportAsync_OtherHalfOfAMovementThatWentOutside_PairsThemFromOneAccountToTheOther()
    {
        var (northwind, _) = await NorthwindThenNationalAsync();

        await ImportAsync(Statement("Northwind Bank", [new(D(11, 5), "ONLINE TRANSFER FROM NATIONAL BANK", 300m)], 11), _northwind, "Northwind chequing", account: northwind);

        var november = Month(11);
        var back = Assert.Single(november.Global.Movements);
        Assert.Equal(("National chequing", "Northwind chequing", false, false), (back.From, back.To, back.FromOutside, back.ToOutside));
        Assert.Equal((4.75m, 0m), (november.Global.Expenses, november.Global.Earnings));
    }

    private static byte[] Csv(IEnumerable<FakeTransaction> txs) =>
        Encoding.UTF8.GetBytes("Date,Description,Amount\n" + string.Concat(txs.Select(t => $"{t.Date:yyyy-MM-dd},\"{t.Label}\",{t.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n")));

    [Fact]
    public async Task ImportAsync_CrossingExportsOfBothBanks_AddNothingAndPairNothingTwice()
    {
        var northwind = await ImportAsync(NorthwindOctober, _northwind, "Northwind chequing");
        var national = await ImportAsync(NationalOctober, _national, "National chequing");
        var before = _store.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList();

        await _store.ImportAsync("nw.csv", Csv(NorthwindOctober.Transactions.Where(t => t.Date.Day >= 15)), account: northwind);
        await _store.ImportAsync("nat.csv", Csv(NationalOctober.Transactions.Where(t => t.Date.Day >= 15)), account: national);

        Assert.Equal(before, _store.Db.AllTransactions().Select(t => (t.Id, t.LinkedId, t.CategoryId)).Order().ToList());
        Assert.Single(Month(10).Global.Diagram.Arrows);
    }

    [Fact]
    public async Task Report_TransferAcrossTheEndOfAMonth_IsInNeitherMonthsSpendingOrIncomeAndShowsInBoth()
    {
        await ImportAsync(Statement("Northwind Bank", [new(D(10, 31), "ONLINE TRANSFER TO NATIONAL BANK", -250m)]), _northwind, "Northwind chequing");
        await ImportAsync(Statement("National Bank of Canada", [new(D(11, 2), "ONLINE TRANSFER FROM NORTHWIND BANK", 250m)], 11), _national, "National chequing");

        var (october, november) = (Month(10), Month(11));

        Assert.Equal((0m, 0m, 0m, 0m), (october.Global.Expenses, october.Global.Earnings, november.Global.Expenses, november.Global.Earnings));
        Assert.Contains(october.Global.Movements, m => (m.From, m.To) == ("Northwind chequing", "National chequing"));
        Assert.Contains(november.Global.Movements, m => (m.From, m.To) == ("Northwind chequing", "National chequing"));
    }

    [Fact]
    public async Task ImportAsync_TwoTransfersOfOneAmountToTwoAccounts_PairsEachWithItsOwnAccount()
    {
        var northwind = await ImportAsync(Statement("Northwind Bank",
            [new(D(10, 5), "ONLINE TRANSFER TO NATIONAL BANK", -200m), new(D(10, 6), "ONLINE TRANSFER TO NATIONAL SAVINGS", -200m)]), _northwind, "Northwind chequing");
        var chequing = await ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)]), _national, "National chequing");
        var savings = await ImportAsync(Statement("National Bank of Canada", [new(D(10, 8), "ONLINE TRANSFER FROM NORTHWIND BANK", 200m)]), _national, "National savings", AccountKind.Savings);

        var txs = _store.Db.AllTransactions().ToDictionary(t => t.Id);

        Assert.Equal(chequing, txs[txs.Values.Single(t => t.AccountId == northwind && t.Date == D(10, 5)).LinkedId!.Value].AccountId);
        Assert.Equal(savings, txs[txs.Values.Single(t => t.AccountId == northwind && t.Date == D(10, 6)).LinkedId!.Value].AccountId);
    }

    [Fact]
    public async Task Report_TransfersBetweenAccountsOfOneBank_DrawsTheArrowAndItsAmountInsideTheBanksBox()
    {
        await ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER TO SAVINGS", -400m), new(D(10, 9), "METRO #123 MONTREAL QC", -20m)]), _national, "National chequing");
        await ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM CHEQUING", 400m)]), _national, "National savings", AccountKind.Savings);

        var d = Month(10).Global.Diagram;

        var bank = Assert.Single(d.Banks);
        var arrow = Assert.Single(d.Arrows);
        Assert.Equal((Box(d, "National chequing"), Box(d, "National savings")), (arrow.From, arrow.To));
        var points = Regex.Matches(arrow.Path, @"-?\d+(\.\d+)?\s-?\d+(\.\d+)?").Select(p => p.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
        Assert.All(points, xy => Assert.InRange(xy[0], bank.X - bank.W / 2, bank.X + bank.W / 2));
        Assert.All(points, xy => Assert.InRange(xy[1], bank.Y - bank.H / 2, bank.Y + bank.H / 2));
        Assert.InRange(arrow.LabelY, bank.Y - bank.H / 2, bank.Y + bank.H / 2);
    }

    [Fact]
    public async Task Report_AccountsOfOneNameAtTwoBanks_AreTwoBoxes()
    {
        await ImportAsync(Statement("Northwind Bank", [new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m)]), _northwind, "Chequing");
        await ImportAsync(Statement("National Bank of Canada", [new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m)]), _national, "Chequing");

        var d = Month(10).Global.Diagram;

        Assert.Equal(2, d.Nodes.Count(n => n.Name == "Chequing"));
        Assert.Equal(2, d.Banks.Count);
        var arrow = Assert.Single(d.Arrows);
        Assert.NotEqual(arrow.From, arrow.To);
    }

    // ------------------------------------------------------------------------------------------------ two currencies

    /// <summary>500 EUR sent from the French bank, 790.40 CAD received in Canada the day after (1.6000 that day, 1.2% kept by the banks).</summary>
    private async Task<(long Euro, long Cad)> TwoCurrenciesAsync(decimal received = 790.40m, int sentDay = 8)
    {
        var euro = await ImportAsync(Statement("Banque Exemple", [new(D(10, 9), "CB BOULANGERIE PAUL", -12.40m), new(D(10, sentDay), "VIR SEPA EMIS NORTHWIND BANK", -500m)],
            style: Style.FrenchDebitCredit, currency: "EUR"), _exemple, "Compte courant");
        var cad = await ImportAsync(Statement("Northwind Bank", [new(D(10, sentDay + 1), "WIRE TRANSFER RECEIVED BANQUE EXEMPLE", received), new(D(10, 15), "PAYROLL DEPOSIT EXAMPLE CORP", 2100m)]),
            _northwind, "Northwind chequing");
        return (euro, cad);
    }

    [Fact]
    public async Task ImportAsync_TransferBetweenTwoCurrenciesWithinTheBanksMargin_PairsTheHalves()
    {
        Rates();

        var (euro, cad) = await TwoCurrenciesAsync();

        var txs = _store.Db.AllTransactions();
        Assert.Equal(txs.Single(t => t.AccountId == cad && t.Amount == 790.40m).Id, txs.Single(t => t.AccountId == euro && t.Amount == -500m).LinkedId);
    }

    [Fact]
    public async Task Report_TransferBetweenTwoCurrencies_IsOneMovementOfTheAmountSentInTheDisplayCurrency()
    {
        var rates = Rates();
        await TwoCurrenciesAsync();

        var october = Month(10, rates: rates);

        Assert.Equal((Math.Round(12.40m * 1.6m, 2), 2100m), (october.Global.Expenses, october.Global.Earnings));
        var movement = Assert.Single(october.Global.Movements);
        Assert.Equal(("Compte courant", "Northwind chequing", 800.00m), (movement.From, movement.To, movement.Amount));
        var d = october.Global.Diagram;
        Assert.Equal((Box(d, "Compte courant"), Box(d, "Northwind chequing"), 800.00m), (Assert.Single(d.Arrows).From, d.Arrows[0].To, d.Arrows[0].Amount));
        AssertInside(d, "Compte courant", "Banque Exemple");
    }

    [Fact]
    public async Task Report_TransferBetweenTwoCurrenciesInEuros_IsTheEurosSent()
    {
        var rates = Rates();
        await TwoCurrenciesAsync();

        var october = Month(10, "EUR", rates);

        Assert.Equal((500.00m, 12.40m), (Assert.Single(october.Global.Diagram.Arrows).Amount, october.Global.Expenses));
    }

    [Fact]
    public async Task ImportAsync_SalaryOfAboutTheConvertedAmount_IsNeverTakenForTheOtherHalf()
    {
        Rates();
        await ImportAsync(Statement("Banque Exemple", [new(D(10, 8), "VIR SEPA EMIS NORTHWIND BANK", -500m)], style: Style.FrenchDebitCredit, currency: "EUR"), _exemple, "Compte courant");

        await ImportAsync(Statement("Northwind Bank", [new(D(10, 9), "PAYROLL DEPOSIT EXAMPLE CORP", 798m)]), _northwind, "Northwind chequing");

        Assert.All(_store.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Fact]
    public async Task ImportAsync_AmountTooFarFromTheRate_IsNotTheOtherHalf()
    {
        Rates();

        await TwoCurrenciesAsync(received: 700m); // 12.5% off the rate of 1.60: far more than banks keep

        Assert.All(_store.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Fact]
    public async Task ImportAsync_TransferBetweenCurrenciesBeforeTheFirstRate_IsNotPaired()
    {
        Rates();

        await TwoCurrenciesAsync(sentDay: 2);

        Assert.All(_store.Db.AllTransactions(), t => Assert.Null(t.LinkedId));
    }

    [Fact]
    public async Task ReapplyRules_RatesOfTheTransfersDayArrivedLater_PairsTheHalves()
    {
        var rates = Rates();
        await TwoCurrenciesAsync(sentDay: 2);
        rates.Save(RateSource.Ecb, Encoding.UTF8.GetBytes("Date,CAD,\n2026-10-12,1.6100,\n2026-10-09,1.6000,\n2026-10-08,1.6000,\n2026-10-07,1.5900,\n2026-10-02,1.6000,\n2026-10-01,1.6000,\n"));

        _store.Transactions.ReapplyRules();

        Assert.Equal(2, _store.Db.AllTransactions().Count(t => t.LinkedId is not null));
    }

    // ------------------------------------------------------------------------------------------------ four banks, Wise between them

    /// <summary>The statements of <see cref="FourBanksGenerator"/>, each into a new account at its bank, with 1 EUR = 1.60 CAD = 1.10 USD every day.</summary>
    private async Task<(ExchangeRates Rates, Dictionary<string, long> Accounts)> FourBanksAsync()
    {
        var csv = new StringBuilder("Date,CAD,USD,\n");
        for (var d = D(10, 31); d >= D(9, 25); d = d.AddDays(-1)) csv.Append(CultureInfo.InvariantCulture, $"{d:yyyy-MM-dd},1.6000,1.1000,\n");
        var rates = Rates(csv.ToString());
        var banks = new Dictionary<string, long>();
        var accounts = new Dictionary<string, long>();
        foreach (var f in FourBanksGenerator.Statements())
        {
            if (!banks.TryGetValue(f.Bank, out var bank)) banks[f.Bank] = bank = _store.AddBank(f.Bank, f.System, f.Currency, f.Country);
            accounts[f.Account] = (await _store.ImportAsync(f.FileName, f.Bytes, f.Currency, f.Kind, bank: bank, accountName: f.Account)).AccountId;
        }
        return (rates, accounts);
    }

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
    public async Task ImportAsync_FourBanksInThreeCurrenciesWithWiseBetweenThem_PairsEveryMovement(string from, double sent, string to, double received)
    {
        var (_, accounts) = await FourBanksAsync();

        var txs = _store.Db.AllTransactions();
        var outgoing = txs.Single(t => t.AccountId == accounts[from] && t.Amount == (decimal)sent);
        var incoming = txs.Single(t => t.AccountId == accounts[to] && t.Amount == (decimal)received);
        Assert.Equal((incoming.Id, outgoing.Id), (outgoing.LinkedId, incoming.LinkedId));
    }

    [Fact]
    public async Task Report_FourBanks_CountsOnlyThePurchasesAsSpendingAndTheSalariesAsIncome()
    {
        var (rates, _) = await FourBanksAsync();

        var october = Month(10, rates: rates);

        decimal Cad(decimal amount, string currency) => rates.Convert(amount, currency, "CAD", D(10, 15))!.Amount;
        Assert.Equal(80m + 16.99m + Cad(12.40m, "EUR") + Cad(45m, "USD") + Cad(25m, "USD"), october.Global.Expenses);
        Assert.Equal(4200m + Cad(2500m, "EUR") + Cad(3000m, "USD"), october.Global.Earnings);
        Assert.Equal(october.Global.Expenses, october.Banks.Sum(b => b.Summary.Expenses));
        Assert.Equal(2, october.Global.Movements.Count(m => m.ToOutside && m.CategoryId == Categories.Investments));
    }

    [Fact]
    public async Task Report_FourBanks_SaysWhatTheBanksKeptOnAnExchange()
    {
        var (rates, _) = await FourBanksAsync();

        var movements = Month(10, rates: rates).Global.Movements;

        // 820.00 EUR are 1,312.00 CAD at the day's rate; 1,296.26 CAD arrived.
        var direct = movements.Single(m => m.From == "Compte courant" && m.To == "Northwind chequing");
        Assert.Equal((820m, "EUR", 1296.26m, "CAD", 15.74m), (direct.Sent, direct.SentCurrency, direct.Received, direct.ReceivedCurrency, direct.Lost));
        // Inside Wise: 1,985.00 CAD are 1,240.63 EUR; 1,228.22 EUR arrived: 12.41 EUR kept.
        Assert.Equal(rates.Convert(12.41m, "EUR", "CAD", D(10, 15))!.Amount, movements.Single(m => m.From == "Wise CAD" && m.To == "Wise EUR").Lost);
    }

    [Fact]
    public async Task Report_FourBanks_DrawsEveryBankHoldingItsAccountsReadably()
    {
        var (rates, _) = await FourBanksAsync();

        var d = Month(10, rates: rates).Global.Diagram;

        Assert.Equal(["Banque Exemple", "Contoso Bank", "Northwind Bank", "Wise"], d.Banks.Select(b => b.Name).Order());
        Assert.Equal((11, 1), (d.Nodes.Count(n => !n.Outside), d.Nodes.Count(n => n.Outside)));
        Assert.Contains(d.Arrows, a => a.Lost == 15.74m);
        Assert.Empty(DiagramInspector.Problems(d));
        Assert.InRange(DiagramInspector.Crossings(d), 0, 1);
    }
}
