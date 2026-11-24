using System.Globalization;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.TransferBanks;
using static ProductivityApp.Finance.Testing.StatementGenerator;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// Money moved between the person's accounts (<see cref="TransferBanks"/>) in the report of a month: out of the
/// spending and the income, drawn as one arrow from one account to the other. A report that several tests read is
/// made once.
/// </summary>
public sealed class SummaryServiceTransfersBetweenBanksTests(ReadOnce read) : IClassFixture<ReadOnce>
{
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

    /// <summary>October or November once Northwind's statement then the National Bank's two were saved.</summary>
    private Task<PeriodReport> NorthwindThenNationalAsync(int month) => read.GetAsync($"northwind then national, month {month}", async store =>
    {
        var banks = new TransferBanks(store);
        await banks.NorthwindThenNationalAsync();
        return banks.Month(month);
    });

    /// <summary>October once the four banks of <see cref="FourBanksGenerator"/> were saved, with their rates.</summary>
    private Task<(ExchangeRates Rates, PeriodReport October)> FourBanksAsync() => read.GetAsync("four banks", async store =>
    {
        var banks = new TransferBanks(store);
        var (rates, _) = await banks.FourBanksAsync();
        return (rates, banks.Month(10, rates));
    });

    [Fact]
    public async Task Report_TransferBetweenTwoBanks_IsNeitherSpendingNorIncomeAndTheBanksAddUp()
    {
        var october = await NorthwindThenNationalAsync(10);

        Assert.Equal((52.10m + 38.20m, 2100m), (october.Global.Expenses, october.Global.Earnings));
        Assert.Equal((october.Global.Expenses, october.Global.Earnings), (october.Banks.Sum(b => b.Summary.Expenses), october.Banks.Sum(b => b.Summary.Earnings)));
    }

    [Fact]
    public async Task Report_TransferBetweenTwoBanks_IsOneArrowBetweenTheAccountsEachInsideItsBanksBox()
    {
        var october = await NorthwindThenNationalAsync(10);

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
        var october = await NorthwindThenNationalAsync(10);

        Assert.All(october.Banks, b => Assert.Empty(b.Summary.Diagram.Nodes));
    }

    [Fact]
    public async Task Report_HalfWhoseOtherHalfIsNotImportedYet_GoesOutsideTheApp()
    {
        var november = await NorthwindThenNationalAsync(11);

        Assert.Single(november.Global.Movements, x => x.ToOutside && x.Amount == 300m);
    }

    [Fact]
    public async Task Report_TransferAcrossTheEndOfAMonth_IsInNeitherMonthsSpendingOrIncomeAndShowsInBoth()
    {
        using var store = new TestStore();
        var banks = new TransferBanks(store);
        await banks.ImportAsync(Statement("Northwind Bank", [new(D(10, 31), "ONLINE TRANSFER TO NATIONAL BANK", -250m)]), banks.Northwind, "Northwind chequing");
        await banks.ImportAsync(Statement("National Bank of Canada", [new(D(11, 2), "ONLINE TRANSFER FROM NORTHWIND BANK", 250m)], 11), banks.National, "National chequing");

        var (october, november) = (banks.Month(10), banks.Month(11));

        Assert.Equal((0m, 0m, 0m, 0m), (october.Global.Expenses, october.Global.Earnings, november.Global.Expenses, november.Global.Earnings));
        Assert.Contains(october.Global.Movements, m => (m.From, m.To) == ("Northwind chequing", "National chequing"));
        Assert.Contains(november.Global.Movements, m => (m.From, m.To) == ("Northwind chequing", "National chequing"));
    }

    [Fact]
    public async Task Report_TransfersBetweenAccountsOfOneBank_DrawsTheArrowAndItsAmountInsideTheBanksBox()
    {
        using var store = new TestStore();
        var banks = new TransferBanks(store);
        await banks.ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER TO SAVINGS", -400m), new(D(10, 9), "METRO #123 MONTREAL QC", -20m)]), banks.National, "National chequing");
        await banks.ImportAsync(Statement("National Bank of Canada", [new(D(10, 5), "ONLINE TRANSFER FROM CHEQUING", 400m)]), banks.National, "National savings", AccountKind.Savings);

        var d = banks.Month(10).Global.Diagram;

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
        using var store = new TestStore();
        var banks = new TransferBanks(store);
        await banks.ImportAsync(Statement("Northwind Bank", [new(D(10, 20), "ONLINE TRANSFER TO NATIONAL BANK", -500m)]), banks.Northwind, "Chequing");
        await banks.ImportAsync(Statement("National Bank of Canada", [new(D(10, 21), "ONLINE TRANSFER FROM NORTHWIND BANK", 500m)]), banks.National, "Chequing");

        var d = banks.Month(10).Global.Diagram;

        Assert.Equal(2, d.Nodes.Count(n => n.Name == "Chequing"));
        Assert.Equal(2, d.Banks.Count);
        var arrow = Assert.Single(d.Arrows);
        Assert.NotEqual(arrow.From, arrow.To);
    }

    [Fact]
    public async Task Report_TransferBetweenTwoCurrencies_IsOneMovementOfTheAmountSentInTheDisplayCurrency()
    {
        using var store = new TestStore();
        var banks = new TransferBanks(store);
        var rates = banks.Rates();
        await banks.TwoCurrenciesAsync();

        var october = banks.Month(10, rates);

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
        using var store = new TestStore();
        var banks = new TransferBanks(store);
        var rates = banks.Rates();
        await banks.TwoCurrenciesAsync();

        var october = banks.Month(10, rates, "EUR");

        Assert.Equal((500.00m, 12.40m), (Assert.Single(october.Global.Diagram.Arrows).Amount, october.Global.Expenses));
    }

    [Fact]
    public async Task Report_FourBanks_CountsOnlyThePurchasesAsSpendingAndTheSalariesAsIncome()
    {
        var (rates, october) = await FourBanksAsync();

        decimal Cad(decimal amount, string currency) => rates.Convert(amount, currency, "CAD", D(10, 15))!.Amount;
        Assert.Equal(80m + 16.99m + Cad(12.40m, "EUR") + Cad(45m, "USD") + Cad(25m, "USD"), october.Global.Expenses);
        Assert.Equal(4200m + Cad(2500m, "EUR") + Cad(3000m, "USD"), october.Global.Earnings);
        Assert.Equal(october.Global.Expenses, october.Banks.Sum(b => b.Summary.Expenses));
        Assert.Equal(2, october.Global.Movements.Count(m => m.ToOutside && m.CategoryId == Categories.Investments));
    }

    [Fact]
    public async Task Report_FourBanks_SaysWhatTheBanksKeptOnAnExchange()
    {
        var (rates, october) = await FourBanksAsync();

        var movements = october.Global.Movements;
        // 820.00 EUR are 1,312.00 CAD at the day's rate; 1,296.26 CAD arrived.
        var direct = movements.Single(m => m.From == "Compte courant" && m.To == "Northwind chequing");
        Assert.Equal((820m, "EUR", 1296.26m, "CAD", 15.74m), (direct.Sent, direct.SentCurrency, direct.Received, direct.ReceivedCurrency, direct.Lost));
        // Inside Wise: 1,985.00 CAD are 1,240.63 EUR; 1,228.22 EUR arrived: 12.41 EUR kept.
        Assert.Equal(rates.Convert(12.41m, "EUR", "CAD", D(10, 15))!.Amount, movements.Single(m => m.From == "Wise CAD" && m.To == "Wise EUR").Lost);
    }

    [Fact]
    public async Task Report_FourBanks_DrawsEveryBankHoldingItsAccountsReadably()
    {
        var (_, october) = await FourBanksAsync();

        var d = october.Global.Diagram;
        Assert.Equal(["Banque Exemple", "Contoso Bank", "Northwind Bank", "Wise"], d.Banks.Select(b => b.Name).Order());
        Assert.Equal((11, 1), (d.Nodes.Count(n => !n.Outside), d.Nodes.Count(n => n.Outside)));
        Assert.Contains(d.Arrows, a => a.Lost == 15.74m);
        Assert.Empty(DiagramInspector.Problems(d));
        Assert.InRange(DiagramInspector.Crossings(d), 0, 1);
    }
}
